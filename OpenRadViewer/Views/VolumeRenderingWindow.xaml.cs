// @purpose: 3D interactive volume rendering inspection window supporting presets, clipping planes, orientation cube, and snapshot export.
// @depends: OpenRadViewer.Core.Services (VolumeRenderingService, D3D11VolumeRenderer, MprService), OpenRadViewer.Core.Models
// @exports: VolumeRenderingWindow

using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public partial class VolumeRenderingWindow : Window
{
    private readonly SeriesModel _series;
    private MprService.VolumeContext? _volCtx;
    private bool _isInitialized;

    // Camera & Rendering State
    private double _yaw = 0.0;      // Yaw (horizontal turntable rotation)
    private double _pitch = 0.0;    // Pitch (vertical tilt)
    private double _roll = 0.0;     // Roll (in-plane twist)
    private double _zoom = 1.0;
    private double _panX = 0.0;
    private double _panY = 0.0;

    private double _windowCenter = 280;
    private double _windowWidth = 400;
    private VolumeRenderingPreset _preset = VolumeRenderingPreset.Angio;

    // 3D Cropping / Table Removal (6 Planes) - "Fără Masă" is ON by default (18% posterior crop)
    private double _cropPosterior = 0.18;
    private double _cropAnterior = 0.0;
    private double _cropRight = 0.0;
    private double _cropLeft = 0.0;
    private double _cropSuperior = 0.0;
    private double _cropInferior = 0.0;
    private bool _isHideTable = true;

    // Mouse Interaction
    private bool _isDragging;
    private Point _lastMousePos;
    private DragAction _currentDragAction = DragAction.None;

    // Auto-Spin
    private readonly DispatcherTimer _spinTimer;
    private bool _isAutoSpinning;

    // Debounced progressive refinement render
    private readonly DispatcherTimer _refineTimer;

    private enum DragAction
    {
        None,
        Rotate,
        Roll,
        Pan,
        Zoom,
        WindowLevel
    }

    // Active Left Mouse Tool
    private DragAction _leftMouseTool = DragAction.Rotate;

    public VolumeRenderingWindow(SeriesModel series)
    {
        _series = series;

        _spinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _spinTimer.Tick += (s, e) =>
        {
            _yaw = (_yaw + 2.0) % 360.0;
            Render(isFastMode: true);
        };

        _refineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _refineTimer.Tick += (s, e) =>
        {
            _refineTimer?.Stop();
            Render(isFastMode: false);
        };

        InitializeComponent();

        TxtPatientName.Text = series.PatientName;
        TxtSeriesInfo.Text = $"{series.Modality} - {series.SeriesDescription} ({series.Slices.Count} slices)";

        // Initialize with upright coronal view (patient standing upright)
        _yaw = 0.0;
        _pitch = 0.0;
        _roll = 0.0;

        // Set default WL/WW based on modality & preset
        if (_series.Modality == "MR")
        {
            _preset = VolumeRenderingPreset.MIP;
            var refSlice = _series.Slices.FirstOrDefault();
            if (refSlice != null && refSlice.DefaultWindowWidth > 1)
            {
                _windowCenter = refSlice.DefaultWindowCenter;
                _windowWidth = refSlice.DefaultWindowWidth;
            }
            else
            {
                _windowCenter = 400;
                _windowWidth = 800;
            }
            TxtPresetName.Text = "Preset: MIP (MR Signal)";
            UpdateWlOverlay();
        }
        else
        {
            ApplyPresetDefaults(_preset);
        }
        UpdateHideTableButton();

        Loaded += OnWindowLoaded;
        KeyDown += OnWindowKeyDown;

        _isInitialized = true;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLongPtr", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLong", SetLastError = true)]
    private static extern int SetClassLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr SetClassLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetClassLongPtr64(hWnd, nIndex, dwNewLong);
        else
            return new IntPtr(SetClassLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int WM_ERASEBKGND = 0x0014;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            // Dark mode title bar for Windows 10 & 11
            int darkMode = 1;
            DwmSetWindowAttribute(handle, 19, ref darkMode, sizeof(int));
            DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));

            int captionColor = 0x00181818; // dark caption bar (#181818 in BGR)
            DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));

            int borderColor = 0x00333333;
            DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));

            // Set Win32 window class background to BLACK_BRUSH (4) to prevent white flash
            SetClassLong(handle, -10, GetStockObject(4));

            // Hook WM_ERASEBKGND so Win32 never erases with default white
            var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
            source?.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ERASEBKGND)
        {
            GetClientRect(hwnd, out RECT rc);
            FillRect(wParam, ref rc, GetStockObject(4)); // BLACK_BRUSH
            handled = true;
            return (IntPtr)1;
        }
        return IntPtr.Zero;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // Yield to WPF dispatcher so the black window, dark toolbar, and loading overlay render first
        await Task.Delay(50);
        try
        {
            if (_volCtx == null)
            {
                _volCtx = await Task.Run(() => MprService.GetVolumeContext(_series));
            }

            if (_volCtx != null)
            {
                Render(isFastMode: false);
            }
            else
            {
                if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;
                MessageBox.Show(this, "Failed to load 3D volumetric data for this series.", "3D VR", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during initial VR render: {ex.Message}");
            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            OnResetView(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnCopyToClipboard(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            OnSelectToolRotate(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.W)
        {
            OnSelectToolWl(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.M)
        {
            OnSelectToolPan(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Z)
        {
            OnSelectToolZoom(sender, e);
            e.Handled = true;
        }
    }

    private void ApplyPresetDefaults(VolumeRenderingPreset preset)
    {
        _preset = preset;
        switch (preset)
        {
            case VolumeRenderingPreset.Bones:
                _windowCenter = 300;
                _windowWidth = 500;
                if (TxtPresetName != null) TxtPresetName.Text = "Preset: Bones (CT)";
                break;
            case VolumeRenderingPreset.Angio:
                _windowCenter = 280;
                _windowWidth = 400;
                if (TxtPresetName != null) TxtPresetName.Text = "Preset: Angio (Vessels)";
                break;
            case VolumeRenderingPreset.Skin:
                _windowCenter = -300;
                _windowWidth = 450;
                if (TxtPresetName != null) TxtPresetName.Text = "Preset: Skin & Scars";
                break;
            case VolumeRenderingPreset.MIP:
                _windowCenter = 300;
                _windowWidth = 1200;
                if (TxtPresetName != null) TxtPresetName.Text = "Preset: MIP (Max Intensity)";
                break;
            case VolumeRenderingPreset.Airways:
                _windowCenter = -620;
                _windowWidth = 260;
                if (TxtPresetName != null) TxtPresetName.Text = "Preset: Airways / Lungs";
                _isHideTable = true;
                _cropPosterior = 0.18;
                UpdateHideTableButton();
                break;
        }
        UpdateWlOverlay();
    }

    private void UpdateWlOverlay()
    {
        if (TxtWlWw != null)
        {
            TxtWlWw.Text = $"WL: {(int)_windowCenter} WW: {(int)_windowWidth}";
        }
    }

    private void UpdateOrientationLabels()
    {
        // Calculate anatomical labels dynamically based on turntable camera orientation
        float radYaw = (float)(_yaw * Math.PI / 180.0);
        float radPitch = (float)(_pitch * Math.PI / 180.0);
        float clampedPitch = Math.Clamp(radPitch, -89.0f * (float)Math.PI / 180.0f, 89.0f * (float)Math.PI / 180.0f);

        Vector3 camRight = new Vector3((float)Math.Cos(radYaw), (float)Math.Sin(radYaw), 0f);
        Vector3 upBase = new Vector3(
            -(float)Math.Sin(radYaw) * (float)Math.Sin(clampedPitch),
            (float)Math.Cos(radYaw) * (float)Math.Sin(clampedPitch),
            (float)Math.Cos(clampedPitch)
        );

        static string GetAnatomicalLabel(Vector3 v)
        {
            float ax = Math.Abs(v.X);
            float ay = Math.Abs(v.Y);
            float az = Math.Abs(v.Z);

            if (az >= ax && az >= ay)
                return v.Z >= 0 ? "S" : "I";
            if (ay >= ax && ay >= az)
                return v.Y >= 0 ? "P" : "A";
            return v.X >= 0 ? "L" : "R";
        }

        string top = GetAnatomicalLabel(upBase);
        string bottom = GetAnatomicalLabel(-upBase);
        string right = GetAnatomicalLabel(camRight);
        string left = GetAnatomicalLabel(-camRight);

        if (TxtOrientTop != null) TxtOrientTop.Text = top;
        if (TxtOrientBottom != null) TxtOrientBottom.Text = bottom;
        if (TxtOrientLeft != null) TxtOrientLeft.Text = left;
        if (TxtOrientRight != null) TxtOrientRight.Text = right;
    }

    public void Render(bool isFastMode = false)
    {
        if (!_isInitialized || _volCtx == null || ImgVr == null) return;

        try
        {
            int renderW = isFastMode ? 440 : 640;
            int renderH = isFastMode ? 440 : 640;

            var p = new VolumeRenderingService.RenderParams(
                Yaw: _yaw,
                Pitch: _pitch,
                Roll: _roll,
                Zoom: _zoom,
                PanX: _panX,
                PanY: _panY,
                WindowCenter: _windowCenter,
                WindowWidth: _windowWidth,
                Preset: _preset,
                IsFastMode: isFastMode,
                CropPosterior: _cropPosterior,
                CropAnterior: _cropAnterior,
                CropSuperior: _cropSuperior,
                CropInferior: _cropInferior,
                CropLeft: _cropLeft,
                CropRight: _cropRight
            );

            string volKey = $"{_series.SeriesInstanceUid}_{_series.Slices.Count}";
            var sw = Stopwatch.StartNew();
            var bmp = VolumeRenderingService.Instance.RenderVolume(_volCtx, p, renderW, renderH, volKey);
            sw.Stop();

            if (bmp != null)
            {
                ImgVr.Source = bmp;
                if (LoadingOverlay != null)
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                }
            }

            string engine = VolumeRenderingService.Instance.IsGpuAccelerated ? "GPU D3D11" : "CPU";
            if (TxtRenderTime != null)
            {
                TxtRenderTime.Text = $"{sw.ElapsedMilliseconds} ms [{engine}] ({(isFastMode ? "Interactive" : "Refined")})";
            }
            UpdateOrientationLabels();
            UpdateWlOverlay();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Volume Rendering error: {ex.Message}");
            if (LoadingOverlay != null)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void RequestRefinement()
    {
        _refineTimer?.Stop();
        _refineTimer?.Start();
    }

    #region Mouse Navigation
    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        ViewportBorder.Focus();
        _lastMousePos = e.GetPosition(ViewportBorder);
        _isDragging = true;
        ViewportBorder.CaptureMouse();

        if (e.ChangedButton == MouseButton.Left)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                _currentDragAction = DragAction.Roll;
            else
                _currentDragAction = _leftMouseTool;
        }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            _currentDragAction = DragAction.WindowLevel;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            _currentDragAction = DragAction.Pan;
        }

        e.Handled = true;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _currentDragAction == DragAction.None) return;

        Point current = e.GetPosition(ViewportBorder);
        double deltaX = current.X - _lastMousePos.X;
        double deltaY = current.Y - _lastMousePos.Y;
        _lastMousePos = current;

        switch (_currentDragAction)
        {
            case DragAction.Rotate:
                _yaw -= deltaX * 0.6;
                _yaw = ((_yaw % 360.0) + 360.0) % 360.0;
                _pitch = Math.Clamp(_pitch + deltaY * 0.6, -89.0, 89.0);
                Render(isFastMode: true);
                RequestRefinement();
                break;

            case DragAction.Roll:
                _roll += deltaX * 0.8;
                Render(isFastMode: true);
                RequestRefinement();
                break;

            case DragAction.Pan:
                _panX += deltaX * 0.8;
                _panY += deltaY * 0.8;
                Render(isFastMode: true);
                RequestRefinement();
                break;

            case DragAction.Zoom:
                double zoomFactor = 1.0 + (-deltaY * 0.008);
                _zoom = Math.Clamp(_zoom * zoomFactor, 0.2, 10.0);
                Render(isFastMode: true);
                RequestRefinement();
                break;

            case DragAction.WindowLevel:
                _windowWidth = Math.Max(10.0, _windowWidth + deltaX * 4.0);
                _windowCenter += deltaY * 3.0;
                Render(isFastMode: true);
                RequestRefinement();
                break;
        }

        e.Handled = true;
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            _currentDragAction = DragAction.None;
            ViewportBorder.ReleaseMouseCapture();
            Render(isFastMode: false);
        }
        e.Handled = true;
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.12 : 0.89;
        _zoom = Math.Clamp(_zoom * factor, 0.2, 10.0);
        Render(isFastMode: true);
        RequestRefinement();
        e.Handled = true;
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Render(isFastMode: false);
        }
    }
    #endregion

    #region Toolbar Handlers
    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        if (ComboPresets?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (Enum.TryParse<VolumeRenderingPreset>(tag, out var preset))
            {
                ApplyPresetDefaults(preset);
                Render(isFastMode: false);
            }
        }
    }

    private void OnViewAnterior(object sender, RoutedEventArgs e)
    {
        _yaw = 0; _pitch = 0; _roll = 0;
        Render(isFastMode: false);
    }

    private void OnViewPosterior(object sender, RoutedEventArgs e)
    {
        _yaw = 180; _pitch = 0; _roll = 0;
        Render(isFastMode: false);
    }

    private void OnViewLeft(object sender, RoutedEventArgs e)
    {
        _yaw = 90; _pitch = 0; _roll = 0;
        Render(isFastMode: false);
    }

    private void OnViewRight(object sender, RoutedEventArgs e)
    {
        _yaw = 270; _pitch = 0; _roll = 0;
        Render(isFastMode: false);
    }

    private void OnViewSuperior(object sender, RoutedEventArgs e)
    {
        _yaw = 0; _pitch = 89; _roll = 0;
        Render(isFastMode: false);
    }

    private void OnViewInferior(object sender, RoutedEventArgs e)
    {
        _yaw = 0; _pitch = -89; _roll = 0;
        Render(isFastMode: false);
    }

    private void OnResetView(object sender, RoutedEventArgs e)
    {
        _yaw = 0; _pitch = 0; _roll = 0;
        _panX = 0; _panY = 0; _zoom = 1.0;
        ApplyPresetDefaults(_preset);
        Render(isFastMode: false);
    }

    private void OnToggleAutoSpin(object sender, RoutedEventArgs e)
    {
        _isAutoSpinning = !_isAutoSpinning;
        if (_isAutoSpinning)
        {
            _spinTimer.Start();
            BtnAutoSpin.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(100, 0, 230, 118));
        }
        else
        {
            _spinTimer.Stop();
            BtnAutoSpin.Background = System.Windows.Media.Brushes.Transparent;
            Render(isFastMode: false);
        }
    }

    private void OnCopyToClipboard(object sender, RoutedEventArgs e)
    {
        if (ImgVr.Source is BitmapSource bmp)
        {
            Clipboard.SetImage(bmp);
            MessageBox.Show(this, "3D VR image copied to clipboard.", "Parsec 3D VR", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnSelectToolRotate(object sender, RoutedEventArgs e)
    {
        _leftMouseTool = DragAction.Rotate;
        UpdateToolHighlight();
    }

    private void OnSelectToolWl(object sender, RoutedEventArgs e)
    {
        _leftMouseTool = DragAction.WindowLevel;
        UpdateToolHighlight();
    }

    private void OnSelectToolPan(object sender, RoutedEventArgs e)
    {
        _leftMouseTool = DragAction.Pan;
        UpdateToolHighlight();
    }

    private void OnSelectToolZoom(object sender, RoutedEventArgs e)
    {
        _leftMouseTool = DragAction.Zoom;
        UpdateToolHighlight();
    }

    private void UpdateToolHighlight()
    {
        var activeBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 229, 255));
        var defaultBrush = System.Windows.Media.Brushes.Transparent;
        var whiteBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 224, 224));

        BtnToolRotate.Background = _leftMouseTool == DragAction.Rotate ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 0, 229, 255)) : defaultBrush;
        BtnToolWl.Background = _leftMouseTool == DragAction.WindowLevel ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 0, 229, 255)) : defaultBrush;
        BtnToolPan.Background = _leftMouseTool == DragAction.Pan ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 0, 229, 255)) : defaultBrush;
        BtnToolZoom.Background = _leftMouseTool == DragAction.Zoom ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 0, 229, 255)) : defaultBrush;

        TxtToolRotate.Foreground = _leftMouseTool == DragAction.Rotate ? activeBrush : whiteBrush;
        TxtToolWl.Foreground = _leftMouseTool == DragAction.WindowLevel ? activeBrush : whiteBrush;
        TxtToolPan.Foreground = _leftMouseTool == DragAction.Pan ? activeBrush : whiteBrush;
        TxtToolZoom.Foreground = _leftMouseTool == DragAction.Zoom ? activeBrush : whiteBrush;
    }

    #region 3D Crop & Table Removal Handlers
    private void OnToggleHideTable(object sender, RoutedEventArgs e)
    {
        _isHideTable = !_isHideTable;
        _cropPosterior = _isHideTable ? 0.18 : 0.0;
        SliderCropPost.Value = _cropPosterior * 100.0;
        UpdateHideTableButton();
        Render(isFastMode: false);
    }

    private void UpdateHideTableButton()
    {
        if (BtnHideTable == null || IconHideTable == null || TxtHideTable == null) return;
        var activeBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 229, 255));
        var activeBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 0, 229, 255));
        var defaultBrush = System.Windows.Media.Brushes.Transparent;
        var whiteBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(204, 204, 204));

        BtnHideTable.Background = _isHideTable ? activeBg : defaultBrush;
        IconHideTable.Stroke = _isHideTable ? activeBrush : whiteBrush;
        TxtHideTable.Foreground = _isHideTable ? activeBrush : whiteBrush;
    }

    private void OnOpenCropPopup(object sender, RoutedEventArgs e)
    {
        SliderCropAnt.Value = _cropAnterior * 100.0;
        SliderCropPost.Value = _cropPosterior * 100.0;
        SliderCropRight.Value = _cropRight * 100.0;
        SliderCropLeft.Value = _cropLeft * 100.0;
        SliderCropSup.Value = _cropSuperior * 100.0;
        SliderCropInf.Value = _cropInferior * 100.0;

        TxtCropAntVal.Text = $"{(int)(_cropAnterior * 100.0)}%";
        TxtCropPostVal.Text = $"{(int)(_cropPosterior * 100.0)}%";
        TxtCropRightVal.Text = $"{(int)(_cropRight * 100.0)}%";
        TxtCropLeftVal.Text = $"{(int)(_cropLeft * 100.0)}%";
        TxtCropSupVal.Text = $"{(int)(_cropSuperior * 100.0)}%";
        TxtCropInfVal.Text = $"{(int)(_cropInferior * 100.0)}%";

        CropPopup.IsOpen = true;
    }

    private void OnCropAntChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _cropAnterior = e.NewValue / 100.0;
        if (TxtCropAntVal != null) TxtCropAntVal.Text = $"{(int)e.NewValue}%";
        if (!_isInitialized || _volCtx == null || _refineTimer == null) return;
        Render(isFastMode: true);
        RequestRefinement();
    }

    private void OnCropPostChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _cropPosterior = e.NewValue / 100.0;
        if (TxtCropPostVal != null) TxtCropPostVal.Text = $"{(int)e.NewValue}%";
        _isHideTable = _cropPosterior > 0.05;
        UpdateHideTableButton();
        if (!_isInitialized || _volCtx == null || _refineTimer == null) return;
        Render(isFastMode: true);
        RequestRefinement();
    }

    private void OnCropRightChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _cropRight = e.NewValue / 100.0;
        if (TxtCropRightVal != null) TxtCropRightVal.Text = $"{(int)e.NewValue}%";
        if (!_isInitialized || _volCtx == null || _refineTimer == null) return;
        Render(isFastMode: true);
        RequestRefinement();
    }

    private void OnCropLeftChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _cropLeft = e.NewValue / 100.0;
        if (TxtCropLeftVal != null) TxtCropLeftVal.Text = $"{(int)e.NewValue}%";
        if (!_isInitialized || _volCtx == null || _refineTimer == null) return;
        Render(isFastMode: true);
        RequestRefinement();
    }

    private void OnCropSupChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _cropSuperior = e.NewValue / 100.0;
        if (TxtCropSupVal != null) TxtCropSupVal.Text = $"{(int)e.NewValue}%";
        if (!_isInitialized || _volCtx == null || _refineTimer == null) return;
        Render(isFastMode: true);
        RequestRefinement();
    }

    private void OnCropInfChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _cropInferior = e.NewValue / 100.0;
        if (TxtCropInfVal != null) TxtCropInfVal.Text = $"{(int)e.NewValue}%";
        if (!_isInitialized || _volCtx == null || _refineTimer == null) return;
        Render(isFastMode: true);
        RequestRefinement();
    }

    private void OnResetCrop(object sender, RoutedEventArgs e)
    {
        _cropAnterior = 0.0;
        _cropPosterior = 0.0;
        _cropRight = 0.0;
        _cropLeft = 0.0;
        _cropSuperior = 0.0;
        _cropInferior = 0.0;
        _isHideTable = false;

        SliderCropAnt.Value = 0;
        SliderCropPost.Value = 0;
        SliderCropRight.Value = 0;
        SliderCropLeft.Value = 0;
        SliderCropSup.Value = 0;
        SliderCropInf.Value = 0;

        TxtCropAntVal.Text = "0%";
        TxtCropPostVal.Text = "0%";
        TxtCropRightVal.Text = "0%";
        TxtCropLeftVal.Text = "0%";
        TxtCropSupVal.Text = "0%";
        TxtCropInfVal.Text = "0%";

        UpdateHideTableButton();
        CropPopup.IsOpen = false;
        Render(isFastMode: false);
    }
    #endregion

    private void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        _spinTimer.Stop();
        Close();
    }
    #endregion
}
