using System.Text.Json;

namespace WalkDistance.Core;

public sealed record ProjectData(string DxfPath, double CellSize, List<WorldPoint> Exits);

public static class ProjectFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(string path, ProjectData data)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(data, Options));
    }

    public static ProjectData Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProjectData>(json, Options)
               ?? throw new InvalidDataException($"Invalid project file: {path}");
    }
}
