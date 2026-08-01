namespace WalkDistance.Core;

public enum ExitDrawState
{
    Idle,
    AwaitingSecondPoint,
}

public sealed class ExitLineEditor
{
    private readonly List<Segment> _segments = [];
    private WorldPoint? _pendingStart;

    public IReadOnlyList<Segment> Segments => _segments;
    public ExitDrawState State => _pendingStart is null ? ExitDrawState.Idle : ExitDrawState.AwaitingSecondPoint;
    public WorldPoint? PendingStart => _pendingStart;

    public bool HandleLeftClick(WorldPoint point)
    {
        if (_pendingStart is null)
        {
            _pendingStart = point;
            return false;
        }

        _segments.Add(new Segment(_pendingStart.Value, point));
        _pendingStart = null;
        return true;
    }

    public bool HandleRightClick()
    {
        if (_pendingStart is not null)
        {
            _pendingStart = null;
            return true;
        }

        if (_segments.Count > 0)
        {
            _segments.RemoveAt(_segments.Count - 1);
        }
        return false;
    }

    public void Clear()
    {
        _segments.Clear();
        _pendingStart = null;
    }

    public void LoadSegments(IEnumerable<Segment> segments)
    {
        Clear();
        _segments.AddRange(segments);
    }
}
