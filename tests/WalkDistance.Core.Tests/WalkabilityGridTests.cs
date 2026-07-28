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
}
