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
        Assert.All(path.Zip(path.Skip(1)), segment =>
            Assert.True(grid.HasLineOfSight(segment.First, segment.Second)));
        Assert.Equal(PathLength(path),
            DistanceMapCalculator.GetDistanceAt(grid, result, path[0])!.Value,
            precision: 10);
    }

    [Fact]
    public void GetPath_OpenSpaceUsesClickedPointAndActualStraightLineLength()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 10, 10), cellSize: 0.5, marginCells: 0);
        var exitPoint = new WorldPoint(1.2, 2.3);
        var exitCell = grid.WorldToCell(exitPoint);
        var query = new WorldPoint(8.2, 7.3);
        var result = DistanceMapCalculator.Compute(grid,
            [new DistanceSource(exitCell.Col, exitCell.Row, exitPoint)]);

        var path = DistanceMapCalculator.GetPath(grid, result, query)!;

        var walkingPath = DistanceMapCalculator.FindPath(grid, result, query)!;
        Assert.Equal(query, walkingPath.Start);
        Assert.Equal(exitPoint, walkingPath.Arrival);
        Assert.Equal(query, path[0]);
        Assert.Equal(exitPoint, path[^1]);
        Assert.Equal(2, path.Count);
        Assert.Equal(PathLength(path),
            DistanceMapCalculator.GetDistanceAt(grid, result, query)!.Value,
            precision: 10);
        var farthest = result.FarthestCell!.Value;
        var farthestPath = DistanceMapCalculator.GetPath(
            grid,
            result,
            grid.CellCenter(farthest.Col, farthest.Row))!;
        Assert.Equal(PathLength(farthestPath), result.MaxDistance, precision: 10);
    }

    [Fact]
    public void GetPath_EndsAtVisiblePointOnExitSegment()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 10, 10), cellSize: 0.5, marginCells: 0);
        var exit = new Segment(new WorldPoint(1, 4), new WorldPoint(1, 6));
        var sources = grid.WalkableSourcesNearSegment(exit, grid.CellSize);
        Assert.All(sources, source => Assert.True(grid.HasLineOfSight(
            grid.CellCenter(source.Col, source.Row),
            source.ExitPoint)));
        Assert.All(sources, source => Assert.Null(source.ArrivalPoint));
        var result = DistanceMapCalculator.Compute(grid, sources);

        var path = DistanceMapCalculator.GetPath(grid, result, new WorldPoint(8.2, 5.3))!;
        var contact = path[^1];

        Assert.Equal(1, contact.X, precision: 10);
        Assert.InRange(contact.Y, exit.Start.Y, exit.End.Y);
        Assert.True(grid.HasLineOfSight(path[^2], contact));
    }

    [Fact]
    public void GetPath_BodyRouteEndsTangentToTheExit()
    {
        const double clearanceRadius = 0.2;
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 10, 10), cellSize: 0.05, marginCells: 0,
            clearanceRadius: clearanceRadius);
        var exit = new Segment(new WorldPoint(4, 0), new WorldPoint(6, 0));
        var sources = grid.WalkableSourcesNearSegment(exit, grid.CellSize, exitGroupId: 1);

        Assert.NotEmpty(sources);
        Assert.All(sources, source =>
        {
            Assert.Equal(grid.CellCenter(source.Col, source.Row), source.ExitPoint);
            Assert.Equal(exit, source.ExitSegment);
            Assert.True(source.ArrivalPoint is { } arrival);
            Assert.Equal(clearanceRadius, DistanceToSegment(source.ArrivalPoint!.Value, exit), precision: 10);
            Assert.InRange(source.ArrivalPoint!.Value.X, exit.Start.X, exit.End.X);
        });

        var query = new WorldPoint(5, 5);
        var result = DistanceMapCalculator.Compute(grid, sources);
        var walkingPath = DistanceMapCalculator.FindPath(grid, result, query)!;
        var path = walkingPath.Points;

        Assert.Equal(query, walkingPath.Start);
        Assert.Equal(clearanceRadius, DistanceToSegment(walkingPath.Arrival, exit), precision: 10);
        Assert.InRange(walkingPath.Arrival.X, exit.Start.X, exit.End.X);
        Assert.Equal(walkingPath.Arrival, path[^1]);
        Assert.Equal(walkingPath.Distance,
            DistanceMapCalculator.GetDistanceAt(grid, result, query)!.Value, precision: 10);

        var farthest = result.FarthestCell!.Value;
        var farthestPath = DistanceMapCalculator.FindPath(
            grid, result, grid.CellCenter(farthest.Col, farthest.Row))!;
        Assert.Equal(grid.CellCenter(farthest.Col, farthest.Row), farthestPath.Start);
        Assert.Equal(clearanceRadius, DistanceToSegment(farthestPath.Arrival, exit), precision: 10);
    }

    [Fact]
    public void GetPath_BodyRouteDoesNotIntrudeOnAnAdjacentWall()
    {
        const double clearanceRadius = 0.2;
        var walls = Rectangle(0, 0, 10, 10);
        var adjacentWall = new Segment(new WorldPoint(5, 0), new WorldPoint(5, 0.05));
        walls.Add(adjacentWall);
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.05, marginCells: 0,
            clearanceRadius: clearanceRadius);
        var exit = new Segment(new WorldPoint(4, 0), new WorldPoint(6, 0));
        var sources = grid.WalkableSourcesNearSegment(exit, grid.CellSize, exitGroupId: 1);

        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.True(source.ArrivalPoint is { } arrival &&
            DistanceToSegment(arrival, adjacentWall) >= clearanceRadius - 1e-10));

        var path = DistanceMapCalculator.FindPath(
            grid, DistanceMapCalculator.Compute(grid, sources), new WorldPoint(4.5, 5));

        Assert.NotNull(path);
        Assert.True(DistanceToSegment(path!.Arrival, adjacentWall) >= clearanceRadius - 1e-10);
        Assert.Equal(clearanceRadius, DistanceToSegment(path.Arrival, exit), precision: 10);
    }

    [Fact]
    public void GetPath_BodyRouteHasNoArrivalWhenExitIsTooNarrow()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 10, 10), cellSize: 0.05, marginCells: 0,
            clearanceRadius: 0.2);
        var narrowExit = new Segment(new WorldPoint(4.9, 0), new WorldPoint(5.1, 0));
        var sources = grid.WalkableSourcesNearSegment(narrowExit, grid.CellSize, exitGroupId: 1);

        Assert.Empty(sources);
        Assert.Null(DistanceMapCalculator.GetPath(
            grid, DistanceMapCalculator.Compute(grid, sources), new WorldPoint(5, 5)));
    }

    [Fact]
    public void HasLineOfSight_RejectsWallCellsAndBlockedDiagonalCorners()
    {
        var walls = new List<Segment>
        {
            new(new WorldPoint(1, 0), new WorldPoint(1, 0)),
            new(new WorldPoint(0, 1), new WorldPoint(0, 1)),
            new(new WorldPoint(3, 3), new WorldPoint(3, 3)),
        };
        var grid = WalkabilityGrid.Build(walls, cellSize: 1, marginCells: 0);
        var start = grid.CellCenter(0, 0);
        var contactBeyondWall = grid.CellCenter(2, 0);

        Assert.False(grid.HasLineOfSight(start, contactBeyondWall));
        Assert.False(grid.HasLineOfSight(start, grid.CellCenter(1, 1)));
        Assert.False(grid.HasLineOfSightToExit(
            start,
            contactBeyondWall,
            new Segment(contactBeyondWall, contactBeyondWall)));
        var invalidSource = DistanceMapCalculator.Compute(
            grid,
            [new DistanceSource(0, 0, contactBeyondWall)]);
        Assert.True(double.IsPositiveInfinity(invalidSource.Distances[0, 0]));
    }

    [Fact]
    public void Compute_ChoosesSourceByTheSameAnyAngleMetricAsTheDisplayedPath()
    {
        var grid = GridFromBlocked(
        [
            (1, 0), (4, 0), (3, 1), (4, 1), (0, 2), (1, 2), (6, 2), (2, 3),
            (1, 4), (6, 4), (2, 5), (5, 6), (7, 6), (9, 7), (2, 8), (3, 8),
            (2, 9),
        ]);
        var source0 = (Col: 2, Row: 2);
        var source1 = (Col: 9, Row: 2);
        var query = grid.CellCenter(6, 5);
        var fromSource0 = DistanceMapCalculator.FindPath(
            grid,
            DistanceMapCalculator.Compute(grid, [source0]),
            query)!;
        var fromSource1 = DistanceMapCalculator.FindPath(
            grid,
            DistanceMapCalculator.Compute(grid, [source1]),
            query)!;

        var combined = DistanceMapCalculator.Compute(grid, [source0, source1]);
        var selected = DistanceMapCalculator.FindPath(grid, combined, query)!;

        Assert.True(fromSource1.Distance < fromSource0.Distance);
        Assert.Equal(grid.CellCenter(source1.Col, source1.Row), selected.Points[^1]);
        Assert.Equal(fromSource1.Distance, selected.Distance, precision: 10);
        Assert.Equal(selected.Distance, combined.Distances[5, 6], precision: 10);
    }

    [Fact]
    public void Compute_MultipleLegacySourcesComposeTheirIndependentThetaFields()
    {
        var grid = GridFromBlocked(
        [
            (0, 0), (9, 2), (4, 3), (7, 3), (9, 3), (1, 4), (8, 5),
            (1, 6), (4, 7), (7, 8), (0, 9), (2, 9), (9, 9),
        ]);
        var sourceA = (Col: 2, Row: 2);
        var sourceB = (Col: 9, Row: 2);
        var query = grid.CellCenter(6, 8);
        var fromA = DistanceMapCalculator.FindPath(
            grid,
            DistanceMapCalculator.Compute(grid, [sourceA]),
            query)!;
        var fromB = DistanceMapCalculator.FindPath(
            grid,
            DistanceMapCalculator.Compute(grid, [sourceB]),
            query)!;

        Assert.Equal(7.21110255, fromA.Distance, precision: 8);
        Assert.Equal(7.33508749, fromB.Distance, precision: 8);
        foreach (var sources in new[]
                 {
                     new[] { sourceA, sourceB },
                     new[] { sourceB, sourceA },
                 })
        {
            var combined = DistanceMapCalculator.Compute(grid, sources);
            var selected = DistanceMapCalculator.FindPath(grid, combined, query)!;

            Assert.Equal(grid.CellCenter(sourceA.Col, sourceA.Row), selected.Points[^1]);
            Assert.Equal(fromA.Distance, selected.Distance, precision: 10);
            Assert.Equal(fromA.Distance, combined.Distances[8, 6], precision: 10);
        }
    }

    [Fact]
    public void FindPath_ChoosesNearestExitGroupFromTheActualPoint()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 1, 1), cellSize: 0.1, marginCells: 0);
        var exitA = new WorldPoint(0.22, 0.55);
        var exitB = new WorldPoint(0.45, 0.55);
        var query = new WorldPoint(0.31, 0.55);
        DistanceSource Source(WorldPoint exit, int groupId)
        {
            var cell = grid.WorldToCell(exit);
            return new DistanceSource(cell.Col, cell.Row, exit, new Segment(exit, exit), groupId);
        }

        var sourceA = Source(exitA, 1);
        var sourceB = Source(exitB, 2);
        Assert.Equal(0.09, DistanceMapCalculator.FindPath(
            grid, DistanceMapCalculator.Compute(grid, [sourceA]), query)!.Distance, precision: 10);
        Assert.Equal(0.14, DistanceMapCalculator.FindPath(
            grid, DistanceMapCalculator.Compute(grid, [sourceB]), query)!.Distance, precision: 10);

        foreach (var sources in new[]
                 {
                     new[] { sourceA, sourceB },
                     new[] { sourceB, sourceA },
                 })
        {
            var path = DistanceMapCalculator.FindPath(
                grid, DistanceMapCalculator.Compute(grid, sources), query)!;

            Assert.Equal(exitA, path.Points[^1]);
            Assert.Equal(0.09, path.Distance, precision: 10);
        }
    }

    [Fact]
    public void Compute_FarthestCellAndMaximumUseEveryCellsAnyAnglePathMetric()
    {
        var grid = GridFromBlocked(
        [
            (8, 0), (0, 1), (2, 1), (6, 1), (8, 1), (2, 2), (4, 2), (9, 2),
            (0, 3), (0, 4), (1, 4), (3, 4), (4, 4), (5, 4), (7, 4), (2, 5),
            (3, 5), (0, 6), (4, 6), (9, 6), (5, 7), (6, 7), (8, 7), (3, 8),
            (5, 9), (8, 9),
        ]);
        var result = DistanceMapCalculator.Compute(grid, [(Col: 2, Row: 2)]);
        (int Col, int Row)? expectedFarthest = null;
        double expectedMaximum = 0;

        for (int row = 0; row < grid.Rows; row++)
        {
            for (int col = 0; col < grid.Cols; col++)
            {
                double distance = result.Distances[row, col];
                if (grid.IsBlocked(col, row) || !double.IsFinite(distance))
                {
                    continue;
                }

                var path = DistanceMapCalculator.FindPath(grid, result, grid.CellCenter(col, row));
                Assert.NotNull(path);
                Assert.Equal(path!.Distance, distance, precision: 10);
                if (expectedFarthest is null || path.Distance > expectedMaximum)
                {
                    expectedFarthest = (col, row);
                    expectedMaximum = path.Distance;
                }
            }
        }

        Assert.Equal(expectedFarthest, result.FarthestCell);
        Assert.Equal(expectedMaximum, result.MaxDistance, precision: 10);
        Assert.All(
            result.Distances.Cast<double>().Where(double.IsFinite),
            distance => Assert.True(distance <= result.MaxDistance));
    }

    [Fact]
    public void WallLineExit_ConnectsInteriorSideAndKeepsTheExitContact()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 10, 10), cellSize: 0.5, marginCells: 1);
        var exit = new Segment(new WorldPoint(4, 0), new WorldPoint(6, 0));
        var sources = grid.WalkableSourcesNearSegment(exit, grid.CellSize, exitGroupId: 7);
        int wallRow = grid.WorldToCell(new WorldPoint(5, 0)).Row;

        Assert.All(sources, source => Assert.Equal(7, source.ExitGroupId));
        Assert.Contains(sources, source => source.Row > wallRow);
        Assert.DoesNotContain(sources, source => source.Row < wallRow);
        var result = DistanceMapCalculator.Compute(grid, sources);

        foreach (var query in new[] { new WorldPoint(5, 5) })
        {
            var path = DistanceMapCalculator.FindPath(grid, result, query);

            Assert.NotNull(path);
            var contact = path!.Points[^1];
            Assert.Equal(0, contact.Y, precision: 10);
            Assert.InRange(contact.X, exit.Start.X, exit.End.X);
            Assert.True(grid.HasLineOfSightToExit(path.Points[^2], contact, exit));
            Assert.False(grid.HasLineOfSight(path.Points[^2], contact));
        }
    }

    [Fact]
    public void WallLineExit_DoesNotIgnoreAnotherWallInTheTerminalCell()
    {
        var walls = Rectangle(0, 0, 10, 10);
        walls.Add(new Segment(new WorldPoint(4, 0.02), new WorldPoint(6, 0.02)));
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.1, marginCells: 1);
        var exit = new Segment(new WorldPoint(4, 0), new WorldPoint(6, 0));
        var sources = grid.WalkableSourcesNearSegment(exit, grid.CellSize);
        var result = DistanceMapCalculator.Compute(grid, sources);

        Assert.Empty(sources);
        Assert.False(grid.HasLineOfSightToExit(
            new WorldPoint(5, 5),
            new WorldPoint(5, 0),
            exit));
        Assert.Null(DistanceMapCalculator.FindPath(grid, result, new WorldPoint(5, 5)));
        Assert.Null(DistanceMapCalculator.FindPath(grid, result, new WorldPoint(5, -0.05)));
    }

    [Fact]
    public void CircularWallFixedExit_UsesBothSidesOfTheDisplayedExitAsDistanceSources()
    {
        const int sideCount = 64;
        var walls = Enumerable.Range(0, sideCount)
            .Select(i => new Segment(
                CirclePoint(i),
                CirclePoint((i + 1) % sideCount)))
            .ToArray();
        var wallIndex = WallIndex.Build(walls);
        var exitPath = wallIndex.TraceFixedLengthFromMidpoint(
            new WorldPoint(10, 0), length: 8, tolerance: 0.1);
        var grid = WalkabilityGrid.Build(walls, cellSize: 0.25, marginCells: 1);
        var sources = exitPath.Zip(exitPath.Skip(1), (start, end) => new Segment(start, end))
            .SelectMany(segment => grid.WalkableSourcesNearSegment(segment, grid.CellSize, exitGroupId: 0))
            .ToList();

        Assert.Contains(sources, source => source.ExitPoint.Y > 0.5);
        Assert.Contains(sources, source => source.ExitPoint.Y < -0.5);

        var path = DistanceMapCalculator.FindPath(
            grid,
            DistanceMapCalculator.Compute(grid, sources),
            new WorldPoint(8, -1.5));

        Assert.NotNull(path);
        Assert.True(path!.Points[^1].Y < -0.5);

        static WorldPoint CirclePoint(int index)
        {
            double angle = 2 * Math.PI * index / sideCount;
            return new WorldPoint(10 * Math.Cos(angle), 10 * Math.Sin(angle));
        }
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

    private static WalkabilityGrid GridFromBlocked(IReadOnlyList<(int Col, int Row)> blocked)
    {
        var walls = blocked
            .Select(cell => new Segment(
                new WorldPoint(cell.Col, cell.Row),
                new WorldPoint(cell.Col, cell.Row)))
            .ToList();
        walls.AddRange(Rectangle(-1, -1, 10, 10));
        return WalkabilityGrid.Build(walls, cellSize: 1, marginCells: 0);
    }

    private static double PathLength(IReadOnlyList<WorldPoint> path) =>
        path.Zip(path.Skip(1)).Sum(segment => Math.Sqrt(
            Math.Pow(segment.Second.X - segment.First.X, 2) +
            Math.Pow(segment.Second.Y - segment.First.Y, 2)));

    private static double DistanceToSegment(WorldPoint point, Segment segment)
    {
        double dx = segment.End.X - segment.Start.X;
        double dy = segment.End.Y - segment.Start.Y;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared == 0 ? 0 : Math.Clamp(
            ((point.X - segment.Start.X) * dx + (point.Y - segment.Start.Y) * dy) / lengthSquared,
            0,
            1);
        double nearestX = segment.Start.X + dx * t;
        double nearestY = segment.Start.Y + dy * t;
        return Math.Sqrt(Math.Pow(point.X - nearestX, 2) + Math.Pow(point.Y - nearestY, 2));
    }
}
