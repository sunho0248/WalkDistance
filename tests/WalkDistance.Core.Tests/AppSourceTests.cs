namespace WalkDistance.Core.Tests;

public class AppSourceTests
{
    [Fact]
    public void MainWindow_DefaultCellSizeIsPointOne()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WalkDistance.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string xaml = File.ReadAllText(Path.Combine(
            directory!.FullName,
            "src",
            "WalkDistance.App",
            "MainWindow.xaml"));
        Assert.Contains("x:Name=\"CellSizeBox\" Width=\"50\" Text=\"0.1\"", xaml);
    }
}
