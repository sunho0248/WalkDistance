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
        WorldPoint start = segment.Start;
        WorldPoint end = segment.End;

        if (start.X == end.X && start.Y == end.Y)
        {
            return start;
        }

        // Normalize start/end (and point, via the same scale) BEFORE any
        // subtraction, so dx/dy can never overflow even when the original
        // finite coordinates are individually near +/-double.MaxValue (a
        // plain end.X - start.X would already overflow to Infinity there).
        // The scale is deliberately derived from start/end only, not point:
        // folding point's magnitude in as well would shrink dxN/dyN toward
        // zero whenever the query point is far larger than the segment span,
        // and squaring those tiny values would then underflow to zero and
        // corrupt an otherwise well-conditioned projection.
        double scale = Math.Max(
            Math.Max(Math.Abs(start.X), Math.Abs(start.Y)),
            Math.Max(Math.Abs(end.X), Math.Abs(end.Y)));
        scale = Math.Max(scale, 1.0);

        double startXN = start.X / scale;
        double startYN = start.Y / scale;
        double endXN = end.X / scale;
        double endYN = end.Y / scale;
        double pointXN = point.X / scale;
        double pointYN = point.Y / scale;

        double dxN = endXN - startXN;
        double dyN = endYN - startYN;
        if (dxN == 0 && dyN == 0)
        {
            return start;
        }

        double pxN = pointXN - startXN;
        double pyN = pointYN - startYN;

        double t = ((pxN * dxN) + (pyN * dyN)) / ((dxN * dxN) + (dyN * dyN));
        t = Math.Clamp(t, 0, 1);

        // Stable convex interpolation in the ORIGINAL (unscaled) coordinates:
        // never recomputes end-start, and gives exact endpoint clamps at t=0/1
        // since (1-0)*start+0*end == start and 0*start+(1)*end == end exactly.
        double oneMinusT = 1 - t;
        return new WorldPoint(
            (oneMinusT * start.X) + (t * end.X),
            (oneMinusT * start.Y) + (t * end.Y));
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

        // a and b are both finite, but the subtraction itself can still overflow
        // when they sit near opposite double.MaxValue extremes. That true distance
        // exceeds double's representable range and cannot fit any finite tolerance,
        // so resolve it explicitly as +Infinity rather than letting a stray
        // Infinity/Infinity division below produce NaN.
        if (double.IsPositiveInfinity(dx) || double.IsPositiveInfinity(dy))
        {
            return double.PositiveInfinity;
        }

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
