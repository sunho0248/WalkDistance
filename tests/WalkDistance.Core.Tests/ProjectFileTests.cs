using System.Text.Json;
using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public sealed class ProjectFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"walkdistance-{Guid.NewGuid():N}");

    public ProjectFileTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Version2_RoundTripsWithoutSourceDxf()
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
            Version: 2,
            CellSize: 0.25,
            MetersPerDrawingUnit: document.MetersPerDrawingUnit,
            Walls: document.Walls.ToList(),
            Exits: [new WorldPoint(0.5, 0.5)],
            DxfPath: dxfPath);

        ProjectFile.Save(projectPath, expected);
        File.Delete(dxfPath);
        var actual = ProjectFile.Load(projectPath);

        Assert.Equal(2, actual.Version);
        Assert.Equal(expected.Walls, actual.Walls);
        Assert.Equal(expected.Exits, actual.Exits);
        Assert.Equal(expected.CellSize, actual.CellSize);
        Assert.Equal(expected.MetersPerDrawingUnit, actual.MetersPerDrawingUnit);
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

        Assert.Equal(2, project.Version);
        Assert.Single(project.Walls);
        Assert.Equal(new WorldPoint(4, 0), project.Walls[0].End);
        Assert.Equal(new WorldPoint(1, 1), project.Exits.Single());
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

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
