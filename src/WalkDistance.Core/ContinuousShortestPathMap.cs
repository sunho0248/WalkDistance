using System.Diagnostics;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;

namespace WalkDistance.Core;

public sealed record ContinuousVertex(int Id, WorldPoint Point, string Kind);

public sealed record ContinuousPath(
    IReadOnlyList<WorldPoint> Points,
    double Distance,
    int ExitIndex,
    WorldPoint Contact,
    WorldPoint Arrival);

public sealed record ContinuousQueryResult(ContinuousPath? Path, long CandidateTests);

public sealed record ContinuousSampleResult(
    double[,] Distances,
    (int Col, int Row)? FarthestCell,
    double MaxDistance,
    int UnreachableCellCount,
    long CandidateTests);

internal readonly record struct ContinuousBuildMetrics(
    long VisibilityGraphTicks,
    long VisibilityGraphAllocatedBytes,
    long MultiSourceDijkstraTicks,
    long MultiSourceDijkstraAllocatedBytes);

internal readonly record struct ContinuousQueryStageMetrics(
    long DistanceSelectionTicks,
    long DistanceSelectionAllocatedBytes,
    long ReconstructValidateTicks,
    long ReconstructValidateAllocatedBytes);

internal readonly record struct ContinuousDetailedQueryResult(
    ContinuousQueryResult Result,
    ContinuousQueryStageMetrics Metrics);

internal readonly record struct ContinuousDistanceQueryResult(double Distance, long CandidateTests);

public sealed class ContinuousShortestPathMap
{
    public const long DefaultMaxCandidateTests = 250_000_000;
    private const long MaxCandidatePairs = 50_000_000;
    private const double TwoPi = Math.PI * 2;
    private const double AngleTolerance = 1e-10;
    [ThreadStatic]
    private static RobustLineIntersector? _lineIntersector;

    private readonly ContinuousModel _model;
    private readonly GraphVertex[] _vertices;
    private readonly int _edgeCount;
    private readonly Label?[] _labels;
    private readonly int[] _queryVertexIds;
    private readonly IReadOnlyDictionary<WorldPoint, Sector[]> _blockerSectors;

    private ContinuousShortestPathMap(
        ContinuousModel model,
        GraphVertex[] vertices,
        int edgeCount,
        Label?[] labels,
        IReadOnlyDictionary<WorldPoint, Sector[]> blockerSectors)
    {
        _model = model;
        _vertices = vertices;
        _edgeCount = edgeCount;
        _labels = labels;
        _queryVertexIds = Enumerable.Range(0, vertices.Length)
            .Where(id => !vertices[id].TerminalOnly && labels[id] is not null)
            .ToArray();
        _blockerSectors = blockerSectors;
        Vertices = vertices.Select(vertex => new ContinuousVertex(vertex.Id, vertex.Point, vertex.Kind)).ToArray();
    }

    public string ModelHash => _model.ModelHash;
    public IReadOnlyList<ContinuousVertex> Vertices { get; }
    public int EdgeCount => _edgeCount;
    public int TerminalSeedCount { get; private init; }

    public static ContinuousShortestPathMap Build(
        ContinuousModel model,
        CancellationToken cancellationToken = default) =>
        BuildCore(model, cancellationToken, collectMetrics: false, out _);

    internal static ContinuousShortestPathMap BuildWithMetrics(
        ContinuousModel model,
        out ContinuousBuildMetrics metrics,
        CancellationToken cancellationToken = default) =>
        BuildCore(model, cancellationToken, collectMetrics: true, out metrics);

