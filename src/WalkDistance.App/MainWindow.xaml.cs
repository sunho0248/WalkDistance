using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using WalkDistance.Core;

namespace WalkDistance.App;

public partial class MainWindow : Window
{
    private const double SelectionToleranceScreenPixels = 8;
    private const double ExitSnapToleranceScreenPixels = 12;
    private const double ZoomStepFactor = 1.1;

    private List<Segment> _walls = [];
    private WallIndex _wallIndex = WallIndex.Build([]);
    private readonly System.Windows.Shapes.Path _wallPath = new()
    {
        Stroke = Brushes.Black,
        StrokeThickness = 1.5,
    };
    private WorldPoint _wallGeometryOrigin;
    private readonly ExitLineEditor _exitEditor = new();
    private WalkabilityGrid? _grid;
    private DistanceMapResult? _result;
    private string? _dxfPath;
    private double _metersPerDrawingUnit = 1;
    private bool _addExitMode;
    private bool _isPanning;
    private Point _panStart;
    private bool _isFitMode = true;
    private ViewTransform? _transform;
    private WorldPoint? _queryPoint;
    private double? _queryDistance;
    private IReadOnlyList<WorldPoint>? _farthestPathPoints;
    private IReadOnlyList<WorldPoint>? _queryPathPoints;
    private IReadOnlyList<WorldPoint>? _previewRoute;
    private double? _threshold;
    private IReadOnlyList<DistanceContour> _normalContours = [];
    private IReadOnlyList<IReadOnlyList<DistanceContour>> _normalContourComponents = [];
    private IReadOnlyList<DistanceContour> _thresholdContours = [];
    private WriteableBitmap? _heatmapBitmap;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnOpenDxf(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "DXF 파일|*.dxf|모든 파일|*.*" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var document = LoadDxfWithUnitSelection(dialog.FileName);
            if (document is null)
            {
                return;
            }

            _walls = document.Walls.ToList();
            _wallIndex = WallIndex.Build(_walls);
            CacheWallGeometry();
            _metersPerDrawingUnit = document.MetersPerDrawingUnit;
            _dxfPath = dialog.FileName;
            ResetAnalysis(clearExits: true);
            StatusText.Text = $"{System.IO.Path.GetFileName(dialog.FileName)} 불러옴 · 벽 선분 {_walls.Count:N0}개 · 1 도면 단위 = {_metersPerDrawingUnit:G6} m";
            FitView();
            Redraw();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"DXF 불러오기 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private DxfDocument? LoadDxfWithUnitSelection(string path)
    {
        try
        {
            return DxfLoader.Load(path);
        }
        catch (DxfUnitRequiredException)
        {
            double? scale = SelectUnitScale();
            return scale is null ? null : DxfLoader.Load(path, scale);
        }
    }

    private void OnSaveProject(object sender, RoutedEventArgs e)
    {
        if (_walls.Count == 0)
        {
            MessageBox.Show(this, "먼저 DXF 또는 프로젝트를 불러오세요.", "알림");
            return;
        }

        double? cellSize = ParseCellSize();
        if (cellSize is null)
        {
            return;
        }

        var dialog = new SaveFileDialog { Filter = "보행거리 프로젝트|*.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            ProjectFile.Save(dialog.FileName, new ProjectData(
                Version: 4,
                CellSize: cellSize.Value,
                MetersPerDrawingUnit: _metersPerDrawingUnit,
                Walls: _walls.ToList(),
                Exits: [],
                DxfPath: _dxfPath,
                ExitPaths: _exitEditor.Paths.Select(path => path.ToList()).ToList()));
            StatusText.Text = $"프로젝트 저장됨: {System.IO.Path.GetFileName(dialog.FileName)} (DXF 없이 다시 열 수 있음)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로젝트 저장 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "보행거리 프로젝트|*.json|모든 파일|*.*" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var data = LoadProjectWithUnitSelection(dialog.FileName);
            if (data is null)
            {
                return;
            }

            _walls = data.Walls.ToList();
            _wallIndex = WallIndex.Build(_walls);
            CacheWallGeometry();
            _dxfPath = data.DxfPath;
            _metersPerDrawingUnit = data.MetersPerDrawingUnit;
            CellSizeBox.Text = data.CellSize.ToString(CultureInfo.InvariantCulture);
            ResetAnalysis(clearExits: true);
            _exitEditor.LoadPaths(data.ExitPaths!);
            StatusText.Text = $"프로젝트 v{data.Version} 불러옴: {System.IO.Path.GetFileName(dialog.FileName)} · 출구 {_exitEditor.Segments.Count}개";
            FitView();
            Redraw();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로젝트 불러오기 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private ProjectData? LoadProjectWithUnitSelection(string path)
    {
        try
        {
            return ProjectFile.Load(path);
        }
        catch (DxfUnitRequiredException)
        {
            double? scale = SelectUnitScale();
            return scale is null ? null : ProjectFile.Load(path, scale);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAddExitModeChanged(object sender, RoutedEventArgs e)
    {
        _addExitMode = AddExitToggle.IsChecked == true;
        DrawingCanvas.Cursor = _addExitMode ? Cursors.Cross : Cursors.Arrow;
        if (_addExitMode)
        {
            _exitEditor.ClearSelection();
            _previewRoute = null;
            StatusText.Text = TryGetFixedExitLength(out _)
                ? "마우스 위치를 중심으로 고정 길이 출구를 미리 봅니다. 클릭: 그대로 확정 · Esc: 모드 종료/조회 지우기"
                : "도면을 두 번 클릭해 출구를 지정하세요. Esc: 모드 종료/그리기 취소/조회 지우기";
            Redraw();
            return;
        }

        _previewRoute = null;
        if (_exitEditor.PendingStart is not null)
        {
            _exitEditor.HandleRightClick();
            StatusText.Text = "출구 선분 그리기가 취소되었습니다.";
        }
        else
        {
            StatusText.Text = "출구 지정 모드가 꺼졌습니다. 완료된 출구를 클릭하면 선택할 수 있습니다.";
        }
        Redraw();
    }

    private void OnClearExits(object sender, RoutedEventArgs e)
    {
        ResetAnalysis(clearExits: true);
        StatusText.Text = "모든 출구가 초기화되었습니다.";
        Redraw();
    }

    private void OnCanvasLeftClick(object sender, MouseButtonEventArgs e)
    {
        DrawingCanvas.Focus();
        if (_walls.Count == 0 || _transform is null)
        {
            return;
        }

        var worldPoint = _transform.ToWorld(e.GetPosition(DrawingCanvas));
        if (_addExitMode)
        {
            if (TryGetFixedExitLength(out double fixedLength))
            {
                bool fixedCommitted = _previewRoute is { Count: > 1 } previewRoute &&
                                      _exitEditor.CommitPath(previewRoute);
                if (fixedCommitted)
                {
                    _previewRoute = null;
                    InvalidateAnalysis();
                    StatusText.Text = $"고정 길이 {fixedLength:G} m 출구 {_exitEditor.Segments.Count}개 지정됨 · 마우스를 움직여 다음 출구 미리보기";
                }
                else
                {
                    StatusText.Text = "이 위치에서는 입력한 고정 길이 전체를 연결된 벽에서 미리 볼 수 없습니다.";
                }
                Redraw();
                return;
            }

            var snappedPoint = SnapToNearestWall(worldPoint, out bool snapped);
            string snapNote = snapped ? " (벽/도형에 자동 스냅)" : "";
            bool committed = _exitEditor.HandleLeftClick(snappedPoint);
            if (committed)
            {
                _previewRoute = null;
                InvalidateAnalysis();
                StatusText.Text = $"출구 {_exitEditor.Segments.Count}개 지정됨{snapNote} · 우클릭: 그리기 취소";
            }
            else
            {
                _previewRoute = [snappedPoint];
                StatusText.Text = $"출구 시작점 지정됨{snapNote} · 끝점을 클릭하세요. Esc: 모드 종료 및 그리기 취소";
            }
            Redraw();
            return;
        }

        if (_exitEditor.SelectedIndex is int selectedIndex &&
            _wallIndex.TrySnapToNearest(worldPoint, SnapToleranceWorld()) is not null)
        {
            if (_exitEditor.TryRelocateSelected(_wallIndex, worldPoint, SnapToleranceWorld()))
            {
                InvalidateAnalysis();
                StatusText.Text = $"출구 {selectedIndex + 1}번을 클릭 위치 중심으로 이동했습니다. 기존 길이와 방향 및 전체 경로 길이를 유지했습니다.";
            }
            else
            {
                StatusText.Text = "선택한 위치에서 기존 출구 길이만큼 연결된 벽을 찾을 수 없습니다.";
            }
            Redraw();
            return;
        }

        double selectionToleranceWorld = SelectionToleranceScreenPixels / _transform.Scale;
        bool hadSelection = _exitEditor.SelectedIndex is not null;
        if (_exitEditor.TrySelectNear(worldPoint, selectionToleranceWorld))
        {
            StatusText.Text = $"출구 {_exitEditor.SelectedIndex!.Value + 1}번 선택됨 · 벽 클릭 위치가 중심이 되도록 기존 길이/방향으로 이동 · Delete: 삭제";
            Redraw();
            return;
        }

        if (hadSelection)
        {
            StatusText.Text = "출구 선택을 해제했습니다.";
        }

        if (_grid is null || _result is null)
        {
            Redraw();
            return;
        }

        _queryPoint = worldPoint;
        if (!_grid.Contains(worldPoint))
        {
            _queryDistance = null;
            _queryPathPoints = null;
            StatusText.Text = "선택 지점은 건물 외부입니다.";
            Redraw();
            return;
        }
        var queryCell = _grid.WorldToCell(worldPoint);
        if (!_grid.IsWalkable(queryCell))
        {
            _queryDistance = null;
            _queryPathPoints = null;
            StatusText.Text = _grid.IsBlocked(queryCell.Col, queryCell.Row)
                ? "선택 지점은 벽 위입니다."
                : "선택 지점은 건물 외부입니다.";
            Redraw();
            return;
        }
        var queryPath = DistanceMapCalculator.FindPath(_grid, _result, worldPoint);
        _queryPathPoints = queryPath?.Points;
        _queryDistance = queryPath?.Distance;
        StatusText.Text = _queryDistance is { } distance
            ? $"선택 지점 → 가장 가까운 출구: {distance:F2} m"
            : "선택 지점은 벽 위이거나 출구에서 도달할 수 없습니다.";
        Redraw();
    }

    private void OnCanvasRightClick(object sender, MouseButtonEventArgs e)
    {
        DrawingCanvas.Focus();
        e.Handled = true;
        var result = _exitEditor.HandleRightClick();
        _previewRoute = null;
        StatusText.Text = result switch
        {
            ExitRightClickResult.CancelledPending => "출구 선분 그리기를 취소했습니다.",
            ExitRightClickResult.ClearedSelection => "출구 선택을 해제했습니다.",
            _ => "취소할 그리기나 선택이 없습니다.",
        };
        Redraw();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        bool exitedAddMode = _addExitMode;
        bool clearedQuery = _queryPoint is not null || _queryDistance is not null || _queryPathPoints is not null;
        if (exitedAddMode)
        {
            AddExitToggle.IsChecked = false;
        }
        _queryPoint = null;
        _queryDistance = null;
        _queryPathPoints = null;
        if (clearedQuery)
        {
            StatusText.Text = exitedAddMode
                ? "출구 지정 모드를 종료하고 조회 지점과 경로를 지웠습니다."
                : "조회 지점과 경로를 지웠습니다.";
        }
        e.Handled = true;
        Redraw();
    }

    private void OnCanvasKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        if (!_exitEditor.DeleteSelected())
        {
            return;
        }

        e.Handled = true;
        InvalidateAnalysis();
        StatusText.Text = $"선택한 출구를 삭제했습니다. 남은 출구: {_exitEditor.Segments.Count}개";
        Redraw();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning && _transform is not null)
        {
            var position = e.GetPosition(DrawingCanvas);
            _transform = _transform.PanBy(position - _panStart);
            _panStart = position;
            _isFitMode = false;
            Redraw();
            return;
        }

        if (!_addExitMode || _transform is null)
        {
            return;
        }

        var worldPoint = _transform.ToWorld(e.GetPosition(DrawingCanvas));
        if (TryGetFixedExitLength(out double fixedLength))
        {
            _previewRoute = _wallIndex.TraceFixedLengthFromMidpoint(worldPoint, fixedLength, SnapToleranceWorld());
            StatusText.Text = _previewRoute.Count > 1
                ? $"고정 길이 {fixedLength:G} m 전체 미리보기 · 마우스 위치가 중심 · 클릭하면 그대로 확정"
                : "이 위치에서는 입력한 고정 길이 전체를 연결된 벽에서 추적할 수 없습니다.";
            Redraw();
            return;
        }

        if (_exitEditor.PendingStart is not { } pendingStart)
        {
            _previewRoute = null;
            return;
        }

        _previewRoute = [pendingStart, SnapToNearestWall(worldPoint, out _)];
        Redraw();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFitMode)
        {
            FitView();
        }
        Redraw();
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount == 2)
        {
            _isPanning = false;
            DrawingCanvas.ReleaseMouseCapture();
            FitView();
            Redraw();
            return;
        }

