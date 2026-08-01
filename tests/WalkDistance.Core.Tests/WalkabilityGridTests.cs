using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class WalkabilityGridTests
{
    [Fact]
    public void Build_MarksCellsAlongWallAsBlocked()
    {
        var walls = new List<Segment>
        {
            new(new WorldPoint(0, 0), new WorldPoint(10, 0)),
            new(new WorldPoint(0, 0), new WorldPoint(0, 10)),
            new(new WorldPoint(10, 0), new WorldPoint(10, 10)),
            new(new WorldPoint(0, 10), new WorldPoint(10, 10)),
        };

        var grid = WalkabilityGrid.Build(walls, cellSize: 1.0, marginCells: 1);

        var (col, row) = grid.WorldToCell(new WorldPoint(0, 5));
        Assert.True(grid.IsBlocked(col, row));

        var (freeCol, freeRow) = grid.WorldToCell(new WorldPoint(5, 5));
        Assert.False(grid.IsBlocked(freeCol, freeRow));
    }

    [Fact]
    public void NearestWalkableCell_StepsAwayFromBlockedStart()
    {
        var walls = new List<Segment> { new(new WorldPoint(0, 0), new WorldPoint(10, 0)) };
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.5, marginCells: 4);

        var cell = grid.NearestWalkableCell(new WorldPoint(5, 0));

        Assert.NotNull(cell);
        Assert.False(grid.IsBlocked(cell!.Value.Col, cell.Value.Row));
    }

    [Fact]
    public void Build_RejectsExcessiveCellCountBeforeAllocation()
    {
        var walls = new List<Segment>
        {
            new(new WorldPoint(0, 0), new WorldPoint(100, 100)),
        };

        var exception = Assert.Throws<GridSizeLimitExceededException>(() =>
            WalkabilityGrid.Build(walls, cellSize: 0.01, marginCells: 0, maxCellCount: 10_000));

        Assert.True(exception.RequestedCellCount > exception.MaxCellCount);
    }

    [Fact]
    public void WalkableCellsNearSegment_ReturnsDeduplicatedWalkableHalo()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 10, 10), cellSize: 1, marginCells: 0);
        var segment = new Segment(new WorldPoint(3.2, 5.2), new WorldPoint(5.2, 5.2));

        var cells = grid.WalkableCellsNearSegment(segment, grid.CellSize);

        Assert.Equal(cells.Count, cells.Distinct().Count());
        Assert.All(cells, cell => Assert.False(grid.IsBlocked(cell.Col, cell.Row)));
        Assert.Contains(grid.WorldToCell(segment.Start), cells);
        Assert.Contains(grid.WorldToCell(segment.End), cells);
        Assert.Contains((Col: 2, Row: 4), cells);
        Assert.Contains((Col: 6, Row: 6), cells);
    }

    [Fact]
    public void WalkableCellsNearSegment_ZeroLengthMatchesLegacyNearestCell()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 10, 10), cellSize: 1, marginCells: 0);
        var point = new WorldPoint(5.2, 5.2);

        var cells = grid.WalkableCellsNearSegment(new Segment(point, point), grid.CellSize);

        Assert.Equal(grid.NearestWalkableCell(point), Assert.Single(cells));
    }

    [Fact]
    public void WalkableCellsNearSegment_EmbeddedZeroLengthFallsBackToLegacyNearestCell()
    {
        var walls = Enumerable.Range(3, 5)
            .Select(y => new Segment(new WorldPoint(0, y), new WorldPoint(10, y)))
            .ToList();
        var grid = WalkabilityGrid.Build(walls, cellSize: 1, marginCells: 4);
        var point = new WorldPoint(5.2, 5.2);

        var cells = grid.WalkableCellsNearSegment(new Segment(point, point), grid.CellSize);

        Assert.Equal(grid.NearestWalkableCell(point), Assert.Single(cells));
    }

    [Fact]
    public void WalkableCellsNearSegment_DoesNotReachDisconnectedRoom()
    {
        var walls = Rectangle(0, 0, 2, 2);
        walls.AddRange(Rectangle(4, 0, 6, 2));
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.25, marginCells: 1);
        var segment = new Segment(new WorldPoint(0.75, 1), new WorldPoint(1.25, 1));

        var cells = grid.WalkableCellsNearSegment(segment, grid.CellSize);

        Assert.NotEmpty(cells);
        Assert.All(cells, cell => Assert.True(grid.CellCenter(cell.Col, cell.Row).X < 2));
    }

    private static List<Segment> Rectangle(double minX, double minY, double maxX, double maxY) =>
    [
        new(new WorldPoint(minX, minY), new WorldPoint(maxX, minY)),
        new(new WorldPoint(maxX, minY), new WorldPoint(maxX, maxY)),
        new(new WorldPoint(maxX, maxY), new WorldPoint(minX, maxY)),
        new(new WorldPoint(minX, maxY), new WorldPoint(minX, minY)),
    ];
}
