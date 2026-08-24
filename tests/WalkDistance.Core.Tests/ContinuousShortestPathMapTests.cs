using System.Text.Json;
using System.Security.Cryptography;
using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public sealed class ContinuousShortestPathMapTests
{
    private static readonly IReadOnlyList<Fixture> Fixtures = LoadFixtures();

    [Fact]
    public void AnalyticFixtureCatalogue_PassesGeometryRouteAndBodyExpectations()
    {
        foreach (var fixture in Fixtures)
        {
            if (fixture.BuildReason is { } buildReason)
            {
                var error = Assert.Throws<ContinuousBuildException>(() => Build(fixture, 0));
                Assert.Equal(buildReason, error.ReasonCode);
                continue;
            }

            var geometric = Build(fixture, 0);
            AssertQueries(fixture.Id, geometric, fixture.Queries, 1e-6);

            if (fixture.BodyRadius is not { } radius)
            {
                continue;
            }

            if (fixture.BodyBuildReason is { } bodyBuildReason)
            {
                var error = Assert.Throws<ContinuousBuildException>(() => Build(fixture, radius));
                Assert.Equal(bodyBuildReason, error.ReasonCode);
            }
            else
            {
                AssertQueries(fixture.Id, Build(fixture, radius), fixture.BodyQueries, 0.001);
            }
        }
    }

    [Fact]
    public void Detour_UsesFiniteBlockerEndpointAndNeverCrossesPartition()
    {
        var fixture = Find("T-DETOUR");
        var map = Build(fixture, 0);
        var path = map.Query(new WorldPoint(8, 5))!;

        Assert.Contains(new WorldPoint(5, 8), map.Vertices.Select(vertex => vertex.Point));
        Assert.Contains(new WorldPoint(5, 8), path.Points);
        Assert.True(map.Validate(path.Points));
        Assert.False(map.Validate([new WorldPoint(8, 5), new WorldPoint(2, 5), new WorldPoint(0, 5)]));
        Assert.False(map.Validate([new WorldPoint(8, 5), new WorldPoint(12, 5), new WorldPoint(0, 5)]));
    }

    [Fact]
    public void BodyDetour_PreservesBufferedClearanceWhenVisibilityChecksAreReduced()
    {
        var fixture = Find("T-DETOUR");
        var map = Build(fixture, 0.2);
        var path = map.Query(new WorldPoint(8, 5))!;

        Assert.True(path.Distance > 9.6);
        Assert.True(map.Validate(path.Points));
        Assert.False(map.Validate([new WorldPoint(8, 5), new WorldPoint(2, 5), new WorldPoint(0.2, 5)]));
    }

    [Fact]
    public void ValidatorRejectsAChordAcrossAConcaveExteriorOpening()
    {
        WorldPoint[] boundary =
        [
            new(0, 0), new(10, 0), new(10, 10), new(7, 10), new(7, 3),
            new(3, 3), new(3, 10), new(0, 10), new(0, 0),
        ];
        var walls = boundary.Zip(boundary.Skip(1), (start, end) => new Segment(start, end)).ToArray();
        var exits = new IReadOnlyList<WorldPoint>[]
        {
            new WorldPoint[] { new(0, 4), new(0, 6) },
        };
        var map = ContinuousShortestPathMap.Build(ContinuousGeometry.Build(walls, exits));

        Assert.False(map.Validate([new WorldPoint(3, 10), new WorldPoint(7, 10)]));
    }

    [Fact]
    public void InputOrderAndDirection_DoNotChangeCanonicalModelOrQuery()
    {
        var fixture = Find("T-PRECISION");
        var forward = Build(fixture, 0);
        var forwardPath = forward.Query(new WorldPoint(8, 5))!;
        for (int run = 0; run < 20; run++)
        {
            var permuted = fixture.Walls.Skip(run % fixture.Walls.Count)
                .Concat(fixture.Walls.Take(run % fixture.Walls.Count))
                .Select((segment, index) => (run + index) % 2 == 0
                    ? segment : new Segment(segment.End, segment.Start))
                .Reverse().ToArray();
            var current = ContinuousShortestPathMap.Build(ContinuousGeometry.Build(permuted, fixture.Exits));
            var currentPath = current.Query(new WorldPoint(8, 5))!;
            Assert.Equal(forward.ModelHash, current.ModelHash);
            Assert.Equal(forwardPath.Distance, currentPath.Distance);
            Assert.Equal(forwardPath.ExitIndex, currentPath.ExitIndex);
            Assert.Equal(forwardPath.Points, currentPath.Points);
        }
    }

    [Fact]
    public void GraphIncludesEveryInputEndpointAndUsesInteriorTargetProjections()
    {
        var fixture = Find("T-DETOUR");
        var map = Build(fixture, 0);
        var points = map.Vertices.Select(vertex => vertex.Point).ToHashSet();
        Assert.All(fixture.Walls.SelectMany(wall => new[] { wall.Start, wall.End }).Distinct(),
            point => Assert.Contains(point, points));

        var open = Build(Find("T-OPEN"), 0);
        var path = open.Query(new WorldPoint(8, 5))!;
        Assert.Equal(new WorldPoint(0, 5), path.Contact);
        Assert.True(open.TerminalSeedCount >= 3);
    }

    [Fact]
    public void QueryTestsNearestLowerBoundFirstAndSkipsDominatedVisibilityWork()
    {
        var map = Build(Find("T-OPEN"), 0);
        var result = map.QueryWithMetrics(new WorldPoint(8, 5));
        Assert.NotNull(result.Path);
        Assert.Equal(1, result.CandidateTests);
    }

    [Fact]
    public void ExitOrderControlsOnlyTheExactTie()
    {
        var fixture = Find("T-MULTI-TIE");
        var reversed = ContinuousShortestPathMap.Build(ContinuousGeometry.Build(
            fixture.Walls, fixture.Exits.Reverse().ToArray()));
        var path = reversed.Query(new WorldPoint(5, 5))!;
        Assert.Equal(5, path.Distance);
        Assert.Equal(0, path.ExitIndex);
        Assert.Equal(new WorldPoint(10, 5), path.Contact);
    }

    [Fact]
    public void FixtureCatalogueHashIsFrozen()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ContinuousMigration");
        string expected = File.ReadAllText(Path.Combine(directory, "fixtures.sha256"))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "fixtures.json"))));
        Assert.True(expected == actual, "Fixture input changed; version and review the fixture instead of rewriting its baseline.");
    }

    [Fact]
    public void Sampling_IsDeterministicAndRejectsWorkAboveTheCandidateCap()
    {
        var fixture = Find("T-OPEN");
        var map = Build(fixture, 0);
        var grid = WalkabilityGrid.Build(fixture.Walls, 0.5, marginCells: 0);
        var first = map.Sample(grid, maxCandidateTests: 1_000_000, workerCount: 1);
        var second = map.Sample(grid, maxCandidateTests: 1_000_000, workerCount: Environment.ProcessorCount);

        Assert.Equal(first.Distances.Cast<double>(), second.Distances.Cast<double>());
        Assert.Equal(first.FarthestCell, second.FarthestCell);
        Assert.Equal(first.MaxDistance, second.MaxDistance);
        Assert.Equal(grid.Rows, first.Distances.GetLength(0));
        Assert.Equal(grid.Cols, first.Distances.GetLength(1));
        Assert.DoesNotContain(first.Distances.Cast<double>(), double.IsNaN);
        Assert.Throws<ContinuousBuildException>(() => map.Sample(grid, maxCandidateTests: 1));
    }

    [Fact]
    public void Sampling_ObservesCancellationWithoutPublishingAPartialMap()
    {
        var fixture = Find("T-OPEN");
        var map = Build(fixture, fixture.BodyRadius!.Value);
        var grid = WalkabilityGrid.Build(
            fixture.Walls, 0.1, marginCells: 0, clearanceRadius: fixture.BodyRadius.Value);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => map.Sample(
            grid, maxCandidateTests: 10_000_000, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Build_ObservesCancellationBeforePublishingAShortestPathMap()
    {
        var fixture = Find("T-OPEN");
        var model = ContinuousGeometry.Build(fixture.Walls, fixture.Exits, fixture.BodyRadius!.Value);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ContinuousShortestPathMap.Build(model, cancellation.Token));
    }

    [Fact]
    public void GeometryBuild_ObservesCancellationBeforePublishingAModel()
    {
        var fixture = Find("T-OPEN");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ContinuousGeometry.Build(
            fixture.Walls, fixture.Exits, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void SampleDistanceMapCarriesContinuousProvenanceAndRoutesQueriesThroughTheSameMap()
    {
        var fixture = Find("T-OPEN");
        var map = Build(fixture, 0);
        var grid = WalkabilityGrid.Build(fixture.Walls, 0.5, marginCells: 0);
        var result = map.SampleDistanceMap(grid);
        var query = new WorldPoint(8, 5);

        Assert.Equal(DistanceMapResult.ContinuousEngine, result.Engine);
        Assert.Equal(ContinuousGeometry.PolicyVersion, result.PolicyVersion);
        Assert.Equal(map.ModelHash, result.ModelHash);
        Assert.Equal(map.Query(query)!.Distance,
            DistanceMapCalculator.FindPath(grid, result, query)!.Distance);
    }

    [Fact]
    public void Sampling_CoversBodyAndMultiExitMapsAndKeepsTheFarthestContinuousPath()
    {
        var bodyFixture = Find("T-OPEN");
        var bodyGrid = WalkabilityGrid.Build(
            bodyFixture.Walls, 0.5, marginCells: 0, clearanceRadius: bodyFixture.BodyRadius!.Value);
        var bodyMap = Build(bodyFixture, bodyFixture.BodyRadius.Value);
        var bodySample = bodyMap.Sample(bodyGrid, maxCandidateTests: 1_000_000);
        var farthest = bodySample.FarthestCell!.Value;
        var farthestPath = bodyMap.Query(bodyGrid.CellCenter(farthest.Col, farthest.Row))!;

        Assert.Equal(bodySample.MaxDistance, farthestPath.Distance);
        Assert.True(bodyMap.Validate(farthestPath.Points));
        Assert.DoesNotContain(bodySample.Distances.Cast<double>(), double.IsNaN);

        var multiFixture = Find("T-MULTI-TIE");
        var multiGrid = WalkabilityGrid.Build(multiFixture.Walls, 0.5, marginCells: 0);
        var multiMap = Build(multiFixture, 0);
        var multiSample = multiMap.Sample(multiGrid, maxCandidateTests: 1_000_000);

        Assert.Equal(multiGrid.Rows, multiSample.Distances.GetLength(0));
        Assert.Equal(multiGrid.Cols, multiSample.Distances.GetLength(1));
        Assert.Equal(0, multiMap.Query(new WorldPoint(5, 5))!.ExitIndex);
    }

    [Fact]
    public void Sampling_DistancesMatchFullyReconstructedQueriesForGeometricAndBodyModels()
    {
        foreach (var fixture in Fixtures.Where(item => item.BuildReason is null).Append(MediumFixture()))
        {
            var radii = fixture.BodyRadius is { } bodyRadius && fixture.BodyBuildReason is null
                ? new[] { 0, bodyRadius }
                : new[] { 0d };
            foreach (double radius in radii)
            {
                var map = Build(fixture, radius);
                var grid = WalkabilityGrid.Build(
                    fixture.Walls, fixture.Id == "B-MEDIUM" ? 1 : 0.5,
                    marginCells: 0, clearanceRadius: radius);
                var sample = map.Sample(grid, maxCandidateTests: 10_000_000, workerCount: 1);
                var parallel = map.Sample(grid, maxCandidateTests: 10_000_000, workerCount: 2);

                Assert.Equal(sample.Distances.Cast<double>(), parallel.Distances.Cast<double>());
                Assert.Equal(sample.FarthestCell, parallel.FarthestCell);
                Assert.Equal(sample.MaxDistance, parallel.MaxDistance);

                for (int row = 0; row < grid.Rows; row++)
                for (int col = 0; col < grid.Cols; col++)
                {
                    if (!grid.IsWalkable(col, row)) continue;
                    Assert.Equal(
                        map.Query(grid.CellCenter(col, row))?.Distance ?? double.PositiveInfinity,
                        sample.Distances[row, col]);
                }
            }
        }
    }

    [Fact]
    public void Sampling_DistanceOnlyPathAllocatesLessThanFullPathQueries()
    {
        var fixture = Find("T-OPEN");
        foreach (double radius in new[] { 0, fixture.BodyRadius!.Value })
        {
            var map = Build(fixture, radius);
            var grid = WalkabilityGrid.Build(
                fixture.Walls, 0.2, marginCells: 0, clearanceRadius: radius);

            _ = map.Sample(grid, maxCandidateTests: 10_000_000, workerCount: 1);
            QueryEveryCell();

            long before = GC.GetTotalAllocatedBytes(true);
            var sample = map.Sample(grid, maxCandidateTests: 10_000_000, workerCount: 1);
            long sampleBytes = GC.GetTotalAllocatedBytes(true) - before;

            before = GC.GetTotalAllocatedBytes(true);
            long candidateTests = QueryEveryCell();
            long fullPathBytes = GC.GetTotalAllocatedBytes(true) - before;

            Assert.Equal(sample.CandidateTests, candidateTests);
            Assert.True(sampleBytes < fullPathBytes * 0.8,
                $"Distance-only sampling allocated {sampleBytes:N0} bytes; full paths allocated {fullPathBytes:N0} bytes.");

            long QueryEveryCell()
            {
                long tests = 0;
                for (int row = 0; row < grid.Rows; row++)
                for (int col = 0; col < grid.Cols; col++)
                {
                    if (!grid.IsWalkable(col, row)) continue;
                    tests += map.QueryWithMetrics(grid.CellCenter(col, row)).CandidateTests;
                }
                return tests;
            }
        }
    }

    private static ContinuousShortestPathMap Build(Fixture fixture, double radius) =>
        ContinuousShortestPathMap.Build(ContinuousGeometry.Build(fixture.Walls, fixture.Exits, radius));

    private static void AssertQueries(
        string fixtureId,
        ContinuousShortestPathMap map,
        IReadOnlyList<QueryExpectation> queries,
        double tolerance)
    {
        foreach (var expected in queries)
        {
            var query = map.QueryWithMetrics(expected.Point);
            var actual = query.Path;
            if (expected.Distance is null)
            {
                Assert.Null(actual);
                continue;
            }

            Assert.True(actual is not null,
                $"{fixtureId}: no route from {expected.Point}; candidates={query.CandidateTests}; vertices={map.Vertices.Count}");
            Assert.InRange(actual!.Distance, expected.Distance.Value - tolerance, expected.Distance.Value + tolerance);
            Assert.Equal(expected.Exit, actual.ExitIndex);
            if (expected.Contact is { } contact)
            {
                AssertPoint(contact, actual.Contact, tolerance);
            }
            if (expected.Arrival is { } arrival)
            {
                AssertPoint(arrival, actual.Arrival, tolerance);
            }
            Assert.Equal(expected.Point, actual.Points[0]);
            Assert.True(map.Validate(actual.Points), fixtureId);
        }
    }

    private static void AssertPoint(WorldPoint expected, WorldPoint actual, double tolerance)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
    }

    private static Fixture Find(string id) => Fixtures.Single(fixture => fixture.Id == id);

    private static Fixture MediumFixture()
    {
        var walls = new List<Segment>();
        AddSubdivided(new(0, 0), new(60, 0), 120);
        AddSubdivided(new(60, 0), new(60, 40), 80);
        AddSubdivided(new(60, 40), new(0, 40), 120);
        AddSubdivided(new(0, 40), new(0, 0), 80);
        walls.AddRange([
            new(new(12, 0), new(12, 32)),
            new(new(24, 8), new(24, 40)),
            new(new(36, 0), new(36, 32)),
            new(new(48, 8), new(48, 40)),
        ]);
        IReadOnlyList<IReadOnlyList<WorldPoint>> exits =
        [
            new WorldPoint[] { new(0, 8), new(0, 12) },
            new WorldPoint[] { new(0, 28), new(0, 32) },
            new WorldPoint[] { new(60, 8), new(60, 12) },
            new WorldPoint[] { new(60, 28), new(60, 32) },
        ];
        return new Fixture("B-MEDIUM", walls, exits, [], 0.2, [], null, null);

        void AddSubdivided(WorldPoint start, WorldPoint end, int count)
        {
            for (int index = 0; index < count; index++)
            {
                WorldPoint Point(double ratio) => new(
                    start.X + (end.X - start.X) * ratio,
                    start.Y + (end.Y - start.Y) * ratio);
                walls.Add(new Segment(Point((double)index / count), Point((double)(index + 1) / count)));
            }
        }
    }

    private static IReadOnlyList<Fixture> LoadFixtures()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ContinuousMigration", "fixtures.json");
        var fixtures = JsonSerializer.Deserialize<List<FixtureDto>>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
        return fixtures.Select(fixture => new Fixture(
            fixture.Id,
            fixture.Walls.Select(wall => new Segment(Point(wall, 0), Point(wall, 2))).ToArray(),
            fixture.Exits.Select(exit => (IReadOnlyList<WorldPoint>)exit.Select(point =>
                new WorldPoint(point[0], point[1])).ToArray()).ToArray(),
            Convert(fixture.Queries),
            fixture.BodyRadius,
            Convert(fixture.BodyQueries),
            fixture.BuildReason,
            fixture.BodyBuildReason)).ToArray();

        static WorldPoint Point(double[] values, int offset) => new(values[offset], values[offset + 1]);
        static IReadOnlyList<QueryExpectation> Convert(IEnumerable<QueryDto>? queries) =>
            queries?.Select(query => new QueryExpectation(
                new WorldPoint(query.Point[0], query.Point[1]),
                query.Distance,
                query.Exit,
                query.Contact is { } contact ? new WorldPoint(contact[0], contact[1]) : null,
                query.Arrival is { } arrival ? new WorldPoint(arrival[0], arrival[1]) : null)).ToArray() ?? [];
    }

    public sealed record FixtureDto(
        string Id,
        double[][] Walls,
        double[][][] Exits,
        QueryDto[]? Queries,
        double? BodyRadius,
        QueryDto[]? BodyQueries,
        string? BuildReason,
        string? BodyBuildReason);

    public sealed record QueryDto(
        double[] Point,
        double? Distance,
        int? Exit,
        double[]? Contact,
        double[]? Arrival);

    public sealed record Fixture(
        string Id,
        IReadOnlyList<Segment> Walls,
        IReadOnlyList<IReadOnlyList<WorldPoint>> Exits,
        IReadOnlyList<QueryExpectation> Queries,
        double? BodyRadius,
        IReadOnlyList<QueryExpectation> BodyQueries,
        string? BuildReason,
        string? BodyBuildReason);

    public sealed record QueryExpectation(
        WorldPoint Point,
        double? Distance,
        int? Exit,
        WorldPoint? Contact,
        WorldPoint? Arrival);
}
