using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class DxfLoaderTests
{
    private const string SampleDxf = """
        0
        SECTION
        2
        ENTITIES
        0
        LINE
        10
        0.0
        20
        0.0
        11
        10.0
        21
        0.0
        0
        LWPOLYLINE
        90
        4
        70
        1
        10
        0.0
        20
        0.0
        10
        5.0
        20
        0.0
        10
        5.0
        20
        5.0
        10
        0.0
        20
        5.0
        0
        ENDSEC
        0
        EOF
        """;

    [Fact]
    public void LoadWalls_ParsesLineEntity()
    {
        using var reader = new StringReader(SampleDxf);
        var segments = DxfLoader.LoadWalls(reader);

        Assert.Contains(segments, s =>
            s.Start == new WorldPoint(0, 0) && s.End == new WorldPoint(10, 0));
    }

    [Fact]
    public void LoadWalls_ClosedPolylineProducesFourSegments()
    {
        using var reader = new StringReader(SampleDxf);
        var segments = DxfLoader.LoadWalls(reader);

        // 1 LINE + 4 segments from the closed 4-vertex LWPOLYLINE square.
        Assert.Equal(5, segments.Count);
        Assert.Contains(segments, s =>
            s.Start == new WorldPoint(0, 5) && s.End == new WorldPoint(0, 0));
    }

    [Fact]
    public void LoadWalls_EmptyDocumentReturnsNoSegments()
    {
        using var reader = new StringReader("0\nEOF");
        var segments = DxfLoader.LoadWalls(reader);

        Assert.Empty(segments);
    }
}
