using System.Globalization;
using System.ComponentModel;
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
    private WalkabilityGrid? _mapGrid;
    private DistanceMapResult? _mapResult;
    private WalkabilityGrid? _bodyGrid;
    private DistanceMapResult? _bodyResult;
    private string? _dxfPath;
    private string? _projectPath;
    private string? _startupProjectPath;
    private double _metersPerDrawingUnit = 1;
    private BodyProfile _bodyProfile = BodyProfile.KoreanAdult;
    private bool _applyBodyMeasurements = true;
    private bool _addExitMode;
    private bool _isPanning;
    private Point _panStart;
    private bool _isZoomWindowDragging;
    private Point _zoomWindowStart;
    private Point _zoomWindowEnd;
    private Point? _zoomWindowCursor;
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
    private bool _isCalculating;
    private bool _isDirty;

    public MainWindow()
    {
        InitializeComponent();
        UpdateTitle();
    }

    private void OnOpenDxf(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
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
            _projectPath = null;
            ResetAnalysis(clearExits: true);
            SetDirty(false);
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

    private void OnSaveCurrentProject(object sender, ExecutedRoutedEventArgs e) => SaveProject(saveAs: false);

    private void OnSaveProjectAs(object sender, ExecutedRoutedEventArgs e) => SaveProject(saveAs: true);

    private bool SaveProject(bool saveAs)
    {
        if (_walls.Count == 0)
        {
            MessageBox.Show(this, "먼저 DXF 또는 프로젝트를 불러오세요.", "알림");
            return false;
        }

        double? cellSize = ParseCellSize();
        if (cellSize is null)
        {
            return false;
        }
        BodyProfile? profile = ParseBodyProfile();
        if (profile is null)
        {
            return false;
        }

        string? path = saveAs ? null : _projectPath;
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "보행거리 프로젝트|*.walkdistance",
                DefaultExt = ".walkdistance",
                AddExtension = true,
            };
            if (dialog.ShowDialog() != true) return false;
            path = dialog.FileName;
        }

        try
        {
            var analysis = _mapGrid is not null && _mapResult is not null &&
                           _mapGrid.CellSize == cellSize.Value && _mapGrid.ClearanceRadius == 0
                ? DistanceMapCache.Create(_mapGrid, _mapResult, null, null, null, null)
                : null;
            double clearanceRadius = _applyBodyMeasurements ? profile.ClearanceRadius : 0;
            var bodyAnalysis = _bodyGrid is not null && _bodyResult is not null &&
                               _bodyGrid.CellSize == cellSize.Value && _bodyProfile == profile &&
                               _bodyGrid.ClearanceRadius == clearanceRadius
                ? DistanceMapCache.Create(_bodyGrid, _bodyResult, _queryPoint, _queryDistance,
                    _farthestPathPoints, _queryPathPoints, _applyBodyMeasurements ? profile : null)
                : null;
            ProjectFile.Save(path, new ProjectData(
                Version: 8,
                CellSize: cellSize.Value,
                MetersPerDrawingUnit: _metersPerDrawingUnit,
                Walls: _walls.ToList(),
                Exits: [],
                DxfPath: _dxfPath,
                ExitPaths: _exitEditor.Paths.Select(exitPath => exitPath.ToList()).ToList(),
                Analysis: analysis,
                BodyProfile: profile,
                BodyAnalysis: bodyAnalysis,
                ApplyBodyMeasurements: _applyBodyMeasurements));
            _projectPath = path;
            SetDirty(false);
            StatusText.Text = $"프로젝트 저장됨: {System.IO.Path.GetFileName(path)} (DXF 없이 다시 열 수 있음)";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로젝트 저장 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
        string? path = _startupProjectPath;
        _startupProjectPath = null;
        if (path is null)
        {
            var dialog = new OpenFileDialog { Filter = "보행거리 프로젝트|*.walkdistance|모든 파일|*.*" };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }
        try
        {
            var data = LoadProjectWithUnitSelection(path);
            if (data is null)
            {
                return;
            }

            _walls = data.Walls.ToList();
            _wallIndex = WallIndex.Build(_walls);
            CacheWallGeometry();
            _dxfPath = data.DxfPath;
            _projectPath = path;
            _metersPerDrawingUnit = data.MetersPerDrawingUnit;
            _bodyProfile = data.BodyProfile!;
            _applyBodyMeasurements = data.ApplyBodyMeasurements;
            CellSizeBox.Text = data.CellSize.ToString(CultureInfo.InvariantCulture);
            ApplyBodyMeasurementsToggle.IsChecked = _applyBodyMeasurements;
            ShoulderWidthBox.Text = _bodyProfile.ShoulderWidth.ToString(CultureInfo.InvariantCulture);
            ResetAnalysis(clearExits: true);
            _exitEditor.LoadPaths(data.ExitPaths!);
            if (data.Analysis is { } cache)
            {
                var mapGrid = WalkabilityGrid.Build(_walls, data.CellSize);
                if (cache.IsCompatibleWith(mapGrid))
                {
                    _mapGrid = mapGrid;
                    _mapResult = cache.Restore(mapGrid).Result;
                    RefreshMapCaches();

                    if (data.BodyAnalysis is { } bodyCache)
                    {
                        double clearanceRadius = _applyBodyMeasurements ? _bodyProfile.ClearanceRadius : 0;
                        var bodyGrid = WalkabilityGrid.Build(
                            _walls, data.CellSize, clearanceRadius: clearanceRadius);
                        bool isCompatible = _applyBodyMeasurements
                            ? bodyCache.IsCompatibleWith(bodyGrid, _bodyProfile)
                            : bodyCache.IsCompatibleWith(bodyGrid);
                        if (isCompatible)
                        {
                            _bodyGrid = bodyGrid;
                            var restored = bodyCache.Restore(bodyGrid);
                            _bodyResult = restored.Result;
                            _queryPoint = restored.QueryPoint;
                            _queryDistance = restored.QueryDistance;
                            _farthestPathPoints = restored.FarthestPath;
                            _queryPathPoints = restored.QueryPath;
                        }
                    }
                }
            }
            SetDirty(false);
            StatusText.Text = $"프로젝트 v{data.Version} 불러옴: {System.IO.Path.GetFileName(path)} · 출구 {_exitEditor.Segments.Count}개";
            FitView();
            Redraw();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로젝트 불러오기 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void OpenProject(string path)
    {
        _startupProjectPath = path;
        OnOpenProject(this, new RoutedEventArgs());
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

    private void OnWindowClosing(object? sender, CancelEventArgs e) => e.Cancel = !ConfirmDiscardChanges();

    private bool ConfirmDiscardChanges()
    {
        if (!_isDirty) return true;
        var choice = MessageBox.Show(this, "변경 내용을 저장할까요?", "저장하지 않은 변경",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return choice == MessageBoxResult.No || choice == MessageBoxResult.Yes && SaveProject(saveAs: false);
    }

    private void SetDirty(bool dirty)
    {
        _isDirty = dirty;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string name = _projectPath is not null ? System.IO.Path.GetFileName(_projectPath) : "Untitled";
        Title = $"{name}{(_isDirty ? "*" : "")} - 보행거리 계산";
    }

    private void OnAddExitModeChanged(object sender, RoutedEventArgs e)
    {
        _addExitMode = AddExitToggle.IsChecked == true;
        DrawingCanvas.Cursor = ZoomWindowToggle.IsChecked == true || _addExitMode ? Cursors.Cross : Cursors.Arrow;
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
        if (_exitEditor.Segments.Count == 0) return;
        ResetAnalysis(clearExits: true);
        StatusText.Text = "모든 출구가 초기화되었습니다.";
        Redraw();
    }

    private void OnZoomWindowModeChanged(object sender, RoutedEventArgs e)
    {
        _isZoomWindowDragging = false;
        _zoomWindowCursor = null;
        DrawingCanvas.ReleaseMouseCapture();
        DrawingCanvas.Cursor = ZoomWindowToggle.IsChecked == true || _addExitMode ? Cursors.Cross : Cursors.Arrow;
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
                bool fixedCommitted = CommitExitPreview();
                if (fixedCommitted)
                {
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
            if (_exitEditor.PendingStart is { } pendingStart)
            {
                _previewRoute = [pendingStart, snappedPoint];
                if (CommitExitPreview())
                {
                    StatusText.Text = $"출구 {_exitEditor.Segments.Count}개 지정됨{snapNote} · 우클릭: 그리기 취소";
                }
                Redraw();
                return;
            }
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

        if (_bodyGrid is null || _bodyResult is null)
        {
            Redraw();
            return;
        }

        _queryPoint = worldPoint;
        if (!_bodyGrid.Contains(worldPoint))
        {
            _queryDistance = null;
            _queryPathPoints = null;
            StatusText.Text = "선택 지점은 건물 외부입니다.";
            Redraw();
            return;
        }
        var queryCell = _bodyGrid.WorldToCell(worldPoint);
        if (!_bodyGrid.IsWalkable(queryCell))
        {
            _queryDistance = null;
            _queryPathPoints = null;
            StatusText.Text = _bodyGrid.IsBlocked(queryCell.Col, queryCell.Row)
                ? "선택 지점은 벽 위입니다."
                : "선택 지점은 건물 외부입니다.";
            Redraw();
            return;
        }
        var queryPath = DistanceMapCalculator.FindPath(_bodyGrid, _bodyResult, worldPoint);
        _queryPathPoints = queryPath?.Points;
        _queryDistance = queryPath?.Distance;
        StatusText.Text = _queryDistance is { } distance
            ? $"선택 지점 → 가장 가까운 출구: {distance:F2} m"
            : "선택 지점은 벽 위이거나 출구에서 도달할 수 없습니다.";
        Redraw();
    }

    private bool CommitExitPreview()
    {
        if (!_addExitMode || _previewRoute is not { Count: > 1 } previewRoute || !_exitEditor.CommitPath(previewRoute))
        {
            return false;
        }

        _previewRoute = null;
        InvalidateAnalysis();
        return true;
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
        if (e.Key == Key.Space && _addExitMode && ZoomWindowToggle.IsChecked != true)
        {
            if (CommitExitPreview())
            {
                StatusText.Text = $"출구 {_exitEditor.Segments.Count}개 지정됨 · Space로 미리보기 그대로 확정";
                e.Handled = true;
                Redraw();
            }
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        if (ZoomWindowToggle.IsChecked == true)
        {
            ZoomWindowToggle.IsChecked = false;
            StatusText.Text = "Zoom Window를 취소했습니다.";
            e.Handled = true;
            Redraw();
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
        _exitEditor.ClearSelection();
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
        if (ZoomWindowToggle.IsChecked == true)
        {
            _zoomWindowCursor = e.GetPosition(DrawingCanvas);
            if (!_isZoomWindowDragging)
            {
                Redraw();
                return;
            }
        }

        if (_isZoomWindowDragging)
        {
            _zoomWindowEnd = e.GetPosition(DrawingCanvas);
            Redraw();
            return;
        }

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
        if (ZoomWindowToggle.IsChecked == true && e.ChangedButton == MouseButton.Left && _transform is not null)
        {
            e.Handled = true;
            _isZoomWindowDragging = true;
            _zoomWindowStart = _zoomWindowEnd = e.GetPosition(DrawingCanvas);
            DrawingCanvas.CaptureMouse();
            Redraw();
            return;
        }

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
        if (_isZoomWindowDragging && e.ChangedButton == MouseButton.Left)
        {
            e.Handled = true;
            _isZoomWindowDragging = false;
            DrawingCanvas.ReleaseMouseCapture();
            var selection = new Rect(_zoomWindowStart, _zoomWindowEnd);
            if (_transform is not null && selection.Width >= 4 && selection.Height >= 4)
            {
                _transform = _transform.ZoomToRectangle(selection, DrawingCanvas.ActualWidth, DrawingCanvas.ActualHeight);
                _isFitMode = false;
            }
            ZoomWindowToggle.IsChecked = false;
            Redraw();
            return;
        }

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

    private void OnCellSizeChanged(object sender, TextChangedEventArgs e)
    {
        if (_walls.Count > 0) InvalidateAnalysis();
    }

    private void OnBodyProfileChanged(object sender, TextChangedEventArgs e)
    {
        if (!_applyBodyMeasurements || !TryParseBodyProfile(out var profile) || profile == _bodyProfile)
        {
            return;
        }

        _bodyProfile = profile;
        InvalidateBodyAnalysis();
        StatusText.Text = $"인체 반경 {_bodyProfile.ClearanceRadius:F2} m · 다시 계산하세요.";
        Redraw();
    }

    private void OnBodyMeasurementsChanged(object sender, RoutedEventArgs e)
    {
        bool apply = ApplyBodyMeasurementsToggle?.IsChecked == true;
        if (ShoulderWidthBox is not null)
        {
            ShoulderWidthBox.IsEnabled = apply;
        }
        if (_applyBodyMeasurements == apply)
        {
            return;
        }

        _applyBodyMeasurements = apply;
        if (_walls.Count > 0)
        {
            InvalidateBodyAnalysis();
        }
        StatusText.Text = apply
            ? $"인체 반경 {_bodyProfile.ClearanceRadius:F2} m · 다시 계산하세요."
            : "인체 치수 미적용 · 다시 계산하세요.";
        Redraw();
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

    private BodyProfile? ParseBodyProfile()
    {
        if (TryParseBodyProfile(out var profile))
        {
            return profile;
        }

        MessageBox.Show(this, "어깨너비는 0보다 큰 숫자여야 합니다.", "알림");
        return null;
    }

    private bool TryParseBodyProfile(out BodyProfile profile)
    {
        profile = default!;
        if (ShoulderWidthBox is null)
        {
            return false;
        }

        bool shoulderParsed = double.TryParse(ShoulderWidthBox.Text, NumberStyles.Float,
                                  CultureInfo.CurrentCulture, out double shoulderWidth) ||
                              double.TryParse(ShoulderWidthBox.Text, NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out shoulderWidth);
        if (!shoulderParsed)
        {
            return false;
        }

        profile = new BodyProfile(shoulderWidth);
        return profile.IsValid;
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

    private async void OnCalculate(object sender, RoutedEventArgs e)
    {
        if (_isCalculating)
        {
            return;
        }
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
        BodyProfile? profile = ParseBodyProfile();
        if (profile is null)
        {
            return;
        }
        _bodyProfile = profile;
        double clearanceRadius = _applyBodyMeasurements ? profile.ClearanceRadius : 0;

        var walls = _walls.ToArray();
        var exitPaths = _exitEditor.Paths.Select(path => path.ToArray()).ToArray();
        _isCalculating = true;
        CalculateButton.IsEnabled = false;
        CalculationProgress.Visibility = Visibility.Visible;
        try
        {
            CalculationProgress.Value = 1;
            StatusText.Text = "1/4 · 격자 생성 중...";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            var grids = await Task.Run(() => (
                Map: WalkabilityGrid.Build(walls, cellSize.Value),
                Body: WalkabilityGrid.Build(walls, cellSize.Value, clearanceRadius: clearanceRadius)));
            _mapGrid = grids.Map;
            _bodyGrid = grids.Body;
            _mapResult = null;
            _bodyResult = null;
            ClearMapCaches();
            _farthestPathPoints = null;
            _queryPoint = null;
            _queryDistance = null;
            _queryPathPoints = null;
            if (_mapGrid.InteriorCellCount == 0)
            {
                InvalidateAnalysis();
                MessageBox.Show(this,
                    "닫힌 건물 외곽선을 찾을 수 없습니다. 벽 선을 연결해 닫힌 공간을 만든 뒤 다시 계산하세요.",
                    "닫힌 외곽선 필요",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusText.Text = "계산 중단: 닫힌 건물 외곽선이 필요합니다.";
                Redraw();
                return;
            }
            CalculationProgress.Value = 2;
            StatusText.Text = "2/4 · 출구 소스 생성 중...";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            var mapGrid = _mapGrid;
            var bodyGrid = _bodyGrid;
            var sourceSets = await Task.Run(() => (
                Map: exitPaths.SelectMany((path, exitGroupId) => path
                    .Zip(path.Skip(1), (start, end) => new Segment(start, end))
                    .SelectMany(exit => mapGrid.WalkableSourcesNearSegment(exit, mapGrid.CellSize, exitGroupId)))
                    .ToList(),
                Body: exitPaths.SelectMany((path, exitGroupId) => path
                    .Zip(path.Skip(1), (start, end) => new Segment(start, end))
                    .SelectMany(exit => bodyGrid.WalkableSourcesNearSegment(exit, bodyGrid.CellSize, exitGroupId)))
                    .ToList()));
            if (sourceSets.Map.Count == 0)
            {
                InvalidateAnalysis();
                MessageBox.Show(this, "건물 내부와 연결되는 사용 가능한 출구가 없습니다. 출구 위치를 확인하세요.", "알림");
                StatusText.Text = "계산 중단: 건물 내부와 연결되는 사용 가능한 출구가 없습니다.";
                Redraw();
                return;
            }

            CalculationProgress.Value = 3;
            StatusText.Text = "3/4 · 보행거리 계산 중...";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            var results = await Task.Run(() => (
                Map: DistanceMapCalculator.Compute(mapGrid, sourceSets.Map),
                Body: DistanceMapCalculator.Compute(bodyGrid, sourceSets.Body)));
            _mapResult = results.Map;
            _bodyResult = results.Body;

            CalculationProgress.Value = 4;
            StatusText.Text = "4/4 · 결과 렌더링 중...";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            var artifacts = await Task.Run(() =>
            {
                var normalContours = DistanceContourGenerator.Generate(mapGrid, results.Map.Distances);
                var normalContourComponents = DistanceContourAssembler.Assemble(
                    normalContours.Where(contour => contour.Level % 10 == 0).ToList(),
                    Math.Max(mapGrid.CellSize * 1e-6, 1e-9));
                var thresholdContours = threshold is { } limit
                    ? DistanceContourGenerator.GenerateThreshold(mapGrid, results.Map.Distances, limit)
                    : [];
                var heatmapBitmap = HeatmapRenderer.Render(
                    mapGrid, results.Map.Distances, results.Map.MaxDistance, threshold);
                var farthestPathPoints = results.Body.FarthestCell is { } farthest
                    ? DistanceMapCalculator.FindPath(
                        bodyGrid,
                        results.Body,
                        bodyGrid.CellCenter(farthest.Col, farthest.Row))?.Points
                    : null;
                return (normalContours, normalContourComponents, thresholdContours,
                    heatmapBitmap, farthestPathPoints);
            });
            _normalContours = artifacts.normalContours;
            _normalContourComponents = artifacts.normalContourComponents;
            _thresholdContours = artifacts.thresholdContours;
            _heatmapBitmap = artifacts.heatmapBitmap;
            _farthestPathPoints = artifacts.farthestPathPoints;
            AddExitToggle.IsChecked = false;
            _queryPoint = null;
            _queryDistance = null;
            _queryPathPoints = null;
            StatusText.Text = _bodyResult.FarthestCell is null
                ? "도달 가능한 보행 영역이 없습니다."
                : $"최대 보행거리: {_bodyResult.MaxDistance:F2} m · 계산 후 도면을 클릭하면 해당 최단경로를 표시합니다.";

            if (_bodyResult.UnreachableCellCount > 0)
            {
                StatusText.Text += $" · 도달 불가 {_bodyResult.UnreachableCellCount:N0}셀 제외";
            }

            SetDirty(true);
            Redraw();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or OverflowException)
        {
            InvalidateAnalysis();
            MessageBox.Show(this,
                "격자를 만들 메모리가 부족합니다. 셀 크기를 키워 다시 계산하세요.",
                "메모리 부족",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            StatusText.Text = "계산 중단: 메모리가 부족합니다. 셀 크기를 키워 다시 계산하세요.";
            Redraw();
        }
        catch (Exception ex)
        {
            InvalidateAnalysis();
            StatusText.Text = $"계산 실패: {ex.Message}";
            MessageBox.Show(this, $"보행거리 계산 실패: {ex.Message}", "오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Redraw();
        }
        finally
        {
            CalculationProgress.Visibility = Visibility.Collapsed;
            CalculateButton.IsEnabled = true;
            _isCalculating = false;
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
        _mapGrid = null;
        _mapResult = null;
        ClearMapCaches();
        InvalidateBodyAnalysis(markDirty: false);
        if (_walls.Count > 0) SetDirty(true);
    }

    private void InvalidateBodyAnalysis(bool markDirty = true)
    {
        _bodyGrid = null;
        _bodyResult = null;
        _queryPoint = null;
        _queryDistance = null;
        _farthestPathPoints = null;
        _queryPathPoints = null;
        if (markDirty && _walls.Count > 0) SetDirty(true);
    }

    private void RefreshMapCaches()
    {
        if (_mapGrid is null || _mapResult is null)
            return;
        _normalContours = DistanceContourGenerator.Generate(_mapGrid, _mapResult.Distances);
        _normalContourComponents = DistanceContourAssembler.Assemble(
            _normalContours.Where(contour => contour.Level % 10 == 0).ToList(),
            Math.Max(_mapGrid.CellSize * 1e-6, 1e-9));
        RefreshThresholdCaches();
    }

    private void RefreshThresholdCaches()
    {
        if (_mapGrid is null || _mapResult is null)
            return;
        _thresholdContours = _threshold is { } threshold
            ? DistanceContourGenerator.GenerateThreshold(_mapGrid, _mapResult.Distances, threshold)
            : [];
        _heatmapBitmap = HeatmapRenderer.Render(_mapGrid, _mapResult.Distances, _mapResult.MaxDistance, _threshold);
    }

    private void ClearMapCaches()
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
        if (showMap && _mapGrid is not null && _heatmapBitmap is not null)
        {
            DrawHeatmap(_mapGrid, _heatmapBitmap);
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
            AddMarker(_transform.ToScreen(previewRoute[0]), 4, Brushes.Red, "출구 미리보기 시작점");
            AddMarker(_transform.ToScreen(previewRoute[^1]), 4, Brushes.Red, "출구 미리보기 끝점");
        }

        if (ZoomWindowToggle.IsChecked == true && _zoomWindowCursor is { } zoomCursor)
        {
            DrawZoomWindowCrosshair(zoomCursor);
        }

        if (_isZoomWindowDragging)
        {
            DrawZoomWindow();
        }

        if (_exitEditor.PendingStart is { } pendingStart)
        {
            AddMarker(_transform.ToScreen(pendingStart), 4, Brushes.LightGreen, "출구 시작점 (지정 중)");
        }

        if (showPaths)
        {
            AddPath(_farthestPathPoints, Brushes.OrangeRed, 2.5);
            AddPath(_queryPathPoints, Brushes.DeepSkyBlue, 2.5);
            var farthestLabelPosition = AddPathLabel(_farthestPathPoints, _bodyResult?.MaxDistance, Brushes.OrangeRed);
            AddPathLabel(_queryPathPoints, _queryDistance, Brushes.DeepSkyBlue, farthestLabelPosition);

            if (_bodyGrid is not null && _bodyResult?.FarthestCell is { } farthest)
            {
                AddBodyClearanceOutline(_bodyGrid.CellCenter(farthest.Col, farthest.Row), Brushes.OrangeRed,
                    $"최대 보행거리 지점 ({_bodyResult.MaxDistance:F2} m)");
                if (_farthestPathPoints is { Count: > 0 } farthestPath)
                {
                    AddBodyClearanceOutline(farthestPath[^1], Brushes.OrangeRed,
                        "최대 보행거리 도착 중심", isArrival: true);
                }
            }

            if (_queryPoint is { } queryPoint)
            {
                string tooltip = _queryDistance is { } distance
                    ? $"선택 지점 ({distance:F2} m)"
                    : "도달 불가능 또는 벽";
                AddBodyClearanceOutline(queryPoint,
                    _queryDistance is null ? Brushes.Gray : Brushes.DeepSkyBlue, tooltip);
                if (_queryPathPoints is { Count: > 0 } queryPath)
                {
                    AddBodyClearanceOutline(queryPath[^1], Brushes.DeepSkyBlue,
                        "선택 지점 경로 도착 중심", isArrival: true);
                }
            }
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

    private void AddBodyClearanceOutline(WorldPoint point, Brush brush, string tooltip, bool isArrival = false)
    {
        if (!_applyBodyMeasurements || _transform is null)
        {
            return;
        }

        var center = _transform.ToScreen(point);
        double radius = _bodyProfile.ClearanceRadius * _transform.Scale;
        var outline = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = Brushes.Transparent,
            Stroke = brush,
            StrokeThickness = 2,
            StrokeDashArray = isArrival ? null : [4, 2],
            ToolTip = tooltip,
        };
        Canvas.SetLeft(outline, center.X - radius);
        Canvas.SetTop(outline, center.Y - radius);
        DrawingCanvas.Children.Add(outline);
    }

    private void DrawZoomWindow()
    {
        var selection = new Rect(_zoomWindowStart, _zoomWindowEnd);
        var rectangle = new Rectangle
        {
            Width = selection.Width,
            Height = selection.Height,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1,
            StrokeDashArray = [4, 2],
            Fill = new SolidColorBrush(Color.FromArgb(32, 30, 144, 255)),
        };
        Canvas.SetLeft(rectangle, selection.Left);
        Canvas.SetTop(rectangle, selection.Top);
        DrawingCanvas.Children.Add(rectangle);
    }

    private void DrawZoomWindowCrosshair(Point cursor)
    {
        DrawingCanvas.Children.Add(new Line
        {
            X1 = 0, Y1 = cursor.Y, X2 = DrawingCanvas.ActualWidth, Y2 = cursor.Y,
            Stroke = Brushes.DodgerBlue, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false,
        });
        DrawingCanvas.Children.Add(new Line
        {
            X1 = cursor.X, Y1 = 0, X2 = cursor.X, Y2 = DrawingCanvas.ActualHeight,
            Stroke = Brushes.DodgerBlue, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false,
        });
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

    public ViewTransform ZoomToRectangle(Rect selection, double canvasWidth, double canvasHeight)
    {
        double factor = Math.Min(canvasWidth / selection.Width, canvasHeight / selection.Height);
        var center = new Point(selection.Left + selection.Width / 2, selection.Top + selection.Height / 2);
        return ZoomAround(center, factor).PanBy(new Vector(canvasWidth / 2 - center.X, canvasHeight / 2 - center.Y));
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
