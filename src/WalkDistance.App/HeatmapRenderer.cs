using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WalkDistance.Core;

namespace WalkDistance.App;

/// <summary>
/// Rasterizes a distance map into semi-transparent, solid 5 m color bands.
/// </summary>
public static class HeatmapRenderer
{
    private static readonly (byte R, byte G, byte B)[] DistanceBandColors =
    [
        (0, 80, 255),
        (0, 180, 255),
        (0, 190, 100),
        (150, 205, 0),
        (245, 215, 0),
        (255, 145, 0),
        (235, 70, 35),
        (200, 25, 30),
    ];

    public static WriteableBitmap Render(WalkabilityGrid grid, double[,] distances, double maxDistance, double? threshold = null)
    {
        int width = grid.Cols;
        int height = grid.Rows;
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];

        for (int gridRow = 0; gridRow < height; gridRow++)
        {
            int bitmapRow = height - 1 - gridRow; // grid row 0 = world MinY = bottom of screen
            for (int col = 0; col < width; col++)
            {
                int idx = (bitmapRow * width + col) * 4;
                double d = distances[gridRow, col];
                if (!grid.IsWalkable(col, gridRow) || double.IsPositiveInfinity(d) || maxDistance <= 0)
                {
                    continue; // leave fully transparent
                }

                int band = Math.Max(0, (int)Math.Floor(d / 5));
                int colorIndex = band < DistanceBandColors.Length
                    ? band
                    : DistanceBandColors.Length - 2 + band % 2;
                var (r, g, b) = threshold is { } limit && d > limit
                    ? ((byte)190, (byte)35, (byte)210)
                    : DistanceBandColors[colorIndex];
                pixels[idx + 0] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 170;
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

}
