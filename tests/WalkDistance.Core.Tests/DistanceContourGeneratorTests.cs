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

    private static List<Segment> Rectangle(double minX, double minY, double maxX, double maxY) =>
    [
        new(new WorldPoint(minX, minY), new WorldPoint(maxX, minY)),
        new(new WorldPoint(maxX, minY), new WorldPoint(maxX, maxY)),
        new(new WorldPoint(maxX, maxY), new WorldPoint(minX, maxY)),
        new(new WorldPoint(minX, maxY), new WorldPoint(minX, minY)),
    ];
}
