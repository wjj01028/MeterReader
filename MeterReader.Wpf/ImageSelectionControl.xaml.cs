using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MeterReader.Wpf;

public partial class ImageSelectionControl : UserControl
{
    private Point _startPoint;
    private bool _isDragging;
    private bool _isPanning;
    private bool _spaceHeld;
    private double _panStartX;
    private double _panStartY;
    private double _currentScale = 1.0;
    private readonly BrushConverter _brushConverter = new();

    public event Action<double, double, double, double>? RegionSelected;

    public ImageSelectionControl()
    {
        InitializeComponent();

        // 监听空格键
        Loaded += (_, _) =>
        {
            DependencyObject obj = this;
            while (obj != null)
            {
                if (obj is Window win)
                {
                    win.KeyDown += OnKeyDown;
                    win.KeyUp += OnKeyUp;
                    break;
                }
                obj = VisualTreeHelper.GetParent(obj);
            }
        };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceHeld = true;
            e.Handled = true;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceHeld = false;
            if (_isPanning)
            {
                _isPanning = false;
                ImageCanvas.ReleaseMouseCapture();
            }
            e.Handled = true;
        }
    }

    public void LoadImage(string filePath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(filePath);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();

        DisplayImage.Source = bitmap;
        ImageCanvas.Width = bitmap.PixelWidth;
        ImageCanvas.Height = bitmap.PixelHeight;
        Placeholder.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Collapsed;

        FitToView();
    }

    private void FitToView()
    {
        if (DisplayImage.Source is not BitmapSource bitmap) return;

        var availableW = ImageScrollViewer.ActualWidth - 10;
        var availableH = ImageScrollViewer.ActualHeight - 10;

        if (availableW <= 0 || availableH <= 0) return;

        var scaleX = availableW / bitmap.PixelWidth;
        var scaleY = availableH / bitmap.PixelHeight;
        _currentScale = Math.Min(scaleX, scaleY);

        ZoomTransform.ScaleX = _currentScale;
        ZoomTransform.ScaleY = _currentScale;
    }

    public void ClearSelection()
    {
        SelectionRect.Visibility = Visibility.Collapsed;
    }

    public void SetSelectionColor(string hexColor)
    {
        SelectionRect.Stroke = (Brush)_brushConverter.ConvertFromString(hexColor)!;
    }

    public void ShowSelectionRect(double pixelX, double pixelY, double pixelW, double pixelH)
    {
        if (DisplayImage.Source == null) return;

        Canvas.SetLeft(SelectionRect, pixelX);
        Canvas.SetTop(SelectionRect, pixelY);
        SelectionRect.Width = pixelW;
        SelectionRect.Height = pixelH;
        SelectionRect.Visibility = Visibility.Visible;
    }

    public void RenderMeterOverlays(List<MeterPosition> meters)
    {
        OverlayCanvas.Children.Clear();

        foreach (var m in meters)
        {
            if (m.Width <= 0 || m.Height <= 0) continue;

            var color = string.IsNullOrEmpty(m.Color) ? "#0044CC" : m.Color;
            var brush = (Brush)_brushConverter.ConvertFromString(color)!;

            var rect = new Rectangle
            {
                Stroke = brush,
                StrokeThickness = 10,
                Fill = Brushes.Transparent,
                StrokeDashArray = new DoubleCollection { 6, 3 },
                Width = m.Width,
                Height = m.Height
            };
            Canvas.SetLeft(rect, m.X);
            Canvas.SetTop(rect, m.Y);
            OverlayCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = $"#{m.Index}",
                Foreground = Brushes.White,
                Background = brush,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(4, 1, 4, 1)
            };
            Canvas.SetLeft(label, m.X);
            Canvas.SetTop(label, m.Y - 16);
            OverlayCanvas.Children.Add(label);
        }
    }

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DisplayImage.Source != null)
            FitToView();
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _currentScale = Math.Min(_currentScale + 0.2, 5.0);
        ZoomTransform.ScaleX = _currentScale;
        ZoomTransform.ScaleY = _currentScale;
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _currentScale = Math.Max(_currentScale - 0.2, 0.1);
        ZoomTransform.ScaleX = _currentScale;
        ZoomTransform.ScaleY = _currentScale;
    }

    private void ImageCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DisplayImage.Source == null) return;

        if (_spaceHeld)
        {
            // 空格按下 → 拖拽平移图片
            _isPanning = true;
            _panStartX = ImageScrollViewer.HorizontalOffset + e.GetPosition(ImageScrollViewer).X;
            _panStartY = ImageScrollViewer.VerticalOffset + e.GetPosition(ImageScrollViewer).Y;
            ImageCanvas.CaptureMouse();
            return;
        }

        // 正常模式 → 框选
        _startPoint = e.GetPosition(ImageCanvas);
        _isDragging = true;

        Canvas.SetLeft(SelectionRect, _startPoint.X);
        Canvas.SetTop(SelectionRect, _startPoint.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRect.Visibility = Visibility.Visible;

        ImageCanvas.CaptureMouse();
    }

    private void ImageCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            // 平移模式
            var pos = e.GetPosition(ImageScrollViewer);
            ImageScrollViewer.ScrollToHorizontalOffset(_panStartX - pos.X);
            ImageScrollViewer.ScrollToVerticalOffset(_panStartY - pos.Y);
            return;
        }

        if (!_isDragging) return;

        var currentPoint = e.GetPosition(ImageCanvas);

        var x = Math.Min(_startPoint.X, currentPoint.X);
        var y = Math.Min(_startPoint.Y, currentPoint.Y);
        var w = Math.Abs(currentPoint.X - _startPoint.X);
        var h = Math.Abs(currentPoint.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
    }

    private void ImageCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            ImageCanvas.ReleaseMouseCapture();
            return;
        }

        _isDragging = false;
        ImageCanvas.ReleaseMouseCapture();

        if (DisplayImage.Source is not BitmapSource bitmap) return;

        var canvasX = Canvas.GetLeft(SelectionRect);
        var canvasY = Canvas.GetTop(SelectionRect);
        var canvasSelW = SelectionRect.Width;
        var canvasSelH = SelectionRect.Height;

        if (double.IsNaN(canvasX)) canvasX = _startPoint.X;
        if (double.IsNaN(canvasY)) canvasY = _startPoint.Y;
        if (double.IsNaN(canvasSelW)) canvasSelW = Math.Abs(SelectionRect.Width);
        if (double.IsNaN(canvasSelH)) canvasSelH = Math.Abs(SelectionRect.Height);

        if (canvasSelW < 5 || canvasSelH < 5)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        var selectionX = canvasX;
        var selectionY = canvasY;
        var selectionW = canvasSelW;
        var selectionH = canvasSelH;

        selectionX = Math.Max(0, selectionX);
        selectionY = Math.Max(0, selectionY);
        selectionW = Math.Min(bitmap.PixelWidth - selectionX, selectionW);
        selectionH = Math.Min(bitmap.PixelHeight - selectionY, selectionH);

        RegionSelected?.Invoke(selectionX, selectionY, selectionW, selectionH);
    }
}
