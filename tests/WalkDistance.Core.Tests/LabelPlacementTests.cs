using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class LabelPlacementTests
{
    [Theory]
    [InlineData(-20, 40, 4, 40)]
    [InlineData(190, 40, 156, 40)]
    [InlineData(80, -10, 80, 4)]
    [InlineData(80, 95, 80, 76)]
    public void ClampToCanvas_KeepsMeasuredLabelInsideEveryEdge(
        double x, double y, double expectedX, double expectedY)
    {
        var position = LabelPlacement.ClampToCanvas(
            new WorldPoint(x, y),
            labelWidth: 40,
            labelHeight: 20,
            canvasWidth: 200,
            canvasHeight: 100,
            padding: 4);

        Assert.Equal(new WorldPoint(expectedX, expectedY), position);
        Assert.InRange(position.X, 4, 200 - 40 - 4);
        Assert.InRange(position.Y, 4, 100 - 20 - 4);
    }
}
