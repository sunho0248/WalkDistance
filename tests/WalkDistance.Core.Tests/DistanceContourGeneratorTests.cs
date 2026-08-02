using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class DistanceContourGeneratorTests
{
    [Fact]
    public void Generate_ProducesOneMeterContoursOnlyAcrossWalkableFiniteCells()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 4, 4), cellSize: 1, marginCells: 0);
        var distances = new double[grid.Rows, grid.Cols];
        for (int row = 0; row < grid.Rows; row++)
        for (int col = 0; col < grid.Cols; col++)
            distances[row, col] = grid.IsWalkable(col, row) ? col : double.PositiveInfinity;

        var contours = DistanceContourGenerator.Generate(grid, distances);

        Assert.NotEmpty(contours);
        Assert.All(contours, contour =>
        {
            Assert.Equal(Math.Round(contour.Level), contour.Level);
            Assert.True(contour.Level >= 1);
            Assert.NotEqual(contour.Start, contour.End);
        });
        Assert.Contains(contours, contour => contour.Level == 1);
        Assert.Contains(contours, contour => contour.Level == 2);
    }

    [Fact]
    public void Generate_OptionalThresholdAddsThatExactContour()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 4, 4), cellSize: 1, marginCells: 0);
        var distances = new double[grid.Rows, grid.Cols];
        for (int row = 0; row < grid.Rows; row++)
        for (int col = 0; col < grid.Cols; col++)
            distances[row, col] = grid.IsWalkable(col, row) ? col : double.PositiveInfinity;

        var contours = DistanceContourGenerator.Generate(grid, distances, threshold: 1.5);

        Assert.Contains(contours, contour => contour.Level == 1.5 && contour.IsThreshold);
        Assert.DoesNotContain(contours, contour => contour.IsThreshold && contour.Level != 1.5);
    }

    [Fact]
    public void Generate_IntegerThresholdKeepsNormalAndDistinctThresholdContours()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 40, 4), cellSize: 1, marginCells: 0);
        var distances = Gradient(grid, col => col);

        var contours = DistanceContourGenerator.Generate(grid, distances, threshold: 10);

        Assert.Contains(contours, contour => contour.Level == 10 && !contour.IsThreshold);
        Assert.Contains(contours, contour => contour.Level == 10 && contour.IsThreshold);
    }

    [Fact]
    public void SeparateApis_GenerateNormalAndThresholdGeometryIndependently()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 40, 4), cellSize: 1, marginCells: 0);
        var distances = Gradient(grid, col => col);

        var normal = DistanceContourGenerator.Generate(grid, distances);
        var threshold = DistanceContourGenerator.GenerateThreshold(grid, distances, 10);

        Assert.Contains(normal, contour => contour.Level == 10 && !contour.IsThreshold);
        Assert.All(threshold, contour => Assert.True(contour.IsThreshold));
        Assert.Contains(threshold, contour => contour.Level == 10);
    }

    [Fact]
    public void Generate_LargeGridUsesBroadlyBoundedAllocations()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 500, 500), cellSize: 1, marginCells: 0);
        var distances = Gradient(grid, col => col * 0.1);
        _ = DistanceContourGenerator.Generate(grid, distances);
        long before = GC.GetAllocatedBytesForCurrentThread();

        var contours = DistanceContourGenerator.Generate(grid, distances);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotEmpty(contours);
        Assert.True(allocated < 32_000_000, $"Allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Generate_HugeFiniteDistancesOnlyEnumeratesLocallyCrossedLevels()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 4, 4), cellSize: 1, marginCells: 0);
        var distances = Gradient(grid, col => 1_000_000_000d + col);

        var contours = DistanceContourGenerator.Generate(grid, distances);

        Assert.NotEmpty(contours);
        Assert.All(contours, contour => Assert.InRange(contour.Level, 1_000_000_001, 1_000_000_003));
    }

    [Fact]
    public void Generate_IgnoresFiniteValuesOutsideWalkableInterior()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 4, 4), cellSize: 1, marginCells: 1);
        var distances = Gradient(grid, col => col);
        distances[0, 0] = double.MaxValue;

        var contours = DistanceContourGenerator.Generate(grid, distances);

        Assert.All(contours, contour => Assert.True(contour.Level < grid.Cols));
    }

    [Fact]
    public void Generate_RejectsAnUnsafeNumberOfLevelsWithinOneCell()
    {
        var grid = WalkabilityGrid.Build(Rectangle(0, 0, 4, 4), cellSize: 1, marginCells: 0);
        var distances = Gradient(grid, col => col == 1 ? 0 : 2_000_000);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DistanceContourGenerator.Generate(grid, distances));
    }

    private static double[,] Gradient(WalkabilityGrid grid, Func<int, double> value)
    {
        var distances = new double[grid.Rows, grid.Cols];
        for (int row = 0; row < grid.Rows; row++)
        for (int col = 0; col < grid.Cols; col++)
            distances[row, col] = grid.IsWalkable(col, row) ? value(col) : double.PositiveInfinity;
        return distances;
    }

    private static List<Segment> Rectangle(double minX, double minY, double maxX, double maxY) =>
    [
        new(new WorldPoint(minX, minY), new WorldPoint(maxX, minY)),
        new(new WorldPoint(maxX, minY), new WorldPoint(maxX, maxY)),
        new(new WorldPoint(maxX, maxY), new WorldPoint(minX, maxY)),
        new(new WorldPoint(minX, maxY), new WorldPoint(minX, minY)),
    ];
}
