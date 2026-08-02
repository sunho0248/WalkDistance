namespace WalkDistance.Core;

/// <summary>
/// Nearest-point/projection helpers used to snap exit endpoints onto DXF wall/shape geometry.
/// </summary>
public static class GeometrySnap
{
    public static WorldPoint ProjectOntoSegment(WorldPoint point, Segment segment)
    {
        ValidatePoint(point, nameof(point));
        ValidatePoint(segment.Start, nameof(segment));
        ValidatePoint(segment.End, nameof(segment));

        return Project(point, segment);
    }

    public static WorldPoint? TrySnapToNearest(WorldPoint point, IReadOnlyList<Segment> segments, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ValidatePoint(point, nameof(point));
        ValidateTolerance(tolerance);

        WorldPoint? best = null;
        double bestDistance = double.PositiveInfinity;

        foreach (var segment in segments)
        {
            ValidatePoint(segment.Start, nameof(segments));
            ValidatePoint(segment.End, nameof(segments));

            var projected = Project(point, segment);
            double distance = Distance(point, projected);
            if (distance <= tolerance && distance < bestDistance)
            {
                bestDistance = distance;
                best = projected;
            }
        }

        return best;
    }

    private static WorldPoint Project(WorldPoint point, Segment segment)
    {
        double dx = segment.End.X - segment.Start.X;
        double dy = segment.End.Y - segment.Start.Y;
        if (dx == 0 && dy == 0)
        {
            return segment.Start;
        }

        double px = point.X - segment.Start.X;
        double py = point.Y - segment.Start.Y;

        // Scale by the largest component before combining products, so that
        // squaring/dot-product terms stay in range even when dx/dy/px/py are
        // individually finite but too large to square without overflowing.
        double scale = Math.Max(Math.Abs(dx), Math.Abs(dy));
        double dxN = dx / scale;
        double dyN = dy / scale;
        double pxN = px / scale;
        double pyN = py / scale;

        double t = ((pxN * dxN) + (pyN * dyN)) / ((dxN * dxN) + (dyN * dyN));
        t = Math.Clamp(t, 0, 1);
        return new WorldPoint(segment.Start.X + (t * dx), segment.Start.Y + (t * dy));
    }

    private static void ValidateTolerance(double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "허용 오차는 0 이상의 유한한 값이어야 합니다.");
        }
    }

    private static void ValidatePoint(WorldPoint point, string paramName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new ArgumentException("좌표 값이 유한하지 않습니다.", paramName);
        }
    }

    private static double Distance(WorldPoint a, WorldPoint b)
    {
        // Scaled hypot: avoids overflow in dx*dx/dy*dy when either component
        // is individually finite but too large to square directly.
        double dx = Math.Abs(a.X - b.X);
        double dy = Math.Abs(a.Y - b.Y);
        double max = Math.Max(dx, dy);
        if (max == 0)
        {
            return 0;
        }

        double min = Math.Min(dx, dy);
        double ratio = min / max;
        return max * Math.Sqrt(1 + (ratio * ratio));
    }
}
