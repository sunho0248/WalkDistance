using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WalkDistance.Core;

return args.FirstOrDefault() switch
{
    "verify" => Verify(args),
    "benchmark" => Benchmark(args, MediumFixture(), 0.05, 60, 0),
    "benchmark-large" => Benchmark(args, LargeFixture(), 0.10, 180, 0),
    "benchmark-body" => Benchmark(args, MediumFixture(), 0.10, 60, 0.20),
    "benchmark-corpus" => BenchmarkCorpus(args),
    "differential" => Differential(args),
    _ => Usage(),
};

static int Verify(string[] args)
{
    string fixtureDirectory = Option(args, "--fixtures");
    string output = Option(args, "--output");
    var fixtures = Load(Path.Combine(fixtureDirectory, "fixtures.json"));
    try
    {
        VerifyFixtureHash(fixtureDirectory);
        string canonical = Json(BuildVerification(fixtures));
        for (int run = 1; run < 20; run++)
        {
            if (Json(BuildVerification(fixtures)) != canonical)
                throw new InvalidOperationException($"Determinism failed on warm run {run + 1}.");
        }
        Write(output, canonical);
        Console.WriteLine($"verify: PASS; fixtures={fixtures.Count}; warmRuns=20; output={output}");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"verify: FAIL; {exception.Message}");
        return 2;
    }
}

static void VerifyFixtureHash(string fixtureDirectory)
{
    string expected = File.ReadAllText(Path.Combine(fixtureDirectory, "fixtures.sha256"))
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    string actual = Convert.ToHexString(SHA256.HashData(
        File.ReadAllBytes(Path.Combine(fixtureDirectory, "fixtures.json"))));
    if (actual != expected)
        throw new InvalidOperationException("Fixture input changed; version and review the fixture baseline.");
}

