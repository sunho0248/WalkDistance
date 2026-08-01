namespace WalkDistance.Core;

public sealed record DistanceMapResult(
    double[,] Distances,
    (int Col, int Row)? FarthestCell,
    double MaxDistance,
    int UnreachableCellCount);

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
        var dist = new double[grid.Rows, grid.Cols];
        for (int r = 0; r < grid.Rows; r++)
        {
            for (int c = 0; c < grid.Cols; c++)
            {
                dist[r, c] = double.PositiveInfinity;
            }
        }

        var visited = new bool[grid.Rows, grid.Cols];
        var queue = new PriorityQueue<(int Col, int Row), double>();

        foreach (var (col, row) in sources)
        {
            if (!grid.InBounds(col, row) || grid.IsBlocked(col, row))
            {
                continue;
            }

            if (dist[row, col] > 0)
            {
                dist[row, col] = 0;
                queue.Enqueue((col, row), 0);
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

        return new DistanceMapResult(dist, farthest, maxDistance, unreachableCellCount);
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

        var (col, row) = grid.WorldToCell(point);
        double distance = result.Distances[row, col];
        return grid.IsBlocked(col, row) || !double.IsFinite(distance) ? null : distance;
    }
}
