using System.IO.Compression;
using System.Security.Cryptography;
using WalkDistance.Update;

namespace WalkDistance.Core.Tests;

public sealed class ReleaseUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WalkDistanceTests", Guid.NewGuid().ToString("N"));

    public ReleaseUpdateTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("v1.3.4", true)]
    [InlineData("1.3.10", true)]
    [InlineData("v1.3.3", false)]
    [InlineData("v1.2.99", false)]
    [InlineData("not-a-version", false)]
    public void IsNewer_UsesNumericVersions(string tag, bool expected) =>
        Assert.Equal(expected, ReleaseUpdate.IsNewer(tag, new Version(1, 3, 3, 0)));

    [Fact]
    public void SelectWindowsX64Zip_RequiresOneExpectedAsset()
    {
        var expected = new GitHubAsset("WalkDistance-win-x64.zip", "https://example.test/app.zip", "sha256:00");
        var selected = ReleaseUpdate.SelectWindowsX64Zip([new GitHubAsset("source.zip", "", null), expected]);

        Assert.Same(expected, selected);
        Assert.Null(ReleaseUpdate.SelectWindowsX64Zip([expected, expected]));
    }

    [Fact]
    public void IsSha256Match_RequiresTheReleaseDigest()
    {
        byte[] content = "WalkDistance"u8.ToArray();
        string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(content));

        Assert.True(PackageIntegrity.IsSha256Match(content, digest));
        Assert.False(PackageIntegrity.IsSha256Match(content, "sha256:00"));
        Assert.False(PackageIntegrity.IsSha256Match("modified"u8, digest));
    }

    [Fact]
    public async Task DownloadVerifiedAsync_ClosesVerifiedFileBeforePublishing()
    {
        byte[] content = "WalkDistance update"u8.ToArray();
        string destination = Path.Combine(_directory, "WalkDistance-win-x64.zip");
        var asset = new GitHubAsset("package.zip", "https://example.test/package.zip",
            "sha256:" + Convert.ToHexString(SHA256.HashData(content)));
        using var client = new System.Net.Http.HttpClient(new BytesHandler(content));

        await PackageIntegrity.DownloadVerifiedAsync(client, asset, destination);

        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".download"));
    }

    [Fact]
    public void ExtractSafely_RejectsZipSlipAndMissingApplication()
    {
        string zip = Path.Combine(_directory, "unsafe.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open()))
            writer.Write("no");

        Assert.Throws<InvalidDataException>(() => UpdatePackage.ExtractSafely(zip, Path.Combine(_directory, "stage"), "WalkDistance.exe"));
        Assert.False(File.Exists(Path.Combine(_directory, "outside.txt")));
    }

    [Fact]
    public void ExtractSafely_RequiresRootWalkDistanceExecutable()
    {
        string zip = Path.Combine(_directory, "wrong-layout.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("nested/WalkDistance.exe").Open()))
            writer.Write("app");

        Assert.Throws<InvalidDataException>(() => UpdatePackage.ExtractSafely(zip, Path.Combine(_directory, "stage"), "WalkDistance.exe"));
    }

    [Fact]
    public void ExtractSafely_ExtractsExpectedPackage()
    {
        string zip = Path.Combine(_directory, "valid.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("WalkDistance.exe").Open());
            writer.Write("app");
        }

        string stage = Path.Combine(_directory, "stage");
        UpdatePackage.ExtractSafely(zip, stage, "WalkDistance.exe");

        Assert.Equal("app", File.ReadAllText(Path.Combine(stage, "WalkDistance.exe")));
    }

    [Fact]
    public void StageUpdater_LeavesDownloadedPackageReadable()
    {
        string install = Path.Combine(_directory, "install");
        string update = Path.Combine(_directory, "update");
        string package = Path.Combine(update, ReleaseUpdate.WindowsX64ZipName);
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(update);
        File.WriteAllText(Path.Combine(install, "WalkDistance.Updater.exe"), "helper");
        File.WriteAllText(Path.Combine(install, ReleaseUpdate.WindowsX64ZipName), "old package");
        File.WriteAllText(package, "downloaded package");

        using (File.Open(package, FileMode.Open, FileAccess.Read, FileShare.None))
            UpdatePackage.StageUpdater(install, update);

        Assert.Equal("downloaded package", File.ReadAllText(package));
        Assert.True(File.Exists(Path.Combine(update, "updater", "WalkDistance.Updater.exe")));
    }

    [Fact]
    public void CopyDirectory_RejectsDestinationInsideSource()
    {
        string source = Path.Combine(_directory, "source");
        Directory.CreateDirectory(source);

        Assert.Throws<IOException>(() => UpdatePackage.CopyDirectory(source, source));
        Assert.Throws<IOException>(() => UpdatePackage.CopyDirectory(source, Path.Combine(source, "copy")));
    }

    [Fact]
    public void ReplaceWithBackup_RestoresInstallWhenCopyFails()
    {
        string install = Path.Combine(_directory, "install");
        string staging = Path.Combine(_directory, "staging");
        string backup = Path.Combine(_directory, "backup");
        Directory.CreateDirectory(Path.Combine(install, "runtime"));
        Directory.CreateDirectory(Path.Combine(staging, "runtime"));
        File.WriteAllText(Path.Combine(install, "WalkDistance.exe"), "old app");
        File.WriteAllText(Path.Combine(install, "runtime", "old.dll"), "old runtime");
        File.WriteAllText(Path.Combine(staging, "WalkDistance.exe"), "new app");
        File.WriteAllText(Path.Combine(staging, "runtime", "new.dll"), "new runtime");

        int copied = 0;
        Assert.Throws<IOException>(() => UpdatePackage.ReplaceWithBackup(staging, install, backup, (source, target) =>
        {
            if (++copied == 2)
                throw new IOException("injected copy failure");
            File.Copy(source, target, overwrite: true);
        }));

        Assert.Equal("old app", File.ReadAllText(Path.Combine(install, "WalkDistance.exe")));
        Assert.Equal("old runtime", File.ReadAllText(Path.Combine(install, "runtime", "old.dll")));
        Assert.False(File.Exists(Path.Combine(install, "runtime", "new.dll")));
        Assert.True(File.Exists(Path.Combine(backup, "WalkDistance.exe")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class BytesHandler(byte[] content) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.ByteArrayContent(content),
            });
    }
}
