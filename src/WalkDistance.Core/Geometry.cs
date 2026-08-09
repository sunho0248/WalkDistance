namespace WalkDistance.Core;

public readonly record struct WorldPoint(double X, double Y);

public readonly record struct Segment(WorldPoint Start, WorldPoint End);

/// <summary>
/// Person-sized clearance used when rasterizing a walking plan. The torso is
/// conservatively treated as a circle, while shoulder width supplies the
/// minimum side-to-side radius.
/// </summary>
public sealed record BodyProfile(double ShoulderWidth, double TorsoCircumference)
{
    public static BodyProfile KoreanAdult { get; } = new(0.40, 0.95);

    public double ClearanceRadius => Math.Max(ShoulderWidth / 2, TorsoCircumference / (2 * Math.PI));

    public bool IsValid => double.IsFinite(ShoulderWidth) && ShoulderWidth > 0 &&
                           double.IsFinite(TorsoCircumference) && TorsoCircumference > 0;
}

public readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public static Bounds FromSegments(IReadOnlyList<Segment> segments)
    {
        if (segments.Count == 0)
        {
            return new Bounds(0, 0, 0, 0);
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var s in segments)
        {
            minX = Math.Min(minX, Math.Min(s.Start.X, s.End.X));
            minY = Math.Min(minY, Math.Min(s.Start.Y, s.End.Y));
            maxX = Math.Max(maxX, Math.Max(s.Start.X, s.End.X));
            maxY = Math.Max(maxY, Math.Max(s.Start.Y, s.End.Y));
        }

        return new Bounds(minX, minY, maxX, maxY);
    }
}
