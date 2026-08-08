namespace WalkDistance.Core;

/// <summary>
/// Spatial index over a fixed set of wall segments. Backs two operations that
/// would otherwise scan every wall segment on each call: nearest-wall
/// snapping (bucketed uniform grid) and fixed-length tracing along connected
/// wall chains (endpoint adjacency map). Tessellated arcs and circles are
/// already stored as consecutive short segments that share exact endpoint
/// values (see DxfLoader.AddChain), so they trace like any other polyline.
/// ponytail: uniform grid, not an R-tree; upgrade if wall density becomes
/// wildly non-uniform and buckets get overloaded.
/// </summary>
public sealed class WallIndex
{
    private readonly IReadOnlyList<Segment> _walls;
    private readonly Dictionary<(long Col, long Row), List<int>> _grid;
    private readonly Dictionary<(long X, long Y), List<int>> _vertexAdjacency;
    private readonly double _cellSize;
    private readonly double _minX;
    private readonly double _minY;
    private readonly double _vertexEpsilon;

    private WallIndex(
        IReadOnlyList<Segment> walls,
        Dictionary<(long Col, long Row), List<int>> grid,
        Dictionary<(long X, long Y), List<int>> vertexAdjacency,
        double cellSize,
        double minX,
        double minY,
        double vertexEpsilon)
    {
        _walls = walls;
        _grid = grid;
        _vertexAdjacency = vertexAdjacency;
        _cellSize = cellSize;
        _minX = minX;
        _minY = minY;
        _vertexEpsilon = vertexEpsilon;
    }

    public static WallIndex Build(IReadOnlyList<Segment> walls)
    {
        var wallSnapshot = walls.ToArray();
        if (wallSnapshot.Length == 0)
        {
            return new WallIndex(wallSnapshot, [], [], 1, 0, 0, 1e-6);
        }

        var bounds = Bounds.FromSegments(wallSnapshot);
        double averageLength = wallSnapshot.Average(w => Distance(w.Start, w.End));
        double spanDiagonal = Math.Max(bounds.Width, bounds.Height);
        double cellSize = averageLength > 0 && double.IsFinite(averageLength)
            ? averageLength
            : Math.Max(spanDiagonal / 64.0, 1e-6);
        if (!double.IsFinite(cellSize) || cellSize <= 0)
        {
            cellSize = 1.0;
        }

        double coordinateScale = Math.Max(
            Math.Max(Math.Abs(bounds.MinX), Math.Abs(bounds.MaxX)),
            Math.Max(Math.Abs(bounds.MinY), Math.Abs(bounds.MaxY)));
        double vertexEpsilon = Math.Max(1e-6, coordinateScale * 1e-9);

        var grid = new Dictionary<(long Col, long Row), List<int>>();
        var vertexAdjacency = new Dictionary<(long X, long Y), List<int>>();

        for (int i = 0; i < wallSnapshot.Length; i++)
        {
            var wall = wallSnapshot[i];
            foreach (var key in CellsCovered(wall, bounds.MinX, bounds.MinY, cellSize))
            {
                AddTo(grid, key, i);
            }
            AddTo(vertexAdjacency, VertexKey(wall.Start, vertexEpsilon), i);
            AddTo(vertexAdjacency, VertexKey(wall.End, vertexEpsilon), i);
        }

        return new WallIndex(wallSnapshot, grid, vertexAdjacency, cellSize, bounds.MinX, bounds.MinY, vertexEpsilon);
    }

