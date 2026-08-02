namespace WalkDistance.Core.Tests;

public class GeometrySnapTests
{
    [Fact]
    public void ProjectOntoSegment_PointOverInterior_ReturnsPerpendicularFoot()
    {
        var segment = new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0));

        var projected = GeometrySnap.ProjectOntoSegment(new WorldPoint(4, 3), segment);

        Assert.Equal(new WorldPoint(4, 0), projected);
    }

    [Fact]
    public void ProjectOntoSegment_PointPastStart_ClampsToStartEndpoint()
    {
        var segment = new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0));

        var projected = GeometrySnap.ProjectOntoSegment(new WorldPoint(-5, 3), segment);

        Assert.Equal(segment.Start, projected);
    }

    [Fact]
    public void ProjectOntoSegment_PointPastEnd_ClampsToEndEndpoint()
    {
        var segment = new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0));

        var projected = GeometrySnap.ProjectOntoSegment(new WorldPoint(15, -4), segment);

        Assert.Equal(segment.End, projected);
    }

    [Fact]
    public void ProjectOntoSegment_DegenerateSegment_ReturnsThatPoint()
    {
        var point = new WorldPoint(5, 5);
        var segment = new Segment(point, point);

        var projected = GeometrySnap.ProjectOntoSegment(new WorldPoint(9, 9), segment);

        Assert.Equal(point, projected);
    }

    [Fact]
    public void ProjectOntoSegment_InvalidPoint_Throws()
    {
        var segment = new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0));

        Assert.Throws<ArgumentException>(() =>
            GeometrySnap.ProjectOntoSegment(new WorldPoint(double.NaN, 0), segment));
    }

    [Fact]
    public void ProjectOntoSegment_InvalidSegmentEndpoint_Throws()
    {
        var segment = new Segment(new WorldPoint(0, 0), new WorldPoint(double.PositiveInfinity, 0));

        Assert.Throws<ArgumentException>(() =>
            GeometrySnap.ProjectOntoSegment(new WorldPoint(1, 1), segment));
    }

    [Fact]
    public void TrySnapToNearest_WithinTolerance_ReturnsProjectedPointOnClosestSegment()
    {
        var segments = new[]
        {
            new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)),
            new Segment(new WorldPoint(0, 5), new WorldPoint(10, 5)),
        };

        var snapped = GeometrySnap.TrySnapToNearest(new WorldPoint(4, 0.5), segments, 1.0);

        Assert.Equal(new WorldPoint(4, 0), snapped);
    }

    [Fact]
    public void TrySnapToNearest_NearestOfMany_PicksClosestNotFirst()
    {
        var segments = new[]
        {
            new Segment(new WorldPoint(0, 10), new WorldPoint(10, 10)),
            new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)),
            new Segment(new WorldPoint(0, 3), new WorldPoint(10, 3)),
        };

        var snapped = GeometrySnap.TrySnapToNearest(new WorldPoint(5, 1), segments, 5.0);

        Assert.Equal(new WorldPoint(5, 0), snapped);
    }

    [Fact]
    public void TrySnapToNearest_ExactlyAtThreshold_Snaps()
    {
        var segments = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)) };

        var snapped = GeometrySnap.TrySnapToNearest(new WorldPoint(4, 2), segments, 2.0);

        Assert.Equal(new WorldPoint(4, 0), snapped);
    }

    [Fact]
    public void TrySnapToNearest_OutsideTolerance_ReturnsNull()
    {
        var segments = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)) };

        var snapped = GeometrySnap.TrySnapToNearest(new WorldPoint(4, 2.01), segments, 2.0);

        Assert.Null(snapped);
    }

    [Fact]
    public void TrySnapToNearest_NoSegments_ReturnsNull()
    {
        var snapped = GeometrySnap.TrySnapToNearest(new WorldPoint(4, 2), [], 2.0);

        Assert.Null(snapped);
    }

    [Fact]
    public void TrySnapToNearest_InvalidPoint_Throws()
    {
        var segments = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)) };

        Assert.Throws<ArgumentException>(() =>
            GeometrySnap.TrySnapToNearest(new WorldPoint(double.NaN, 0), segments, 2.0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.001)]
    public void TrySnapToNearest_InvalidTolerance_Throws(double tolerance)
    {
        var segments = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)) };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GeometrySnap.TrySnapToNearest(new WorldPoint(4, 0), segments, tolerance));
    }

    [Fact]
    public void TrySnapToNearest_ZeroTolerance_IsValidAndOnlySnapsExactHits()
    {
        var segments = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)) };

        Assert.Null(GeometrySnap.TrySnapToNearest(new WorldPoint(4, 0.001), segments, 0.0));
        Assert.Equal(new WorldPoint(4, 0), GeometrySnap.TrySnapToNearest(new WorldPoint(4, 0), segments, 0.0));
    }
}
