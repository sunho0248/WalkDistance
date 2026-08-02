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
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= double.Epsilon)
        {
            return segment.Start;
        }

        double t = (((point.X - segment.Start.X) * dx) + ((point.Y - segment.Start.Y) * dy)) / lengthSquared;
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
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
