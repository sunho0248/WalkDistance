using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class ExitLineEditorTests
{
    [Fact]
    public void LeftClicks_SetPendingThenCommitSegment()
    {
        var editor = new ExitLineEditor();
        var start = new WorldPoint(1, 2);
        var end = new WorldPoint(3, 4);

        Assert.False(editor.HandleLeftClick(start));
        Assert.Equal(ExitDrawState.AwaitingSecondPoint, editor.State);
        Assert.Equal(start, editor.PendingStart);
        Assert.Empty(editor.Segments);

        Assert.True(editor.HandleLeftClick(end));
        Assert.Equal(ExitDrawState.Idle, editor.State);
        Assert.Null(editor.PendingStart);
        Assert.Equal(new Segment(start, end), Assert.Single(editor.Segments));
    }

    [Fact]
    public void RightClick_CancelsOnlyPendingSegment()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        editor.HandleLeftClick(new WorldPoint(2, 0));

        Assert.True(editor.HandleRightClick());
        Assert.Equal(ExitDrawState.Idle, editor.State);
        Assert.Single(editor.Segments);
    }

    [Fact]
    public void RightClick_WhenIdle_RemovesLastSegmentOrDoesNothing()
    {
        var editor = new ExitLineEditor();

        Assert.False(editor.HandleRightClick());

        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        editor.HandleLeftClick(new WorldPoint(2, 0));
        editor.HandleLeftClick(new WorldPoint(3, 0));

        Assert.False(editor.HandleRightClick());
        Assert.Equal(new Segment(new WorldPoint(0, 0), new WorldPoint(1, 0)), Assert.Single(editor.Segments));
    }

    [Fact]
    public void Clear_EmptiesSegmentsAndPendingState()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        editor.HandleLeftClick(new WorldPoint(2, 0));

        editor.Clear();

        Assert.Empty(editor.Segments);
        Assert.Equal(ExitDrawState.Idle, editor.State);
    }

    [Fact]
    public void LoadSegments_ReplacesSegmentsAndCancelsPendingState()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        var segments = new[]
        {
            new Segment(new WorldPoint(1, 1), new WorldPoint(2, 2)),
            new Segment(new WorldPoint(3, 3), new WorldPoint(4, 4)),
        };

        editor.LoadSegments(segments);

        Assert.Equal(segments, editor.Segments);
        Assert.Equal(ExitDrawState.Idle, editor.State);
    }
}
