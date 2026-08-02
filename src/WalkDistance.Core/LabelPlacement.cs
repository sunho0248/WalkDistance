namespace WalkDistance.Core;

public static class LabelPlacement
{
    public static WorldPoint HalfLengthPoint(IReadOnlyList<WorldPoint> points)
    {
        if (points.Count == 0)
            throw new ArgumentException("At least one point is required.", nameof(points));

        double totalLength = 0;
        for (int i = 1; i < points.Count; i++)
            totalLength += SegmentLength(points[i - 1], points[i]);

        if (totalLength == 0)
            return points[0];

        double traversed = 0;
        double target = totalLength / 2;
        for (int i = 1; i < points.Count; i++)
        {
            double segmentLength = SegmentLength(points[i - 1], points[i]);
            if (segmentLength == 0)
                continue;
            if (traversed + segmentLength >= target)
            {
                double ratio = (target - traversed) / segmentLength;
                return new WorldPoint(
                    points[i - 1].X + ((points[i].X - points[i - 1].X) * ratio),
                    points[i - 1].Y + ((points[i].Y - points[i - 1].Y) * ratio));
            }
            traversed += segmentLength;
        }

        return points[^1];
    }

    public static WorldPoint ClampToCanvas(
        WorldPoint desired,
        double labelWidth,
        double labelHeight,
        double canvasWidth,
        double canvasHeight,
        double padding)
    {
        if (!double.IsFinite(desired.X) || !double.IsFinite(desired.Y) ||
            !double.IsFinite(labelWidth) || labelWidth < 0 ||
            !double.IsFinite(labelHeight) || labelHeight < 0 ||
            !double.IsFinite(canvasWidth) || canvasWidth < 0 ||
            !double.IsFinite(canvasHeight) || canvasHeight < 0 ||
            !double.IsFinite(padding) || padding < 0)
            throw new ArgumentOutOfRangeException(nameof(desired));

        double maxX = Math.Max(padding, canvasWidth - labelWidth - padding);
        double maxY = Math.Max(padding, canvasHeight - labelHeight - padding);
        return new WorldPoint(
            Math.Clamp(desired.X, padding, maxX),
            Math.Clamp(desired.Y, padding, maxY));
    }

    private static double SegmentLength(WorldPoint start, WorldPoint end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
