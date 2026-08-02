using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WalkDistance.Core;

namespace WalkDistance.App;

/// <summary>
/// Rasterizes a distance map into a semi-transparent heatmap bitmap
/// (blue = near an exit, red = far from every exit).
/// </summary>
public static class HeatmapRenderer
{
    public static WriteableBitmap Render(WalkabilityGrid grid, double[,] distances, double maxDistance)
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

                double t = Math.Clamp(d / maxDistance, 0, 1);
                var (r, g, b) = Gradient(t);
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

    private static (byte R, byte G, byte B) Gradient(double t)
    {
        (double R, double G, double B) blue = (0, 0, 1);
        (double R, double G, double B) green = (0, 1, 0);
        (double R, double G, double B) yellow = (1, 1, 0);
        (double R, double G, double B) red = (1, 0, 0);

        if (t < 1.0 / 3)
        {
            return Lerp(blue, green, t / (1.0 / 3));
        }

        if (t < 2.0 / 3)
        {
            return Lerp(green, yellow, (t - 1.0 / 3) / (1.0 / 3));
        }

        return Lerp(yellow, red, (t - 2.0 / 3) / (1.0 / 3));
    }

    private static (byte R, byte G, byte B) Lerp((double R, double G, double B) a, (double R, double G, double B) b, double t)
    {
        return (
            (byte)((a.R + (b.R - a.R) * t) * 255),
            (byte)((a.G + (b.G - a.G) * t) * 255),
            (byte)((a.B + (b.B - a.B) * t) * 255));
    }
}
