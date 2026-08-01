namespace WalkDistance.Core;

public sealed class GridSizeLimitExceededException : InvalidOperationException
{
    public long RequestedCellCount { get; }
    public int MaxCellCount { get; }

    public GridSizeLimitExceededException(long requestedCellCount, int maxCellCount)
        : base($"격자 셀 {requestedCellCount:N0}개가 허용 한도 {maxCellCount:N0}개를 초과합니다.")
    {
        RequestedCellCount = requestedCellCount;
        MaxCellCount = maxCellCount;
    }
}

/// <summary>
/// Rasterized floor plan: each cell is either walkable (free) or blocked (wall).
/// </summary>
public sealed class WalkabilityGrid
{
    public const int DefaultMaxCellCount = 4_000_000;

    public double CellSize { get; }
    public Bounds Bounds { get; }
    public int Cols { get; }
    public int Rows { get; }
    private readonly bool[,] _blocked;

    private WalkabilityGrid(double cellSize, Bounds bounds, int cols, int rows, bool[,] blocked)
    {
        CellSize = cellSize;
        Bounds = bounds;
        Cols = cols;
        Rows = rows;
        _blocked = blocked;
    }

    public bool IsBlocked(int col, int row) => _blocked[row, col];

    public bool InBounds(int col, int row) => col >= 0 && col < Cols && row >= 0 && row < Rows;

    public (int Col, int Row) WorldToCell(WorldPoint p)
    {
        int col = (int)((p.X - Bounds.MinX) / CellSize);
        int row = (int)((p.Y - Bounds.MinY) / CellSize);
        return (Math.Clamp(col, 0, Cols - 1), Math.Clamp(row, 0, Rows - 1));
    }

    public WorldPoint CellCenter(int col, int row) => new(
        Bounds.MinX + (col + 0.5) * CellSize,
        Bounds.MinY + (row + 0.5) * CellSize);

    /// <summary>
    /// Conservatively checks every grid cell crossed by a world-space segment.
    /// Exact corner crossings also check both side cells, matching the diagonal
    /// corner-cut rule used by the path search.
    /// </summary>
    public bool HasLineOfSight(WorldPoint start, WorldPoint end)
    {
        if (!IsFinite(start) || !IsFinite(end) ||
            start.X < Bounds.MinX || start.X > Bounds.MaxX ||
            start.Y < Bounds.MinY || start.Y > Bounds.MaxY ||
            end.X < Bounds.MinX || end.X > Bounds.MaxX ||
            end.Y < Bounds.MinY || end.Y > Bounds.MaxY)
        {
            return false;
        }

        double x0 = (start.X - Bounds.MinX) / CellSize;
        double y0 = (start.Y - Bounds.MinY) / CellSize;
        double x1 = (end.X - Bounds.MinX) / CellSize;
        double y1 = (end.Y - Bounds.MinY) / CellSize;
        int col = Math.Clamp((int)Math.Floor(x0), 0, Cols - 1);
        int row = Math.Clamp((int)Math.Floor(y0), 0, Rows - 1);
        int endCol = Math.Clamp((int)Math.Floor(x1), 0, Cols - 1);
        int endRow = Math.Clamp((int)Math.Floor(y1), 0, Rows - 1);
        double dx = x1 - x0;
        double dy = y1 - y0;
        int stepCol = Math.Sign(dx);
        int stepRow = Math.Sign(dy);
        double tDeltaX = stepCol == 0 ? double.PositiveInfinity : 1 / Math.Abs(dx);
        double tDeltaY = stepRow == 0 ? double.PositiveInfinity : 1 / Math.Abs(dy);
        double tMaxX = stepCol switch
        {
            > 0 => (col + 1 - x0) / dx,
            < 0 => (x0 - col) / -dx,
            _ => double.PositiveInfinity,
        };
        double tMaxY = stepRow switch
        {
            > 0 => (row + 1 - y0) / dy,
            < 0 => (y0 - row) / -dy,
            _ => double.PositiveInfinity,
        };

        bool horizontalBoundary = stepRow == 0 && IsGridLine(y0);
        bool verticalBoundary = stepCol == 0 && IsGridLine(x0);

        bool IsClear(int candidateCol, int candidateRow)
        {
            if (!InBounds(candidateCol, candidateRow) || IsBlocked(candidateCol, candidateRow))
            {
                return false;
            }
            if (horizontalBoundary &&
                (!InBounds(candidateCol, candidateRow - 1) || IsBlocked(candidateCol, candidateRow - 1)))
            {
                return false;
            }
            if (verticalBoundary &&
                (!InBounds(candidateCol - 1, candidateRow) || IsBlocked(candidateCol - 1, candidateRow)))
            {
                return false;
            }
            return true;
        }

        if (!IsClear(col, row))
        {
            return false;
        }

        const double cornerTolerance = 1e-12;
        for (int steps = 0; (col != endCol || row != endRow) && steps <= Cols + Rows + 2; steps++)
        {
            if (tMaxX + cornerTolerance < tMaxY)
            {
                col += stepCol;
                tMaxX += tDeltaX;
                if (!IsClear(col, row))
                {
                    return false;
                }
            }
            else if (tMaxY + cornerTolerance < tMaxX)
            {
                row += stepRow;
                tMaxY += tDeltaY;
                if (!IsClear(col, row))
                {
                    return false;
                }
            }
            else
            {
                int nextCol = col + stepCol;
                int nextRow = row + stepRow;
                if (!IsClear(nextCol, row) || !IsClear(col, nextRow) || !IsClear(nextCol, nextRow))
                {
                    return false;
                }
                col = nextCol;
                row = nextRow;
                tMaxX += tDeltaX;
                tMaxY += tDeltaY;
            }
        }

        return col == endCol && row == endRow;
    }

