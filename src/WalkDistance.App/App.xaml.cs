using System.Windows;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using WalkDistance.Update;

namespace WalkDistance.App;

public partial class App : Application
{
    private static int _updateCheckStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterFileAssociation();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args is [var path, ..] &&
            string.Equals(System.IO.Path.GetExtension(path), ".walkdistance", StringComparison.OrdinalIgnoreCase))
            window.OpenProject(path);

        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        if (Interlocked.Exchange(ref _updateCheckStarted, 1) != 0)
            return;

        GitHubRelease release;
        try
        {
            using var client = GitHubReleaseClient.CreateClient();
            release = await GitHubReleaseClient.GetLatestAsync(client);
        }
        catch (HttpRequestException)
        {
            return;
        }
        catch (TaskCanceledException)
        {
            return;
        }
        catch
        {
            return;
        }

        Version currentVersion = typeof(App).Assembly.GetName().Version ?? new Version(1, 3, 3);
        GitHubAsset? asset = ReleaseUpdate.SelectWindowsX64Zip(release.Assets);
        if (!ReleaseUpdate.IsNewer(release.TagName, currentVersion) || asset is null ||
            !PackageIntegrity.TryParseSha256Digest(asset.Digest, out _))
            return;

        if (MessageBox.Show(
                $"새 버전 {release.TagName}을(를) 다운로드하고 설치할까요?\n프로그램이 종료된 뒤 업데이트됩니다.",
                "WalkDistance 업데이트",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        try
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "WalkDistance", "update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            string packagePath = Path.Combine(tempDirectory, ReleaseUpdate.WindowsX64ZipName);
            using var client = GitHubReleaseClient.CreateClient();
            await PackageIntegrity.DownloadVerifiedAsync(client, asset, packagePath);
            StartUpdater(tempDirectory, packagePath);
            Shutdown();
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show($"업데이트 파일 검증에 실패했습니다.\n{exception.Message}", "WalkDistance 업데이트",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException)
        {
            MessageBox.Show("업데이트 파일을 다운로드하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도하세요.", "WalkDistance 업데이트",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException)
        {
            MessageBox.Show("업데이트 다운로드 시간이 초과되었습니다. 나중에 다시 시도하세요.", "WalkDistance 업데이트",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"업데이트를 시작하지 못했습니다.\n{exception.Message}", "WalkDistance 업데이트",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void StartUpdater(string tempDirectory, string packagePath)
    {
        string installDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string helperPath = Path.Combine(tempDirectory, "updater", "WalkDistance.Updater.exe");
        if (!File.Exists(Path.Combine(installDirectory, "WalkDistance.Updater.exe")))
            throw new FileNotFoundException("업데이트 도우미를 찾을 수 없습니다.", installDirectory);

        UpdatePackage.StageUpdater(installDirectory, tempDirectory);
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("업데이트 도우미를 찾을 수 없습니다.", helperPath);

        _ = Process.Start(new ProcessStartInfo(helperPath,
            $"--apply --pid {Environment.ProcessId} --zip \"{packagePath}\" --temp-dir \"{tempDirectory}\" --install-dir \"{installDirectory}\"")
        {
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("업데이트 도우미를 시작하지 못했습니다.");
    }

    private static void RegisterFileAssociation()
    {
        try
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException();
            using var extension = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.walkdistance");
            extension?.SetValue(null, "WalkDistance.Project");
            using var command = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\WalkDistance.Project\shell\open\command");
            command?.SetValue(null, $"\"{executable}\" \"%1\"");
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Association is optional; restricted profiles must still start normally.
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
