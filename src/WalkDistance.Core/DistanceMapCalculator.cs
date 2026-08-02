namespace WalkDistance.Core;

public sealed record DistanceMapResult(
    double[,] Distances,
    (int Col, int Row)? FarthestCell,
    double MaxDistance,
    int UnreachableCellCount,
    (int Col, int Row)?[,] Predecessor)
{
    internal IReadOnlyDictionary<(int Col, int Row), DistanceRoot> Roots { get; init; } =
        new Dictionary<(int Col, int Row), DistanceRoot>();
    internal IReadOnlyList<IReadOnlyList<DistanceSource>> SourceGroups { get; init; } =
        Array.Empty<IReadOnlyList<DistanceSource>>();
    internal int[,]? WinningGroupIndexes { get; init; }
}

public readonly record struct DistanceSource(
    int Col,
    int Row,
    WorldPoint ExitPoint,
    Segment? ExitSegment = null,
    int? ExitGroupId = null);

public sealed record WalkingPath(IReadOnlyList<WorldPoint> Points, double Distance);

internal readonly record struct DistanceRoot(WorldPoint Contact, Segment? ExitSegment);

internal sealed record DistanceField(
    double[,] Distances,
    (int Col, int Row)?[,] Predecessor,
    IReadOnlyDictionary<(int Col, int Row), DistanceRoot> Roots);

/// <summary>
/// Runs one multi-source Theta* field per logical exit, then composes the
/// fields by taking each cell's minimum safe any-angle distance.
/// </summary>
public static class DistanceMapCalculator
{
    private static readonly int[] Dc = { -1, 0, 1, -1, 1, -1, 0, 1 };
    private static readonly int[] Dr = { -1, -1, -1, 0, 0, 1, 1, 1 };

    public static DistanceMapResult Compute(WalkabilityGrid grid, IReadOnlyList<(int Col, int Row)> sources)
    {
        var centeredSources = sources
            .Select(source => new DistanceSource(
                source.Col,
                source.Row,
                grid.InBounds(source.Col, source.Row)
                    ? grid.CellCenter(source.Col, source.Row)
                    : default))
            .ToList();
        return Compute(grid, centeredSources);
    }

    public static DistanceMapResult Compute(WalkabilityGrid grid, IReadOnlyList<DistanceSource> sources)
    {
        var sourceGroups = GroupSources(sources);
        var dist = CreateDistanceArray(grid);
        var predecessor = new (int Col, int Row)?[grid.Rows, grid.Cols];
        var roots = new Dictionary<(int Col, int Row), DistanceRoot>();
        var winningGroups = new int[grid.Rows, grid.Cols];
        for (int row = 0; row < grid.Rows; row++)
        {
            for (int col = 0; col < grid.Cols; col++)
            {
                winningGroups[row, col] = -1;
            }
        }

        for (int groupIndex = 0; groupIndex < sourceGroups.Count; groupIndex++)
        {
            var field = ComputeField(grid, sourceGroups[groupIndex]);
            for (int row = 0; row < grid.Rows; row++)
            {
                for (int col = 0; col < grid.Cols; col++)
                {
                    if (field.Distances[row, col] >= dist[row, col])
                    {
                        continue;
                    }

                    dist[row, col] = field.Distances[row, col];
                    predecessor[row, col] = field.Predecessor[row, col];
                    winningGroups[row, col] = groupIndex;
                    var cell = (Col: col, Row: row);
                    if (field.Roots.TryGetValue(cell, out var root))
                    {
                        roots[cell] = root;
                    }
                    else
                    {
                        roots.Remove(cell);
                    }
                }
            }
        }

        (int Col, int Row)? farthest = null;
        double maxDistance = 0;
        int unreachableCellCount = 0;
        for (int row = 0; row < grid.Rows; row++)
        {
            for (int col = 0; col < grid.Cols; col++)
            {
                if (!grid.IsWalkable(col, row))
                {
                    continue;
                }

                if (!double.IsFinite(dist[row, col]))
                {
                    unreachableCellCount++;
                    continue;
                }

                if (farthest is null || dist[row, col] > maxDistance)
                {
                    maxDistance = dist[row, col];
                    farthest = (col, row);
                }
            }
        }

        return new DistanceMapResult(dist, farthest, maxDistance, unreachableCellCount, predecessor)
        {
            Roots = roots,
            SourceGroups = sourceGroups,
            WinningGroupIndexes = winningGroups,
        };
    }

