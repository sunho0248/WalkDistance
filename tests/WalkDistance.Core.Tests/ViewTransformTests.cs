using System.Windows;
using WalkDistance.App;
using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class ViewTransformTests
{
    // Mirrors the private margin constant baked into ViewTransform.Build.
    private const double BuildMargin = 20;

    [Fact]
    public void ToScreen_StaysAccurateForLargeAbsoluteDrawingCoordinates()
    {
        // Real DXF files are frequently placed on a national survey grid rather than a
        // building-local origin, so raw drawing-unit coordinates can be far from zero
        // (here: ~2e8, representative of millimeter-scale national-grid coordinates).
        // Every value below is exactly representable as a double so any discrepancy
        // comes only from ViewTransform's own arithmetic, not from parsing the input.
        double minX = 195473820.0, maxX = 195473855.0;
        double minY = 452891347.0, maxY = 452891370.0;
        double canvasWidth = 1400, canvasHeight = 900;

        var bounds = new Bounds(minX, minY, maxX, maxY);
        var transform = ViewTransform.Build(bounds, canvasWidth, canvasHeight);

        double width = maxX - minX;
        double height = maxY - minY;
        double expectedScale = Math.Min(
            (canvasWidth - 2 * BuildMargin) / width,
            (canvasHeight - 2 * BuildMargin) / height);
        double expectedOffsetX = (canvasWidth - width * expectedScale) / 2;

        var point = new WorldPoint(minX + 10.0, minY + 5.0);

        // Ground truth computed independently of ViewTransform's internal representation:
        // the screen X of a point 10 units right of the bounds' left edge must be
        // offsetX + 10*scale, to within double's own precision (~1e-9 at this magnitude).
        double expectedScreenX = expectedOffsetX + (point.X - minX) * expectedScale;

        Point screen = transform.ToScreen(point);

        Assert.Equal(expectedScreenX, screen.X, precision: 7);
    }

    [Fact]
    public void ZoomAround_KeepsTheWorldPointUnderThePointerFixedOnScreen()
    {
        var bounds = new Bounds(195473820.0, 452891347.0, 195473855.0, 452891370.0);
        var transform = ViewTransform.Build(bounds, 1400, 900);

        var pointer = new Point(700, 450);
        WorldPoint worldUnderPointerBefore = transform.ToWorld(pointer);

        ViewTransform zoomed = transform.ZoomAround(pointer, 1.1);
        WorldPoint worldUnderPointerAfter = zoomed.ToWorld(pointer);

        Assert.Equal(worldUnderPointerBefore.X, worldUnderPointerAfter.X, precision: 6);
        Assert.Equal(worldUnderPointerBefore.Y, worldUnderPointerAfter.Y, precision: 6);

        Point reprojected = zoomed.ToScreen(worldUnderPointerBefore);
        Assert.Equal(pointer.X, reprojected.X, precision: 6);
        Assert.Equal(pointer.Y, reprojected.Y, precision: 6);
    }
}
