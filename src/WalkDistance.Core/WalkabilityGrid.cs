namespace WalkDistance.Core;

/// <summary>
/// Rasterized floor plan: each cell is either walkable (free) or blocked (wall).
/// </summary>
public sealed class WalkabilityGrid
{
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

    /// <summary>
    /// Rasterizes wall segments into a blocked/free grid. Walls are sampled along
    /// their length at half a cell size to mark every cell they pass through.
    /// ponytail: sampling, not exact segment-rectangle intersection; upgrade if
    /// thin walls at shallow angles start leaking through cells.
    /// </summary>
    public static WalkabilityGrid Build(IReadOnlyList<Segment> walls, double cellSize, int marginCells = 2)
    {
        if (cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive.");
        }

        var bounds = Bounds.FromSegments(walls);
        var padded = new Bounds(
            bounds.MinX - marginCells * cellSize,
            bounds.MinY - marginCells * cellSize,
            bounds.MaxX + marginCells * cellSize,
            bounds.MaxY + marginCells * cellSize);

        int cols = Math.Max(1, (int)Math.Ceiling(padded.Width / cellSize) + 1);
        int rows = Math.Max(1, (int)Math.Ceiling(padded.Height / cellSize) + 1);
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
}
