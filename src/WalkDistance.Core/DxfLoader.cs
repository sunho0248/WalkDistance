using System.Globalization;

namespace WalkDistance.Core;

/// <summary>
/// Minimal ASCII DXF reader that extracts wall geometry (LINE, LWPOLYLINE,
/// POLYLINE, CIRCLE, ARC entities) from the ENTITIES section as line segments.
/// Only the subset of the DXF spec needed to rasterize a floor plan is implemented.
/// </summary>
public static class DxfLoader
{
    private const int CircleTessellation = 32;

    public static IReadOnlyList<Segment> LoadWalls(string filePath)
    {
        using var reader = new StreamReader(filePath);
        return LoadWalls(reader);
    }

    public static IReadOnlyList<Segment> LoadWalls(TextReader reader)
    {
        var pairs = ReadPairs(reader);
        var segments = new List<Segment>();

        int entitiesStart = FindEntitiesStart(pairs);
        if (entitiesStart < 0)
        {
            return segments;
        }

        int i = entitiesStart;
        while (i < pairs.Count && !(pairs[i].Code == 0 && pairs[i].Value == "ENDSEC"))
        {
            var (type, attrs) = ReadEntity(pairs, ref i);

            switch (type)
            {
                case "LINE":
                    AddLine(segments, attrs);
                    break;
                case "LWPOLYLINE":
                    AddPolylineFromPoints(segments, attrs);
                    break;
                case "CIRCLE":
                    AddCircle(segments, attrs);
                    break;
                case "ARC":
                    AddArc(segments, attrs);
                    break;
                case "POLYLINE":
                    i = AddLegacyPolyline(segments, attrs, pairs, i);
                    break;
                default:
                    break;
            }
        }

        return segments;
    }

    private static int FindEntitiesStart(IReadOnlyList<(int Code, string Value)> pairs)
    {
        for (int i = 0; i < pairs.Count - 1; i++)
        {
            if (pairs[i].Code == 0 && pairs[i].Value == "SECTION" &&
                pairs[i + 1].Code == 2 && pairs[i + 1].Value == "ENTITIES")
            {
                return i + 2;
            }
        }

        return -1;
    }

    private static (string Type, Dictionary<int, List<string>> Attrs) ReadEntity(
        IReadOnlyList<(int Code, string Value)> pairs, ref int i)
    {
        string type = pairs[i].Value;
        i++;

        var attrs = new Dictionary<int, List<string>>();
        while (i < pairs.Count && pairs[i].Code != 0)
        {
            if (!attrs.TryGetValue(pairs[i].Code, out var list))
            {
                list = new List<string>();
                attrs[pairs[i].Code] = list;
            }
            list.Add(pairs[i].Value);
            i++;
        }

        return (type, attrs);
    }

    private static void AddLine(List<Segment> segments, Dictionary<int, List<string>> attrs)
    {
        if (!TryGetDouble(attrs, 10, 0, out var x1) || !TryGetDouble(attrs, 20, 0, out var y1) ||
            !TryGetDouble(attrs, 11, 0, out var x2) || !TryGetDouble(attrs, 21, 0, out var y2))
        {
            return;
        }

        segments.Add(new Segment(new WorldPoint(x1, y1), new WorldPoint(x2, y2)));
    }

