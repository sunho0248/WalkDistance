using System.Globalization;

namespace WalkDistance.Core;

public sealed record DxfDocument(
    IReadOnlyList<Segment> Walls,
    int? InsUnits,
    double MetersPerDrawingUnit,
    IReadOnlyList<IReadOnlyList<WorldPoint>> ExitPaths,
    DxfLoadDiagnostics Diagnostics);

/// <summary>
/// Additive, read-only input-quality counters gathered during <see cref="DxfLoader.Load(string, double?)"/>.
/// Never changes the parsed <see cref="DxfDocument.Walls"/>/<see cref="DxfDocument.ExitPaths"/> geometry —
/// unsupported/degenerate entities are still counted-and-kept, matching pre-existing load behavior.
/// </summary>
public sealed record DxfLoadDiagnostics(
    IReadOnlyDictionary<string, int> UnsupportedEntityCounts,
    int ZeroLengthSegmentCount,
    int TessellatedCurveCount,
    int TessellationSegmentCount,
    int DuplicateConsecutiveVertexCount)
{
    public static readonly DxfLoadDiagnostics Empty = new(
        new Dictionary<string, int>(), 0, 0, 0, 0);

    /// <summary>
    /// True when the load produced anything worth a human's attention. Tessellation counts are
    /// informational (expected whenever a drawing has arcs/circles), so they never drive this flag.
    /// </summary>
    public bool HasIssues =>
        UnsupportedEntityCounts.Count > 0 || ZeroLengthSegmentCount > 0 || DuplicateConsecutiveVertexCount > 0;
}

/// <summary>
/// Mutable accumulator for <see cref="DxfLoadDiagnostics"/>, threaded through the parse loop.
/// </summary>
internal sealed class DxfLoadDiagnosticsBuilder
{
    private readonly Dictionary<string, int> _unsupportedEntities = new(StringComparer.OrdinalIgnoreCase);

    public int ZeroLengthSegmentCount { get; private set; }
    public int TessellatedCurveCount { get; private set; }
    public int TessellationSegmentCount { get; private set; }
    public int DuplicateConsecutiveVertexCount { get; private set; }

    public void RecordUnsupportedEntity(string type) =>
        _unsupportedEntities[type] = _unsupportedEntities.GetValueOrDefault(type) + 1;

    public void RecordZeroLengthSegment() => ZeroLengthSegmentCount++;

    public void RecordTessellatedCurve(int segmentCount)
    {
        TessellatedCurveCount++;
        TessellationSegmentCount += segmentCount;
    }

    public void RecordDuplicateConsecutiveVertex() => DuplicateConsecutiveVertexCount++;

    public DxfLoadDiagnostics Build() => new(
        new Dictionary<string, int>(_unsupportedEntities),
        ZeroLengthSegmentCount,
        TessellatedCurveCount,
        TessellationSegmentCount,
        DuplicateConsecutiveVertexCount);
}

public sealed class DxfUnitRequiredException : Exception
{
    public DxfUnitRequiredException()
        : base("DXF에 $INSUNITS 단위가 없습니다. 도면 단위를 직접 선택해야 합니다.")
    {
    }
}

/// <summary>
/// Minimal ASCII DXF reader for wall entities. Geometry is returned in metres.
/// </summary>
public static class DxfLoader
{
    private const int CircleTessellation = 32;

    public static DxfDocument Load(string filePath, double? unitlessMetersPerUnit = null)
    {
        using var reader = new StreamReader(filePath);
        return Load(reader, unitlessMetersPerUnit);
    }

