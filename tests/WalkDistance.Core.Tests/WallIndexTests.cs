using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class WallIndexTests
{
    [Fact]
    public void Build_EmptyWalls_TrySnapReturnsNull()
    {
        var index = WallIndex.Build([]);

        Assert.Null(index.TrySnapToNearest(new WorldPoint(0, 0), 1.0));
    }

    [Fact]
    public void TrySnapToNearest_WithinTolerance_ReturnsProjectedPointAndSegmentIndex()
    {
        var walls = new[]
        {
            new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)),
            new Segment(new WorldPoint(10, 0), new WorldPoint(10, 10)),
        };
        var index = WallIndex.Build(walls);

        var result = index.TrySnapToNearest(new WorldPoint(4, 0.5), 1.0);

        Assert.NotNull(result);
        Assert.Equal(new WorldPoint(4, 0), result!.Value.Point);
        Assert.Equal(0, result.Value.SegmentIndex);
    }

    [Fact]
    public void TrySnapToNearest_PicksNearestSegmentNotFirstBucketHit()
    {
        var walls = new[]
        {
            new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)),
            new Segment(new WorldPoint(0, 1), new WorldPoint(10, 1)),
        };
        var index = WallIndex.Build(walls);

        var result = index.TrySnapToNearest(new WorldPoint(5, 0.9), 2.0);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Value.SegmentIndex);
    }

    [Fact]
    public void TrySnapToNearest_OutsideTolerance_ReturnsNull()
    {
        var walls = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)) };
        var index = WallIndex.Build(walls);

        Assert.Null(index.TrySnapToNearest(new WorldPoint(5, 5), 1.0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TrySnapToNearest_NonFinitePoint_Throws(double bad)
    {
        var index = WallIndex.Build([new Segment(new WorldPoint(0, 0), new WorldPoint(1, 0))]);

        Assert.Throws<ArgumentException>(() => index.TrySnapToNearest(new WorldPoint(bad, 0), 1.0));
    }

    [Fact]
    public void TrySnapToNearest_NegativeTolerance_Throws()
    {
        var index = WallIndex.Build([new Segment(new WorldPoint(0, 0), new WorldPoint(1, 0))]);

        Assert.Throws<ArgumentOutOfRangeException>(() => index.TrySnapToNearest(new WorldPoint(0, 0), -1.0));
    }

    [Fact]
    public void TraceFixedLength_StraightWall_WalksTowardPointerDirection()
    {
        var walls = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(20, 0)) };
        var index = WallIndex.Build(walls);

        var route = index.TraceFixedLength(
            start: new WorldPoint(2, 0), towardPoint: new WorldPoint(100, 0), length: 5, tolerance: 0.5);

        Assert.Equal(new WorldPoint(2, 0), route[0]);
        Assert.Equal(new WorldPoint(7, 0), route[^1]);
    }

    [Fact]
    public void TraceFixedLength_TowardOppositeEnd_WalksTheOtherDirection()
    {
        var walls = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(20, 0)) };
        var index = WallIndex.Build(walls);

        var route = index.TraceFixedLength(
            start: new WorldPoint(10, 0), towardPoint: new WorldPoint(-100, 0), length: 4, tolerance: 0.5);

        Assert.Equal(new WorldPoint(6, 0), route[^1]);
    }

    [Fact]
    public void TraceFixedLength_CrossesJunctionOntoConnectedWall()
    {
        // L-shaped chain: (0,0)-(10,0) then (10,0)-(10,10). Starting near the
        // corner and walking past it should continue onto the second wall.
        var walls = new[]
        {
            new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)),
            new Segment(new WorldPoint(10, 0), new WorldPoint(10, 10)),
        };
        var index = WallIndex.Build(walls);

        var route = index.TraceFixedLength(
            start: new WorldPoint(8, 0), towardPoint: new WorldPoint(100, 0), length: 5, tolerance: 0.5);

        Assert.Equal(new WorldPoint(8, 0), route[0]);
        Assert.Equal(new WorldPoint(10, 0), route[1]);
        Assert.Equal(new WorldPoint(10, 3), route[^1]);
    }

    [Fact]
    public void TraceFixedLength_ChainShorterThanRequestedLength_ReturnsPartialRouteNoThrow()
    {
        var walls = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(3, 0)) };
        var index = WallIndex.Build(walls);

        var route = index.TraceFixedLength(
            start: new WorldPoint(0, 0), towardPoint: new WorldPoint(100, 0), length: 50, tolerance: 0.5);

        Assert.Equal(new WorldPoint(3, 0), route[^1]);
    }

    [Fact]
    public void TraceFixedLength_StartNotOnAnyWall_ReturnsOnlyStart()
    {
        var walls = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)) };
        var index = WallIndex.Build(walls);

        var route = index.TraceFixedLength(
            start: new WorldPoint(0, 50), towardPoint: new WorldPoint(10, 50), length: 5, tolerance: 0.5);

        Assert.Equal([new WorldPoint(0, 50)], route);
    }

    [Fact]
    public void TraceFixedLength_NonPositiveLength_ReturnsOnlyStart()
    {
        var walls = new[] { new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)) };
        var index = WallIndex.Build(walls);

        var route = index.TraceFixedLength(
            start: new WorldPoint(2, 0), towardPoint: new WorldPoint(100, 0), length: 0, tolerance: 0.5);

        Assert.Equal([new WorldPoint(2, 0)], route);
    }

    [Fact]
    public void TraceFixedLength_AtJunction_PrefersStraightestContinuationOverSharpTurn()
    {
        // Three walls meeting at (10,0): the straight continuation (10,0)-(20,0)
        // should be favored over the perpendicular branch (10,0)-(10,10).
        var walls = new[]
        {
            new Segment(new WorldPoint(0, 0), new WorldPoint(10, 0)),
            new Segment(new WorldPoint(10, 0), new WorldPoint(20, 0)),
            new Segment(new WorldPoint(10, 0), new WorldPoint(10, 10)),
        };
        var index = WallIndex.Build(walls);

        var route = index.TraceFixedLength(
            start: new WorldPoint(8, 0), towardPoint: new WorldPoint(100, 0), length: 6, tolerance: 0.5);

        Assert.Equal(new WorldPoint(14, 0), route[^1]);
    }

    [Fact]
    public void TraceFixedLength_TessellatedArcSegmentsChainLikeAnyPolyline()
    {
        // DxfLoader.AddChain tessellates arcs/circles into consecutive short
        // segments sharing exact endpoint values; a chain of many short hops
        // must trace across all of them like any other polyline.
        var walls = new List<Segment>();
        for (int i = 0; i < 20; i++)
        {
            walls.Add(new Segment(new WorldPoint(i, 0), new WorldPoint(i + 1, 0)));
        }
        var index = WallIndex.Build(walls);

        var route = index.TraceFixedLength(
            start: new WorldPoint(0, 0), towardPoint: new WorldPoint(100, 0), length: 15, tolerance: 0.5);

        Assert.Equal(new WorldPoint(15, 0), route[^1]);
    }
}