    private static void AddPolylineFromPoints(List<Segment> segments, Dictionary<int, List<string>> attrs)
    {
        if (!attrs.TryGetValue(10, out var xs) || !attrs.TryGetValue(20, out var ys))
        {
            return;
        }

        bool closed = attrs.TryGetValue(70, out var flags) && flags.Count > 0 &&
                      int.TryParse(flags[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var flag) &&
                      (flag & 1) != 0;

        var points = new List<WorldPoint>();
        int count = Math.Min(xs.Count, ys.Count);
        for (int idx = 0; idx < count; idx++)
        {
            points.Add(new WorldPoint(
                double.Parse(xs[idx], CultureInfo.InvariantCulture),
                double.Parse(ys[idx], CultureInfo.InvariantCulture)));
        }

        AddChain(segments, points, closed);
    }

    private static int AddLegacyPolyline(
        List<Segment> segments,
        Dictionary<int, List<string>> polylineAttrs,
        IReadOnlyList<(int Code, string Value)> pairs,
        int i)
    {
        bool closed = polylineAttrs.TryGetValue(70, out var flags) && flags.Count > 0 &&
                      int.TryParse(flags[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var flag) &&
                      (flag & 1) != 0;

        var points = new List<WorldPoint>();
        while (i < pairs.Count && pairs[i].Code == 0 && pairs[i].Value is "VERTEX" or "SEQEND")
        {
            var (type, attrs) = ReadEntity(pairs, ref i);
            if (type == "SEQEND")
            {
                break;
            }

            if (TryGetDouble(attrs, 10, 0, out var x) && TryGetDouble(attrs, 20, 0, out var y))
            {
                points.Add(new WorldPoint(x, y));
            }
        }

        AddChain(segments, points, closed);
        return i;
    }

    private static void AddChain(List<Segment> segments, List<WorldPoint> points, bool closed)
    {
        for (int idx = 0; idx < points.Count - 1; idx++)
        {
            segments.Add(new Segment(points[idx], points[idx + 1]));
        }

        if (closed && points.Count > 2)
        {
            segments.Add(new Segment(points[^1], points[0]));
        }
    }

    private static void AddCircle(List<Segment> segments, Dictionary<int, List<string>> attrs)
    {
        if (!TryGetDouble(attrs, 10, 0, out var cx) || !TryGetDouble(attrs, 20, 0, out var cy) ||
            !TryGetDouble(attrs, 40, 0, out var radius))
        {
            return;
        }

        TessellateArc(segments, cx, cy, radius, 0, 360, CircleTessellation);
    }

    private static void AddArc(List<Segment> segments, Dictionary<int, List<string>> attrs)
    {
        if (!TryGetDouble(attrs, 10, 0, out var cx) || !TryGetDouble(attrs, 20, 0, out var cy) ||
            !TryGetDouble(attrs, 40, 0, out var radius) ||
            !TryGetDouble(attrs, 50, 0, out var startDeg) ||
            !TryGetDouble(attrs, 51, 0, out var endDeg))
        {
            return;
        }

        if (endDeg < startDeg)
        {
            endDeg += 360;
        }

        int steps = Math.Max(2, (int)(CircleTessellation * (endDeg - startDeg) / 360.0));
        TessellateArc(segments, cx, cy, radius, startDeg, endDeg, steps);
    }

    private static void TessellateArc(
        List<Segment> segments, double cx, double cy, double radius,
        double startDeg, double endDeg, int steps)
    {
        var points = new List<WorldPoint>(steps + 1);
        for (int s = 0; s <= steps; s++)
        {
            double angle = (startDeg + (endDeg - startDeg) * s / steps) * Math.PI / 180.0;
            points.Add(new WorldPoint(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
        }

        AddChain(segments, points, closed: false);
    }

    private static bool TryGetDouble(Dictionary<int, List<string>> attrs, int code, int index, out double value)
    {
        value = 0;
        if (!attrs.TryGetValue(code, out var list) || index >= list.Count)
        {
            return false;
        }

        return double.TryParse(list[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static List<(int Code, string Value)> ReadPairs(TextReader reader)
    {
        var pairs = new List<(int Code, string Value)>();
        while (true)
        {
            string? codeLine = reader.ReadLine();
            if (codeLine is null)
            {
                break;
            }

            string? valueLine = reader.ReadLine();
            if (valueLine is null)
            {
                break;
            }

            if (!int.TryParse(codeLine.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var code))
            {
                continue;
            }

            pairs.Add((code, valueLine.TrimEnd('\r')));
        }

        return pairs;
    }
}
