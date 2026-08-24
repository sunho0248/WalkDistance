using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;

namespace WalkDistance.Core;

public sealed class ContinuousBuildException(string reasonCode, string message) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
}

public sealed record ContinuousGeometryDiagnostics(
    int InputSegments,
    int NormalizedSegments,
    int CollapsedSegments,
    int DuplicateSegments,
    int NodedSegments,
    int PolygonCount,
    int Dangles,
    int CutEdges,
    int InvalidRings,
    int RejectedExits);

public static class ContinuousGeometry
{
    public const string PolicyVersion = "continuous-geometry-v1";
    internal const double Precision = 0.000001;
    private const double PrecisionScale = 1_000_000;
    private const int BufferQuadrantSegments = 18;

    public static ContinuousModel Build(
        IReadOnlyList<Segment> walls,
        IReadOnlyList<IReadOnlyList<WorldPoint>> exitPaths,
        double clearanceRadius = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(walls);
        ArgumentNullException.ThrowIfNull(exitPaths);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(clearanceRadius) || clearanceRadius < 0)
        {
            throw new ContinuousBuildException("NON_FINITE_INPUT", "Clearance radius must be finite and non-negative.");
        }
        if (walls.Any(wall => !Finite(wall.Start) || !Finite(wall.End)) ||
            exitPaths.Any(path => path is null || path.Any(point => !Finite(point))))
        {
            throw new ContinuousBuildException("NON_FINITE_INPUT", "Continuous geometry contains a non-finite coordinate.");
        }
        if (exitPaths.Any(path => PathLength(path) == 0))
        {
            throw new ContinuousBuildException(
                "LEGACY_POINT_EXIT_UNSUPPORTED",
                "A zero-arclength legacy exit requires the complete legacy result.");
        }

        var factory = new GeometryFactory(new PrecisionModel(PrecisionScale));
        var normalized = Normalize(walls, out int collapsed, out int duplicates);
        cancellationToken.ThrowIfCancellationRequested();
        if (normalized.Count == 0)
        {
            throw new ContinuousBuildException("NO_BOUNDED_FACE", "No normalized wall linework remains.");
        }