    /// <summary>
    /// Nearest point on any wall within tolerance, plus the index of the wall
    /// it landed on. Only buckets near the query point are scanned.
    /// </summary>
    public (WorldPoint Point, int SegmentIndex)? TrySnapToNearest(WorldPoint point, double tolerance)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new ArgumentException("좌표 값이 유한하지 않습니다.", nameof(point));
        }
        if (!double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "허용 오차는 0 이상의 유한한 값이어야 합니다.");
        }
        if (_walls.Count == 0)
        {
            return null;
        }

        (WorldPoint Point, int SegmentIndex)? best = null;
        double bestDistance = double.PositiveInfinity;
        foreach (var index in CandidateIndexes(point, tolerance))
        {
            var projected = GeometrySnap.ProjectOntoSegment(point, _walls[index]);
            double distance = Distance(point, projected);
            if (distance <= tolerance && distance < bestDistance)
            {
                bestDistance = distance;
                best = (projected, index);
            }
        }
        return best;
    }

    /// <summary>
    /// Traces a fixed-length route starting at <paramref name="start"/> (which
    /// must lie on a wall within <paramref name="tolerance"/>), heading toward
    /// whichever of that wall's two ends is closer to <paramref name="towardPoint"/>,
    /// then continuing across connected wall segments (straightest continuation
    /// at junctions) until the requested length is walked. Returns fewer points
    /// (a shorter route) when the connected wall chain runs out first; never
    /// throws for that case. Returns just <c>[start]</c> when start is not on
    /// any wall or the request is degenerate.
    /// </summary>
    public IReadOnlyList<WorldPoint> TraceFixedLength(
        WorldPoint start, WorldPoint towardPoint, double length, double tolerance)
    {
        if (!double.IsFinite(length) || length <= 0 ||
            !double.IsFinite(start.X) || !double.IsFinite(start.Y) ||
            _walls.Count == 0)
        {
            return [start];
        }

        var onSegment = TrySnapToNearest(start, tolerance);
        if (onSegment is null)
        {
            return [start];
        }

        return TraceFixedLength(onSegment.Value.SegmentIndex, start, towardPoint, length);
    }

    /// <summary>
    /// Snaps <paramref name="midpoint"/> to a wall and traces half of the
    /// requested length in each direction. The optional toward point controls
    /// start-to-end orientation; otherwise the snapped wall's orientation is
    /// used. A full-length route is required.
    /// </summary>
    public IReadOnlyList<WorldPoint> TraceFixedLengthFromMidpoint(
        WorldPoint midpoint,
        double length,
        double tolerance,
        WorldPoint? towardPoint = null)
    {
        if (!double.IsFinite(length) || length <= 0 ||
            !double.IsFinite(midpoint.X) || !double.IsFinite(midpoint.Y) ||
            _walls.Count == 0)
        {
            return [midpoint];
        }

        var snap = TrySnapToNearest(midpoint, tolerance);
        if (snap is null)
        {
            return [midpoint];
        }

        var center = snap.Value.Point;
        var segment = _walls[snap.Value.SegmentIndex];
        var forwardTarget = towardPoint ?? new WorldPoint(
            center.X + segment.End.X - segment.Start.X,
            center.Y + segment.End.Y - segment.Start.Y);
        if (forwardTarget == center)
        {
            forwardTarget = segment.End;
        }
        var backwardTarget = new WorldPoint(
            (2 * center.X) - forwardTarget.X,
            (2 * center.Y) - forwardTarget.Y);
        double halfLength = length / 2;
        var backward = TraceFixedLength(
            snap.Value.SegmentIndex, center, backwardTarget, halfLength);
        var forward = TraceFixedLength(
            snap.Value.SegmentIndex, center, forwardTarget, halfLength);
        double lengthTolerance = Math.Max(1e-9, length * 1e-9);
        if (Math.Abs(PathLength(backward) - halfLength) > lengthTolerance ||
            Math.Abs(PathLength(forward) - halfLength) > lengthTolerance)
        {
            return [center];
        }

        return backward.Reverse().Concat(forward.Skip(1)).ToArray();
    }

    private IReadOnlyList<WorldPoint> TraceFixedLength(
        int initialSegmentIndex,
        WorldPoint start,
        WorldPoint towardPoint,
        double length)
    {
        var segment = _walls[initialSegmentIndex];
        var visited = new HashSet<int> { initialSegmentIndex };
        var points = new List<WorldPoint> { start };

        WorldPoint previous = start;
        WorldPoint next = ChooseDirection(start, segment, towardPoint);
        double remaining = length;

        while (remaining > 1e-12)
        {
            double hop = Distance(previous, next);
            if (hop <= 1e-12)
            {
                var zeroHopContinuation = FindContinuation(
                    next,
                    SquaredDistance(next, segment.Start) <= 1e-24 ? segment.End : segment.Start,
                    visited);
                if (zeroHopContinuation is null)
                {
                    break;
                }
                next = zeroHopContinuation.Value.FarEnd;
                visited.Add(zeroHopContinuation.Value.Index);
                continue;
            }

            if (hop >= remaining)
            {
                points.Add(Lerp(previous, next, remaining / hop));
                break;
            }

            points.Add(next);
            remaining -= hop;

            var continuation = FindContinuation(next, previous, visited);
            if (continuation is null)
            {
                break;
            }

            previous = next;
            next = continuation.Value.FarEnd;
            visited.Add(continuation.Value.Index);
        }

        return points;
    }

    private static double PathLength(IReadOnlyList<WorldPoint> path) =>
        path.Zip(path.Skip(1)).Sum(segment => Distance(segment.First, segment.Second));

    private (int Index, WorldPoint FarEnd)? FindContinuation(
        WorldPoint vertex, WorldPoint incomingFrom, HashSet<int> visited)
    {
        if (!_vertexAdjacency.TryGetValue(VertexKey(vertex, _vertexEpsilon), out var candidates))
        {
            return null;
        }

        double inX = vertex.X - incomingFrom.X;
        double inY = vertex.Y - incomingFrom.Y;
        double bestScore = double.NegativeInfinity;
        (int Index, WorldPoint FarEnd)? best = null;

        foreach (var index in candidates)
        {
            if (visited.Contains(index))
            {
                continue;
            }

            var far = FarEndpoint(_walls[index], vertex);
            double outX = far.X - vertex.X;
            double outY = far.Y - vertex.Y;
            double score = inX * outX + inY * outY; // favors the straightest continuation
            if (score > bestScore)
            {
                bestScore = score;
                best = (index, far);
            }
        }

        return best;
    }

    private static WorldPoint ChooseDirection(WorldPoint start, Segment segment, WorldPoint towardPoint)
    {
        double towardX = towardPoint.X - start.X;
        double towardY = towardPoint.Y - start.Y;
        if (towardX == 0 && towardY == 0)
        {
            return segment.End;
        }

        double startDot = ((segment.Start.X - start.X) * towardX) + ((segment.Start.Y - start.Y) * towardY);
        double endDot = ((segment.End.X - start.X) * towardX) + ((segment.End.Y - start.Y) * towardY);
        return endDot >= startDot ? segment.End : segment.Start;
    }

    private static WorldPoint FarEndpoint(Segment segment, WorldPoint vertex)
    {
        double toStart = SquaredDistance(vertex, segment.Start);
        double toEnd = SquaredDistance(vertex, segment.End);
        return toStart <= toEnd ? segment.End : segment.Start;
    }

    private IEnumerable<int> CandidateIndexes(WorldPoint point, double tolerance)
    {
        if (_grid.Count == 0)
        {
            yield break;
        }

        int radiusCells = Math.Max(1, (int)Math.Ceiling(tolerance / _cellSize) + 1);
        var (col, row) = CellOf(point);
        var seen = new HashSet<int>();
        for (long dc = -radiusCells; dc <= radiusCells; dc++)
        {
            for (long dr = -radiusCells; dr <= radiusCells; dr++)
            {
                if (!_grid.TryGetValue((col + dc, row + dr), out var bucket))
                {
                    continue;
                }
                foreach (var index in bucket)
                {
                    if (seen.Add(index))
                    {
                        yield return index;
                    }
                }
            }
        }
    }

    private (long Col, long Row) CellOf(WorldPoint p) =>
        ((long)Math.Floor((p.X - _minX) / _cellSize), (long)Math.Floor((p.Y - _minY) / _cellSize));

    private static IEnumerable<(long Col, long Row)> CellsCovered(
        Segment wall, double minX, double minY, double cellSize)
    {
        double length = Distance(wall.Start, wall.End);
        int steps = Math.Max(1, (int)Math.Ceiling(length / (cellSize / 2)));
        var seen = new HashSet<(long, long)>();
        for (int s = 0; s <= steps; s++)
        {
            var p = Lerp(wall.Start, wall.End, (double)s / steps);
            var key = ((long)Math.Floor((p.X - minX) / cellSize), (long)Math.Floor((p.Y - minY) / cellSize));
            if (seen.Add(key))
            {
                yield return key;
            }
        }
    }

    private static void AddTo(Dictionary<(long, long), List<int>> map, (long, long) key, int index)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }
        list.Add(index);
    }

    private static (long X, long Y) VertexKey(WorldPoint p, double epsilon) =>
        ((long)Math.Round(p.X / epsilon), (long)Math.Round(p.Y / epsilon));

    private static WorldPoint Lerp(WorldPoint a, WorldPoint b, double t) =>
        new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    private static double Distance(WorldPoint a, WorldPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double SquaredDistance(WorldPoint a, WorldPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return (dx * dx) + (dy * dy);
    }
}