    private static DistanceField ComputeField(
        WalkabilityGrid grid,
        IReadOnlyList<DistanceSource> sources)
    {
        var dist = CreateDistanceArray(grid);
        var visited = new bool[grid.Rows, grid.Cols];
        var predecessor = new (int Col, int Row)?[grid.Rows, grid.Cols];
        var roots = new Dictionary<(int Col, int Row), DistanceRoot>();
        var queue = new PriorityQueue<(int Col, int Row), double>();

        foreach (var source in sources)
        {
            var cell = (source.Col, source.Row);
            if (!grid.IsWalkable(cell.Col, cell.Row))
            {
                continue;
            }

            var root = new DistanceRoot(source.ExitPoint, source.ExitSegment);
            var center = grid.CellCenter(cell.Col, cell.Row);
            if (!HasLineOfSightToRoot(grid, center, root))
            {
                continue;
            }

            double sourceDistance = Distance(center, root.Contact);
            if (sourceDistance < dist[cell.Row, cell.Col])
            {
                dist[cell.Row, cell.Col] = sourceDistance;
                predecessor[cell.Row, cell.Col] = null;
                roots[cell] = root;
                queue.Enqueue(cell, sourceDistance);
            }
        }

        while (queue.TryDequeue(out var current, out _))
        {
            if (visited[current.Row, current.Col])
            {
                continue;
            }
            visited[current.Row, current.Col] = true;
            var currentCenter = grid.CellCenter(current.Col, current.Row);

            for (int direction = 0; direction < 8; direction++)
            {
                var next = (Col: current.Col + Dc[direction], Row: current.Row + Dr[direction]);
                if (!grid.IsWalkable(next.Col, next.Row) ||
                    visited[next.Row, next.Col])
                {
                    continue;
                }

                bool diagonal = Dc[direction] != 0 && Dr[direction] != 0;
                if (diagonal &&
                    (!grid.IsWalkable(current.Col + Dc[direction], current.Row) ||
                     !grid.IsWalkable(current.Col, current.Row + Dr[direction])))
                {
                    continue;
                }

                var nextCenter = grid.CellCenter(next.Col, next.Row);
                double candidate = dist[current.Row, current.Col] + Distance(currentCenter, nextCenter);
                (int Col, int Row)? candidatePredecessor = current;
                DistanceRoot? candidateRoot = null;

                if (predecessor[current.Row, current.Col] is { } parent)
                {
                    var parentCenter = grid.CellCenter(parent.Col, parent.Row);
                    if (grid.HasLineOfSight(parentCenter, nextCenter))
                    {
                        double anyAngleCandidate =
                            dist[parent.Row, parent.Col] + Distance(parentCenter, nextCenter);
                        if (anyAngleCandidate <= candidate)
                        {
                            candidate = anyAngleCandidate;
                            candidatePredecessor = parent;
                        }
                    }
                }
                else if (roots.TryGetValue(current, out var root) &&
                         HasLineOfSightToRoot(grid, nextCenter, root))
                {
                    double anyAngleCandidate = Distance(nextCenter, root.Contact);
                    if (anyAngleCandidate <= candidate)
                    {
                        candidate = anyAngleCandidate;
                        candidatePredecessor = null;
                        candidateRoot = root;
                    }
                }

                if (candidate >= dist[next.Row, next.Col])
                {
                    continue;
                }

                dist[next.Row, next.Col] = candidate;
                predecessor[next.Row, next.Col] = candidatePredecessor;
                if (candidateRoot is { } directRoot)
                {
                    roots[next] = directRoot;
                }
                else
                {
                    roots.Remove(next);
                }
                queue.Enqueue(next, candidate);
            }
        }

        return new DistanceField(dist, predecessor, roots);
    }

    private static double[,] CreateDistanceArray(WalkabilityGrid grid)
    {
        var distances = new double[grid.Rows, grid.Cols];
        for (int row = 0; row < grid.Rows; row++)
        {
            for (int col = 0; col < grid.Cols; col++)
            {
                distances[row, col] = double.PositiveInfinity;
            }
        }
        return distances;
    }

    private static IReadOnlyList<IReadOnlyList<DistanceSource>> GroupSources(
        IReadOnlyList<DistanceSource> sources)
    {
        var groups = new List<List<DistanceSource>>();
        var explicitGroups = new Dictionary<int, int>();
        var segmentGroups = new Dictionary<Segment, int>();

        foreach (var source in sources)
        {
            int groupIndex;
            if (source.ExitGroupId is { } exitGroupId)
            {
                if (!explicitGroups.TryGetValue(exitGroupId, out groupIndex))
                {
                    groupIndex = groups.Count;
                    explicitGroups.Add(exitGroupId, groupIndex);
                    groups.Add([]);
                }
            }
            else if (source.ExitSegment is { } exitSegment)
            {
                if (!segmentGroups.TryGetValue(exitSegment, out groupIndex))
                {
                    groupIndex = groups.Count;
                    segmentGroups.Add(exitSegment, groupIndex);
                    groups.Add([]);
                }
            }
            else
            {
                groupIndex = groups.Count;
                groups.Add([]);
            }

            groups[groupIndex].Add(source);
        }

        return groups
            .Select(group => (IReadOnlyList<DistanceSource>)group.ToArray())
            .ToArray();
    }

