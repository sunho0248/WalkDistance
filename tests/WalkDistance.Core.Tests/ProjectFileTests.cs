using System.Text.Json;
using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public sealed class ProjectFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"walkdistance-{Guid.NewGuid():N}");

    public ProjectFileTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Version5_RoundTripsCachedAnalysis()
    {
        var walls = Rectangle(0, 0, 10, 10);
        var grid = WalkabilityGrid.Build(walls, 1, marginCells: 0);
        var source = grid.NearestWalkableCell(new WorldPoint(5, 5))!.Value;
        var result = DistanceMapCalculator.Compute(grid, new[] { source });
        var cache = DistanceMapCache.Create(grid, result,
            new WorldPoint(6, 6), 2.5,
            [new WorldPoint(5, 5), new WorldPoint(6, 6)],
            [new WorldPoint(5, 5), new WorldPoint(7, 7)]);
        var path = Path.Combine(_directory, "cached.walkdistance");
        var expected = new ProjectData(5, 1, 1, walls, [], Analysis: cache);

        ProjectFile.Save(path, expected);
        var actual = ProjectFile.Load(path);
        var restored = actual.Analysis!.Restore(grid);

        Assert.Equal(5, actual.Version);
        Assert.Equal(result.MaxDistance, restored.Result.MaxDistance);
        Assert.Equal(result.FarthestCell, restored.Result.FarthestCell);
        Assert.Equal(result.Distances[source.Row, source.Col], restored.Result.Distances[source.Row, source.Col]);
        Assert.Equal(cache.QueryPoint, actual.Analysis.QueryPoint);
        Assert.Equal(cache.QueryPath, actual.Analysis.QueryPath);
        Assert.Equal(cache.FarthestPath, actual.Analysis.FarthestPath);
    }

    [Fact]
    public void Version4_RoundTripsMultiPointExitPathsWithoutSourceDxf()
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
        var exitPath = new List<WorldPoint>
        {
            new(0.5, 0.5),
            new(1, 0.5),
            new(1, 0.75),
        };
        var expected = new ProjectData(
            Version: 4,
            CellSize: 0.25,
            MetersPerDrawingUnit: document.MetersPerDrawingUnit,
            Walls: document.Walls.ToList(),
            Exits: [new Segment(exitPath[0], exitPath[^1])],
            DxfPath: dxfPath,
            ExitPaths: [exitPath]);

        ProjectFile.Save(projectPath, expected);
        File.Delete(dxfPath);
        var actual = ProjectFile.Load(projectPath);

        Assert.Equal(5, actual.Version);
        Assert.Equal(expected.Walls, actual.Walls);
        Assert.Equal(expected.Exits, actual.Exits);
        Assert.Equal(exitPath, Assert.Single(actual.ExitPaths!));
        Assert.Equal(expected.CellSize, actual.CellSize);
        Assert.Equal(expected.MetersPerDrawingUnit, actual.MetersPerDrawingUnit);
    }

    [Fact]
    public void Version3_MigratesSegmentsToTwoPointExitPaths()
    {
        var walls = Rectangle(0, 0, 10, 10);
        var exit = new Segment(new WorldPoint(2, 0), new WorldPoint(4, 0));
        var projectPath = Path.Combine(_directory, "version3.json");
        File.WriteAllText(projectPath, JsonSerializer.Serialize(new
        {
            Version = 3,
            CellSize = 1.0,
            MetersPerDrawingUnit = 1.0,
            Walls = walls,
            Exits = new[] { exit },
            DxfPath = (string?)null,
        }));

        var project = ProjectFile.Load(projectPath);

        Assert.Equal(5, project.Version);
        Assert.Equal(exit, Assert.Single(project.Exits));
        Assert.Equal(new[] { exit.Start, exit.End }, Assert.Single(project.ExitPaths!));
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

        Assert.Equal(5, project.Version);
        Assert.Equal(new Segment(pointExit, pointExit), migratedExit);
        Assert.Equal(new[] { pointExit, pointExit }, Assert.Single(project.ExitPaths!));
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

        Assert.Equal(5, project.Version);
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
        File.WriteAllText(projectPath, "{\"Version\":6}");

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
