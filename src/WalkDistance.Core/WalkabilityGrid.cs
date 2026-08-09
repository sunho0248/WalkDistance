namespace WalkDistance.Core;

/// <summary>
/// Rasterized floor plan: each cell is either walkable (free) or blocked (wall).
/// </summary>
public sealed class WalkabilityGrid
{
    public double CellSize { get; }
    public double ClearanceRadius { get; }
    public Bounds Bounds { get; }
    public int Cols { get; }
    public int Rows { get; }
    private readonly bool[,] _blocked;
    private readonly bool[,] _exterior;
    private readonly Segment[] _walls;

    private WalkabilityGrid(
        double cellSize,
        Bounds bounds,
        int cols,
        int rows,
        bool[,] blocked,
        Segment[] walls,
        double clearanceRadius)
    {
        CellSize = cellSize;
        Bounds = bounds;
        Cols = cols;
        Rows = rows;
        _blocked = blocked;
        _exterior = new bool[rows, cols];
        _walls = walls;
        ClearanceRadius = clearanceRadius;
    }

    public bool IsBlocked(int col, int row) => _blocked[row, col];

    public bool IsWalkable(int col, int row) =>
        InBounds(col, row) && !_blocked[row, col] && !_exterior[row, col];

    public bool IsWalkable((int Col, int Row) cell) => IsWalkable(cell.Col, cell.Row);

