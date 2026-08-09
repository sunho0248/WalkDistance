using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class WalkabilityGridTests
{
    [Fact]
    public void KoreanAdultProfile_UsesShoulderWidthForClearanceRadius()
    {
        var profile = BodyProfile.KoreanAdult;

        Assert.Equal(0.40, profile.ShoulderWidth);
        Assert.Equal(0.20, profile.ClearanceRadius, precision: 10);
    }

    [Fact]
    public void NarrowCorridor_IsPresentInTheGeometricGridButUnavailableToTheBodyGrid()
    {
        var walls = Rectangle(0, 0, 4, 4);
        walls.Add(new Segment(new WorldPoint(2, 0), new WorldPoint(2, 1.8)));
        walls.Add(new Segment(new WorldPoint(2, 2.1), new WorldPoint(2, 4)));

        var geometricGrid = WalkabilityGrid.Build(walls, cellSize: 0.05, marginCells: 2);
        var bodyGrid = WalkabilityGrid.Build(walls, cellSize: 0.05, marginCells: 2,
            clearanceRadius: BodyProfile.KoreanAdult.ClearanceRadius);

        var gap = geometricGrid.WorldToCell(new WorldPoint(2, 1.95));
        Assert.True(geometricGrid.IsWalkable(gap.Col, gap.Row));
        Assert.False(bodyGrid.IsWalkable(gap.Col, gap.Row));

        var exit = new Segment(new WorldPoint(0, 1), new WorldPoint(0, 3));
        var geometricResult = DistanceMapCalculator.Compute(
            geometricGrid, geometricGrid.WalkableSourcesNearSegment(exit, geometricGrid.CellSize));
        var bodyResult = DistanceMapCalculator.Compute(
            bodyGrid, bodyGrid.WalkableSourcesNearSegment(exit, bodyGrid.CellSize));
        var target = new WorldPoint(3, 2);

        Assert.NotNull(DistanceMapCalculator.FindPath(geometricGrid, geometricResult, target));
        Assert.Null(DistanceMapCalculator.FindPath(bodyGrid, bodyResult, target));
    }

    [Fact]
    public void BodyProfileChanges_DoNotChangeGeometricDistances()
    {
        var walls = Rectangle(0, 0, 4, 4);
        var exit = new Segment(new WorldPoint(1, 0), new WorldPoint(3, 0));
        var geometricGrid = WalkabilityGrid.Build(walls, cellSize: 0.1, marginCells: 2);
        var sources = geometricGrid.WalkableSourcesNearSegment(exit, geometricGrid.CellSize);
        var beforeBodyEdit = DistanceMapCalculator.Compute(geometricGrid, sources);

        var narrowBodyGrid = WalkabilityGrid.Build(walls, cellSize: 0.1, marginCells: 2,
            clearanceRadius: new BodyProfile(0.4).ClearanceRadius);
        var wideBodyGrid = WalkabilityGrid.Build(walls, cellSize: 0.1, marginCells: 2,
            clearanceRadius: new BodyProfile(0.8).ClearanceRadius);
        var afterBodyEdit = DistanceMapCalculator.Compute(geometricGrid, sources);

        Assert.NotEqual(narrowBodyGrid.ClearanceRadius, wideBodyGrid.ClearanceRadius);
        Assert.Equal(beforeBodyEdit.Distances.Cast<double>(), afterBodyEdit.Distances.Cast<double>());
    }

    [Fact]
    public void WalkableSourcesNearSegment_RejectsExitNarrowerThanTheConfiguredBody()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 4, 4), cellSize: 0.05, marginCells: 2,
            clearanceRadius: BodyProfile.KoreanAdult.ClearanceRadius);
        var narrowExit = new Segment(new WorldPoint(1.8, 0), new WorldPoint(2.1, 0));
        var wideExit = new Segment(new WorldPoint(1, 0), new WorldPoint(3, 0));

        Assert.Empty(grid.WalkableSourcesNearSegment(narrowExit, grid.CellSize));
        Assert.NotEmpty(grid.WalkableSourcesNearSegment(wideExit, grid.CellSize));
    }

    [Fact]
    public void WalkableSourcesNearSegment_PreservesLegacyPointExitWithClearance()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 4, 4), cellSize: 0.05, marginCells: 2,
            clearanceRadius: BodyProfile.KoreanAdult.ClearanceRadius);
        var pointExit = new Segment(new WorldPoint(2, 2), new WorldPoint(2, 2));

        var sources = grid.WalkableSourcesNearSegment(pointExit, grid.CellSize);

        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.True(grid.IsWalkable(source.Col, source.Row)));
    }

    [Fact]
    public void Build_ClassifiesOnlyClosedInteriorAsWalkable()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 10, 10), cellSize: 0.5, marginCells: 2);

        var inside = grid.WorldToCell(new WorldPoint(5, 5));
        var outside = grid.WorldToCell(new WorldPoint(-0.5, 5));

        Assert.True(grid.IsWalkable(inside.Col, inside.Row));
        Assert.False(grid.IsWalkable(outside.Col, outside.Row));
    }

    [Fact]
    public void Build_OpenOutlineHasNoWalkableInterior()
    {
        var walls = Rectangle(0, 0, 10, 10);
        walls.RemoveAt(3);
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.5, marginCells: 2);

        Assert.False(grid.IsWalkable(grid.WorldToCell(new WorldPoint(5, 5))));
        Assert.Null(grid.NearestWalkableCell(new WorldPoint(5, 5)));
    }

    [Fact]
    public void ExitSourcesExcludeExteriorSide()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 10, 10), cellSize: 0.25, marginCells: 2);
        var exit = new Segment(new WorldPoint(4, 0), new WorldPoint(6, 0));

        var sources = grid.WalkableSourcesNearSegment(exit, grid.CellSize);

        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.True(grid.IsWalkable(source.Col, source.Row)));
        Assert.All(sources, source => Assert.True(grid.CellCenter(source.Col, source.Row).Y > 0));
    }

    [Fact]
    public void HasLineOfSight_ConcaveShortcutCannotCrossExterior()
    {
        Segment[] outline =
        [
            new(new(0, 0), new(6, 0)), new(new(6, 0), new(6, 6)),
            new(new(6, 6), new(4, 6)), new(new(4, 6), new(4, 2)),
            new(new(4, 2), new(2, 2)), new(new(2, 2), new(2, 6)),
            new(new(2, 6), new(0, 6)), new(new(0, 6), new(0, 0)),
        ];
        var grid = WalkabilityGrid.Build(outline, cellSize: 0.25, marginCells: 2);

        Assert.True(grid.IsWalkable(grid.WorldToCell(new WorldPoint(1, 5))));
        Assert.True(grid.IsWalkable(grid.WorldToCell(new WorldPoint(5, 5))));
        Assert.False(grid.HasLineOfSight(new WorldPoint(1, 5), new WorldPoint(5, 5)));
    }

    [Fact]
    public void BoundaryClassification_AcceptsInsideAndRejectsJustOutside()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 10, 10), cellSize: 0.05, marginCells: 2);

        Assert.True(grid.IsWalkable(grid.WorldToCell(new WorldPoint(5, 0.075))));
        Assert.False(grid.IsWalkable(grid.WorldToCell(new WorldPoint(5, -0.025))));
    }

    [Fact]
    public void Build_TwoClosedOutlinesKeepBothInteriorsAndRejectExteriorGap()
    {
        var walls = Rectangle(0, 0, 2, 2);
        walls.AddRange(Rectangle(4, 0, 6, 2));
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.25, marginCells: 2);

        Assert.True(grid.IsWalkable(grid.WorldToCell(new WorldPoint(1, 1))));
        Assert.True(grid.IsWalkable(grid.WorldToCell(new WorldPoint(5, 1))));
        Assert.False(grid.IsWalkable(grid.WorldToCell(new WorldPoint(3, 1))));
    }

    [Fact]
    public void Build_InternalPartitionKeepsFreeCellsOnBothSidesInterior()
    {
        var walls = Rectangle(0, 0, 10, 10);
        walls.Add(new Segment(new WorldPoint(5, 0), new WorldPoint(5, 8)));
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.25, marginCells: 2);

        Assert.True(grid.IsWalkable(grid.WorldToCell(new WorldPoint(2, 5))));
        Assert.True(grid.IsWalkable(grid.WorldToCell(new WorldPoint(8, 5))));
        var partition = grid.WorldToCell(new WorldPoint(5, 5));
        Assert.True(grid.IsBlocked(partition.Col, partition.Row));
    }

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
        var walls = Rectangle(0, 0, 10, 10);
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.5, marginCells: 4);

        var cell = grid.NearestWalkableCell(new WorldPoint(5, 0));

        Assert.NotNull(cell);
        Assert.False(grid.IsBlocked(cell!.Value.Col, cell.Value.Row));
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
        var walls = Rectangle(0, 0, 10, 10);
        walls.AddRange(Enumerable.Range(3, 5)
            .Select(y => new Segment(new WorldPoint(0, y), new WorldPoint(10, y)))
            .ToList());
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

    [Fact]
    public void HasLineOfSightToExit_HandlesExitEndpointAndCollinearOverlap()
    {
        var grid = WalkabilityGrid.Build(
            [new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0))],
            cellSize: 1,
            marginCells: 1);
        var exit = new Segment(new WorldPoint(4.5, 0), new WorldPoint(6.5, 0));

        Assert.True(grid.HasLineOfSightToExit(
            new WorldPoint(4.75, 0.5),
            new WorldPoint(4.75, 0),
            exit));
        Assert.True(grid.HasLineOfSightToExit(
            new WorldPoint(4.55, 0),
            new WorldPoint(4.75, 0),
            exit));
        Assert.False(grid.HasLineOfSightToExit(
            new WorldPoint(4.25, 0),
            new WorldPoint(4.75, 0),
            exit));
    }

    [Fact]
    public void HasLineOfSightToExit_RejectsNonExitWallEndpointAtLargeCoordinates()
    {
        const double origin = 1_000_000_000;
        var exit = new Segment(
            new WorldPoint(origin + 4, origin),
            new WorldPoint(origin + 6, origin));
        var walls = Rectangle(origin, origin, origin + 10, origin + 10);
        var clearGrid = WalkabilityGrid.Build(walls, cellSize: 0.1, marginCells: 1);
        Assert.True(clearGrid.HasLineOfSightToExit(
            new WorldPoint(origin + 5, origin + 0.05),
            new WorldPoint(origin + 5, origin),
            exit));

        walls.Add(new Segment(
            new WorldPoint(origin + 5, origin + 0.02),
            new WorldPoint(origin + 6, origin + 0.02)));
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.1, marginCells: 1);

        Assert.False(grid.HasLineOfSightToExit(
            new WorldPoint(origin + 5, origin + 0.05),
            new WorldPoint(origin + 5, origin),
            exit));
    }

    [Fact]
    public void HasLineOfSightToExit_IgnoresDisjointWallInTheTerminalCell()
    {
        var walls = Rectangle(0, 0, 10, 10);
        walls.Add(new Segment(new WorldPoint(5.07, 0.02), new WorldPoint(5.09, 0.02)));
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.1, marginCells: 1);
        var exit = new Segment(new WorldPoint(4, 0), new WorldPoint(6, 0));

        Assert.True(grid.HasLineOfSightToExit(
            new WorldPoint(5.04, 0.05),
            new WorldPoint(5.04, 0),
            exit));
    }

    private static List<Segment> Rectangle(double minX, double minY, double maxX, double maxY) =>
    [
        new(new WorldPoint(minX, minY), new WorldPoint(maxX, minY)),
        new(new WorldPoint(maxX, minY), new WorldPoint(maxX, maxY)),
        new(new WorldPoint(maxX, maxY), new WorldPoint(minX, maxY)),
        new(new WorldPoint(minX, maxY), new WorldPoint(minX, minY)),
    ];
}
