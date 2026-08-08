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

        Assert.Equal(ExitRightClickResult.CancelledPending, editor.HandleRightClick());
        Assert.Equal(ExitDrawState.Idle, editor.State);
        Assert.Single(editor.Segments);
    }

    [Fact]
    public void RightClick_WhenIdle_NeverDeletesCompletedExits()
    {
        var editor = new ExitLineEditor();

        Assert.Equal(ExitRightClickResult.NoOp, editor.HandleRightClick());

        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        editor.HandleLeftClick(new WorldPoint(2, 0));
        editor.HandleLeftClick(new WorldPoint(3, 0));

        Assert.Equal(ExitRightClickResult.NoOp, editor.HandleRightClick());
        Assert.Equal(2, editor.Segments.Count);
        Assert.Equal(new Segment(new WorldPoint(0, 0), new WorldPoint(1, 0)), editor.Segments[0]);
        Assert.Equal(new Segment(new WorldPoint(2, 0), new WorldPoint(3, 0)), editor.Segments[1]);
    }

    [Fact]
    public void RightClick_WhenIdleWithSelection_ClearsSelectionWithoutDeleting()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        Assert.True(editor.TrySelectNear(new WorldPoint(0.5, 0), 0.1));
        Assert.Equal(0, editor.SelectedIndex);

        Assert.Equal(ExitRightClickResult.ClearedSelection, editor.HandleRightClick());

        Assert.Null(editor.SelectedIndex);
        Assert.Single(editor.Segments);
    }

    [Fact]
    public void TrySelectNear_SelectsClosestSegmentWithinTolerance()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        editor.HandleLeftClick(new WorldPoint(10, 10));
        editor.HandleLeftClick(new WorldPoint(11, 10));

        Assert.True(editor.TrySelectNear(new WorldPoint(10.5, 10.05), 0.2));

        Assert.Equal(1, editor.SelectedIndex);
        Assert.Equal(new Segment(new WorldPoint(10, 10), new WorldPoint(11, 10)), editor.SelectedExit);
    }

    [Fact]
    public void TrySelectNear_OutsideTolerance_ReturnsFalseAndClearsSelection()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        Assert.True(editor.TrySelectNear(new WorldPoint(0.5, 0), 0.1));

        Assert.False(editor.TrySelectNear(new WorldPoint(50, 50), 0.1));

        Assert.Null(editor.SelectedIndex);
        Assert.Null(editor.SelectedExit);
    }

    [Fact]
    public void DeleteSelected_RemovesOnlySelectedExitAndClearsSelection()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        editor.HandleLeftClick(new WorldPoint(10, 10));
        editor.HandleLeftClick(new WorldPoint(11, 10));
        Assert.True(editor.TrySelectNear(new WorldPoint(0.5, 0), 0.1));

        Assert.True(editor.DeleteSelected());

        Assert.Single(editor.Segments);
        Assert.Equal(new Segment(new WorldPoint(10, 10), new WorldPoint(11, 10)), editor.Segments[0]);
        Assert.Null(editor.SelectedIndex);
    }

    [Fact]
    public void DeleteSelected_WithNoSelection_ReturnsFalseAndKeepsSegments()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));

        Assert.False(editor.DeleteSelected());

        Assert.Single(editor.Segments);
    }

    [Fact]
    public void HandleLeftClick_StartingNewPending_ClearsExistingSelection()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        Assert.True(editor.TrySelectNear(new WorldPoint(0.5, 0), 0.1));
        Assert.NotNull(editor.SelectedIndex);

        editor.HandleLeftClick(new WorldPoint(5, 5));

        Assert.Null(editor.SelectedIndex);
    }

    [Fact]
    public void Clear_EmptiesSegmentsPendingAndSelectionState()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        editor.HandleLeftClick(new WorldPoint(2, 0));
        Assert.True(editor.TrySelectNear(new WorldPoint(0.5, 0), 0.1));

        editor.Clear();

        Assert.Empty(editor.Segments);
        Assert.Equal(ExitDrawState.Idle, editor.State);
        Assert.Null(editor.SelectedIndex);
    }

    [Fact]
    public void HandleLeftClick_TracedRoute_CommitsFullPathAndChordSegment()
    {
        var editor = new ExitLineEditor();
        var start = new WorldPoint(0, 0);
        var route = new[] { start, new WorldPoint(5, 0), new WorldPoint(5, 3) };
        editor.HandleLeftClick(start);

        Assert.True(editor.HandleLeftClick(route));

        Assert.Equal(ExitDrawState.Idle, editor.State);
        Assert.Equal(new Segment(start, new WorldPoint(5, 3)), Assert.Single(editor.Segments));
        Assert.Equal(route, Assert.Single(editor.Paths));
    }

    [Fact]
    public void HandleLeftClick_TracedRoute_WithoutPendingStart_NoOps()
    {
        var editor = new ExitLineEditor();
        var route = new[] { new WorldPoint(0, 0), new WorldPoint(5, 0) };

        Assert.False(editor.HandleLeftClick(route));
        Assert.Empty(editor.Paths);
    }

    [Fact]
    public void HandleLeftClick_TracedRoute_TooShort_NoOpsAndKeepsPending()
    {
        var editor = new ExitLineEditor();
        var start = new WorldPoint(0, 0);
        editor.HandleLeftClick(start);

        Assert.False(editor.HandleLeftClick(new[] { start }));

        Assert.Equal(ExitDrawState.AwaitingSecondPoint, editor.State);
        Assert.Equal(start, editor.PendingStart);
        Assert.Empty(editor.Paths);
    }

    [Fact]
    public void HandleLeftClick_TracedRoute_ClearsExistingSelection()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(0, 0));
        editor.HandleLeftClick(new WorldPoint(1, 0));
        Assert.True(editor.TrySelectNear(new WorldPoint(0.5, 0), 0.1));

        editor.HandleLeftClick(new WorldPoint(10, 10));
        editor.HandleLeftClick(new[] { new WorldPoint(10, 10), new WorldPoint(11, 10) });

        Assert.Null(editor.SelectedIndex);
    }

    [Fact]
    public void LoadSegments_ReplacesSegmentsCancelsPendingAndClearsSelection()
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
        Assert.Null(editor.SelectedIndex);
    }

    [Fact]
    public void CommitPath_CommitsTheExactPreviewWithoutAPendingLegacyPoint()
    {
        var editor = new ExitLineEditor();
        IReadOnlyList<WorldPoint> preview =
        [
            new WorldPoint(2, 0),
            new WorldPoint(5, 0),
            new WorldPoint(5, 2),
        ];

        Assert.True(editor.CommitPath(preview));

        Assert.Equal(preview, Assert.Single(editor.Paths));
        Assert.Equal(ExitDrawState.Idle, editor.State);
    }

    [Fact]
    public void CommitPath_ReplacesAnyPendingLegacyDraftAndReturnsToIdle()
    {
        var editor = new ExitLineEditor();
        editor.HandleLeftClick(new WorldPoint(100, 100));
        IReadOnlyList<WorldPoint> preview =
        [new WorldPoint(2, 0), new WorldPoint(5, 0)];

        Assert.True(editor.CommitPath(preview));

        Assert.Equal(preview, Assert.Single(editor.Paths));
        Assert.Null(editor.PendingStart);
        Assert.Equal(ExitDrawState.Idle, editor.State);
    }

    [Fact]
    public void LoadPaths_RestoresEveryRoutePoint()
    {
        var editor = new ExitLineEditor();
        IReadOnlyList<WorldPoint> route =
        [
            new WorldPoint(0, 0),
            new WorldPoint(4, 0),
            new WorldPoint(4, 3),
        ];

        editor.LoadPaths([route]);

        Assert.Equal(route, Assert.Single(editor.Paths));
        Assert.Equal(new Segment(route[0], route[^1]), Assert.Single(editor.Segments));
    }

    [Fact]
    public void TryRelocateSelected_UsesClickedPointAsMidpointAndPreservesLengthAndOrientation()
    {
        var walls = WallIndex.Build(
        [
            new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)),
            new Segment(new WorldPoint(10, 0), new WorldPoint(10, 10)),
        ]);
        var editor = new ExitLineEditor();
        editor.LoadPaths([[new WorldPoint(2, 0), new WorldPoint(7, 0)]]);
        Assert.True(editor.TrySelectNear(new WorldPoint(4, 0), 0.1));

        Assert.True(editor.TryRelocateSelected(walls, new WorldPoint(8, 0.2), 0.5));

        Assert.Equal(
            [
                new WorldPoint(5.5, 0),
                new WorldPoint(8, 0),
                new WorldPoint(10, 0),
                new WorldPoint(10, 0.5),
            ],
            Assert.Single(editor.Paths));
        Assert.Equal(0, editor.SelectedIndex);
    }
}