static int Benchmark(
    string[] args,
    Fixture fixture,
    double cellSize,
    double timeLimitSeconds,
    double clearanceRadius)
{
    _ = Option(args, "--fixtures"); // Kept in the command contract for fixture discovery by automation.
    string output = Option(args, "--output");
    int iterations = int.Parse(Option(args, "--iterations"));
    if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));

    var initialModel = ContinuousGeometry.Build(fixture.Walls, fixture.Exits, clearanceRadius);
    ContinuousShortestPathMap map = ContinuousShortestPathMap.Build(initialModel);
    ContinuousGeometryDiagnostics diagnostics = initialModel.Diagnostics;

    var profiles = new List<object>();
    bool blocked = false;
    foreach (int count in new[] { 1, 100, 500, 1000 })
    {
        var elapsed = new List<double>();
        var allocated = new List<long>();
        var selectionMilliseconds = new List<double>();
        var selectionAllocated = new List<long>();
        var reconstructMilliseconds = new List<double>();
        var reconstructAllocated = new List<long>();
        var candidates = new List<long>();
        for (int warmup = 0; warmup < 2; warmup++) RunQueries(map, Math.Max(count, 1000), null);
        for (int run = 0; run < iterations; run++)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            long started = Stopwatch.GetTimestamp();
            var metrics = RunQueriesWithStages(map, count, candidates);
            elapsed.Add(ToMilliseconds(Stopwatch.GetTimestamp() - started));
            allocated.Add(GC.GetTotalAllocatedBytes(true) - before);
            selectionMilliseconds.Add(ToMilliseconds(metrics.DistanceSelectionTicks));
            selectionAllocated.Add(metrics.DistanceSelectionAllocatedBytes);
            reconstructMilliseconds.Add(ToMilliseconds(metrics.ReconstructValidateTicks));
            reconstructAllocated.Add(metrics.ReconstructValidateAllocatedBytes);
        }
        double medianMs = Percentile(elapsed, 0.5);
        double candidatesPerQuery = candidates.Average();
        double projectedMs = medianMs / count * 1_000_000;
        long projectedCandidates = checked((long)Math.Ceiling(candidatesPerQuery * 1_000_000));
        bool profileBlocked = projectedCandidates > ContinuousShortestPathMap.DefaultMaxCandidateTests;
        blocked |= profileBlocked;
        profiles.Add(new
        {
            queries = count,
            rawMilliseconds = elapsed,
            allocatedBytes = allocated,
            medianMilliseconds = medianMs,
            p95Milliseconds = Percentile(elapsed, 0.95),
            distanceSelection = Stage(selectionMilliseconds, selectionAllocated),
            reconstructAndValidate = Stage(reconstructMilliseconds, reconstructAllocated),
            candidatesPerQuery,
            candidateP50 = Percentile(candidates.Select(value => (double)value).ToList(), 0.5),
            candidateP95 = Percentile(candidates.Select(value => (double)value).ToList(), 0.95),
            projectedOneMillionMilliseconds = projectedMs,
            projectedOneMillionCandidateTests = projectedCandidates,
            blocked = profileBlocked,
        });
    }

    object? fullMap = null;
    bool spikeBlocked = blocked;
    int samplingWorkers = Environment.ProcessorCount;
    var continuousGrid = new StageAccumulator();
    var continuousGeometry = new StageAccumulator();
    var visibilityGraph = new StageAccumulator();
    var continuousDijkstra = new StageAccumulator();
    var continuousSampling = new StageAccumulator();
    var continuousContours = new StageAccumulator();
    var continuousEndToEnd = new StageAccumulator();
    var thetaGrid = new StageAccumulator();
    var thetaSources = new StageAccumulator();
    var thetaDistanceField = new StageAccumulator();
    var thetaContours = new StageAccumulator();
    var thetaEndToEnd = new StageAccumulator();
    var continuousRetained = new List<long>();
    var thetaRetained = new List<long>();
    if (!spikeBlocked)
    {
        ContinuousSampleResult? sample = null;
        for (int run = -2; run < iterations; run++)
        {
            ForceFullGc();
            long totalBefore = GC.GetTotalAllocatedBytes(true);
            long totalStarted = Stopwatch.GetTimestamp();
            var grid = Measure(() => WalkabilityGrid.Build(
                fixture.Walls, cellSize, marginCells: 0, clearanceRadius: clearanceRadius),
                out var gridMetric);
            var model = Measure(() => ContinuousGeometry.Build(
                fixture.Walls, fixture.Exits, clearanceRadius), out var geometryMetric);
            map = ContinuousShortestPathMap.BuildWithMetrics(model, out var buildMetrics);
            sample = Measure(() => map.Sample(grid, workerCount: samplingWorkers), out var sampleMetric);
            _ = Measure(() => DistanceContourGenerator.Generate(grid, sample.Distances), out var contourMetric);
            var totalMetric = new StageMetric(
                Stopwatch.GetTimestamp() - totalStarted,
                GC.GetTotalAllocatedBytes(true) - totalBefore);
            if (run >= 0)
            {
                continuousGrid.Add(gridMetric);
                continuousGeometry.Add(geometryMetric);
                visibilityGraph.Add(new(buildMetrics.VisibilityGraphTicks, buildMetrics.VisibilityGraphAllocatedBytes));
                continuousDijkstra.Add(new(buildMetrics.MultiSourceDijkstraTicks, buildMetrics.MultiSourceDijkstraAllocatedBytes));
                continuousSampling.Add(sampleMetric);
                continuousContours.Add(contourMetric);
                continuousEndToEnd.Add(totalMetric);
                continuousRetained.Add(RetainedHeapBytes());
            }

            ForceFullGc();
            totalBefore = GC.GetTotalAllocatedBytes(true);
            totalStarted = Stopwatch.GetTimestamp();
            var thetaGridValue = Measure(() => WalkabilityGrid.Build(
                fixture.Walls, cellSize, marginCells: 0, clearanceRadius: clearanceRadius),
                out var thetaGridMetric);
            var sources = Measure(() => fixture.Exits.SelectMany((path, exitIndex) =>
                thetaGridValue.WalkableSourcesNearSegment(path, thetaGridValue.CellSize, exitIndex)).ToList(),
                out var sourceMetric);
            var theta = Measure(() => DistanceMapCalculator.Compute(thetaGridValue, sources),
                out var thetaMetric);
            _ = Measure(() => DistanceContourGenerator.Generate(thetaGridValue, theta.Distances),
                out var thetaContourMetric);
            var thetaTotalMetric = new StageMetric(
                Stopwatch.GetTimestamp() - totalStarted,
                GC.GetTotalAllocatedBytes(true) - totalBefore);
            if (run >= 0)
            {
                thetaGrid.Add(thetaGridMetric);
                thetaSources.Add(sourceMetric);
                thetaDistanceField.Add(thetaMetric);
                thetaContours.Add(thetaContourMetric);
                thetaEndToEnd.Add(thetaTotalMetric);
                thetaRetained.Add(RetainedHeapBytes());
            }
        }
        bool passed = Percentile(continuousSampling.Milliseconds, 0.95) <= timeLimitSeconds * 1000;
        blocked |= !passed;
        var measuredGrid = WalkabilityGrid.Build(
            fixture.Walls, cellSize, marginCells: 0, clearanceRadius: clearanceRadius);
        fullMap = new
        {
            cellSizeMetres = cellSize,
            samples = measuredGrid.InteriorCellCount,
            rows = measuredGrid.Rows,
            columns = measuredGrid.Cols,
            rawMilliseconds = continuousSampling.Milliseconds,
            allocatedBytes = continuousSampling.AllocatedBytes,
            medianMilliseconds = Percentile(continuousSampling.Milliseconds, 0.5),
            p95Milliseconds = Percentile(continuousSampling.Milliseconds, 0.95),
            workerCount = samplingWorkers,
            sample!.CandidateTests,
            sample.UnreachableCellCount,
            sample.MaxDistance,
            passed,
        };
    }

    var report = new
    {
        schema = "continuous-performance-milestone-v4",
        scope = $"AC-BENCH-P1d stage/allocation evidence for continuous and Theta* plus AC-BENCH-P1a {fixture.Id} full-map runs",
        environment = new
        {
            os = Environment.OSVersion.ToString(),
            runtime = Environment.Version.ToString(),
            logicalProcessors = Environment.ProcessorCount,
        },
        fixture = fixture.Id,
        clearanceRadiusMetres = clearanceRadius,
        dimensionsMetres = new[] { fixture.Width, fixture.Height },
        inputSegments = fixture.Walls.Count,
        exits = fixture.Exits.Count,
        diagnostics,
        graphVertices = map.Vertices.Count,
        candidateVertexPairs = checked((long)map.Vertices.Count * (map.Vertices.Count - 1) / 2),
        acceptedEdges = map.EdgeCount,
        terminalSeeds = map.TerminalSeedCount,
        stages = new
        {
            continuous = new
            {
                walkabilityGridBuild = continuousGrid.Report(),
                geometryBuild = continuousGeometry.Report(),
                visibilityGraphBuild = visibilityGraph.Report(),
                multiSourceDijkstra = continuousDijkstra.Report(),
                sampling = continuousSampling.Report(),
                contours = continuousContours.Report(),
                endToEnd = continuousEndToEnd.Report(),
                retainedHeapBytes = continuousRetained,
            },
            theta = new
            {
                walkabilityGridBuild = thetaGrid.Report(),
                exitSourceBuild = thetaSources.Report(),
                distanceFieldAndSampling = thetaDistanceField.Report(),
                contours = thetaContours.Report(),
                endToEnd = thetaEndToEnd.Report(),
                retainedHeapBytes = thetaRetained,
            },
        },
        peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        sampleCandidateCap = ContinuousShortestPathMap.DefaultMaxCandidateTests,
        samplingStrategy = "retained multi-source shortest-path labels with deterministic row-parallel sampling",
        profiles,
        spikeGatePassed = !spikeBlocked,
        fullMap,
        promotionReady = false,
        remainingGate = blocked ? null : iterations >= 7
            ? "REAL_CORPUS_UI_AND_RELEASE_GATES"
            : "SEVEN_RUN_FULL_MAP_THETA_AND_RELEASE_GATES",
        gate = spikeBlocked ? "O1_STRATEGY_REVISION_REQUIRED" :
            blocked ? $"P1A_{fixture.Id.Replace("-", "")}_FULL_MAP_LIMIT" :
                $"P1A_{fixture.Id.Replace("-", "")}_{(iterations >= 7 ? "SEVEN_RUN" : "SINGLE_RUN")}_PASS",
    };
    Write(output, Json(report));
    Console.WriteLine($"benchmark: {(blocked ? "BLOCKED" : "MILESTONE_PASS")}; segments={fixture.Walls.Count}; " +
        $"sampleMedianMs={(continuousSampling.Milliseconds.Count == 0 ? 0 : Percentile(continuousSampling.Milliseconds, 0.5)):F3}; gate={report.gate}; output={output}");
    return blocked ? 2 : 0;
}

