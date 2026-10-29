// @purpose: Core interactive DICOM canvas control rendering pixel data, handling mouse gesture pipelines (WL/WW, pan, zoom, measurements, MPR crosshairs), file drag-and-drop, and HUD overlays.
// @depends: OpenRadViewer.ViewModels, OpenRadViewer.Core.Services, OpenRadViewer.Core.Models, OpenRadViewer.Core.Config
// @exports: ViewportControl

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OpenRadViewer.Core.Config;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;
using OpenRadViewer.ViewModels;

namespace OpenRadViewer.Views;

public partial class ViewportControl : UserControl
{
    private Point _lastMousePos;
    private Point _dragStartPos;
    private bool _hasActuallyDragged;
    private bool _isDragging;
    private MouseInputTrigger _currentDragTrigger;
    private MeasurementItem? _activeMeasurement;
    private MeasurementItem? _selectedMeasurement;
    private bool _isMovingMeasurement;
    private bool _isDraggingMeasurementText;
    private Point _dragTextStartMouse;
    private Point _dragTextStartOffset;
    private bool _isResizingHandle;
    private int _activeHandleIndex = -1;
    private int _angleCreationStep = 0; // 0: idle, 1: placing vertex, 2: placing end
    private Point _dragMeasurementStartImgPt;
    private List<Point> _origMeasurementPoints = new();
    private VertebraLabelItem? _draggedVertebra;
    private bool _isDraggingVertebra;
    private Point _dragVertebraStartImgPt;
    private Point _dragVertebraOrigImgPt;
    private bool _hasActuallyDraggedVertebra;
    private double _browseAccumulatorY;

    private enum MprDragTarget { None, Intersection, LineTranslate, LineRotate }
    private MprDragTarget _mprDragTarget = MprDragTarget.None;
    private MprReferenceLine? _draggedMprLine = null;
    private Point _mprRotateCenterScreen;
    private double _mprRotateInitialAngle;
    private (double x, double y, double z)? _mprPivot3D;
    private bool _isEditingVertebra = false;

