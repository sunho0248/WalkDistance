namespace WalkDistance.Core;

public readonly record struct DistanceContour(
    double Level,
    WorldPoint Start,
    WorldPoint End,
    bool IsThreshold = false);

public static class DistanceContourGenerator
{
    public static IReadOnlyList<DistanceContour> Generate(
        WalkabilityGrid grid,
        double[,] distances,
        double? threshold = null)
    {
        if (distances.GetLength(0) != grid.Rows || distances.GetLength(1) != grid.Cols)
            throw new ArgumentException("Distance dimensions must match the grid.", nameof(distances));
        if (threshold is { } value && (!double.IsFinite(value) || value < 0))
            throw new ArgumentOutOfRangeException(nameof(threshold));

        var contours = new List<DistanceContour>();
        for (int row = 0; row < grid.Rows - 1; row++)
        for (int col = 0; col < grid.Cols - 1; col++)
        {
            var cells = new[] { (col, row), (col + 1, row), (col + 1, row + 1), (col, row + 1) };
            if (cells.Any(cell => !grid.IsWalkable(cell.Item1, cell.Item2) ||
                                  !double.IsFinite(distances[cell.Item2, cell.Item1])))
                continue;

            var points = cells.Select(cell => grid.CellCenter(cell.Item1, cell.Item2)).ToArray();
            var values = cells.Select(cell => distances[cell.Item2, cell.Item1]).ToArray();
            double firstLevel = Math.Max(1, Math.Ceiling(values.Min()));
            double lastLevel = Math.Floor(values.Max());
            const double largestExactInteger = 9_007_199_254_740_992d;
            if (firstLevel <= lastLevel && firstLevel <= largestExactInteger)
            {
                lastLevel = Math.Min(lastLevel, largestExactInteger);
                if (lastLevel - firstLevel > 1_000_000)
                    throw new ArgumentOutOfRangeException(nameof(distances),
                        "Adjacent cells cross too many contour levels.");
                for (double level = firstLevel; level <= lastLevel; level++)
                {
                    AddTriangle(0, 1, 2, level, false);
                    AddTriangle(0, 2, 3, level, false);
                }
            }
            if (threshold is { } thresholdValue &&
                thresholdValue >= values.Min() && thresholdValue <= values.Max())
            {
                AddTriangle(0, 1, 2, thresholdValue, true);
                AddTriangle(0, 2, 3, thresholdValue, true);
            }

            void AddTriangle(int a, int b, int c, double level, bool isThreshold)
            {
                var crossings = new List<WorldPoint>(3);
                AddCrossing(a, b, level, crossings);
                AddCrossing(b, c, level, crossings);
                AddCrossing(c, a, level, crossings);
                if (crossings.Distinct().Take(2).ToArray() is [var start, var end] && start != end)
                    contours.Add(new DistanceContour(level, start, end, isThreshold));
            }

            void AddCrossing(int a, int b, double level, List<WorldPoint> crossings)
            {
                double first = values[a], second = values[b];
                if (level < Math.Min(first, second) || level > Math.Max(first, second) || first == second)
                    return;
                double t = (level - first) / (second - first);
                crossings.Add(new WorldPoint(
                    points[a].X + (points[b].X - points[a].X) * t,
                    points[a].Y + (points[b].Y - points[a].Y) * t));
            }
        }
        return contours;
    }
}