    /// <summary>
    /// Finds the nearest walkable cell to a world point, searching outward ring by ring.
    /// Returns null if no walkable cell exists on the grid.
    /// </summary>
    public (int Col, int Row)? NearestWalkableCell(WorldPoint p)
    {
        var (col, row) = WorldToCell(p);
        if (!IsBlocked(col, row))
        {
            return (col, row);
        }

        int maxRadius = Math.Max(Cols, Rows);
        for (int radius = 1; radius <= maxRadius; radius++)
        {
            for (int dc = -radius; dc <= radius; dc++)
            {
                for (int dr = -radius; dr <= radius; dr++)
                {
                    if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != radius)
                    {
                        continue;
                    }

                    int c = col + dc, r = row + dr;
                    if (InBounds(c, r) && !IsBlocked(c, r))
                    {
                        return (c, r);
                    }
                }
            }
        }

        return null;
    }

    public IReadOnlyList<(int Col, int Row)> WalkableCellsNearSegment(
        Segment segment,
        double proximity)
    {
        if (!double.IsFinite(proximity) || proximity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(proximity));
        }

        if (segment.Start == segment.End)
        {
            var legacyCell = NearestWalkableCell(segment.Start);
            return legacyCell is { } cell ? [cell] : [];
        }

        double dx = segment.End.X - segment.Start.X;
        double dy = segment.End.Y - segment.Start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        int steps = Math.Max(1, (int)Math.Ceiling(length / (CellSize / 2)));
        int radiusCells = (int)Math.Ceiling(proximity / CellSize);
        var cells = new HashSet<(int Col, int Row)>();

        for (int step = 0; step <= steps; step++)
        {
            double t = (double)step / steps;
            var (col, row) = WorldToCell(new WorldPoint(
                segment.Start.X + dx * t,
                segment.Start.Y + dy * t));

            for (int dc = -radiusCells; dc <= radiusCells; dc++)
            {
                for (int dr = -radiusCells; dr <= radiusCells; dr++)
                {
                    int candidateCol = col + dc;
                    int candidateRow = row + dr;
                    if (InBounds(candidateCol, candidateRow) && !IsBlocked(candidateCol, candidateRow))
                    {
                        cells.Add((candidateCol, candidateRow));
                    }
                }
            }
        }

        if (cells.Count == 0 && NearestWalkableCell(segment.Start) is { } fallback)
        {
            cells.Add(fallback);
        }
        return cells.ToList();
    }

    /// <summary>
    /// Associates exit-adjacent source cells with a visible contact point on
    /// the actual exit segment. Unsafe contacts are omitted so a source cannot
    /// reach through a wall merely because it is near the exit.
    /// </summary>
    public IReadOnlyList<DistanceSource> WalkableSourcesNearSegment(
        Segment segment,
        double proximity)
    {
        var candidates = WalkableCellsNearSegment(segment, proximity);
        var sources = new List<DistanceSource>(candidates.Count);
        foreach (var (col, row) in candidates)
        {
            var center = CellCenter(col, row);
            var contact = ClosestPoint(segment, center);
            if (HasLineOfSight(center, contact))
            {
                sources.Add(new DistanceSource(col, row, contact));
            }
        }

        if (sources.Count == 0 && candidates.Count > 0)
        {
            var fallback = candidates.MinBy(cell => SquaredDistance(
                CellCenter(cell.Col, cell.Row),
                ClosestPoint(segment, CellCenter(cell.Col, cell.Row))));
            sources.Add(new DistanceSource(
                fallback.Col,
                fallback.Row,
                CellCenter(fallback.Col, fallback.Row)));
        }
        return sources;
    }

    /// <summary>
    /// Rasterizes wall segments into a blocked/free grid. Walls are sampled along
    /// their length at half a cell size to mark every cell they pass through.
    /// ponytail: sampling, not exact segment-rectangle intersection; upgrade if
    /// thin walls at shallow angles start leaking through cells.
    /// </summary>
    public static WalkabilityGrid Build(
        IReadOnlyList<Segment> walls,
        double cellSize,
        int marginCells = 2,
        int maxCellCount = DefaultMaxCellCount)
    {
        if (!double.IsFinite(cellSize) || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive.");
        }
        if (marginCells < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(marginCells));
        }
        if (maxCellCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCellCount));
        }

        var bounds = Bounds.FromSegments(walls);
        var padded = new Bounds(
            bounds.MinX - marginCells * cellSize,
            bounds.MinY - marginCells * cellSize,
            bounds.MaxX + marginCells * cellSize,
            bounds.MaxY + marginCells * cellSize);

        double requestedCols = Math.Max(1, Math.Ceiling(padded.Width / cellSize) + 1);
        double requestedRows = Math.Max(1, Math.Ceiling(padded.Height / cellSize) + 1);
        if (!double.IsFinite(requestedCols) || !double.IsFinite(requestedRows) ||
            requestedCols > int.MaxValue || requestedRows > int.MaxValue)
        {
            throw new GridSizeLimitExceededException(long.MaxValue, maxCellCount);
        }

        int cols = (int)requestedCols;
        int rows = (int)requestedRows;
        long requestedCellCount = (long)cols * rows;
        if (requestedCellCount > maxCellCount)
        {
            throw new GridSizeLimitExceededException(requestedCellCount, maxCellCount);
        }

        var blocked = new bool[rows, cols];

        var grid = new WalkabilityGrid(cellSize, padded, cols, rows, blocked);

        foreach (var wall in walls)
        {
            grid.RasterizeSegment(wall);
        }

        return grid;
    }

    private void RasterizeSegment(Segment wall)
    {
        double length = Math.Sqrt(
            Math.Pow(wall.End.X - wall.Start.X, 2) + Math.Pow(wall.End.Y - wall.Start.Y, 2));
        int steps = Math.Max(1, (int)Math.Ceiling(length / (CellSize / 2)));

        for (int s = 0; s <= steps; s++)
        {
            double t = (double)s / steps;
            var p = new WorldPoint(
                wall.Start.X + (wall.End.X - wall.Start.X) * t,
                wall.Start.Y + (wall.End.Y - wall.Start.Y) * t);
            var (col, row) = WorldToCell(p);
            _blocked[row, col] = true;
        }
    }

    private static WorldPoint ClosestPoint(Segment segment, WorldPoint point)
    {
        double dx = segment.End.X - segment.Start.X;
        double dy = segment.End.Y - segment.Start.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
        {
            return segment.Start;
        }

        double t = Math.Clamp(
            ((point.X - segment.Start.X) * dx + (point.Y - segment.Start.Y) * dy) / lengthSquared,
            0,
            1);
        return new WorldPoint(segment.Start.X + dx * t, segment.Start.Y + dy * t);
    }

    private static double SquaredDistance(WorldPoint a, WorldPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return dx * dx + dy * dy;
    }

    private static bool IsGridLine(double value) =>
        Math.Abs(value - Math.Round(value)) <= 1e-10;

    private static bool IsFinite(WorldPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);
}
