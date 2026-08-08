using System.Windows;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace WalkDistance.App;

public partial class App : Application
{
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