static object BuildVerification(IReadOnlyList<Fixture> fixtures)
{
    var results = new List<object>();
    foreach (var fixture in fixtures)
    {
        if (fixture.BuildReason is { } expectedFailure)
        {
            ExpectFailure(fixture, 0, expectedFailure);
            results.Add(new { fixture.Id, buildFailure = expectedFailure });
            continue;
        }

        var model = ContinuousGeometry.Build(fixture.Walls, fixture.Exits);
        var map = ContinuousShortestPathMap.Build(model);
        var queries = VerifyQueries(fixture, map, fixture.Queries, 1e-6);
        string? bodyHash = null;
        if (fixture.BodyRadius is { } radius)
        {
            if (fixture.BodyBuildReason is { } bodyFailure) ExpectFailure(fixture, radius, bodyFailure);
            else
            {
                var body = ContinuousGeometry.Build(fixture.Walls, fixture.Exits, radius);
                bodyHash = body.ModelHash;
                VerifyQueries(fixture, ContinuousShortestPathMap.Build(body), fixture.BodyQueries, 0.001);
            }
        }
        results.Add(new
        {
            fixture.Id,
            modelHash = model.ModelHash,
            bodyHash,
            vertices = map.Vertices.Count,
            edges = map.EdgeCount,
            terminalSeeds = map.TerminalSeedCount,
            queries,
        });
    }
    return new { schema = "continuous-verify-v1", policy = ContinuousGeometry.PolicyVersion, fixtures = results };
}

