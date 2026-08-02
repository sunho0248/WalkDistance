namespace WalkDistance.Core;

public static class LabelPlacement
{
    public static WorldPoint ClampToCanvas(
        WorldPoint desired,
        double labelWidth,
        double labelHeight,
        double canvasWidth,
        double canvasHeight,
        double padding)
    {
        if (!double.IsFinite(desired.X) || !double.IsFinite(desired.Y) ||
            !double.IsFinite(labelWidth) || labelWidth < 0 ||
            !double.IsFinite(labelHeight) || labelHeight < 0 ||
            !double.IsFinite(canvasWidth) || canvasWidth < 0 ||
            !double.IsFinite(canvasHeight) || canvasHeight < 0 ||
            !double.IsFinite(padding) || padding < 0)
            throw new ArgumentOutOfRangeException(nameof(desired));

        double maxX = Math.Max(padding, canvasWidth - labelWidth - padding);
        double maxY = Math.Max(padding, canvasHeight - labelHeight - padding);
        return new WorldPoint(
            Math.Clamp(desired.X, padding, maxX),
            Math.Clamp(desired.Y, padding, maxY));
    }
}