    public static WalkingPath? FindPath(
        WalkabilityGrid grid,
        DistanceMapResult result,
        WorldPoint point)
    {
        if (!IsFinite(point) ||
            point.X < grid.Bounds.MinX || point.X > grid.Bounds.MaxX ||
            point.Y < grid.Bounds.MinY || point.Y > grid.Bounds.MaxY)
        {
            return null;
        }

        var cell = grid.WorldToCell(point);
        if (!grid.IsWalkable(cell.Col, cell.Row) ||
            !double.IsFinite(result.Distances[cell.Row, cell.Col]))
        {
            return null;
        }

        var center = grid.CellCenter(cell.Col, cell.Row);
        if (result.WinningGroupIndexes is not { } winningGroups)
        {
            return ReconstructPath(
                grid, point, cell, result.Distances, result.Predecessor, result.Roots);
        }

        if (point == center)
        {
            int groupIndex = winningGroups[cell.Row, cell.Col];
            if (groupIndex < 0 || groupIndex >= result.SourceGroups.Count)
            {
                return null;
            }

            var field = ComputeField(grid, result.SourceGroups[groupIndex]);
            return ReconstructPath(
                grid, point, cell, field.Distances, field.Predecessor, field.Roots);
        }

        WalkingPath? bestPath = null;
        foreach (var sourceGroup in result.SourceGroups)
        {
            var field = ComputeField(grid, sourceGroup);
            var candidate = ReconstructPath(
                grid, point, cell, field.Distances, field.Predecessor, field.Roots);
            if (candidate is not null &&
                (bestPath is null || candidate.Distance < bestPath.Distance))
            {
                bestPath = candidate;
            }
        }
        return bestPath;
    }

    private static WalkingPath? ReconstructPath(
        WalkabilityGrid grid,
        WorldPoint point,
        (int Col, int Row) cell,
        double[,] distances,
        (int Col, int Row)?[,] predecessor,
        IReadOnlyDictionary<(int Col, int Row), DistanceRoot> roots)
    {
        (int Col, int Row)? firstCell = cell;
        DistanceRoot? directRoot = null;
        var center = grid.CellCenter(cell.Col, cell.Row);
        double bestDistance = grid.HasLineOfSight(point, center)
            ? Distance(point, center) + distances[cell.Row, cell.Col]
            : double.PositiveInfinity;

        if (predecessor[cell.Row, cell.Col] is { } parent)
        {
            var parentCenter = grid.CellCenter(parent.Col, parent.Row);
            if (grid.HasLineOfSight(point, parentCenter))
            {
                double candidate =
                    Distance(point, parentCenter) + distances[parent.Row, parent.Col];
                if (candidate <= bestDistance)
                {
                    bestDistance = candidate;
                    firstCell = parent;
                }
            }
        }
        else if (roots.TryGetValue(cell, out var root) &&
                 HasLineOfSightToRoot(grid, point, root))
        {
            double candidate = Distance(point, root.Contact);
            if (candidate <= bestDistance)
            {
                bestDistance = candidate;
                firstCell = null;
                directRoot = root;
            }
        }

        if (!double.IsFinite(bestDistance))
        {
            return null;
        }

        var points = new List<WorldPoint> { point };
        if (directRoot is { } queryRoot)
        {
            AddIfDifferent(points, queryRoot.Contact);
        }
        else if (firstCell is { } pathCell)
        {
            while (true)
            {
                AddIfDifferent(points, grid.CellCenter(pathCell.Col, pathCell.Row));
                if (predecessor[pathCell.Row, pathCell.Col] is { } previous)
                {
                    pathCell = previous;
                    continue;
                }
                if (!roots.TryGetValue(pathCell, out var pathRoot))
                {
                    return null;
                }
                AddIfDifferent(points, pathRoot.Contact);
                break;
            }
        }

        return new WalkingPath(points, PathLength(points));
    }

    public static double? GetDistanceAt(
        WalkabilityGrid grid,
        DistanceMapResult result,
        WorldPoint point) => FindPath(grid, result, point)?.Distance;

    public static IReadOnlyList<WorldPoint>? GetPath(
        WalkabilityGrid grid,
        DistanceMapResult result,
        WorldPoint point) => FindPath(grid, result, point)?.Points;

    private static bool HasLineOfSightToRoot(
        WalkabilityGrid grid,
        WorldPoint point,
        DistanceRoot root) =>
        IsFinite(root.Contact) &&
        (root.ExitSegment is { } exit
            ? grid.HasLineOfSightToExit(point, root.Contact, exit)
            : grid.HasLineOfSight(point, root.Contact));

    private static void AddIfDifferent(List<WorldPoint> points, WorldPoint point)
    {
        if (points.Count == 0 || points[^1] != point)
        {
            points.Add(point);
        }
    }

    private static double PathLength(IReadOnlyList<WorldPoint> path)
    {
        double length = 0;
        for (int i = 1; i < path.Count; i++)
        {
            length += Distance(path[i - 1], path[i]);
        }
        return length;
    }

    private static double Distance(WorldPoint a, WorldPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsFinite(WorldPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);
}
