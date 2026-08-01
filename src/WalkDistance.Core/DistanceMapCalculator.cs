namespace WalkDistance.Core;

public sealed record DistanceMapResult(
    double[,] Distances,
    (int Col, int Row)? FarthestCell,
    double MaxDistance,
    int UnreachableCellCount,
    (int Col, int Row)?[,] Predecessor)
{
    internal IReadOnlyDictionary<(int Col, int Row), WorldPoint> SourceContacts { get; init; } =
        new Dictionary<(int Col, int Row), WorldPoint>();
}

public readonly record struct DistanceSource(int Col, int Row, WorldPoint ExitPoint);

/// <summary>
/// Multi-source Dijkstra over a walkability grid: computes, for every walkable
/// cell, the shortest walking distance to the nearest exit cell.
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
        var dist = new double[grid.Rows, grid.Cols];
        for (int r = 0; r < grid.Rows; r++)
        {
            for (int c = 0; c < grid.Cols; c++)
            {
                dist[r, c] = double.PositiveInfinity;
            }
        }

        var visited = new bool[grid.Rows, grid.Cols];
        var predecessor = new (int Col, int Row)?[grid.Rows, grid.Cols];
        var sourceContacts = new Dictionary<(int Col, int Row), WorldPoint>();
        var queue = new PriorityQueue<(int Col, int Row), double>();

        foreach (var source in sources)
        {
            var (col, row) = (source.Col, source.Row);
            if (!grid.InBounds(col, row) || grid.IsBlocked(col, row))
            {
                continue;
            }

            var center = grid.CellCenter(col, row);
            var contact = IsFinite(source.ExitPoint) && grid.HasLineOfSight(center, source.ExitPoint)
                ? source.ExitPoint
                : center;
            double sourceDistance = Distance(center, contact);
            if (dist[row, col] > sourceDistance)
            {
                dist[row, col] = sourceDistance;
                sourceContacts[(col, row)] = contact;
                queue.Enqueue((col, row), sourceDistance);
            }
        }

        double diagonalStep = grid.CellSize * Math.Sqrt(2);

        while (queue.TryDequeue(out var current, out var currentDist))
        {
            var (col, row) = current;
            if (visited[row, col])
            {
                continue;
            }
            visited[row, col] = true;

            for (int k = 0; k < 8; k++)
            {
                int nc = col + Dc[k];
                int nr = row + Dr[k];
                if (!grid.InBounds(nc, nr) || grid.IsBlocked(nc, nr))
                {
                    continue;
                }

                bool isDiagonal = Dc[k] != 0 && Dr[k] != 0;
                if (isDiagonal && (grid.IsBlocked(col + Dc[k], row) || grid.IsBlocked(col, row + Dr[k])))
                {
                    // Disallow cutting across a wall corner diagonally.
                    continue;
                }

                double step = isDiagonal ? diagonalStep : grid.CellSize;
                double candidate = currentDist + step;
                if (candidate < dist[nr, nc])
                {
                    dist[nr, nc] = candidate;
                    predecessor[nr, nc] = (col, row);
                    queue.Enqueue((nc, nr), candidate);
                }
            }
        }

        (int Col, int Row)? farthest = null;
        double maxDistance = 0;
        int unreachableCellCount = 0;
        for (int r = 0; r < grid.Rows; r++)
        {
            for (int c = 0; c < grid.Cols; c++)
            {
                if (grid.IsBlocked(c, r))
                {
                    continue;
                }

                if (double.IsPositiveInfinity(dist[r, c]))
                {
                    unreachableCellCount++;
                    continue;
                }

                if (farthest is null || dist[r, c] > maxDistance)
                {
                    maxDistance = dist[r, c];
                    farthest = (c, r);
                }
            }
        }

        var result = new DistanceMapResult(dist, farthest, maxDistance, unreachableCellCount, predecessor)
        {
            SourceContacts = sourceContacts,
        };
        if (farthest is { } farthestCell &&
            GetPath(grid, result, grid.CellCenter(farthestCell.Col, farthestCell.Row)) is { } farthestPath)
        {
            result = result with { MaxDistance = PathLength(farthestPath) };
        }
        return result;
    }

    public static double? GetDistanceAt(
        WalkabilityGrid grid,
        DistanceMapResult result,
        WorldPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < grid.Bounds.MinX || point.X > grid.Bounds.MaxX ||
            point.Y < grid.Bounds.MinY || point.Y > grid.Bounds.MaxY)
        {
            return null;
        }

        var path = GetPath(grid, result, point);
        return path is null ? null : PathLength(path);
    }

    public static IReadOnlyList<WorldPoint>? GetPath(
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
        if (grid.IsBlocked(cell.Col, cell.Row) ||
            !double.IsFinite(result.Distances[cell.Row, cell.Col]))
        {
            return null;
        }

        var rawPath = new List<WorldPoint> { point };
        AddIfDifferent(rawPath, grid.CellCenter(cell.Col, cell.Row));
        while (true)
        {
            if (result.Predecessor[cell.Row, cell.Col] is not { } previous)
            {
                if (result.SourceContacts.TryGetValue(cell, out var contact))
                {
                    AddIfDifferent(rawPath, contact);
                }
                return Simplify(grid, rawPath);
            }
            cell = previous;
            AddIfDifferent(rawPath, grid.CellCenter(cell.Col, cell.Row));
        }
    }

    private static IReadOnlyList<WorldPoint>? Simplify(
        WalkabilityGrid grid,
        IReadOnlyList<WorldPoint> rawPath)
    {
        if (rawPath.Count < 2)
        {
            return rawPath;
        }

        var path = new List<WorldPoint> { rawPath[0] };
        int anchor = 0;
        while (anchor < rawPath.Count - 1)
        {
            int next = rawPath.Count - 1;
            while (next > anchor && !grid.HasLineOfSight(rawPath[anchor], rawPath[next]))
            {
                next--;
            }
            if (next == anchor)
            {
                return null;
            }
            path.Add(rawPath[next]);
            anchor = next;
        }
        return path;
    }

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
