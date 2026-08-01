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
        Assert.True(result.UnreachableCellCount > 0);
        Assert.True(double.IsFinite(result.MaxDistance));
    }

    [Fact]
    public void Compute_MultipleExitsUseTheNearestSource()
    {
        var walls = Rectangle(0, 0, 10, 4);
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.5, marginCells: 0);
        var leftExit = grid.NearestWalkableCell(new WorldPoint(1, 2))!.Value;
        var rightExit = grid.NearestWalkableCell(new WorldPoint(9, 2))!.Value;
        var target = grid.WorldToCell(new WorldPoint(8, 2));

        var fromLeft = DistanceMapCalculator.Compute(grid, new[] { leftExit });
        var fromBoth = DistanceMapCalculator.Compute(grid, new[] { leftExit, rightExit });

        Assert.True(fromBoth.Distances[target.Row, target.Col] < fromLeft.Distances[target.Row, target.Col]);
    }

    [Fact]
    public void Compute_DoesNotCutBlockedDiagonalCorner()
    {
        var walls = new List<Segment>
        {
            new(new WorldPoint(1, 0), new WorldPoint(1, 0)),
            new(new WorldPoint(0, 1), new WorldPoint(0, 1)),
        };
        var grid = WalkabilityGrid.Build(walls, cellSize: 1, marginCells: 0);

        var result = DistanceMapCalculator.Compute(grid, new[] { (Col: 0, Row: 0) });

        Assert.True(double.IsPositiveInfinity(result.Distances[1, 1]));
    }

    [Fact]
    public void GetDistanceAt_ReturnsDistanceOrNullForUnreachablePoint()
    {
        var walls = new List<Segment>();
        walls.AddRange(Rectangle(0, 0, 2, 2));
        walls.AddRange(Rectangle(4, 0, 6, 2));
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.25, marginCells: 1);
        var exit = grid.NearestWalkableCell(new WorldPoint(1, 1))!.Value;
        var result = DistanceMapCalculator.Compute(grid, new[] { exit });

        Assert.NotNull(DistanceMapCalculator.GetDistanceAt(grid, result, new WorldPoint(1.5, 1)));
        Assert.Null(DistanceMapCalculator.GetDistanceAt(grid, result, new WorldPoint(5, 1)));
    }

    [Fact]
    public void GetPath_DetoursThroughGapFromQueryToSource()
    {
        var walls = Rectangle(0, 0, 10, 10);
        walls.Add(new Segment(new WorldPoint(5, 0), new WorldPoint(5, 8)));
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.5, marginCells: 1);
        var source = grid.NearestWalkableCell(new WorldPoint(1, 5))!.Value;
        var query = grid.WorldToCell(new WorldPoint(9, 5));
        var result = DistanceMapCalculator.Compute(grid, new[] { source });

        var path = DistanceMapCalculator.GetPath(grid, result, grid.CellCenter(query.Col, query.Row));

        Assert.NotNull(path);
        Assert.Equal(grid.CellCenter(query.Col, query.Row), path![0]);
        Assert.Equal(grid.CellCenter(source.Col, source.Row), path[^1]);
        Assert.Contains(path, point => point.Y > 8);
    }

    [Fact]
    public void GetPath_SourceCellReturnsOnePoint()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 4, 4), cellSize: 0.5, marginCells: 0);
        var source = grid.NearestWalkableCell(new WorldPoint(1, 1))!.Value;
        var result = DistanceMapCalculator.Compute(grid, new[] { source });

        var path = DistanceMapCalculator.GetPath(grid, result, grid.CellCenter(source.Col, source.Row));

        Assert.Equal(grid.CellCenter(source.Col, source.Row), Assert.Single(path!));
    }

    [Fact]
    public void GetPath_ReturnsNullForBlockedOutOfBoundsAndUnreachablePoints()
    {
        var walls = Rectangle(0, 0, 2, 2);
        walls.AddRange(Rectangle(4, 0, 6, 2));
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.25, marginCells: 1);
        var source = grid.NearestWalkableCell(new WorldPoint(1, 1))!.Value;
        var result = DistanceMapCalculator.Compute(grid, new[] { source });

        Assert.Null(DistanceMapCalculator.GetPath(grid, result, new WorldPoint(0, 0)));
        Assert.Null(DistanceMapCalculator.GetPath(grid, result, new WorldPoint(-100, -100)));
        Assert.Null(DistanceMapCalculator.GetPath(grid, result, new WorldPoint(5, 1)));
    }

    private static List<Segment> Rectangle(double minX, double minY, double maxX, double maxY)
    {
        return
        [
            new(new WorldPoint(minX, minY), new WorldPoint(maxX, minY)),
            new(new WorldPoint(maxX, minY), new WorldPoint(maxX, maxY)),
            new(new WorldPoint(maxX, maxY), new WorldPoint(minX, maxY)),
            new(new WorldPoint(minX, maxY), new WorldPoint(minX, minY)),
        ];
    }
}
