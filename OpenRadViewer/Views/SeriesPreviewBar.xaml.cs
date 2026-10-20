// @purpose: Left thumbnail sidebar component displaying patient studies, series thumbnails, slice counts, scout indicators, and drag-and-drop onto viewports.
// @depends: OpenRadViewer.Core.Models, OpenRadViewer.ViewModels
// @exports: SeriesPreviewBar

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenRadViewer.Core.Models;
using OpenRadViewer.ViewModels;

namespace OpenRadViewer.Views;

public partial class SeriesPreviewBar : UserControl
{
    private Point _dragStartPoint;
    private Window? _dragGhostWindow;

    private bool _isPotentialDrag;
    private SeriesModel? _pendingClickSeries;

    public SeriesPreviewBar()
    {
        InitializeComponent();
    }

    private void OnSeriesCardClicked(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        if (sender is FrameworkElement fe && fe.DataContext is SeriesModel series)
        {
            _pendingClickSeries = series;
            _isPotentialDrag = true;
        }
    }

    private void OnSeriesCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPotentialDrag && _pendingClickSeries != null)
        {
            _isPotentialDrag = false;
            var mainVm = DataContext as MainViewModel;
            if (mainVm?.ActiveViewport != null)
            {
                mainVm.ActiveViewport.LoadSeries(_pendingClickSeries);
            }
            _pendingClickSeries = null;
        }
    }

    private void OnSeriesCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPotentialDrag && e.LeftButton == MouseButtonState.Pressed && _pendingClickSeries != null)
        {
            Point current = e.GetPosition(this);
            if (Math.Abs(current.X - _dragStartPoint.X) > 5 || Math.Abs(current.Y - _dragStartPoint.Y) > 5)
            {
                _isPotentialDrag = false;
                var series = _pendingClickSeries;
                _pendingClickSeries = null;

                if (sender is FrameworkElement fe)
                {
                    // Create floating semi-transparent drag ghost window
                    ShowDragGhost(series);

                    var data = new DataObject();
                    data.SetData(typeof(SeriesModel), series);

                    void OnQueryContinueDrag(object? s, QueryContinueDragEventArgs qe)
                    {
                        if (qe.Action == DragAction.Continue)
                        {
                            UpdateDragGhostPosition();
                        }
                        else
                        {
                            CloseDragGhost();
                        }
                    }

                    QueryContinueDragEventHandler dragHandler = OnQueryContinueDrag;
                    fe.QueryContinueDrag += dragHandler;

                    try
                    {
                        DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy);
                    }
                    finally
                    {
                        fe.QueryContinueDrag -= dragHandler;
                        CloseDragGhost();
                    }
                }
            }
        }
    }

    private void ShowDragGhost(SeriesModel series)
    {
        CloseDragGhost();

        var ghostGrid = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
            Width = 110,
            Height = 110
        };

        if (series.ThumbnailImage != null)
        {
            var img = new Image
            {
                Source = series.ThumbnailImage,
                Stretch = Stretch.Uniform
            };
            ghostGrid.Children.Add(img);
        }

        var labelBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            Padding = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = series.DisplayTitle,
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        ghostGrid.Children.Add(labelBorder);

        var outerBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(245, 175, 40)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = ghostGrid
        };

        _dragGhostWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Opacity = 0.65, // ~60-65% semi-transparent as requested
            ShowInTaskbar = false,
            Topmost = true,
            IsHitTestVisible = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = outerBorder
        };

        UpdateDragGhostPosition();
        _dragGhostWindow.Show();
    }

    private void UpdateDragGhostPosition()
    {
        if (_dragGhostWindow == null) return;
        var p = GetCursorPos();
        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        if (dpiX <= 0) dpiX = 1.0;
        if (dpiY <= 0) dpiY = 1.0;

        _dragGhostWindow.Left = (p.X / dpiX) + 12;
        _dragGhostWindow.Top = (p.Y / dpiY) + 12;
    }

    private void CloseDragGhost()
    {
        if (_dragGhostWindow != null)
        {
            _dragGhostWindow.Close();
            _dragGhostWindow = null;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private static Point GetCursorPos()
    {
        if (GetCursorPos(out POINT pt))
            return new Point(pt.X, pt.Y);
        return new Point(0, 0);
    }

    private void OnCloseSeriesClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SeriesModel series)
        {
            var mainVm = DataContext as MainViewModel;
            mainVm?.CloseSeries(series);
        }
    }

    private void OnSplitSeriesClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SeriesModel series)
        {
            var mainVm = DataContext as MainViewModel;
            mainVm?.SplitGivenSeries(series, OpenRadViewer.Core.Services.SplitCriterion.Auto);
        }
    }

    private void OnCopyPatientNameClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PatientGroupModel pGroup)
        {
            if (!string.IsNullOrWhiteSpace(pGroup.PatientName))
            {
                Clipboard.SetText(pGroup.PatientName);
                var mainVm = DataContext as MainViewModel;
                if (mainVm != null)
                {
                    mainVm.StatusMessage = $"Copied Patient Name: {pGroup.PatientName}";
                }
            }
        }
    }

    private void OnCopyPatientIdClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PatientGroupModel pGroup)
        {
            if (!string.IsNullOrWhiteSpace(pGroup.PatientId))
            {
                Clipboard.SetText(pGroup.PatientId);
                var mainVm = DataContext as MainViewModel;
                if (mainVm != null)
                {
                    mainVm.StatusMessage = $"Copied CNP / Patient ID: {pGroup.PatientId}";
                }
            }
        }
    }

    private void OnClosePatientClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PatientGroupModel pGroup)
        {
            var mainVm = DataContext as MainViewModel;
            mainVm?.ClosePatient(pGroup);
        }
    }

    public bool IsCollapsed { get; private set; }

    public void SetCollapsedState(bool collapsed)
    {
        IsCollapsed = collapsed;
        BorderNormalHeader.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        BorderCollapsedHeader.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        SeriesScrollViewer.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        BorderCollapsedStrip.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleCollapseClicked(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this) as MainWindow;
        win?.ToggleSeriesBarCollapse();
    }

    private void OnCollapsedStripClicked(object sender, MouseButtonEventArgs e)
    {
        var win = Window.GetWindow(this) as MainWindow;
        win?.ToggleSeriesBarCollapse();
    }
}
