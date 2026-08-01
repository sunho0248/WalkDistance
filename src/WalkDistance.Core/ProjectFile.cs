using System.Text.Json;

namespace WalkDistance.Core;

public sealed record ProjectData(
    int Version,
    double CellSize,
    double MetersPerDrawingUnit,
    List<Segment> Walls,
    List<WorldPoint> Exits,
    string? DxfPath = null);

public static class ProjectFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static void Save(string path, ProjectData data)
    {
        var version2 = data with { Version = 2 };
        ValidateVersion2(version2);
        File.WriteAllText(path, JsonSerializer.Serialize(version2, Options));
    }

    public static ProjectData Load(string path, double? unitlessMetersPerUnit = null)
    {
        string json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        int version = ReadVersion(document.RootElement);

        if (version == 2)
        {
            var data = JsonSerializer.Deserialize<ProjectData>(json, Options)
                       ?? throw new InvalidDataException($"올바르지 않은 프로젝트 파일입니다: {path}");
            ValidateVersion2(data);
            return data;
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
        return new ProjectData(
            Version: 2,
            CellSize: legacy.CellSize,
            MetersPerDrawingUnit: dxf.MetersPerDrawingUnit,
            Walls: dxf.Walls.ToList(),
            Exits: exits,
            DxfPath: dxfPath);
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

    private static void ValidateVersion2(ProjectData data)
    {
        if (data.Version != 2)
        {
            throw new InvalidDataException("저장할 프로젝트 버전은 2여야 합니다.");
        }
        if (!double.IsFinite(data.CellSize) || data.CellSize <= 0)
        {
            throw new InvalidDataException("프로젝트 셀 크기는 0보다 큰 유한한 값이어야 합니다.");
        }
        if (!double.IsFinite(data.MetersPerDrawingUnit) || data.MetersPerDrawingUnit <= 0)
        {
            throw new InvalidDataException("프로젝트 단위 배율이 올바르지 않습니다.");
        }
        if (data.Walls is null || data.Walls.Count == 0 || data.Walls.Any(segment =>
                !IsFinite(segment.Start) || !IsFinite(segment.End)))
        {
            throw new InvalidDataException("프로젝트에 올바른 벽 geometry가 없습니다.");
        }
        if (data.Exits is null || data.Exits.Any(point => !IsFinite(point)))
        {
            throw new InvalidDataException("프로젝트 출구 좌표가 올바르지 않습니다.");
        }
    }

    private static bool IsFinite(WorldPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private sealed record LegacyProjectData(string? DxfPath, double CellSize, List<WorldPoint>? Exits);
}
