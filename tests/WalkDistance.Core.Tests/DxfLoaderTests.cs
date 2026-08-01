using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public class DxfLoaderTests
{
    [Fact]
    public void Load_ScalesMillimetersToMeters()
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            LINE
            10
            0
            20
            0
            11
            2500
            21
            0
            """, insUnits: 4));

        var document = DxfLoader.Load(reader);

        Assert.Equal(0.001, document.MetersPerDrawingUnit);
        Assert.Equal(new WorldPoint(2.5, 0), document.Walls.Single().End);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Load_UnknownUnitsRequireExplicitScale(int? insUnits)
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            LINE
            10
            0
            20
            0
            11
            1000
            21
            0
            """, insUnits));

        Assert.Throws<DxfUnitRequiredException>(() => DxfLoader.Load(reader));
    }

    [Fact]
    public void Load_UnitlessUsesExplicitScale()
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            LINE
            10
            0
            20
            0
            11
            1000
            21
            0
            """, insUnits: 0));

        var document = DxfLoader.Load(reader, unitlessMetersPerUnit: 0.001);

        Assert.Equal(new WorldPoint(1, 0), document.Walls.Single().End);
    }

    [Fact]
    public void Load_ParsesSupportedWallEntities()
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            LINE
            10
            0
            20
            0
            11
            10
            21
            0
            0
            LWPOLYLINE
            70
            1
            10
            0
            20
            0
            10
            2
            20
            0
            10
            2
            20
            2
            0
            POLYLINE
            70
            0
            0
            VERTEX
            10
            3
            20
            0
            0
            VERTEX
            10
            3
            20
            3
            0
            SEQEND
            0
            CIRCLE
            10
            5
            20
            5
            40
            1
            0
            ARC
            10
            8
            20
            8
            40
            1
            50
            0
            51
            90
            """, insUnits: 6));

        var document = DxfLoader.Load(reader);

        Assert.Equal(45, document.Walls.Count); // 1 + 3 + 1 + 32 + 8
        Assert.Contains(document.Walls, s =>
            s.Start == new WorldPoint(3, 0) && s.End == new WorldPoint(3, 3));
    }

    [Fact]
    public void Load_DocumentWithoutSupportedWallsThrows()
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            TEXT
            10
            0
            20
            0
            1
            hello
            """, insUnits: 6));

        Assert.Throws<InvalidDataException>(() => DxfLoader.Load(reader));
    }

    [Fact]
    public void Load_MalformedDocumentThrows()
    {
        using var reader = new StringReader("not-a-group-code\nSECTION\n");

        Assert.Throws<InvalidDataException>(() => DxfLoader.Load(reader));
    }

    internal static string DxfWithEntities(string entities, int? insUnits)
    {
        var header = insUnits is null
            ? string.Empty
            : $"0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n{insUnits}\n0\nENDSEC\n";
        return $"{header}0\nSECTION\n2\nENTITIES\n{entities.Trim()}\n0\nENDSEC\n0\nEOF\n";
    }
}
