// @purpose: ViewModel for an individual DICOM viewport tile, controlling active series, current slice index, pan, zoom, windowing (WL/WW), rotation, Cine playback, and ROI measurements.
// @depends: CommunityToolkit.Mvvm, OpenRadViewer.Core.Models, OpenRadViewer.Core.Services
// @exports: ViewportViewModel

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.ViewModels;

public partial class ViewportViewModel : ObservableObject
{
    private readonly DispatcherTimer _cineTimer;
    private WriteableBitmap? _cachedBitmap;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private int _viewportIndex;

    [ObservableProperty]
    private SeriesModel? _currentSeries;

    [ObservableProperty]
    private int _currentSliceIndex;

    [ObservableProperty]
    private double _windowCenter = 128;

    [ObservableProperty]
    private double _windowWidth = 256;

    [ObservableProperty]
    private bool _isNegative;

    [ObservableProperty]
    private double _zoom = 1.0;

    public bool NeedsAutoFit { get; set; } = true;

    [ObservableProperty]
    private double _panX = 0;

    [ObservableProperty]
    private double _panY = 0;

    [ObservableProperty]
    private int _rotationAngle = 0; // 0, 90, 180, 270

    [ObservableProperty]
    private bool _flipHorizontal;

    [ObservableProperty]
    private bool _flipVertical;

    [ObservableProperty]
    private bool _showOverlays = true;

    [ObservableProperty]
    private bool _showAnnotations = true;

    [ObservableProperty]
    private bool _showPatientData = true;

    [ObservableProperty]
    private bool _isCinePlaying;

    [ObservableProperty]
    private int _cineFps = 15;

    [ObservableProperty]
    private BitmapSource? _displayImage;

    [ObservableProperty]
    private Point? _crosshair3DPoint;

    [ObservableProperty]
    private bool _showCrosshair3D;

    [ObservableProperty]
    private Point? _scoutLineStart;

    [ObservableProperty]
    private Point? _scoutLineEnd;

    [ObservableProperty]
    private bool _showScoutLine;

    public SeriesModel? OriginalSeries { get; set; }

    [ObservableProperty]
    private OrthogonalPlane _currentPlane = OrthogonalPlane.Axial;

    [ObservableProperty]
    private bool _isMprViewport;

    [ObservableProperty]
    private OrthogonalPlane? _mprPlane;

    public double[]? MprRowDir { get; set; }
    public double[]? MprColDir { get; set; }
    public double[]? MprCenterPoint { get; set; }
    public bool HasCustomMprOrientation => MprRowDir != null && MprColDir != null;

    public void ResetMprOrientation()
    {
        MprRowDir = null;
        MprColDir = null;
        MprCenterPoint = null;
    }

    public ObservableCollection<MprReferenceLine> MprLines { get; } = new();

    public event Action? MprLinesChanged;

    public void NotifyMprLinesChanged() => MprLinesChanged?.Invoke();

    public Brush? MprBadgeBrush => MprPlane switch
    {
        OrthogonalPlane.Axial => new SolidColorBrush(Color.FromRgb(255, 214, 0)),   // Solar Gold (#FFD600)
        OrthogonalPlane.Coronal => new SolidColorBrush(Color.FromRgb(255, 64, 129)), // Coral Rose (#FF4081)
        OrthogonalPlane.Sagittal => new SolidColorBrush(Color.FromRgb(0, 229, 255)), // Electric Cyan (#00E5FF)
        _ => null
    };

    public event Action<ViewportViewModel>? TransformChanged;

    partial void OnZoomChanged(double value) => TransformChanged?.Invoke(this);
    partial void OnPanXChanged(double value) => TransformChanged?.Invoke(this);
    partial void OnPanYChanged(double value) => TransformChanged?.Invoke(this);

    public ObservableCollection<MeasurementItem> Measurements { get; } = new();
    public ObservableCollection<VertebraLabelItem> VertebraLabels { get; } = new();

    public DicomSliceModel? CurrentSlice =>
        CurrentSeries != null && CurrentSliceIndex >= 0 && CurrentSliceIndex < CurrentSeries.Slices.Count
            ? CurrentSeries.Slices[CurrentSliceIndex]
            : null;

    public int TotalSlices => CurrentSeries?.Slices.Count ?? 0;

