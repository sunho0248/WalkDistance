using System.Text.Json;
using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public sealed class ProjectFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"walkdistance-{Guid.NewGuid():N}");

    public ProjectFileTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Version3_RoundTripsSegmentsWithoutSourceDxf()
    {
        var dxfPath = Path.Combine(_directory, "room.dxf");
        File.WriteAllText(dxfPath, DxfLoaderTests.DxfWithEntities("""
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
            """, insUnits: 4));
        var document = DxfLoader.Load(dxfPath);
        var projectPath = Path.Combine(_directory, "room.json");
        var expected = new ProjectData(
            Version: 3,
            CellSize: 0.25,
            MetersPerDrawingUnit: document.MetersPerDrawingUnit,
            Walls: document.Walls.ToList(),
            Exits: [new Segment(new WorldPoint(0.5, 0.5), new WorldPoint(1, 0.5))],
            DxfPath: dxfPath);

        ProjectFile.Save(projectPath, expected);
        File.Delete(dxfPath);
        var actual = ProjectFile.Load(projectPath);

        Assert.Equal(3, actual.Version);
        Assert.Equal(expected.Walls, actual.Walls);
        Assert.Equal(expected.Exits, actual.Exits);
        Assert.Equal(expected.CellSize, actual.CellSize);
        Assert.Equal(expected.MetersPerDrawingUnit, actual.MetersPerDrawingUnit);
    }

    [Fact]
    public void Version2_MigratesPointExitsWithoutChangingDistanceResults()
    {
        var walls = Rectangle(0, 0, 10, 10);
        var pointExit = new WorldPoint(5.2, 5.2);
        var projectPath = Path.Combine(_directory, "version2.json");
        File.WriteAllText(projectPath, JsonSerializer.Serialize(new
        {
            Version = 2,
            CellSize = 1.0,
            MetersPerDrawingUnit = 1.0,
            Walls = walls,
            Exits = new[] { pointExit },
            DxfPath = (string?)null,
        }));

        var project = ProjectFile.Load(projectPath);
        var migratedExit = Assert.Single(project.Exits);
        var grid = WalkabilityGrid.Build(project.Walls, project.CellSize, marginCells: 0);
        var legacySource = grid.NearestWalkableCell(pointExit)!.Value;
        var migratedSources = project.Exits
            .SelectMany(exit => grid.WalkableCellsNearSegment(exit, grid.CellSize))
            .Distinct()
            .ToList();
        var legacyResult = DistanceMapCalculator.Compute(grid, new[] { legacySource });
        var migratedResult = DistanceMapCalculator.Compute(grid, migratedSources);

        Assert.Equal(3, project.Version);
        Assert.Equal(new Segment(pointExit, pointExit), migratedExit);
        Assert.Equal(legacySource, Assert.Single(migratedSources));
        Assert.Equal(legacyResult.FarthestCell, migratedResult.FarthestCell);
        Assert.Equal(legacyResult.MaxDistance, migratedResult.MaxDistance);
    }

    [Fact]
    public void Version1_LoadsExistingDxfForCompatibility()
    {
        var dxfPath = Path.Combine(_directory, "legacy.dxf");
        File.WriteAllText(dxfPath, DxfLoaderTests.DxfWithEntities("""
            0
            LINE
            10
            0
            20
            0
            11
            4
            21
            0
            """, insUnits: 6));
        var projectPath = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(projectPath, JsonSerializer.Serialize(new
        {
            DxfPath = dxfPath,
            CellSize = 0.5,
            Exits = new[] { new WorldPoint(1, 1) },
        }));

        var project = ProjectFile.Load(projectPath);

        Assert.Equal(3, project.Version);
        Assert.Single(project.Walls);
        Assert.Equal(new WorldPoint(4, 0), project.Walls[0].End);
        Assert.Equal(new Segment(new WorldPoint(1, 1), new WorldPoint(1, 1)), project.Exits.Single());
    }

    [Fact]
    public void Version1_ReportsMissingDxf()
    {
        var projectPath = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(projectPath, JsonSerializer.Serialize(new
        {
            DxfPath = Path.Combine(_directory, "missing.dxf"),
            CellSize = 0.5,
            Exits = Array.Empty<WorldPoint>(),
        }));

        Assert.Throws<FileNotFoundException>(() => ProjectFile.Load(projectPath));
    }

    [Fact]
    public void UnsupportedVersion_IsRejected()
    {
        var projectPath = Path.Combine(_directory, "future.json");
        File.WriteAllText(projectPath, "{\"Version\":4}");

        Assert.Throws<InvalidDataException>(() => ProjectFile.Load(projectPath));
    }

    private static List<Segment> Rectangle(double minX, double minY, double maxX, double maxY) =>
    [
        new(new WorldPoint(minX, minY), new WorldPoint(maxX, minY)),
        new(new WorldPoint(maxX, minY), new WorldPoint(maxX, maxY)),
        new(new WorldPoint(maxX, maxY), new WorldPoint(minX, maxY)),
        new(new WorldPoint(minX, maxY), new WorldPoint(minX, minY)),
    ];

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
