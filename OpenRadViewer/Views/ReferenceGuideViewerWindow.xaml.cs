// @purpose: Image viewer window for radiological classification charts and tables with pan, zoom, and fit-to-window controls.
// @depends: OpenRadViewer.Core.Models, OpenRadViewer.Core.Services (ReferenceGuideService, DarkWindowHelper)
// @exports: ReferenceGuideViewerWindow

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public partial class ReferenceGuideViewerWindow : Window
{
    private readonly ReferenceGuideItem _item;
    private Point _lastMousePos;
    private bool _isDragging = false;
    private double _currentZoom = 1.0;

    public ReferenceGuideViewerWindow(ReferenceGuideItem item)
    {
        InitializeComponent();
        _item = item;
        DarkWindowHelper.ApplyDarkTitleBar(this);

        Title = $"{item.Title} - Parsec Reference Guide";
        TxtTitle.Text = item.Title;

        if (string.Equals(item.Category, "Table", StringComparison.OrdinalIgnoreCase))
        {
            TxtCategoryBadge.Text = "REFERENCE TABLE";
            BadgeCategory.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#00838F")!;
        }
        else
        {
            TxtCategoryBadge.Text = "CLASSIFICATION";
            BadgeCategory.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#C27D00")!;
        }

        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            TxtDescription.Text = item.Description;
            TxtDescription.Visibility = Visibility.Visible;
        }

        if (!item.IsBuiltIn)
        {
            BtnDelete.Visibility = Visibility.Visible;
        }

        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var bmp = ReferenceGuideService.LoadBitmapSafely(_item.ImagePath);
        if (bmp != null)
        {
            GuideImage.Source = bmp;
            FitToWindow();
        }
        else
        {
            MessageBox.Show(this, $"Failed to load image for this guide:\n{_item.ImagePath}", "Image Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void FitToWindow()
    {
        if (GuideImage.Source is not BitmapSource src) return;

        double viewportW = ActualWidth - 40;
        double viewportH = ActualHeight - 120;

        if (viewportW <= 0 || viewportH <= 0 || src.PixelWidth <= 0 || src.PixelHeight <= 0)
        {
            SetZoom(1.0);
            return;
        }

        double scaleX = viewportW / src.PixelWidth;
        double scaleY = viewportH / src.PixelHeight;
        double scale = Math.Min(scaleX, scaleY);

        if (scale > 1.2) scale = 1.0; // Don't over-magnify small images on fit

        ImgTranslate.X = 0;
        ImgTranslate.Y = 0;
        SetZoom(Math.Max(0.1, scale));
    }

    private void SetZoom(double zoom)
    {
        _currentZoom = Math.Clamp(zoom, 0.1, 10.0);
        ImgScale.ScaleX = _currentZoom;
        ImgScale.ScaleY = _currentZoom;
        TxtStatus.Text = $"Zoom: {Math.Round(_currentZoom * 100)}%";
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        SetZoom(_currentZoom * 1.25);
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        SetZoom(_currentZoom / 1.25);
    }

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        FitToWindow();
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        ImgTranslate.X = 0;
        ImgTranslate.Y = 0;
        SetZoom(1.0);
    }

    private void OnCopyImageClick(object sender, RoutedEventArgs e)
    {
        if (GuideImage.Source is BitmapSource bmp)
        {
            try
            {
                Clipboard.SetImage(bmp);
                TxtStatus.Text = "Image copied to clipboard!";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to copy image to clipboard:\n{ex.Message}", "Clipboard Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(this,
            $"Are you sure you want to delete this custom reference guide?\n\nTitle: {_item.Title}\nCategory: {_item.Category}",
            "Confirm Delete Guide",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            ReferenceGuideService.Instance.DeleteCustomItem(_item.Id);
            Close();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.15 : (1.0 / 1.15);
        SetZoom(_currentZoom * factor);
        e.Handled = true;
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isDragging = true;
            _lastMousePos = e.GetPosition(this);
            Mouse.Capture((IInputElement)sender);
            e.Handled = true;
        }
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            Mouse.Capture(null);
            e.Handled = true;
        }
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            Point currentPos = e.GetPosition(this);
            double deltaX = currentPos.X - _lastMousePos.X;
            double deltaY = currentPos.Y - _lastMousePos.Y;

            ImgTranslate.X += deltaX;
            ImgTranslate.Y += deltaY;

            _lastMousePos = currentPos;
            e.Handled = true;
        }
    }
}
