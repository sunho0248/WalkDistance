namespace WalkDistance.Core.Tests;

public class AppSourceTests
{
    [Fact]
    public void MainWindow_DefaultCellSizeIsPointOne()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("x:Name=\"CellSizeBox\" Width=\"50\" Text=\"0.1\"", xaml);
    }

    [Fact]
    public void MainWindow_ToolbarHint_ExplainsSelectionRequiresExitModeOff()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("출구 모드 끔: 완료된 출구 클릭해 선택 후 Delete로 삭제", xaml);
    }

    [Fact]
    public void MainWindow_BlankCanvasClick_ClearsStaleSelectionStatusBeforeAnalysisNullReturn()
    {
        string source = ReadAppFile("MainWindow.xaml.cs");
        int clearedSelectionStatusIndex = source.IndexOf("출구 선택을 해제했습니다.", StringComparison.Ordinal);
        int analysisNullReturnIndex = source.IndexOf("_grid is null || _result is null", StringComparison.Ordinal);

        Assert.True(clearedSelectionStatusIndex >= 0, "Expected a status message clearing stale exit-selection text.");
        Assert.True(analysisNullReturnIndex >= 0, "Expected the analysis-null early return guard in OnCanvasLeftClick.");
        Assert.True(
            clearedSelectionStatusIndex < analysisNullReturnIndex,
            "Stale selection status must be cleared before the analysis-null early return, so a blank click never leaves stale 'selected' status text when no analysis has run yet.");
    }

    private static string ReadAppFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WalkDistance.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "WalkDistance.App", fileName));
    }
}