static int BenchmarkCorpus(string[] args)
{
    string directory = Path.GetFullPath(Option(args, "--fixtures"));
    string output = Option(args, "--output");
    int iterations = int.Parse(Option(args, "--iterations"));
    if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));

    string? directPath = OptionOrNull(args, "--fixture");
    IReadOnlyList<CorpusFixture> fixtures;
    if (directPath is not null)
    {
        string fullPath = Path.GetFullPath(directPath);
        fixtures = [new CorpusFixture(
            Path.GetFileNameWithoutExtension(fullPath), fullPath, null, "B-SAMPLE self-test", "SAMPLE",
            OptionOrNull(args, "--sidecar"))];
    }
    else
    {
        string manifestPath = Path.Combine(directory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<CorpusManifest>(
            File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Corpus manifest is invalid.");
        fixtures = manifest.Fixtures;
    }

    if (fixtures.Count == 0)
    {
        Write(output, Json(new
        {
            schema = "continuous-corpus-benchmark-v1",
            status = "no-corpus",
            message = "No B-REAL fixtures are present; supply anonymized DXFs and manifest entries.",
            fixtures = Array.Empty<object>(),
        }));
        Console.WriteLine($"benchmark-corpus: NO_CORPUS; output={output}");
        return 0;
    }

    var results = new List<object>();
    int succeeded = 0;
    foreach (var source in fixtures)
    {
        try
        {
            string dxfPath = Path.IsPathRooted(source.File)
                ? source.File : Path.Combine(directory, source.File);
            string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dxfPath)));
            if (source.Sha256 is { Length: > 0 } expectedHash &&
                !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SHA-256 mismatch; the corpus drawing drifted from its manifest.");
            }

            var document = DxfLoader.Load(dxfPath);
            string? sidecarPath = source.Sidecar ?? OptionOrNull(args, "--sidecar");
            CorpusSidecar? sidecar = sidecarPath is null ? null : LoadCorpusSidecar(
                Path.IsPathRooted(sidecarPath) ? sidecarPath : directPath is null
                    ? Path.Combine(directory, sidecarPath)
                    : Path.GetFullPath(sidecarPath));
            var exits = sidecar?.Exits is { Length: > 0 } overrides
                ? ConvertExits(overrides)
                : document.ExitPaths;
            if (exits.Count == 0)
                throw new InvalidDataException("No exit definition: add WD_Exit geometry or a benchmark sidecar override.");

            var bounds = document.Walls.SelectMany(wall => new[] { wall.Start, wall.End }).ToArray();
            var fixture = new Fixture(
                sidecar?.Id ?? source.Id,
                document.Walls,
                exits,
                [],
                sidecar?.BodyRadius,
                [],
                null,
                null,
                (int)Math.Ceiling(bounds.Max(point => point.X) - bounds.Min(point => point.X)),
                (int)Math.Ceiling(bounds.Max(point => point.Y) - bounds.Min(point => point.Y)));
            results.Add(MeasureCorpusFixture(
                fixture,
                sidecar?.CellSize ?? 0.25,
                sidecar?.BodyRadius ?? 0,
                iterations,
                actualHash,
                source.SizeClass,
                source.Provenance));
            succeeded++;
        }
        catch (Exception exception)
        {
            results.Add(new { fixture = source.Id, status = "error", error = exception.Message });
        }
    }

    string status = succeeded == fixtures.Count ? "ok" : succeeded == 0 ? "failed" : "partial";
    Write(output, Json(new
    {
        schema = "continuous-corpus-benchmark-v1",
        status,
        environment = new
        {
            os = Environment.OSVersion.ToString(),
            runtime = Environment.Version.ToString(),
            logicalProcessors = Environment.ProcessorCount,
        },
        warmups = 2,
        measuredRuns = iterations,
        fixtures = results,
    }));
    Console.WriteLine($"benchmark-corpus: {status.ToUpperInvariant()}; succeeded={succeeded}; total={fixtures.Count}; output={output}");
    return succeeded > 0 ? 0 : 2;
}

