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
    private List<Segment> _walls = new();
    private readonly List<WorldPoint> _exits = new();
    private WalkabilityGrid? _grid;
    private DistanceMapResult? _result;
    private string? _dxfPath;
    private bool _addExitMode;
    private ViewTransform? _transform;

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
            _walls = DxfLoader.LoadWalls(dialog.FileName).ToList();
            _dxfPath = dialog.FileName;
            _exits.Clear();
            _grid = null;
            _result = null;
            StatusText.Text = $"{System.IO.Path.GetFileName(dialog.FileName)} 불러옴 (선분 {_walls.Count}개)";
            Redraw();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"DXF 불러오기 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSaveProject(object sender, RoutedEventArgs e)
    {
        if (_dxfPath is null)
        {
            MessageBox.Show(this, "먼저 DXF 파일을 불러오세요.", "알림");
            return;
        }

        var dialog = new SaveFileDialog { Filter = "프로젝트 파일|*.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var cellSize = ParseCellSize();
        ProjectFile.Save(dialog.FileName, new ProjectData(_dxfPath, cellSize ?? 0.3, _exits.ToList()));
        StatusText.Text = $"프로젝트 저장됨: {System.IO.Path.GetFileName(dialog.FileName)}";
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "프로젝트 파일|*.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var data = ProjectFile.Load(dialog.FileName);
            _walls = DxfLoader.LoadWalls(data.DxfPath).ToList();
            _dxfPath = data.DxfPath;
            CellSizeBox.Text = data.CellSize.ToString(CultureInfo.InvariantCulture);
            _exits.Clear();
            _exits.AddRange(data.Exits);
            _grid = null;
            _result = null;
            StatusText.Text = $"프로젝트 불러옴: {System.IO.Path.GetFileName(dialog.FileName)}";
            Redraw();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로젝트 불러오기 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAddExitModeChanged(object sender, RoutedEventArgs e)
    {
        _addExitMode = AddExitToggle.IsChecked == true;
        DrawingCanvas.Cursor = _addExitMode ? Cursors.Cross : Cursors.Arrow;
    }

    private void OnClearExits(object sender, RoutedEventArgs e)
    {
        _exits.Clear();
        _result = null;
        StatusText.Text = "출구가 초기화되었습니다.";
        Redraw();
    }

    private void OnCanvasClick(object sender, MouseButtonEventArgs e)
    {
        if (!_addExitMode || _walls.Count == 0 || _transform is null)
        {
            return;
        }

        var screenPoint = e.GetPosition(DrawingCanvas);
        _exits.Add(_transform.ToWorld(screenPoint));
        _result = null;
        Redraw();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private double? ParseCellSize()
    {
        if (double.TryParse(CellSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cellSize) && cellSize > 0)
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
            MessageBox.Show(this, "먼저 DXF 파일을 불러오세요.", "알림");
            return;
        }

        if (_exits.Count == 0)
        {
            MessageBox.Show(this, "출구를 최소 1개 지정하세요.", "알림");
            return;
        }

        var cellSize = ParseCellSize();
        if (cellSize is null)
        {
            return;
        }

        _grid = WalkabilityGrid.Build(_walls, cellSize.Value);

        var sources = new List<(int Col, int Row)>();
        foreach (var exit in _exits)
        {
            var cell = _grid.NearestWalkableCell(exit);
            if (cell is not null)
            {
                sources.Add(cell.Value);
            }
        }

        if (sources.Count == 0)
        {
            MessageBox.Show(this, "출구 위치 근처에서 통행 가능한 셀을 찾을 수 없습니다.", "알림");
            return;
        }

        _result = DistanceMapCalculator.Compute(_grid, sources);
        StatusText.Text = _result.FarthestCell is null
            ? "도달 가능한 영역이 없습니다."
            : $"최대 보행거리: {_result.MaxDistance:F2} m";

        Redraw();
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
            var p1 = _transform.ToScreen(wall.Start);
            var p2 = _transform.ToScreen(wall.End);
            DrawingCanvas.Children.Add(new Line
            {
                X1 = p1.X,
                Y1 = p1.Y,
                X2 = p2.X,
                Y2 = p2.Y,
                Stroke = Brushes.Black,
                StrokeThickness = 1.5
            });
        }

        foreach (var exit in _exits)
        {
            var p = _transform.ToScreen(exit);
            AddMarker(p, 6, Brushes.LimeGreen, "출구");
        }

        if (_grid is not null && _result?.FarthestCell is { } farthest)
        {
            var worldPoint = _grid.CellCenter(farthest.Col, farthest.Row);
            var p = _transform.ToScreen(worldPoint);
            AddMarker(p, 8, Brushes.Red, $"최대 보행거리 지점 ({_result.MaxDistance:F2} m)");
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
            Stretch = Stretch.Fill
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
            ToolTip = tooltip
        };
        Canvas.SetLeft(ellipse, center.X - radius);
        Canvas.SetTop(ellipse, center.Y - radius);
        DrawingCanvas.Children.Add(ellipse);
    }
}

/// <summary>
/// Maps between DXF world coordinates (Y-up) and canvas screen coordinates (Y-down),
/// scaling the drawing bounds to fit and center within the available canvas size.
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

    public Point ToScreen(WorldPoint p) => new(
        _offsetX + (p.X - _minX) * Scale,
        _canvasHeight - _offsetY - (p.Y - _minY) * Scale);

    public WorldPoint ToWorld(Point p) => new(
        _minX + (p.X - _offsetX) / Scale,
        _minY + (_canvasHeight - _offsetY - p.Y) / Scale);
}
