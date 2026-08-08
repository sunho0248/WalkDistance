namespace WalkDistance.Core;

public enum ExitDrawState
{
    Idle,
    AwaitingSecondPoint,
}

public enum ExitRightClickResult
{
    NoOp,
    CancelledPending,
    ClearedSelection,
}

/// <summary>
/// Each exit is stored as its full point path (2 points for the classic
/// straight two-click exit, 2+ for a wall-traced fixed-length exit).
/// <see cref="Segments"/> exposes the start-to-end chord of each path for
/// callers that only care about the two endpoints (unchanged since before
/// tracing existed); <see cref="Paths"/> exposes the full route.
/// </summary>
public sealed class ExitLineEditor
{
    private readonly List<WorldPoint[]> _paths = [];
    private WorldPoint? _pendingStart;

    public IReadOnlyList<Segment> Segments => _paths.Select(p => new Segment(p[0], p[^1])).ToList();
    public IReadOnlyList<IReadOnlyList<WorldPoint>> Paths => _paths;
    public ExitDrawState State => _pendingStart is null ? ExitDrawState.Idle : ExitDrawState.AwaitingSecondPoint;
    public WorldPoint? PendingStart => _pendingStart;
    public int? SelectedIndex { get; private set; }
    public Segment? SelectedExit => SelectedIndex is int index ? new Segment(_paths[index][0], _paths[index][^1]) : null;

    public bool HandleLeftClick(WorldPoint point)
    {
        SelectedIndex = null;

        if (_pendingStart is null)
        {
            _pendingStart = point;
            return false;
        }

        _paths.Add([_pendingStart.Value, point]);
        _pendingStart = null;
        return true;
    }

    /// <summary>
    /// Commits the pending exit as a pre-traced multi-point route (a fixed-length
    /// wall-hugging exit) instead of a straight chord.
    /// No-ops (returns false) unless a start point is already pending and the
    /// route has at least 2 points.
    /// </summary>
    public bool HandleLeftClick(IReadOnlyList<WorldPoint> tracedRoute)
    {
        if (_pendingStart is null || tracedRoute.Count < 2)
        {
            return false;
        }

        SelectedIndex = null;
        _paths.Add(tracedRoute.ToArray());
        _pendingStart = null;
        return true;
    }

    public ExitRightClickResult HandleRightClick()
    {
        if (_pendingStart is not null)
        {
            _pendingStart = null;
            return ExitRightClickResult.CancelledPending;
        }

        if (SelectedIndex is not null)
        {
            SelectedIndex = null;
            return ExitRightClickResult.ClearedSelection;
        }

        return ExitRightClickResult.NoOp;
    }

    public bool TrySelectNear(WorldPoint point, double tolerance)
    {
        int? bestIndex = null;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < _paths.Count; i++)
        {
            double distance = DistanceToPath(point, _paths[i]);
            if (distance <= tolerance && distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        SelectedIndex = bestIndex;
        return bestIndex is not null;
    }

    public void ClearSelection() => SelectedIndex = null;

    public bool DeleteSelected()
    {
        if (SelectedIndex is not int index)
        {
            return false;
        }

        _paths.RemoveAt(index);
        SelectedIndex = null;
        return true;
    }

    public bool TryRelocateSelected(WallIndex walls, WorldPoint point, double tolerance)
    {
        if (SelectedIndex is not int index || walls.TrySnapToNearest(point, tolerance) is not { } snap)
        {
            return false;
        }

        var path = _paths[index];
        double length = PathLength(path);
        var direction = new WorldPoint(
            snap.Point.X + path[1].X - path[0].X,
            snap.Point.Y + path[1].Y - path[0].Y);
        var relocated = walls.TraceFixedLength(snap.Point, direction, length, tolerance);
        if (relocated.Count < 2 || Math.Abs(PathLength(relocated) - length) > Math.Max(1e-9, length * 1e-9))
        {
            return false;
        }

        _paths[index] = relocated.ToArray();
        return true;
    }

    public void Clear()
    {
        _paths.Clear();
        _pendingStart = null;
        SelectedIndex = null;
    }

    public void LoadSegments(IEnumerable<Segment> segments)
    {
        LoadPaths(segments.Select(segment =>
            (IReadOnlyList<WorldPoint>)new[] { segment.Start, segment.End }));
    }

    public void LoadPaths(IEnumerable<IReadOnlyList<WorldPoint>> paths)
    {
        Clear();
        _paths.AddRange(paths.Select(path => path.Count >= 2
            ? path.ToArray()
            : throw new ArgumentException("출구 경로는 점이 2개 이상이어야 합니다.", nameof(paths))));
    }

    private static double DistanceToPath(WorldPoint point, IReadOnlyList<WorldPoint> path)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < path.Count - 1; i++)
        {
            best = Math.Min(best, DistanceToSegment(point, new Segment(path[i], path[i + 1])));
        }
        return best;
    }

    private static double PathLength(IReadOnlyList<WorldPoint> path)
    {
        double length = 0;
        for (int i = 0; i < path.Count - 1; i++)
        {
            double dx = path[i + 1].X - path[i].X;
            double dy = path[i + 1].Y - path[i].Y;
            length += Math.Sqrt((dx * dx) + (dy * dy));
        }
        return length;
    }

    private static double DistanceToSegment(WorldPoint point, Segment segment)
    {
        double dx = segment.End.X - segment.Start.X;
        double dy = segment.End.Y - segment.Start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= double.Epsilon)
        {
            return Distance(point, segment.Start);
        }

        double t = (((point.X - segment.Start.X) * dx) + ((point.Y - segment.Start.Y) * dy)) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        var closest = new WorldPoint(segment.Start.X + (t * dx), segment.Start.Y + (t * dy));
        return Distance(point, closest);
    }

    private static double Distance(WorldPoint a, WorldPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
