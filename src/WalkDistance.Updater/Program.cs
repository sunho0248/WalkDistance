using System.Diagnostics;
using WalkDistance.Update;

namespace WalkDistance.Updater;

internal static class Program
{
    private const string AppFileName = "WalkDistance.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = ParseOptions(args);
            return args.FirstOrDefault() == "--cleanup"
                ? Cleanup(options)
                : Apply(options);
        }
        catch (Exception exception)
        {
            ShowFailure("업데이트 실행 실패", exception.Message);
            return 1;
        }
    }

    private static int Apply(IReadOnlyDictionary<string, string> options)
    {
        string tempDirectory = RequiredPath(options, "--temp-dir");
        string zipPath = RequiredPath(options, "--zip");
        string installDirectory = RequiredPath(options, "--install-dir");
        int oldProcessId = RequiredProcessId(options, "--pid");
        string backupDirectory = Path.Combine(Path.GetDirectoryName(installDirectory)!,
            $".{Path.GetFileName(installDirectory)}.backup-{Guid.NewGuid():N}");
        bool replaced = false;

        try
        {
            if (!IsWithin(Path.GetTempPath(), tempDirectory) || !IsWithin(tempDirectory, zipPath) || !File.Exists(zipPath))
                throw new InvalidDataException("업데이트 파일 위치가 올바르지 않습니다.");

            WaitForExit(oldProcessId);
            string staging = Path.Combine(tempDirectory, "staging");
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            UpdatePackage.ExtractSafely(zipPath, staging, AppFileName);
            UpdatePackage.ReplaceWithBackup(staging, installDirectory, backupDirectory);
            replaced = true;

            string application = Path.Combine(installDirectory, AppFileName);
            string cleaner = Path.Combine(installDirectory, "WalkDistance.Updater.exe");
            if (!File.Exists(application) || !File.Exists(cleaner))
                throw new InvalidDataException("업데이트 패키지에 필요한 실행 파일이 없습니다.");

            string noLaunch = options.ContainsKey("--no-launch") ? " --no-launch" : string.Empty;
            _ = Process.Start(new ProcessStartInfo(cleaner,
                $"--cleanup --pid {Environment.ProcessId} --temp-dir \"{tempDirectory}\" --install-dir \"{installDirectory}\" --backup-dir \"{backupDirectory}\"{noLaunch}")
            {
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("업데이트 정리 도우미를 시작하지 못했습니다.");
            return 0;
        }
        catch (Exception exception)
        {
            if (replaced)
            {
                try
                {
                    UpdatePackage.RestoreFromBackup(installDirectory, backupDirectory);
                }
                catch
                {
                    // The original error is more useful; leave the backup in place for recovery.
                }
            }
            ShowFailure("업데이트 설치 실패", exception.Message);
            return 1;
        }
    }

    private static int Cleanup(IReadOnlyDictionary<string, string> options)
    {
        string tempDirectory = RequiredPath(options, "--temp-dir");
        string installDirectory = RequiredPath(options, "--install-dir");
        string backupDirectory = RequiredPath(options, "--backup-dir");
        int oldProcessId = RequiredProcessId(options, "--pid");
        bool launched = false;

        try
        {
            if (!IsWithin(Path.GetTempPath(), tempDirectory) ||
                !IsBackupForInstall(backupDirectory, installDirectory))
                throw new InvalidDataException("업데이트 백업 폴더 위치가 올바르지 않습니다.");

            WaitForExit(oldProcessId);
            if (!options.ContainsKey("--no-launch"))
            {
                string application = Path.Combine(installDirectory, AppFileName);
                if (!File.Exists(application))
                    throw new FileNotFoundException("새 WalkDistance.exe를 찾을 수 없습니다.", application);
                _ = Process.Start(new ProcessStartInfo(application) { UseShellExecute = true })
                    ?? throw new InvalidOperationException("새 WalkDistance.exe를 시작하지 못했습니다.");
            }
            launched = true;
        }
        catch (Exception exception)
        {
            try
            {
                UpdatePackage.RestoreFromBackup(installDirectory, backupDirectory);
            }
            catch
            {
                // Keep the backup for manual recovery when rollback itself fails.
            }
            ShowFailure("업데이트 마무리 실패", exception.Message);
            return 1;
        }

        if (launched)
        {
            try
            {
                Directory.Delete(backupDirectory, recursive: true);
                DeleteTempDirectory(tempDirectory);
            }
            catch (Exception exception)
            {
                ShowFailure("업데이트 정리 실패", exception.Message);
                return 1;
            }
        }

        return 0;
    }

    private static IReadOnlyDictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length == 0 || (args[0] != "--apply" && args[0] != "--cleanup"))
            throw new ArgumentException("업데이트 실행 인수가 올바르지 않습니다.");

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index++)
        {
            string name = args[index];
            if (name == "--no-launch")
            {
                options.Add(name, string.Empty);
                continue;
            }
            if (index + 1 >= args.Length || !name.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("업데이트 실행 인수가 올바르지 않습니다.");
            options.Add(name, args[++index]);
        }
        return options;
    }

    private static string RequiredPath(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(value))
            : throw new ArgumentException($"{name} 인수가 없습니다.");

    private static int RequiredProcessId(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && int.TryParse(value, out int processId) && processId > 0 && processId != Environment.ProcessId
            ? processId
            : throw new ArgumentException($"{name} 인수가 올바르지 않습니다.");

    private static void WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
                throw new TimeoutException("기존 프로그램이 2분 안에 종료되지 않았습니다.");
        }
        catch (ArgumentException)
        {
            // The process already ended before the helper started.
        }
    }

    private static void DeleteTempDirectory(string tempDirectory)
    {
        if (!IsWithin(Path.GetTempPath(), tempDirectory))
            throw new InvalidDataException("임시 업데이트 폴더 위치가 올바르지 않습니다.");

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(500);
            }
        }

        throw new IOException("임시 업데이트 파일을 정리하지 못했습니다.");
    }

    private static bool IsWithin(string parent, string child)
    {
        string parentPath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string childPath = Path.GetFullPath(child);
        return childPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBackupForInstall(string backupDirectory, string installDirectory) =>
        string.Equals(Path.GetDirectoryName(backupDirectory), Path.GetDirectoryName(installDirectory), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(backupDirectory).StartsWith($".{Path.GetFileName(installDirectory)}.backup-", StringComparison.OrdinalIgnoreCase);

    private static void ShowFailure(string title, string detail) =>
        System.Windows.Forms.MessageBox.Show(
            detail,
            title,
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Error);
}
