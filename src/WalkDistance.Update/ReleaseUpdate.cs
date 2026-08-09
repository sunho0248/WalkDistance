using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace WalkDistance.Update;

public sealed record GitHubRelease(string TagName, IReadOnlyList<GitHubAsset> Assets);

public sealed record GitHubAsset(string Name, string BrowserDownloadUrl, string? Digest);

public static class GitHubReleaseClient
{
    public static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/sunho0248/WalkDistance/releases/latest");

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WalkDistance", "1.3.5"));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<GitHubRelease> GetLatestAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(LatestReleaseUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var assets = new List<GitHubAsset>();

        if (root.TryGetProperty("assets", out var rawAssets))
        {
            foreach (var asset in rawAssets.EnumerateArray())
            {
                assets.Add(new GitHubAsset(
                    RequiredString(asset, "name"),
                    RequiredString(asset, "browser_download_url"),
                    asset.TryGetProperty("digest", out var digest) ? digest.GetString() : null));
            }
        }

        return new GitHubRelease(RequiredString(root, "tag_name"), assets);
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException($"GitHub Release의 {property} 값이 없습니다.");
}

public static class ReleaseUpdate
{
    public const string WindowsX64ZipName = "WalkDistance-win-x64.zip";

    public static bool IsNewer(string releaseTag, Version currentVersion) =>
        TryParseVersion(releaseTag, out var releaseVersion) && releaseVersion > Normalize(currentVersion);

    public static GitHubAsset? SelectWindowsX64Zip(IEnumerable<GitHubAsset> assets)
    {
        var matches = assets
            .Where(asset => string.Equals(asset.Name, WindowsX64ZipName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool TryParseVersion(string text, out Version version)
    {
        text = text.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        if (Version.TryParse(text, out var parsed) && parsed.Major >= 0 && parsed.Minor >= 0 && parsed.Build >= 0)
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version();
        return false;
    }

    private static Version Normalize(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));
}

public static class PackageIntegrity
{
    public static bool IsSha256Match(ReadOnlySpan<byte> bytes, string? digest) =>
        TryParseSha256Digest(digest, out var expected) &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), expected);

    public static bool TryParseSha256Digest(string? digest, out byte[] hash)
    {
        hash = [];
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            hash = Convert.FromHexString(digest["sha256:".Length..]);
            return hash.Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static async Task DownloadVerifiedAsync(
        HttpClient client,
        GitHubAsset asset,
        string destination,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseSha256Digest(asset.Digest, out var expectedHash))
            throw new InvalidDataException("Release 파일의 SHA-256 정보가 없습니다.");

        string fullDestination = Path.GetFullPath(destination);
        string partial = fullDestination + ".download";
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);

        try
        {
            using var response = await client.GetAsync(asset.BrowserDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(partial))
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

            byte[] actualHash;
            await using (var file = File.OpenRead(partial))
                actualHash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                throw new InvalidDataException("다운로드한 파일의 SHA-256 검증에 실패했습니다.");

            File.Move(partial, fullDestination, overwrite: true);
        }
        catch
        {
            if (File.Exists(partial))
                File.Delete(partial);
            throw;
        }
    }
}

public static class UpdatePackage
{
    private const int MaximumEntries = 10_000;
    private const long MaximumUncompressedBytes = 2L * 1024 * 1024 * 1024;

    public static void ExtractSafely(string zipPath, string destination, string requiredRootFile)
    {
        string root = Path.GetFullPath(destination);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("업데이트 작업 폴더가 비어 있지 않습니다.");

        Directory.CreateDirectory(root);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string expectedFile = Path.GetFullPath(Path.Combine(root, requiredRootFile));
        var extractedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool foundExpectedFile = false;
        long totalLength = 0;

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException("업데이트 ZIP의 파일 수가 너무 많습니다.");

        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) ||
                name.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("업데이트 ZIP에 안전하지 않은 경로가 있습니다.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("업데이트 ZIP의 심볼릭 링크는 허용되지 않습니다.");

            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaximumUncompressedBytes)
                throw new InvalidDataException("업데이트 ZIP의 압축 해제 크기가 너무 큽니다.");

            string target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("업데이트 ZIP 경로가 설치 폴더 밖을 가리킵니다.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (!extractedFiles.Add(target))
                throw new InvalidDataException("업데이트 ZIP에 중복 파일이 있습니다.");
            if (string.Equals(Path.GetFileName(target), requiredRootFile, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target, expectedFile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{requiredRootFile} 위치가 올바르지 않습니다.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
            foundExpectedFile |= string.Equals(target, expectedFile, StringComparison.OrdinalIgnoreCase);
        }

        if (!foundExpectedFile || !File.Exists(expectedFile) || new FileInfo(expectedFile).Length == 0)
            throw new InvalidDataException($"업데이트 ZIP에 {requiredRootFile} 파일이 없습니다.");
    }

    public static void CopyDirectory(string source, string destination, Action<string, string>? copyFile = null)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (destination.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("대상 폴더는 원본 폴더 안에 있을 수 없습니다.");

        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            (copyFile ?? CopyFile)(file, target);
        }
    }

    public static void StageUpdater(string installDirectory, string updateDirectory) =>
        CopyDirectory(installDirectory, Path.Combine(updateDirectory, "updater"));

    public static void ReplaceWithBackup(string staging, string installDirectory, string backupDirectory,
        Action<string, string>? copyFile = null)
    {
        if (!Directory.Exists(staging))
            throw new DirectoryNotFoundException("업데이트 작업 폴더를 찾을 수 없습니다.");
        if (Directory.Exists(backupDirectory))
            throw new IOException("기존 버전 백업 폴더가 이미 있습니다.");

        string[] originalDirectories = Directory.Exists(installDirectory)
            ? Directory.EnumerateDirectories(installDirectory, "*", SearchOption.AllDirectories)
                .Select(directory => Path.GetRelativePath(installDirectory, directory)).ToArray()
            : [];
        string[] originalFiles = Directory.Exists(installDirectory)
            ? Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(installDirectory, file)).ToArray()
            : [];
        var movedFiles = new List<string>();

        Directory.CreateDirectory(backupDirectory);
        try
        {
            foreach (string directory in originalDirectories)
                Directory.CreateDirectory(Path.Combine(backupDirectory, directory));
            foreach (string file in originalFiles)
            {
                string source = Path.Combine(installDirectory, file);
                string target = Path.Combine(backupDirectory, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
                movedFiles.Add(file);
            }

            CopyDirectory(staging, installDirectory, copyFile);
        }
        catch
        {
            RestoreFromBackup(installDirectory, backupDirectory, movedFiles.Count == originalFiles.Length);
            throw;
        }
    }

    public static void RestoreFromBackup(string installDirectory, string backupDirectory) =>
        RestoreFromBackup(installDirectory, backupDirectory, clearInstallDirectory: true);

    private static void RestoreFromBackup(string installDirectory, string backupDirectory, bool clearInstallDirectory)
    {
        if (!Directory.Exists(backupDirectory))
            throw new DirectoryNotFoundException("기존 버전 백업 폴더를 찾을 수 없습니다.");

        if (clearInstallDirectory)
            ClearDirectory(installDirectory);

        CopyDirectory(backupDirectory, installDirectory);
    }

    private static void CopyFile(string source, string target) => File.Copy(source, target, overwrite: true);

    private static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
    }
}
