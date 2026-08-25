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
    public void Load_PreservesExtrudedLineWcsAndTransformsCircleAndArcFromOcs()
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            LINE
            10
            10
            20
            2
            11
            20
            21
            3
            210
            0
            220
            0
            230
            -1
            0
            CIRCLE
            10
            -30
            20
            4
            40
            1
            210
            0
            220
            0
            230
            -1
            0
            ARC
            10
            -40
            20
            5
            40
            2
            50
            0
            51
            90
            210
            0
            220
            0
            230
            -1
            """, insUnits: 6));

        var document = DxfLoader.Load(reader);

        Assert.Equal(new Segment(new WorldPoint(10, 2), new WorldPoint(20, 3)), document.Walls[0]);
        Assert.Equal(29, document.Walls[1].Start.X, precision: 10);
        Assert.Equal(4, document.Walls[1].Start.Y, precision: 10);
        Assert.Equal(38, document.Walls[33].Start.X, precision: 10);
        Assert.Equal(5, document.Walls[33].Start.Y, precision: 10);
        Assert.Equal(40, document.Walls[^1].End.X, precision: 10);
        Assert.Equal(7, document.Walls[^1].End.Y, precision: 10);
    }

    [Fact]
    public void Load_RetainsWdExitLineAndPolylinePathsInOrderAndMeters()
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            LINE
            8
            WD_Exit
            10
            0
            20
            0
            11
            1000
            21
            0
            0
            LWPOLYLINE
            8
            wd_exit
            10
            1000
            20
            0
            10
            1000
            20
            2000
            10
            3000
            20
            2000
            0
            LINE
            8
            Walls
            10
            0
            20
            1000
            11
            1000
            21
            1000
            0
            TEXT
            8
            WD_Exit
            1
            ignored
            """, insUnits: 4));

        var document = DxfLoader.Load(reader);

        Assert.Equal(4, document.Walls.Count);
        Assert.Equal(new[] { new WorldPoint(0, 0), new WorldPoint(1, 0) }, document.ExitPaths[0]);
        Assert.Equal(
            new[] { new WorldPoint(1, 0), new WorldPoint(1, 2), new WorldPoint(3, 2) },
            document.ExitPaths[1]);
        Assert.Equal(2, document.ExitPaths.Count);
    }

    [Fact]
    public void Load_WdExitOnWallBoundaryProducesBodySourcesAtRealWorldOffset()
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            LWPOLYLINE
            70
            0
            10
            22943.67116923112
            20
            5036.688388349255
            10
            22943.67116923112
            20
            200
            10
            26318.37804968603
            20
            200
            10
            26318.37804968603
            20
            6136.688388349256
            10
            22943.67116923112
            20
            6136.688388349256
            0
            LINE
            8
            wd_exit
            10
            22943.67116923112
            20
            5036.688388349255
            11
            22943.67116923112
            21
            6136.688388349256
            """, insUnits: 4));

        var document = DxfLoader.Load(reader);
        var exitPath = Assert.Single(document.ExitPaths);
        var geometricGrid = WalkabilityGrid.Build(document.Walls, cellSize: 0.05);
        var bodyGrid = WalkabilityGrid.Build(
            document.Walls,
            cellSize: 0.05,
            clearanceRadius: BodyProfile.KoreanAdult.ClearanceRadius);

        Assert.Equal(1.10, Math.Sqrt(Math.Pow(exitPath[1].X - exitPath[0].X, 2) +
                                     Math.Pow(exitPath[1].Y - exitPath[0].Y, 2)), precision: 10);
        Assert.NotEmpty(geometricGrid.WalkableSourcesNearSegment(exitPath, geometricGrid.CellSize));
        Assert.NotEmpty(bodyGrid.WalkableSourcesNearSegment(exitPath, bodyGrid.CellSize));
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

    [Fact]
    public void Load_UnsupportedEntityType_CountedInDiagnostics()
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
            0
            TEXT
            10
            1
            20
            1
            1
            world
            0
            LINE
            10
            0
            20
            0
            11
            5
            21
            0
            """, insUnits: 6));

        var document = DxfLoader.Load(reader);

        Assert.Single(document.Walls);
        Assert.Equal(2, document.Diagnostics.UnsupportedEntityCounts["TEXT"]);
    }

    [Fact]
    public void Load_ZeroLengthLineSegment_CountedButKept()
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            LINE
            10
            0
            20
            0
            11
            0
            21
            0
            0
            LINE
            10
            0
            20
            0
            11
            5
            21
            0
            """, insUnits: 6));

        var document = DxfLoader.Load(reader);

        Assert.Equal(2, document.Walls.Count);
        Assert.Contains(document.Walls, s => s.Start == s.End);
        Assert.Equal(1, document.Diagnostics.ZeroLengthSegmentCount);
    }

    [Fact]
    public void Load_DuplicateConsecutivePolylineVertices_CountedButKept()
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            LWPOLYLINE
            70
            0
            10
            0
            20
            0
            10
            0
            20
            0
            10
            3
            20
            0
            """, insUnits: 6));

        var document = DxfLoader.Load(reader);

        Assert.Equal(2, document.Walls.Count);
        Assert.Equal(1, document.Diagnostics.DuplicateConsecutiveVertexCount);
        Assert.Equal(1, document.Diagnostics.ZeroLengthSegmentCount);
    }

    [Fact]
    public void Load_ArcAndCircleEntities_TessellationCountsRecorded()
    {
        using var reader = new StringReader(DxfWithEntities("""
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

        Assert.Equal(2, document.Diagnostics.TessellatedCurveCount);
        Assert.Equal(40, document.Diagnostics.TessellationSegmentCount); // 32 (circle) + 8 (90-degree arc)
    }

    [Fact]
    public void Load_NoQualityIssues_DiagnosticsAreEmpty()
    {
        using var reader = new StringReader(DxfWithEntities("""
            0
            LINE
            10
            0
            20
            0
            11
            5
            21
            0
            """, insUnits: 6));

        var document = DxfLoader.Load(reader);

        Assert.Empty(document.Diagnostics.UnsupportedEntityCounts);
        Assert.Equal(0, document.Diagnostics.ZeroLengthSegmentCount);
        Assert.Equal(0, document.Diagnostics.TessellatedCurveCount);
        Assert.Equal(0, document.Diagnostics.TessellationSegmentCount);
        Assert.Equal(0, document.Diagnostics.DuplicateConsecutiveVertexCount);
        Assert.False(document.Diagnostics.HasIssues);
    }

    [Fact]
    public void Load_ExistingSampleRoomDxf_DiagnosticsMatchKnownCleanGeometry()
    {
        var document = DxfLoader.Load(SampleRoomDxfPath());

        Assert.Empty(document.Diagnostics.UnsupportedEntityCounts);
        Assert.Equal(0, document.Diagnostics.ZeroLengthSegmentCount);
        Assert.Equal(0, document.Diagnostics.TessellatedCurveCount);
        Assert.Equal(0, document.Diagnostics.DuplicateConsecutiveVertexCount);
        Assert.False(document.Diagnostics.HasIssues);
    }

    private static string SampleRoomDxfPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WalkDistance.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "samples", "sample-room.dxf");
    }

    internal static string DxfWithEntities(string entities, int? insUnits)
    {
        var header = insUnits is null
            ? string.Empty
            : $"0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n{insUnits}\n0\nENDSEC\n";
        return $"{header}0\nSECTION\n2\nENTITIES\n{entities.Trim()}\n0\nENDSEC\n0\nEOF\n";
    }
}