    // Overlay properties for XAML bindings
    public string PatientName => CurrentSeries?.PatientName ?? "";
    public string PatientId => CurrentSeries?.PatientId ?? "";
    public string PatientIdAndAgeOverlay => OpenRadViewer.Core.Services.PatientInfoHelper.FormatIdAndAge(PatientId);
    public string SliceCommentOverlay => !string.IsNullOrWhiteSpace(CurrentSlice?.ImageComments) ? CurrentSlice.ImageComments.Trim().Replace("/", "-") : "";
    public string StudyDate => CurrentSeries?.StudyDate ?? "";
    public string SeriesDescription => CurrentSeries?.SeriesDescription ?? "";
    public string Modality => CurrentSeries?.Modality ?? "";
    public int SeriesNumber => CurrentSeries?.SeriesNumber ?? 0;

    public string ImageIndexOverlay => TotalSlices > 0 ? $"Im: {CurrentSliceIndex + 1}/{TotalSlices}" : "";
    public string SeriesNumberOverlay => SeriesNumber > 0 ? $"Se: {SeriesNumber}" : "";
    public string SeriesTimeOverlay
    {
        get
        {
            string time = CurrentSlice != null && !string.IsNullOrWhiteSpace(CurrentSlice.AcquisitionTime)
                ? CurrentSlice.AcquisitionTime
                : (CurrentSeries?.SeriesTime ?? "");
            return OpenRadViewer.Core.Services.PatientInfoHelper.FormatDicomTime(time);
        }
    }
    public string WindowOverlay => $"WL: {(int)WindowCenter} WW: {(int)WindowWidth}{(IsNegative ? " [INV]" : "")}";
    public string ThicknessLocationOverlay
    {
        get
        {
            if (CurrentSlice == null) return "";
            return $"T: {CurrentSlice.SliceThickness:0.#}mm L: {CurrentSlice.SliceLocation:0.#}mm";
        }
    }
    public string AcquisitionParamsOverlay
    {
        get
        {
            if (CurrentSlice == null) return "";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(CurrentSlice.MagneticFieldStrength)) parts.Add($"FS: {CurrentSlice.MagneticFieldStrength}");
            if (CurrentSlice.RepetitionTime > 0) parts.Add($"TR: {CurrentSlice.RepetitionTime:0.#}");
            if (CurrentSlice.EchoTime > 0) parts.Add($"TE: {CurrentSlice.EchoTime:0.#}");
            return string.Join(" ", parts);
        }
    }

    public string OrientationLeft => CurrentSlice?.OrientationLeft ?? "";
    public string OrientationRight => CurrentSlice?.OrientationRight ?? "";
    public string OrientationTop => CurrentSlice?.OrientationTop ?? "";
    public string OrientationBottom => CurrentSlice?.OrientationBottom ?? "";

    public event Action<ViewportViewModel>? ImageRendered;

    public ViewportViewModel(int index = 0)
    {
        ViewportIndex = index;
        _cineTimer = new DispatcherTimer();
        _cineTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, CineFps));
        _cineTimer.Tick += (s, e) => NextImage();
    }

    public void LoadSeries(SeriesModel series, int initialSlice = 0)
    {
        OriginalSeries = series;
        CurrentSeries = series;
        ResetMprOrientation();
        CurrentSliceIndex = Math.Clamp(initialSlice, 0, Math.Max(0, series.Slices.Count - 1));

        if (CurrentSlice != null)
        {
            CurrentPlane = MprService.DetectOrientation(CurrentSlice);
            WindowCenter = CurrentSlice.DefaultWindowCenter;
            WindowWidth = CurrentSlice.DefaultWindowWidth;
        }

        ResetTransform();
        RenderCurrentSlice();
        NotifyOverlayChanges();
    }

    public void SwitchOrthogonalPlane(OrthogonalPlane targetPlane, int? initialSlice = null)
    {
        if (OriginalSeries == null || OriginalSeries.Slices.Count < 3) return;
        if (CurrentPlane == targetPlane && CurrentSeries != null) return;

        var mprSeries = MprService.GenerateOrthogonalSeries(OriginalSeries, targetPlane);
        if (mprSeries != null)
        {
            CurrentSeries = mprSeries;
            CurrentPlane = targetPlane;
            ResetMprOrientation();
            int sliceIdx = initialSlice.HasValue 
                ? Math.Clamp(initialSlice.Value, 0, Math.Max(0, mprSeries.Slices.Count - 1))
                : mprSeries.Slices.Count / 2;
            CurrentSliceIndex = sliceIdx;
            ResetTransform();
            RenderCurrentSlice();
            NotifyOverlayChanges();
        }
    }

    public void SetSliceIndex(int index)
    {
        if (CurrentSeries == null || CurrentSeries.Slices.Count == 0) return;
        int clamped = Math.Clamp(index, 0, CurrentSeries.Slices.Count - 1);
        int delta = clamped - CurrentSliceIndex;
        if (delta == 0) return;

        CurrentSliceIndex = clamped;
        if (HasCustomMprOrientation)
        {
            MoveObliqueSlice(delta);
        }
        else
        {
            RenderCurrentSlice();
            NotifyOverlayChanges();
        }
    }

    public void MoveObliqueSlice(int delta)
    {
        if (!HasCustomMprOrientation || CurrentSlice == null) return;
        var r = MprRowDir!;
        var c = MprColDir!;
        double nx = r[1] * c[2] - r[2] * c[1];
        double ny = r[2] * c[0] - r[0] * c[2];
        double nz = r[0] * c[1] - r[1] * c[0];
        double nlen = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (nlen > 1e-4) { nx /= nlen; ny /= nlen; nz /= nlen; }

        double stepDist = (CurrentSlice.SliceThickness > 0 ? CurrentSlice.SliceThickness : 1.0) * delta;
        var curCenter = MprCenterPoint ?? new double[] { 
            CurrentSlice.ImagePositionPatient![0], 
            CurrentSlice.ImagePositionPatient![1], 
            CurrentSlice.ImagePositionPatient![2] 
        };

        double[] newCenter = new double[] {
            curCenter[0] + stepDist * nx,
            curCenter[1] + stepDist * ny,
            curCenter[2] + stepDist * nz
        };
        MprCenterPoint = newCenter;

        var volCtx = MprService.GetVolumeContext(OriginalSeries ?? CurrentSeries!);
        if (volCtx != null)
        {
            var obliqueSlice = MprService.GenerateObliqueSlice(
                volCtx, 
                newCenter, 
                r, 
                c, 
                CurrentSlice.Columns, 
                CurrentSlice.Rows, 
                CurrentSlice.PixelSpacingX, 
                CurrentSlice.PixelSpacingY);

            if (obliqueSlice != null)
            {
                if (CurrentSeries != null && CurrentSliceIndex >= 0 && CurrentSliceIndex < CurrentSeries.Slices.Count)
                {
                    CurrentSeries.Slices[CurrentSliceIndex] = obliqueSlice;
                }
                RenderCurrentSlice();
                NotifyOverlayChanges();
            }
        }
    }

    public void NextImage()
    {
        if (CurrentSeries == null || CurrentSeries.Slices.Count == 0) return;
        int next = (CurrentSliceIndex + 1) % CurrentSeries.Slices.Count;
        SetSliceIndex(next);
    }

    public void PreviousImage()
    {
        if (CurrentSeries == null || CurrentSeries.Slices.Count == 0) return;
        int prev = CurrentSliceIndex - 1;
        if (prev < 0) prev = CurrentSeries.Slices.Count - 1;
        SetSliceIndex(prev);
    }

    public void StepImages(int delta)
    {
        if (CurrentSeries == null || CurrentSeries.Slices.Count == 0) return;
        int count = CurrentSeries.Slices.Count;
        int target = (CurrentSliceIndex + delta) % count;
        if (target < 0) target += count;
        SetSliceIndex(target);
    }

    public void AdjustWindowLevel(double deltaW, double deltaL)
    {
        WindowWidth = Math.Max(1.0, WindowWidth + deltaW);
        WindowCenter += deltaL;
        RenderCurrentSlice();
        OnPropertyChanged(nameof(WindowOverlay));
    }

    public void SetWindowPreset(double center, double width)
    {
        WindowCenter = center;
        WindowWidth = Math.Max(1.0, width);
        RenderCurrentSlice();
        OnPropertyChanged(nameof(WindowOverlay));
    }

    public void ApplyWindowPresetNumber(int number)
    {
        switch (number)
        {
            case 0: // Default DICOM
                if (CurrentSlice != null)
                {
                    WindowCenter = CurrentSlice.DefaultWindowCenter;
                    WindowWidth = CurrentSlice.DefaultWindowWidth;
                }
                break;
            case 1: // Full dynamic
                if (CurrentSlice != null)
                {
                    WindowWidth = Math.Max(1.0, CurrentSlice.MaxPixelValue - CurrentSlice.MinPixelValue);
                    WindowCenter = CurrentSlice.MinPixelValue + WindowWidth / 2.0;
                }
                break;
            case 2: // Abdomen CT
                WindowCenter = 40;
                WindowWidth = 350;
                break;
            case 3: // Angio CT
                WindowCenter = 300;
                WindowWidth = 600;
                break;
            case 4: // Bone CT
                WindowCenter = 300;
                WindowWidth = 1500;
                break;
            case 5: // Brain CT
                WindowCenter = 40;
                WindowWidth = 80;
                break;
            case 6: // Chest CT
                WindowCenter = 50;
                WindowWidth = 350;
                break;
            case 7: // Lung CT
                WindowCenter = -600;
                WindowWidth = 1500;
                break;
            case 8: // Liver CT
                WindowCenter = 60;
                WindowWidth = 160;
                break;
            case 9: // Stroke CT
                WindowCenter = 32;
                WindowWidth = 8;
                break;
        }
        RenderCurrentSlice();
        OnPropertyChanged(nameof(WindowOverlay));
    }

    public void ToggleNegative()
    {
        IsNegative = !IsNegative;
        RenderCurrentSlice();
        OnPropertyChanged(nameof(WindowOverlay));
    }

    public void RotateClockwise()
    {
        RotationAngle = (RotationAngle + 90) % 360;
    }

    public void RotateCounterClockwise()
    {
        RotationAngle = (RotationAngle - 90 + 360) % 360;
    }

    public void FlipHorizontalToggle() => FlipHorizontal = !FlipHorizontal;
    public void FlipVerticalToggle() => FlipVertical = !FlipVertical;

    public void ResetTransform()
    {
        NeedsAutoFit = true;
        PanX = 0;
        PanY = 0;
        RotationAngle = 0;
        FlipHorizontal = false;
        FlipVertical = false;
        OnPropertyChanged(nameof(Zoom));
    }

    /// <summary>
    /// Zooms the viewport to <paramref name="newZoom"/> while preserving whatever image region is currently centered in the viewport.
    /// </summary>
    public void ApplyZoomCentered(double newZoom)
    {
        double oldZoom = Math.Max(0.01, Zoom);
        newZoom = Math.Clamp(newZoom, 0.1, 20.0);
        if (Math.Abs(newZoom - oldZoom) < 0.0001) return;

        double scaleRatio = newZoom / oldZoom;
        PanX *= scaleRatio;
        PanY *= scaleRatio;
        Zoom = newZoom;
    }

    public void ToggleCine()
    {
        IsCinePlaying = !IsCinePlaying;
        if (IsCinePlaying)
            _cineTimer.Start();
        else
            _cineTimer.Stop();
    }

    public void RenderCurrentSlice()
    {
        if (CurrentSlice == null)
        {
            DisplayImage = null;
            return;
        }

        try
        {
            PixelRenderer.RenderToBitmap(CurrentSlice, WindowCenter, WindowWidth, IsNegative, ref _cachedBitmap);
            DisplayImage = _cachedBitmap;
        }
        catch
        {
            DisplayImage = null;
        }
        ImageRendered?.Invoke(this);
    }

    public void NotifyOverlayChanges()
    {
        OnPropertyChanged(nameof(CurrentSlice));
        OnPropertyChanged(nameof(TotalSlices));
        OnPropertyChanged(nameof(PatientName));
        OnPropertyChanged(nameof(PatientId));
        OnPropertyChanged(nameof(PatientIdAndAgeOverlay));
        OnPropertyChanged(nameof(SliceCommentOverlay));
        OnPropertyChanged(nameof(StudyDate));
        OnPropertyChanged(nameof(SeriesDescription));
        OnPropertyChanged(nameof(Modality));
        OnPropertyChanged(nameof(SeriesNumber));
        OnPropertyChanged(nameof(ImageIndexOverlay));
        OnPropertyChanged(nameof(SeriesNumberOverlay));
        OnPropertyChanged(nameof(SeriesTimeOverlay));
        OnPropertyChanged(nameof(WindowOverlay));
        OnPropertyChanged(nameof(ThicknessLocationOverlay));
        OnPropertyChanged(nameof(AcquisitionParamsOverlay));
        OnPropertyChanged(nameof(OrientationLeft));
        OnPropertyChanged(nameof(OrientationRight));
        OnPropertyChanged(nameof(OrientationTop));
        OnPropertyChanged(nameof(OrientationBottom));
    }
}