static object MeasureCorpusFixture(
    Fixture fixture,
    double cellSize,
    double clearanceRadius,
    int iterations,
    string dxfSha256,
    string sizeClass,
    string provenance)
{
    var continuousGrid = new StageAccumulator();
    var continuousGeometry = new StageAccumulator();
    var visibilityGraph = new StageAccumulator();
    var continuousDijkstra = new StageAccumulator();
    var continuousSampling = new StageAccumulator();
    var continuousContours = new StageAccumulator();
    var continuousEndToEnd = new StageAccumulator();
    var thetaGrid = new StageAccumulator();
    var thetaSources = new StageAccumulator();
    var thetaDistanceField = new StageAccumulator();
    var thetaContours = new StageAccumulator();
    var thetaEndToEnd = new StageAccumulator();
    var continuousRetained = new List<long>();
    var thetaRetained = new List<long>();
    ContinuousSampleResult? sample = null;
    DistanceMapResult? theta = null;
    ContinuousShortestPathMap? map = null;

    for (int run = -2; run < iterations; run++)
    {
        ForceFullGc();
        long totalBefore = GC.GetTotalAllocatedBytes(true);
        long totalStarted = Stopwatch.GetTimestamp();
        var grid = Measure(() => WalkabilityGrid.Build(
            fixture.Walls, cellSize, marginCells: 0, clearanceRadius: clearanceRadius),
            out var gridMetric);
        var model = Measure(() => ContinuousGeometry.Build(
            fixture.Walls, fixture.Exits, clearanceRadius), out var geometryMetric);
        map = ContinuousShortestPathMap.BuildWithMetrics(model, out var buildMetrics);
        sample = Measure(() => map.Sample(grid), out var sampleMetric);
        _ = Measure(() => DistanceContourGenerator.Generate(grid, sample.Distances), out var contourMetric);
        var totalMetric = new StageMetric(
            Stopwatch.GetTimestamp() - totalStarted,
            GC.GetTotalAllocatedBytes(true) - totalBefore);
        if (run >= 0)
        {
            continuousGrid.Add(gridMetric);
            continuousGeometry.Add(geometryMetric);
            visibilityGraph.Add(new(buildMetrics.VisibilityGraphTicks, buildMetrics.VisibilityGraphAllocatedBytes));
            continuousDijkstra.Add(new(buildMetrics.MultiSourceDijkstraTicks, buildMetrics.MultiSourceDijkstraAllocatedBytes));
            continuousSampling.Add(sampleMetric);
            continuousContours.Add(contourMetric);
            continuousEndToEnd.Add(totalMetric);
            continuousRetained.Add(RetainedHeapBytes());
        }

        ForceFullGc();
        totalBefore = GC.GetTotalAllocatedBytes(true);
        totalStarted = Stopwatch.GetTimestamp();
        var thetaGridValue = Measure(() => WalkabilityGrid.Build(
            fixture.Walls, cellSize, marginCells: 0, clearanceRadius: clearanceRadius),
            out var thetaGridMetric);
        var sources = Measure(() => fixture.Exits.SelectMany((path, exitIndex) =>
            thetaGridValue.WalkableSourcesNearSegment(path, thetaGridValue.CellSize, exitIndex)).ToList(),
            out var sourceMetric);
        theta = Measure(() => DistanceMapCalculator.Compute(thetaGridValue, sources), out var thetaMetric);
        _ = Measure(() => DistanceContourGenerator.Generate(thetaGridValue, theta.Distances),
            out var thetaContourMetric);
        var thetaTotalMetric = new StageMetric(
            Stopwatch.GetTimestamp() - totalStarted,
            GC.GetTotalAllocatedBytes(true) - totalBefore);
        if (run >= 0)
        {
            thetaGrid.Add(thetaGridMetric);
            thetaSources.Add(sourceMetric);
            thetaDistanceField.Add(thetaMetric);
            thetaContours.Add(thetaContourMetric);
            thetaEndToEnd.Add(thetaTotalMetric);
            thetaRetained.Add(RetainedHeapBytes());
        }
    }

    return new
    {
        fixture = fixture.Id,
        status = "measured",
        classification = sizeClass,
        provenance,
        dxfSha256,
        cellSizeMetres = cellSize,
        clearanceRadiusMetres = clearanceRadius,
        inputSegments = fixture.Walls.Count,
        exits = fixture.Exits.Count,
        graphVertices = map!.Vertices.Count,
        continuousMapSha256 = MapHash(sample!.Distances),
        thetaMapSha256 = MapHash(theta!.Distances),
        stages = new
        {
            continuous = new
            {
                walkabilityGridBuild = continuousGrid.Report(),
                geometryBuild = continuousGeometry.Report(),
                visibilityGraphBuild = visibilityGraph.Report(),
                multiSourceDijkstra = continuousDijkstra.Report(),
                sampling = continuousSampling.Report(),
                contours = continuousContours.Report(),
                endToEnd = continuousEndToEnd.Report(),
                retainedHeapBytes = continuousRetained,
            },
            theta = new
            {
                walkabilityGridBuild = thetaGrid.Report(),
                exitSourceBuild = thetaSources.Report(),
                distanceFieldAndSampling = thetaDistanceField.Report(),
                contours = thetaContours.Report(),
                endToEnd = thetaEndToEnd.Report(),
                retainedHeapBytes = thetaRetained,
            },
        },
    };
}