    public static DxfDocument Load(TextReader reader, double? unitlessMetersPerUnit = null)
    {
        var pairs = ReadPairs(reader);
        var segments = new List<Segment>();
        var exitPaths = new List<IReadOnlyList<WorldPoint>>();
        var diagnostics = new DxfLoadDiagnosticsBuilder();
        int entitiesStart = FindEntitiesStart(pairs);
        if (entitiesStart < 0)
        {
            throw new InvalidDataException("DXF ENTITIES 섹션을 찾을 수 없습니다.");
        }

        int i = entitiesStart;
        while (i < pairs.Count && !IsPair(pairs[i], 0, "ENDSEC"))
        {
            if (pairs[i].Code != 0)
            {
                throw new InvalidDataException($"잘못된 DXF 엔티티 시작 위치입니다: group code {pairs[i].Code}");
            }

            var (type, attrs) = ReadEntity(pairs, ref i);
            int firstSegment = segments.Count;
            switch (type.ToUpperInvariant())
            {
                case "LINE":
                    AddLine(segments, attrs, diagnostics);
                    break;
                case "LWPOLYLINE":
                    AddPolylineFromPoints(segments, attrs, diagnostics);
                    break;
                case "POLYLINE":
                    i = AddLegacyPolyline(segments, attrs, pairs, i, diagnostics);
                    break;
                case "CIRCLE":
                    AddCircle(segments, attrs, diagnostics);
                    break;
                case "ARC":
                    AddArc(segments, attrs, diagnostics);
                    break;
                default:
                    diagnostics.RecordUnsupportedEntity(type);
                    break;
            }

            if (IsExitLayer(attrs) && segments.Count > firstSegment)
            {
                var path = new List<WorldPoint> { segments[firstSegment].Start };
                path.AddRange(segments.Skip(firstSegment).Select(segment => segment.End));
                exitPaths.Add(path);
            }
        }

        if (i >= pairs.Count || !IsPair(pairs[i], 0, "ENDSEC"))
        {
            throw new InvalidDataException("DXF ENTITIES 섹션이 정상적으로 끝나지 않았습니다.");
        }

        if (segments.Count == 0)
        {
            throw new InvalidDataException("지원되는 벽 엔티티(LINE/LWPOLYLINE/POLYLINE/CIRCLE/ARC)가 없습니다.");
        }

        int? insUnits = FindInsUnits(pairs);
        double scale = insUnits is null or 0
            ? ValidateUnitlessScale(unitlessMetersPerUnit)
            : MetersPerUnit(insUnits.Value);

        var scaled = segments.Select(segment => new Segment(
            Scale(segment.Start, scale),
            Scale(segment.End, scale))).ToList();
        if (scaled.Any(segment => !IsFinite(segment.Start) || !IsFinite(segment.End)))
        {
            throw new InvalidDataException("단위 변환 후 DXF 좌표가 유효 범위를 벗어났습니다.");
        }
        var scaledExitPaths = exitPaths
            .Select(path => (IReadOnlyList<WorldPoint>)path.Select(point => Scale(point, scale)).ToList())
            .ToList();
        return new DxfDocument(scaled, insUnits, scale, scaledExitPaths, diagnostics.Build());
    }

    public static IReadOnlyList<Segment> LoadWalls(string filePath, double? unitlessMetersPerUnit = null) =>
        Load(filePath, unitlessMetersPerUnit).Walls;

    public static IReadOnlyList<Segment> LoadWalls(TextReader reader, double? unitlessMetersPerUnit = null) =>
        Load(reader, unitlessMetersPerUnit).Walls;

    private static WorldPoint Scale(WorldPoint point, double scale) =>
        new(point.X * scale, point.Y * scale);

    private static bool IsFinite(WorldPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static double ValidateUnitlessScale(double? scale)
    {
        if (scale is null)
        {
            throw new DxfUnitRequiredException();
        }

        if (!double.IsFinite(scale.Value) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "단위 배율은 0보다 큰 유한한 값이어야 합니다.");
        }

        return scale.Value;
    }

    private static double MetersPerUnit(int code) => code switch
    {
        1 => 0.0254,                 // inches
        2 => 0.3048,                 // feet
        3 => 1609.344,               // miles
        4 => 0.001,                  // millimetres
        5 => 0.01,                   // centimetres
        6 => 1,                      // metres
        7 => 1000,                   // kilometres
        8 => 0.0000000254,           // microinches
        9 => 0.0000254,              // mils
        10 => 0.9144,                // yards
        11 => 1e-10,                 // angstroms
        12 => 1e-9,                  // nanometres
        13 => 1e-6,                  // microns
        14 => 0.1,                   // decimetres
        15 => 10,                    // decametres
        16 => 100,                   // hectometres
        17 => 1e9,                   // gigametres
        18 => 149_597_870_700,       // astronomical units
        19 => 9.4607304725808e15,    // light years
        20 => 3.0856775814913673e16, // parsecs
        21 => 1200d / 3937,          // US survey feet
        22 => 100d / 3937,           // US survey inches
        23 => 3600d / 3937,          // US survey yards
        24 => 6_336_000d / 3937,     // US survey miles
        _ => throw new InvalidDataException($"지원하지 않는 DXF $INSUNITS 값입니다: {code}"),
    };

