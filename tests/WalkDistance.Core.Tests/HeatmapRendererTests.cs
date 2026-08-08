using System.Windows;
using WalkDistance.App;
using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class HeatmapRendererTests
{
    [Fact]
    public void Render_IncludesZeroDistanceSourceCellWhenMaximumIsZero()
    {
        var grid = WalkabilityGrid.Build(
        [
            new(new(0, 0), new(1, 0)),
            new(new(1, 0), new(1, 1)),
            new(new(1, 1), new(0, 1)),
            new(new(0, 1), new(0, 0)),
        ], cellSize: 0.25, marginCells: 1);
        var source = (from row in Enumerable.Range(0, grid.Rows)
                      from col in Enumerable.Range(0, grid.Cols)
                      where grid.IsWalkable(col, row)
                      select (Col: col, Row: row)).First();
        var distances = new double[grid.Rows, grid.Cols];
        for (int row = 0; row < grid.Rows; row++)
        for (int col = 0; col < grid.Cols; col++)
            distances[row, col] = double.PositiveInfinity;
        distances[source.Row, source.Col] = 0;

        var bitmap = HeatmapRenderer.Render(grid, distances, maxDistance: 0);

        Assert.Equal(170, Pixel(bitmap, grid, source)[3]);
    }

    [Fact]
    public void Render_UsesSolidFiveMeterBandsAndPreservesThresholdOverlay()
    {
        var grid = WalkabilityGrid.Build(
        [
            new(new(0, 0), new(4, 0)),
            new(new(4, 0), new(4, 4)),
            new(new(4, 4), new(0, 4)),
            new(new(0, 4), new(0, 0)),
        ], cellSize: 0.5, marginCells: 1);
        var cells = (from row in Enumerable.Range(0, grid.Rows)
                     from col in Enumerable.Range(0, grid.Cols)
                     where grid.IsWalkable(col, row)
                     select (Col: col, Row: row)).Take(7).ToArray();
        var distances = new double[grid.Rows, grid.Cols];
        for (int row = 0; row < grid.Rows; row++)
        for (int col = 0; col < grid.Cols; col++)
            distances[row, col] = double.PositiveInfinity;

        distances[cells[0].Row, cells[0].Col] = 0;
        distances[cells[1].Row, cells[1].Col] = 4.999;
        distances[cells[2].Row, cells[2].Col] = 5;
        distances[cells[3].Row, cells[3].Col] = 9.999;
        distances[cells[4].Row, cells[4].Col] = 10;
        distances[cells[5].Row, cells[5].Col] = 40;
        distances[cells[6].Row, cells[6].Col] = 45;

        var bitmap = HeatmapRenderer.Render(grid, distances, maxDistance: 50);
        byte[] zero = Pixel(bitmap, grid, cells[0]);
        byte[] belowFive = Pixel(bitmap, grid, cells[1]);
        byte[] five = Pixel(bitmap, grid, cells[2]);
        byte[] forty = Pixel(bitmap, grid, cells[5]);
        byte[] fortyFive = Pixel(bitmap, grid, cells[6]);

        Assert.Equal(zero, belowFive);
        Assert.False(belowFive.SequenceEqual(five));
        Assert.False(forty.SequenceEqual(fortyFive));

        var thresholdBitmap = HeatmapRenderer.Render(grid, distances, maxDistance: 50, threshold: 7);
        byte[] thresholdExceeded = Pixel(thresholdBitmap, grid, cells[3]);
        byte[] ten = Pixel(thresholdBitmap, grid, cells[4]);
        Assert.Equal(new byte[] { 210, 35, 190, 170 }, thresholdExceeded);
        Assert.Equal(thresholdExceeded, ten);
    }

    private static byte[] Pixel(
        System.Windows.Media.Imaging.WriteableBitmap bitmap,
        WalkabilityGrid grid,
        (int Col, int Row) cell)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(cell.Col, grid.Rows - 1 - cell.Row, 1, 1), pixel, 4, 0);
        return pixel;
    }
}