static CorpusSidecar LoadCorpusSidecar(string path) =>
    JsonSerializer.Deserialize<CorpusSidecar>(
        File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidDataException("Corpus sidecar is invalid.");

static IReadOnlyList<IReadOnlyList<WorldPoint>> ConvertExits(double[][][] exits) =>
    exits.Select(path => (IReadOnlyList<WorldPoint>)path.Select(point =>
        new WorldPoint(point[0], point[1])).ToArray()).ToArray();

static int Differential(string[] args)
{
    _ = Option(args, "--fixtures");
    string output = Option(args, "--output");
    var fixture = MediumFixture();
    double cellSize = double.Parse(OptionOrDefault(args, "--cell-size", "0.05"), CultureInfo.InvariantCulture);
    var grid = WalkabilityGrid.Build(fixture.Walls, cellSize, marginCells: 0);

    var continuousWatch = Stopwatch.StartNew();
    var continuousMap = ContinuousShortestPathMap.Build(
        ContinuousGeometry.Build(fixture.Walls, fixture.Exits));
    var continuous = continuousMap.Sample(grid);
    continuousWatch.Stop();

    var sources = fixture.Exits.SelectMany((path, exitIndex) =>
        grid.WalkableSourcesNearSegment(path, grid.CellSize, exitIndex)).ToList();
    var thetaWatch = Stopwatch.StartNew();
    var theta = DistanceMapCalculator.Compute(grid, sources);
    thetaWatch.Stop();

    int eligible = 0;
    int reachabilityMatches = 0;
    var deltas = new List<double>();
    var signedDeltas = new List<double>();
    var largestDeltas = new List<(double Delta, int Col, int Row, double Continuous, double Theta)>();
    for (int row = 0; row < grid.Rows; row++)
    for (int col = 0; col < grid.Cols; col++)
    {
        if (!grid.IsWalkable(col, row)) continue;
        eligible++;
        bool continuousReachable = double.IsFinite(continuous.Distances[row, col]);
        bool thetaReachable = double.IsFinite(theta.Distances[row, col]);
        if (continuousReachable == thetaReachable) reachabilityMatches++;
        if (continuousReachable && thetaReachable)
        {
            double delta = Math.Abs(continuous.Distances[row, col] - theta.Distances[row, col]);
            deltas.Add(delta);
            signedDeltas.Add(theta.Distances[row, col] - continuous.Distances[row, col]);
            largestDeltas.Add((delta, col, row, continuous.Distances[row, col], theta.Distances[row, col]));
        }
    }

    double agreement = eligible == 0 ? 0 : (double)reachabilityMatches / eligible;
    double p95 = deltas.Count == 0 ? double.PositiveInfinity : Percentile(deltas, 0.95);
    double maximum = deltas.Count == 0 ? double.PositiveInfinity : deltas.Max();
    double p95Limit = 2 * cellSize * Math.Sqrt(2);
    double maximumLimit = 4 * cellSize * Math.Sqrt(2);
    bool reachabilityPassed = agreement >= 0.999;
    bool legacyDistanceEnvelopePassed = p95 <= p95Limit && maximum <= maximumLimit;
    var report = new
    {
        schema = "continuous-theta-differential-v2",
        fixture = fixture.Id,
        fixtureHash = continuousMap.ModelHash,
        continuousMapSha256 = MapHash(continuous.Distances),
        thetaMapSha256 = MapHash(theta.Distances),
        cellSizeMetres = cellSize,
        grid.Rows,
        grid.Cols,
        eligibleSamples = eligible,
        reachabilityMatches,
        reachabilityAgreement = agreement,
        comparableDistances = deltas.Count,
        absoluteDeltaP95Metres = p95,
        absoluteDeltaMaximumMetres = maximum,
        thetaMinusContinuousP50Metres = signedDeltas.Count == 0 ? double.PositiveInfinity : Percentile(signedDeltas, 0.5),
        thetaMinusContinuousP95Metres = signedDeltas.Count == 0 ? double.PositiveInfinity : Percentile(signedDeltas, 0.95),
        largestDeltas = largestDeltas.OrderByDescending(item => item.Delta).Take(10).Select(item => new
        {
            point = grid.CellCenter(item.Col, item.Row),
            item.Continuous,
            item.Theta,
            item.Delta,
        }),
        legacyDistanceEnvelope = new
        {
            p95Metres = p95Limit,
            maximumMetres = maximumLimit,
            passed = legacyDistanceEnvelopePassed,
            status = "INFORMATIONAL_RASTER_BIAS",
        },
        semanticDecision = new
        {
            reachabilityAgreementMinimum = 0.999,
            continuousDistanceIsAuthoritative = true,
            thetaFallbackMustRemainWholeResult = true,
            hybridOrInflatedContinuousGeometryAllowed = false,
        },
        continuousMilliseconds = continuousWatch.Elapsed.TotalMilliseconds,
        thetaMilliseconds = thetaWatch.Elapsed.TotalMilliseconds,
        continuous.MaxDistance,
        thetaMaxDistance = theta.MaxDistance,
        passed = reachabilityPassed,
    };
    Write(output, Json(report));
    Console.WriteLine($"differential: {(reachabilityPassed ? "PASS" : "FAIL")}; agreement={agreement:P4}; " +
        $"p95={p95:F6}; max={maximum:F6}; legacyEnvelope={(legacyDistanceEnvelopePassed ? "PASS" : "EXPECTED_FAIL")}; output={output}");
    return reachabilityPassed ? 0 : 2;
}

static IReadOnlyList<object> VerifyQueries(
    Fixture fixture,
    ContinuousShortestPathMap map,
    IReadOnlyList<QueryExpectation> expectedQueries,
    double tolerance)
{
    var results = new List<object>();
    foreach (var expected in expectedQueries)
    {
        var result = map.QueryWithMetrics(expected.Point);
        if (expected.Distance is null)
        {
            if (result.Path is not null) throw new InvalidOperationException($"{fixture.Id}: expected no route.");
            results.Add(new { point = expected.Point, reachable = false, result.CandidateTests });
            continue;
        }
        var path = result.Path ?? throw new InvalidOperationException($"{fixture.Id}: expected a route.");
        if (Math.Abs(path.Distance - expected.Distance.Value) > tolerance || path.ExitIndex != expected.Exit ||
            !map.Validate(path.Points))
            throw new InvalidOperationException($"{fixture.Id}: analytic or legality expectation failed.");
        if (expected.Contact is { } contact && Distance(contact, path.Contact) > tolerance)
            throw new InvalidOperationException($"{fixture.Id}: contact expectation failed.");
        if (expected.Arrival is { } arrival && Distance(arrival, path.Arrival) > tolerance)
            throw new InvalidOperationException($"{fixture.Id}: arrival expectation failed.");
        results.Add(new
        {
            point = expected.Point,
            reachable = true,
            path.Distance,
            path.ExitIndex,
            path.Contact,
            path.Arrival,
            path.Points,
            result.CandidateTests,
        });
    }
    return results;
}

static void ExpectFailure(Fixture fixture, double radius, string reason)
{
    try
    {
        _ = ContinuousGeometry.Build(fixture.Walls, fixture.Exits, radius);
    }
    catch (ContinuousBuildException exception) when (exception.ReasonCode == reason)
    {
        return;
    }
    throw new InvalidOperationException($"{fixture.Id}: expected build failure {reason}.");
}

static void RunQueries(ContinuousShortestPathMap map, int count, List<long>? candidates)
{
    for (int index = 0; index < count; index++)
    {
        double x = 0.25 + (index * 7919 % 5950) / 100.0;
        double y = 0.25 + (index * 3571 % 3950) / 100.0;
        var result = map.QueryWithMetrics(new WorldPoint(x, y));
        candidates?.Add(result.CandidateTests);
    }
}

static QueryBatchMetrics RunQueriesWithStages(
    ContinuousShortestPathMap map,
    int count,
    List<long> candidates)
{
    long selectionTicks = 0;
    long selectionAllocated = 0;
    long reconstructTicks = 0;
    long reconstructAllocated = 0;
    for (int index = 0; index < count; index++)
    {
        double x = 0.25 + (index * 7919 % 5950) / 100.0;
        double y = 0.25 + (index * 3571 % 3950) / 100.0;
        var query = map.QueryWithStageMetrics(new WorldPoint(x, y));
        candidates.Add(query.Result.CandidateTests);
        selectionTicks = checked(selectionTicks + query.Metrics.DistanceSelectionTicks);
        selectionAllocated = checked(selectionAllocated + query.Metrics.DistanceSelectionAllocatedBytes);
        reconstructTicks = checked(reconstructTicks + query.Metrics.ReconstructValidateTicks);
        reconstructAllocated = checked(reconstructAllocated + query.Metrics.ReconstructValidateAllocatedBytes);
    }
    return new QueryBatchMetrics(
        selectionTicks, selectionAllocated, reconstructTicks, reconstructAllocated);
}

static Fixture MediumFixture()
{
    var walls = new List<Segment>();
    AddSubdivided(walls, new(0, 0), new(60, 0), 120);
    AddSubdivided(walls, new(60, 0), new(60, 40), 80);
    AddSubdivided(walls, new(60, 40), new(0, 40), 120);
    AddSubdivided(walls, new(0, 40), new(0, 0), 80);
    walls.AddRange([
        new(new(12, 0), new(12, 32)),
        new(new(24, 8), new(24, 40)),
        new(new(36, 0), new(36, 32)),
        new(new(48, 8), new(48, 40)),
    ]);
    var exits = new IReadOnlyList<WorldPoint>[]
    {
        new WorldPoint[] { new(0, 8), new(0, 12) },
        new WorldPoint[] { new(0, 28), new(0, 32) },
        new WorldPoint[] { new(60, 8), new(60, 12) },
        new WorldPoint[] { new(60, 28), new(60, 32) },
    };
    return new Fixture("B-MEDIUM", walls, exits, [], null, [], null, null, 60, 40);
}

static Fixture LargeFixture()
{
    var walls = new List<Segment>();
    AddSubdivided(walls, new(0, 0), new(120, 0), 480);
    AddSubdivided(walls, new(120, 0), new(120, 80), 320);
    AddSubdivided(walls, new(120, 80), new(0, 80), 480);
    AddSubdivided(walls, new(0, 80), new(0, 0), 320);
    for (int index = 1; index <= 7; index++)
    {
        double x = index * 15;
        walls.Add(index % 2 == 0
            ? new Segment(new(x, 12), new(x, 80))
            : new Segment(new(x, 0), new(x, 68)));
    }
    var exits = new IReadOnlyList<WorldPoint>[]
    {
        new WorldPoint[] { new(0, 8), new(0, 12) },
        new WorldPoint[] { new(0, 28), new(0, 32) },
        new WorldPoint[] { new(0, 48), new(0, 52) },
        new WorldPoint[] { new(0, 68), new(0, 72) },
        new WorldPoint[] { new(120, 8), new(120, 12) },
        new WorldPoint[] { new(120, 28), new(120, 32) },
        new WorldPoint[] { new(120, 48), new(120, 52) },
        new WorldPoint[] { new(120, 68), new(120, 72) },
    };
    return new Fixture("B-LARGE", walls, exits, [], null, [], null, null, 120, 80);
}

static void AddSubdivided(List<Segment> walls, WorldPoint start, WorldPoint end, int count)
{
    for (int index = 0; index < count; index++)
    {
        WorldPoint Point(double ratio) => new(
            start.X + (end.X - start.X) * ratio,
            start.Y + (end.Y - start.Y) * ratio);
        walls.Add(new Segment(Point((double)index / count), Point((double)(index + 1) / count)));
    }
}

static IReadOnlyList<Fixture> Load(string path)
{
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var source = JsonSerializer.Deserialize<List<FixtureDto>>(File.ReadAllText(path), options)!;
    return source.Select(fixture => new Fixture(
        fixture.Id,
        fixture.Walls.Select(values => new Segment(
            new(values[0], values[1]), new(values[2], values[3]))).ToArray(),
        fixture.Exits.Select(exit => (IReadOnlyList<WorldPoint>)exit.Select(values =>
            new WorldPoint(values[0], values[1])).ToArray()).ToArray(),
        Convert(fixture.Queries), fixture.BodyRadius, Convert(fixture.BodyQueries),
        fixture.BuildReason, fixture.BodyBuildReason)).ToArray();

    static IReadOnlyList<QueryExpectation> Convert(IEnumerable<QueryDto>? source) =>
        source?.Select(query => new QueryExpectation(
            new(query.Point[0], query.Point[1]), query.Distance, query.Exit,
            query.Contact is { } contact ? new(contact[0], contact[1]) : null,
            query.Arrival is { } arrival ? new(arrival[0], arrival[1]) : null)).ToArray() ?? [];
}

static string Option(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    if (index < 0 || index + 1 == args.Length) throw new ArgumentException($"Missing {name}.");
    return args[index + 1];
}

static string OptionOrDefault(string[] args, string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index < 0 ? fallback : index + 1 < args.Length ? args[index + 1] :
        throw new ArgumentException($"Missing {name}.");
}

static string? OptionOrNull(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index < 0 ? null : index + 1 < args.Length ? args[index + 1] :
        throw new ArgumentException($"Missing {name}.");
}

static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
});