    private static int? FindInsUnits(IReadOnlyList<(int Code, string Value)> pairs)
    {
        for (int i = 0; i < pairs.Count - 1; i++)
        {
            if (!IsPair(pairs[i], 9, "$INSUNITS"))
            {
                continue;
            }

            if (pairs[i + 1].Code != 70 ||
                !int.TryParse(pairs[i + 1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidDataException("DXF $INSUNITS 값이 올바르지 않습니다.");
            }

            return value;
        }

        return null;
    }

    private static int FindEntitiesStart(IReadOnlyList<(int Code, string Value)> pairs)
    {
        for (int i = 0; i < pairs.Count - 1; i++)
        {
            if (IsPair(pairs[i], 0, "SECTION") && IsPair(pairs[i + 1], 2, "ENTITIES"))
            {
                return i + 2;
            }
        }

        return -1;
    }

    private static bool IsPair((int Code, string Value) pair, int code, string value) =>
        pair.Code == code && string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase);

    private static bool IsExitLayer(Dictionary<int, List<string>> attrs) =>
        attrs.TryGetValue(8, out var layers) && layers.Count > 0 &&
        string.Equals(layers[0], "WD_Exit", StringComparison.OrdinalIgnoreCase);

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
                list = [];
                attrs[pairs[i].Code] = list;
            }
            list.Add(pairs[i].Value);
            i++;
        }

        return (type, attrs);
    }

    private static void AddLine(List<Segment> segments, Dictionary<int, List<string>> attrs, DxfLoadDiagnosticsBuilder diagnostics)
    {
        if (!TryGetDouble(attrs, 10, 0, out var x1) || !TryGetDouble(attrs, 20, 0, out var y1) ||
            !TryGetDouble(attrs, 11, 0, out var x2) || !TryGetDouble(attrs, 21, 0, out var y2))
        {
            return;
        }

        var start = OcsToWcs(attrs, x1, y1, GetDoubleOrDefault(attrs, 30, 0));
        var end = OcsToWcs(attrs, x2, y2, GetDoubleOrDefault(attrs, 31, 0));
        if (start == end)
        {
            diagnostics.RecordZeroLengthSegment();
        }

        segments.Add(new Segment(start, end));
    }

    private static void AddPolylineFromPoints(List<Segment> segments, Dictionary<int, List<string>> attrs, DxfLoadDiagnosticsBuilder diagnostics)
    {
        if (!attrs.TryGetValue(10, out var xs) || !attrs.TryGetValue(20, out var ys))
        {
            return;
        }

        bool closed = IsClosed(attrs);
        var points = new List<WorldPoint>();
        int count = Math.Min(xs.Count, ys.Count);
        for (int index = 0; index < count; index++)
        {
            if (double.TryParse(xs[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(ys[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                double.IsFinite(x) && double.IsFinite(y))
            {
                points.Add(new WorldPoint(x, y));
            }
        }

        AddChain(segments, points, closed, diagnostics);
    }

    private static int AddLegacyPolyline(
        List<Segment> segments,
        Dictionary<int, List<string>> polylineAttrs,
        IReadOnlyList<(int Code, string Value)> pairs,
        int i,
        DxfLoadDiagnosticsBuilder diagnostics)
    {
        bool closed = IsClosed(polylineAttrs);
        var points = new List<WorldPoint>();
        while (i < pairs.Count && pairs[i].Code == 0 &&
               (IsPair(pairs[i], 0, "VERTEX") || IsPair(pairs[i], 0, "SEQEND")))
        {
            var (type, attrs) = ReadEntity(pairs, ref i);
            if (string.Equals(type, "SEQEND", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (TryGetDouble(attrs, 10, 0, out var x) && TryGetDouble(attrs, 20, 0, out var y))
            {
                points.Add(new WorldPoint(x, y));
            }
        }

        AddChain(segments, points, closed, diagnostics);
        return i;
    }

    private static bool IsClosed(Dictionary<int, List<string>> attrs) =>
        attrs.TryGetValue(70, out var flags) && flags.Count > 0 &&
        int.TryParse(flags[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var flag) &&
        (flag & 1) != 0;

    private static void AddChain(List<Segment> segments, IReadOnlyList<WorldPoint> points, bool closed, DxfLoadDiagnosticsBuilder diagnostics)
    {
        for (int index = 0; index < points.Count - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            if (start == end)
            {
                diagnostics.RecordZeroLengthSegment();
                diagnostics.RecordDuplicateConsecutiveVertex();
            }

            segments.Add(new Segment(start, end));
        }

        if (closed && points.Count > 2)
        {
            segments.Add(new Segment(points[^1], points[0]));
        }
    }

    private static void AddCircle(List<Segment> segments, Dictionary<int, List<string>> attrs, DxfLoadDiagnosticsBuilder diagnostics)
    {
        if (!TryGetDouble(attrs, 10, 0, out var cx) || !TryGetDouble(attrs, 20, 0, out var cy) ||
            !TryGetDouble(attrs, 40, 0, out var radius) || radius <= 0)
        {
            return;
        }

        TessellateArc(segments, attrs, cx, cy, GetDoubleOrDefault(attrs, 30, 0), radius, 0, 360, CircleTessellation, diagnostics);
    }

    private static void AddArc(List<Segment> segments, Dictionary<int, List<string>> attrs, DxfLoadDiagnosticsBuilder diagnostics)
    {
        if (!TryGetDouble(attrs, 10, 0, out var cx) || !TryGetDouble(attrs, 20, 0, out var cy) ||
            !TryGetDouble(attrs, 40, 0, out var radius) || radius <= 0 ||
            !TryGetDouble(attrs, 50, 0, out var startDegrees) ||
            !TryGetDouble(attrs, 51, 0, out var endDegrees))
        {
            return;
        }

        if (endDegrees < startDegrees)
        {
            endDegrees += 360;
        }

        int steps = Math.Max(2, (int)(CircleTessellation * (endDegrees - startDegrees) / 360));
        TessellateArc(segments, attrs, cx, cy, GetDoubleOrDefault(attrs, 30, 0), radius, startDegrees, endDegrees, steps, diagnostics);
    }

    private static void TessellateArc(
        List<Segment> segments,
        Dictionary<int, List<string>> attrs,
        double cx,
        double cy,
        double cz,
        double radius,
        double startDegrees,
        double endDegrees,
        int steps,
        DxfLoadDiagnosticsBuilder diagnostics)
    {
        var points = new List<WorldPoint>(steps + 1);
        for (int step = 0; step <= steps; step++)
        {
            double angle = (startDegrees + (endDegrees - startDegrees) * step / steps) * Math.PI / 180;
            points.Add(OcsToWcs(attrs, cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle), cz));
        }
        diagnostics.RecordTessellatedCurve(steps);
        AddChain(segments, points, closed: false, diagnostics);
    }

    private static WorldPoint OcsToWcs(Dictionary<int, List<string>> attrs, double x, double y, double z)
    {
        double nx = GetDoubleOrDefault(attrs, 210, 0);
        double ny = GetDoubleOrDefault(attrs, 220, 0);
        double nz = GetDoubleOrDefault(attrs, 230, 1);
        if (nx == 0 && ny == 0 && nz == 1)
        {
            return new WorldPoint(x, y);
        }

        double normalLength = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (normalLength == 0)
        {
            return new WorldPoint(x, y);
        }

        nx /= normalLength;
        ny /= normalLength;
        nz /= normalLength;

        double ax;
        double ay;
        double az;
        if (Math.Abs(nx) < 1d / 64 && Math.Abs(ny) < 1d / 64)
        {
            ax = nz;
            ay = 0;
            az = -nx;
        }
        else
        {
            ax = -ny;
            ay = nx;
            az = 0;
        }

        double axisLength = Math.Sqrt(ax * ax + ay * ay + az * az);
        ax /= axisLength;
        ay /= axisLength;
        az /= axisLength;

        double bx = ny * az - nz * ay;
        double by = nz * ax - nx * az;
        return new WorldPoint(x * ax + y * bx + z * nx, x * ay + y * by + z * ny);
    }

    private static double GetDoubleOrDefault(
        Dictionary<int, List<string>> attrs,
        int code,
        double defaultValue) =>
        TryGetDouble(attrs, code, 0, out var value) ? value : defaultValue;

    private static bool TryGetDouble(
        Dictionary<int, List<string>> attrs,
        int code,
        int index,
        out double value)
    {
        value = 0;
        return attrs.TryGetValue(code, out var list) && index < list.Count &&
               double.TryParse(list[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }

    private static List<(int Code, string Value)> ReadPairs(TextReader reader)
    {
        var pairs = new List<(int Code, string Value)>();
        int lineNumber = 0;
        while (true)
        {
            string? codeLine = reader.ReadLine();
            lineNumber++;
            if (codeLine is null)
            {
                break;
            }

            string? valueLine = reader.ReadLine();
            lineNumber++;
            if (valueLine is null)
            {
                throw new InvalidDataException($"DXF group code의 값이 누락되었습니다 ({lineNumber - 1}행). ");
            }

            if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                throw new InvalidDataException($"잘못된 DXF group code입니다 ({lineNumber - 1}행). ");
            }

            pairs.Add((code, valueLine.Trim()));
        }

        if (pairs.Count == 0)
        {
            throw new InvalidDataException("DXF 파일이 비어 있습니다.");
        }

        return pairs;
    }
}