    private static ContinuousShortestPathMap BuildCore(
        ContinuousModel model,
        CancellationToken cancellationToken,
        bool collectMetrics,
        out ContinuousBuildMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        long visibilityAllocatedBefore = collectMetrics ? GC.GetTotalAllocatedBytes(true) : 0;
        long visibilityStarted = collectMetrics ? Stopwatch.GetTimestamp() : 0;
        var (vertices, sectors) = BuildVertices(model);
        long pairs;
        try
        {
            pairs = checked((long)vertices.Length * (vertices.Length - 1) / 2);
        }
        catch (OverflowException)
        {
            throw new ContinuousBuildException("MEMORY_LIMIT", "Visibility pair count overflowed.");
        }
        if (pairs > MaxCandidatePairs)
        {
            throw new ContinuousBuildException("MEMORY_LIMIT", "Visibility graph exceeds 50 million candidate pairs.");
        }

        var adjacency = Enumerable.Range(0, vertices.Length).Select(_ => new List<Edge>()).ToArray();
        for (int left = 0; left < vertices.Length; left++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (vertices[left].TerminalOnly) continue;
            for (int right = left + 1; right < vertices.Length; right++)
            {
                if (vertices[right].TerminalOnly || vertices[left].Point == vertices[right].Point) continue;
                if (!CanTravel(model, vertices[left].Point, vertices[right].Point, vertices[left], vertices[right]))
                {
                    continue;
                }
                double length = ContinuousGeometry.Distance(vertices[left].Point, vertices[right].Point);
                adjacency[left].Add(new Edge(right, length));
                adjacency[right].Add(new Edge(left, length));
            }
        }
        var sortedAdjacency = adjacency.Select(edges => edges.OrderBy(edge => edge.To).ToArray()).ToArray();
        long visibilityTicks = collectMetrics ? Stopwatch.GetTimestamp() - visibilityStarted : 0;
        long visibilityAllocated = collectMetrics ? GC.GetTotalAllocatedBytes(true) - visibilityAllocatedBefore : 0;

        long dijkstraAllocatedBefore = collectMetrics ? GC.GetTotalAllocatedBytes(true) : 0;
        long dijkstraStarted = collectMetrics ? Stopwatch.GetTimestamp() : 0;
        var labels = new Label?[vertices.Length];
        int seedCount = 0;
        var queue = new PriorityQueue<int, QueuePriority>();
        for (int vertexId = 0; vertexId < vertices.Length; vertexId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (vertices[vertexId].TerminalOnly) continue;
            foreach (var target in model.Targets)
            {
                foreach (var arrival in CandidateArrivals(vertices[vertexId].Point, target.Segment))
                {
                    seedCount++;
                    if (!CanTravel(model, vertices[vertexId].Point, arrival, vertices[vertexId], null)) continue;
                    var candidate = new Label(
                        ContinuousGeometry.Distance(vertices[vertexId].Point, arrival),
                        target.ExitIndex,
                        -1,
                        target.Id,
                        arrival,
                        target.Contact(arrival));
                    if (!Better(candidate, labels[vertexId])) continue;
                    labels[vertexId] = candidate;
                    queue.Enqueue(vertexId, Priority(candidate, vertexId));
                }
            }
        }

        while (queue.TryDequeue(out int current, out var priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentLabel = labels[current]!;
            if (!priority.Matches(currentLabel, current)) continue;
            foreach (var edge in sortedAdjacency[current])
            {
                var candidate = currentLabel with
                {
                    Distance = currentLabel.Distance + edge.Length,
                    Next = current,
                };
                if (!Better(candidate, labels[edge.To])) continue;
                labels[edge.To] = candidate;
                queue.Enqueue(edge.To, Priority(candidate, edge.To));
            }
        }

        long dijkstraTicks = collectMetrics ? Stopwatch.GetTimestamp() - dijkstraStarted : 0;
        long dijkstraAllocated = collectMetrics ? GC.GetTotalAllocatedBytes(true) - dijkstraAllocatedBefore : 0;
        metrics = new ContinuousBuildMetrics(
            visibilityTicks, visibilityAllocated, dijkstraTicks, dijkstraAllocated);
        int edgeCount = sortedAdjacency.Sum(edges => edges.Length) / 2;
        return new ContinuousShortestPathMap(model, vertices, edgeCount, labels, sectors)
        {
            TerminalSeedCount = seedCount,
        };
    }

    public ContinuousPath? Query(WorldPoint point) => QueryWithMetrics(point).Path;

    public ContinuousQueryResult QueryWithMetrics(WorldPoint point)
    {
        var selection = Select(point);
        return CreateQueryResult(point, selection);
    }

    internal ContinuousDetailedQueryResult QueryWithStageMetrics(WorldPoint point)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        long started = Stopwatch.GetTimestamp();
        var selection = Select(point);
        long selectionTicks = Stopwatch.GetTimestamp() - started;
        long selectionAllocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;

        allocatedBefore = GC.GetTotalAllocatedBytes(true);
        started = Stopwatch.GetTimestamp();
        var result = CreateQueryResult(point, selection);
        return new ContinuousDetailedQueryResult(result, new ContinuousQueryStageMetrics(
            selectionTicks,
            selectionAllocated,
            Stopwatch.GetTimestamp() - started,
            GC.GetTotalAllocatedBytes(true) - allocatedBefore));
    }

