namespace WalkDistance.Core;

public readonly record struct DistanceContour(
    double Level,
    WorldPoint Start,
    WorldPoint End,
    bool IsThreshold = false);

public static class DistanceContourGenerator
{
    private const double LargestExactInteger = 9_007_199_254_740_992d;
    private const double MaxLevelsPerCell = 1_000_000;

    public static IReadOnlyList<DistanceContour> Generate(
        WalkabilityGrid grid,
        double[,] distances,
        double? threshold = null)
    {
        if (threshold is { } invalid && (!double.IsFinite(invalid) || invalid < 0))
            throw new ArgumentOutOfRangeException(nameof(threshold));
        var contours = GenerateNormal(grid, distances);
        if (threshold is { } value)
            contours.AddRange(GenerateThresholdCore(grid, distances, value));
        return contours;
    }

    public static IReadOnlyList<DistanceContour> GenerateThreshold(
        WalkabilityGrid grid,
        double[,] distances,
        double threshold) => GenerateThresholdCore(grid, distances, threshold);

    private static List<DistanceContour> GenerateNormal(WalkabilityGrid grid, double[,] distances)
    {
        Validate(grid, distances);
        var contours = new List<DistanceContour>();
        VisitQuads(grid, distances, (a, av, b, bv, c, cv, d, dv) =>
        {
            double minimum = Math.Min(Math.Min(av, bv), Math.Min(cv, dv));
            double maximum = Math.Max(Math.Max(av, bv), Math.Max(cv, dv));
            double first = Math.Max(1, Math.Ceiling(minimum));
            double last = Math.Min(Math.Floor(maximum), LargestExactInteger);
            if (first > last || first > LargestExactInteger)
                return;
            if (last - first > MaxLevelsPerCell)
                throw new ArgumentOutOfRangeException(nameof(distances),
                    "Adjacent cells cross too many contour levels.");
            for (double level = first; level <= last; level++)
            {
                AddTriangle(contours, level, false, a, av, b, bv, c, cv);
                AddTriangle(contours, level, false, a, av, c, cv, d, dv);
            }
        });
        return contours;
    }

    private static List<DistanceContour> GenerateThresholdCore(
        WalkabilityGrid grid,
        double[,] distances,
        double threshold)
    {
        Validate(grid, distances);
        if (!double.IsFinite(threshold) || threshold < 0)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        var contours = new List<DistanceContour>();
        VisitQuads(grid, distances, (a, av, b, bv, c, cv, d, dv) =>
        {
            double minimum = Math.Min(Math.Min(av, bv), Math.Min(cv, dv));
            double maximum = Math.Max(Math.Max(av, bv), Math.Max(cv, dv));
            if (threshold < minimum || threshold > maximum)
                return;
            AddTriangle(contours, threshold, true, a, av, b, bv, c, cv);
            AddTriangle(contours, threshold, true, a, av, c, cv, d, dv);
        });
        return contours;
    }

    private delegate void QuadVisitor(
        WorldPoint a, double av, WorldPoint b, double bv,
        WorldPoint c, double cv, WorldPoint d, double dv);

    private static void VisitQuads(WalkabilityGrid grid, double[,] distances, QuadVisitor visit)
    {
        for (int row = 0; row < grid.Rows - 1; row++)
        for (int col = 0; col < grid.Cols - 1; col++)
        {
            if (!grid.IsWalkable(col, row) || !grid.IsWalkable(col + 1, row) ||
                !grid.IsWalkable(col + 1, row + 1) || !grid.IsWalkable(col, row + 1))
                continue;
            double av = distances[row, col];
            double bv = distances[row, col + 1];
            double cv = distances[row + 1, col + 1];
            double dv = distances[row + 1, col];
            if (!double.IsFinite(av) || !double.IsFinite(bv) ||
                !double.IsFinite(cv) || !double.IsFinite(dv))
                continue;
            visit(
                grid.CellCenter(col, row), av,
                grid.CellCenter(col + 1, row), bv,
                grid.CellCenter(col + 1, row + 1), cv,
                grid.CellCenter(col, row + 1), dv);
        }
    }

    private static void AddTriangle(
        List<DistanceContour> output,
        double level,
        bool isThreshold,
        WorldPoint a, double av,
        WorldPoint b, double bv,
        WorldPoint c, double cv)
    {
        int count = 0;
        WorldPoint first = default, second = default;
        AddCrossing(a, av, b, bv, level, ref count, ref first, ref second);
        AddCrossing(b, bv, c, cv, level, ref count, ref first, ref second);
        AddCrossing(c, cv, a, av, level, ref count, ref first, ref second);
        if (count == 2 && first != second)
            output.Add(new DistanceContour(level, first, second, isThreshold));
    }

    private static void AddCrossing(
        WorldPoint a, double av,
        WorldPoint b, double bv,
        double level,
        ref int count,
        ref WorldPoint first,
        ref WorldPoint second)
    {
        if (av == bv || level < Math.Min(av, bv) || level > Math.Max(av, bv))
            return;
        double t = (level - av) / (bv - av);
        var point = new WorldPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        if (count > 0 && point == first || count > 1 && point == second)
            return;
        if (count++ == 0)
            first = point;
        else if (count == 2)
            second = point;
    }

    private static void Validate(WalkabilityGrid grid, double[,] distances)
    {
        if (distances.GetLength(0) != grid.Rows || distances.GetLength(1) != grid.Cols)
            throw new ArgumentException("Distance dimensions must match the grid.", nameof(distances));
    }
}