        var inputLines = normalized.Select(segment => factory.CreateLineString(
            [Coordinate(segment.Start), Coordinate(segment.End)])).Cast<Geometry>().ToArray();
        Geometry noded;
        try
        {
            noded = UnaryUnionOp.Union(inputLines, factory);
        }
        catch (Exception exception)
        {
            throw GeometryError("INTERNAL_GEOMETRY_ERROR", "Wall noding failed.", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var blockerSegments = ExtractSegments(noded, factory).ToArray();
        var polygonizer = new Polygonizer();
        polygonizer.Add(noded);
        cancellationToken.ThrowIfCancellationRequested();
        var invalidRings = polygonizer.GetInvalidRingLines();
        if (invalidRings.Count > 0)
        {
            throw new ContinuousBuildException("INVALID_RING", "Polygonization produced invalid rings.");
        }

        var polygons = polygonizer.GetPolygons();
        if (polygons.Count == 0)
        {
            throw new ContinuousBuildException("NO_BOUNDED_FACE", "No bounded polygonal floor face was produced.");
        }

        Geometry floor;
        try
        {
            floor = UnaryUnionOp.Union(polygons.Cast<Geometry>(), factory);
        }
        catch (Exception exception)
        {
            throw GeometryError("INVALID_RING", "Bounded floor faces could not be unioned.", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (floor.IsEmpty || !floor.IsValid)
        {
            throw new ContinuousBuildException("INVALID_RING", "The polygonal floor is invalid.");
        }

        double safeRadius = clearanceRadius == 0
            ? 0
            : clearanceRadius / (1 - BufferParameters.BufferDistanceError(BufferQuadrantSegments)) + Precision;
        Geometry freeSpace;
        try
        {
            freeSpace = clearanceRadius == 0
                ? floor
                : floor.Difference(noded.Buffer(safeRadius, BufferQuadrantSegments));
        }
        catch (Exception exception)
        {
            throw GeometryError("INTERNAL_GEOMETRY_ERROR", "Clearance geometry construction failed.", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (freeSpace.IsEmpty)
        {
            throw new ContinuousBuildException("CLEARANCE_COLLAPSED", "Clearance removed the complete floor.");
        }

        var blockerIndex = new STRtree<LineString>();
        foreach (var segment in blockerSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            blockerIndex.Insert(segment.EnvelopeInternal, segment);
        }
        blockerIndex.Build();

        int rejectedExits = 0;
        var targets = new List<ContinuousTarget>();
        for (int exitIndex = 0; exitIndex < exitPaths.Count; exitIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NormalizePath(exitPaths[exitIndex]);
            if (clearanceRadius > 0 && PathLength(path) < 2 * clearanceRadius)
            {
                rejectedExits++;
                continue;
            }

            var exitGeometry = PathGeometry(path, factory);
            if (clearanceRadius == 0)
            {
                foreach (var (segment, _) in PathSegments(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (floor.Distance(Line(segment, factory)) <= Precision)
                    {
                        targets.Add(new ContinuousTarget(
                            targets.Count, exitIndex, segment, segment, 0, 0, false));
                    }
                }
                if (!targets.Any(target => target.ExitIndex == exitIndex)) rejectedExits++;
                continue;
            }

            var trimmed = Trim(path, clearanceRadius);
            int before = targets.Count;
            foreach (var (source, _) in PathSegments(trimmed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                double dx = source.End.X - source.Start.X;
                double dy = source.End.Y - source.Start.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= Precision) continue;
                for (int side = -1; side <= 1; side += 2)
                {
                    double arrivalRadius = safeRadius + Precision * 4;
                    double ox = -dy / length * arrivalRadius * side;
                    double oy = dx / length * arrivalRadius * side;
                    var target = new Segment(
                        new WorldPoint(source.Start.X + ox, source.Start.Y + oy),
                        new WorldPoint(source.End.X + ox, source.End.Y + oy));
                    var targetLine = Line(target, factory);
                    if (!freeSpace.Covers(targetLine) ||
                        !ConnectorLegal(source.Start, target.Start, exitGeometry, blockerIndex, factory) ||
                        !ConnectorLegal(source.End, target.End, exitGeometry, blockerIndex, factory))
                    {
                        continue;
                    }
                    targets.Add(new ContinuousTarget(
                        targets.Count, exitIndex, target, source, ox, oy, true));
                }
            }
            if (targets.Count == before) rejectedExits++;
        }

        if (targets.Count == 0)
        {
            throw new ContinuousBuildException("ALL_EXITS_REJECTED", "No exit has a legal continuous target.");
        }

        var diagnostics = new ContinuousGeometryDiagnostics(
            walls.Count,
            normalized.Count,
            collapsed,
            duplicates,
            blockerSegments.Length,
            polygons.Count,
            polygonizer.GetDangles().Count,
            polygonizer.GetCutEdges().Count,
            invalidRings.Count,
            rejectedExits);
        return new ContinuousModel(
            factory,
            floor,
            freeSpace,
            noded,
            blockerSegments,
            blockerIndex,
            targets,
            diagnostics,
            clearanceRadius,
            safeRadius,
            Hash(normalized, targets, clearanceRadius));
    }

    private static List<Segment> Normalize(
        IReadOnlyList<Segment> walls,
        out int collapsed,
        out int duplicates)
    {
        collapsed = 0;
        duplicates = 0;
        var unique = new HashSet<Segment>();
        foreach (var wall in walls)
        {
            var start = Snap(wall.Start);
            var end = Snap(wall.End);
            if (start == end)
            {
                collapsed++;
                continue;
            }
            var normalized = Compare(start, end) <= 0 ? new Segment(start, end) : new Segment(end, start);
            if (!unique.Add(normalized)) duplicates++;
        }
        return unique.OrderBy(segment => segment.Start.X)
            .ThenBy(segment => segment.Start.Y)
            .ThenBy(segment => segment.End.X)
            .ThenBy(segment => segment.End.Y)
            .ToList();
    }

    private static IReadOnlyList<WorldPoint> NormalizePath(IReadOnlyList<WorldPoint> path)
    {
        var points = new List<WorldPoint>();
        foreach (var point in path.Select(Snap))
        {
            if (points.Count == 0 || points[^1] != point) points.Add(point);
        }
        return points;
    }

    private static IReadOnlyList<WorldPoint> Trim(IReadOnlyList<WorldPoint> path, double amount)
    {
        double length = PathLength(path);
        var start = PointAt(path, amount);
        var end = PointAt(path, length - amount);
        var result = new List<WorldPoint> { start };
        double walked = 0;
        for (int index = 0; index < path.Count - 1; index++)
        {
            double segmentLength = Distance(path[index], path[index + 1]);
            walked += segmentLength;
            if (walked > amount + Precision && walked < length - amount - Precision)
            {
                result.Add(path[index + 1]);
            }
        }
        if (result[^1] != end) result.Add(end);
        return result;
    }

    private static WorldPoint PointAt(IReadOnlyList<WorldPoint> path, double distance)
    {
        double walked = 0;
        foreach (var (segment, _) in PathSegments(path))
        {
            double length = Distance(segment.Start, segment.End);
            if (walked + length >= distance)
            {
                double ratio = length == 0 ? 0 : (distance - walked) / length;
                return new WorldPoint(
                    segment.Start.X + (segment.End.X - segment.Start.X) * ratio,
                    segment.Start.Y + (segment.End.Y - segment.Start.Y) * ratio);
            }
            walked += length;
        }
        return path[^1];
    }

    internal static IEnumerable<(Segment Segment, int Index)> PathSegments(IReadOnlyList<WorldPoint> path)
    {
        for (int index = 0; index < path.Count - 1; index++)
        {
            if (path[index] != path[index + 1]) yield return (new Segment(path[index], path[index + 1]), index);
        }
    }

    internal static IEnumerable<LineString> ExtractSegments(Geometry geometry, GeometryFactory factory)
    {
        if (geometry is LineString line)
        {
            for (int index = 0; index < line.NumPoints - 1; index++)
            {
                var start = line.GetCoordinateN(index);
                var end = line.GetCoordinateN(index + 1);
                if (!start.Equals2D(end)) yield return factory.CreateLineString([start.Copy(), end.Copy()]);
            }
            yield break;
        }
        for (int index = 0; index < geometry.NumGeometries; index++)
        {
            foreach (var segment in ExtractSegments(geometry.GetGeometryN(index), factory)) yield return segment;
        }
    }

    internal static LineString Line(Segment segment, GeometryFactory factory) =>
        factory.CreateLineString([Coordinate(segment.Start), Coordinate(segment.End)]);

    private static Geometry PathGeometry(IReadOnlyList<WorldPoint> path, GeometryFactory factory) =>
        factory.CreateLineString(path.Select(Coordinate).ToArray());

    private static bool ConnectorLegal(
        WorldPoint contact,
        WorldPoint arrival,
        Geometry selectedExit,
        STRtree<LineString> blockers,
        GeometryFactory factory)
    {
        var connector = Line(new Segment(contact, arrival), factory);
        var selectedArea = selectedExit.Buffer(Precision * 2);
        foreach (var blocker in blockers.Query(connector.EnvelopeInternal))
        {
            if (selectedArea.Covers(blocker)) continue;
            var intersection = connector.Intersection(blocker);
            if (!intersection.IsEmpty && intersection.Coordinates.Any(point =>
                    Distance(new WorldPoint(point.X, point.Y), contact) > Precision))
            {
                return false;
            }
        }
        return true;
    }

    private static string Hash(
        IReadOnlyList<Segment> walls,
        IReadOnlyList<ContinuousTarget> targets,
        double radius)
    {
        var text = new StringBuilder(PolicyVersion).Append('|').Append(radius.ToString("R", CultureInfo.InvariantCulture));
        foreach (var wall in walls) Append(text, wall);
        foreach (var target in targets)
        {
            text.Append('|').Append(target.ExitIndex).Append(':');
            Append(text, target.Segment);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Append(StringBuilder text, Segment segment) => text
        .Append('|').Append(segment.Start.X.ToString("R", CultureInfo.InvariantCulture))
        .Append(',').Append(segment.Start.Y.ToString("R", CultureInfo.InvariantCulture))
        .Append(',').Append(segment.End.X.ToString("R", CultureInfo.InvariantCulture))
        .Append(',').Append(segment.End.Y.ToString("R", CultureInfo.InvariantCulture));

    internal static double PathLength(IReadOnlyList<WorldPoint> path) =>
        PathSegments(path).Sum(item => Distance(item.Segment.Start, item.Segment.End));

    internal static double Distance(WorldPoint left, WorldPoint right)
    {
        double dx = left.X - right.X;
        double dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal static WorldPoint Snap(WorldPoint point) => new(
        Math.Round(point.X * PrecisionScale, MidpointRounding.AwayFromZero) / PrecisionScale,
        Math.Round(point.Y * PrecisionScale, MidpointRounding.AwayFromZero) / PrecisionScale);

    internal static Coordinate Coordinate(WorldPoint point) => new(point.X, point.Y);
    internal static int Compare(WorldPoint left, WorldPoint right) =>
        left.X.CompareTo(right.X) is var x && x != 0 ? x : left.Y.CompareTo(right.Y);
    private static bool Finite(WorldPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static ContinuousBuildException GeometryError(string code, string message, Exception inner) =>
        new(code, $"{message} {inner.GetType().Name}");
}

public sealed class ContinuousModel
{
    internal ContinuousModel(
        GeometryFactory factory,
        Geometry floor,
        Geometry freeSpace,
        Geometry blockers,
        IReadOnlyList<LineString> blockerSegments,
        STRtree<LineString> blockerIndex,
        IReadOnlyList<ContinuousTarget> targets,
        ContinuousGeometryDiagnostics diagnostics,
        double clearanceRadius,
        double safeRadius,
        string modelHash)
    {
        Factory = factory;
        Floor = floor;
        FreeSpace = freeSpace;
        Blockers = blockers;
        BlockerSegments = blockerSegments;
        BlockerIndex = blockerIndex;
        Targets = targets;
        Diagnostics = diagnostics;
        ClearanceRadius = clearanceRadius;
        SafeRadius = safeRadius;
        ModelHash = modelHash;
        PreparedFreeSpace = PreparedGeometryFactory.Prepare(freeSpace);
    }

    internal GeometryFactory Factory { get; }
    internal Geometry Floor { get; }
    internal Geometry FreeSpace { get; }
    internal Geometry Blockers { get; }
    internal IReadOnlyList<LineString> BlockerSegments { get; }
    internal STRtree<LineString> BlockerIndex { get; }
    internal IReadOnlyList<ContinuousTarget> Targets { get; }
    internal IPreparedGeometry PreparedFreeSpace { get; }
    internal double SafeRadius { get; }
    public ContinuousGeometryDiagnostics Diagnostics { get; }
    public double ClearanceRadius { get; }
    public string ModelHash { get; }
}

internal sealed record ContinuousTarget(
    int Id,
    int ExitIndex,
    Segment Segment,
    Segment SourceSegment,
    double OffsetX,
    double OffsetY,
    bool IsBody)
{
    internal WorldPoint Contact(WorldPoint arrival) => IsBody
        ? new WorldPoint(arrival.X - OffsetX, arrival.Y - OffsetY)
        : arrival;
}
