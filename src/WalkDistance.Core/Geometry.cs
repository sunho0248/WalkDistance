namespace WalkDistance.Core;

public readonly record struct WorldPoint(double X, double Y);

public readonly record struct Segment(WorldPoint Start, WorldPoint End);

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