static void Write(string path, string contents)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, contents + Environment.NewLine);
}

static double Percentile(List<double> values, double percentile)
{
    var ordered = values.Order().ToArray();
    return ordered[(int)Math.Floor((ordered.Length - 1) * percentile)];
}

static object Stage(List<double> milliseconds, List<long> allocatedBytes) => new
{
    rawMilliseconds = milliseconds,
    medianMilliseconds = Percentile(milliseconds, 0.5),
    p95Milliseconds = Percentile(milliseconds, 0.95),
    allocatedBytes,
};

static T Measure<T>(Func<T> action, out StageMetric metric)
{
    long allocatedBefore = GC.GetTotalAllocatedBytes(true);
    long started = Stopwatch.GetTimestamp();
    T result = action();
    metric = new StageMetric(
        Stopwatch.GetTimestamp() - started,
        GC.GetTotalAllocatedBytes(true) - allocatedBefore);
    return result;
}

static double ToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

static void ForceFullGc()
{
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
}

static long RetainedHeapBytes()
{
    ForceFullGc();
    return GC.GetTotalMemory(forceFullCollection: false);
}

static double Distance(WorldPoint left, WorldPoint right) =>
    Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

static string MapHash(double[,] distances)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    Span<byte> bytes = stackalloc byte[sizeof(long)];
    foreach (double distance in distances)
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(distance));
        hash.AppendData(bytes);
    }
    return Convert.ToHexString(hash.GetHashAndReset());
}

