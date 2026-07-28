using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class DistanceMapCalculatorTests
{
    [Fact]
    public void Compute_RoutesAroundAnInteriorWall()
    {
        // 10x10 room with a dividing wall from (5,0) to (5,8), leaving a 2m gap
        // at the top. A straight line from the exit to the far corner is blocked,
        // so the shortest path must detour through the gap.
        var walls = new List<Segment>
        {
            new(new WorldPoint(0, 0), new WorldPoint(10, 0)),
            new(new WorldPoint(10, 0), new WorldPoint(10, 10)),
            new(new WorldPoint(10, 10), new WorldPoint(0, 10)),
            new(new WorldPoint(0, 10), new WorldPoint(0, 0)),
            new(new WorldPoint(5, 0), new WorldPoint(5, 8)),
        };

        var grid = WalkabilityGrid.Build(walls, cellSize: 0.5, marginCells: 1);
        var exit = grid.NearestWalkableCell(new WorldPoint(1, 5))!.Value;

        var result = DistanceMapCalculator.Compute(grid, new[] { exit });

        Assert.NotNull(result.FarthestCell);

        double straightLineDistance = 8.0; // (1,5) -> (9,5), blocked by the divider
        Assert.True(result.MaxDistance > straightLineDistance,
            $"Expected a detour longer than the direct distance, got {result.MaxDistance}");
    }

    [Fact]
    public void Compute_UnreachableAreaStaysAtInfinity()
    {
        // Two separate 2x2 rooms with no opening between them.
        var walls = new List<Segment>
        {
            new(new WorldPoint(0, 0), new WorldPoint(2, 0)),
            new(new WorldPoint(2, 0), new WorldPoint(2, 2)),
            new(new WorldPoint(2, 2), new WorldPoint(0, 2)),
            new(new WorldPoint(0, 2), new WorldPoint(0, 0)),

            new(new WorldPoint(4, 0), new WorldPoint(6, 0)),
            new(new WorldPoint(6, 0), new WorldPoint(6, 2)),
            new(new WorldPoint(6, 2), new WorldPoint(4, 2)),
            new(new WorldPoint(4, 2), new WorldPoint(4, 0)),
        };

        var grid = WalkabilityGrid.Build(walls, cellSize: 0.25, marginCells: 1);
        var exit = grid.NearestWalkableCell(new WorldPoint(1, 1))!.Value;
        var farRoomCell = grid.WorldToCell(new WorldPoint(5, 1));

        var result = DistanceMapCalculator.Compute(grid, new[] { exit });

        Assert.True(double.IsPositiveInfinity(result.Distances[farRoomCell.Row, farRoomCell.Col]));
    }
}