    public bool Contains(WorldPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X >= Bounds.MinX && point.X < Bounds.MaxX &&
        point.Y >= Bounds.MinY && point.Y < Bounds.MaxY;

    public int InteriorCellCount { get; private set; }

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
    public bool HasLineOfSight(WorldPoint start, WorldPoint end) =>
        HasLineOfSight(start, end, allowBlockedEndCell: false);

    /// <summary>
    /// Checks a route whose final point is on a designated exit. Only the cell
    /// containing that final contact may be blocked, so an exit rasterized as
    /// wall remains reachable without permitting travel through other walls.
    /// </summary>
    public bool HasLineOfSightToExit(WorldPoint start, WorldPoint contact, Segment exit)
    {
        if (!IsFinite(exit.Start) || !IsFinite(exit.End))
        {
            return false;
        }

        double tolerance = GeometryTolerance(exit);
        if (SquaredDistance(contact, ClosestPoint(exit, contact)) > tolerance * tolerance)
        {
            return false;
        }
        if (!HasLineOfSight(start, contact, allowBlockedEndCell: true, exit))
        {
            return false;
        }

        var route = new Segment(start, contact);
        foreach (var wall in _walls)
        {
            if (!WallIntersectionIsAllowed(route, wall, exit, tolerance))
            {
                return false;
            }
        }
        return true;
    }

    private bool HasLineOfSight(
        WorldPoint start,
        WorldPoint end,
        bool allowBlockedEndCell,
        Segment? exit = null)
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
            bool isAllowedEnd = allowBlockedEndCell && InBounds(candidateCol, candidateRow) &&
                                candidateCol == endCol && candidateRow == endRow &&
                                (IsBlocked(candidateCol, candidateRow) || IsWalkable(candidateCol, candidateRow));
            bool IsExitClearance(int col, int row) =>
                exit is { } allowedExit && InBounds(col, row) && !_exterior[row, col] &&
                IsWithinExitClearance(CellCenter(col, row), allowedExit);

            if (!InBounds(candidateCol, candidateRow) ||
                (!isAllowedEnd && !IsWalkable(candidateCol, candidateRow) && !IsExitClearance(candidateCol, candidateRow)))
            {
                return false;
            }
            if (horizontalBoundary &&
                (!InBounds(candidateCol, candidateRow - 1) ||
                 (!isAllowedEnd && !IsWalkable(candidateCol, candidateRow - 1) &&
                  !IsExitClearance(candidateCol, candidateRow - 1))))
            {
                return false;
            }
            if (verticalBoundary &&
                (!InBounds(candidateCol - 1, candidateRow) ||
                 (!isAllowedEnd && !IsWalkable(candidateCol - 1, candidateRow) &&
                  !IsExitClearance(candidateCol - 1, candidateRow))))
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
        if (IsWalkable(col, row))
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
                    if (IsWalkable(c, r))
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
                    if (IsWalkable(candidateCol, candidateRow))
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
        double proximity,
        int? exitGroupId = null)
    {
        bool legacyPointExit = segment.Start == segment.End;
        if (ClearanceRadius > 0 && !legacyPointExit &&
            Math.Sqrt(SquaredDistance(segment.Start, segment.End)) < 2 * ClearanceRadius)
        {
            return [];
        }

        var candidates = WalkableCellsNearSegment(segment, proximity + ClearanceRadius);
        var sources = new List<DistanceSource>(candidates.Count);
        double dx = segment.End.X - segment.Start.X;
        double dy = segment.End.Y - segment.Start.Y;
        double lengthSquared = dx * dx + dy * dy;
        foreach (var (col, row) in candidates)
        {
            var center = CellCenter(col, row);
            var contact = ClosestPoint(segment, center);
            double projection = (center.X - segment.Start.X) * dx +
                                (center.Y - segment.Start.Y) * dy;
            if ((lengthSquared == 0 || projection >= 0 && projection <= lengthSquared) &&
                (ClearanceRadius == 0 || legacyPointExit ||
                 Math.Sqrt(SquaredDistance(contact, segment.Start)) >= ClearanceRadius &&
                 Math.Sqrt(SquaredDistance(contact, segment.End)) >= ClearanceRadius) &&
                HasLineOfSightToExit(center, contact, segment))
            {
                sources.Add(new DistanceSource(
                    col, row,
                    ClearanceRadius > 0 ? center : contact,
                    ClearanceRadius > 0 ? null : segment,
                    exitGroupId));
            }
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
        double clearanceRadius = 0)
    {
        if (!double.IsFinite(cellSize) || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive.");
        }
        if (marginCells < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(marginCells));
        }
        if (!double.IsFinite(clearanceRadius) || clearanceRadius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clearanceRadius));
        }

        var wallSnapshot = walls.ToArray();
        var bounds = Bounds.FromSegments(wallSnapshot);
        var padded = new Bounds(
            bounds.MinX - marginCells * cellSize,
            bounds.MinY - marginCells * cellSize,
            bounds.MaxX + marginCells * cellSize,
            bounds.MaxY + marginCells * cellSize);

        int cols = checked((int)Math.Max(1, Math.Ceiling(padded.Width / cellSize) + 1));
        int rows = checked((int)Math.Max(1, Math.Ceiling(padded.Height / cellSize) + 1));

        var blocked = new bool[rows, cols];
        var gridBounds = new Bounds(
            padded.MinX,
            padded.MinY,
            padded.MinX + cols * cellSize,
            padded.MinY + rows * cellSize);
        var grid = new WalkabilityGrid(
            cellSize, gridBounds, cols, rows, blocked, wallSnapshot, clearanceRadius);

        foreach (var wall in wallSnapshot)
        {
            grid.RasterizeSegment(wall);
        }

        grid.ClassifyExterior();

        return grid;
    }

    private void ClassifyExterior()
    {
        var queue = new Queue<(int Col, int Row)>();
        void Enqueue(int col, int row)
        {
            if (InBounds(col, row) && !_blocked[row, col] && !_exterior[row, col])
            {
                _exterior[row, col] = true;
                queue.Enqueue((col, row));
            }
        }

        for (int col = 0; col < Cols; col++)
        {
            Enqueue(col, 0);
            Enqueue(col, Rows - 1);
        }
        for (int row = 1; row < Rows - 1; row++)
        {
            Enqueue(0, row);
            Enqueue(Cols - 1, row);
        }

        ReadOnlySpan<int> dc = [-1, 1, 0, 0];
        ReadOnlySpan<int> dr = [0, 0, -1, 1];
        while (queue.TryDequeue(out var cell))
        {
            for (int direction = 0; direction < 4; direction++)
            {
                Enqueue(cell.Col + dc[direction], cell.Row + dr[direction]);
            }
        }

        int interior = 0;
        for (int row = 0; row < Rows; row++)
        for (int col = 0; col < Cols; col++)
        {
            if (IsWalkable(col, row))
            {
                interior++;
            }
        }
        InteriorCellCount = interior;
    }

    private void RasterizeSegment(Segment wall)
    {
        if (ClearanceRadius > 0)
        {
            RasterizeInflatedSegment(wall);
            return;
        }

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

    private void RasterizeInflatedSegment(Segment wall)
    {
        double reach = ClearanceRadius + (CellSize * Math.Sqrt(2) / 2);
        double reachSquared = reach * reach;
        int minCol = Math.Max(0, (int)Math.Floor(
            (Math.Min(wall.Start.X, wall.End.X) - reach - Bounds.MinX) / CellSize));
        int maxCol = Math.Min(Cols - 1, (int)Math.Floor(
            (Math.Max(wall.Start.X, wall.End.X) + reach - Bounds.MinX) / CellSize));
        int minRow = Math.Max(0, (int)Math.Floor(
            (Math.Min(wall.Start.Y, wall.End.Y) - reach - Bounds.MinY) / CellSize));
        int maxRow = Math.Min(Rows - 1, (int)Math.Floor(
            (Math.Max(wall.Start.Y, wall.End.Y) + reach - Bounds.MinY) / CellSize));

        for (int row = minRow; row <= maxRow; row++)
        for (int col = minCol; col <= maxCol; col++)
        {
            if (SquaredDistance(CellCenter(col, row), ClosestPoint(wall, CellCenter(col, row))) <= reachSquared)
            {
                _blocked[row, col] = true;
            }
        }
    }

    private bool IsWithinExitClearance(WorldPoint point, Segment exit)
    {
        if (ClearanceRadius == 0)
        {
            return false;
        }

        double reach = ClearanceRadius + (CellSize * Math.Sqrt(2) / 2);
        return SquaredDistance(point, ClosestPoint(exit, point)) <= reach * reach;
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

    private double GeometryTolerance(Segment exit)
    {
        double coordinateScale = Math.Max(1, Math.Abs(Bounds.MinX));
        coordinateScale = Math.Max(coordinateScale, Math.Abs(Bounds.MinY));
        coordinateScale = Math.Max(coordinateScale, Math.Abs(Bounds.MaxX));
        coordinateScale = Math.Max(coordinateScale, Math.Abs(Bounds.MaxY));
        coordinateScale = Math.Max(coordinateScale, Math.Abs(exit.Start.X));
        coordinateScale = Math.Max(coordinateScale, Math.Abs(exit.Start.Y));
        coordinateScale = Math.Max(coordinateScale, Math.Abs(exit.End.X));
        coordinateScale = Math.Max(coordinateScale, Math.Abs(exit.End.Y));
        const double machineEpsilon = 2.2204460492503131e-16;
        return Math.Max(CellSize * 1e-9, coordinateScale * 64 * machineEpsilon);
    }

    private static bool WallIntersectionIsAllowed(
        Segment route,
        Segment wall,
        Segment exit,
        double tolerance)
    {
        if (!TryGetIntersection(route, wall, tolerance, out var first, out var last))
        {
            return true;
        }

        return IsPointOnSegment(first, exit, tolerance) &&
               IsPointOnSegment(last, exit, tolerance);
    }

    private static bool TryGetIntersection(
        Segment first,
        Segment second,
        double tolerance,
        out WorldPoint intersectionStart,
        out WorldPoint intersectionEnd)
    {
        intersectionStart = default;
        intersectionEnd = default;
        double rx = first.End.X - first.Start.X;
        double ry = first.End.Y - first.Start.Y;
        double sx = second.End.X - second.Start.X;
        double sy = second.End.Y - second.Start.Y;
        double firstLength = Math.Sqrt(rx * rx + ry * ry);
        double secondLength = Math.Sqrt(sx * sx + sy * sy);

        if (firstLength <= tolerance)
        {
            if (!IsPointOnSegment(first.Start, second, tolerance))
            {
                return false;
            }
            intersectionStart = intersectionEnd = first.Start;
            return true;
        }
        if (secondLength <= tolerance)
        {
            if (!IsPointOnSegment(second.Start, first, tolerance))
            {
                return false;
            }
            intersectionStart = intersectionEnd = second.Start;
            return true;
        }

        double qpx = second.Start.X - first.Start.X;
        double qpy = second.Start.Y - first.Start.Y;
        double cross = Cross(rx, ry, sx, sy);
        double parallelTolerance = tolerance * Math.Max(firstLength, secondLength);
        if (Math.Abs(cross) > parallelTolerance)
        {
            double t = Cross(qpx, qpy, sx, sy) / cross;
            double u = Cross(qpx, qpy, rx, ry) / cross;
            if (t < -tolerance / firstLength || t > 1 + tolerance / firstLength ||
                u < -tolerance / secondLength || u > 1 + tolerance / secondLength)
            {
                return false;
            }

            double clampedT = Math.Clamp(t, 0, 1);
            intersectionStart = intersectionEnd = new WorldPoint(
                first.Start.X + rx * clampedT,
                first.Start.Y + ry * clampedT);
            return true;
        }

        bool collinear = DistanceFromLine(second.Start, first.Start, rx, ry, firstLength) <= tolerance &&
                         DistanceFromLine(second.End, first.Start, rx, ry, firstLength) <= tolerance;
        if (collinear)
        {
            double lengthSquared = firstLength * firstLength;
            double t0 = (qpx * rx + qpy * ry) / lengthSquared;
            double t1 = t0 + (sx * rx + sy * ry) / lengthSquared;
            double overlapStart = Math.Max(0, Math.Min(t0, t1));
            double overlapEnd = Math.Min(1, Math.Max(t0, t1));
            if (overlapStart > overlapEnd + tolerance / firstLength)
            {
                return false;
            }

            overlapStart = Math.Clamp(overlapStart, 0, 1);
            overlapEnd = Math.Clamp(overlapEnd, 0, 1);
            intersectionStart = new WorldPoint(
                first.Start.X + rx * overlapStart,
                first.Start.Y + ry * overlapStart);
            intersectionEnd = new WorldPoint(
                first.Start.X + rx * overlapEnd,
                first.Start.Y + ry * overlapEnd);
            return true;
        }

        bool found = false;
        AddIntersectionCandidate(second.Start, first, tolerance,
            ref found, ref intersectionStart, ref intersectionEnd);
        AddIntersectionCandidate(second.End, first, tolerance,
            ref found, ref intersectionStart, ref intersectionEnd);
        AddIntersectionCandidate(first.Start, second, tolerance,
            ref found, ref intersectionStart, ref intersectionEnd);
        AddIntersectionCandidate(first.End, second, tolerance,
            ref found, ref intersectionStart, ref intersectionEnd);
        return found;
    }

    private static void AddIntersectionCandidate(
        WorldPoint candidate,
        Segment other,
        double tolerance,
        ref bool found,
        ref WorldPoint first,
        ref WorldPoint last)
    {
        if (!IsPointOnSegment(candidate, other, tolerance))
        {
            return;
        }
        if (!found)
        {
            first = candidate;
            found = true;
        }
        last = candidate;
    }

    private static bool IsPointOnSegment(WorldPoint point, Segment segment, double tolerance) =>
        SquaredDistance(point, ClosestPoint(segment, point)) <= tolerance * tolerance;

    private static double DistanceFromLine(
        WorldPoint point,
        WorldPoint lineStart,
        double dx,
        double dy,
        double lineLength) =>
        Math.Abs(Cross(point.X - lineStart.X, point.Y - lineStart.Y, dx, dy)) / lineLength;

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    private static bool IsGridLine(double value) =>
        Math.Abs(value - Math.Round(value)) <= 1e-10;

    private static bool IsFinite(WorldPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);
}