    private ContinuousDistanceQueryResult QueryDistanceWithMetrics(WorldPoint point)
    {
        var selection = Select(point);
        return selection.Best is { } best
            ? new ContinuousDistanceQueryResult(DistanceWithoutPath(point, best), selection.CandidateTests)
            : new ContinuousDistanceQueryResult(double.PositiveInfinity, selection.CandidateTests);
    }

    private QuerySelection Select(WorldPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !ContainsFreePoint(point))
        {
            return new QuerySelection(null, 0);
        }

        int capacity = checked(_model.Targets.Count * 3 + _queryVertexIds.Length);
        Span<QueryOption> options = capacity <= 256
            ? stackalloc QueryOption[capacity]
            : new QueryOption[capacity];
        int optionCount = 0;
        foreach (var target in _model.Targets)
        {
            var segment = target.Segment;
            double dx = segment.End.X - segment.Start.X;
            double dy = segment.End.Y - segment.Start.Y;
            double lengthSquared = dx * dx + dy * dy;
            double projection = lengthSquared == 0 ? 0 :
                ((point.X - segment.Start.X) * dx + (point.Y - segment.Start.Y) * dy) / lengthSquared;
            projection = Math.Clamp(projection, 0, 1);
            var projected = new WorldPoint(
                segment.Start.X + projection * dx, segment.Start.Y + projection * dy);
            options[optionCount++] = TargetOption(point, target, projected);
            if (segment.Start != projected) options[optionCount++] = TargetOption(point, target, segment.Start);
            if (segment.End != projected && segment.End != segment.Start)
                options[optionCount++] = TargetOption(point, target, segment.End);
        }

        foreach (int vertexId in _queryVertexIds)
        {
            var label = _labels[vertexId]!;
            double direct = ContinuousGeometry.Distance(point, _vertices[vertexId].Point);
            options[optionCount++] = new QueryOption(
                direct + label.Distance,
                label.ExitIndex,
                vertexId,
                label.TargetId,
                label.Arrival,
                label.Contact);
        }

        options[..optionCount].Sort();
        long tests = 0;
        QueryCandidate? best = null;
        foreach (var option in options[..optionCount])
        {
            if (best is not null && option.LowerBound > best.Distance) break;
            WorldPoint destination = option.VertexId < 0
                ? option.Arrival : _vertices[option.VertexId].Point;
            tests++;
            if (!CanTravel(_model, point, destination, null,
                    option.VertexId < 0 ? null : _vertices[option.VertexId])) continue;
            var candidate = new QueryCandidate(
                option.LowerBound,
                option.ExitIndex,
                option.VertexId,
                option.TargetId,
                option.Arrival,
                option.Contact);
            if (Better(candidate, best)) best = candidate;
        }

