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
    public void MainWindow_RedrawPassesComputedDistancesToPathLabelsGatedWithPaths()
    {
        string redraw = ReadAppMethod("MainWindow.xaml.cs", "private void Redraw");
        string pathOverlay = Slice(redraw, "if (showPaths)", "if (_grid is not null");

        Assert.Contains("AddPathLabel(_farthestPathPoints, _result?.MaxDistance, Brushes.OrangeRed", pathOverlay);
        Assert.Contains("AddPathLabel(_queryPathPoints, _queryDistance, Brushes.DeepSkyBlue", pathOverlay);
        Assert.Equal(2, pathOverlay.Split("AddPathLabel(").Length - 1);
        Assert.Equal(2, redraw.Split("AddPathLabel(").Length - 1);
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
    public void MainWindow_LabelPlacementTriesAlternateMidpointsAndAlwaysFallsBackPerComponent()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private Point SelectLabelPoint");
        Assert.Contains("Select(contour =>", method);
        Assert.Contains("FirstOrDefault", method);
        Assert.Contains("?? candidates[0]", method);
        string draw = ReadAppMethod("MainWindow.xaml.cs", "private void DrawContours");
        Assert.Contains("SelectLabelPoint", draw);
    }

    [Fact]
    public void MainWindow_MeasuresAndClampsEveryContourLabelToTheCanvas()
    {
        string draw = ReadAppMethod("MainWindow.xaml.cs", "private void DrawContours");
        Assert.Contains("label.Measure", draw);
        Assert.Contains("label.DesiredSize", draw);
        Assert.Contains("LabelPlacement.ClampToCanvas", draw);
        Assert.Contains("DrawingCanvas.ActualWidth", draw);
        Assert.Contains("DrawingCanvas.ActualHeight", draw);
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
    public void MainWindow_ContoursUseDistinctMinorAndMajorStylesWithTenMeterLabels()
    {
        string draw = ReadAppMethod("MainWindow.xaml.cs", "private void DrawContours");
        string refreshAnalysis = ReadAppMethod("MainWindow.xaml.cs", "private void RefreshAnalysisCaches");

        Assert.Contains("level.Key % 10 == 0", draw);
        Assert.Contains("Brushes.DimGray", draw);
        Assert.Contains("Brushes.Black", draw);
        Assert.Contains("1.6", draw);
        Assert.Contains("0.8", draw);
        Assert.Contains("contour.Level % 10 == 0", refreshAnalysis);
        Assert.Contains("Brushes.White, 2.5", draw);
    }

    [Fact]
    public void MainWindow_CachesTenMeterContourComponentsWithGridScaledTolerance()
    {
        string source = ReadAppFile("MainWindow.xaml.cs");
        string refreshAnalysis = ReadAppMethod("MainWindow.xaml.cs", "private void RefreshAnalysisCaches");
        string refreshThreshold = ReadAppMethod("MainWindow.xaml.cs", "private void RefreshThresholdCaches");
        string clear = ReadAppMethod("MainWindow.xaml.cs", "private void ClearAnalysisCaches");
        string redraw = ReadAppMethod("MainWindow.xaml.cs", "private void Redraw");
        string draw = ReadAppMethod("MainWindow.xaml.cs", "private void DrawContours");

        Assert.Contains("IReadOnlyList<IReadOnlyList<DistanceContour>> _normalContourComponents = []", source);
        int contours = refreshAnalysis.IndexOf("_normalContours = DistanceContourGenerator.Generate", StringComparison.Ordinal);
        int components = refreshAnalysis.IndexOf("_normalContourComponents = DistanceContourAssembler.Assemble", StringComparison.Ordinal);
        Assert.True(contours >= 0 && components > contours);
        Assert.Contains("Math.Max(_grid.CellSize * 1e-6, 1e-9)", refreshAnalysis);
        Assert.DoesNotContain("DistanceContourAssembler.Assemble", refreshThreshold);
        Assert.Contains("_normalContourComponents = []", clear);
        Assert.Contains("DrawContours(_normalContours, _thresholdContours, _normalContourComponents)", redraw);
        Assert.DoesNotContain("DistanceContourAssembler.Assemble", draw);
    }

    [Fact]
    public void MainWindow_LabelsEveryCachedTenMeterContourComponent()
    {
        string draw = ReadAppMethod("MainWindow.xaml.cs", "private void DrawContours");

        Assert.Contains("IReadOnlyList<IReadOnlyList<DistanceContour>> normalContourComponents", draw);
        Assert.Contains("foreach (var component", draw);
        Assert.Contains("SelectLabelPoint(component", draw);
        Assert.Contains("component[0].Level", draw);
        Assert.Contains("AddContourPath(thresholdContours, Brushes.White, 2.5)", draw);
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
    public void MainWindow_UsesWallIndexForBothClickAndPreviewSnapping()
    {
        string source = ReadAppFile("MainWindow.xaml.cs");
        int clickSnapIndex = source.IndexOf("var snappedPoint = SnapToNearestWall(worldPoint, out bool snapped);", StringComparison.Ordinal);
        int previewSnapIndex = source.IndexOf("SnapToNearestWall(worldPoint, out _)", StringComparison.Ordinal);
        int scaleUsageIndex = source.IndexOf("ExitSnapToleranceScreenPixels / _transform.Scale", StringComparison.Ordinal);

        Assert.True(clickSnapIndex >= 0, "Expected committed exit clicks to snap via SnapToNearestWall with an out-bool snapped seam.");
        Assert.True(previewSnapIndex >= 0, "Expected the live preview point to snap via SnapToNearestWall.");
        Assert.True(scaleUsageIndex >= 0, "Expected the screen-pixel tolerance to be converted to world units via ViewTransform.Scale.");
        Assert.Contains("private WallIndex _wallIndex = WallIndex.Build([]);", source);
    }

    [Fact]
    public void MainWindow_SnapToNearestWall_ReportsWhetherItActuallySnappedViaOutBool()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private WorldPoint SnapToNearestWall");

        Assert.Contains("out bool snapped", method);
        Assert.Contains("_wallIndex.TrySnapToNearest(worldPoint, SnapToleranceWorld())", method);
        // Only one nearest-point calculation: the out-bool is derived from its result, not a duplicate call.
        int firstCallIndex = method.IndexOf("TrySnapToNearest", StringComparison.Ordinal);
        int secondCallIndex = method.IndexOf("TrySnapToNearest", firstCallIndex + 1, StringComparison.Ordinal);
        Assert.True(firstCallIndex >= 0, "Expected SnapToNearestWall to call the WallIndex spatial snap.");
        Assert.Equal(-1, secondCallIndex);
    }

    [Fact]
    public void MainWindow_WallIndexIsRebuiltOnlyWhenWallsLoadNotOnEveryPreview()
    {
        string source = ReadAppFile("MainWindow.xaml.cs");
        int wallsAssignIndex = source.IndexOf("_walls = document.Walls.ToList();", StringComparison.Ordinal);
        int wallIndexBuildIndex = source.IndexOf("_wallIndex = WallIndex.Build(_walls);", wallsAssignIndex, StringComparison.Ordinal);
        Assert.True(wallsAssignIndex >= 0 && wallIndexBuildIndex > wallsAssignIndex,
            "Expected the WallIndex to be rebuilt right after walls load in OnOpenDxf.");

        int openProjectWallsAssign = source.IndexOf("_walls = data.Walls.ToList();", StringComparison.Ordinal);
        int openProjectWallIndexBuild = source.IndexOf("_wallIndex = WallIndex.Build(_walls);", openProjectWallsAssign, StringComparison.Ordinal);
        Assert.True(openProjectWallsAssign >= 0 && openProjectWallIndexBuild > openProjectWallsAssign,
            "Expected the WallIndex to be rebuilt right after walls load in OnOpenProject.");

        // "no static wall rebuild during previews": the only two WallIndex.Build
        // call sites in the whole file are the two load paths asserted above.
        Assert.Equal(2, source.Split("WallIndex.Build(_walls)").Length - 1);
        string mouseMove = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasMouseMove");
        string redraw = ReadAppMethod("MainWindow.xaml.cs", "private void Redraw");
        Assert.DoesNotContain("WallIndex.Build", mouseMove);
        Assert.DoesNotContain("WallIndex.Build", redraw);
    }

    [Fact]
    public void MainWindow_WallsCacheImmutableGeometryOnLoadAndOnlyTransformItOnRedraw()
    {
        string source = ReadAppFile("MainWindow.xaml.cs");
        string redraw = ReadAppMethod("MainWindow.xaml.cs", "private void Redraw");
        string cacheWalls = ReadAppMethod("MainWindow.xaml.cs", "private void CacheWallGeometry");
        string drawWalls = ReadAppMethod("MainWindow.xaml.cs", "private void DrawWalls");
        Assert.Contains("DrawWalls();", redraw);
        Assert.Contains("new StreamGeometry", cacheWalls);
        Assert.Contains("geometry.Freeze();", cacheWalls);
        Assert.Contains("_wallPath.Data = geometry;", cacheWalls);
        Assert.Contains("_wallPath.RenderTransform = new MatrixTransform", drawWalls);
        Assert.DoesNotContain("new StreamGeometry", drawWalls);
        Assert.DoesNotContain("foreach (var wall in _walls)", drawWalls);
        Assert.Equal(2, source.Split("CacheWallGeometry();").Length - 1);
        Assert.True(source.IndexOf("CacheWallGeometry();", source.IndexOf("_walls = document.Walls.ToList();", StringComparison.Ordinal), StringComparison.Ordinal) >= 0);
        Assert.True(source.IndexOf("CacheWallGeometry();", source.IndexOf("_walls = data.Walls.ToList();", StringComparison.Ordinal), StringComparison.Ordinal) >= 0);
        Assert.DoesNotContain("new Line", redraw);
        Assert.DoesNotContain("new Line", drawWalls);
    }

    [Fact]
    public void MainWindow_ExitPathsRenderTheFullTracedRouteNotJustTheChord()
    {
        string redraw = ReadAppMethod("MainWindow.xaml.cs", "private void Redraw");
        Assert.Contains("_exitEditor.Paths.Count", redraw);
        Assert.Contains("new Polyline", redraw);
        Assert.DoesNotContain("new Line", redraw);
    }

    [Fact]
    public void MainWindow_CalculateUsesEveryConsecutiveExitPathSegment()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnCalculate");

        Assert.Contains("_exitEditor.Paths", method);
        Assert.Contains(".Zip(path.Skip(1), (start, end) => new Segment(start, end))", method);
        Assert.Contains("WalkableSourcesNearSegment(exit, _grid.CellSize, exitGroupId)", method);
    }

    [Fact]
    public void MainWindow_ProjectSaveAndLoadUseFullExitPaths()
    {
        string save = ReadAppMethod("MainWindow.xaml.cs", "private void OnSaveProject");
        string load = ReadAppMethod("MainWindow.xaml.cs", "private void OnOpenProject");

        Assert.Contains("ExitPaths: _exitEditor.Paths", save);
        Assert.DoesNotContain("_exitEditor.Segments", save);
        Assert.Contains("_exitEditor.LoadPaths(data.ExitPaths!)", load);
    }

    [Fact]
    public void MainWindow_FixedLengthExitBox_ExistsInToolbarAsOptionalInput()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("x:Name=\"FixedExitLengthBox\"", xaml);
    }

    [Fact]
    public void MainWindow_FixedExitLength_ParsesOptionalPositiveNumberOnly()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private bool TryGetFixedExitLength");
        Assert.Contains("string.IsNullOrWhiteSpace(FixedExitLengthBox.Text)", method);
        Assert.Contains("double.IsFinite(length) && length > 0", method);
    }

    [Fact]
    public void MainWindow_SecondClick_TracesFixedLengthWhenLengthBoxHasAValue()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasLeftClick");
        Assert.Contains("TryGetFixedExitLength(out double fixedLength)", method);
        Assert.Contains("_wallIndex.TraceFixedLength(pendingStart, worldPoint, fixedLength, SnapToleranceWorld())", method);
    }

    [Fact]
    public void MainWindow_MouseMovePreview_TracesFixedLengthRouteWhenLengthBoxHasAValue()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasMouseMove");
        Assert.Contains("TryGetFixedExitLength(out double fixedLength)", method);
        Assert.Contains("_wallIndex.TraceFixedLength(pendingStart, worldPoint, fixedLength, SnapToleranceWorld())", method);
    }

    [Fact]
    public void MainWindow_MiddleButtonDoubleClick_FitsViewToWallBounds()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("MouseDown=\"OnCanvasMouseDown\"", xaml);

        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasMouseDown");
        Assert.Contains("MouseButton.Middle", method);
        Assert.Contains("e.ClickCount == 2", method);
        Assert.Contains("FitView();", method);
        Assert.Contains("Redraw();", method);
    }

    [Fact]
    public void MainWindow_MiddleButtonDrag_PansCanvasAndPreservesDoubleClickFit()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        string source = ReadAppFile("MainWindow.xaml.cs");
        string down = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasMouseDown");
        string move = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasMouseMove");
        string up = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasMouseUp");
        string pan = Slice(source, "public ViewTransform PanBy", "public Point ToScreen");

        Assert.Contains("MouseUp=\"OnCanvasMouseUp\"", xaml);
        Assert.Contains("DrawingCanvas.CaptureMouse()", down);
        Assert.Contains("e.ClickCount == 2", down);
        Assert.Contains("FitView();", down);
        Assert.Contains("_transform = _transform.PanBy(position - _panStart)", move);
        Assert.Contains("DrawingCanvas.ReleaseMouseCapture()", up);
        Assert.Contains("_isFitMode = false", move);
        Assert.Contains("_translateX + delta.X", pan);
        Assert.Contains("_translateY + delta.Y", pan);
        Assert.Contains("private bool _isPanning", source);
    }

    [Fact]
    public void MainWindow_ToolbarHint_MentionsCadCanvasControls()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("고정 길이(m)", xaml);
        Assert.Contains("휠: 포인터 중심 확대/축소", xaml);
        Assert.Contains("가운데 버튼 드래그: 화면 이동", xaml);
        Assert.Contains("가운데 버튼 더블클릭", xaml);
        Assert.Contains("Esc: 모드 종료/그리기 취소", xaml);
        Assert.Contains("벽 클릭: 기존 길이/방향으로 이동", xaml);
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
    public void MainWindow_Escape_ExitsAddModeAndCancelsPendingDraft()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        string keyDown = ReadAppMethod("MainWindow.xaml.cs", "private void OnWindowPreviewKeyDown");
        string modeChanged = ReadAppMethod("MainWindow.xaml.cs", "private void OnAddExitModeChanged");

        Assert.Contains("PreviewKeyDown=\"OnWindowPreviewKeyDown\"", xaml);
        Assert.Contains("e.Key == Key.Escape && _addExitMode", keyDown);
        Assert.Contains("AddExitToggle.IsChecked = false", keyDown);
        Assert.Contains("e.Handled = true", keyDown);
        Assert.Contains("_exitEditor.HandleRightClick()", modeChanged);
        Assert.Contains("_previewRoute = null", modeChanged);
    }

    [Fact]
    public void MainWindow_NormalModeSelectedExit_ClickingWallRelocatesAndInvalidatesAnalysis()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasLeftClick");
        int relocate = method.IndexOf("_exitEditor.TryRelocateSelected(_wallIndex, worldPoint, SnapToleranceWorld())", StringComparison.Ordinal);
        int select = method.IndexOf("_exitEditor.TrySelectNear(worldPoint, selectionToleranceWorld)", StringComparison.Ordinal);

        Assert.True(relocate >= 0 && relocate < select,
            "A selected exit must get the first chance to relocate to a clicked wall before normal selection runs.");
        Assert.Contains("InvalidateAnalysis();", method[relocate..select]);
        Assert.Contains("기존 길이와 방향", method);
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

    [Fact]
    public void MainWindow_DefaultsToFitModeOnConstruction()
    {
        string source = ReadAppFile("MainWindow.xaml.cs");
        Assert.Contains("private bool _isFitMode = true;", source);
    }

    [Fact]
    public void MainWindow_OpenDxf_ExplicitlyFitsViewToWallBoundsBeforeRedraw()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnOpenDxf");
        int fitIndex = method.IndexOf("FitView();", StringComparison.Ordinal);
        int redrawIndex = method.IndexOf("Redraw();", StringComparison.Ordinal);
        Assert.True(fitIndex >= 0, "Expected OnOpenDxf to explicitly call FitView() to reset the view to the loaded geometry.");
        Assert.True(redrawIndex > fitIndex, "Expected FitView() to run before Redraw() so the first frame is already fitted.");
    }

    [Fact]
    public void MainWindow_OpenProject_ExplicitlyFitsViewToWallBoundsBeforeRedraw()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnOpenProject");
        int fitIndex = method.IndexOf("FitView();", StringComparison.Ordinal);
        int redrawIndex = method.IndexOf("Redraw();", StringComparison.Ordinal);
        Assert.True(fitIndex >= 0, "Expected OnOpenProject to explicitly call FitView() to reset the view to the loaded geometry.");
        Assert.True(redrawIndex > fitIndex, "Expected FitView() to run before Redraw() so the first frame is already fitted.");
    }

    [Fact]
    public void MainWindow_FitViewUsesWallSegmentBoundsIndependentOfAbsoluteCoordinates()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void FitView");
        Assert.Contains("Bounds.FromSegments(_walls)", method);
        Assert.Contains("ViewTransform.Build(bounds, DrawingCanvas.ActualWidth, DrawingCanvas.ActualHeight)", method);
        Assert.Contains("_isFitMode = true", method);
    }

    [Fact]
    public void MainWindow_CanvasWiresUpMouseWheelHandler()
    {
        string xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("MouseWheel=\"OnCanvasMouseWheel\"", xaml);
    }

    [Fact]
    public void MainWindow_PlainWheelZoomsAroundThePointerAndLeavesFitMode()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasMouseWheel");
        Assert.DoesNotContain("Keyboard.Modifiers", method);
        Assert.Contains("e.GetPosition(DrawingCanvas)", method);
        Assert.Contains("ZoomAround", method);
        Assert.Contains("_isFitMode = false", method);
        Assert.Contains("e.Handled = true", method);
    }

    [Fact]
    public void MainWindow_ResizeOnlyRefitsWhileInFitModePreservingManualZoom()
    {
        string method = ReadAppMethod("MainWindow.xaml.cs", "private void OnCanvasSizeChanged");
        int fitModeGuard = method.IndexOf("_isFitMode", StringComparison.Ordinal);
        int fitViewCall = method.IndexOf("FitView();", StringComparison.Ordinal);
        int redrawCall = method.IndexOf("Redraw();", StringComparison.Ordinal);
        Assert.True(fitModeGuard >= 0 && fitViewCall > fitModeGuard,
            "Expected FitView() to be gated behind an _isFitMode check so resize only refits while still in fit mode.");
        Assert.True(redrawCall > fitViewCall);
    }

    [Fact]
    public void MainWindow_RedrawDoesNotUnconditionallyRebuildTheTransform()
    {
        string redraw = ReadAppMethod("MainWindow.xaml.cs", "private void Redraw");
        Assert.DoesNotContain("ViewTransform.Build", redraw);
        Assert.Contains("_transform is null", redraw);
        Assert.Contains("FitView();", redraw);
    }

    [Fact]
    public void ViewTransform_ZoomAroundClampsScaleToFiniteBounds()
    {
        string zoomAround = ReadAppMethod("MainWindow.xaml.cs", "public ViewTransform ZoomAround");
        Assert.Contains("Math.Clamp", zoomAround);
        Assert.Contains("_minScale", zoomAround);
        Assert.Contains("_maxScale", zoomAround);
    }

    [Fact]
    public void ViewTransform_BuildDerivesFiniteMinAndMaxScaleFromTheFitScale()
    {
        string build = ReadAppMethod("MainWindow.xaml.cs", "public static ViewTransform Build");
        Assert.Contains("minScale", build);
        Assert.Contains("maxScale", build);
        Assert.DoesNotContain("double.PositiveInfinity", build);
        Assert.DoesNotContain("double.NegativeInfinity", build);
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
