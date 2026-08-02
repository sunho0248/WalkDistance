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

    [Fact]
    public void ProjectOntoSegment_OppositeSignNearDoubleMaxEndpoints_InteriorPointStaysFiniteAndCorrect()
    {
        var segment = new Segment(new WorldPoint(-8e307, -8e307), new WorldPoint(8e307, 8e307));

        var projected = GeometrySnap.ProjectOntoSegment(new WorldPoint(0, 0), segment);

        AssertFiniteAndClose(0, projected.X);
        AssertFiniteAndClose(0, projected.Y);
    }

    [Fact]
    public void ProjectOntoSegment_OppositeSignNearDoubleMaxEndpoints_PastEndClampsToEndpoint()
    {
        var segment = new Segment(new WorldPoint(-8e307, -8e307), new WorldPoint(8e307, 8e307));

        var projected = GeometrySnap.ProjectOntoSegment(new WorldPoint(9e307, 9e307), segment);

        Assert.True(double.IsFinite(projected.X));
        Assert.True(double.IsFinite(projected.Y));
        Assert.Equal(segment.End, projected);
    }

    [Fact]
    public void ProjectOntoSegment_OppositeSignNearDoubleMaxEndpoints_PastStartClampsToEndpoint()
    {
        var segment = new Segment(new WorldPoint(-8e307, -8e307), new WorldPoint(8e307, 8e307));

        var projected = GeometrySnap.ProjectOntoSegment(new WorldPoint(-9e307, -9e307), segment);

        Assert.True(double.IsFinite(projected.X));
        Assert.True(double.IsFinite(projected.Y));
        Assert.Equal(segment.Start, projected);
    }

    [Fact]
    public void TrySnapToNearest_HugeFiniteQueryAndTolerance_SnapsWithFiniteCorrectProjection()
    {
        var segments = new[] { new Segment(new WorldPoint(-8e307, -8e307), new WorldPoint(8e307, 8e307)) };

        var snapped = GeometrySnap.TrySnapToNearest(new WorldPoint(4e307, 4e307), segments, 1e300);

        Assert.NotNull(snapped);
        AssertFiniteAndClose(4e307, snapped!.Value.X);
        AssertFiniteAndClose(4e307, snapped.Value.Y);
    }

    [Fact]
    public void TrySnapToNearest_HugeFiniteToleranceAgainstHugePerpendicularOffset_StillSnapsWithinTolerance()
    {
        var segments = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)) };

        var snapped = GeometrySnap.TrySnapToNearest(new WorldPoint(4, 1e300), segments, 2e300);

        Assert.NotNull(snapped);
        AssertFiniteAndClose(4, snapped!.Value.X);
        AssertFiniteAndClose(0, snapped.Value.Y);
    }

    [Fact]
    public void TrySnapToNearest_HugePerpendicularOffsetExceedsTolerance_ReturnsNullNotNaNFalsePositive()
    {
        var segments = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)) };

        var snapped = GeometrySnap.TrySnapToNearest(new WorldPoint(4, 2e300), segments, 1e300);

        Assert.Null(snapped);
    }

    [Fact]
    public void ProjectOntoSegment_EndpointsNearDoubleMaxWhereRawSubtractionWouldOverflow_ReturnsFiniteZero()
    {
        // 0.9 * double.MaxValue is far beyond the 8e307 case already covered above:
        // end.X - start.X alone (before any scaling) is ~1.8 * double.MaxValue, which
        // overflows to +Infinity. A fix that only scales dx/dy/px/py *after* computing
        // them from a naive subtraction is still broken here.
        double bound = 0.9 * double.MaxValue;
        var segment = new Segment(new WorldPoint(-bound, -bound), new WorldPoint(bound, bound));

        var projected = GeometrySnap.ProjectOntoSegment(new WorldPoint(0, 0), segment);

        Assert.True(double.IsFinite(projected.X), $"Expected finite X but got {projected.X}.");
        Assert.True(double.IsFinite(projected.Y), $"Expected finite Y but got {projected.Y}.");
        AssertFiniteAndClose(0, projected.X);
        AssertFiniteAndClose(0, projected.Y);
    }

    [Fact]
    public void TrySnapToNearest_EndpointsNearDoubleMaxZeroTolerance_QueryAtZeroSnapsExactly()
    {
        double bound = 0.9 * double.MaxValue;
        var segments = new[] { new Segment(new WorldPoint(-bound, -bound), new WorldPoint(bound, bound)) };

        var snapped = GeometrySnap.TrySnapToNearest(new WorldPoint(0, 0), segments, 0.0);

        Assert.NotNull(snapped);
        Assert.True(double.IsFinite(snapped!.Value.X), $"Expected finite X but got {snapped.Value.X}.");
        Assert.True(double.IsFinite(snapped.Value.Y), $"Expected finite Y but got {snapped.Value.Y}.");
        AssertFiniteAndClose(0, snapped.Value.X);
        AssertFiniteAndClose(0, snapped.Value.Y);
    }

    [Fact]
    public void TrySnapToNearest_QueryAndSegmentOnOppositeDoubleMaxExtremes_DistanceOverflowReturnsNullNotNaN()
    {
        // point-to-projected distance itself overflows past double range here;
        // Distance must resolve that as +Infinity (never NaN) so the comparison
        // against even the largest finite tolerance correctly yields "no snap".
        double bound = 0.9 * double.MaxValue;
        var segments = new[] { new Segment(new WorldPoint(bound * 0.9, 0), new WorldPoint(bound, 0)) };

        var snapped = GeometrySnap.TrySnapToNearest(new WorldPoint(-bound, 0), segments, double.MaxValue);

        Assert.Null(snapped);
    }

    private static void AssertFiniteAndClose(double expected, double actual)
    {
        Assert.True(double.IsFinite(actual), $"Expected a finite value but got {actual}.");
        double scale = Math.Max(1.0, Math.Abs(expected));
        double relativeError = Math.Abs(actual - expected) / scale;
        Assert.True(relativeError <= 1e-9, $"Expected ~{expected} but was {actual} (relative error {relativeError}).");
    }
}