        return new QuerySelection(best, tests);
    }

    private ContinuousQueryResult CreateQueryResult(WorldPoint point, QuerySelection selection)
    {
        if (selection.Best is not { } best)
            return new ContinuousQueryResult(null, selection.CandidateTests);
        var points = Reconstruct(point, best);
        if (!Validate(points))
        {
            throw new ContinuousBuildException("PATH_VALIDATION_FAILED", "A reconstructed continuous path is not legal.");
        }
        double length = 0;
        for (int index = 0; index < points.Count - 1; index++)
        {
            length += ContinuousGeometry.Distance(points[index], points[index + 1]);
        }
        return new ContinuousQueryResult(
            new ContinuousPath(points, length, best.ExitIndex, best.Contact, best.Arrival),
            selection.CandidateTests);
    }

    public ContinuousSampleResult Sample(
        WalkabilityGrid grid,
        long maxCandidateTests = DefaultMaxCandidateTests,
        int workerCount = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCandidateTests, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 0);
        long upperBound;
        try
        {
            upperBound = checked((long)grid.InteriorCellCount *
                (_vertices.Count(vertex => !vertex.TerminalOnly) + _model.Targets.Count * 3L));
        }
        catch (OverflowException)
        {
            throw new ContinuousBuildException("SAMPLING_LIMIT", "Sampling candidate count overflowed.");
        }
        if (upperBound > maxCandidateTests)
        {
            throw new ContinuousBuildException("SAMPLING_LIMIT", "Sampling exceeds the candidate-connection cap.");
        }

        var distances = new double[grid.Rows, grid.Cols];
        for (int row = 0; row < grid.Rows; row++)
        for (int col = 0; col < grid.Cols; col++)
            distances[row, col] = double.PositiveInfinity;

        var rowTests = new long[grid.Rows];
        var rowUnreachable = new int[grid.Rows];
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = workerCount == 0 ? PhysicalCoreDetector.GetWorkerCount() : workerCount,
        };
        Parallel.For(0, grid.Rows, parallelOptions, row =>
        {
            for (int col = 0; col < grid.Cols; col++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!grid.IsWalkable(col, row)) continue;
                var query = QueryDistanceWithMetrics(grid.CellCenter(col, row));
                rowTests[row] = checked(rowTests[row] + query.CandidateTests);
                if (!double.IsFinite(query.Distance))
                {
                    rowUnreachable[row]++;
                    continue;
                }
                distances[row, col] = query.Distance;
            }
        });

        long tests = rowTests.Sum();
        (int Col, int Row)? farthest = null;
        double maxDistance = 0;
        for (int row = 0; row < grid.Rows; row++)
        {
            for (int col = 0; col < grid.Cols; col++)
            {
                if (!double.IsFinite(distances[row, col])) continue;
                double distance = distances[row, col];
                if (farthest is null || distance > maxDistance)
                {
                    farthest = (col, row);
                    maxDistance = distance;
                }
            }
        }
        return new ContinuousSampleResult(
            distances, farthest, maxDistance, rowUnreachable.Sum(), tests);
    }

    public DistanceMapResult SampleDistanceMap(
        WalkabilityGrid grid,
        CancellationToken cancellationToken = default)
    {
        var sample = Sample(grid, cancellationToken: cancellationToken);
        return new DistanceMapResult(
            sample.Distances,
            sample.FarthestCell,
            sample.MaxDistance,
            sample.UnreachableCellCount,
            new (int Col, int Row)?[grid.Rows, grid.Cols])
        {
            Engine = DistanceMapResult.ContinuousEngine,
            PolicyVersion = ContinuousGeometry.PolicyVersion,
            ModelHash = ModelHash,
            ContinuousMap = this,
        };
    }

    public bool Validate(IReadOnlyList<WorldPoint> points)
    {
        if (points.Count == 0 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            return false;
        }
        for (int index = 0; index < points.Count - 1; index++)
        {
            if (points[index] == points[index + 1] ||
                !CanTravel(_model, points[index], points[index + 1], null, null))
            {
                return false;
            }
            if (_model.ClearanceRadius == 0)
            {
                var midpoint = new WorldPoint(
                    (points[index].X + points[index + 1].X) / 2,
                    (points[index].Y + points[index + 1].Y) / 2);
                if (!_model.PreparedFreeSpace.Covers(
                        _model.Factory.CreatePoint(ContinuousGeometry.Coordinate(midpoint))))
                {
                    return false;
                }
            }
        }
        for (int index = 1; index < points.Count - 1; index++)
        {
            if (!_blockerSectors.TryGetValue(points[index], out var sectors)) continue;
            double incoming = Angle(points[index], points[index - 1]);
            double outgoing = Angle(points[index], points[index + 1]);
            if (!sectors.Any(sector => sector.Contains(incoming) && sector.Contains(outgoing))) return false;
        }
        return true;
    }

    private IReadOnlyList<WorldPoint> Reconstruct(WorldPoint query, QueryCandidate candidate)
    {
        var points = new List<WorldPoint> { query };
        if (candidate.VertexId >= 0)
        {
            int vertexId = candidate.VertexId;
            while (vertexId >= 0)
            {
                Add(points, _vertices[vertexId].Point);
                vertexId = _labels[vertexId]!.Next;
            }
        }
        Add(points, candidate.Arrival);
        return points;
    }

    private double DistanceWithoutPath(WorldPoint query, QueryCandidate candidate)
    {
        double distance = 0;
        WorldPoint previous = query;
        if (candidate.VertexId >= 0)
        {
            int vertexId = candidate.VertexId;
            while (vertexId >= 0)
            {
                var point = _vertices[vertexId].Point;
                if (previous != point) distance += ContinuousGeometry.Distance(previous, point);
                previous = point;
                vertexId = _labels[vertexId]!.Next;
            }
        }
        if (previous != candidate.Arrival)
            distance += ContinuousGeometry.Distance(previous, candidate.Arrival);
        return distance;
    }

    private bool ContainsFreePoint(WorldPoint point)
    {
        var geometry = _model.Factory.CreatePoint(ContinuousGeometry.Coordinate(point));
        if (!_model.PreparedFreeSpace.Covers(geometry)) return false;
        if (_model.ClearanceRadius > 0) return true;
        return _model.BlockerIndex.Query(geometry.EnvelopeInternal)
            .All(blocker => blocker.Distance(geometry) > ContinuousGeometry.Precision);
    }

    private static (GraphVertex[] Vertices, IReadOnlyDictionary<WorldPoint, Sector[]> Sectors) BuildVertices(
        ContinuousModel model)
    {
        var incidence = new Dictionary<WorldPoint, List<double>>();
        foreach (var blocker in model.BlockerSegments)
        {
            var start = Point(blocker.GetCoordinateN(0));
            var end = Point(blocker.GetCoordinateN(1));
            AddAngle(start, Angle(start, end));
            AddAngle(end, Angle(end, start));
        }

        var sectors = new Dictionary<WorldPoint, Sector[]>();
        var candidates = new List<GraphVertex>();
        foreach (var pair in incidence.OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y))
        {
            var local = CreateSectors(pair.Value);
            sectors[pair.Key] = local;
            bool collinearPassThrough = pair.Value.Count == 2 &&
                Math.Abs(AngularDistance(pair.Value[0], pair.Value[1]) - Math.PI) <= AngleTolerance;
            foreach (var sector in local)
            {
                double midpoint = NormalizeAngle(sector.Start + sector.Span / 2);
                var probe = new WorldPoint(
                    pair.Key.X + Math.Cos(midpoint) * ContinuousGeometry.Precision * 10,
                    pair.Key.Y + Math.Sin(midpoint) * ContinuousGeometry.Precision * 10);
                if (model.PreparedFreeSpace.Covers(model.Factory.CreatePoint(ContinuousGeometry.Coordinate(probe))))
                {
                    candidates.Add(new GraphVertex(
                        -1, pair.Key, collinearPassThrough ? "collinear-blocker" : "blocker",
                        sector, collinearPassThrough));
                }
            }
        }

        foreach (var coordinate in model.FreeSpace.Boundary.Coordinates.Select(Point).Distinct())
        {
            if (!incidence.ContainsKey(coordinate))
            {
                candidates.Add(new GraphVertex(-1, coordinate, "boundary", null, false));
            }
        }
        foreach (var target in model.Targets)
        {
            foreach (var point in new[] { target.Segment.Start, target.Segment.End })
            {
                if (candidates.All(vertex => vertex.Point != point))
                {
                    candidates.Add(new GraphVertex(-1, point, "target", null, true));
                }
            }
        }

        var sorted = candidates
            .OrderBy(vertex => vertex.Point.X)
            .ThenBy(vertex => vertex.Point.Y)
            .ThenBy(vertex => vertex.Kind, StringComparer.Ordinal)
            .ThenBy(vertex => vertex.Sector?.Start ?? -1)
            .Select((vertex, id) => vertex with { Id = id })
            .ToArray();
        return (sorted, sectors);

        void AddAngle(WorldPoint point, double angle)
        {
            if (!incidence.TryGetValue(point, out var angles)) incidence[point] = angles = [];
            if (angles.All(existing => AngularDistance(existing, angle) > AngleTolerance)) angles.Add(angle);
        }
    }

    private static Sector[] CreateSectors(IReadOnlyList<double> source)
    {
        var angles = source.Select(NormalizeAngle).Order().ToArray();
        if (angles.Length <= 1) return [new Sector(angles.FirstOrDefault(), TwoPi)];
        var sectors = new Sector[angles.Length];
        for (int index = 0; index < angles.Length; index++)
        {
            double start = angles[index];
            double end = index == angles.Length - 1 ? angles[0] + TwoPi : angles[index + 1];
            sectors[index] = new Sector(start, end - start);
        }
        return sectors;
    }

    private static bool CanTravel(
        ContinuousModel model,
        WorldPoint start,
        WorldPoint end,
        GraphVertex? startVertex,
        GraphVertex? endVertex)
    {
        if (start == end) return true;
        if (startVertex?.Sector is { } startSector && !startSector.Contains(Angle(start, end))) return false;
        if (endVertex?.Sector is { } endSector && !endSector.Contains(Angle(end, start))) return false;
        var line = ContinuousGeometry.Line(new Segment(start, end), model.Factory);
        var lineStart = line.GetCoordinateN(0);
        var lineEnd = line.GetCoordinateN(1);
        var intersector = _lineIntersector ??= new RobustLineIntersector();
        if (model.ClearanceRadius > 0 && !model.PreparedFreeSpace.Covers(line)) return false;
        foreach (var blocker in model.BlockerIndex.Query(line.EnvelopeInternal))
        {
            intersector.ComputeIntersection(
                lineStart, lineEnd, blocker.GetCoordinateN(0), blocker.GetCoordinateN(1));
            if (!intersector.HasIntersection) continue;
            if (intersector.IntersectionNum == 2) return false;
            for (int index = 0; index < intersector.IntersectionNum; index++)
            {
                var coordinate = intersector.GetIntersection(index);
                var point = Point(coordinate);
                if (ContinuousGeometry.Distance(point, start) > ContinuousGeometry.Precision &&
                    ContinuousGeometry.Distance(point, end) > ContinuousGeometry.Precision)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static IEnumerable<WorldPoint> CandidateArrivals(WorldPoint point, Segment target)
    {
        double dx = target.End.X - target.Start.X;
        double dy = target.End.Y - target.Start.Y;
        double lengthSquared = dx * dx + dy * dy;
        double projection = lengthSquared == 0 ? 0 :
            ((point.X - target.Start.X) * dx + (point.Y - target.Start.Y) * dy) / lengthSquared;
        projection = Math.Clamp(projection, 0, 1);
        var projected = new WorldPoint(target.Start.X + projection * dx, target.Start.Y + projection * dy);
        yield return projected;
        if (target.Start != projected) yield return target.Start;
        if (target.End != projected && target.End != target.Start) yield return target.End;
    }

    private static bool Better(Label candidate, Label? current)
    {
        if (current is null) return true;
        int comparison = candidate.Distance.CompareTo(current.Distance);
        if (comparison != 0) return comparison < 0;
        comparison = candidate.ExitIndex.CompareTo(current.ExitIndex);
        if (comparison != 0) return comparison < 0;
        comparison = candidate.Next.CompareTo(current.Next);
        if (comparison != 0) return comparison < 0;
        comparison = candidate.TargetId.CompareTo(current.TargetId);
        if (comparison != 0) return comparison < 0;
        comparison = ContinuousGeometry.Compare(candidate.Arrival, current.Arrival);
        return comparison < 0;
    }

    private static bool Better(QueryCandidate candidate, QueryCandidate? current)
    {
        if (current is null) return true;
        int comparison = candidate.Distance.CompareTo(current.Distance);
        if (comparison != 0) return comparison < 0;
        comparison = candidate.ExitIndex.CompareTo(current.ExitIndex);
        if (comparison != 0) return comparison < 0;
        comparison = candidate.VertexId.CompareTo(current.VertexId);
        if (comparison != 0) return comparison < 0;
        comparison = candidate.TargetId.CompareTo(current.TargetId);
        if (comparison != 0) return comparison < 0;
        return ContinuousGeometry.Compare(candidate.Arrival, current.Arrival) < 0;
    }

    private static QueryOption TargetOption(
        WorldPoint query,
        ContinuousTarget target,
        WorldPoint arrival) => new(
            ContinuousGeometry.Distance(query, arrival), target.ExitIndex, -1, target.Id,
            arrival, target.Contact(arrival));

    private static QueuePriority Priority(Label label, int vertexId) =>
        new(label.Distance, label.ExitIndex, vertexId, label.TargetId);

    private static void Add(List<WorldPoint> points, WorldPoint point)
    {
        if (points.Count == 0 || points[^1] != point) points.Add(point);
    }

    private static double Angle(WorldPoint origin, WorldPoint point) =>
        NormalizeAngle(Math.Atan2(point.Y - origin.Y, point.X - origin.X));
    private static double NormalizeAngle(double angle)
    {
        angle %= TwoPi;
        return angle < 0 ? angle + TwoPi : angle;
    }
    private static double AngularDistance(double left, double right)
    {
        double distance = Math.Abs(NormalizeAngle(left) - NormalizeAngle(right));
        return Math.Min(distance, TwoPi - distance);
    }
    private static WorldPoint Point(Coordinate coordinate) =>
        ContinuousGeometry.Snap(new WorldPoint(coordinate.X, coordinate.Y));

    private sealed record GraphVertex(
        int Id,
        WorldPoint Point,
        string Kind,
        Sector? Sector,
        bool TerminalOnly);

    private readonly record struct Sector(double Start, double Span)
    {
        internal bool Contains(double angle)
        {
            if (Span >= TwoPi - AngleTolerance) return AngularDistance(angle, Start) > AngleTolerance;
            double relative = NormalizeAngle(angle - Start);
            return relative > AngleTolerance && relative < Span - AngleTolerance;
        }
    }

    private readonly record struct Edge(int To, double Length);
    private sealed record Label(
        double Distance,
        int ExitIndex,
        int Next,
        int TargetId,
        WorldPoint Arrival,
        WorldPoint Contact);
    private sealed record QueryCandidate(
        double Distance,
        int ExitIndex,
        int VertexId,
        int TargetId,
        WorldPoint Arrival,
        WorldPoint Contact);
    private readonly record struct QuerySelection(QueryCandidate? Best, long CandidateTests);
    private readonly record struct QueryOption(
        double LowerBound,
        int ExitIndex,
        int VertexId,
        int TargetId,
        WorldPoint Arrival,
        WorldPoint Contact) : IComparable<QueryOption>
    {
        public int CompareTo(QueryOption other)
        {
            int comparison = LowerBound.CompareTo(other.LowerBound);
            if (comparison != 0) return comparison;
            comparison = ExitIndex.CompareTo(other.ExitIndex);
            if (comparison != 0) return comparison;
            comparison = VertexId.CompareTo(other.VertexId);
            if (comparison != 0) return comparison;
            comparison = TargetId.CompareTo(other.TargetId);
            return comparison != 0 ? comparison : ContinuousGeometry.Compare(Arrival, other.Arrival);
        }
    }

    private readonly record struct QueuePriority(
        double Distance,
        int ExitIndex,
        int VertexId,
        int TargetId) : IComparable<QueuePriority>
    {
        public int CompareTo(QueuePriority other)
        {
            int comparison = Distance.CompareTo(other.Distance);
            if (comparison != 0) return comparison;
            comparison = ExitIndex.CompareTo(other.ExitIndex);
            if (comparison != 0) return comparison;
            comparison = VertexId.CompareTo(other.VertexId);
            return comparison != 0 ? comparison : TargetId.CompareTo(other.TargetId);
        }

        internal bool Matches(Label label, int vertexId) =>
            Distance == label.Distance && ExitIndex == label.ExitIndex &&
            VertexId == vertexId && TargetId == label.TargetId;
    }
}
