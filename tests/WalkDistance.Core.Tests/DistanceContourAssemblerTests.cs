using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class DistanceContourAssemblerTests
{
    [Fact]
    public void Assemble_ConnectedChainFormsOneComponent()
    {
        DistanceContour[] contours =
        [
            Contour(0, 0, 1, 0),
            Contour(1, 0, 2, 0),
            Contour(2, 0, 3, 0),
        ];

        var component = Assert.Single(DistanceContourAssembler.Assemble(contours, tolerance: 1e-6));

        Assert.Equal(contours, component);
    }

    [Fact]
    public void Assemble_DisconnectedSameLevelSetsFormTwoComponents()
    {
        DistanceContour[] contours =
        [
            Contour(0, 0, 1, 0),
            Contour(10, 0, 11, 0),
            Contour(1, 0, 2, 0),
            Contour(11, 0, 12, 0),
        ];

        var components = DistanceContourAssembler.Assemble(contours, tolerance: 1e-6);

        Assert.Equal(2, components.Count);
        Assert.Equal([contours[0], contours[2]], components[0]);
        Assert.Equal([contours[1], contours[3]], components[1]);
    }

    [Theory]
    [InlineData(1.125, 1)]
    [InlineData(1.25, 2)]
    public void Assemble_ConnectsEndpointsOnlyWithinTolerance(double secondStartX, int expectedComponents)
    {
        DistanceContour[] contours =
        [
            Contour(0, 0, 1, 0),
            Contour(secondStartX, 0, 2, 0),
        ];

        var components = DistanceContourAssembler.Assemble(contours, tolerance: 0.125);

        Assert.Equal(expectedComponents, components.Count);
    }

    [Fact]
    public void Assemble_DoesNotMergeDifferentLevelsOrThresholdClasses()
    {
        var normalTen = Contour(0, 0, 1, 0);
        var normalTwenty = normalTen with { Level = 20 };
        var thresholdTen = normalTen with { IsThreshold = true };

        var components = DistanceContourAssembler.Assemble(
            [normalTen, normalTwenty, thresholdTen], tolerance: 1);

        Assert.Equal(3, components.Count);
        Assert.Equal(normalTen, Assert.Single(components[0]));
        Assert.Equal(normalTwenty, Assert.Single(components[1]));
        Assert.Equal(thresholdTen, Assert.Single(components[2]));
    }

    [Fact]
    public void Assemble_RejectsNonPositiveOrNonFiniteTolerance()
    {
        var contours = new[] { Contour(0, 0, 1, 0) };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DistanceContourAssembler.Assemble(contours, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DistanceContourAssembler.Assemble(contours, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DistanceContourAssembler.Assemble(contours, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DistanceContourAssembler.Assemble(contours, double.PositiveInfinity));
    }

    [Fact]
    public void Assemble_RejectsNonFiniteContourValues()
    {
        Assert.Throws<ArgumentException>(() => DistanceContourAssembler.Assemble(
            [Contour(0, 0, 1, 0) with { Level = double.NaN }], tolerance: 1));
        Assert.Throws<ArgumentException>(() => DistanceContourAssembler.Assemble(
            [Contour(double.PositiveInfinity, 0, 1, 0)], tolerance: 1));
    }

    private static DistanceContour Contour(double startX, double startY, double endX, double endY) =>
        new(10, new WorldPoint(startX, startY), new WorldPoint(endX, endY));
}