static int Usage()
{
    Console.Error.WriteLine("Usage: verify|benchmark|benchmark-large|benchmark-body|benchmark-corpus|differential --fixtures <directory> --output <file> [--iterations <count>] [--fixture <dxf> --sidecar <json>]");
    return 1;
}

sealed record FixtureDto(
    string Id, double[][] Walls, double[][][] Exits, QueryDto[]? Queries,
    double? BodyRadius, QueryDto[]? BodyQueries, string? BuildReason, string? BodyBuildReason);
sealed record QueryDto(double[] Point, double? Distance, int? Exit, double[]? Contact, double[]? Arrival);
sealed record Fixture(
    string Id, IReadOnlyList<Segment> Walls, IReadOnlyList<IReadOnlyList<WorldPoint>> Exits,
    IReadOnlyList<QueryExpectation> Queries, double? BodyRadius,
    IReadOnlyList<QueryExpectation> BodyQueries, string? BuildReason, string? BodyBuildReason,
    int Width = 0, int Height = 0);
sealed record QueryExpectation(WorldPoint Point, double? Distance, int? Exit, WorldPoint? Contact, WorldPoint? Arrival);
sealed record CorpusManifest(CorpusFixture[] Fixtures);
sealed record CorpusFixture(
    string Id,
    string File,
    string? Sha256,
    string Provenance,
    string SizeClass,
    string? Sidecar);
sealed record CorpusSidecar(
    string? Id,
    double[][][]? Exits,
    double? BodyRadius,
    double? CellSize);
sealed record QueryBatchMetrics(
    long DistanceSelectionTicks,
    long DistanceSelectionAllocatedBytes,
    long ReconstructValidateTicks,
    long ReconstructValidateAllocatedBytes);
readonly record struct StageMetric(long Ticks, long AllocatedBytes);

sealed class StageAccumulator
{
    public List<double> Milliseconds { get; } = [];
    public List<long> AllocatedBytes { get; } = [];

    public void Add(StageMetric metric)
    {
        Milliseconds.Add(metric.Ticks * 1000d / Stopwatch.Frequency);
        AllocatedBytes.Add(metric.AllocatedBytes);
    }

    public object Report() => Milliseconds.Count == 0
        ? new { status = "not-run", rawMilliseconds = Array.Empty<double>(), allocatedBytes = Array.Empty<long>() }
        : new
        {
            status = "measured",
            rawMilliseconds = Milliseconds,
            medianMilliseconds = ValueAt(0.5),
            p95Milliseconds = ValueAt(0.95),
            allocatedBytes = AllocatedBytes,
        };

    private double ValueAt(double percentile)
    {
        var ordered = Milliseconds.Order().ToArray();
        return ordered[(int)Math.Floor((ordered.Length - 1) * percentile)];
    }
}
