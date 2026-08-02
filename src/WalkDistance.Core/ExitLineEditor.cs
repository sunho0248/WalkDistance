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

public sealed class ExitLineEditor
{
    private readonly List<Segment> _segments = [];
    private WorldPoint? _pendingStart;

    public IReadOnlyList<Segment> Segments => _segments;
    public ExitDrawState State => _pendingStart is null ? ExitDrawState.Idle : ExitDrawState.AwaitingSecondPoint;
    public WorldPoint? PendingStart => _pendingStart;
    public int? SelectedIndex { get; private set; }
    public Segment? SelectedExit => SelectedIndex is int index ? _segments[index] : null;

    public bool HandleLeftClick(WorldPoint point)
    {
        SelectedIndex = null;

        if (_pendingStart is null)
        {
            _pendingStart = point;
            return false;
        }

        _segments.Add(new Segment(_pendingStart.Value, point));
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
        for (int i = 0; i < _segments.Count; i++)
        {
            double distance = DistanceToSegment(point, _segments[i]);
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

        _segments.RemoveAt(index);
        SelectedIndex = null;
        return true;
    }

    public void Clear()
    {
        _segments.Clear();
        _pendingStart = null;
        SelectedIndex = null;
    }

    public void LoadSegments(IEnumerable<Segment> segments)
    {
        Clear();
        _segments.AddRange(segments);
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
