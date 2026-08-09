using System.Text.Json;
using System.Text.Json.Serialization;
using WalkDistance.Core;

namespace WalkDistance.Core.Tests;

public sealed class ProjectFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"walkdistance-{Guid.NewGuid():N}");

    public ProjectFileTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Version5_DropsMalformedProfilelessCache()
    {
        var walls = Rectangle(0, 0, 10, 10);
        var path = Path.Combine(_directory, "version5.walkdistance");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Version = 5,
            CellSize = 1.0,
            MetersPerDrawingUnit = 1.0,
            Walls = walls,
            Exits = Array.Empty<Segment>(),
            Analysis = new { },
        }));

        var project = ProjectFile.Load(path);

        Assert.Equal(8, project.Version);
        Assert.Equal(BodyProfile.KoreanAdult, project.BodyProfile);
        Assert.Null(project.Analysis);
    }

    [Fact]
    public void Version6_DropsProfilelessBodyFilteredCacheInsteadOfUsingItAsTheMap()
    {
        var profile = new BodyProfile(0.5);
        var walls = Rectangle(0, 0, 10, 10);
        var grid = WalkabilityGrid.Build(walls, 1, marginCells: 0, clearanceRadius: profile.ClearanceRadius);
        var source = grid.NearestWalkableCell(new WorldPoint(5, 5))!.Value;
        var result = DistanceMapCalculator.Compute(grid, [source]);
        var cache = DistanceMapCache.Create(grid, result, null, null, null, null);
        var path = Path.Combine(_directory, "profile.walkdistance");

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Version = 6,
            CellSize = 1.0,
            MetersPerDrawingUnit = 1.0,
            Walls = walls,
            Exits = Array.Empty<Segment>(),
            BodyProfile = profile,
            Analysis = new
            {
                cache.Rows, cache.Cols, cache.Distances, cache.Predecessors, cache.FarthestCell,
                cache.MaxDistance, cache.UnreachableCellCount, cache.Roots, cache.SourceGroups,
                cache.WinningGroupIndexes, cache.QueryPoint, cache.QueryDistance, cache.FarthestPath,
                cache.QueryPath,
            },
        }, new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        }));
        var project = ProjectFile.Load(path);

        Assert.Equal(8, project.Version);
        Assert.Equal(profile, project.BodyProfile);
        Assert.Null(project.Analysis);
        Assert.Null(project.BodyAnalysis);
    }

    [Fact]
    public void Version8_RoundTripsSeparateGeometricAndBodyCaches()
    {
        var profile = BodyProfile.KoreanAdult;
        var walls = Rectangle(0, 0, 10, 10);
        var mapGrid = WalkabilityGrid.Build(walls, 1, marginCells: 0);
        var bodyGrid = WalkabilityGrid.Build(walls, 1, marginCells: 0, clearanceRadius: profile.ClearanceRadius);
        var mapSource = mapGrid.NearestWalkableCell(new WorldPoint(5, 5))!.Value;
        var bodySource = bodyGrid.NearestWalkableCell(new WorldPoint(5, 5))!.Value;
        var mapResult = DistanceMapCalculator.Compute(mapGrid, new[] { mapSource });
        var bodyResult = DistanceMapCalculator.Compute(bodyGrid, new[] { bodySource });
        var mapCache = DistanceMapCache.Create(mapGrid, mapResult, null, null, null, null);
        var bodyCache = DistanceMapCache.Create(bodyGrid, bodyResult,
            new WorldPoint(6, 6), 2.5,
            [new WorldPoint(5, 5), new WorldPoint(6, 6)],
            [new WorldPoint(5, 5), new WorldPoint(7, 7)], profile);
        var path = Path.Combine(_directory, "cached.walkdistance");
        var expected = new ProjectData(8, 1, 1, walls, [], Analysis: mapCache,
            BodyProfile: profile, BodyAnalysis: bodyCache);

        ProjectFile.Save(path, expected);
        var actual = ProjectFile.Load(path);
        var restoredMap = actual.Analysis!.Restore(mapGrid);
        var restoredBody = actual.BodyAnalysis!.Restore(bodyGrid);

        Assert.Equal(8, actual.Version);
        Assert.True(actual.Analysis.IsCompatibleWith(mapGrid));
        Assert.True(actual.BodyAnalysis.IsCompatibleWith(bodyGrid, profile));
        Assert.Equal(mapResult.MaxDistance, restoredMap.Result.MaxDistance);
        Assert.Equal(bodyResult.MaxDistance, restoredBody.Result.MaxDistance);
        Assert.Equal(bodyCache.QueryPoint, actual.BodyAnalysis.QueryPoint);
        Assert.Equal(bodyCache.QueryPath, actual.BodyAnalysis.QueryPath);
        Assert.Equal(bodyCache.FarthestPath, actual.BodyAnalysis.FarthestPath);
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

        Assert.Equal(8, actual.Version);
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

        Assert.Equal(8, project.Version);
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

        Assert.Equal(8, project.Version);
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

        Assert.Equal(8, project.Version);
        Assert.Equal(BodyProfile.KoreanAdult, project.BodyProfile);
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
        File.WriteAllText(projectPath, "{\"Version\":9}");

        Assert.Throws<InvalidDataException>(() => ProjectFile.Load(projectPath));
    }

    [Fact]
    public void Version7_MigratesTorsoProfileToShoulderOnlyAndDropsBodyCache()
    {
        var path = Path.Combine(_directory, "version7.walkdistance");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Version = 7,
            CellSize = 1.0,
            MetersPerDrawingUnit = 1.0,
            Walls = Rectangle(0, 0, 10, 10),
            Exits = Array.Empty<Segment>(),
            BodyProfile = new { ShoulderWidth = 0.6, TorsoCircumference = 3.0 },
            BodyAnalysis = new { },
        }));

        var project = ProjectFile.Load(path);

        Assert.Equal(8, project.Version);
        Assert.True(project.ApplyBodyMeasurements);
        Assert.Equal(new BodyProfile(0.6), project.BodyProfile);
        Assert.Null(project.BodyAnalysis);
    }

    [Fact]
    public void Version8_RoundTripsDisabledBodyMeasurementsWithoutTorsoData()
    {
        var walls = Rectangle(0, 0, 10, 10);
        var grid = WalkabilityGrid.Build(walls, 1, marginCells: 0);
        var source = grid.NearestWalkableCell(new WorldPoint(5, 5))!.Value;
        var cache = DistanceMapCache.Create(grid, DistanceMapCalculator.Compute(grid, [source]),
            null, null, null, null);
        var path = Path.Combine(_directory, "disabled.walkdistance");

        ProjectFile.Save(path, new ProjectData(8, 1, 1, walls, [], Analysis: cache,
            BodyProfile: new BodyProfile(0.5), BodyAnalysis: cache, ApplyBodyMeasurements: false));
        string json = File.ReadAllText(path);
        var project = ProjectFile.Load(path);

        Assert.DoesNotContain("Torso", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(project.ApplyBodyMeasurements);
        Assert.Equal(new BodyProfile(0.5), project.BodyProfile);
        Assert.NotNull(project.BodyAnalysis);
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
