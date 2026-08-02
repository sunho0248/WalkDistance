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

    [Fact]
    public void MainWindow_ExitModeOffWithoutPending_SetsAccurateModeOffStatus()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnAddExitModeChanged");

        int pendingCancelIndex = method.IndexOf("출구 선분 그리기가 취소되었습니다.", StringComparison.Ordinal);
        int modeOffStatusIndex = method.IndexOf("출구 지정 모드가 꺼졌습니다.", StringComparison.Ordinal);

        Assert.True(pendingCancelIndex >= 0, "Expected the pending-cancel status message to remain unchanged.");
        Assert.True(
            modeOffStatusIndex >= 0,
            "Expected an accurate status message for turning exit mode off with nothing pending (e.g. right after completing an exit), instead of leaving the add-mode instruction stale.");
        Assert.True(
            modeOffStatusIndex > pendingCancelIndex,
            "The mode-off status must be the else-branch fallback that runs when there is no pending point to cancel.");
        Assert.DoesNotContain("_exitEditor.Clear()", method);
        Assert.DoesNotContain("InvalidateAnalysis", method);
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

    private static string ReadAppMethod(string fileName, string methodSignaturePrefix)
    {
        string source = ReadAppFile(fileName);
        int methodStart = source.IndexOf(methodSignaturePrefix, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Expected to find method starting with '{methodSignaturePrefix}'.");
        int methodEnd = source.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, $"Expected to find the closing brace of '{methodSignaturePrefix}'.");
        return source[methodStart..methodEnd];
    }
}
