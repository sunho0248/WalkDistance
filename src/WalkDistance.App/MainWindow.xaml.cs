using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using WalkDistance.Core;

namespace WalkDistance.App;

public partial class MainWindow : Window
{
    private List<Segment> _walls = [];
    private readonly ExitLineEditor _exitEditor = new();
    private WalkabilityGrid? _grid;
    private DistanceMapResult? _result;
    private string? _dxfPath;
    private double _metersPerDrawingUnit = 1;
    private bool _addExitMode;
    private ViewTransform? _transform;
    private WorldPoint? _queryPoint;
    private double? _queryDistance;
    private IReadOnlyList<WorldPoint>? _farthestPathPoints;
    private IReadOnlyList<WorldPoint>? _queryPathPoints;
    private WorldPoint? _previewEnd;

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
            _metersPerDrawingUnit = document.MetersPerDrawingUnit;
            _dxfPath = dialog.FileName;
            ResetAnalysis(clearExits: true);
            StatusText.Text = $"{System.IO.Path.GetFileName(dialog.FileName)} 불러옴 · 벽 선분 {_walls.Count:N0}개 · 1 도면 단위 = {_metersPerDrawingUnit:G6} m";
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
                Version: 3,
                CellSize: cellSize.Value,
                MetersPerDrawingUnit: _metersPerDrawingUnit,
                Walls: _walls.ToList(),
                Exits: _exitEditor.Segments.ToList(),
                DxfPath: _dxfPath));
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
            _dxfPath = data.DxfPath;
            _metersPerDrawingUnit = data.MetersPerDrawingUnit;
            CellSizeBox.Text = data.CellSize.ToString(CultureInfo.InvariantCulture);
            ResetAnalysis(clearExits: true);
            _exitEditor.LoadSegments(data.Exits);
            StatusText.Text = $"프로젝트 v{data.Version} 불러옴: {System.IO.Path.GetFileName(dialog.FileName)} · 출구 {_exitEditor.Segments.Count}개";
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
            StatusText.Text = "도면을 두 번 클릭해 출구 선분을 지정하세요. 우클릭: 그리기 취소/마지막 출구 삭제";
            return;
        }

        if (_exitEditor.PendingStart is not null)
        {
            _exitEditor.HandleRightClick();
            _previewEnd = null;
            StatusText.Text = "출구 선분 그리기가 취소되었습니다.";
            Redraw();
        }
    }

    private void OnClearExits(object sender, RoutedEventArgs e)
    {
        ResetAnalysis(clearExits: true);
        StatusText.Text = "모든 출구가 초기화되었습니다.";
        Redraw();
    }

    private void OnCanvasLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_walls.Count == 0 || _transform is null)
        {
            return;
        }

        var worldPoint = _transform.ToWorld(e.GetPosition(DrawingCanvas));
        if (_addExitMode)
        {
            if (_exitEditor.HandleLeftClick(worldPoint))
            {
                _previewEnd = null;
                InvalidateAnalysis();
                StatusText.Text = $"출구 {_exitEditor.Segments.Count}개 지정됨 · 우클릭: 마지막 출구 삭제";
            }
            else
            {
                _previewEnd = worldPoint;
                StatusText.Text = "출구 시작점 지정됨 · 끝점을 클릭하세요. 우클릭: 그리기 취소";
            }
            Redraw();
            return;
        }

        if (_grid is null || _result is null)
        {
            return;
        }

        _queryPoint = worldPoint;
        _queryDistance = DistanceMapCalculator.GetDistanceAt(_grid, _result, worldPoint);
        _queryPathPoints = _queryDistance is null
            ? null
            : DistanceMapCalculator.GetPath(_grid, _result, worldPoint);
        StatusText.Text = _queryDistance is { } distance
            ? $"선택 지점 → 가장 가까운 출구: {distance:F2} m"
            : "선택 지점은 벽 위이거나 출구에서 도달할 수 없습니다.";
        Redraw();
    }

    private void OnCanvasRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!_addExitMode)
        {
            return;
        }

        e.Handled = true;
        int previousCount = _exitEditor.Segments.Count;
        bool cancelledPending = _exitEditor.HandleRightClick();
        _previewEnd = null;
        if (cancelledPending)
        {
            StatusText.Text = "출구 선분 그리기를 취소했습니다.";
        }
        else if (_exitEditor.Segments.Count < previousCount)
        {
            InvalidateAnalysis();
            StatusText.Text = $"마지막 출구를 삭제했습니다. 남은 출구: {_exitEditor.Segments.Count}개";
        }
        else
        {
            StatusText.Text = "삭제할 출구가 없습니다.";
        }
        Redraw();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_addExitMode || _exitEditor.PendingStart is null || _transform is null)
        {
            return;
        }

        _previewEnd = _transform.ToWorld(e.GetPosition(DrawingCanvas));
        Redraw();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

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

    private void OnCalculate(object sender, RoutedEventArgs e)
    {
        if (_walls.Count == 0)
        {
            MessageBox.Show(this, "먼저 DXF 또는 프로젝트를 불러오세요.", "알림");
            return;
        }
        if (_exitEditor.Segments.Count == 0)
        {
            MessageBox.Show(this, "출구를 최소 1개 지정하세요.", "알림");
            return;
        }

        double? cellSize = ParseCellSize();
        if (cellSize is null)
        {
            return;
        }

        try
        {
            _grid = WalkabilityGrid.Build(_walls, cellSize.Value);
            _result = null;
            _farthestPathPoints = null;
            _queryPathPoints = null;
            var sources = _exitEditor.Segments
                .SelectMany(exit => _grid.WalkableCellsNearSegment(exit, _grid.CellSize))
                .Distinct()
                .ToList();
            if (sources.Count == 0)
            {
                MessageBox.Show(this, "출구 위치 근처에서 통행 가능한 셀을 찾을 수 없습니다.", "알림");
                return;
            }

            _result = DistanceMapCalculator.Compute(_grid, sources);
            _farthestPathPoints = _result.FarthestCell is { } farthest
                ? DistanceMapCalculator.GetPath(_grid, _result, _grid.CellCenter(farthest.Col, farthest.Row))
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
            _farthestPathPoints = null;
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
            _previewEnd = null;
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

    private void Redraw()
    {
        DrawingCanvas.Children.Clear();
        if (_walls.Count == 0 || DrawingCanvas.ActualWidth <= 0 || DrawingCanvas.ActualHeight <= 0)
        {
            _transform = null;
            return;
        }

        var bounds = Bounds.FromSegments(_walls);
        _transform = ViewTransform.Build(bounds, DrawingCanvas.ActualWidth, DrawingCanvas.ActualHeight);

        if (_grid is not null && _result is not null)
        {
            DrawHeatmap(_grid, _result);
        }

        foreach (var wall in _walls)
        {
            var start = _transform.ToScreen(wall.Start);
            var end = _transform.ToScreen(wall.End);
            DrawingCanvas.Children.Add(new Line
            {
                X1 = start.X,
                Y1 = start.Y,
                X2 = end.X,
                Y2 = end.Y,
                Stroke = Brushes.Black,
                StrokeThickness = 1.5,
            });
        }

        foreach (var exit in _exitEditor.Segments)
        {
            var start = _transform.ToScreen(exit.Start);
            var end = _transform.ToScreen(exit.End);
            DrawingCanvas.Children.Add(new Line
            {
                X1 = start.X,
                Y1 = start.Y,
                X2 = end.X,
                Y2 = end.Y,
                Stroke = Brushes.LimeGreen,
                StrokeThickness = 3,
                ToolTip = "출구 선분",
            });
            AddMarker(start, 4, Brushes.LimeGreen, "출구 시작점");
            AddMarker(end, 4, Brushes.LimeGreen, "출구 끝점");
        }

        if (_exitEditor.PendingStart is { } pendingStart)
        {
            AddMarker(_transform.ToScreen(pendingStart), 4, Brushes.LightGreen, "출구 시작점 (지정 중)");
            if (_previewEnd is { } previewEnd)
            {
                AddPath([pendingStart, previewEnd], Brushes.LightGreen, 2);
            }
        }

        AddPath(_farthestPathPoints, Brushes.OrangeRed, 2.5);
        AddPath(_queryPathPoints, Brushes.DeepSkyBlue, 2.5);

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

    private void DrawHeatmap(WalkabilityGrid grid, DistanceMapResult result)
    {
        if (_transform is null)
        {
            return;
        }

        var bitmap = HeatmapRenderer.Render(grid, result.Distances, result.MaxDistance);
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

    private sealed record UnitChoice(string Label, double MetersPerUnit);
}

/// <summary>
/// Maps DXF world coordinates (Y-up) to the WPF canvas (Y-down).
/// </summary>
public sealed class ViewTransform
{
    public double Scale { get; }
    private readonly double _offsetX;
    private readonly double _offsetY;
    private readonly double _canvasHeight;
    private readonly double _minX;
    private readonly double _minY;

    private ViewTransform(double scale, double offsetX, double offsetY, double canvasHeight, double minX, double minY)
    {
        Scale = scale;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _canvasHeight = canvasHeight;
        _minX = minX;
        _minY = minY;
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
        return new ViewTransform(scale, offsetX, offsetY, canvasHeight, bounds.MinX, bounds.MinY);
    }

    public Point ToScreen(WorldPoint point) => new(
        _offsetX + (point.X - _minX) * Scale,
        _canvasHeight - _offsetY - (point.Y - _minY) * Scale);

    public WorldPoint ToWorld(Point point) => new(
        _minX + (point.X - _offsetX) / Scale,
        _minY + (_canvasHeight - _offsetY - point.Y) / Scale);
}