    public ViewportControl()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        DataContextChanged += OnDataContextChanged;

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseWheel += OnMouseWheel;
        KeyDown += OnViewportKeyDown;

        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ViewportViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            oldVm.Measurements.CollectionChanged -= OnMeasurementsChanged;
            oldVm.VertebraLabels.CollectionChanged -= OnMeasurementsChanged;
            oldVm.MprLinesChanged -= RedrawMeasurements;
        }
        if (e.NewValue is ViewportViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            newVm.Measurements.CollectionChanged += OnMeasurementsChanged;
            newVm.VertebraLabels.CollectionChanged += OnMeasurementsChanged;
            newVm.MprLinesChanged += RedrawMeasurements;
        }
        Dispatcher.InvokeAsync(() =>
        {
            if (ViewModel?.NeedsAutoFit == true)
            {
                AutoFitToViewport(forceResetZoom: true);
            }
            else
            {
                CenterImage();
            }
            RedrawMeasurements();
        });
    }

    private void OnMeasurementsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RedrawMeasurements();
    }

    private void OnViewportKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;

        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            var selVert = vm.VertebraLabels.Where(v => v.IsSelected && v.SliceIndex == vm.CurrentSliceIndex).ToList();
            if (selVert.Count > 0)
            {
                foreach (var v in selVert) vm.VertebraLabels.Remove(v);
                RedrawMeasurements();
                e.Handled = true;
                return;
            }

            var selected = vm.Measurements.Where(m => m.IsSelected).ToList();
            if (selected.Count > 0)
            {
                foreach (var item in selected)
                {
                    vm.Measurements.Remove(item);
                }
                _selectedMeasurement = null;
                RedrawMeasurements();
                e.Handled = true;
                return;
            }

            var mainVm = GetMainViewModel();
            if (mainVm?.LeftTool == MouseToolAction.VertebraLabel)
            {
                var lastVert = vm.VertebraLabels.LastOrDefault(v => v.SliceIndex == vm.CurrentSliceIndex);
                if (lastVert != null)
                {
                    vm.VertebraLabels.Remove(lastVert);
                    RedrawMeasurements();
                    e.Handled = true;
                    return;
                }
            }
        }
        else if (e.Key == Key.Escape)
        {
            bool changed = false;
            foreach (var m in vm.Measurements) { if (m.IsSelected) { m.IsSelected = false; changed = true; } }
            foreach (var v in vm.VertebraLabels) { if (v.IsSelected) { v.IsSelected = false; changed = true; } }
            _selectedMeasurement = null;
            _draggedVertebra = null;
            if (changed) RedrawMeasurements();
            e.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewportViewModel.CurrentSeries) or
                              nameof(ViewportViewModel.DisplayImage) or
                              nameof(ViewportViewModel.Crosshair3DPoint) or 
                              nameof(ViewportViewModel.ShowCrosshair3D) or 
                              nameof(ViewportViewModel.ShowScoutLine) or
                              nameof(ViewportViewModel.ScoutLineStart) or
                              nameof(ViewportViewModel.ScoutLineEnd) or
                              nameof(ViewportViewModel.Zoom) or 
                              nameof(ViewportViewModel.PanX) or 
                              nameof(ViewportViewModel.PanY) or
                              nameof(ViewportViewModel.CurrentSliceIndex))
        {
            Dispatcher.InvokeAsync(() =>
            {
                var vm = ViewModel;
                if (vm != null && vm.NeedsAutoFit)
                {
                    AutoFitToViewport(forceResetZoom: true);
                }
                else
                {
                    CenterImage();
                }
                UpdateRuler();
                RedrawMeasurements();
            });
        }
    }

    private MainViewModel? GetMainViewModel() =>
        Application.Current.MainWindow?.DataContext as MainViewModel;

    private ViewportViewModel? ViewModel => DataContext as ViewportViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var mainVm = GetMainViewModel();
        if (mainVm != null)
        {
            mainVm.PropertyChanged -= OnMainViewModelPropertyChanged;
            mainVm.PropertyChanged += OnMainViewModelPropertyChanged;
        }

        if (ViewModel?.NeedsAutoFit == true)
        {
            AutoFitToViewport(forceResetZoom: true);
        }
        else
        {
            CenterImage();
        }
        UpdateRuler();
        RedrawMeasurements();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var mainVm = GetMainViewModel();
        if (mainVm != null)
        {
            mainVm.PropertyChanged -= OnMainViewModelPropertyChanged;
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.MeasurementsCurrentSliceOnly))
        {
            Dispatcher.InvokeAsync(RedrawMeasurements);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var vm = ViewModel;
        if (vm != null && vm.NeedsAutoFit)
        {
            AutoFitToViewport(forceResetZoom: true);
        }
        else
        {
            CenterImage();
        }
        UpdateRuler();
        RedrawMeasurements();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (vm?.CurrentSeries != null && mainVm != null && mainVm.ActiveViewport != vm)
        {
            mainVm.SetActiveViewport(vm);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(SeriesModel)) || e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(SeriesModel)))
        {
            if (e.Data.GetData(typeof(SeriesModel)) is SeriesModel series)
            {
                var vm = ViewModel;
                var mainVm = GetMainViewModel();
                if (vm != null)
                {
                    int initialSlice = series.Slices.Count > 0 ? series.Slices.Count / 2 : 0;
                    if (mainVm != null && mainVm.CurrentSyncMode == SyncMode.Automatic && mainVm.ActiveViewport?.CurrentSlice != null && series.Slices.Count > 0)
                    {
                        var activeSlice = mainVm.ActiveViewport.CurrentSlice;
                        if (MprService.DetectOrientation(activeSlice) == MprService.DetectSeriesOrientation(series))
                        {
                            initialSlice = SynchronizationService.FindClosestSliceIndex(activeSlice, series);
                        }
                    }

                    if (mainVm != null)
                    {
                        mainVm.IsSyncSuspended = true;
                        try
                        {
                            vm.LoadSeries(series, initialSlice);
                            mainVm.SetActiveViewport(vm);
                        }
                        finally
                        {
                            mainVm.IsSyncSuspended = false;
                        }
                    }
                    else
                    {
                        vm.LoadSeries(series, initialSlice);
                    }

                    Dispatcher.InvokeAsync(() =>
                    {
                        AutoFitToViewport(forceResetZoom: true);
                        RedrawMeasurements();
                    });
                }
            }
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] filePaths && filePaths.Length > 0)
            {
                var mainVm = GetMainViewModel();
                if (mainVm != null)
                {
                    _ = mainVm.ScanPathsAsync(filePaths, targetViewport: ViewModel);
                }
            }
            e.Handled = true;
        }
    }

    public void CenterImage()
    {
        AutoFitToViewport();
    }

    public void AutoFitToViewport(bool forceResetZoom = false)
    {
        var vm = ViewModel;
        if (vm?.DisplayImage == null) return;

        double canvasWidth = ImageCanvas.ActualWidth > 0 ? ImageCanvas.ActualWidth : ActualWidth;
        double canvasHeight = ImageCanvas.ActualHeight > 0 ? ImageCanvas.ActualHeight : ActualHeight;
        double imgWidth = vm.DisplayImage.PixelWidth;
        double imgHeight = vm.DisplayImage.PixelHeight;

        if (canvasWidth <= 0 || canvasHeight <= 0 || imgWidth <= 0 || imgHeight <= 0)
            return;

        Canvas.SetLeft(SliceImage, (canvasWidth - imgWidth) / 2.0);
        Canvas.SetTop(SliceImage, (canvasHeight - imgHeight) / 2.0);

        if (forceResetZoom || vm.NeedsAutoFit || vm.Zoom <= 0.05)
        {
            // Auto-fit image to occupy 92% of viewport box cleanly
            double scaleX = (canvasWidth * 0.92) / imgWidth;
            double scaleY = (canvasHeight * 0.92) / imgHeight;
            double fitScale = Math.Min(scaleX, scaleY);
            if (fitScale > 0.01)
            {
                vm.Zoom = fitScale;
                vm.PanX = 0;
                vm.PanY = 0;
                vm.NeedsAutoFit = false;
            }
        }
    }

    /// <summary>
    /// Smoothly applies zoom to the viewport while preserving the screen anchor point.
    /// If pivotScreenPoint is null, it anchors on the exact center of the currently visible viewport.
    /// If pivotScreenPoint is provided (e.g. mouse cursor position), that exact point remains pinned under the cursor.
    /// </summary>
    public void ApplyZoom(double newZoom, Point? pivotScreenPoint = null)
    {
        var vm = ViewModel;
        if (vm == null || vm.DisplayImage == null) return;

        double oldZoom = Math.Max(0.01, vm.Zoom);
        newZoom = Math.Clamp(newZoom, 0.1, 20.0);
        if (Math.Abs(newZoom - oldZoom) < 0.00001) return;

        double cx = ActualWidth / 2.0;
        double cy = ActualHeight / 2.0;

        Point pivot = pivotScreenPoint ?? new Point(cx, cy);
        if (pivot.X < 0 || pivot.X > ActualWidth || pivot.Y < 0 || pivot.Y > ActualHeight || double.IsNaN(pivot.X) || double.IsNaN(pivot.Y))
        {
            pivot = new Point(cx, cy);
        }

        double scaleRatio = newZoom / oldZoom;
        double dx = pivot.X - cx;
        double dy = pivot.Y - cy;

        // Image point under pivot must remain stationary on screen:
        // Sx = cx + PanX + (Ix - W/2) * Zoom ==>
        // newPan = dx - (dx - oldPan) * scaleRatio
        double newPanX = dx - (dx - vm.PanX) * scaleRatio;
        double newPanY = dy - (dy - vm.PanY) * scaleRatio;

        vm.Zoom = newZoom;
        vm.PanX = newPanX;
        vm.PanY = newPanY;

        UpdateRuler();
        RedrawMeasurements();
    }

    private void UpdateRuler()
    {
        if (ActualHeight < 50) return;
        var vm = ViewModel;
        double spY = vm?.CurrentSlice?.PixelSpacingY ?? 1.0;
        if (spY <= 0.0001 || double.IsNaN(spY))
        {
            spY = vm?.CurrentSlice?.PixelSpacingX ?? 1.0;
            if (spY <= 0.0001 || double.IsNaN(spY)) spY = 1.0;
        }

        double zoom = Math.Max(0.01, vm?.Zoom ?? 1.0);
        // 1 cm = 10 mm. In screen pixels: (10.0 / spY) * zoom
        double cmScreen = (10.0 / spY) * zoom;
        if (cmScreen <= 0.5) return;

        int[] candidates = { 1, 2, 5, 10, 15, 20, 25 };
        int chosenCm = 5;
        double targetH = Math.Min(140.0, ActualHeight * 0.35);

        double bestDiff = double.MaxValue;
        foreach (int c in candidates)
        {
            double h = c * cmScreen;
            if (h >= 30.0 && h <= ActualHeight * 0.6)
            {
                double diff = Math.Abs(h - targetH);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    chosenCm = c;
                }
            }
        }

        double totalBarHeight = chosenCm * cmScreen;
        double startY = 0;
        double endY = totalBarHeight;
        double x = 4;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x, startY), false, false);
            ctx.LineTo(new Point(x, endY), true, false);

            ctx.BeginFigure(new Point(x, startY), false, false);
            ctx.LineTo(new Point(x + 6, startY), true, false);

            ctx.BeginFigure(new Point(x, endY), false, false);
            ctx.LineTo(new Point(x + 6, endY), true, false);

            for (int i = 1; i < chosenCm; i++)
            {
                double tickY = startY + i * cmScreen;
                ctx.BeginFigure(new Point(x, tickY), false, false);
                ctx.LineTo(new Point(x + 3.5, tickY), true, false);
            }
        }
        RulerPath.Data = geometry;
        RulerLabel.Text = $"{chosenCm} cm";
    }

    #region Coordinates Transformations
    private Point ScreenToImageCoords(Point screenPt)
    {
        var vm = ViewModel;
        if (vm?.DisplayImage == null) return screenPt;

        double cx = ActualWidth / 2.0;
        double cy = ActualHeight / 2.0;
        double imgW = vm.DisplayImage.PixelWidth;
        double imgH = vm.DisplayImage.PixelHeight;

        double ix = (screenPt.X - cx - vm.PanX) / Math.Max(0.01, vm.Zoom) + imgW / 2.0;
        double iy = (screenPt.Y - cy - vm.PanY) / Math.Max(0.01, vm.Zoom) + imgH / 2.0;
        return new Point(ix, iy);
    }

    private Point ImageToScreenCoords(Point imgPt)
    {
        var vm = ViewModel;
        if (vm?.DisplayImage == null) return imgPt;

        double cx = ActualWidth / 2.0;
        double cy = ActualHeight / 2.0;
        double imgW = vm.DisplayImage.PixelWidth;
        double imgH = vm.DisplayImage.PixelHeight;

        double sx = (imgPt.X - imgW / 2.0) * vm.Zoom + cx + vm.PanX;
        double sy = (imgPt.Y - imgH / 2.0) * vm.Zoom + cy + vm.PanY;
        return new Point(sx, sy);
    }

    private (Point h1, Point h2) GetZoomAdaptiveHandles(MprReferenceLine mprLine)
    {
        Point s1 = ImageToScreenCoords(mprLine.StartPoint);
        Point s2 = ImageToScreenCoords(mprLine.EndPoint);

        double minX = 22, minY = 22;
        double maxX = Math.Max(minX + 10, ActualWidth - 22);
        double maxY = Math.Max(minY + 10, ActualHeight - 22);

        double dx = s2.X - s1.X;
        double dy = s2.Y - s1.Y;

        double p0 = -dx, q0 = s1.X - minX;
        double p1 =  dx, q1 = maxX - s1.X;
        double p2 = -dy, q2 = s1.Y - minY;
        double p3 =  dy, q3 = maxY - s1.Y;

        double t0 = 0.0, t1 = 1.0;

        bool Clip(double p, double q)
        {
            if (p < 0)
            {
                double r = q / p;
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else if (p > 0)
            {
                double r = q / p;
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            else if (q < 0) return false;
            return true;
        }

        if (Clip(p0, q0) && Clip(p1, q1) && Clip(p2, q2) && Clip(p3, q3))
        {
            Point v1 = new Point(s1.X + t0 * dx, s1.Y + t0 * dy);
            Point v2 = new Point(s1.X + t1 * dx, s1.Y + t1 * dy);

            Vector diff = v2 - v1;
            double segLen = diff.Length;
            if (segLen > 40)
            {
                Vector dir = diff / segLen;
                Point handle1 = v1 + dir * 18.0;
                Point handle2 = v2 - dir * 18.0;
                return (handle1, handle2);
            }
            return (v1, v2);
        }

        return (s1, s2);
    }
    #endregion

    #region Mouse Events & Measurements
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (vm == null || mainVm == null) return;

        mainVm.SetActiveViewport(vm);
        Focus();

        // Double-click to Maximize / Restore this viewport panel (RadiAnt style)
        // Unless clicking on a vertebra label badge or measurement text badge (which resets offset)
        if (e.ChangedButton == MouseButton.Left && e.ClickCount >= 2)
        {
            Point mousePos = e.GetPosition(this);
            var textHit = FindMeasurementTextAt(mousePos);
            if (textHit != null && textHit.TextOffset != null)
            {
                textHit.TextOffset = null;
                RedrawMeasurements();
                e.Handled = true;
                return;
            }

            var onSliceVertebra = vm.VertebraLabels.Where(v => v.SliceIndex == vm.CurrentSliceIndex).ToList();
            bool nearVertebra = onSliceVertebra.Any(v =>
            {
                Point sCenter = ImageToScreenCoords(v.ImagePoint);
                Point sBadge = new Point(sCenter.X + 22, sCenter.Y - 11);
                return (Math.Abs(sCenter.X - mousePos.X) <= 35 && Math.Abs(sCenter.Y - mousePos.Y) <= 35) ||
                       (Math.Abs(sBadge.X - mousePos.X) <= 45 && Math.Abs(sBadge.Y - mousePos.Y) <= 25);
            });

            if (!nearVertebra && !_isEditingVertebra)
            {
                mainVm.ToggleMaximizeActivePanel();
                e.Handled = true;
                return;
            }
        }

        _lastMousePos = e.GetPosition(this);
        _dragStartPos = _lastMousePos;
        _hasActuallyDragged = false;
        Point imgPt = ScreenToImageCoords(_lastMousePos);
        _browseAccumulatorY = 0;

        // 0. 3D MPR Crosshair and Reference Line Dragging & Rotation
        if (vm.IsMprViewport && vm.MprLines.Count >= 2 && (mainVm?.ShowMprAxes ?? true) && e.ChangedButton == MouseButton.Left)
        {
            Point mousePos = _lastMousePos;
            Point s1A = ImageToScreenCoords(vm.MprLines[0].StartPoint);
            Point s1B = ImageToScreenCoords(vm.MprLines[0].EndPoint);
            Point s2A = ImageToScreenCoords(vm.MprLines[1].StartPoint);
            Point s2B = ImageToScreenCoords(vm.MprLines[1].EndPoint);

            Point? inter = GetLineIntersection(s1A, s1B, s2A, s2B);

            // A. Check rotation handles on all MPR lines first (near line endpoints/handles)
            for (int i = 0; i < vm.MprLines.Count; i++)
            {
                var mLine = vm.MprLines[i];
                var (h1, h2) = GetZoomAdaptiveHandles(mLine);

                if ((mousePos - h1).Length <= 12.0 || (mousePos - h2).Length <= 12.0)
                {
                    _mprDragTarget = MprDragTarget.LineRotate;
                    _draggedMprLine = mLine;
                    Point centerScr = inter ?? ImageToScreenCoords(mLine.CenterPoint);
                    _mprRotateCenterScreen = centerScr;
                    _mprRotateInitialAngle = Math.Atan2(mousePos.Y - centerScr.Y, mousePos.X - centerScr.X);

                    Point pivotImg = ScreenToImageCoords(centerScr);
                    _mprPivot3D = SynchronizationService.PatientPointFromPixel(vm.CurrentSlice, pivotImg.X, pivotImg.Y);

                    _isDragging = true;
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            // B. Check intersection center dragging (translates both planes)
            if (inter != null && (mousePos - inter.Value).Length <= 14)
            {
                _mprDragTarget = MprDragTarget.Intersection;
                _isDragging = true;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            // C. Check line body dragging (translates single plane)
            double dist1 = DistanceToSegment(mousePos, s1A, s1B);
            double dist2 = DistanceToSegment(mousePos, s2A, s2B);

            if (dist1 <= 8.0 && dist1 <= dist2)
            {
                _mprDragTarget = MprDragTarget.LineTranslate;
                _draggedMprLine = vm.MprLines[0];
                _isDragging = true;
                CaptureMouse();
                e.Handled = true;
                return;
            }
            else if (dist2 <= 8.0)
            {
                _mprDragTarget = MprDragTarget.LineTranslate;
                _draggedMprLine = vm.MprLines[1];
                _isDragging = true;
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // 1. 3D Cursor Spatial Reference Tool (Q)
        bool isCursor3DTrigger = (e.ChangedButton == MouseButton.Left && (mainVm.CurrentTool == MouseToolAction.Cursor3D || mainVm.LeftTool == MouseToolAction.Cursor3D))
                              || (e.ChangedButton == MouseButton.Right && mainVm.RightTool == MouseToolAction.Cursor3D)
                              || (e.ChangedButton == MouseButton.Middle && mainVm.MiddleTool == MouseToolAction.Cursor3D);
        if (isCursor3DTrigger)
        {
            _isDragging = true;
            CaptureMouse();
            _currentDragTrigger = e.ChangedButton switch
            {
                MouseButton.Left => MouseInputTrigger.LeftDrag,
                MouseButton.Right => MouseInputTrigger.RightDrag,
                MouseButton.Middle => MouseInputTrigger.MiddleDrag,
                _ => MouseInputTrigger.LeftDrag
            };
            if (vm.CurrentSlice != null)
            {
                var pt3d = SynchronizationService.PatientPointFromPixel(vm.CurrentSlice, imgPt.X, imgPt.Y);
                if (pt3d != null)
                {
                    mainVm.Update3DCursor(pt3d.Value.x, pt3d.Value.y, pt3d.Value.z, vm);
                }
            }
            e.Handled = true;
            return;
        }

        // 2. Angle and Insall-Salvati multi-step placement (Click 1: Pt 0, Click 2: Pt 1, Click 3: Pt 2)
        if (e.ChangedButton == MouseButton.Left && 
            (mainVm.CurrentTool is MouseToolAction.MeasureAngle or MouseToolAction.MeasureInsallSalvati) && 
            _angleCreationStep > 0 && _activeMeasurement != null)
        {
            if (_angleCreationStep == 1)
            {
                _activeMeasurement.Points[1] = imgPt;
                _activeMeasurement.Points[2] = imgPt;
                _angleCreationStep = 2;
                if (mainVm.CurrentTool == MouseToolAction.MeasureInsallSalvati)
                {
                    mainVm.StatusMessage = "Insall-Salvati: Click 3 for superior patellar pole (base)...";
                }
                RedrawMeasurements();
                e.Handled = true;
                return;
            }
            else if (_angleCreationStep == 2)
            {
                _activeMeasurement.Points[2] = imgPt;
                if (_activeMeasurement.Type == MeasurementType.InsallSalvati)
                {
                    double spX = vm.CurrentSlice?.PixelSpacingX ?? 1.0;
                    double spY = vm.CurrentSlice?.PixelSpacingY ?? 1.0;
                    var (ratio, tl, pl) = _activeMeasurement.CalculateInsallSalvati(spX, spY);
                    _activeMeasurement.Details = MeasurementItem.FormatInsallSalvatiDetails(ratio, tl, pl);
                    mainVm.StatusMessage = $"Insall-Salvati complete: {MeasurementItem.FormatInsallSalvatiDetails(ratio, tl, pl)}";
                }
                else
                {
                    double deg = _activeMeasurement.CalculateAngleDegrees();
                    _activeMeasurement.Details = $"{deg:0.0}°";
                }
                _activeMeasurement.IsSelected = true;
                vm.Measurements.Add(_activeMeasurement);
                _selectedMeasurement = _activeMeasurement;
                _activeMeasurement = null;
                _angleCreationStep = 0;
                RedrawMeasurements();
                e.Handled = true;
                return;
            }
        }

        // 2.6. Interactive Selection and Dragging of Measurement Text Badge (moving text table independently of ROI shape)
        if (e.ChangedButton == MouseButton.Left)
        {
            var textHit = FindMeasurementTextAt(_lastMousePos);
            if (textHit != null)
            {
                foreach (var m in vm.Measurements) m.IsSelected = false;
                textHit.IsSelected = true;
                _selectedMeasurement = textHit;
                mainVm.NotifyMeasurementSelected(textHit);

                _isDraggingMeasurementText = true;
                _dragTextStartMouse = _lastMousePos;
                _dragTextStartOffset = textHit.TextOffset ?? new Point(0, 0);
                _isDragging = true;
                CaptureMouse();
                RedrawMeasurements();
                e.Handled = true;
                return;
            }
        }

        // Determine button action
        MouseToolAction buttonAction = e.ChangedButton switch
        {
            MouseButton.Left => mainVm.LeftTool,
            MouseButton.Right => mainVm.RightTool,
            MouseButton.Middle => mainVm.MiddleTool,
            MouseButton.XButton1 => mainVm.MouseConfig.GetActionFor(MouseInputTrigger.XButton1Drag),
            MouseButton.XButton2 => mainVm.MouseConfig.GetActionFor(MouseInputTrigger.XButton2Drag),
            _ => MouseToolAction.None
        };

        // 3. Interactive Handle Resizing on currently selected measurement (only when NOT holding an active drawing tool)
        if (e.ChangedButton == MouseButton.Left && _selectedMeasurement != null && !IsMeasurementTool(buttonAction) && buttonAction != MouseToolAction.VertebraLabel)
        {
            int handleIndex = HitTestHandles(_selectedMeasurement, _lastMousePos);
            if (handleIndex >= 0)
            {
                _isResizingHandle = true;
                _activeHandleIndex = handleIndex;
                _origMeasurementPoints = new List<Point>(_selectedMeasurement.Points);
                _isDragging = true;
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // 4. Interactive Selection & Moving of existing measurements (when not holding an active drawing tool)
        if (e.ChangedButton == MouseButton.Left)
        {
            bool isDrawingTool = IsMeasurementTool(buttonAction) || buttonAction == MouseToolAction.VertebraLabel;
            var hit = !isDrawingTool ? FindMeasurementAt(_lastMousePos) : null;
            if (hit != null)
            {
                foreach (var m in vm.Measurements) m.IsSelected = false;
                hit.IsSelected = true;
                _selectedMeasurement = hit;
                mainVm.NotifyMeasurementSelected(hit);
                _isMovingMeasurement = true;
                _dragMeasurementStartImgPt = imgPt;
                _origMeasurementPoints = new List<Point>(hit.Points);
                _isDragging = true;
                CaptureMouse();
                RedrawMeasurements();
                e.Handled = true;
                return;
            }
            else
            {
                // If clicked empty space (and not near any vertebra label), deselect existing
                var onSliceVertebra = vm.VertebraLabels.Where(v => v.SliceIndex == vm.CurrentSliceIndex).ToList();
                bool nearVertebra = onSliceVertebra.Any(v =>
                {
                    Point sCenter = ImageToScreenCoords(v.ImagePoint);
                    Point sBadge = new Point(sCenter.X + 22, sCenter.Y - 11);
                    return (Math.Abs(sCenter.X - _lastMousePos.X) <= 35 && Math.Abs(sCenter.Y - _lastMousePos.Y) <= 35) ||
                           (Math.Abs(sBadge.X - _lastMousePos.X) <= 45 && Math.Abs(sBadge.Y - _lastMousePos.Y) <= 25);
                });

                if (!nearVertebra)
                {
                    bool hadSelection = vm.Measurements.Any(m => m.IsSelected) || vm.VertebraLabels.Any(v => v.IsSelected);
                    foreach (var m in vm.Measurements) m.IsSelected = false;
                    foreach (var v in vm.VertebraLabels) v.IsSelected = false;
                    _selectedMeasurement = null;
                    _draggedVertebra = null;
                    if (hadSelection) RedrawMeasurements();
                }
            }
        }

        // 5. Drawing new measurement (if button's assigned action is a measurement tool)

        if (buttonAction == MouseToolAction.VertebraLabel)
        {
            var onSlice = vm.VertebraLabels.Where(v => v.SliceIndex == vm.CurrentSliceIndex).ToList();
            VertebraLabelItem? hit = null;
            foreach (var v in onSlice)
            {
                Point screen = ImageToScreenCoords(v.ImagePoint);
                if (Math.Abs(screen.X - _lastMousePos.X) <= 35 && Math.Abs(screen.Y - _lastMousePos.Y) <= 35)
                {
                    hit = v;
                    break;
                }
            }

            if (hit != null)
            {
                foreach (var v in onSlice) v.IsSelected = false;
                hit.IsSelected = true;
                _draggedVertebra = hit;
                _isDraggingVertebra = true;
                _isDragging = true;
                _dragVertebraStartImgPt = imgPt;
                _dragVertebraOrigImgPt = hit.ImagePoint;
                _hasActuallyDraggedVertebra = false;
                CaptureMouse();
                RedrawMeasurements();
                e.Handled = true;
                return;
            }

            foreach (var v in onSlice) v.IsSelected = false;
            string nextLevel = DetermineNextVertebraLevel(vm, imgPt, onSlice);
            var newVertebra = new VertebraLabelItem
            {
                Level = nextLevel,
                ImagePoint = imgPt,
                SliceIndex = vm.CurrentSliceIndex,
                IsSelected = true
            };
            vm.VertebraLabels.Add(newVertebra);
            _draggedVertebra = newVertebra;
            _isDraggingVertebra = true;
            _isDragging = true;
            _dragVertebraStartImgPt = imgPt;
            _dragVertebraOrigImgPt = imgPt;
            _hasActuallyDraggedVertebra = false;
            CaptureMouse();
            RedrawMeasurements();
            e.Handled = true;
            return;
        }

        if (IsMeasurementTool(buttonAction))
        {
            foreach (var m in vm.Measurements) m.IsSelected = false;

            if (buttonAction is MouseToolAction.MeasureAngle or MouseToolAction.MeasureInsallSalvati)
            {
                var mType = buttonAction == MouseToolAction.MeasureInsallSalvati 
                    ? MeasurementType.InsallSalvati 
                    : MeasurementType.Angle;
                _activeMeasurement = new MeasurementItem
                {
                    Type = mType,
                    Points = new List<Point> { imgPt, imgPt, imgPt },
                    SliceIndex = vm.CurrentSliceIndex,
                    IsSelected = true
                };
                _angleCreationStep = 1;
                _selectedMeasurement = null;
                _isDragging = false;
                if (buttonAction == MouseToolAction.MeasureInsallSalvati)
                {
                    mainVm.StatusMessage = "Insall-Salvati: Click 2 for inferior patellar pole (apex)...";
                }
                RedrawMeasurements();
                e.Handled = true;
                return;
            }

            var newType = ToolToMeasurementType(buttonAction);
            var initPoints = new List<Point> { imgPt, imgPt };

            _activeMeasurement = new MeasurementItem
            {
                Type = newType,
                Points = initPoints,
                SliceIndex = vm.CurrentSliceIndex,
                IsSelected = true
            };
            _selectedMeasurement = _activeMeasurement;
            _isDragging = true;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // 6. Standard mouse actions (Browse, WL, Pan, Zoom)
        _isDragging = true;
        CaptureMouse();

        if (e.ChangedButton == MouseButton.Left)
            _currentDragTrigger = MouseInputTrigger.LeftDrag;
        else if (e.ChangedButton == MouseButton.Right)
            _currentDragTrigger = MouseInputTrigger.RightDrag;
        else if (e.ChangedButton == MouseButton.Middle)
            _currentDragTrigger = MouseInputTrigger.MiddleDrag;
        else if (e.ChangedButton == MouseButton.XButton1)
            _currentDragTrigger = MouseInputTrigger.XButton1Drag;
        else if (e.ChangedButton == MouseButton.XButton2)
            _currentDragTrigger = MouseInputTrigger.XButton2Drag;

        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (vm == null || mainVm == null) return;

        Point currentPos = e.GetPosition(this);
        Point currentImgPt = ScreenToImageCoords(currentPos);

        if (_isDraggingMeasurementText && _selectedMeasurement != null)
        {
            Vector deltaScreen = currentPos - _dragTextStartMouse;
            double zoom = Math.Max(0.01, vm.Zoom);
            double deltaImgX = deltaScreen.X / zoom;
            double deltaImgY = deltaScreen.Y / zoom;
            _selectedMeasurement.TextOffset = new Point(_dragTextStartOffset.X + deltaImgX, _dragTextStartOffset.Y + deltaImgY);
            RedrawMeasurements();
            e.Handled = true;
            return;
        }

        if (_isDraggingVertebra && _draggedVertebra != null)
        {
            double dx = currentImgPt.X - _dragVertebraStartImgPt.X;
            double dy = currentImgPt.Y - _dragVertebraStartImgPt.Y;
            if (Math.Abs(dx) > 1.0 || Math.Abs(dy) > 1.0)
            {
                _hasActuallyDraggedVertebra = true;
                _draggedVertebra.ImagePoint = new Point(_dragVertebraOrigImgPt.X + dx, _dragVertebraOrigImgPt.Y + dy);
                RedrawMeasurements();
            }
            e.Handled = true;
            return;
        }

        // 0. Angle creation in progress (mouse move tracks vertex or second arm)
        if (_angleCreationStep > 0 && _activeMeasurement != null)
        {
            while (_activeMeasurement.Points.Count < 3)
                _activeMeasurement.Points.Add(currentImgPt);

            if (_angleCreationStep == 1)
            {
                _activeMeasurement.Points[1] = currentImgPt;
                _activeMeasurement.Points[2] = currentImgPt;
                if (_activeMeasurement.Type == MeasurementType.InsallSalvati)
                {
                    double spX = vm.CurrentSlice?.PixelSpacingX ?? 1.0;
                    double spY = vm.CurrentSlice?.PixelSpacingY ?? 1.0;
                    double dx = (_activeMeasurement.Points[1].X - _activeMeasurement.Points[0].X) * spX;
                    double dy = (_activeMeasurement.Points[1].Y - _activeMeasurement.Points[0].Y) * spY;
                    double tl = Math.Sqrt(dx * dx + dy * dy);
                    _activeMeasurement.Details = $"TL: {tl:0.#} mm";
                }
            }
            else if (_angleCreationStep == 2)
            {
                _activeMeasurement.Points[2] = currentImgPt;
                if (_activeMeasurement.Type == MeasurementType.InsallSalvati)
                {
                    double spX = vm.CurrentSlice?.PixelSpacingX ?? 1.0;
                    double spY = vm.CurrentSlice?.PixelSpacingY ?? 1.0;
                    var (ratio, tl, pl) = _activeMeasurement.CalculateInsallSalvati(spX, spY);
                    _activeMeasurement.Details = MeasurementItem.FormatInsallSalvatiDetails(ratio, tl, pl);
                }
                else
                {
                    double deg = _activeMeasurement.CalculateAngleDegrees();
                    _activeMeasurement.Details = $"{deg:0.0}°";
                }
            }
            RedrawMeasurements();
            e.Handled = true;
            return;
        }

        // MPR Crosshair / Hover Cursor Updates while hovering (not dragging)
        if (!_isDragging)
        {
            UpdateHoverCursor(currentPos);
            return;
        }

        // MPR Crosshair / Reference Line Active Dragging & Rotation
        if (_mprDragTarget != MprDragTarget.None)
        {
            if (_mprDragTarget == MprDragTarget.LineRotate && _draggedMprLine != null)
            {
                // Calculate rotation delta around center point
                double curAngle = Math.Atan2(currentPos.Y - _mprRotateCenterScreen.Y, currentPos.X - _mprRotateCenterScreen.X);
                double deltaAngle = curAngle - _mprRotateInitialAngle;

                // Normalize deltaAngle to [-PI, PI]
                while (deltaAngle > Math.PI) deltaAngle -= 2 * Math.PI;
                while (deltaAngle < -Math.PI) deltaAngle += 2 * Math.PI;

                if (Math.Abs(deltaAngle) >= 0.005) // small threshold for smooth responsiveness (~0.3 deg)
                {
                    mainVm.RotateMprPlane(vm, _draggedMprLine.TargetPlane, deltaAngle, _mprPivot3D);
                    _mprRotateInitialAngle = curAngle;
                }
                e.Handled = true;
                return;
            }

            var currentSlice = vm.CurrentSlice;
            if (currentSlice != null)
            {
                double clampedX = Math.Clamp(currentImgPt.X, 0, currentSlice.Columns);
                double clampedY = Math.Clamp(currentImgPt.Y, 0, currentSlice.Rows);

                var pt3d = SynchronizationService.PatientPointFromPixel(currentSlice, clampedX, clampedY);
                if (pt3d != null)
                {
                    if (_mprDragTarget == MprDragTarget.Intersection)
                    {
                        var line1 = vm.MprLines[0];
                        var line2 = vm.MprLines[1];
                        mainVm.TranslateMprPlane(line1.TargetPlane, pt3d.Value.x, pt3d.Value.y, pt3d.Value.z);
                        mainVm.TranslateMprPlane(line2.TargetPlane, pt3d.Value.x, pt3d.Value.y, pt3d.Value.z);
                    }
                    else if (_mprDragTarget == MprDragTarget.LineTranslate && _draggedMprLine != null)
                    {
                        mainVm.TranslateMprPlane(_draggedMprLine.TargetPlane, pt3d.Value.x, pt3d.Value.y, pt3d.Value.z);
                    }
                }
            }
            e.Handled = true;
            return;
        }

        // 1. Resizing selected ROI/Measurement via Handle
        if (_isResizingHandle && _selectedMeasurement != null && _origMeasurementPoints.Count >= 2)
        {
            if (_selectedMeasurement.Type is MeasurementType.Ellipse or MeasurementType.Rectangle)
            {
                double x0 = Math.Min(_origMeasurementPoints[0].X, _origMeasurementPoints[1].X);
                double x1 = Math.Max(_origMeasurementPoints[0].X, _origMeasurementPoints[1].X);
                double y0 = Math.Min(_origMeasurementPoints[0].Y, _origMeasurementPoints[1].Y);
                double y1 = Math.Max(_origMeasurementPoints[0].Y, _origMeasurementPoints[1].Y);

                switch (_activeHandleIndex)
                {
                    case 0: x0 = currentImgPt.X; y0 = currentImgPt.Y; break; // TopLeft
                    case 1: x1 = currentImgPt.X; y0 = currentImgPt.Y; break; // TopRight
                    case 2: x1 = currentImgPt.X; y1 = currentImgPt.Y; break; // BottomRight
                    case 3: x0 = currentImgPt.X; y1 = currentImgPt.Y; break; // BottomLeft
                }

                _selectedMeasurement.Points[0] = new Point(Math.Min(x0, x1), Math.Min(y0, y1));
                _selectedMeasurement.Points[1] = new Point(Math.Max(x0, x1), Math.Max(y0, y1));

                if (vm.CurrentSlice != null)
                {
                    if (_selectedMeasurement.Type == MeasurementType.Ellipse)
                        _selectedMeasurement.ComputeEllipseStatistics(vm.CurrentSlice);
                    else
                        _selectedMeasurement.ComputeRectangleStatistics(vm.CurrentSlice);
                }
            }
            else if (_selectedMeasurement.Type is MeasurementType.Length or MeasurementType.Arrow)
            {
                if (_activeHandleIndex >= 0 && _activeHandleIndex < _selectedMeasurement.Points.Count)
                {
                    _selectedMeasurement.Points[_activeHandleIndex] = currentImgPt;
                    if (vm.CurrentSlice != null && _selectedMeasurement.Type == MeasurementType.Length)
                    {
                        double lenMm = _selectedMeasurement.CalculateLengthMm(vm.CurrentSlice.PixelSpacingX, vm.CurrentSlice.PixelSpacingY);
                        _selectedMeasurement.Details = $"{lenMm:0.0} mm";
                    }
                }
            }
            else if (_selectedMeasurement.Type == MeasurementType.Angle)
            {
                if (_activeHandleIndex >= 0 && _activeHandleIndex < _selectedMeasurement.Points.Count)
                {
                    _selectedMeasurement.Points[_activeHandleIndex] = currentImgPt;
                    double deg = _selectedMeasurement.CalculateAngleDegrees();
                    _selectedMeasurement.Details = $"{deg:0.0}°";
                }
            }
            else if (_selectedMeasurement.Type == MeasurementType.InsallSalvati)
            {
                if (_activeHandleIndex >= 0 && _activeHandleIndex < _selectedMeasurement.Points.Count)
                {
                    _selectedMeasurement.Points[_activeHandleIndex] = currentImgPt;
                    double spX = vm.CurrentSlice?.PixelSpacingX ?? 1.0;
                    double spY = vm.CurrentSlice?.PixelSpacingY ?? 1.0;
                    var (ratio, tl, pl) = _selectedMeasurement.CalculateInsallSalvati(spX, spY);
                    _selectedMeasurement.Details = MeasurementItem.FormatInsallSalvatiDetails(ratio, tl, pl);
                }
            }

            RedrawMeasurements();
            e.Handled = true;
            return;
        }

        // 2. Moving selected ROI/Measurement body
        if (_isMovingMeasurement && _selectedMeasurement != null)
        {
            double dx = currentImgPt.X - _dragMeasurementStartImgPt.X;
            double dy = currentImgPt.Y - _dragMeasurementStartImgPt.Y;
            for (int i = 0; i < _selectedMeasurement.Points.Count && i < _origMeasurementPoints.Count; i++)
            {
                _selectedMeasurement.Points[i] = new Point(_origMeasurementPoints[i].X + dx, _origMeasurementPoints[i].Y + dy);
            }

            if (vm.CurrentSlice != null)
            {
                if (_selectedMeasurement.Type == MeasurementType.Ellipse)
                    _selectedMeasurement.ComputeEllipseStatistics(vm.CurrentSlice);
                else if (_selectedMeasurement.Type == MeasurementType.Rectangle)
                    _selectedMeasurement.ComputeRectangleStatistics(vm.CurrentSlice);
            }

            RedrawMeasurements();
            e.Handled = true;
            return;
        }

        // 3. 3D Cursor dragging (Q)
        bool isCursor3DDragging = _isDragging && (_currentDragTrigger switch
        {
            MouseInputTrigger.LeftDrag => mainVm.LeftTool == MouseToolAction.Cursor3D,
            MouseInputTrigger.RightDrag => mainVm.RightTool == MouseToolAction.Cursor3D,
            MouseInputTrigger.MiddleDrag => mainVm.MiddleTool == MouseToolAction.Cursor3D,
            _ => false
        } || (mainVm.CurrentTool == MouseToolAction.Cursor3D && e.LeftButton == MouseButtonState.Pressed));

        if (isCursor3DDragging)
        {
            if (vm.CurrentSlice != null)
            {
                var pt3d = SynchronizationService.PatientPointFromPixel(vm.CurrentSlice, currentImgPt.X, currentImgPt.Y);
                if (pt3d != null)
                {
                    mainVm.Update3DCursor(pt3d.Value.x, pt3d.Value.y, pt3d.Value.z, vm);
                }
            }
            e.Handled = true;
            return;
        }

        // 4. Drawing new active measurement (drag based)
        if (_activeMeasurement != null)
        {
            if (_activeMeasurement.Type == MeasurementType.Pencil)
            {
                _activeMeasurement.Points.Add(currentImgPt);
            }
            else
            {
                if (_activeMeasurement.Points.Count >= 2)
                {
                    _activeMeasurement.Points[1] = currentImgPt;
                }
            }

            if (vm.CurrentSlice != null)
            {
                if (_activeMeasurement.Type == MeasurementType.Ellipse)
                    _activeMeasurement.ComputeEllipseStatistics(vm.CurrentSlice);
                else if (_activeMeasurement.Type == MeasurementType.Rectangle)
                    _activeMeasurement.ComputeRectangleStatistics(vm.CurrentSlice);
                else if (_activeMeasurement.Type == MeasurementType.AiCrop)
                    _activeMeasurement.Details = "[AI Crop]";
                else if (_activeMeasurement.Type == MeasurementType.Length)
                {
                    double lenMm = _activeMeasurement.CalculateLengthMm(vm.CurrentSlice.PixelSpacingX, vm.CurrentSlice.PixelSpacingY);
                    _activeMeasurement.Details = $"{lenMm:0.0} mm";
                }
            }

            RedrawMeasurements();
            e.Handled = true;
            return;
        }

        // 5. Mouse navigation / drag actions
        double deltaX = currentPos.X - _lastMousePos.X;
        double deltaY = currentPos.Y - _lastMousePos.Y;
        _lastMousePos = currentPos;

        if (Math.Abs(currentPos.X - _dragStartPos.X) > 4 || Math.Abs(currentPos.Y - _dragStartPos.Y) > 4)
        {
            _hasActuallyDragged = true;
        }

        MouseToolAction action;
        if (_currentDragTrigger == MouseInputTrigger.LeftDrag)
            action = mainVm.LeftTool;
        else if (_currentDragTrigger == MouseInputTrigger.RightDrag)
            action = mainVm.RightTool;
        else if (_currentDragTrigger == MouseInputTrigger.MiddleDrag)
            action = mainVm.MiddleTool;
        else
            action = mainVm.MouseConfig.GetActionFor(_currentDragTrigger);

        ExecuteMouseAction(action, deltaX, deltaY, vm);
        RedrawMeasurements();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_mprDragTarget != MprDragTarget.None)
            {
                _mprDragTarget = MprDragTarget.None;
                _draggedMprLine = null;
                _isDragging = false;
                ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            if (_isDraggingVertebra)
            {
                _isDraggingVertebra = false;
                _draggedVertebra = null;
                _isDragging = false;
                ReleaseMouseCapture();
                RedrawMeasurements();
                e.Handled = true;
                return;
            }

            if (_isDragging)
            {
                _isDragging = false;
                ReleaseMouseCapture();

                if (_isDraggingMeasurementText)
                {
                    _isDraggingMeasurementText = false;
                    RedrawMeasurements();
                    e.Handled = true;
                    return;
                }

                if (_isResizingHandle)
                {
                    _isResizingHandle = false;
                    _activeHandleIndex = -1;
                    RedrawMeasurements();
                    e.Handled = true;
                    return;
                }

                if (_isMovingMeasurement)
                {
                    _isMovingMeasurement = false;
                    RedrawMeasurements();
                    e.Handled = true;
                    return;
                }

                if (_activeMeasurement != null)
                {
                    if (_activeMeasurement.Type == MeasurementType.AiCrop)
                    {
                        var finishedAi = _activeMeasurement;
                        _activeMeasurement = null;
                        RedrawMeasurements();
                        e.Handled = true;

                        if (finishedAi.Points.Count >= 2)
                        {
                            Point p1 = finishedAi.Points[0];
                            Point p2 = finishedAi.Points[1];
                            double minX = Math.Min(p1.X, p2.X);
                            double minY = Math.Min(p1.Y, p2.Y);
                            double w = Math.Abs(p2.X - p1.X);
                            double h = Math.Abs(p2.Y - p1.Y);

                            if (w >= 4 && h >= 4)
                            {
                                TriggerAiConsultation(new Rect(minX, minY, w, h));
                            }
                        }
                        return;
                    }

                    if (_activeMeasurement.Type != MeasurementType.Angle && _activeMeasurement.Type != MeasurementType.InsallSalvati)
                    {
                        var vm = ViewModel;
                        if (vm?.CurrentSlice != null)
                        {
                            bool isValidMeasurement = false;
                            if (_activeMeasurement.Type == MeasurementType.Ellipse)
                            {
                                if (_activeMeasurement.Points.Count >= 2)
                                {
                                    double dx = Math.Abs(_activeMeasurement.Points[1].X - _activeMeasurement.Points[0].X);
                                    double dy = Math.Abs(_activeMeasurement.Points[1].Y - _activeMeasurement.Points[0].Y);
                                    if (dx >= 3 && dy >= 3)
                                    {
                                        _activeMeasurement.ComputeEllipseStatistics(vm.CurrentSlice);
                                        isValidMeasurement = true;
                                    }
                                }
                            }
                            else if (_activeMeasurement.Type == MeasurementType.Rectangle)
                            {
                                if (_activeMeasurement.Points.Count >= 2)
                                {
                                    double dx = Math.Abs(_activeMeasurement.Points[1].X - _activeMeasurement.Points[0].X);
                                    double dy = Math.Abs(_activeMeasurement.Points[1].Y - _activeMeasurement.Points[0].Y);
                                    if (dx >= 3 && dy >= 3)
                                    {
                                        _activeMeasurement.ComputeRectangleStatistics(vm.CurrentSlice);
                                        isValidMeasurement = true;
                                    }
                                }
                            }
                            else if (_activeMeasurement.Type == MeasurementType.Length)
                            {
                                double lenMm = _activeMeasurement.CalculateLengthMm(vm.CurrentSlice.PixelSpacingX, vm.CurrentSlice.PixelSpacingY);
                                // Require at least 1.0 mm to avoid accidental zero-length click micro-artifacts
                                if (lenMm >= 1.0)
                                {
                                    _activeMeasurement.Details = $"{lenMm:0.0} mm";
                                    isValidMeasurement = true;
                                }
                            }
                            else if (_activeMeasurement.Type == MeasurementType.Arrow)
                            {
                                if (_activeMeasurement.Points.Count >= 2)
                                {
                                    double dist = (_activeMeasurement.Points[1] - _activeMeasurement.Points[0]).Length;
                                    if (dist >= 4.0) isValidMeasurement = true;
                                }
                            }
                            else if (_activeMeasurement.Type == MeasurementType.Pencil)
                            {
                                if (_activeMeasurement.Points.Count >= 3) isValidMeasurement = true;
                            }

                            if (isValidMeasurement)
                            {
                                _activeMeasurement.IsSelected = true;
                                vm.Measurements.Add(_activeMeasurement);
                                _selectedMeasurement = _activeMeasurement;
                                GetMainViewModel()?.NotifyMeasurementSelected(_activeMeasurement);
                            }
                        }
                        _activeMeasurement = null;
                        RedrawMeasurements();
                        e.Handled = true;
                        return;
                    }
                }

                var mainVm = GetMainViewModel();
                var currentVm = ViewModel;
                if (mainVm != null)
                {
                    mainVm.Clear3DCursor();
                    if (currentVm != null && !_hasActuallyDragged)
                    {
                        if (e.ChangedButton == MouseButton.XButton1)
                        {
                            var clickAction = mainVm.MouseConfig.GetActionFor(MouseInputTrigger.XButton1Click);
                            ExecuteMouseAction(clickAction, 0, 0, currentVm);
                        }
                        else if (e.ChangedButton == MouseButton.XButton2)
                        {
                            var clickAction = mainVm.MouseConfig.GetActionFor(MouseInputTrigger.XButton2Click);
                            ExecuteMouseAction(clickAction, 0, 0, currentVm);
                        }
                    }
                }
            }
            e.Handled = true;
        }
        finally
        {
            UpdateHoverCursor(e.GetPosition(this));
        }
    }

    private void UpdateHoverCursor(Point currentPos)
    {
        if (_isDragging) return;

        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (vm == null || mainVm == null)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        // MPR Crosshair Cursor Updates while hovering (not dragging)
        if (vm.IsMprViewport && vm.MprLines.Count >= 2 && (mainVm?.ShowMprAxes ?? true))
        {
            Point s1A = ImageToScreenCoords(vm.MprLines[0].StartPoint);
            Point s1B = ImageToScreenCoords(vm.MprLines[0].EndPoint);
            Point s2A = ImageToScreenCoords(vm.MprLines[1].StartPoint);
            Point s2B = ImageToScreenCoords(vm.MprLines[1].EndPoint);

            for (int i = 0; i < vm.MprLines.Count; i++)
            {
                var mLine = vm.MprLines[i];
                var (h1, h2) = GetZoomAdaptiveHandles(mLine);

                if ((currentPos - h1).Length <= 12.0 || (currentPos - h2).Length <= 12.0)
                {
                    Cursor = Cursors.Hand;
                    return;
                }
            }

            Point? inter = GetLineIntersection(s1A, s1B, s2A, s2B);
            if (inter != null && (currentPos - inter.Value).Length <= 14)
            {
                Cursor = Cursors.SizeAll;
                return;
            }

            double dist1 = DistanceToSegment(currentPos, s1A, s1B);
            double dist2 = DistanceToSegment(currentPos, s2A, s2B);

            if (dist1 <= 8.0)
            {
                bool isHoriz = Math.Abs(s1B.Y - s1A.Y) < Math.Abs(s1B.X - s1A.X);
                Cursor = isHoriz ? Cursors.SizeNS : Cursors.SizeWE;
                return;
            }
            else if (dist2 <= 8.0)
            {
                bool isHoriz = Math.Abs(s2B.Y - s2A.Y) < Math.Abs(s2B.X - s2A.X);
                Cursor = isHoriz ? Cursors.SizeNS : Cursors.SizeWE;
                return;
            }
        }

        if (FindMeasurementTextAt(currentPos) != null)
        {
            Cursor = Cursors.SizeAll;
            return;
        }

        if (_selectedMeasurement != null && !IsMeasurementTool(mainVm.CurrentTool) && HitTestHandles(_selectedMeasurement, currentPos) >= 0)
        {
            Cursor = Cursors.Cross;
            return;
        }

        Cursor = Cursors.Arrow;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _isDragging = false;
        _isDraggingMeasurementText = false;
        _isResizingHandle = false;
        _isMovingMeasurement = false;
        _isDraggingVertebra = false;
        _draggedVertebra = null;
        _mprDragTarget = MprDragTarget.None;
        _draggedMprLine = null;
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_isDragging)
        {
            Cursor = Cursors.Arrow;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (vm == null || mainVm == null) return;
        if (mainVm.ActiveViewport != vm)
        {
            mainVm.SetActiveViewport(vm);
        }

        var trigger = MouseInputTrigger.WheelScroll;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            trigger = MouseInputTrigger.CtrlWheelScroll;
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            trigger = MouseInputTrigger.ShiftWheelScroll;
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            trigger = MouseInputTrigger.AltWheelScroll;

        var action = mainVm.MouseConfig.GetActionFor(trigger);
        int steps = e.Delta > 0 ? 1 : -1;
        if (mainVm.InvertScrolling)
        {
            steps = -steps;
        }

        switch (action)
        {
            case MouseToolAction.BrowseSeries:
                if (steps > 0) vm.PreviousImage();
                else vm.NextImage();
                break;

            case MouseToolAction.Zoom:
                Point mousePos = e.GetPosition(this);
                double factor = steps > 0 ? 1.08 : 0.92;
                ApplyZoom(vm.Zoom * factor, mousePos);
                break;

            case MouseToolAction.WindowLevel:
                vm.AdjustWindowLevel(0, steps * 15);
                break;

            case MouseToolAction.NextImage:
                if (steps > 0) vm.NextImage();
                else vm.PreviousImage();
                break;

            case MouseToolAction.PreviousImage:
                if (steps > 0) vm.PreviousImage();
                else vm.NextImage();
                break;
        }

        e.Handled = true;
    }

    private void ExecuteMouseAction(MouseToolAction action, double deltaX, double deltaY, ViewportViewModel vm)
    {
        switch (action)
        {
            case MouseToolAction.WindowLevel:
                vm.AdjustWindowLevel(deltaX * 2.0, -deltaY * 2.0);
                break;

            case MouseToolAction.Zoom:
                double zoomDelta = 1.0 - (deltaY * 0.005);
                Point dragAnchor = (_dragStartPos.X >= 0 && _dragStartPos.X <= ActualWidth && _dragStartPos.Y >= 0 && _dragStartPos.Y <= ActualHeight)
                    ? _dragStartPos
                    : new Point(ActualWidth / 2.0, ActualHeight / 2.0);
                ApplyZoom(vm.Zoom * zoomDelta, dragAnchor);
                break;

            case MouseToolAction.Pan:
                vm.PanX += deltaX;
                vm.PanY += deltaY;
                RedrawMeasurements();
                break;

            case MouseToolAction.BrowseSeries:
                _browseAccumulatorY += deltaY;
                double pixelsPerSlice = GetMainViewModel()?.BrowseSensitivityPixels ?? 6.0;
                if (pixelsPerSlice <= 0) pixelsPerSlice = 6.0;
                if (Math.Abs(_browseAccumulatorY) >= pixelsPerSlice)
                {
                    int steps = (int)(_browseAccumulatorY / pixelsPerSlice);
                    _browseAccumulatorY -= steps * pixelsPerSlice;
                    vm.StepImages(-steps);
                    RedrawMeasurements();
                }
                break;

            case MouseToolAction.NextImage:
                vm.NextImage();
                RedrawMeasurements();
                break;

            case MouseToolAction.PreviousImage:
                vm.PreviousImage();
                RedrawMeasurements();
                break;

            case MouseToolAction.ResetView:
                vm.ResetTransform();
                RedrawMeasurements();
                break;
        }
    }
    #endregion

    #region Measurements Hit Testing & Redraw
    private MeasurementItem? FindMeasurementAt(Point screenPt)
    {
        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (vm == null || !vm.ShowAnnotations) return null;

        for (int i = vm.Measurements.Count - 1; i >= 0; i--)
        {
            var m = vm.Measurements[i];
            if (mainVm?.MeasurementsCurrentSliceOnly == true && m.SliceIndex != vm.CurrentSliceIndex) continue;
            if (m.Points.Count < 2) continue;

            if (m.Type == MeasurementType.Ellipse)
            {
                Point s0 = ImageToScreenCoords(m.Points[0]);
                Point s1 = ImageToScreenCoords(m.Points[1]);
                double cx = (s0.X + s1.X) / 2.0;
                double cy = (s0.Y + s1.Y) / 2.0;
                double rx = Math.Max(8.0, Math.Abs(s1.X - s0.X) / 2.0);
                double ry = Math.Max(8.0, Math.Abs(s1.Y - s0.Y) / 2.0);

                double dx = (screenPt.X - cx) / rx;
                double dy = (screenPt.Y - cy) / ry;
                if (dx * dx + dy * dy <= 1.3)
                    return m;
            }
            else if (m.Type == MeasurementType.Rectangle)
            {
                Point s0 = ImageToScreenCoords(m.Points[0]);
                Point s1 = ImageToScreenCoords(m.Points[1]);
                double minX = Math.Min(s0.X, s1.X) - 6;
                double maxX = Math.Max(s0.X, s1.X) + 6;
                double minY = Math.Min(s0.Y, s1.Y) - 6;
                double maxY = Math.Max(s0.Y, s1.Y) + 6;

                if (screenPt.X >= minX && screenPt.X <= maxX && screenPt.Y >= minY && screenPt.Y <= maxY)
                    return m;
            }
            else if (m.Type is MeasurementType.Length or MeasurementType.Arrow)
            {
                Point s0 = ImageToScreenCoords(m.Points[0]);
                Point s1 = ImageToScreenCoords(m.Points[1]);
                if (DistanceToSegment(screenPt, s0, s1) <= 10.0)
                    return m;
            }
            else if ((m.Type == MeasurementType.Angle || m.Type == MeasurementType.InsallSalvati) && m.Points.Count >= 3)
            {
                Point s0 = ImageToScreenCoords(m.Points[0]);
                Point s1 = ImageToScreenCoords(m.Points[1]);
                Point s2 = ImageToScreenCoords(m.Points[2]);
                if (DistanceToSegment(screenPt, s1, s0) <= 10.0 || DistanceToSegment(screenPt, s1, s2) <= 10.0)
                    return m;
            }
            else if (m.Type == MeasurementType.Pencil && m.Points.Count >= 2)
            {
                for (int p = 0; p < m.Points.Count - 1; p++)
                {
                    Point sa = ImageToScreenCoords(m.Points[p]);
                    Point sb = ImageToScreenCoords(m.Points[p + 1]);
                    if (DistanceToSegment(screenPt, sa, sb) <= 10.0)
                        return m;
                }
            }
        }
        return null;
    }

    private MeasurementItem? FindMeasurementTextAt(Point screenPt)
    {
        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (vm == null || !vm.ShowAnnotations) return null;

        var allItems = new List<MeasurementItem>(vm.Measurements);
        if (_activeMeasurement != null) allItems.Add(_activeMeasurement);

        // 1. If currently selected measurement's text badge is under cursor, give it priority (crucial when multiple lines overlap)
        if (_selectedMeasurement != null && allItems.Contains(_selectedMeasurement))
        {
            if (mainVm?.MeasurementsCurrentSliceOnly != true || _selectedMeasurement == _activeMeasurement || _selectedMeasurement.SliceIndex == vm.CurrentSliceIndex)
            {
                if (!string.IsNullOrEmpty(_selectedMeasurement.Details) && !_selectedMeasurement.LastTextScreenRect.IsEmpty)
                {
                    var selRect = _selectedMeasurement.LastTextScreenRect;
                    selRect.Inflate(6, 6);
                    if (selRect.Contains(screenPt))
                        return _selectedMeasurement;
                }
            }
        }

        // 2. Iterate from topmost to bottom
        for (int i = allItems.Count - 1; i >= 0; i--)
        {
            var m = allItems[i];
            if (mainVm?.MeasurementsCurrentSliceOnly == true && m != _activeMeasurement && m.SliceIndex != vm.CurrentSliceIndex) continue;
            if (string.IsNullOrEmpty(m.Details) || m.LastTextScreenRect.IsEmpty) continue;

            // Expand hit area comfortably (6px margin) for seamless clicking and dragging
            var rect = m.LastTextScreenRect;
            rect.Inflate(6, 6);
            if (rect.Contains(screenPt))
                return m;
        }

        return null;
    }

    private static double DistanceToSegment(Point p, Point v, Point w)
    {
        double l2 = (w.X - v.X) * (w.X - v.X) + (w.Y - v.Y) * (w.Y - v.Y);
        if (l2 == 0) return (p - v).Length;
        double t = Math.Clamp(((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2, 0.0, 1.0);
        Point proj = new Point(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
        return (p - proj).Length;
    }

    private bool IsMeasurementTool(MouseToolAction tool) =>
        tool is MouseToolAction.MeasureLength or MouseToolAction.MeasureAngle or MouseToolAction.MeasureInsallSalvati or
                MouseToolAction.MeasureEllipse or MouseToolAction.MeasureRectangle or 
                MouseToolAction.Arrow or MouseToolAction.Pencil or MouseToolAction.AiLesionCrop;

    private MeasurementType ToolToMeasurementType(MouseToolAction tool) => tool switch
    {
        MouseToolAction.MeasureLength => MeasurementType.Length,
        MouseToolAction.MeasureAngle => MeasurementType.Angle,
        MouseToolAction.MeasureInsallSalvati => MeasurementType.InsallSalvati,
        MouseToolAction.MeasureEllipse => MeasurementType.Ellipse,
        MouseToolAction.MeasureRectangle => MeasurementType.Rectangle,
        MouseToolAction.AiLesionCrop => MeasurementType.AiCrop,
        MouseToolAction.Arrow => MeasurementType.Arrow,
        MouseToolAction.Pencil => MeasurementType.Pencil,
        _ => MeasurementType.Length
    };

    private int HitTestHandles(MeasurementItem m, Point screenPt)
    {
        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (mainVm?.MeasurementsCurrentSliceOnly == true && m != _activeMeasurement && m.SliceIndex != vm?.CurrentSliceIndex) return -1;
        if (m.Points.Count < 2) return -1;

        if (m.Type is MeasurementType.Ellipse or MeasurementType.Rectangle)
        {
            Point p1 = ImageToScreenCoords(m.Points[0]);
            Point p2 = ImageToScreenCoords(m.Points[1]);
            double left = Math.Min(p1.X, p2.X);
            double top = Math.Min(p1.Y, p2.Y);
            double right = Math.Max(p1.X, p2.X);
            double bottom = Math.Max(p1.Y, p2.Y);

            Point[] corners = {
                new Point(left, top),       // 0: TopLeft
                new Point(right, top),      // 1: TopRight
                new Point(right, bottom),   // 2: BottomRight
                new Point(left, bottom)     // 3: BottomLeft
            };

            for (int i = 0; i < 4; i++)
            {
                if ((screenPt - corners[i]).Length <= 10.0)
                    return i;
            }
        }
        else if (m.Type is MeasurementType.Length or MeasurementType.Arrow)
        {
            Point s0 = ImageToScreenCoords(m.Points[0]);
            Point s1 = ImageToScreenCoords(m.Points[1]);
            if ((screenPt - s0).Length <= 10.0) return 0;
            if ((screenPt - s1).Length <= 10.0) return 1;
        }
        else if ((m.Type == MeasurementType.Angle || m.Type == MeasurementType.InsallSalvati) && m.Points.Count >= 3)
        {
            Point s0 = ImageToScreenCoords(m.Points[0]);
            Point s1 = ImageToScreenCoords(m.Points[1]);
            Point s2 = ImageToScreenCoords(m.Points[2]);
            if ((screenPt - s0).Length <= 10.0) return 0;
            if ((screenPt - s1).Length <= 10.0) return 1;
            if ((screenPt - s2).Length <= 10.0) return 2;
        }
        return -1;
    }

    private void DrawHandleBox(Point p, Brush brush)
    {
        var handle = new Rectangle
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), // Semi-transparent subtle white handle
            Stroke = brush,
            StrokeThickness = 0.8
        };
        Canvas.SetLeft(handle, p.X - 2.5);
        Canvas.SetTop(handle, p.Y - 2.5);
        MeasurementCanvas.Children.Add(handle);
    }

    private void RenderRoiBadge(MeasurementItem m, Point p1, Point p2, Brush strokeBrush, Brush textBgBrush)
    {
        if (string.IsNullOrEmpty(m.Details))
        {
            m.LastTextScreenRect = Rect.Empty;
            return;
        }

        double left = Math.Min(p1.X, p2.X);
        double top = Math.Min(p1.Y, p2.Y);
        double width = Math.Abs(p2.X - p1.X);
        double height = Math.Abs(p2.Y - p1.Y);

        double defaultTextX = left + width + 8;
        double defaultTextY = top;

        var vm = ViewModel;
        double zoom = vm?.Zoom ?? 1.0;

        double textX = defaultTextX;
        double textY = defaultTextY;

        bool hasCustomOffset = false;
        if (m.TextOffset != null)
        {
            textX += m.TextOffset.Value.X * zoom;
            textY += m.TextOffset.Value.Y * zoom;
            if (Math.Abs(m.TextOffset.Value.X) > 4 || Math.Abs(m.TextOffset.Value.Y) > 4)
            {
                hasCustomOffset = true;
            }
        }

        var border = new Border
        {
            Background = textBgBrush,
            BorderBrush = strokeBrush,
            BorderThickness = new Thickness(m.IsSelected ? 1.5 : 1.0),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 4, 6, 4),
            Cursor = Cursors.SizeAll,
            Child = new TextBlock
            {
                Text = m.Details,
                Foreground = strokeBrush,
                FontSize = 10.5,
                FontWeight = m.IsSelected ? FontWeights.Bold : FontWeights.Normal,
                LineHeight = 14
            }
        };

        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double badgeW = Math.Max(border.DesiredSize.Width, 115);
        double badgeH = Math.Max(border.DesiredSize.Height, 44);

        m.LastTextScreenRect = new Rect(textX, textY, badgeW, badgeH);

        // If moved away from ROI, draw a subtle leader line connecting to the ROI center
        if (hasCustomOffset)
        {
            Point roiCenter = new Point(left + width / 2.0, top + height / 2.0);
            Point badgeCenter = new Point(textX + badgeW / 2.0, textY + badgeH / 2.0);

            var leaderLine = new Line
            {
                X1 = roiCenter.X,
                Y1 = roiCenter.Y,
                X2 = badgeCenter.X,
                Y2 = badgeCenter.Y,
                Stroke = strokeBrush,
                StrokeThickness = 1.0,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Opacity = 0.65
            };
            MeasurementCanvas.Children.Add(leaderLine);
        }

        Canvas.SetLeft(border, textX);
        Canvas.SetTop(border, textY);
        MeasurementCanvas.Children.Add(border);
    }

    private void RenderMeasurementTextBadge(
        MeasurementItem m,
        Point anchorScreenPt,
        double defaultOffsetX,
        double defaultOffsetY,
        Brush strokeBrush,
        Brush textBgBrush,
        bool isBold = false)
    {
        if (string.IsNullOrEmpty(m.Details))
        {
            m.LastTextScreenRect = Rect.Empty;
            return;
        }

        var vm = ViewModel;
        double zoom = vm?.Zoom ?? 1.0;
        double textX = anchorScreenPt.X + defaultOffsetX;
        double textY = anchorScreenPt.Y + defaultOffsetY;

        bool hasCustomOffset = false;
        if (m.TextOffset != null)
        {
            textX += m.TextOffset.Value.X * zoom;
            textY += m.TextOffset.Value.Y * zoom;
            if (Math.Abs(m.TextOffset.Value.X) > 4 || Math.Abs(m.TextOffset.Value.Y) > 4)
            {
                hasCustomOffset = true;
            }
        }

        var border = new Border
        {
            Background = textBgBrush,
            BorderBrush = strokeBrush,
            BorderThickness = new Thickness(m.IsSelected ? 1.2 : 0.8),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 2, 5, 2),
            Cursor = Cursors.SizeAll,
            Child = new TextBlock
            {
                Text = m.Details,
                Foreground = strokeBrush,
                FontSize = 11,
                FontWeight = (m.IsSelected || isBold) ? FontWeights.Bold : FontWeights.SemiBold
            }
        };

        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double minTextW = Math.Max(50.0, (m.Details?.Length ?? 5) * 8.0 + 16.0);
        double badgeW = Math.Max(border.DesiredSize.Width, minTextW);
        double badgeH = Math.Max(border.DesiredSize.Height, 22.0);
        m.LastTextScreenRect = new Rect(textX, textY, badgeW, badgeH);

        if (hasCustomOffset)
        {
            Point badgeCenter = new Point(textX + badgeW / 2.0, textY + badgeH / 2.0);
            var leaderLine = new Line
            {
                X1 = anchorScreenPt.X,
                Y1 = anchorScreenPt.Y,
                X2 = badgeCenter.X,
                Y2 = badgeCenter.Y,
                Stroke = strokeBrush,
                StrokeThickness = 0.9,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Opacity = 0.65
            };
            MeasurementCanvas.Children.Add(leaderLine);
        }

        Canvas.SetLeft(border, textX);
        Canvas.SetTop(border, textY);
        MeasurementCanvas.Children.Add(border);
    }

    public void RedrawMeasurements()
    {
        MeasurementCanvas.Children.Clear();
        InteractiveOverlayCanvas?.Children.Clear();
        var vm = ViewModel;
        if (vm == null || !vm.ShowAnnotations) return;

        // Draw Scout / Cross-reference lines (Thin #FFA500 orange line, standard 2D viewports only)
        if (!vm.IsMprViewport && vm.ShowScoutLine && vm.ScoutLineStart != null && vm.ScoutLineEnd != null)
        {
            Point sStart = ImageToScreenCoords(vm.ScoutLineStart.Value);
            Point sEnd = ImageToScreenCoords(vm.ScoutLineEnd.Value);

            var scoutLine = new Line
            {
                X1 = sStart.X, Y1 = sStart.Y,
                X2 = sEnd.X, Y2 = sEnd.Y,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 165, 0)), // #FFA500 Orange
                StrokeThickness = 1.0,
                SnapsToDevicePixels = true
            };
            MeasurementCanvas.Children.Add(scoutLine);
        }

        // Draw 3D MPR Crosshair Reference Lines and End Handles (if axes are visible)
        var mainVm = GetMainViewModel();
        if (vm.IsMprViewport && vm.MprLines.Count > 0 && (mainVm?.ShowMprAxes ?? true))
        {
            foreach (var mprLine in vm.MprLines)
            {
                Point s1 = ImageToScreenCoords(mprLine.StartPoint);
                Point s2 = ImageToScreenCoords(mprLine.EndPoint);

                var brush = new SolidColorBrush(mprLine.Color);
                brush.Freeze();

                var line = new Line
                {
                    X1 = s1.X, Y1 = s1.Y,
                    X2 = s2.X, Y2 = s2.Y,
                    Stroke = brush,
                    StrokeThickness = 1.3,
                    SnapsToDevicePixels = true
                };
                MeasurementCanvas.Children.Add(line);

                // Circular Handle Dots on both ends of the reference line (zoom-adaptive)
                var (sHandle1, sHandle2) = GetZoomAdaptiveHandles(mprLine);
                var handleDot1 = new Ellipse
                {
                    Width = 9,
                    Height = 9,
                    Fill = brush,
                    Stroke = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                    StrokeThickness = 1.4
                };
                Canvas.SetLeft(handleDot1, sHandle1.X - 4.5);
                Canvas.SetTop(handleDot1, sHandle1.Y - 4.5);
                MeasurementCanvas.Children.Add(handleDot1);

                var handleDot2 = new Ellipse
                {
                    Width = 9,
                    Height = 9,
                    Fill = brush,
                    Stroke = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                    StrokeThickness = 1.4
                };
                Canvas.SetLeft(handleDot2, sHandle2.X - 4.5);
                Canvas.SetTop(handleDot2, sHandle2.Y - 4.5);
                MeasurementCanvas.Children.Add(handleDot2);
            }
        }

        var yellowBrush = new SolidColorBrush(Color.FromRgb(255, 215, 0)); // Unselected is Yellow
        var selectedBrush = new SolidColorBrush(Color.FromRgb(0, 230, 118)); // Selected is Crisp Green (#00E676)
        var textBgBrush = new SolidColorBrush(Color.FromArgb(90, 10, 10, 14)); // Translucent background so image details underneath remain visible
        textBgBrush.Freeze();

        var allItems = new List<MeasurementItem>(vm.Measurements);
        if (_activeMeasurement != null) allItems.Add(_activeMeasurement);

        if (mainVm?.MeasurementsCurrentSliceOnly == true)
        {
            allItems = allItems.Where(m => m == _activeMeasurement || m.SliceIndex == vm.CurrentSliceIndex).ToList();
        }
        else if (vm.CurrentSlice != null)
        {
            foreach (var m in allItems)
            {
                if (m.Type == MeasurementType.Ellipse)
                    m.ComputeEllipseStatistics(vm.CurrentSlice);
                else if (m.Type == MeasurementType.Rectangle)
                    m.ComputeRectangleStatistics(vm.CurrentSlice);
            }
        }

        foreach (var m in allItems)
        {
            if (m.Points.Count < 2) continue;

            Brush strokeBrush = m.IsSelected ? selectedBrush : yellowBrush;
            double strokeThickness = m.IsSelected ? 1.2 : 1.0;

            if (m.Type == MeasurementType.Length)
            {
                Point p1 = ImageToScreenCoords(m.Points[0]);
                Point p2 = ImageToScreenCoords(m.Points[1]);

                var line = new Line
                {
                    X1 = p1.X, Y1 = p1.Y,
                    X2 = p2.X, Y2 = p2.Y,
                    Stroke = strokeBrush,
                    StrokeThickness = strokeThickness
                };
                MeasurementCanvas.Children.Add(line);

                // Small tick marks at endpoints
                DrawEndpointDot(p1, strokeBrush);
                DrawEndpointDot(p2, strokeBrush);

                if (!string.IsNullOrEmpty(m.Details))
                {
                    Point midPoint = new Point((p1.X + p2.X) / 2.0, (p1.Y + p2.Y) / 2.0);
                    RenderMeasurementTextBadge(m, midPoint, 6, 6, strokeBrush, textBgBrush);
                }
                else
                {
                    m.LastTextScreenRect = Rect.Empty;
                }

                if (m.IsSelected)
                {
                    DrawHandleBox(p1, selectedBrush);
                    DrawHandleBox(p2, selectedBrush);
                }
            }
            else if (m.Type == MeasurementType.Arrow)
            {
                Point p1 = ImageToScreenCoords(m.Points[0]);
                Point p2 = ImageToScreenCoords(m.Points[1]);

                // Shaft
                var line = new Line
                {
                    X1 = p1.X, Y1 = p1.Y,
                    X2 = p2.X, Y2 = p2.Y,
                    Stroke = strokeBrush,
                    StrokeThickness = strokeThickness
                };
                MeasurementCanvas.Children.Add(line);

                // Arrow head at p2
                Vector dir = p2 - p1;
                if (dir.Length > 2)
                {
                    dir.Normalize();
                    Vector normal = new Vector(-dir.Y, dir.X);
                    Point head1 = p2 - dir * 14 + normal * 6;
                    Point head2 = p2 - dir * 14 - normal * 6;

                    var headLine1 = new Line { X1 = p2.X, Y1 = p2.Y, X2 = head1.X, Y2 = head1.Y, Stroke = strokeBrush, StrokeThickness = strokeThickness };
                    var headLine2 = new Line { X1 = p2.X, Y1 = p2.Y, X2 = head2.X, Y2 = head2.Y, Stroke = strokeBrush, StrokeThickness = strokeThickness };
                    MeasurementCanvas.Children.Add(headLine1);
                    MeasurementCanvas.Children.Add(headLine2);
                }

                if (m.IsSelected)
                {
                    DrawHandleBox(p1, selectedBrush);
                    DrawHandleBox(p2, selectedBrush);
                }
            }
            else if (m.Type == MeasurementType.Angle && m.Points.Count >= 3)
            {
                Point p0 = ImageToScreenCoords(m.Points[0]);
                Point vertex = ImageToScreenCoords(m.Points[1]);
                Point p2 = ImageToScreenCoords(m.Points[2]);

                var arm1 = new Line { X1 = vertex.X, Y1 = vertex.Y, X2 = p0.X, Y2 = p0.Y, Stroke = strokeBrush, StrokeThickness = strokeThickness };
                var arm2 = new Line { X1 = vertex.X, Y1 = vertex.Y, X2 = p2.X, Y2 = p2.Y, Stroke = strokeBrush, StrokeThickness = strokeThickness };
                MeasurementCanvas.Children.Add(arm1);
                MeasurementCanvas.Children.Add(arm2);

                DrawEndpointDot(p0, strokeBrush);
                DrawEndpointDot(vertex, strokeBrush);
                DrawEndpointDot(p2, strokeBrush);

                if (!string.IsNullOrEmpty(m.Details))
                {
                    RenderMeasurementTextBadge(m, vertex, 8, 8, strokeBrush, textBgBrush);
                }
                else
                {
                    m.LastTextScreenRect = Rect.Empty;
                }

                if (m.IsSelected)
                {
                    DrawHandleBox(p0, selectedBrush);
                    DrawHandleBox(vertex, selectedBrush);
                    DrawHandleBox(p2, selectedBrush);
                }
            }
            else if (m.Type == MeasurementType.InsallSalvati && m.Points.Count >= 2)
            {
                Point p0 = ImageToScreenCoords(m.Points[0]);      // Tuberozitate tibie
                Point vertex = ImageToScreenCoords(m.Points[1]);  // Apex patelă (pol inferior)
                Point p2 = m.Points.Count >= 3 ? ImageToScreenCoords(m.Points[2]) : vertex; // Bază patelă (pol superior)

                var isBrush = m.IsSelected ? selectedBrush : new SolidColorBrush(Color.FromRgb(0, 230, 255));
                var arm1 = new Line { X1 = p0.X, Y1 = p0.Y, X2 = vertex.X, Y2 = vertex.Y, Stroke = isBrush, StrokeThickness = strokeThickness };
                MeasurementCanvas.Children.Add(arm1);

                DrawEndpointDot(p0, isBrush);
                DrawEndpointDot(vertex, isBrush);

                if (m.Points.Count >= 3)
                {
                    var arm2 = new Line { X1 = vertex.X, Y1 = vertex.Y, X2 = p2.X, Y2 = p2.Y, Stroke = isBrush, StrokeThickness = strokeThickness };
                    MeasurementCanvas.Children.Add(arm2);
                    DrawEndpointDot(p2, isBrush);
                }

                if (!string.IsNullOrEmpty(m.Details))
                {
                    RenderMeasurementTextBadge(m, vertex, 8, -14, isBrush, textBgBrush, isBold: true);
                }
                else
                {
                    m.LastTextScreenRect = Rect.Empty;
                }

                if (m.IsSelected)
                {
                    DrawHandleBox(p0, selectedBrush);
                    DrawHandleBox(vertex, selectedBrush);
                    if (m.Points.Count >= 3)
                    {
                        DrawHandleBox(p2, selectedBrush);
                    }
                }
            }
            else if (m.Type == MeasurementType.Pencil && m.Points.Count >= 2)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    Point start = ImageToScreenCoords(m.Points[0]);
                    ctx.BeginFigure(start, false, false);
                    for (int i = 1; i < m.Points.Count; i++)
                    {
                        ctx.LineTo(ImageToScreenCoords(m.Points[i]), true, false);
                    }
                }
                var path = new Path
                {
                    Data = geometry,
                    Stroke = strokeBrush,
                    StrokeThickness = strokeThickness
                };
                MeasurementCanvas.Children.Add(path);
            }
            else if (m.Type is MeasurementType.Rectangle or MeasurementType.AiCrop)
            {
                Point p1 = ImageToScreenCoords(m.Points[0]);
                Point p2 = ImageToScreenCoords(m.Points[1]);

                double left = Math.Min(p1.X, p2.X);
                double top = Math.Min(p1.Y, p2.Y);
                double width = Math.Abs(p2.X - p1.X);
                double height = Math.Abs(p2.Y - p1.Y);

                if (width > 2 && height > 2)
                {
                    bool isAi = m.Type == MeasurementType.AiCrop;
                    var stroke = isAi ? new SolidColorBrush(Color.FromRgb(0, 229, 255)) : strokeBrush;

                    var rect = new Rectangle
                    {
                        Width = width,
                        Height = height,
                        Stroke = stroke,
                        StrokeThickness = isAi ? 2.0 : strokeThickness,
                        StrokeDashArray = isAi ? new DoubleCollection { 3, 2 } : (m.IsSelected ? null : new DoubleCollection { 4, 2 })
                    };
                    Canvas.SetLeft(rect, left);
                    Canvas.SetTop(rect, top);
                    MeasurementCanvas.Children.Add(rect);

                    if (isAi)
                    {
                        var badge = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(220, 0, 131, 143)),
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(5, 2, 5, 2),
                            Child = new TextBlock
                            {
                                Text = "AI Crop",
                                FontSize = 10,
                                FontWeight = FontWeights.Bold,
                                Foreground = Brushes.White
                            }
                        };
                        Canvas.SetLeft(badge, left);
                        Canvas.SetTop(badge, Math.Max(0, top - 20));
                        MeasurementCanvas.Children.Add(badge);
                    }

                    if (m.IsSelected)
                    {
                        DrawHandleBox(new Point(left, top), selectedBrush);
                        DrawHandleBox(new Point(left + width, top), selectedBrush);
                        DrawHandleBox(new Point(left + width, top + height), selectedBrush);
                        DrawHandleBox(new Point(left, top + height), selectedBrush);
                    }

                    if (!string.IsNullOrEmpty(m.Details))
                    {
                        RenderRoiBadge(m, p1, p2, strokeBrush, textBgBrush);
                    }
                    else
                    {
                        m.LastTextScreenRect = Rect.Empty;
                    }
                }
            }
            else if (m.Type == MeasurementType.Ellipse)
            {
                Point p1 = ImageToScreenCoords(m.Points[0]);
                Point p2 = ImageToScreenCoords(m.Points[1]);

                double left = Math.Min(p1.X, p2.X);
                double top = Math.Min(p1.Y, p2.Y);
                double width = Math.Abs(p2.X - p1.X);
                double height = Math.Abs(p2.Y - p1.Y);

                if (width > 2 && height > 2)
                {
                    var ellipse = new Ellipse
                    {
                        Width = width,
                        Height = height,
                        Stroke = strokeBrush,
                        StrokeThickness = strokeThickness,
                        StrokeDashArray = m.IsSelected ? null : new DoubleCollection { 4, 2 }
                    };
                    Canvas.SetLeft(ellipse, left);
                    Canvas.SetTop(ellipse, top);
                    MeasurementCanvas.Children.Add(ellipse);

                    if (m.IsSelected)
                    {
                        DrawHandleBox(new Point(left, top), selectedBrush);
                        DrawHandleBox(new Point(left + width, top), selectedBrush);
                        DrawHandleBox(new Point(left + width, top + height), selectedBrush);
                        DrawHandleBox(new Point(left, top + height), selectedBrush);
                    }

                    if (!string.IsNullOrEmpty(m.Details))
                    {
                        RenderRoiBadge(m, p1, p2, strokeBrush, textBgBrush);
                    }
                    else
                    {
                        m.LastTextScreenRect = Rect.Empty;
                    }
                }
            }
        }

        // Draw 3D Crosshair spatial reference cursor if enabled
        if (vm.ShowCrosshair3D && vm.Crosshair3DPoint != null)
        {
            Point screenPos = ImageToScreenCoords(vm.Crosshair3DPoint.Value);
            double cx = screenPos.X;
            double cy = screenPos.Y;

            var crosshairBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255)); // Glowing Cyan
            double len = 30;
            double gap = 6;

            var lineH1 = new Line { X1 = cx - len, Y1 = cy, X2 = cx - gap, Y2 = cy, Stroke = crosshairBrush, StrokeThickness = 1.5 };
            var lineH2 = new Line { X1 = cx + gap, Y1 = cy, X2 = cx + len, Y2 = cy, Stroke = crosshairBrush, StrokeThickness = 1.5 };
            var lineV1 = new Line { X1 = cx, Y1 = cy - len, X2 = cx, Y2 = cy - gap, Stroke = crosshairBrush, StrokeThickness = 1.5 };
            var lineV2 = new Line { X1 = cx, Y1 = cy + gap, X2 = cx, Y2 = cy + len, Stroke = crosshairBrush, StrokeThickness = 1.5 };

            var centerDot = new Ellipse
            {
                Width = 8, Height = 8,
                Stroke = crosshairBrush, StrokeThickness = 1.5
            };
            Canvas.SetLeft(centerDot, cx - 4);
            Canvas.SetTop(centerDot, cy - 4);

            MeasurementCanvas.Children.Add(lineH1);
            MeasurementCanvas.Children.Add(lineH2);
            MeasurementCanvas.Children.Add(lineV1);
            MeasurementCanvas.Children.Add(lineV2);
            MeasurementCanvas.Children.Add(centerDot);
        }

        // Draw Spine Vertebra Labels on current slice
        var vertebraOnSlice = vm.VertebraLabels.Where(v => v.SliceIndex == vm.CurrentSliceIndex).ToList();
        foreach (var v in vertebraOnSlice)
        {
            Point sCenter = ImageToScreenCoords(v.ImagePoint);

            var dot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = v.IsSelected ? selectedBrush : new SolidColorBrush(Color.FromRgb(245, 175, 40)),
                Stroke = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                StrokeThickness = 1.2,
                Cursor = Cursors.SizeAll
            };
            dot.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var mainVm = GetMainViewModel();
                mainVm?.SetActiveViewport(vm);
                foreach (var item in vm.VertebraLabels) item.IsSelected = (item == v);
                _draggedVertebra = v;
                _isDraggingVertebra = true;
                _isDragging = true;
                _lastMousePos = e.GetPosition(this);
                _dragVertebraStartImgPt = ScreenToImageCoords(_lastMousePos);
                _dragVertebraOrigImgPt = v.ImagePoint;
                _hasActuallyDraggedVertebra = false;
                CaptureMouse();
                RedrawMeasurements();
                e.Handled = true;
            };
            Canvas.SetLeft(dot, sCenter.X - 4.5);
            Canvas.SetTop(dot, sCenter.Y - 4.5);
            InteractiveOverlayCanvas?.Children.Add(dot);

            Point sBadge = new Point(sCenter.X + 22, sCenter.Y - 11);
            var pointer = new Line
            {
                X1 = sCenter.X, Y1 = sCenter.Y,
                X2 = sBadge.X, Y2 = sBadge.Y + 11,
                Stroke = v.IsSelected ? selectedBrush : new SolidColorBrush(Color.FromRgb(245, 175, 40)),
                StrokeThickness = 1.0,
                StrokeDashArray = new DoubleCollection { 2, 2 }
            };
            MeasurementCanvas.Children.Add(pointer);

            var badgeBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 20, 20, 20)),
                BorderBrush = v.IsSelected ? selectedBrush : new SolidColorBrush(Color.FromRgb(245, 175, 40)),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 2, 5, 2),
                Cursor = Cursors.Hand
            };

            var badgeStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var tb = new TextBlock
            {
                Text = v.Level,
                Foreground = v.IsSelected ? selectedBrush : new SolidColorBrush(Color.FromRgb(245, 175, 40)),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Double-click to edit level (e.g. L3 -> L1)\nDrag to reposition"
            };
            badgeStack.Children.Add(tb);

            Border? btnUp = null;
            Border? btnDown = null;

            if (v.IsSelected)
            {
                btnUp = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(180, 0, 70, 90)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0, 230, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(6, 0, 2, 0),
                    Padding = new Thickness(4, 1, 4, 1),
                    Cursor = Cursors.Hand,
                    ToolTip = "Shift levels up/cranial by 1 (e.g. L2 -> L1)"
                };
                var btnUpText = new TextBlock
                {
                    Text = "▲",
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 255)),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    IsHitTestVisible = false,
                    VerticalAlignment = VerticalAlignment.Center
                };
                btnUp.Child = btnUpText;
                btnUp.MouseEnter += (s, e) =>
                {
                    btnUp.Background = new SolidColorBrush(Color.FromRgb(0, 131, 143));
                    btnUpText.Foreground = Brushes.White;
                };
                btnUp.MouseLeave += (s, e) =>
                {
                    btnUp.Background = new SolidColorBrush(Color.FromArgb(180, 0, 70, 90));
                    btnUpText.Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 255));
                };
                btnUp.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    ShiftAllVertebraLabels(vm, -1);
                    RedrawMeasurements();
                };
                badgeStack.Children.Add(btnUp);

                btnDown = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(180, 0, 70, 90)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0, 230, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(1, 0, 1, 0),
                    Padding = new Thickness(4, 1, 4, 1),
                    Cursor = Cursors.Hand,
                    ToolTip = "Shift levels down/caudal by 1 (e.g. L1 -> L2)"
                };
                var btnDownText = new TextBlock
                {
                    Text = "▼",
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 255)),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    IsHitTestVisible = false,
                    VerticalAlignment = VerticalAlignment.Center
                };
                btnDown.Child = btnDownText;
                btnDown.MouseEnter += (s, e) =>
                {
                    btnDown.Background = new SolidColorBrush(Color.FromRgb(0, 131, 143));
                    btnDownText.Foreground = Brushes.White;
                };
                btnDown.MouseLeave += (s, e) =>
                {
                    btnDown.Background = new SolidColorBrush(Color.FromArgb(180, 0, 70, 90));
                    btnDownText.Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 255));
                };
                btnDown.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    ShiftAllVertebraLabels(vm, 1);
                    RedrawMeasurements();
                };
                badgeStack.Children.Add(btnDown);
            }

            badgeBorder.PreviewMouseLeftButtonDown += (s, e) =>
            {
                // CRUCIAL: If user clicked on or inside the up/down shift buttons, ignore here completely!
                if (e.OriginalSource is DependencyObject dep)
                {
                    if ((btnUp != null && (dep == btnUp || IsVisualDescendantOf(dep, btnUp))) ||
                        (btnDown != null && (dep == btnDown || IsVisualDescendantOf(dep, btnDown))))
                    {
                        return;
                    }
                }

                if (e.ClickCount >= 2)
                {
                    e.Handled = true;
                    _isEditingVertebra = true;
                    string origLevel = v.Level;
                    var txtEdit = new TextBox
                    {
                        Text = origLevel,
                        Background = new SolidColorBrush(Color.FromRgb(25, 25, 25)),
                        Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                        CaretBrush = new SolidColorBrush(Color.FromRgb(245, 175, 40)),
                        FontWeight = FontWeights.Bold,
                        FontSize = 11,
                        BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                        Padding = new Thickness(2, 0, 2, 0),
                        MinWidth = 32
                    };

                    bool committed = false;
                    Action commitEdit = () =>
                    {
                        if (committed) return;
                        committed = true;
                        _isEditingVertebra = false;

                        string newLevel = VertebraLabelItem.ParseNormalizedLevel(txtEdit.Text, origLevel);
                        int oldIdx = VertebraLabelItem.GetLevelIndex(origLevel);
                        int newIdx = VertebraLabelItem.GetLevelIndex(newLevel);
                        int delta = newIdx - oldIdx;

                        if (delta != 0)
                        {
                            ShiftAllVertebraLabels(vm, delta);
                        }
                        else
                        {
                            v.Level = newLevel;
                        }
                        RedrawMeasurements();
                    };

                    txtEdit.KeyDown += (ts, te) =>
                    {
                        if (te.Key == Key.Enter)
                        {
                            commitEdit();
                            te.Handled = true;
                        }
                        else if (te.Key == Key.Escape)
                        {
                            committed = true;
                            _isEditingVertebra = false;
                            RedrawMeasurements();
                            te.Handled = true;
                        }
                    };

                    txtEdit.LostFocus += (ts, te) => commitEdit();

                    badgeBorder.Child = txtEdit;
                    txtEdit.Focus();
                    txtEdit.SelectAll();
                }
                else
                {
                    var mainVm = GetMainViewModel();
                    mainVm?.SetActiveViewport(vm);
                    foreach (var item in vm.VertebraLabels) item.IsSelected = (item == v);
                    _draggedVertebra = v;
                    _isDraggingVertebra = true;
                    _isDragging = true;
                    _lastMousePos = e.GetPosition(this);
                    _dragVertebraStartImgPt = ScreenToImageCoords(_lastMousePos);
                    _dragVertebraOrigImgPt = v.ImagePoint;
                    _hasActuallyDraggedVertebra = false;
                    CaptureMouse();
                    RedrawMeasurements();
                    e.Handled = true;
                }
            };

            badgeBorder.Child = badgeStack;
            Canvas.SetLeft(badgeBorder, sBadge.X);
            Canvas.SetTop(badgeBorder, sBadge.Y);
            InteractiveOverlayCanvas?.Children.Add(badgeBorder);
        }
    }

    private string DetermineNextVertebraLevel(ViewportViewModel vm, Point imgPt, List<VertebraLabelItem> onSlice)
    {
        if (onSlice.Count == 0)
        {
            var mainVm = GetMainViewModel();
            var segment = mainVm?.SelectedSpineSegment ?? SpineSegment.Lumbar;
            return segment switch
            {
                SpineSegment.Thoracic => "T12",
                SpineSegment.Cervical => "C7",
                _ => "L5"
            };
        }

        var sorted = onSlice.OrderBy(v => v.ImagePoint.Y).ToList();
        var highest = sorted.First();
        var lowest = sorted.Last();

        if (imgPt.Y < highest.ImagePoint.Y)
        {
            return VertebraLabelItem.ShiftLevel(highest.Level, -1);
        }
        else if (imgPt.Y > lowest.ImagePoint.Y)
        {
            return VertebraLabelItem.ShiftLevel(lowest.Level, 1);
        }
        else
        {
            var closest = sorted.OrderBy(v => Math.Abs(v.ImagePoint.Y - imgPt.Y)).First();
            return imgPt.Y < closest.ImagePoint.Y 
                ? VertebraLabelItem.ShiftLevel(closest.Level, -1) 
                : VertebraLabelItem.ShiftLevel(closest.Level, 1);
        }
    }

    public static void ShiftAllVertebraLabels(ViewportViewModel vm, int delta)
    {
        var onSlice = vm.VertebraLabels.Where(v => v.SliceIndex == vm.CurrentSliceIndex).ToList();
        foreach (var v in onSlice)
        {
            v.Level = VertebraLabelItem.ShiftLevel(v.Level, delta);
        }
    }

    private static bool IsVisualDescendantOf(DependencyObject? child, DependencyObject parent)
    {
        while (child != null)
        {
            if (child == parent) return true;
            if (child is Visual || child is System.Windows.Media.Media3D.Visual3D)
            {
                child = VisualTreeHelper.GetParent(child);
            }
            else if (child is FrameworkContentElement fce)
            {
                child = fce.Parent;
            }
            else
            {
                break;
            }
        }
        return false;
    }

    private void DrawEndpointDot(Point p, Brush brush)
    {
        var dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = brush
        };
        Canvas.SetLeft(dot, p.X - 2.5);
        Canvas.SetTop(dot, p.Y - 2.5);
        MeasurementCanvas.Children.Add(dot);
    }

    private void OnPatientTextRightDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.ContextMenu != null)
        {
            fe.ContextMenu.PlacementTarget = fe;
            fe.ContextMenu.IsOpen = true;
        }
    }

    private void OnCopyPatientName(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (!string.IsNullOrWhiteSpace(vm?.PatientName))
        {
            Clipboard.SetText(vm.PatientName);
            var mainVm = GetMainViewModel();
            if (mainVm != null)
            {
                mainVm.StatusMessage = $"Copied Patient Name: {vm.PatientName}";
            }
        }
    }

    private void OnCopyPatientId(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (!string.IsNullOrWhiteSpace(vm?.PatientId))
        {
            Clipboard.SetText(vm.PatientId);
            var mainVm = GetMainViewModel();
            if (mainVm != null)
            {
                mainVm.StatusMessage = $"Copied CNP / Patient ID: {vm.PatientId}";
            }
        }
    }

    private static Point? GetLineIntersection(Point p1, Point p2, Point p3, Point p4)
    {
        double d = (p1.X - p2.X) * (p3.Y - p4.Y) - (p1.Y - p2.Y) * (p3.X - p4.X);
        if (Math.Abs(d) < 1e-6) return null;
        double t = ((p1.X - p3.X) * (p3.Y - p4.Y) - (p1.Y - p3.Y) * (p3.X - p4.X)) / d;
        return new Point(p1.X + t * (p2.X - p1.X), p1.Y + t * (p2.Y - p1.Y));
    }

    private void OnCloseViewportClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (vm != null && mainVm != null)
        {
            mainVm.ClosePanel(vm);
        }
    }

    private void OnSlabBadgeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (vm != null && mainVm != null)
        {
            mainVm.SetActiveViewport(vm);
        }

        if (SlabPopup != null)
        {
            SlabPopup.IsOpen = !SlabPopup.IsOpen;
        }
    }

    private void OnSlabBadgeMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var vm = ViewModel;
        if (vm == null) return;

        double delta = e.Delta > 0 ? 1.0 : -1.0;
        if (vm.SlabThicknessMm >= 15.0)
        {
            delta = e.Delta > 0 ? 2.0 : -2.0;
        }
        vm.AdjustSlabThicknessMm(delta);

        var mainVm = GetMainViewModel();
        if (mainVm != null)
        {
            string thickStr = vm.SlabThicknessMm > 0.1 ? $"{vm.SlabThicknessMm:0.#} mm" : "Thin (1 slice)";
            mainVm.StatusMessage = $"Slab Thickness: {thickStr} [{vm.ProjectionModeShortName}]";
        }
    }

    private void OnSelectModeAvg(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;
        vm.SetProjectionMode(SlabProjectionMode.Average);
        var mainVm = GetMainViewModel();
        if (mainVm != null)
        {
            string thickStr = vm.SlabThicknessMm > 0.1 ? $" ({vm.SlabThicknessMm:0.#} mm)" : " (Thin slice)";
            mainVm.StatusMessage = $"Projection Mode: MPR / AVG{thickStr}";
        }
    }

    private void OnSelectModeMip(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;
        vm.SetProjectionMode(SlabProjectionMode.Mip);
        var mainVm = GetMainViewModel();
        if (mainVm != null)
        {
            mainVm.StatusMessage = $"Projection Mode: MIP ({vm.SlabThicknessMm:0.#} mm)";
        }
    }

    private void OnSelectModeMinIp(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;
        vm.SetProjectionMode(SlabProjectionMode.MinIp);
        var mainVm = GetMainViewModel();
        if (mainVm != null)
        {
            mainVm.StatusMessage = $"Projection Mode: MinIP ({vm.SlabThicknessMm:0.#} mm)";
        }
    }

    private void OnResetNormalSliceClick(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;
        vm.ResetToNormalSlice();
        if (SlabPopup != null)
        {
            SlabPopup.IsOpen = false;
        }
        var mainVm = GetMainViewModel();
        if (mainVm != null)
        {
            mainVm.StatusMessage = "Revenit la Slice Normal (MPR Thin 1 slice).";
        }
    }

    private void OnApplySlabToAllMprPlanes(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        var mainVm = GetMainViewModel();
        if (vm == null || mainVm == null) return;

        foreach (var vp in mainVm.DisplayedViewports)
        {
            if (vp.CurrentSeries != null && vp.CurrentSeries.Slices.Count >= 3)
            {
                vp.SetProjectionMode(vm.ProjectionMode, vm.SlabThicknessMm);
            }
        }
        if (SlabPopup != null)
        {
            SlabPopup.IsOpen = false;
        }
        mainVm.StatusMessage = $"Applied {vm.ProjectionModeShortName} ({vm.SlabThicknessMm:0.#} mm) to all open viewports.";
    }

    private void TriggerAiConsultation(Rect cropRect)
    {
        var activeVm = ViewModel;
        var mainVm = GetMainViewModel();
        if (activeVm?.DisplayImage == null || mainVm == null) return;

        var crops = new List<AiImageCrop>();

        // 1. Extract crop from active viewport
        var activeCrop = ExtractImageCrop(activeVm, cropRect);
        if (activeCrop != null)
        {
            crops.Add(activeCrop);
        }

        // 2. Check settings if we auto-include other visible viewports
        var settings = AiService.Instance.Settings;
        if (settings.AutoIncludeAllViewports && mainVm.Viewports != null)
        {
            foreach (var vp in mainVm.Viewports)
            {
                if (vp == activeVm || vp.DisplayImage == null) continue;
                var otherCrop = ExtractImageCrop(vp, cropRect);
                if (otherCrop != null)
                {
                    crops.Add(otherCrop);
                }
            }
        }

        if (crops.Count == 0) return;

        // Reset mouse tool back to WindowLevel so user can navigate immediately
        mainVm.AssignLeftTool(MouseToolAction.WindowLevel);

        // Open AI Consultation Dialog
        var consultWin = new AiConsultationWindow(crops)
        {
            Owner = Window.GetWindow(this)
        };
        consultWin.Show();
    }

    private static double DistanceToRect(Point p, Rect r)
    {
        double dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right));
        double dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool LineIntersectsRect(Point p1, Point p2, Rect r)
    {
        if (r.Contains(p1) || r.Contains(p2)) return true;
        Point tl = r.TopLeft;
        Point tr = r.TopRight;
        Point bl = r.BottomLeft;
        Point br = r.BottomRight;

        return GetLineIntersection(p1, p2, tl, tr) != null ||
               GetLineIntersection(p1, p2, tr, br) != null ||
               GetLineIntersection(p1, p2, br, bl) != null ||
               GetLineIntersection(p1, p2, bl, tl) != null;
    }

    public void TriggerAiConsultationForArrow(MeasurementItem arrow) => TriggerAiConsultationForArrows(new List<MeasurementItem> { arrow });

    public void TriggerAiConsultationForArrows(List<MeasurementItem> arrows)
    {
        var activeVm = ViewModel;
        var mainVm = GetMainViewModel();
        if (activeVm?.CurrentSeries == null || arrows.Count == 0 || mainVm == null) return;

        var crops = new List<AiImageCrop>();
        var series = activeVm.CurrentSeries;
        int totalSlices = series.Slices.Count;

        foreach (var arrow in arrows)
        {
            if (arrow.Points.Count < 2) continue;

            Point tip = arrow.Points[1];
            Point tail = arrow.Points[0];
            double arrowLen = (tip - tail).Length;
            double radius = Math.Max(90, Math.Min(350, arrowLen * 1.5));
            double centerX = tip.X;
            double centerY = tip.Y;
            Rect cropRect = new Rect(centerX - radius, centerY - radius, radius * 2.0, radius * 2.0);

            int centerSlice = arrow.SliceIndex;
            int startSlice = Math.Max(0, centerSlice - 2);
            int endSlice = Math.Min(totalSlices - 1, centerSlice + 2);

            for (int s = startSlice; s <= endSlice; s++)
            {
                if (crops.Any(c => c.SliceIndex == s && Math.Abs(c.CropRect.X - cropRect.X) < 10 && Math.Abs(c.CropRect.Y - cropRect.Y) < 10))
                    continue;

                AiImageCrop? crop = ExtractSliceCrop(activeVm, s, cropRect, arrow);
                if (crop != null)
                {
                    crops.Add(crop);
                }
            }
        }

        var settings = AiService.Instance.Settings;
        if (settings.AutoIncludeAllViewports && mainVm.Viewports != null)
        {
            foreach (var vp in mainVm.Viewports)
            {
                if (vp == activeVm || vp.DisplayImage == null) continue;
                if (arrows.Count > 0 && arrows[0].Points.Count >= 2)
                {
                    Point tip = arrows[0].Points[1];
                    Point tail = arrows[0].Points[0];
                    double arrowLen = (tip - tail).Length;
                    double radius = Math.Max(90, Math.Min(350, arrowLen * 1.5));
                    Rect primaryRect = new Rect(tip.X - radius, tip.Y - radius, radius * 2.0, radius * 2.0);
                    var otherCrop = ExtractImageCrop(vp, primaryRect);
                    if (otherCrop != null) crops.Add(otherCrop);
                }
            }
        }

        if (crops.Count == 0) return;

        mainVm.AssignLeftTool(MouseToolAction.WindowLevel);

        var consultWin = new AiConsultationWindow(crops)
        {
            Owner = Application.Current.MainWindow
        };
        consultWin.ShowDialog();
    }

    public static AiImageCrop? ExtractSliceCrop(ViewportViewModel vp, int sliceIndex, Rect cropRect, MeasurementItem? primaryArrow = null)
    {
        try
        {
            if (vp.CurrentSeries == null || sliceIndex < 0 || sliceIndex >= vp.CurrentSeries.Slices.Count)
                return null;

            BitmapSource? baseBmp = null;
            if (sliceIndex == vp.CurrentSliceIndex && vp.DisplayImage != null)
            {
                baseBmp = vp.DisplayImage;
            }
            else
            {
                var slice = vp.CurrentSeries.Slices[sliceIndex];
                WriteableBitmap? wb = null;
                PixelRenderer.RenderToBitmap(slice, vp.WindowCenter, vp.WindowWidth, vp.IsNegative, ref wb);
                baseBmp = wb;
            }

            if (baseBmp == null) return null;

            int imgW = baseBmp.PixelWidth;
            int imgH = baseBmp.PixelHeight;

            int x = Math.Max(0, (int)Math.Round(cropRect.X));
            int y = Math.Max(0, (int)Math.Round(cropRect.Y));
            int w = (int)Math.Round(cropRect.Width);
            int h = (int)Math.Round(cropRect.Height);

            if (x >= imgW || y >= imgH) return null;
            if (x + w > imgW) w = imgW - x;
            if (y + h > imgH) h = imgH - y;
            if (w < 4 || h < 4) return null;

            var croppedBmp = new CroppedBitmap(baseBmp, new Int32Rect(x, y, w, h));
            croppedBmp.Freeze();

            var currentCropRect = new Rect(x, y, w, h);

            var relevantArrows = vp.Measurements
                .Where(m => m.Type == MeasurementType.Arrow &&
                            m.SliceIndex == sliceIndex &&
                            m.Points.Count >= 2 &&
                            (currentCropRect.Contains(m.Points[0]) ||
                             currentCropRect.Contains(m.Points[1]) ||
                             LineIntersectsRect(m.Points[0], m.Points[1], currentCropRect) ||
                             DistanceToRect(m.Points[1], currentCropRect) <= 25.0))
                .ToList();

            if (primaryArrow != null && primaryArrow.SliceIndex == sliceIndex && !relevantArrows.Contains(primaryArrow))
            {
                relevantArrows.Add(primaryArrow);
            }

            bool hasOverlays = relevantArrows.Count > 0;
            BitmapSource finalImage = croppedBmp;

            if (hasOverlays)
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(croppedBmp, new Rect(0, 0, w, h));

                    var yellowBrush = new SolidColorBrush(Color.FromRgb(255, 235, 59));
                    yellowBrush.Freeze();
                    var arrowPen = new Pen(yellowBrush, 2.5);
                    arrowPen.Freeze();
                    var shadowPen = new Pen(Brushes.Black, 4.5);
                    shadowPen.Freeze();

                    foreach (var arrow in relevantArrows)
                    {
                        Point p0 = new Point(arrow.Points[0].X - x, arrow.Points[0].Y - y);
                        Point p1 = new Point(arrow.Points[1].X - x, arrow.Points[1].Y - y);

                        Vector dir = p1 - p0;
                        if (dir.Length > 2)
                        {
                            dir.Normalize();
                            Vector normal = new Vector(-dir.Y, dir.X);
                            Point head1 = p1 - dir * 16 + normal * 7;
                            Point head2 = p1 - dir * 16 - normal * 7;

                            dc.DrawLine(shadowPen, p0, p1);
                            dc.DrawLine(shadowPen, p1, head1);
                            dc.DrawLine(shadowPen, p1, head2);

                            dc.DrawLine(arrowPen, p0, p1);
                            dc.DrawLine(arrowPen, p1, head1);
                            dc.DrawLine(arrowPen, p1, head2);
                        }
                    }
                }

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                finalImage = rtb;
            }

            string title = vp.SeriesDescription;
            if (string.IsNullOrWhiteSpace(title)) title = $"{vp.Modality} Series";

            return new AiImageCrop
            {
                Image = finalImage,
                SeriesTitle = title,
                Modality = string.IsNullOrWhiteSpace(vp.Modality) ? "CT" : vp.Modality,
                SliceIndex = sliceIndex,
                WindowCenter = vp.WindowCenter,
                WindowWidth = vp.WindowWidth,
                CropRect = currentCropRect,
                HasArrowIndicator = hasOverlays
            };
        }
        catch
        {
            return null;
        }
    }

    public static AiImageCrop? ExtractImageCrop(ViewportViewModel vp, Rect? cropRect = null)
    {
        try
        {
            if (vp.DisplayImage == null) return null;

            int imgW = vp.DisplayImage.PixelWidth;
            int imgH = vp.DisplayImage.PixelHeight;

            Rect rect = cropRect ?? new Rect(0, 0, imgW, imgH);

            int x = Math.Max(0, (int)Math.Round(rect.X));
            int y = Math.Max(0, (int)Math.Round(rect.Y));
            int w = (int)Math.Round(rect.Width);
            int h = (int)Math.Round(rect.Height);

            if (x >= imgW || y >= imgH) return null;
            if (x + w > imgW) w = imgW - x;
            if (y + h > imgH) h = imgH - y;

            if (w < 4 || h < 4) return null;

            var croppedBmp = new CroppedBitmap(vp.DisplayImage, new Int32Rect(x, y, w, h));
            croppedBmp.Freeze();

            var currentCropRect = new Rect(x, y, w, h);

            // Arrows
            var relevantArrows = vp.Measurements
                .Where(m => m.Type == MeasurementType.Arrow && 
                            m.SliceIndex == vp.CurrentSliceIndex && 
                            m.Points.Count >= 2 &&
                            (currentCropRect.Contains(m.Points[0]) || 
                             currentCropRect.Contains(m.Points[1]) || 
                             LineIntersectsRect(m.Points[0], m.Points[1], currentCropRect) ||
                             DistanceToRect(m.Points[1], currentCropRect) <= 25.0))
                .ToList();

            // Ellipses (ROIs)
            var relevantEllipses = vp.Measurements
                .Where(m => m.Type == MeasurementType.Ellipse &&
                            m.SliceIndex == vp.CurrentSliceIndex &&
                            m.Points.Count >= 2 &&
                            (currentCropRect.Contains(m.Points[0]) ||
                             currentCropRect.Contains(m.Points[1]) ||
                             currentCropRect.IntersectsWith(new Rect(Math.Min(m.Points[0].X, m.Points[1].X),
                                                                     Math.Min(m.Points[0].Y, m.Points[1].Y),
                                                                     Math.Abs(m.Points[1].X - m.Points[0].X),
                                                                     Math.Abs(m.Points[1].Y - m.Points[0].Y)))))
                .ToList();

            // Rectangles (ROIs)
            var relevantRectangles = vp.Measurements
                .Where(m => m.Type == MeasurementType.Rectangle &&
                            m.SliceIndex == vp.CurrentSliceIndex &&
                            m.Points.Count >= 2 &&
                            (currentCropRect.Contains(m.Points[0]) ||
                             currentCropRect.Contains(m.Points[1]) ||
                             currentCropRect.IntersectsWith(new Rect(Math.Min(m.Points[0].X, m.Points[1].X),
                                                                     Math.Min(m.Points[0].Y, m.Points[1].Y),
                                                                     Math.Abs(m.Points[1].X - m.Points[0].X),
                                                                     Math.Abs(m.Points[1].Y - m.Points[0].Y)))))
                .ToList();

            // Length Rulers
            var relevantRulers = vp.Measurements
                .Where(m => m.Type == MeasurementType.Length &&
                            m.SliceIndex == vp.CurrentSliceIndex &&
                            m.Points.Count >= 2 &&
                            (currentCropRect.Contains(m.Points[0]) ||
                             currentCropRect.Contains(m.Points[1]) ||
                             LineIntersectsRect(m.Points[0], m.Points[1], currentCropRect)))
                .ToList();

            bool hasOverlays = relevantArrows.Count > 0 || relevantEllipses.Count > 0 || relevantRectangles.Count > 0 || relevantRulers.Count > 0;
            BitmapSource finalImage = croppedBmp;

            if (hasOverlays)
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(croppedBmp, new Rect(0, 0, w, h));

                    var yellowBrush = new SolidColorBrush(Color.FromRgb(255, 235, 59));
                    yellowBrush.Freeze();
                    var cyanBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255));
                    cyanBrush.Freeze();

                    var arrowPen = new Pen(yellowBrush, 2.5);
                    arrowPen.Freeze();
                    var roiPen = new Pen(cyanBrush, 2.5);
                    roiPen.Freeze();
                    var shadowPen = new Pen(Brushes.Black, 4.5);
                    shadowPen.Freeze();

                    // Arrows
                    foreach (var arrow in relevantArrows)
                    {
                        Point p0 = new Point(arrow.Points[0].X - x, arrow.Points[0].Y - y);
                        Point p1 = new Point(arrow.Points[1].X - x, arrow.Points[1].Y - y);

                        Vector dir = p1 - p0;
                        if (dir.Length > 2)
                        {
                            dir.Normalize();
                            Vector normal = new Vector(-dir.Y, dir.X);
                            Point head1 = p1 - dir * 16 + normal * 7;
                            Point head2 = p1 - dir * 16 - normal * 7;

                            dc.DrawLine(shadowPen, p0, p1);
                            dc.DrawLine(shadowPen, p1, head1);
                            dc.DrawLine(shadowPen, p1, head2);

                            dc.DrawLine(arrowPen, p0, p1);
                            dc.DrawLine(arrowPen, p1, head1);
                            dc.DrawLine(arrowPen, p1, head2);
                        }
                    }

                    // Ellipses (ROIs)
                    foreach (var ell in relevantEllipses)
                    {
                        Point p0 = new Point(ell.Points[0].X - x, ell.Points[0].Y - y);
                        Point p1 = new Point(ell.Points[1].X - x, ell.Points[1].Y - y);
                        double left = Math.Min(p0.X, p1.X);
                        double top = Math.Min(p0.Y, p1.Y);
                        double ellW = Math.Abs(p1.X - p0.X);
                        double ellH = Math.Abs(p1.Y - p0.Y);
                        Point center = new Point(left + ellW / 2.0, top + ellH / 2.0);

                        dc.DrawEllipse(null, shadowPen, center, ellW / 2.0, ellH / 2.0);
                        dc.DrawEllipse(null, roiPen, center, ellW / 2.0, ellH / 2.0);
                    }

                    // Rectangles (ROIs)
                    foreach (var r in relevantRectangles)
                    {
                        Point p0 = new Point(r.Points[0].X - x, r.Points[0].Y - y);
                        Point p1 = new Point(r.Points[1].X - x, r.Points[1].Y - y);
                        double left = Math.Min(p0.X, p1.X);
                        double top = Math.Min(p0.Y, p1.Y);
                        double rW = Math.Abs(p1.X - p0.X);
                        double rH = Math.Abs(p1.Y - p0.Y);
                        Rect rBounds = new Rect(left, top, rW, rH);

                        dc.DrawRectangle(null, shadowPen, rBounds);
                        dc.DrawRectangle(null, roiPen, rBounds);
                    }

                    // Rulers
                    foreach (var ruler in relevantRulers)
                    {
                        Point p0 = new Point(ruler.Points[0].X - x, ruler.Points[0].Y - y);
                        Point p1 = new Point(ruler.Points[1].X - x, ruler.Points[1].Y - y);
                        dc.DrawLine(shadowPen, p0, p1);
                        dc.DrawLine(arrowPen, p0, p1);
                    }
                }

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                finalImage = rtb;
            }

            string title = vp.SeriesDescription;
            if (string.IsNullOrWhiteSpace(title)) title = $"{vp.Modality} Series";

            return new AiImageCrop
            {
                Image = finalImage,
                SeriesTitle = title,
                Modality = string.IsNullOrWhiteSpace(vp.Modality) ? "CT" : vp.Modality,
                SliceIndex = vp.CurrentSliceIndex,
                WindowCenter = vp.WindowCenter,
                WindowWidth = vp.WindowWidth,
                CropRect = new Rect(x, y, w, h),
                IsIncluded = true,
                HasArrowIndicator = relevantArrows.Count > 0,
                ArrowCount = relevantArrows.Count,
                HasRoiIndicator = relevantEllipses.Count > 0 || relevantRectangles.Count > 0,
                RoiCount = relevantEllipses.Count + relevantRectangles.Count
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to capture image for AI: {ex.Message}");
            return null;
        }
    }
    #endregion
}
