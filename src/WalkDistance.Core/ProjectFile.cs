using System.Text.Json;
using System.Text.Json.Serialization;

namespace WalkDistance.Core;

public sealed record ProjectData(
    int Version,
    double CellSize,
    double MetersPerDrawingUnit,
    List<Segment> Walls,
    List<Segment> Exits,
    string? DxfPath = null,
    List<List<WorldPoint>>? ExitPaths = null,
    DistanceMapCache? Analysis = null,
    BodyProfile? BodyProfile = null,
    DistanceMapCache? BodyAnalysis = null,
    bool ApplyBodyMeasurements = true);

public static class ProjectFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static void Save(string path, ProjectData data)
    {
        var current = NormalizeCurrent(data with { Version = 8 });
        ValidateCurrentVersion(current);
        File.WriteAllText(path, JsonSerializer.Serialize(current, Options));
    }

    public static ProjectData Load(string path, double? unitlessMetersPerUnit = null)
    {
        string json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        int version = ReadVersion(document.RootElement);

        if (version is 3 or 4 or 5 or 6 or 7 or 8)
        {
            var data = JsonSerializer.Deserialize<ProjectData>(json, Options)
                       ?? throw new InvalidDataException($"올바르지 않은 프로젝트 파일입니다: {path}");
            var current = NormalizeCurrent(data with
            {
                Version = 8,
                Analysis = version == 6 ? null : data.Analysis,
                BodyAnalysis = version < 8 ? null : data.BodyAnalysis,
            });
            ValidateCurrentVersion(current);
            return current;
        }

        if (version == 2)
        {
            var version2 = JsonSerializer.Deserialize<LegacyProjectDataV2>(json, Options)
                           ?? throw new InvalidDataException($"올바르지 않은 프로젝트 파일입니다: {path}");
            var migrated = NormalizeCurrent(new ProjectData(
                Version: 8,
                CellSize: version2.CellSize,
                MetersPerDrawingUnit: version2.MetersPerDrawingUnit,
                Walls: version2.Walls ?? [],
                Exits: MigrateExits(version2.Exits ?? []),
                DxfPath: version2.DxfPath));
            ValidateCurrentVersion(migrated);
            return migrated;
        }

        if (version != 1)
        {
            throw new InvalidDataException($"지원하지 않는 프로젝트 버전입니다: {version}");
        }

        var legacy = JsonSerializer.Deserialize<LegacyProjectData>(json, Options)
                     ?? throw new InvalidDataException($"올바르지 않은 프로젝트 파일입니다: {path}");
        if (string.IsNullOrWhiteSpace(legacy.DxfPath))
        {
            throw new InvalidDataException("v1 프로젝트에 DXF 경로가 없습니다.");
        }
        if (!double.IsFinite(legacy.CellSize) || legacy.CellSize <= 0)
        {
            throw new InvalidDataException("프로젝트 셀 크기는 0보다 큰 유한한 값이어야 합니다.");
        }

        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string dxfPath = Path.IsPathRooted(legacy.DxfPath)
            ? legacy.DxfPath
            : Path.GetFullPath(Path.Combine(projectDirectory, legacy.DxfPath));
        if (!File.Exists(dxfPath))
        {
            throw new FileNotFoundException("v1 프로젝트의 원본 DXF 파일을 찾을 수 없습니다.", dxfPath);
        }

        var dxf = DxfLoader.Load(dxfPath, unitlessMetersPerUnit);
        var exits = (legacy.Exits ?? []).Select(point => new WorldPoint(
            point.X * dxf.MetersPerDrawingUnit,
            point.Y * dxf.MetersPerDrawingUnit)).ToList();
        var legacyCurrent = NormalizeCurrent(new ProjectData(
            Version: 8,
            CellSize: legacy.CellSize,
            MetersPerDrawingUnit: dxf.MetersPerDrawingUnit,
            Walls: dxf.Walls.ToList(),
            Exits: MigrateExits(exits),
            DxfPath: dxfPath,
            ExitPaths: MigrateExitPaths(exits)));
        ValidateCurrentVersion(legacyCurrent);
        return legacyCurrent;
    }

    private static int ReadVersion(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "Version", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var version))
                {
                    return version;
                }
                throw new InvalidDataException("프로젝트 Version 값이 올바르지 않습니다.");
            }
        }

        return 1;
    }

    private static void ValidateCurrentVersion(ProjectData data)
    {
        if (data.Version != 8)
        {
            throw new InvalidDataException("저장할 프로젝트 버전은 8이어야 합니다.");
        }
        if (!double.IsFinite(data.CellSize) || data.CellSize <= 0)
        {
            throw new InvalidDataException("프로젝트 셀 크기는 0보다 큰 유한한 값이어야 합니다.");
        }
        if (!double.IsFinite(data.MetersPerDrawingUnit) || data.MetersPerDrawingUnit <= 0)
        {
            throw new InvalidDataException("프로젝트 단위 배율이 올바르지 않습니다.");
        }
        if (data.BodyProfile is not { IsValid: true })
        {
            throw new InvalidDataException("어깨너비는 0보다 큰 유한한 값이어야 합니다.");
        }
        if (data.Walls is null || data.Walls.Count == 0 || data.Walls.Any(segment =>
                !IsFinite(segment.Start) || !IsFinite(segment.End)))
        {
            throw new InvalidDataException("프로젝트에 올바른 벽 geometry가 없습니다.");
        }
        if (data.Exits is null || data.Exits.Any(segment =>
                !IsFinite(segment.Start) || !IsFinite(segment.End)))
        {
            throw new InvalidDataException("프로젝트 출구 좌표가 올바르지 않습니다.");
        }
        if (data.ExitPaths is null || data.ExitPaths.Any(path =>
                path is null || path.Count < 2 || path.Any(point => !IsFinite(point))))
        {
            throw new InvalidDataException("프로젝트 출구 경로가 올바르지 않습니다.");
        }
    }

    private static ProjectData NormalizeCurrent(ProjectData data)
    {
        var paths = data.ExitPaths ?? MigrateExitPaths(data.Exits ?? []);
        if (paths.Any(path => path is null || path.Count < 2))
        {
            throw new InvalidDataException("프로젝트 출구 경로는 점이 2개 이상이어야 합니다.");
        }

        var snapshot = paths.Select(path => path.ToList()).ToList();
        var profile = data.BodyProfile ?? BodyProfile.KoreanAdult;
        if (!profile.IsValid)
        {
            throw new InvalidDataException("어깨너비는 0보다 큰 유한한 값이어야 합니다.");
        }

        return data with
        {
            Version = 8,
            ExitPaths = snapshot,
            Exits = snapshot.Select(path => new Segment(path[0], path[^1])).ToList(),
            BodyProfile = profile,
            Analysis = data.Analysis is { BodyProfile: null, IsSane: true } ? data.Analysis : null,
            BodyAnalysis = data.BodyAnalysis is { IsSane: true } bodyCache &&
                           (bodyCache.BodyProfile == profile ||
                            !data.ApplyBodyMeasurements && bodyCache.BodyProfile is null)
                ? data.BodyAnalysis
                : null,
        };
    }

    private static List<Segment> MigrateExits(IEnumerable<WorldPoint> exits) =>
        exits.Select(point => new Segment(point, point)).ToList();

    private static List<List<WorldPoint>> MigrateExitPaths(IEnumerable<Segment> exits) =>
        exits.Select(exit => new List<WorldPoint> { exit.Start, exit.End }).ToList();

    private static List<List<WorldPoint>> MigrateExitPaths(IEnumerable<WorldPoint> exits) =>
        exits.Select(point => new List<WorldPoint> { point, point }).ToList();

    private static bool IsFinite(WorldPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private sealed record LegacyProjectData(string? DxfPath, double CellSize, List<WorldPoint>? Exits);

    private sealed record LegacyProjectDataV2(
        int Version,
        double CellSize,
        double MetersPerDrawingUnit,
        List<Segment>? Walls,
        List<WorldPoint>? Exits,
        string? DxfPath);
}