        _isPanning = true;
        _panStart = e.GetPosition(DrawingCanvas);
        DrawingCanvas.CaptureMouse();
        DrawingCanvas.Cursor = Cursors.Hand;
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _isPanning = false;
        DrawingCanvas.ReleaseMouseCapture();
        DrawingCanvas.Cursor = _addExitMode ? Cursors.Cross : Cursors.Arrow;
        e.Handled = true;
    }

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_transform is null)
        {
            return;
        }

        double factor = e.Delta > 0 ? ZoomStepFactor : 1 / ZoomStepFactor;
        _transform = _transform.ZoomAround(e.GetPosition(DrawingCanvas), factor);
        _isFitMode = false;
        Redraw();
    }

    private void OnOverlayToggleChanged(object sender, RoutedEventArgs e)
    {
        if (DrawingCanvas is not null)
            Redraw();
    }

    private void OnThresholdChanged(object sender, TextChangedEventArgs e)
    {
        ValidateThresholdInput();
    }

    private void ValidateThresholdInput()
    {
        if (TryParseThreshold(out double? threshold))
        {
            ThresholdBox.ClearValue(BorderBrushProperty);
            ThresholdBox.ToolTip = threshold is null
                ? "기준거리 표시 꺼짐 · 숫자를 입력하면 초과 영역과 경계를 표시합니다."
                : $"보라색: {threshold:G} m 초과(d > 기준) · 흰 선: {threshold:G} m 경계";
        }
        else
        {
            ThresholdBox.BorderBrush = Brushes.Red;
            ThresholdBox.ToolTip = "0 이상의 유한한 숫자 또는 빈 값을 입력하세요. 이전 유효 기준은 유지됩니다.";
        }
    }

    private void OnThresholdLostFocus(object sender, RoutedEventArgs e) => ApplyThresholdInput();

    private void OnThresholdKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyThresholdInput();
            e.Handled = true;
        }
    }

    private void ApplyThresholdInput()
    {
        ValidateThresholdInput();
        if (!TryParseThreshold(out double? threshold) || threshold == _threshold)
            return;
        _threshold = threshold;
        RefreshThresholdCaches();
        Redraw();
    }

    private WorldPoint SnapToNearestWall(WorldPoint worldPoint, out bool snapped)
    {
        if (_transform is null)
        {
            snapped = false;
            return worldPoint;
        }

        var result = _wallIndex.TrySnapToNearest(worldPoint, SnapToleranceWorld());
        snapped = result is not null;
        return result?.Point ?? worldPoint;
    }

    // Screen-pixel snap/select tolerance converted to world units via the
    // current view scale. Shared by click-commit, live preview, and fixed-
    // length tracing so all three agree on the same snap radius.
    private double SnapToleranceWorld() =>
        _transform is null ? 0 : ExitSnapToleranceScreenPixels / _transform.Scale;

    private bool TryGetFixedExitLength(out double length)
    {
        length = 0;
        if (string.IsNullOrWhiteSpace(FixedExitLengthBox.Text))
        {
            return false;
        }

        if ((double.TryParse(FixedExitLengthBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out length) ||
             double.TryParse(FixedExitLengthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out length)) &&
            double.IsFinite(length) && length > 0)
        {
            return true;
        }

        length = 0;
        return false;
    }

    private double? ParseCellSize()
    {
        if (double.TryParse(CellSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cellSize) &&
            double.IsFinite(cellSize) && cellSize > 0)
        {
            return cellSize;
        }

        MessageBox.Show(this, "셀 크기는 0보다 큰 숫자여야 합니다.", "알림");
        return null;
    }

    private bool TryParseThreshold(out double? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(ThresholdBox.Text))
            return true;
        if ((double.TryParse(ThresholdBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double threshold) ||
             double.TryParse(ThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold)) &&
            double.IsFinite(threshold) && threshold >= 0)
        {
            parsed = threshold;
            return true;
        }
        return false;
    }

    private void OnCalculate(object sender, RoutedEventArgs e)
    {
        if (_walls.Count == 0)
        {
            InvalidateAnalysis();
            MessageBox.Show(this, "먼저 DXF 또는 프로젝트를 불러오세요.", "알림");
            Redraw();
            return;
        }
        if (_exitEditor.Segments.Count == 0)
        {
            InvalidateAnalysis();
            MessageBox.Show(this, "출구를 최소 1개 지정하세요.", "알림");
            Redraw();
            return;
        }

        if (!TryParseThreshold(out double? threshold))
        {
            ThresholdBox.BorderBrush = Brushes.Red;
            ThresholdBox.ToolTip = "0 이상의 유한한 숫자 또는 빈 값을 입력하세요. 기존 분석은 유지됩니다.";
            return;
        }
        _threshold = threshold;
        double? cellSize = ParseCellSize();
        if (cellSize is null)
        {
            InvalidateAnalysis();
            Redraw();
            return;
        }

        try
        {
            _grid = WalkabilityGrid.Build(_walls, cellSize.Value);
            _result = null;
            ClearAnalysisCaches();
            _farthestPathPoints = null;
            _queryPoint = null;
            _queryDistance = null;
            _queryPathPoints = null;
            if (_grid.InteriorCellCount == 0)
            {
                _grid = null;
                MessageBox.Show(this,
                    "닫힌 건물 외곽선을 찾을 수 없습니다. 벽 선을 연결해 닫힌 공간을 만든 뒤 다시 계산하세요.",
                    "닫힌 외곽선 필요",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusText.Text = "계산 중단: 닫힌 건물 외곽선이 필요합니다.";
                Redraw();
                return;
            }
            var sources = _exitEditor.Paths
                .SelectMany((path, exitGroupId) => path
                    .Zip(path.Skip(1), (start, end) => new Segment(start, end))
                    .SelectMany(exit =>
                        _grid.WalkableSourcesNearSegment(exit, _grid.CellSize, exitGroupId)))
                .ToList();
            if (sources.Count == 0)
            {
                _grid = null;
                MessageBox.Show(this, "건물 내부와 연결되는 사용 가능한 출구가 없습니다. 출구 위치를 확인하세요.", "알림");
                StatusText.Text = "계산 중단: 건물 내부와 연결되는 사용 가능한 출구가 없습니다.";
                Redraw();
                return;
            }

            _result = DistanceMapCalculator.Compute(_grid, sources);
            RefreshAnalysisCaches();
            _farthestPathPoints = _result.FarthestCell is { } farthest
                ? DistanceMapCalculator.FindPath(
                    _grid,
                    _result,
                    _grid.CellCenter(farthest.Col, farthest.Row))?.Points
                : null;
            AddExitToggle.IsChecked = false;
            _queryPoint = null;
            _queryDistance = null;
            _queryPathPoints = null;
            StatusText.Text = _result.FarthestCell is null
                ? "도달 가능한 보행 영역이 없습니다."
                : $"최대 보행거리: {_result.MaxDistance:F2} m · 계산 후 도면을 클릭하면 해당 최단경로를 표시합니다.";

            if (_result.UnreachableCellCount > 0)
            {
                MessageBox.Show(this,
                    $"출구에서 도달할 수 없는 보행 셀 {_result.UnreachableCellCount:N0}개는 최대 보행거리에서 제외했습니다.",
                    "도달 불가능 영역",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusText.Text += $" · 도달 불가 {_result.UnreachableCellCount:N0}셀 제외";
            }

            Redraw();
        }
        catch (GridSizeLimitExceededException ex)
        {
            _grid = null;
            _result = null;
            ClearAnalysisCaches();
            _farthestPathPoints = null;
            _queryPoint = null;
            _queryDistance = null;
            _queryPathPoints = null;
            MessageBox.Show(this,
                $"격자가 너무 큽니다 ({ex.RequestedCellCount:N0}셀 / 한도 {ex.MaxCellCount:N0}셀). 셀 크기를 키워 다시 계산하세요.",
                "격자 크기 초과",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            StatusText.Text = "계산 중단: 셀 크기를 키워 격자 셀 수를 줄이세요.";
        }
    }

    private void ResetAnalysis(bool clearExits)
    {
        if (clearExits)
        {
            _exitEditor.Clear();
            _previewRoute = null;
            AddExitToggle.IsChecked = false;
        }
        InvalidateAnalysis();
    }

    private void InvalidateAnalysis()
    {
        _grid = null;
        _result = null;
        _queryPoint = null;
        _queryDistance = null;
        _farthestPathPoints = null;
        _queryPathPoints = null;
        ClearAnalysisCaches();
    }

    private void RefreshAnalysisCaches()
    {
        if (_grid is null || _result is null)
            return;
        _normalContours = DistanceContourGenerator.Generate(_grid, _result.Distances);
        _normalContourComponents = DistanceContourAssembler.Assemble(
            _normalContours.Where(contour => contour.Level % 10 == 0).ToList(),
            Math.Max(_grid.CellSize * 1e-6, 1e-9));
        RefreshThresholdCaches();
    }

    private void RefreshThresholdCaches()
    {
        if (_grid is null || _result is null)
            return;
        _thresholdContours = _threshold is { } threshold
            ? DistanceContourGenerator.GenerateThreshold(_grid, _result.Distances, threshold)
            : [];
        _heatmapBitmap = HeatmapRenderer.Render(_grid, _result.Distances, _result.MaxDistance, _threshold);
    }

    private void ClearAnalysisCaches()
    {
        _normalContours = [];
        _normalContourComponents = [];
        _thresholdContours = [];
        _heatmapBitmap = null;
    }

    private double? SelectUnitScale()
    {
        var choices = new[]
        {
            new UnitChoice("밀리미터 (mm)", 0.001),
            new UnitChoice("센티미터 (cm)", 0.01),
            new UnitChoice("미터 (m)", 1),
            new UnitChoice("인치 (in)", 0.0254),
            new UnitChoice("피트 (ft)", 0.3048),
        };
        var unitBox = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(UnitChoice.Label),
            SelectedIndex = 0,
            Margin = new Thickness(0, 10, 0, 14),
            MinWidth = 220,
        };
        var okButton = new Button { Content = "확인", IsDefault = true, MinWidth = 75, Margin = new Thickness(4, 0, 0, 0) };
        var cancelButton = new Button { Content = "취소", IsCancel = true, MinWidth = 75, Margin = new Thickness(4, 0, 0, 0) };
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "DXF에 $INSUNITS가 없거나 unitless입니다. 원본 도면 좌표의 단위를 선택하세요.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 390,
        });
        panel.Children.Add(unitBox);
        panel.Children.Add(buttonPanel);
        var dialog = new Window
        {
            Title = "DXF 단위 선택",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = panel,
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        return ((UnitChoice)unitBox.SelectedItem).MetersPerUnit;
    }

    private void FitView()
    {
        _isFitMode = true;
        if (_walls.Count == 0 || DrawingCanvas.ActualWidth <= 0 || DrawingCanvas.ActualHeight <= 0)
        {
            _transform = null;
            return;
        }

        var bounds = Bounds.FromSegments(_walls);
        _transform = ViewTransform.Build(bounds, DrawingCanvas.ActualWidth, DrawingCanvas.ActualHeight);
    }

    private void Redraw()
    {
        DrawingCanvas.Children.Clear();
        if (_walls.Count == 0 || DrawingCanvas.ActualWidth <= 0 || DrawingCanvas.ActualHeight <= 0)
        {
            _transform = null;
            return;
        }

        if (_transform is null)
        {
            FitView();
            if (_transform is null)
            {
                return;
            }
        }

        bool showMap = MapOverlayToggle.IsChecked == true;
        bool showPaths = PathOverlayToggle.IsChecked == true;
        if (showMap && _grid is not null && _heatmapBitmap is not null)
        {
            DrawHeatmap(_grid, _heatmapBitmap);
            DrawContours(_normalContours, _thresholdContours, _normalContourComponents);
        }

        DrawWalls();

        for (int i = 0; i < _exitEditor.Paths.Count; i++)
        {
            var path = _exitEditor.Paths[i];
            bool isSelected = _exitEditor.SelectedIndex == i;
            var brush = isSelected ? Brushes.DodgerBlue : Brushes.LimeGreen;
            string exitName = $"EXIT {i + 1}";
            string exitTooltip = isSelected ? $"{exitName} (선택됨)" : exitName;
            DrawingCanvas.Children.Add(new Polyline
            {
                Points = new PointCollection(path.Select(_transform.ToScreen)),
                Stroke = brush,
                StrokeThickness = isSelected ? 5 : 3,
                ToolTip = exitTooltip,
            });
            var anchor = _transform.ToScreen(LabelPlacement.HalfLengthPoint(path));
            var label = new TextBlock
            {
                Text = exitName,
                Foreground = brush,
                Background = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(2, 0, 2, 0),
                ToolTip = exitTooltip,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelPosition = LabelPlacement.ClampToCanvas(
                new WorldPoint(anchor.X + 6, anchor.Y + 6),
                label.DesiredSize.Width,
                label.DesiredSize.Height,
                DrawingCanvas.ActualWidth,
                DrawingCanvas.ActualHeight,
                padding: 4);
            Canvas.SetLeft(label, labelPosition.X);
            Canvas.SetTop(label, labelPosition.Y);
            DrawingCanvas.Children.Add(label);
            AddMarker(_transform.ToScreen(path[0]), isSelected ? 5 : 4, brush, "출구 시작점");
            AddMarker(_transform.ToScreen(path[^1]), isSelected ? 5 : 4, brush, "출구 끝점");
        }

        if (_previewRoute is { Count: > 1 } previewRoute)
        {
            AddPath(previewRoute, Brushes.LightGreen, 2);
        }

        if (_exitEditor.PendingStart is { } pendingStart)
        {
            AddMarker(_transform.ToScreen(pendingStart), 4, Brushes.LightGreen, "출구 시작점 (지정 중)");
        }

        if (showPaths)
        {
            AddPath(_farthestPathPoints, Brushes.OrangeRed, 2.5);
            AddPath(_queryPathPoints, Brushes.DeepSkyBlue, 2.5);
            var farthestLabelPosition = AddPathLabel(_farthestPathPoints, _result?.MaxDistance, Brushes.OrangeRed);
            AddPathLabel(_queryPathPoints, _queryDistance, Brushes.DeepSkyBlue, farthestLabelPosition);
        }

        if (_grid is not null && _result?.FarthestCell is { } farthest)
        {
            var point = _transform.ToScreen(_grid.CellCenter(farthest.Col, farthest.Row));
            AddMarker(point, 8, Brushes.Red, $"최대 보행거리 지점 ({_result.MaxDistance:F2} m)");
        }

        if (_queryPoint is { } queryPoint)
        {
            string tooltip = _queryDistance is { } distance
                ? $"선택 지점 ({distance:F2} m)"
                : "도달 불가능 또는 벽";
            AddMarker(_transform.ToScreen(queryPoint), 5,
                _queryDistance is null ? Brushes.Gray : Brushes.DeepSkyBlue, tooltip);
        }
    }

    private void DrawHeatmap(WalkabilityGrid grid, WriteableBitmap bitmap)
    {
        if (_transform is null)
        {
            return;
        }

        var topLeft = _transform.ToScreen(new WorldPoint(grid.Bounds.MinX, grid.Bounds.MaxY));
        var image = new Image
        {
            Source = bitmap,
            Width = grid.Bounds.Width * _transform.Scale,
            Height = grid.Bounds.Height * _transform.Scale,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, topLeft.X);
        Canvas.SetTop(image, topLeft.Y);
        DrawingCanvas.Children.Add(image);
    }

    private void CacheWallGeometry()
    {
        var bounds = Bounds.FromSegments(_walls);
        _wallGeometryOrigin = new WorldPoint(bounds.MinX, bounds.MinY);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var wall in _walls)
            {
                context.BeginFigure(new Point(
                    wall.Start.X - _wallGeometryOrigin.X,
                    wall.Start.Y - _wallGeometryOrigin.Y), false, false);
                context.LineTo(new Point(
                    wall.End.X - _wallGeometryOrigin.X,
                    wall.End.Y - _wallGeometryOrigin.Y), true, false);
            }
        }
        geometry.Freeze();
        _wallPath.Data = geometry;
    }

    private void DrawWalls()
    {
        if (_transform is null || _wallPath.Data is not StreamGeometry geometry || geometry.IsEmpty())
        {
            return;
        }

        var origin = _transform.ToScreen(_wallGeometryOrigin);
        _wallPath.RenderTransform = new MatrixTransform(
            _transform.Scale, 0, 0, -_transform.Scale, origin.X, origin.Y);
        _wallPath.StrokeThickness = 1.5 / _transform.Scale;
        DrawingCanvas.Children.Add(_wallPath);
    }

    private void DrawContours(
        IReadOnlyList<DistanceContour> normalContours,
        IReadOnlyList<DistanceContour> thresholdContours,
        IReadOnlyList<IReadOnlyList<DistanceContour>> normalContourComponents)
    {
        if (_transform is null)
            return;
        foreach (var level in normalContours.GroupBy(contour => contour.Level))
        {
            bool isMajor = level.Key % 10 == 0;
            AddContourPath(level, isMajor ? Brushes.Black : Brushes.DimGray, isMajor ? 1.6 : 0.8);
        }
        AddContourPath(thresholdContours, Brushes.White, 2.5);

        var labelPoints = new List<Point>();
        foreach (var component in normalContourComponents)
        {
            var point = SelectLabelPoint(component, labelPoints);
            var label = new TextBlock { Text = $"{component[0].Level:0} m", Foreground = Brushes.Black,
                Background = Brushes.White, FontSize = 11, Padding = new Thickness(2, 0, 2, 0) };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelPosition = LabelPlacement.ClampToCanvas(
                new WorldPoint(point.X + 3, point.Y + 3),
                label.DesiredSize.Width,
                label.DesiredSize.Height,
                DrawingCanvas.ActualWidth,
                DrawingCanvas.ActualHeight,
                padding: 4);
            Canvas.SetLeft(label, labelPosition.X);
            Canvas.SetTop(label, labelPosition.Y);
            DrawingCanvas.Children.Add(label);
            labelPoints.Add(point);
        }
    }

    private void AddContourPath(IEnumerable<DistanceContour> contours, Brush brush, double thickness)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var contour in contours)
            {
                context.BeginFigure(_transform!.ToScreen(contour.Start), false, false);
                context.LineTo(_transform.ToScreen(contour.End), true, false);
            }
        }
        geometry.Freeze();
        if (!geometry.IsEmpty())
            DrawingCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geometry,
                Stroke = brush,
                StrokeThickness = thickness,
            });
    }

    private Point SelectLabelPoint(IEnumerable<DistanceContour> contours, IReadOnlyList<Point> placed)
    {
        var candidates = contours.Select(contour =>
            {
                var start = _transform!.ToScreen(contour.Start);
                var end = _transform.ToScreen(contour.End);
                return new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
            }).ToList();
        Point? available = candidates.Select(contour => (Point?)contour)
            .FirstOrDefault(candidate => placed.All(point => (candidate!.Value - point).Length >= 60));
        return available ?? candidates[0];
    }

    private void AddMarker(Point center, double radius, Brush brush, string tooltip)
    {
        var ellipse = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = brush,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            ToolTip = tooltip,
        };
        Canvas.SetLeft(ellipse, center.X - radius);
        Canvas.SetTop(ellipse, center.Y - radius);
        DrawingCanvas.Children.Add(ellipse);
    }

    private void AddPath(IReadOnlyList<WorldPoint>? points, Brush brush, double thickness)
    {
        if (_transform is null || points is null || points.Count < 2)
        {
            return;
        }

        DrawingCanvas.Children.Add(new Polyline
        {
            Points = new PointCollection(points.Select(_transform.ToScreen)),
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        });
    }

    private Point? AddPathLabel(
        IReadOnlyList<WorldPoint>? points, double? distance, Brush brush, Point? occupied = null)
    {
        if (_transform is null || points is null || points.Count < 2 ||
            distance is not { } value || !double.IsFinite(value) ||
            points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            return null;

        var anchor = _transform.ToScreen(LabelPlacement.HalfLengthPoint(points));
        var label = new TextBlock
        {
            Text = $"{value.ToString("F2", CultureInfo.InvariantCulture)} m",
            Foreground = brush,
            Background = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(3, 1, 3, 1),
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Point Position(double yOffset)
        {
            var position = LabelPlacement.ClampToCanvas(
                new WorldPoint(anchor.X + 4, anchor.Y + 4 + yOffset),
                label.DesiredSize.Width,
                label.DesiredSize.Height,
                DrawingCanvas.ActualWidth,
                DrawingCanvas.ActualHeight,
                padding: 4);
            return new Point(position.X, position.Y);
        }

        var labelPosition = Position(0);
        if (occupied is { } other && (labelPosition - other).Length < 8)
        {
            labelPosition = Position(label.DesiredSize.Height + 4);
            if ((labelPosition - other).Length < 8)
                labelPosition = Position(-label.DesiredSize.Height - 4);
        }

        Canvas.SetLeft(label, labelPosition.X);
        Canvas.SetTop(label, labelPosition.Y);
        DrawingCanvas.Children.Add(label);
        return labelPosition;
    }

    private sealed record UnitChoice(string Label, double MetersPerUnit);
}

/// <summary>
/// Maps DXF world coordinates (Y-up) to the WPF canvas (Y-down).
/// </summary>
public sealed class ViewTransform
{
    private const double MinScaleFactor = 1e-3;
    private const double MaxScaleFactor = 1e3;

    public double Scale { get; }
    private readonly double _translateX;
    private readonly double _translateY;
    private readonly double _originX;
    private readonly double _originY;
    private readonly double _minScale;
    private readonly double _maxScale;

    private ViewTransform(double scale, double translateX, double translateY, double originX, double originY, double minScale, double maxScale)
    {
        Scale = scale;
        _translateX = translateX;
        _translateY = translateY;
        _originX = originX;
        _originY = originY;
        _minScale = minScale;
        _maxScale = maxScale;
    }

    public static ViewTransform Build(Bounds bounds, double canvasWidth, double canvasHeight)
    {
        const double margin = 20;
        double width = Math.Max(bounds.Width, 1e-6);
        double height = Math.Max(bounds.Height, 1e-6);
        double scale = Math.Min(
            (canvasWidth - 2 * margin) / width,
            (canvasHeight - 2 * margin) / height);
        scale = Math.Max(scale, 1e-6);

        double drawnWidth = width * scale;
        double drawnHeight = height * scale;
        double offsetX = (canvasWidth - drawnWidth) / 2;
        double offsetY = (canvasHeight - drawnHeight) / 2;

        // translateX/Y hold the screen position of (bounds.MinX, bounds.MinY) directly,
        // instead of pre-multiplying bounds.Min by scale into an absolute offset. DXF
        // drawings are often placed on a national survey grid, so bounds.Min can be many
        // orders of magnitude larger than the drawing's own extent; folding it into a
        // single translate and then adding it back to point*scale in ToScreen/ToWorld
        // subtracts two near-equal huge numbers to recover a small result, which is
        // catastrophic cancellation. Keeping an explicit origin and always computing
        // (point - origin) before multiplying by scale avoids ever combining values of
        // wildly different magnitude.
        double translateX = offsetX;
        double translateY = canvasHeight - offsetY;

        double minScale = Math.Max(scale * MinScaleFactor, 1e-9);
        double maxScale = scale * MaxScaleFactor;

        return new ViewTransform(scale, translateX, translateY, bounds.MinX, bounds.MinY, minScale, maxScale);
    }

    public ViewTransform ZoomAround(Point screenPoint, double factor)
    {
        double newScale = Math.Clamp(Scale * factor, _minScale, _maxScale);
        double appliedFactor = newScale / Scale;
        double newTranslateX = screenPoint.X - appliedFactor * (screenPoint.X - _translateX);
        double newTranslateY = screenPoint.Y - appliedFactor * (screenPoint.Y - _translateY);
        return new ViewTransform(newScale, newTranslateX, newTranslateY, _originX, _originY, _minScale, _maxScale);
    }

    public ViewTransform PanBy(Vector delta) => new(
        Scale, _translateX + delta.X, _translateY + delta.Y,
        _originX, _originY, _minScale, _maxScale);

    public Point ToScreen(WorldPoint point) => new(
        _translateX + (point.X - _originX) * Scale,
        _translateY - (point.Y - _originY) * Scale);

    public WorldPoint ToWorld(Point point) => new(
        _originX + (point.X - _translateX) / Scale,
        _originY + (_translateY - point.Y) / Scale);
}
