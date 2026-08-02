namespace WalkDistance.Core.Tests;

public class AppSourceTests
{
    [Fact]
    public void MainWindow_HasDefaultOnIndependentMapAndPathToggles()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("x:Name=\"MapOverlayToggle\" Content=\"디스턴스 맵\"", xaml);
        Assert.Contains("x:Name=\"PathOverlayToggle\" Content=\"보행경로\"", xaml);
        Assert.Equal(2, xaml.Split("IsChecked=\"True\"").Length - 1);
        Assert.Contains("Checked=\"OnOverlayToggleChanged\" Unchecked=\"OnOverlayToggleChanged\"", xaml);
    }

    [Fact]
    public void MainWindow_DefaultCheckedOverlayEventsAreSafeBeforeCanvasInitialization()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnOverlayToggleChanged");
        int guard = method.IndexOf("DrawingCanvas is not null", StringComparison.Ordinal);
        int redraw = method.IndexOf("Redraw();", StringComparison.Ordinal);

        Assert.True(guard >= 0 && redraw > guard);
    }

    [Fact]
    public void MainWindow_ThresholdIsSessionOnlyValidatedAndUsesStrictExceedance()
    {
        string source = ReadAppFile("MainWindow.xaml.cs");
        Assert.Contains("string.IsNullOrWhiteSpace(ThresholdBox.Text)", source);
        Assert.Contains("double.IsFinite(threshold) && threshold >= 0", source);
        Assert.Contains("d > limit", ReadAppFile("HeatmapRenderer.cs"));
        Assert.DoesNotContain("ThresholdBox.Text = data", source);
        Assert.DoesNotContain("ThresholdBox.Text", ReadAppFile("MainWindow.xaml").Split("프로젝트 저장")[0]);
    }

    [Fact]
    public void MainWindow_OverlayToggleOnlyRedrawsAndGatesTheRequestedLayers()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnOverlayToggleChanged");
        Assert.Contains("Redraw();", method);
        Assert.DoesNotContain("InvalidateAnalysis", method);
        Assert.DoesNotContain("_query", method);

        string redraw = ReadAppMethod("MainWindow.xaml.cs", "private void Redraw");
        Assert.Contains("MapOverlayToggle.IsChecked == true", redraw);
        Assert.Contains("PathOverlayToggle.IsChecked == true", redraw);
    }

    [Fact]
    public void MainWindow_ThresholdEditsValidateImmediatelyButApplyOnCommit()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("TextChanged=\"OnThresholdChanged\"", xaml);
        Assert.Contains("KeyDown=\"OnThresholdKeyDown\"", xaml);
        string changed = ReadAppMethod("MainWindow.xaml.cs", "private void OnThresholdChanged");
        Assert.Contains("ValidateThresholdInput", changed);
        Assert.DoesNotContain("Redraw();", changed);
        Assert.DoesNotContain("RefreshThresholdCaches", changed);
        Assert.DoesNotContain("InvalidateAnalysis", changed);
        Assert.DoesNotContain("_result = null", changed);
        Assert.DoesNotContain("_query", changed);
        Assert.Contains("CultureInfo.CurrentCulture", ReadAppFile("MainWindow.xaml.cs"));
        Assert.Contains("CultureInfo.InvariantCulture", ReadAppFile("MainWindow.xaml.cs"));
        Assert.Contains("ApplyThresholdInput", ReadAppMethod("MainWindow.xaml.cs", "private void OnThresholdLostFocus"));
        Assert.Contains("ApplyThresholdInput", ReadAppMethod("MainWindow.xaml.cs", "private void OnThresholdKeyDown"));
    }

    [Fact]
    public void MainWindow_InvalidThresholdCalculationDoesNotInvalidateExistingAnalysis()
    {
        string calculate = ReadAppMethod("MainWindow.xaml.cs", "private void OnCalculate");
        int thresholdGuard = calculate.IndexOf("TryParseThreshold", StringComparison.Ordinal);
        int buildGrid = calculate.IndexOf("WalkabilityGrid.Build", StringComparison.Ordinal);
        Assert.True(thresholdGuard >= 0 && thresholdGuard < buildGrid);
        int acceptedThreshold = calculate.IndexOf("_threshold = threshold", thresholdGuard, StringComparison.Ordinal);
        string guard = calculate[thresholdGuard..acceptedThreshold];
        Assert.DoesNotContain("InvalidateAnalysis", guard);
    }

    [Fact]
    public void MainWindow_LabelPlacementTriesAlternateMidpointsAndAlwaysFallsBackPerLevel()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private Point SelectLabelPoint");
        Assert.Contains("Select(contour =>", method);
        Assert.Contains("FirstOrDefault", method);
        Assert.Contains("?? candidates[0]", method);
        string draw = ReadAppMethod("MainWindow.xaml.cs", "private void DrawContours");
        Assert.Contains("SelectLabelPoint", draw);
    }

    [Fact]
    public void MainWindow_RedrawUsesCachesWithoutRegeneratingFullMapArtifacts()
    {
        string redraw = ReadAppMethod("MainWindow.xaml.cs", "private void Redraw");
        Assert.DoesNotContain("DistanceContourGenerator", redraw);
        Assert.DoesNotContain("HeatmapRenderer.Render", redraw);
        Assert.Contains("_normalContours", redraw);
        Assert.Contains("_thresholdContours", redraw);
        Assert.Contains("_heatmapBitmap", redraw);
    }

    [Fact]
    public void MainWindow_ContoursUseBatchedStreamGeometryNotOneLinePerSegment()
    {
        string draw = ReadAppMethod("MainWindow.xaml.cs", "private void DrawContours");
        string batched = ReadAppMethod("MainWindow.xaml.cs", "private void AddContourPath");
        Assert.Contains("AddContourPath", draw);
        Assert.Contains("StreamGeometry", batched);
        Assert.Contains("System.Windows.Shapes.Path", batched);
        Assert.DoesNotContain("new Line", draw);
    }

    [Fact]
    public void MainWindow_TogglesOnlyRedrawAndDoNotClearCachesOrState()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnOverlayToggleChanged");
        Assert.Contains("Redraw();", method);
        Assert.DoesNotContain("Cache", method);
        Assert.DoesNotContain("_result", method);
        Assert.DoesNotContain("_query", method);
    }

    [Fact]
    public void MainWindow_DefaultCellSizeIsPointZeroFive()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("x:Name=\"CellSizeBox\" Width=\"50\" Text=\"0.05\"", xaml);
    }

    [Fact]
    public void MainWindow_ToolbarHint_ExplainsSelectionRequiresExitModeOff()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("출구 모드 끔: 완료된 출구 클릭해 선택 후 Delete로 삭제", xaml);
    }

    [Fact]
    public void MainWindow_ToolbarHint_ExplainsAutomaticShapeSnapping()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("자동 스냅", xaml);
    }

    [Fact]
    public void MainWindow_UsesGeometrySnapForBothClickAndPreview()
    {
        string source = ReadAppFile("MainWindow.xaml.cs");
        int clickSnapIndex = source.IndexOf("var snappedPoint = SnapToNearestWall(worldPoint, out bool snapped);", StringComparison.Ordinal);
        int previewSnapIndex = source.IndexOf("_previewEnd = SnapToNearestWall(_transform.ToWorld(e.GetPosition(DrawingCanvas)), out _);", StringComparison.Ordinal);
        int scaleUsageIndex = source.IndexOf("ExitSnapToleranceScreenPixels / _transform.Scale", StringComparison.Ordinal);

        Assert.True(clickSnapIndex >= 0, "Expected committed exit clicks to snap via SnapToNearestWall with an out-bool snapped seam.");
        Assert.True(previewSnapIndex >= 0, "Expected the live preview point to snap via SnapToNearestWall.");
        Assert.True(scaleUsageIndex >= 0, "Expected the screen-pixel tolerance to be converted to world units via ViewTransform.Scale.");
    }

    [Fact]
    public void MainWindow_SnapToNearestWall_ReportsWhetherItActuallySnappedViaOutBool()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private WorldPoint SnapToNearestWall");

        Assert.Contains("out bool snapped", method);
        Assert.Contains("GeometrySnap.TrySnapToNearest(worldPoint, _walls, snapToleranceWorld)", method);
        // Only one nearest-point calculation: the out-bool is derived from its result, not a duplicate call.
        int firstCallIndex = method.IndexOf("TrySnapToNearest", StringComparison.Ordinal);
        int secondCallIndex = method.IndexOf("TrySnapToNearest", firstCallIndex + 1, StringComparison.Ordinal);
        Assert.True(firstCallIndex >= 0, "Expected SnapToNearestWall to call GeometrySnap.TrySnapToNearest.");
        Assert.Equal(-1, secondCallIndex);
    }

    [Fact]
    public void MainWindow_ExitClickStatus_OnlyClaimsSnapForAClickThatActuallySnapped()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasLeftClick");

        Assert.Contains("out bool snapped", method);
        Assert.Contains("snapped ?", method);
        Assert.Contains("자동 스냅", method);

        // The old wording unconditionally claimed a snap on every commit/start-point
        // status regardless of whether GeometrySnap actually found a nearby segment.
        Assert.DoesNotContain("개 지정됨 (벽/도형에 자동 스냅) · 우클릭", method);
        Assert.DoesNotContain("시작점 지정됨(벽/도형에 자동 스냅)", method);
    }

    [Fact]
    public void MainWindow_OpenProject_HonorsStoredCellSizeWithoutForcedDefaultOverwrite()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnOpenProject");

        Assert.Contains("CellSizeBox.Text = data.CellSize.ToString(CultureInfo.InvariantCulture);", method);
        Assert.DoesNotContain("\"0.05\"", method);
        Assert.DoesNotContain("\"0.1\"", method);
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

    [Fact]
    public void HeatmapRenderer_RendersOnlyInteriorWalkableCells()
    {
        string source = ReadAppFile("HeatmapRenderer.cs");

        Assert.Contains("!grid.IsWalkable(col, gridRow)", source);
    }

    [Fact]
    public void MainWindow_RejectsOutsideBuildingBeforePathLookup()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasLeftClick");
        int outsideGuard = method.IndexOf("!_grid.Contains(worldPoint)", StringComparison.Ordinal);
        int findPath = method.IndexOf("DistanceMapCalculator.FindPath", StringComparison.Ordinal);

        Assert.True(outsideGuard >= 0);
        Assert.True(findPath > outsideGuard);
        Assert.Contains("선택 지점은 건물 외부입니다.", method);
    }

    [Fact]
    public void MainWindow_CalculationFailuresClearStaleAnalysisAndShowKoreanGuidance()
    {
        string source = ReadAppFile("MainWindow.xaml.cs");
        string preFailureClear = Slice(source, "_grid = WalkabilityGrid.Build", "if (_grid.InteriorCellCount == 0)");
        string openOutline = Slice(source, "if (_grid.InteriorCellCount == 0)", "var sources =");
        string noExit = Slice(source, "if (sources.Count == 0)", "_result = DistanceMapCalculator.Compute");
        string sizeLimit = Slice(source, "catch (GridSizeLimitExceededException ex)", "private void ResetAnalysis");

        AssertFailureClearsQuery(preFailureClear);
        Assert.Contains("닫힌 건물 외곽선", openOutline);
        Assert.Contains("계산 중단", openOutline);
        Assert.Contains("사용 가능한 출구", noExit);
        Assert.Contains("계산 중단", noExit);
        AssertFailureClearsQuery(sizeLimit);
        Assert.Contains("격자가 너무 큽니다", sizeLimit);
        Assert.Contains("계산 중단", sizeLimit);
    }

    private static void AssertFailureClearsQuery(string source)
    {
        Assert.Contains("_result = null", source);
        Assert.Contains("_queryPoint = null", source);
        Assert.Contains("_queryDistance = null", source);
        Assert.Contains("_queryPathPoints = null", source);
    }

    private static string Slice(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        int endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return source[startIndex..endIndex];
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
