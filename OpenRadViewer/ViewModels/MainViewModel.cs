// @purpose: Root ViewModel managing multi-viewport grids (1x1 to 4x4), active patient study list, scanning/import pipeline, synchronization modes, windowing presets, and top toolbar commands.
// @depends: CommunityToolkit.Mvvm, OpenRadViewer.Core.Models, OpenRadViewer.Core.Services, OpenRadViewer.Core.Config
// @exports: MainViewModel, SyncMode, HangingProtocolLayout

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenRadViewer.Core.Config;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.ViewModels;

public enum SyncMode
{
    Off,
    Automatic,
    Manual
}

public enum SpineSegment
{
    Lumbar,    // Starts at L5 upwards
    Thoracic,  // Starts at T12 upwards
    Cervical   // Starts at C7 upwards
}

public partial class MainViewModel : ObservableObject
{
    private readonly DicomLoaderService _loaderService = new();
    private readonly string _settingsPath;

    [ObservableProperty]
    private SpineSegment _selectedSpineSegment = SpineSegment.Lumbar;

    [ObservableProperty]
    private int _gridRows = 1;

    [ObservableProperty]
    private int _gridColumns = 1;

    [ObservableProperty]
    private bool _isMaximizedPanel;

    private int _preMaximizedRows = 1;
    private int _preMaximizedCols = 1;

    [ObservableProperty]
    private ViewportViewModel? _activeViewport;

    [ObservableProperty]
    private SyncMode _currentSyncMode = SyncMode.Automatic;

    public string CurrentSyncModeDisplay => CurrentSyncMode == SyncMode.Automatic ? "Auto" : CurrentSyncMode.ToString();

    public bool IsSyncEnabled => CurrentSyncMode != SyncMode.Off;

    partial void OnCurrentSyncModeChanged(SyncMode value)
    {
        OnPropertyChanged(nameof(IsSyncEnabled));
        OnPropertyChanged(nameof(CurrentSyncModeDisplay));
    }

    public bool IsSyncSuspended { get; set; }

    private readonly Dictionary<int, int> _manualSliceOffsets = new();

    [ObservableProperty]
    private bool _syncSlicePosition = true;

    [ObservableProperty]
    private bool _syncZoomPan = true;

    [ObservableProperty]
    private bool _syncWindowLevel = false;

    [ObservableProperty]
    private bool _showScoutLines = true;

    [ObservableProperty]
    private bool _showMprAxes = true;

    public void ToggleScoutLines()
    {
        ShowScoutLines = !ShowScoutLines;
        UpdateScoutLines();
        StatusMessage = $"Scout / Cross-reference lines: {(ShowScoutLines ? "ENABLED" : "DISABLED")}";
    }

    public void ToggleMprAxes()
    {
        ShowMprAxes = !ShowMprAxes;
        StatusMessage = $"MPR Reference Axes: {(ShowMprAxes ? "VISIBLE" : "HIDDEN (H)")}";
        foreach (var vp in Viewports)
        {
            vp.NotifyMprLinesChanged();
        }
    }

    public void UpdateScoutLines()
    {
        if (IsMprMode)
        {
            for (int i = 0; i < Viewports.Count; i++) Viewports[i].ShowScoutLine = false;
            UpdateMprLines();
            return;
        }

        int totalVisible = GridRows * GridColumns;

        if (!ShowScoutLines || ActiveViewport?.CurrentSlice == null)
        {
            for (int i = 0; i < totalVisible; i++) Viewports[i].ShowScoutLine = false;
            return;
        }

        var activeVp = ActiveViewport;
        var activeSlice = activeVp.CurrentSlice;

        for (int i = 0; i < totalVisible; i++)
        {
            var vp = Viewports[i];
            if (vp.CurrentSlice == null || vp == activeVp)
            {
                // Active viewport itself NEVER displays a scout line on its own slice!
                vp.ShowScoutLine = false;
                continue;
            }

            // Other viewports (e.g. sagittal and coronal if axial is selected) display the intersection line with the active viewport's slice
            var line = SynchronizationService.CalculatePlaneIntersection(activeSlice, vp.CurrentSlice);
            if (line != null)
            {
                vp.ScoutLineStart = line.Value.p1;
                vp.ScoutLineEnd = line.Value.p2;
                vp.ShowScoutLine = true;
            }
            else
            {
                vp.ShowScoutLine = false;
            }
        }
    }

    public void SetSyncMode(SyncMode mode)
    {
        CurrentSyncMode = mode;

        if (CurrentSyncMode == SyncMode.Manual && ActiveViewport != null)
        {
            LockManualSyncOffsets();
        }
        else if (CurrentSyncMode == SyncMode.Automatic && ActiveViewport != null)
        {
            OnViewportImageRendered(ActiveViewport);
            OnViewportTransformChanged(ActiveViewport);
        }

        OnPropertyChanged(nameof(IsSyncEnabled));
        StatusMessage = $"Sync mode: {CurrentSyncMode.ToString().ToUpperInvariant()}";
    }

    public void CycleSyncMode()
    {
        CurrentSyncMode = CurrentSyncMode switch
        {
            SyncMode.Off => SyncMode.Automatic,
            SyncMode.Automatic => SyncMode.Manual,
            _ => SyncMode.Off
        };

        if (CurrentSyncMode == SyncMode.Manual && ActiveViewport != null)
        {
            LockManualSyncOffsets();
        }
        else if (CurrentSyncMode == SyncMode.Automatic && ActiveViewport != null)
        {
            OnViewportImageRendered(ActiveViewport);
            OnViewportTransformChanged(ActiveViewport);
        }

        OnPropertyChanged(nameof(IsSyncEnabled));
        StatusMessage = $"Sync mode: {CurrentSyncMode.ToString().ToUpperInvariant()}";
    }

    [ObservableProperty]
    private double _browseSensitivityPixels = 6.0;

    public void LockManualSyncOffsets()
    {
        _manualSliceOffsets.Clear();
        if (ActiveViewport == null) return;
        int activeIdx = ActiveViewport.CurrentSliceIndex;

        int total = GridRows * GridColumns;
        for (int i = 0; i < total; i++)
        {
            var vp = Viewports[i];
            _manualSliceOffsets[vp.ViewportIndex] = vp.CurrentSliceIndex - activeIdx;
        }
    }

    public void DuplicateActiveRoiToOtherSeries()
    {
        if (ActiveViewport?.CurrentSlice == null || ActiveViewport.Measurements.Count == 0) return;
        var targetRoi = ActiveViewport.Measurements.FirstOrDefault(m => m.IsSelected) ?? ActiveViewport.Measurements.Last();

        int totalVisible = GridRows * GridColumns;
        int count = 0;
        for (int i = 0; i < totalVisible; i++)
        {
            var other = Viewports[i];
            if (other == ActiveViewport || other.CurrentSlice == null) continue;

            // Deselect any previous ROIs on the target viewport
            foreach (var m in other.Measurements) m.IsSelected = false;

            var copy = new MeasurementItem
            {
                Type = targetRoi.Type,
                Points = new List<Point>(targetRoi.Points),
                Label = targetRoi.Label,
                IsSelected = true,
                SliceIndex = other.CurrentSliceIndex
            };
            if (copy.Type == MeasurementType.Ellipse)
            {
                copy.ComputeEllipseStatistics(other.CurrentSlice);
            }
            else if (copy.Type == MeasurementType.Rectangle)
            {
                copy.ComputeRectangleStatistics(other.CurrentSlice);
            }
            else if (copy.Type == MeasurementType.Length)
            {
                double lenMm = copy.CalculateLengthMm(other.CurrentSlice.PixelSpacingX, other.CurrentSlice.PixelSpacingY);
                copy.Details = $"{lenMm:0.1} mm";
            }
            else if (copy.Type == MeasurementType.Angle)
            {
                double deg = copy.CalculateAngleDegrees();
                copy.Details = $"{deg:0.1}°";
            }
            else if (copy.Type == MeasurementType.InsallSalvati && other.CurrentSlice != null)
            {
                var (ratio, tl, pl) = copy.CalculateInsallSalvati(other.CurrentSlice.PixelSpacingX, other.CurrentSlice.PixelSpacingY);
                copy.Details = MeasurementItem.FormatInsallSalvatiDetails(ratio, tl, pl);
            }
            else
            {
                copy.Details = targetRoi.Details;
            }

            other.Measurements.Add(copy);
            count++;
        }
        StatusMessage = $"Duplicated selected ROI to {count} visible series viewport(s).";
    }

    public event Action<MeasurementItem>? MeasurementSelected;
    public MeasurementItem? LastSelectedMeasurement { get; private set; }

    public void NotifyMeasurementSelected(MeasurementItem measurement)
    {
        LastSelectedMeasurement = measurement;
        MeasurementSelected?.Invoke(measurement);
    }

    public void Update3DCursor(double px, double py, double pz, ViewportViewModel source)
    {
        if (IsMprMode)
        {
            Clear3DCursor();
            for (int i = 0; i < 3 && i < Viewports.Count; i++)
            {
                var vp = Viewports[i];
                if (vp != source && vp.MprPlane.HasValue)
                {
                    TranslateMprPlane(vp.MprPlane.Value, px, py, pz);
                }
            }
            UpdateMprLines();
            return;
        }

        int totalVisible = GridRows * GridColumns;
        for (int i = 0; i < totalVisible; i++)
        {
            var vp = Viewports[i];
            if (vp.CurrentSeries == null) continue;

            var proj = SynchronizationService.ProjectPointToSeries(px, py, pz, vp.CurrentSeries);
            if (proj != null)
            {
                var (sliceIndex, pixelX, pixelY, inBounds) = proj.Value;
                if (vp != source && sliceIndex != vp.CurrentSliceIndex)
                {
                    vp.SetSliceIndex(sliceIndex);
                }
                vp.Crosshair3DPoint = new Point(pixelX, pixelY);
                vp.ShowCrosshair3D = true;
            }
        }
    }

    public void Clear3DCursor()
    {
        foreach (var vp in Viewports)
        {
            vp.ShowCrosshair3D = false;
            vp.Crosshair3DPoint = null;
        }
    }

    [ObservableProperty]
    private bool _showSeriesPreviewBar = true;

    [ObservableProperty]
    private bool _showToolbars = true;

    [ObservableProperty]
    private bool _showMenuBar = true;

    public bool IsMenuBarVisible => ShowMenuBar && !IsFullScreen;

    partial void OnShowMenuBarChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMenuBarVisible));
    }

    [RelayCommand]
    public void ToggleMenuBar()
    {
        ShowMenuBar = !ShowMenuBar;
        StatusMessage = $"Main Menu Bar: {(ShowMenuBar ? "SHOWN" : "HIDDEN (Ctrl+Alt+M)")}";
    }

    [ObservableProperty]
    private bool _isMprMode;

    [ObservableProperty]
    private bool _isFullScreen;

    partial void OnIsFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMenuBarVisible));
    }

    [ObservableProperty]
    private MouseToolAction _currentTool = MouseToolAction.BrowseSeries;

    [ObservableProperty]
    private MouseToolAction _leftTool = MouseToolAction.BrowseSeries;

    [ObservableProperty]
    private MouseToolAction _rightTool = MouseToolAction.Pan;

    [ObservableProperty]
    private MouseToolAction _middleTool = MouseToolAction.WindowLevel;

    public void AssignLeftTool(MouseToolAction tool)
    {
        LeftTool = tool;
        CurrentTool = tool;
        MouseConfig.SetActionFor(MouseInputTrigger.LeftDrag, tool);
        try { MouseConfig.SaveToFile(_settingsPath); } catch { }
        StatusMessage = $"Left-click tool set to: {tool}";
    }

    public void AssignRightTool(MouseToolAction tool)
    {
        RightTool = tool;
        MouseConfig.SetActionFor(MouseInputTrigger.RightDrag, tool);
        try { MouseConfig.SaveToFile(_settingsPath); } catch { }
        StatusMessage = $"Right-click tool set to: {tool}";
    }

    public void AssignMiddleTool(MouseToolAction tool)
    {
        MiddleTool = tool;
        MouseConfig.SetActionFor(MouseInputTrigger.MiddleDrag, tool);
        try { MouseConfig.SaveToFile(_settingsPath); } catch { }
        StatusMessage = $"Hold Wheel tool set to: {tool}";
    }

    [ObservableProperty]
    private string _statusMessage = "Ready. Press Ctrl+Shift+O or drag & drop a DICOM folder.";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _filterQuery = string.Empty;

    public ObservableCollection<PatientModel> Patients { get; } = new();
    public ObservableCollection<SeriesModel> FilteredSeries { get; } = new();
    public ObservableCollection<PatientGroupModel> GroupedPatientStudies { get; } = new();
    public ObservableCollection<ViewportViewModel> Viewports { get; } = new();
    public ObservableCollection<ViewportViewModel> DisplayedViewports { get; } = new();

    public MouseSettings MouseConfig { get; set; }

    [ObservableProperty]
    private bool _isProtocolsPanelOpen;

    [ObservableProperty]
    private string _protocolsText = string.Empty;

    [ObservableProperty]
    private string _protocolsStatusMessage = string.Empty;

    public IReadOnlyList<ClinicalProtocol> ClinicalProtocols => ClinicalProtocolsService.Protocols;

    [ObservableProperty]
    private ClinicalProtocol? _selectedClinicalProtocol;

    [ObservableProperty]
    private bool _isVoiceRecording = false;

    [ObservableProperty]
    private string _voiceRecordingButtonText = "Dictate";

    [ObservableProperty]
    private string _voiceRecordingDurationText = "";

    [ObservableProperty]
    private bool _isAiFormatting = false;

    [ObservableProperty]
    private string _aiFormatButtonText = "Format AI";

    [ObservableProperty]
    private bool _isVoiceRefineRecording = false;

    [ObservableProperty]
    private string _voiceRefineButtonText = "F6 Speak";

    [ObservableProperty]
    private bool _isAiRefining = false;

    private readonly string _protocolsPath;

    public MainViewModel()
    {
        _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mousesettings.json");
        MouseConfig = MouseSettings.LoadFromFile(_settingsPath);
        _measurementsCurrentSliceOnly = MouseConfig.MeasurementsCurrentSliceOnly;
        _invertScrolling = MouseConfig.InvertScrolling;

        var cfgLeft = MouseConfig.GetActionFor(MouseInputTrigger.LeftDrag);
        if (cfgLeft != MouseToolAction.None) LeftTool = cfgLeft;
        var cfgRight = MouseConfig.GetActionFor(MouseInputTrigger.RightDrag);
        if (cfgRight != MouseToolAction.None) RightTool = cfgRight;
        var cfgMiddle = MouseConfig.GetActionFor(MouseInputTrigger.MiddleDrag);
        if (cfgMiddle != MouseToolAction.None) MiddleTool = cfgMiddle;

        _protocolsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "protocols.txt");
        SelectedClinicalProtocol = ClinicalProtocols.FirstOrDefault();
        // Editor starts empty so the doctor has a clean reporting workspace.
        ProtocolsText = string.Empty;

        SpeechRecognitionService.Instance.RecordingDurationChanged += sec =>
        {
            VoiceRecordingDurationText = $"{sec / 60:D2}:{sec % 60:D2}";
        };
        SpeechRecognitionService.Instance.StatusChanged += msg =>
        {
            ProtocolsStatusMessage = msg;
        };

        // Initialize with 1x1 grid
        SetGridLayout(1, 1);
    }

    public void LoadProtocols()
    {
        try
        {
            if (File.Exists(_protocolsPath))
            {
                ProtocolsText = File.ReadAllText(_protocolsPath);
            }
            else
            {
                string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parsec", "protocols.txt");
                if (File.Exists(appDataPath))
                {
                    ProtocolsText = File.ReadAllText(appDataPath);
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load protocols: {ex.Message}";
        }
    }

    public void SaveProtocols()
    {
        try
        {
            File.WriteAllText(_protocolsPath, ProtocolsText ?? string.Empty);
            ProtocolsStatusMessage = $"Saved {DateTime.Now:HH:mm:ss}";
            StatusMessage = "Protocols document saved successfully.";
        }
        catch
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parsec");
                Directory.CreateDirectory(dir);
                string appDataPath = Path.Combine(dir, "protocols.txt");
                File.WriteAllText(appDataPath, ProtocolsText ?? string.Empty);
                ProtocolsStatusMessage = $"Saved {DateTime.Now:HH:mm:ss}";
                StatusMessage = "Protocols saved to AppData.";
            }
            catch (Exception ex)
            {
                ProtocolsStatusMessage = "Error saving";
                StatusMessage = $"Error saving protocols: {ex.Message}";
            }
        }
    }

    public void ToggleProtocolsPanel()
    {
        IsProtocolsPanelOpen = !IsProtocolsPanelOpen;
        StatusMessage = IsProtocolsPanelOpen ? "Protocols panel opened." : "Protocols panel closed.";
    }

    public (PatientModel? Patient, StudyModel? Study, SeriesModel? Series) GetCurrentPatientContext()
    {
        var active = ActiveViewport;
        var currentSeries = active?.CurrentSeries ?? active?.OriginalSeries ?? FilteredSeries.FirstOrDefault();
        PatientModel? patient = null;
        StudyModel? study = null;

        if (currentSeries != null && Patients != null)
        {
            foreach (var p in Patients)
            {
                foreach (var st in p.Studies)
                {
                    if (st.Series.Contains(currentSeries) || st.Series.Any(s => s.SeriesInstanceUid == currentSeries.SeriesInstanceUid))
                    {
                        patient = p;
                        study = st;
                        break;
                    }
                }
                if (patient != null) break;
            }
        }

        if (patient == null && Patients != null && Patients.Count > 0)
        {
            patient = Patients[0];
            study = patient.Studies.FirstOrDefault();
        }

        if (patient == null && currentSeries != null)
        {
            patient = new PatientModel
            {
                Name = currentSeries.PatientName,
                Id = currentSeries.PatientId,
                BirthDate = currentSeries.StudyDate
            };
        }

        if (study == null && currentSeries != null)
        {
            study = new StudyModel
            {
                StudyDescription = currentSeries.StudyDescription,
                StudyDate = currentSeries.StudyDate,
                Series = new List<SeriesModel> { currentSeries }
            };
        }

        return (patient, study, currentSeries);
    }

    public string GenerateFullProtocolReport(ClinicalProtocol? protocol = null)
    {
        var targetProto = protocol ?? SelectedClinicalProtocol ?? ClinicalProtocols.FirstOrDefault();
        if (targetProto == null) return string.Empty;

        var (patient, study, currentSeries) = GetCurrentPatientContext();
        return ClinicalProtocolsService.GenerateFullReport(targetProto, patient, study, currentSeries);
    }

    public string BuildCompleteReportForDocxExport(string editorText)
    {
        var (patient, study, currentSeries) = GetCurrentPatientContext();
        return ClinicalProtocolsService.BuildCompleteReportForExport(
            editorText, SelectedClinicalProtocol, patient, study, currentSeries);
    }

    public string GetSelectedProtocolSnippet(ClinicalProtocol? protocol = null)
    {
        var targetProto = protocol ?? SelectedClinicalProtocol ?? ClinicalProtocols.FirstOrDefault();
        if (targetProto == null) return string.Empty;
        return ClinicalProtocolsService.GetProtocolInsertText(targetProto);
    }

    public async Task<string> ToggleVoiceRecordingAsync()
    {
        if (!SpeechRecognitionService.Instance.IsRecording)
        {
            bool ok = SpeechRecognitionService.Instance.StartRecording();
            if (ok)
            {
                IsVoiceRecording = true;
                VoiceRecordingButtonText = "● Stop & Insert";
                VoiceRecordingDurationText = "00:00";
            }
            return string.Empty;
        }
        else
        {
            IsVoiceRecording = false;
            VoiceRecordingButtonText = "Dictate";
            VoiceRecordingDurationText = "";
            ProtocolsStatusMessage = "Transcribing voice...";
            string transcribed = await SpeechRecognitionService.Instance.StopRecordingAndTranscribeAsync();
            return transcribed;
        }
    }

    public async Task<string> FormatDictatedProtocolWithAiAsync(string currentText)
    {
        if (IsAiFormatting) return string.Empty;

        var (patient, study, currentSeries) = GetCurrentPatientContext();

        // Auto-detect best matching protocol based on dictated text and current study
        var detectedProto = ClinicalProtocolsService.DetectBestProtocol(currentText, study, currentSeries, SelectedClinicalProtocol);
        if (detectedProto != null)
        {
            SelectedClinicalProtocol = detectedProto;
        }

        var targetProto = SelectedClinicalProtocol ?? ClinicalProtocols.FirstOrDefault();
        if (targetProto == null) return string.Empty;

        try
        {
            IsAiFormatting = true;
            AiFormatButtonText = "Formatting...";
            string formatted = await SpeechRecognitionService.Instance.FormatDictationToProtocolAsync(
                currentText, targetProto, patient, study, currentSeries);
            return formatted;
        }
        finally
        {
            IsAiFormatting = false;
            AiFormatButtonText = "Format AI";
        }
    }

    public async Task<string> ToggleVoiceRefineRecordingAsync(string currentReportText)
    {
        if (!SpeechRecognitionService.Instance.IsRecording)
        {
            bool ok = SpeechRecognitionService.Instance.StartRecording();
            if (ok)
            {
                IsVoiceRefineRecording = true;
                VoiceRefineButtonText = "● Stop";
                VoiceRecordingDurationText = "00:00";
                ProtocolsStatusMessage = "Speak what you want modified in report (F6 to stop)...";
            }
            return string.Empty;
        }
        else
        {
            IsVoiceRefineRecording = false;
            VoiceRefineButtonText = "F6 Speak";
            VoiceRecordingDurationText = "";
            try
            {
                IsAiRefining = true;
                string updated = await SpeechRecognitionService.Instance.StopRecordingAndRefineReportAsync(
                    currentReportText, SelectedClinicalProtocol);
                return updated;
            }
            finally
            {
                IsAiRefining = false;
            }
        }
    }

    public async Task<string> ApplyTextRefinementWithAiAsync(string currentReportText, string instruction)
    {
        if (IsAiRefining || string.IsNullOrWhiteSpace(instruction)) return string.Empty;

        try
        {
            IsAiRefining = true;
            string updated = await SpeechRecognitionService.Instance.RefineReportWithTextAsync(
                currentReportText, instruction, SelectedClinicalProtocol);
            return updated;
        }
        finally
        {
            IsAiRefining = false;
        }
    }

    public void UpdateDisplayedViewports()
    {
        DisplayedViewports.Clear();
        if (IsMaximizedPanel && ActiveViewport != null)
        {
            DisplayedViewports.Add(ActiveViewport);
        }
        else
        {
            int total = GridRows * GridColumns;
            for (int i = 0; i < total && i < Viewports.Count; i++)
            {
                DisplayedViewports.Add(Viewports[i]);
            }
        }
        foreach (var vp in Viewports)
        {
            vp.NotifyCloseButtonVisibilityChanged();
        }
    }

    public void SetGridLayout(int rows, int cols)
    {
        rows = Math.Clamp(rows, 1, 5);
        cols = Math.Clamp(cols, 1, 6);

        GridRows = rows;
        GridColumns = cols;
        int totalNeeded = rows * cols;

        // Ensure we have totalNeeded viewports
        while (Viewports.Count < totalNeeded)
        {
            var vp = new ViewportViewModel(Viewports.Count);
            vp.ImageRendered += OnViewportImageRendered;
            vp.TransformChanged += OnViewportTransformChanged;
            Viewports.Add(vp);
        }

        // Deactivate unused viewports if grid shrunk
        for (int i = 0; i < Viewports.Count; i++)
        {
            // Viewports are kept in memory for quick switching
        }

        IsMaximizedPanel = false;
        UpdateDisplayedViewports();

        if (ActiveViewport == null || ActiveViewport.ViewportIndex >= totalNeeded)
        {
            SetActiveViewport(Viewports[0]);
        }
    }

    [ObservableProperty]
    private bool _measurementsCurrentSliceOnly = true;

    partial void OnMeasurementsCurrentSliceOnlyChanged(bool value)
    {
        if (MouseConfig != null)
        {
            MouseConfig.MeasurementsCurrentSliceOnly = value;
            try { MouseConfig.SaveToFile(_settingsPath); } catch { }
        }
    }

    [ObservableProperty]
    private bool _invertScrolling = false;

    partial void OnInvertScrollingChanged(bool value)
    {
        if (MouseConfig != null)
        {
            MouseConfig.InvertScrolling = value;
            try { MouseConfig.SaveToFile(_settingsPath); } catch { }
        }
    }

    private readonly HashSet<string> _smartLayoutAppliedStudies = new();

    public void ApplySmartLayout(int rows, int cols)
    {
        SetGridLayout(rows, cols);

        var curSeries = ActiveViewport?.CurrentSeries ?? FilteredSeries.FirstOrDefault();
        if (curSeries == null) return;

        string studyKey = !string.IsNullOrEmpty(curSeries.StudyInstanceUid) 
            ? curSeries.StudyInstanceUid 
            : curSeries.PatientId;

        bool applied = false;

        // Filter series to the currently active study so other loaded patients/studies don't interfere
        var studySeries = FilteredSeries
            .Where(s => (!string.IsNullOrEmpty(curSeries.StudyInstanceUid) && s.StudyInstanceUid == curSeries.StudyInstanceUid) ||
                        (string.IsNullOrEmpty(curSeries.StudyInstanceUid) && s.PatientId == curSeries.PatientId))
            .ToList();
        if (studySeries.Count == 0) studySeries = FilteredSeries.ToList();

        var nonScoutSeries = studySeries.Where(s => !s.IsScoutOrFewSlices).ToList();
        if (nonScoutSeries.Count == 0) nonScoutSeries = studySeries.ToList();

        // 1. Check if study is Knee MRI (IRM Genunchi)
        bool isKnee = (curSeries.Modality == "MR") && (
            (curSeries.BodyPart?.Contains("KNEE", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (curSeries.BodyPart?.Contains("GENU", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (curSeries.StudyDescription?.Contains("GENUNCH", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (curSeries.StudyDescription?.Contains("GENU", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (curSeries.StudyDescription?.Contains("KNEE", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (curSeries.SeriesDescription?.Contains("GENUNCH", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (curSeries.SeriesDescription?.Contains("GENU", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (curSeries.SeriesDescription?.Contains("KNEE", StringComparison.OrdinalIgnoreCase) ?? false) ||
            studySeries.Any(s => 
                (s.StudyDescription?.Contains("GENU", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("GENUNCH", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("KNEE", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.SeriesDescription?.Contains("GENU", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.SeriesDescription?.Contains("GENUNCH", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.SeriesDescription?.Contains("KNEE", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.BodyPart?.Contains("KNEE", StringComparison.OrdinalIgnoreCase) ?? false)));

        // If not explicitly labeled as Knee in tags, check if study contains the classic 3-plane PD series
        if (!isKnee && curSeries.Modality == "MR")
        {
            bool hasAxPd = nonScoutSeries.Any(s => (s.SeriesDescription.Contains("AX", StringComparison.OrdinalIgnoreCase) || s.SeriesDescription.Contains("TRA", StringComparison.OrdinalIgnoreCase)) && s.SeriesDescription.Contains("PD", StringComparison.OrdinalIgnoreCase));
            bool hasCorPd = nonScoutSeries.Any(s => s.SeriesDescription.Contains("COR", StringComparison.OrdinalIgnoreCase) && s.SeriesDescription.Contains("PD", StringComparison.OrdinalIgnoreCase));
            bool hasSagPd = nonScoutSeries.Any(s => s.SeriesDescription.Contains("SAG", StringComparison.OrdinalIgnoreCase) && s.SeriesDescription.Contains("PD", StringComparison.OrdinalIgnoreCase));
            if (hasAxPd && hasCorPd && hasSagPd)
            {
                isKnee = true;
            }
        }

        // 2. Check if study is Spine MRI / CT
        bool isSpine = !isKnee && (
            studySeries.Any(s =>
                (s.BodyPart?.Contains("SPINE", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.BodyPart?.Contains("LSPINE", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.BodyPart?.Contains("CSPINE", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.BodyPart?.Contains("TSPINE", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("COLOAN", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("SPIN", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("LOMB", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("LUMB", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("CERV", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("TORAC", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("THORAC", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("DORSAL", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("VERTEB", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.StudyDescription?.Contains("SACR", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.SeriesDescription?.Contains("SPINE", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.SeriesDescription?.Contains("COLOAN", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.SeriesDescription?.Contains("LOMB", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.SeriesDescription?.Contains("CERV", StringComparison.OrdinalIgnoreCase) ?? false)) ||
            (curSeries.Modality == "MR" &&
             FindBestSpineSagittalSeries(nonScoutSeries, SpineSequenceRole.T1) != null &&
             FindBestSpineSagittalSeries(nonScoutSeries, SpineSequenceRole.T2OrT2Iso) != null &&
             FindBestSpineSagittalSeries(nonScoutSeries, SpineSequenceRole.Stir) != null));

        // 3. Check if study is Abdomen CT
        bool isAbdomen = !isKnee && !isSpine && (curSeries.Modality == "CT") && (
                         (curSeries.BodyPart?.Contains("ABDOM", StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (curSeries.StudyDescription?.Contains("ABDOM", StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (curSeries.StudyDescription?.Contains("PELV", StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (curSeries.StudyDescription?.Contains("TORACE-ABDOMEN", StringComparison.OrdinalIgnoreCase) ?? false) ||
                         StudyModel.IsContrastSeries(curSeries.SeriesDescription, curSeries.ContrastBolusAgent));

        if (isKnee)
        {
            if (rows == 1 && cols == 3) // X+3: Axial PD/FS, Coronal PD/FS, Sagittal PD/FS
            {
                var sAx = FindBestKneeSeries(nonScoutSeries, OrthogonalPlane.Axial, preferPdFs: true);
                var sCor = FindBestKneeSeries(nonScoutSeries, OrthogonalPlane.Coronal, preferPdFs: true);
                var sSag = FindBestKneeSeries(nonScoutSeries, OrthogonalPlane.Sagittal, preferPdFs: true);

                if (sAx != null && Viewports.Count > 0) Viewports[0].LoadSeries(sAx);
                if (sCor != null && Viewports.Count > 1) Viewports[1].LoadSeries(sCor);
                if (sSag != null && Viewports.Count > 2) Viewports[2].LoadSeries(sSag);
                applied = true;
            }
            else if (rows == 2 && cols == 2) // X+4: Axial PD/FS, Coronal PD/FS, Sagittal PD/FS, T1 Sag/Cor
            {
                var sAx = FindBestKneeSeries(nonScoutSeries, OrthogonalPlane.Axial, preferPdFs: true);
                var sCor = FindBestKneeSeries(nonScoutSeries, OrthogonalPlane.Coronal, preferPdFs: true);
                var sSag = FindBestKneeSeries(nonScoutSeries, OrthogonalPlane.Sagittal, preferPdFs: true);
                var sT1 = FindBestKneeSeries(nonScoutSeries, OrthogonalPlane.Sagittal, preferPdFs: false) ??
                          FindBestKneeSeries(nonScoutSeries, OrthogonalPlane.Coronal, preferPdFs: false) ??
                          nonScoutSeries.FirstOrDefault(s => s != sAx && s != sCor && s != sSag);

                if (sAx != null && Viewports.Count > 0) Viewports[0].LoadSeries(sAx);
                if (sCor != null && Viewports.Count > 1) Viewports[1].LoadSeries(sCor);
                if (sSag != null && Viewports.Count > 2) Viewports[2].LoadSeries(sSag);
                if (sT1 != null && Viewports.Count > 3) Viewports[3].LoadSeries(sT1);
                applied = true;
            }
            else if (rows == 1 && cols == 2) // X+2: Coronal PD/FS, Sagittal PD/FS
            {
                var sCor = FindBestKneeSeries(nonScoutSeries, OrthogonalPlane.Coronal, preferPdFs: true);
                var sSag = FindBestKneeSeries(nonScoutSeries, OrthogonalPlane.Sagittal, preferPdFs: true);

                if (sCor != null && Viewports.Count > 0) Viewports[0].LoadSeries(sCor);
                if (sSag != null && Viewports.Count > 1) Viewports[1].LoadSeries(sSag);
                applied = true;
            }
        }
        else if (isSpine)
        {
            if (rows == 1 && cols == 3) // X+3: [0] T1 Sagittal, [1] T2 (or T2 iso) Sagittal, [2] STIR Sagittal
            {
                var sT1 = FindBestSpineSagittalSeries(nonScoutSeries, SpineSequenceRole.T1);
                var sT2 = FindBestSpineSagittalSeries(nonScoutSeries, SpineSequenceRole.T2OrT2Iso, sT1);
                var sStir = FindBestSpineSagittalSeries(nonScoutSeries, SpineSequenceRole.Stir, sT1, sT2);

                if (sT1 != null && Viewports.Count > 0) Viewports[0].LoadSeries(EnsureSagittalSeries(sT1));
                if (sT2 != null && Viewports.Count > 1) Viewports[1].LoadSeries(EnsureSagittalSeries(sT2));
                if (sStir != null && Viewports.Count > 2) Viewports[2].LoadSeries(EnsureSagittalSeries(sStir));
                applied = true;
            }
            else if (rows == 1 && cols == 2) // X+2: T2 Sag (iso preferred) and Axial
            {
                var sT2Sag = FindBestSpineSagittalSeries(nonScoutSeries, SpineSequenceRole.T2OrT2Iso);
                var sAx = nonScoutSeries.FirstOrDefault(s => !IsSagittalPlaneSeries(s) &&
                    ((s.Slices.Count > 0 && s.Slices[0].ImageOrientationPatient is { Length: >= 6 } && MprService.DetectOrientation(s.Slices[0]) == OrthogonalPlane.Axial) ||
                     s.SeriesDescription.Contains("AX", StringComparison.OrdinalIgnoreCase) ||
                     s.SeriesDescription.Contains("TRA", StringComparison.OrdinalIgnoreCase)));

                if (sT2Sag != null && Viewports.Count > 0) Viewports[0].LoadSeries(EnsureSagittalSeries(sT2Sag));
                if (sAx != null && Viewports.Count > 1) Viewports[1].LoadSeries(sAx);
                applied = true;
            }
        }
        else if (isAbdomen)
        {
            var diagCandidates = nonScoutSeries
                .Where(s =>
                {
                    string d = (s.SeriesDescription ?? string.Empty).ToUpperInvariant();
                    return !d.Contains("SMART") && !d.Contains("PREP") && !d.Contains("BOLUS") &&
                           !d.Contains("MONITOR") && !d.Contains("DOSE") && !d.Contains("TRACK");
                })
                .ToList();

            if (diagCandidates.Any(s => s.SliceCount >= 20))
            {
                diagCandidates = diagCandidates.Where(s => s.SliceCount >= 20).ToList();
            }
            if (diagCandidates.Count == 0) diagCandidates = nonScoutSeries;

            int refSlices = curSeries.SliceCount >= 20 ? curSeries.SliceCount : 0;

            SeriesModel? PickBestPhase(Func<string, bool> matcher, params SeriesModel?[] alreadyPicked)
            {
                var matches = diagCandidates
                    .Where(s => !alreadyPicked.Contains(s) && matcher((s.SeriesDescription ?? string.Empty).ToUpperInvariant()))
                    .OrderByDescending(s =>
                    {
                        string d = (s.SeriesDescription ?? string.Empty).ToUpperInvariant();
                        bool isAxial = !d.Contains("COR") && !d.Contains("SAG");
                        int score = isAxial ? 1000 : 0;
                        if (refSlices > 0 && Math.Abs(s.SliceCount - refSlices) <= 5) score += 500;
                        return score;
                    })
                    .ToList();
                return matches.FirstOrDefault();
            }

            var sNative = PickBestPhase(d =>
                d.Contains("NATIV") || d.Contains("NON-CONTRAST") || d.Contains("PLAIN") ||
                d.Contains("C-") || Regex.IsMatch(d, @"(^|[^A-Z])PRE([^A-Z]|$)"));

            if (sNative != null && refSlices == 0) refSlices = sNative.SliceCount;

            var sArt = PickBestPhase(d =>
                !d.Contains("SMART") && (
                    d.Contains("ARTER") ||
                    Regex.IsMatch(d, @"(^|[^A-Z])(ART|EARLY)([^A-Z]|$)") ||
                    d.Contains("LATE ART")),
                sNative);

            var sVen = PickBestPhase(d =>
                d.Contains("VEN") || d.Contains("PORTAL") || Regex.IsMatch(d, @"(^|[^A-Z])PV([^A-Z]|$)"),
                sNative, sArt);

            var sDel = PickBestPhase(d =>
                d.Contains("TARD") || d.Contains("DELAY") || d.Contains("3 MIN") || d.Contains("4 MIN") ||
                d.Contains("4MIN") || d.Contains("5 MIN") || d.Contains("EQUILIBR") || d.Contains("EXCRET"),
                sNative, sArt, sVen);

            if (rows == 2 && cols == 2) // X+4
            {
                if (sNative != null && Viewports.Count > 0) Viewports[0].LoadSeries(sNative);
                if (sArt != null && Viewports.Count > 1) Viewports[1].LoadSeries(sArt);
                if (sVen != null && Viewports.Count > 2) Viewports[2].LoadSeries(sVen);
                if (sDel != null && Viewports.Count > 3) Viewports[3].LoadSeries(sDel);
                applied = true;
            }
            else if (rows == 1 && cols == 3) // X+3
            {
                if (sNative != null && Viewports.Count > 0) Viewports[0].LoadSeries(sNative);
                if (sArt != null && Viewports.Count > 1) Viewports[1].LoadSeries(sArt);
                if (sVen != null && Viewports.Count > 2) Viewports[2].LoadSeries(sVen);
                applied = true;
            }
        }

        if (applied && !string.IsNullOrEmpty(studyKey))
        {
            string protocolName = isKnee ? "Knee MRI (PD/FS Ax, Cor, Sag)" : (isSpine ? "Spine MRI (T1, T2/ISO, STIR Sagittal)" : "Abdomen");
            StatusMessage = $"Smart Layout applied for {protocolName} protocol ({rows}×{cols}).";
        }
    }

    private enum SpineSequenceRole
    {
        T1,
        T2OrT2Iso,
        Stir
    }

    private static bool IsSagittalPlaneSeries(SeriesModel s)
    {
        string desc = (s.SeriesDescription ?? "").ToUpperInvariant();
        bool hasAxLabel = Regex.IsMatch(desc, @"(^|[^A-Z0-9])(AX|AXI|AXIAL|TRA|TRANS)([^A-Z0-9]|$)");
        bool hasCorLabel = Regex.IsMatch(desc, @"(^|[^A-Z0-9])(COR|CORONAL)([^A-Z0-9]|$)");
        bool hasSagLabel = desc.Contains("SAG");

        if (s.Slices.Count > 0 && s.Slices[0].ImageOrientationPatient is { Length: >= 6 })
        {
            var plane = MprService.DetectOrientation(s.Slices[0]);
            if (plane == OrthogonalPlane.Sagittal) return true;
            if (plane != OrthogonalPlane.Sagittal && !hasSagLabel) return false;
        }

        return hasSagLabel && !hasAxLabel && !hasCorLabel;
    }

    private static SeriesModel EnsureSagittalSeries(SeriesModel s)
    {
        if (s.Slices.Count >= 3 && s.Slices[0].ImageOrientationPatient is { Length: >= 6 })
        {
            var plane = MprService.DetectOrientation(s.Slices[0]);
            if (plane != OrthogonalPlane.Sagittal)
            {
                return MprService.GenerateOrthogonalSeries(s, OrthogonalPlane.Sagittal) ?? s;
            }
        }
        return s;
    }

    private static SeriesModel? FindBestSpineSagittalSeries(List<SeriesModel> candidates, SpineSequenceRole role, params SeriesModel?[] exclude)
    {
        var valid = candidates.Where(s => !exclude.Contains(s) && !s.IsScoutOrFewSlices && s.Slices.Count >= 3).ToList();
        if (valid.Count == 0) valid = candidates.Where(s => !exclude.Contains(s) && !s.IsScoutOrFewSlices).ToList();

        SeriesModel? best = null;
        int bestScore = -1;

        foreach (var s in valid)
        {
            string desc = (s.SeriesDescription ?? "").ToUpperInvariant();

            if (desc.Contains("SCOUT") || desc.Contains("LOCALIZER") || desc.Contains("SURV") ||
                desc.Contains("TOPOGRAM") || desc.Contains("SCANOGRAM") || desc.Contains("POSDISP"))
            {
                continue;
            }

            bool isSag = IsSagittalPlaneSeries(s);
            bool isIso3D = desc.Contains("ISO") || desc.Contains("SPACE") || desc.Contains("CUBE") ||
                           desc.Contains("VISTA") || desc.Contains("3D");

            // Must be Sagittal (or a 3D isotropic volume that can be resliced to Sagittal for T2)
            if (!isSag && !(role == SpineSequenceRole.T2OrT2Iso && isIso3D && s.Slices.Count >= 25))
            {
                continue;
            }

            bool hasStirOrTirm = desc.Contains("STIR") || desc.Contains("TIRM") ||
                                 Regex.IsMatch(desc, @"(^|[^A-Z0-9])IR([^A-Z0-9]|$)");
            bool hasFs = desc.Contains("FS") || desc.Contains("FAT") || desc.Contains("SPAIR") ||
                         desc.Contains("SPIR") || desc.Contains("FATSAT") ||
                         Regex.IsMatch(desc, @"(^|[^A-Z0-9])SAT([^A-Z0-9]|$)");
            bool isDixonWater = desc.Contains("DIXON") &&
                                (desc.EndsWith("_W") || desc.Contains("_W_") || desc.Contains(" WATER") || desc.Contains("_WATER") || desc.EndsWith(" W"));
            bool isDixonFatOrOpp = desc.Contains("DIXON") &&
                                   (desc.EndsWith("_F") || desc.Contains("_F_") || desc.Contains(" FAT") || desc.Contains("_OPP") || desc.Contains("OUT"));

            bool hasT1 = desc.Contains("T1") && !desc.Contains("T2");
            bool hasT2 = desc.Contains("T2") || (!hasT1 && (desc.Contains("SPACE") || desc.Contains("VISTA") || desc.Contains("CUBE")));

            int score = 0;

            switch (role)
            {
                case SpineSequenceRole.T1:
                    if (!hasT1 || hasStirOrTirm || isDixonFatOrOpp) continue;
                    score += 100;
                    if (isSag) score += 60;
                    if (!hasFs && !StudyModel.IsContrastSeries(desc, s.ContrastBolusAgent)) score += 40;
                    if (desc.Contains("TSE") || desc.Contains("FSE") || desc.Contains("SE") || desc.Contains("FLAIR")) score += 20;
                    if (desc.Contains("SAG")) score += 15;
                    break;

                case SpineSequenceRole.T2OrT2Iso:
                    if (!hasT2 || hasStirOrTirm || hasFs || isDixonWater || isDixonFatOrOpp) continue;
                    score += 100;
                    if (isSag) score += 60;
                    if (isIso3D) score += 50; // Prefer T2 ISO / SPACE / CUBE / VISTA when available
                    if (desc.Contains("TSE") || desc.Contains("FSE") || desc.Contains("FRFSE")) score += 25;
                    if (desc.Contains("SAG")) score += 15;
                    break;

                case SpineSequenceRole.Stir:
                    if (hasStirOrTirm)
                    {
                        score += 120;
                    }
                    else if (hasT2 && (hasFs || isDixonWater))
                    {
                        score += 100;
                    }
                    else
                    {
                        continue;
                    }
                    if (isSag) score += 60;
                    if (desc.Contains("SAG")) score += 15;
                    break;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }

        return best;
    }

    private static SeriesModel? FindBestKneeSeries(List<SeriesModel> candidates, OrthogonalPlane targetPlane, bool preferPdFs = true)
    {
        var valid = candidates.Where(s => s.Slices.Count >= 4 && !s.IsScoutOrFewSlices).ToList();
        if (valid.Count == 0) valid = candidates.Where(s => !s.IsScoutOrFewSlices).ToList();
        if (valid.Count == 0) valid = candidates;

        SeriesModel? best = null;
        int bestScore = -1;

        foreach (var s in valid)
        {
            string desc = (s.SeriesDescription ?? "").ToUpperInvariant();
            int score = 0;

            // 1. Plane orientation match (Priority A: geometric IOP normal vector; Priority B: series description string)
            bool geomMatch = s.Slices.Count > 0 && s.Slices[0].ImageOrientationPatient != null &&
                             s.Slices[0].ImageOrientationPatient.Length >= 6 &&
                             MprService.DetectOrientation(s.Slices[0]) == targetPlane;

            bool nameMatch = targetPlane switch
            {
                OrthogonalPlane.Axial => desc.Contains("AX") || desc.Contains("TRA") || desc.Contains("TRANS"),
                OrthogonalPlane.Coronal => desc.Contains("COR"),
                OrthogonalPlane.Sagittal => desc.Contains("SAG"),
                _ => false
            };

            if (geomMatch) score += 50;
            if (nameMatch) score += 40;

            if (!geomMatch && !nameMatch) continue; // Must be in the requested plane

            // Heavily penalize scouts/localizers
            if (s.IsScoutOrFewSlices || desc.Contains("LOCALIZER") || desc.Contains("SCOUT") || desc.Contains("SURVEY"))
            {
                score -= 100;
            }

            // 2. Sequence characteristics (PD FS vs T1)
            bool hasPd = desc.Contains("PD") || desc.Contains("PROTON") || desc.Contains("DP");
            bool hasFs = desc.Contains("FS") || desc.Contains("FAT") || desc.Contains("SPAIR") ||
                         desc.Contains("SPIR") || desc.Contains("SAT") || desc.Contains("FATSAT") ||
                         desc.Contains("TIRM") || desc.Contains("DIXON") || desc.Contains(" W") || desc.Contains("_W");

            if (preferPdFs)
            {
                if (hasPd && hasFs) score += 60; // Exact match: PD FS / PD SPAIR / PD Fat Sat
                else if (hasPd) score += 35;     // Plain PD
                else if (hasFs && (desc.Contains("T2") || desc.Contains("STIR"))) score += 30; // Fluid-sensitive fat-suppressed fallback
                else if (desc.Contains("T2")) score += 20;
                else if (desc.Contains("T1")) score += 5;
            }
            else
            {
                // Prefer non-fat-suppressed T1 (e.g. SAG T1 TSE)
                if (desc.Contains("T1") && !hasFs) score += 60;
                else if (desc.Contains("T1")) score += 40;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }

        return best;
    }

    public void SetActiveViewport(ViewportViewModel vp)
    {
        if (ActiveViewport == vp) return;

        if (ActiveViewport != null)
            ActiveViewport.IsActive = false;

        ActiveViewport = vp;
        if (ActiveViewport != null)
            ActiveViewport.IsActive = true;

        UpdateScoutLines();
    }

    public void ToggleMaximizeActivePanel()
    {
        if (IsMprMode || ActiveViewport == null) return;

        if (IsMaximizedPanel)
        {
            // Restore previous grid layout
            IsMaximizedPanel = false;
            GridRows = _preMaximizedRows > 0 ? _preMaximizedRows : 1;
            GridColumns = _preMaximizedCols > 0 ? _preMaximizedCols : 1;
            UpdateDisplayedViewports();
        }
        else
        {
            // If already a single 1x1 panel, nothing to toggle
            if (GridRows == 1 && GridColumns == 1) return;

            // Save layout dimensions
            _preMaximizedRows = GridRows;
            _preMaximizedCols = GridColumns;

            IsMaximizedPanel = true;
            GridRows = 1;
            GridColumns = 1;
            UpdateDisplayedViewports();
        }
    }

    public void ActivateNextPanel()
    {
        int total = GridRows * GridColumns;
        if (total <= 1 || ActiveViewport == null) return;
        int next = (ActiveViewport.ViewportIndex + 1) % total;
        SetActiveViewport(Viewports[next]);
    }

    public void ActivatePreviousPanel()
    {
        int total = GridRows * GridColumns;
        if (total <= 1 || ActiveViewport == null) return;
        int prev = ActiveViewport.ViewportIndex - 1;
        if (prev < 0) prev = total - 1;
        SetActiveViewport(Viewports[prev]);
    }

    public void CloseActivePanel()
    {
        if (ActiveViewport != null)
        {
            ClosePanel(ActiveViewport);
        }
    }

    public void ClosePanel(ViewportViewModel vp)
    {
        if (vp == null) return;

        if (IsMprMode)
        {
            // In 3D MPR mode, clearing a plane clears its content
            vp.CurrentSeries = null;
            vp.OriginalSeries = null;
            vp.DisplayImage = null;
            vp.Measurements.Clear();
            vp.MprLines.Clear();
            vp.NotifyOverlayChanges();
            vp.NotifyMprLinesChanged();
            UpdateScoutLines();
            StatusMessage = "Closed MPR plane.";
            return;
        }

        // 1. Gather all other viewports that currently hold an active series
        int totalVisible = GridRows * GridColumns;
        var remainingSeries = new List<(SeriesModel series, int sliceIndex)>();

        for (int i = 0; i < totalVisible && i < Viewports.Count; i++)
        {
            var other = Viewports[i];
            if (other != vp && other.CurrentSeries != null)
            {
                remainingSeries.Add((other.CurrentSeries, other.CurrentSliceIndex));
            }
        }

        if (remainingSeries.Count == 0)
        {
            // No other series loaded, reset to 1x1 clean
            SetGridLayout(1, 1);
            Viewports[0].CurrentSeries = null;
            Viewports[0].OriginalSeries = null;
            Viewports[0].DisplayImage = null;
            Viewports[0].Measurements.Clear();
            Viewports[0].NotifyOverlayChanges();
            SetActiveViewport(Viewports[0]);
            StatusMessage = "Closed all viewport panels.";
            return;
        }

        // 2. Adapt grid dimensions according to remaining count
        int remainingCount = remainingSeries.Count;
        int newRows = 1;
        int newCols = 1;

        if (remainingCount == 1)
        {
            newRows = 1;
            newCols = 1;
        }
        else if (remainingCount == 2)
        {
            // If previous layout had multiple rows (e.g. 2x2), keep 2x1 or 1x2 depending on columns
            if (GridColumns >= 2 && GridRows == 1)
            {
                newRows = 1;
                newCols = 2;
            }
            else if (GridRows >= 2 && GridColumns == 1)
            {
                newRows = 2;
                newCols = 1;
            }
            else
            {
                // From 2x2: go to 1x2 side-by-side
                newRows = 1;
                newCols = 2;
            }
        }
        else if (remainingCount == 3)
        {
            if (GridRows == 1 || GridColumns >= 3)
            {
                newRows = 1;
                newCols = 3;
            }
            else
            {
                newRows = 3;
                newCols = 1;
            }
        }
        else if (remainingCount == 4)
        {
            newRows = 2;
            newCols = 2;
        }
        else if (remainingCount <= 6)
        {
            newRows = 2;
            newCols = 3;
        }
        else
        {
            newRows = Math.Min(3, (remainingCount + 2) / 3);
            newCols = 3;
        }

        SetGridLayout(newRows, newCols);

        // 3. Reload remaining series into the newly configured grid
        for (int i = 0; i < remainingSeries.Count && i < Viewports.Count; i++)
        {
            var (series, sliceIdx) = remainingSeries[i];
            Viewports[i].LoadSeries(series, sliceIdx);
        }

        if (Viewports.Count > 0)
        {
            SetActiveViewport(Viewports[0]);
        }

        UpdateScoutLines();
        StatusMessage = $"Closed panel. Layout adjusted to {newRows}x{newCols}.";
    }

    public void CloseAllPanels()
    {
        foreach (var vp in Viewports)
        {
            vp.CurrentSeries = null;
            vp.OriginalSeries = null;
            vp.DisplayImage = null;
            vp.Measurements.Clear();
            vp.MprLines.Clear();
            vp.NotifyOverlayChanges();
            vp.NotifyMprLinesChanged();
        }
        UpdateScoutLines();
        StatusMessage = "Closed all viewport panels.";
    }

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private StudyModel? _appendTargetStudy;
    private PatientModel? _appendTargetPatient;

    public Task ScanFolderAsync(string folderPath) => ScanPathsAsync(new[] { folderPath });

    public Task ScanMultipleFoldersAsync(IEnumerable<string> folderPaths) => ScanPathsAsync(folderPaths);

    public async Task ScanPathsAsync(
        IEnumerable<string> rawPaths,
        ViewportViewModel? targetViewport = null,
        string? targetFilePath = null,
        HashSet<string>? allowedSeriesUids = null,
        bool appendToCurrentStudy = false)
    {
        if (rawPaths == null) return;
        var inputList = rawPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (inputList.Count == 0) return;

        // If a single file was passed (e.g. "Open with Parsec" or dropped single file):
        // If its parent directory is a specific folder with other DICOMs, we scan the parent folder
        // so that the entire study is in the left preview list, and target the specific file!
        var pathsToScan = new List<string>();
        if (inputList.Count == 1 && File.Exists(inputList[0]))
        {
            string singleFile = Path.GetFullPath(inputList[0]);
            targetFilePath ??= singleFile;
            string? parent = Path.GetDirectoryName(singleFile);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                string parentName = Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                bool isSpecialFolder = string.IsNullOrEmpty(parentName) || 
                                       parent.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), StringComparison.OrdinalIgnoreCase) ||
                                       parent.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase);

                if (!isSpecialFolder)
                {
                    pathsToScan.Add(parent);
                }
                else
                {
                    pathsToScan.Add(singleFile);
                }
            }
            else
            {
                pathsToScan.Add(singleFile);
            }
        }
        else
        {
            pathsToScan.AddRange(inputList);
            if (string.IsNullOrEmpty(targetFilePath))
            {
                var firstFile = inputList.FirstOrDefault(File.Exists);
                if (firstFile != null) targetFilePath = Path.GetFullPath(firstFile);
            }
        }

        pathsToScan = pathsToScan.Where(p => Directory.Exists(p) || File.Exists(p)).Distinct().ToList();
        if (pathsToScan.Count == 0) return;

        await _scanLock.WaitAsync();
        try
        {
            IsLoading = true;
            if (appendToCurrentStudy)
            {
                var (curPat, curSt, _) = GetCurrentPatientContext();
                _appendTargetPatient = curPat;
                _appendTargetStudy = curSt;
            }
            else
            {
                _appendTargetPatient = null;
                _appendTargetStudy = null;
            }

            int totalPaths = pathsToScan.Count;

            for (int i = 0; i < totalPaths; i++)
            {
                var currentPath = pathsToScan[i];
                StatusMessage = totalPaths > 1
                    ? $"[{i + 1}/{totalPaths}] Scanare: {Path.GetFileName(currentPath)}..."
                    : $"Scanning: {currentPath}...";

                var progress = new Progress<(int scanned, int total, string currentFile)>(p =>
                {
                    double fraction = p.total > 0 ? (double)p.scanned / p.total : 0.0;
                    double overall = ((double)i + fraction) / totalPaths * 100.0;
                    ProgressPercent = Math.Clamp((int)overall, 0, 100);

                    StatusMessage = totalPaths > 1
                        ? $"[{i + 1}/{totalPaths}] Indexare {Path.GetFileName(currentPath)}: {p.scanned}/{p.total}"
                        : $"Indexing DICOM: {p.scanned}/{p.total} ({p.currentFile})";
                });

                await _loaderService.ScanPathsStreamingAsync(new[] { currentPath }, batch =>
                {
                    if (allowedSeriesUids != null && allowedSeriesUids.Count > 0)
                    {
                        batch = batch.Where(item =>
                            allowedSeriesUids.Contains(item.SeriesUid) ||
                            allowedSeriesUids.Any(u => item.SeriesUid.StartsWith(u + "_", StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                        if (batch.Count == 0) return;
                    }

                    if (System.Windows.Application.Current?.Dispatcher != null &&
                        !System.Windows.Application.Current.Dispatcher.CheckAccess())
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() => ProcessIncomingBatch(batch));
                    }
                    else
                    {
                        ProcessIncomingBatch(batch);
                    }
                }, progress);
            }

            // Post-scan chronological sort of series in each patient group
            foreach (var pGroup in GroupedPatientStudies)
            {
                var sorted = pGroup.SeriesList
                    .OrderBy(se => string.IsNullOrWhiteSpace(se.SeriesTime) ? "999999" : se.SeriesTime)
                    .ThenBy(se => se.SeriesNumber)
                    .ToList();

                for (int i = 0; i < sorted.Count; i++)
                {
                    int oldIdx = pGroup.SeriesList.IndexOf(sorted[i]);
                    if (oldIdx >= 0 && oldIdx != i)
                    {
                        pGroup.SeriesList.Move(oldIdx, i);
                    }
                }
            }

            // Sort patient groups by patient name, then descending by scan moment (newest study first)
            var sortedGroups = GroupedPatientStudies
                .OrderBy(g => g.PatientName)
                .ThenByDescending(g => g.SortDateTimeKey)
                .ToList();

            for (int i = 0; i < sortedGroups.Count; i++)
            {
                int oldIdx = GroupedPatientStudies.IndexOf(sortedGroups[i]);
                if (oldIdx >= 0 && oldIdx != i)
                {
                    GroupedPatientStudies.Move(oldIdx, i);
                }
            }

            // Determine which series and slice to display
            SeriesModel? targetSeries = null;
            int targetSliceIdx = 0;

            if (!string.IsNullOrEmpty(targetFilePath))
            {
                foreach (var p in Patients)
                {
                    foreach (var st in p.Studies)
                    {
                        foreach (var se in st.Series)
                        {
                            int matchIdx = se.Slices.FindIndex(sl => string.Equals(sl.FilePath, targetFilePath, StringComparison.OrdinalIgnoreCase));
                            if (matchIdx >= 0)
                            {
                                targetSeries = se;
                                targetSliceIdx = matchIdx;
                                break;
                            }
                        }
                        if (targetSeries != null) break;
                    }
                    if (targetSeries != null) break;
                }
            }

            var vp = targetViewport ?? ActiveViewport ?? Viewports.FirstOrDefault();
            if (targetSeries != null && vp != null)
            {
                vp.LoadSeries(targetSeries, targetSliceIdx);
                SetActiveViewport(vp);
            }
            else if (vp?.CurrentSeries == null)
            {
                var firstSeries = Patients.FirstOrDefault()?.Studies.FirstOrDefault()?.Series.FirstOrDefault(s => s.Slices.Count > 0);
                if (firstSeries != null && vp != null)
                {
                    vp.LoadSeries(firstSeries);
                }
            }

            // Refine thumbnails to the middle slice asynchronously
            _ = Task.Run(() =>
            {
                foreach (var p in Patients)
                {
                    foreach (var st in p.Studies)
                    {
                        foreach (var se in st.Series)
                        {
                            if (se.Slices.Count > 1)
                            {
                                int midIdx = se.Slices.Count / 2;
                                var midSlice = se.Slices[midIdx];
                                var midThumb = DicomLoaderService.RenderThumbnail(midSlice);
                                if (midThumb != null && System.Windows.Application.Current?.Dispatcher != null)
                                {
                                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        se.ThumbnailImage = midThumb;
                                    });
                                }
                            }
                        }
                    }
                }
            });

            int totalSeries = Patients.Sum(p => p.Studies.Sum(s => s.Series.Count));
            int totalSlices = Patients.Sum(p => p.Studies.Sum(s => s.Series.Sum(se => se.Slices.Count)));
            StatusMessage = $"Loaded {Patients.Count} patient(s), {totalSeries} series, {totalSlices} slices.";
            ProgressPercent = 100;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading folder: {ex.Message}";
        }
        finally
        {
            _appendTargetPatient = null;
            _appendTargetStudy = null;
            IsLoading = false;
            _scanLock.Release();
        }
    }

    /// <summary>
    /// Starts background progressive streaming for multiple PACS studies sequentially.
    /// </summary>
    public async Task StartPacsStreamingStudiesAsync(
        List<(PacsServerConfig server, PacsStudyResult study)> studies,
        bool appendToCurrentStudy = false)
    {
        if (studies == null || studies.Count == 0) return;
        int count = studies.Count;
        IsLoading = true;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var (server, study) = studies[i];
                await StartPacsStreamingStudyAsync(server, study, null, i, count, appendToCurrentStudy: appendToCurrentStudy || i > 0);
                if (i + 1 < count)
                {
                    await Task.Delay(400);
                }
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Starts background progressive streaming for one or multiple selected PACS series of a study.
    /// When appendToCurrentStudy is true, existing open series/slices are preserved and only new slices/series are added.
    /// </summary>
    public async Task StartPacsStreamingSeriesListAsync(
        PacsServerConfig server,
        PacsStudyResult study,
        List<PacsSeriesResult> seriesList,
        bool appendToCurrentStudy = false)
    {
        if (seriesList == null || seriesList.Count == 0) return;
        int count = seriesList.Count;
        IsLoading = true;
        try
        {
            for (int i = 0; i < count; i++)
            {
                await StartPacsStreamingStudyAsync(server, study, seriesList[i], i, count, appendToCurrentStudy: appendToCurrentStudy || i > 0);
                if (i + 1 < count)
                {
                    await Task.Delay(250);
                }
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Starts background PACS study download with live progressive streaming into the UI.
    /// As slices arrive, series and thumbnails appear immediately in the sidebar,
    /// the first series auto-loads into the active viewport, and viewers can interact in real-time.
    /// </summary>
    public async Task StartPacsStreamingStudyAsync(
        PacsServerConfig server, 
        PacsStudyResult study, 
        PacsSeriesResult? seriesFilter = null,
        int batchIndex = 0,
        int batchTotal = 1,
        bool appendToCurrentStudy = false)
    {
        if (batchIndex == 0 && !appendToCurrentStudy)
        {
            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(CloseAllPatients);
            }
            else
            {
                CloseAllPatients();
            }
        }

        if (appendToCurrentStudy)
        {
            void CaptureAppendContext()
            {
                var (curPat, curSt, _) = GetCurrentPatientContext();
                _appendTargetPatient = curPat;
                _appendTargetStudy = curSt;
            }
            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(CaptureAppendContext);
            }
            else
            {
                CaptureAppendContext();
            }
        }
        else
        {
            _appendTargetPatient = null;
            _appendTargetStudy = null;
        }

        IsLoading = true;
        StatusMessage = batchTotal > 1
            ? $"[{batchIndex + 1}/{batchTotal}] PACS ({server.Description}): Conectare pentru {study.DisplayPatientName}..."
            : $"PACS ({server.Description}): Conectare pentru {study.DisplayPatientName}...";
        ProgressPercent = batchTotal > 1 ? (batchIndex * 100) / batchTotal : 0;

        var seenSeries = new ConcurrentDictionary<string, byte>();
        var incomingQueue = new ConcurrentQueue<ParsedSliceBatchItem>();
        var processedFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var alreadyInMemorySopUids = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var alreadyInMemorySanitizedSopUids = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        var sw = Stopwatch.StartNew();
        long totalBytesDownloaded = 0;
        int receivedImageCount = 0;
        int activeParsers = 0;

        // Collect existing series and already-loaded slices in memory so half-downloaded series never re-parse or duplicate existing slices
        void CollectInMemoryState()
        {
            foreach (var p in Patients)
            {
                foreach (var st in p.Studies)
                {
                    if (!string.Equals(st.StudyInstanceUid, study.StudyInstanceUid, StringComparison.OrdinalIgnoreCase) &&
                        st != _appendTargetStudy)
                    {
                        continue;
                    }

                    foreach (var se in st.Series)
                    {
                        seenSeries.TryAdd(se.SeriesInstanceUid, 1);

                        bool isTargetSeries = seriesFilter == null ||
                            string.IsNullOrWhiteSpace(seriesFilter.SeriesInstanceUid) ||
                            string.Equals(se.SeriesInstanceUid, seriesFilter.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase) ||
                            se.SeriesInstanceUid.StartsWith(seriesFilter.SeriesInstanceUid + "_", StringComparison.OrdinalIgnoreCase);

                        foreach (var sl in se.Slices)
                        {
                            if (!string.IsNullOrWhiteSpace(sl.FilePath))
                            {
                                processedFiles.TryAdd(sl.FilePath, 1);
                            }
                            if (!string.IsNullOrWhiteSpace(sl.SopInstanceUid))
                            {
                                string cleanSop = sl.SopInstanceUid.Trim();
                                alreadyInMemorySopUids.TryAdd($"{ cleanSop }|{ sl.FrameIndex }", 1);
                                alreadyInMemorySanitizedSopUids.TryAdd(PacsService.Sanitize(cleanSop), 1);
                            }
                            if (isTargetSeries)
                            {
                                Interlocked.Increment(ref receivedImageCount);
                            }
                        }
                    }
                }
            }
        }

        if (System.Windows.Application.Current?.Dispatcher != null &&
            !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(CollectInMemoryState);
        }
        else
        {
            CollectInMemoryState();
        }

        // Determine the exact cache directory to scan (only the specific series folder when seriesFilter is provided)
        string studyCacheDir = Path.Combine(PacsService.Instance.Settings.CacheDirectory, PacsService.Sanitize(study.StudyInstanceUid));
        string targetCacheDir = (seriesFilter != null && !string.IsNullOrWhiteSpace(seriesFilter.SeriesInstanceUid))
            ? Path.Combine(studyCacheDir, PacsService.Sanitize(seriesFilter.SeriesInstanceUid))
            : studyCacheDir;

        if (Directory.Exists(targetCacheDir))
        {
            var cachedFiles = Directory.EnumerateFiles(targetCacheDir, "*", SearchOption.AllDirectories).ToList();
            if (cachedFiles.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var file in cachedFiles)
                    {
                        if (processedFiles.ContainsKey(file))
                        {
                            continue;
                        }

                        string fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                        if (!string.IsNullOrWhiteSpace(fileNameNoExt) && alreadyInMemorySanitizedSopUids.ContainsKey(fileNameNoExt))
                        {
                            processedFiles.TryAdd(file, 1);
                            continue;
                        }

                        var item = _loaderService.ParseSingleFile(file, seenSeries);
                        if (item != null && item.Slices.Count > 0)
                        {
                            processedFiles.TryAdd(file, 1);
                            item.Slices.RemoveAll(s =>
                                !string.IsNullOrWhiteSpace(s.SopInstanceUid) &&
                                alreadyInMemorySopUids.ContainsKey($"{s.SopInstanceUid.Trim()}|{s.FrameIndex}"));

                            if (item.Slices.Count > 0)
                            {
                                try
                                {
                                    var fi = new FileInfo(file);
                                    if (fi.Exists) Interlocked.Add(ref totalBytesDownloaded, fi.Length);
                                }
                                catch { }

                                foreach (var s in item.Slices)
                                {
                                    if (!string.IsNullOrWhiteSpace(s.SopInstanceUid))
                                    {
                                        alreadyInMemorySopUids.TryAdd($"{s.SopInstanceUid.Trim()}|{s.FrameIndex}", 1);
                                    }
                                }

                                Interlocked.Add(ref receivedImageCount, item.Slices.Count);
                                incomingQueue.Enqueue(item);
                            }
                        }
                        else
                        {
                            // Corrupted or truncated file from a previously interrupted transfer: remove it so PACS re-downloads it cleanly
                            try { File.Delete(file); } catch { }
                        }
                    }
                });
            }
        }

        // Hook InstanceReceived event for live incoming instances
        void OnInstanceReceived(string sUid, string serUid, string filePath)
        {
            if (sUid == study.StudyInstanceUid)
            {
                if (seriesFilter != null && !string.IsNullOrWhiteSpace(seriesFilter.SeriesInstanceUid) &&
                    serUid != seriesFilter.SeriesInstanceUid &&
                    !serUid.EndsWith(seriesFilter.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase) &&
                    !seriesFilter.SeriesInstanceUid.EndsWith(serUid, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
                if (!string.IsNullOrWhiteSpace(fileNameNoExt) && alreadyInMemorySanitizedSopUids.ContainsKey(fileNameNoExt))
                {
                    processedFiles.TryAdd(filePath, 1);
                    return;
                }

                if (processedFiles.TryAdd(filePath, 1))
                {
                    try
                    {
                        var fi = new FileInfo(filePath);
                        if (fi.Exists) Interlocked.Add(ref totalBytesDownloaded, fi.Length);
                    }
                    catch { }

                    Interlocked.Increment(ref activeParsers);
                    Task.Run(() =>
                    {
                        try
                        {
                            var item = _loaderService.ParseSingleFile(filePath, seenSeries);
                            if (item != null && item.Slices.Count > 0)
                            {
                                item.Slices.RemoveAll(s =>
                                    !string.IsNullOrWhiteSpace(s.SopInstanceUid) &&
                                    alreadyInMemorySopUids.ContainsKey($"{s.SopInstanceUid.Trim()}|{s.FrameIndex}"));

                                if (item.Slices.Count > 0)
                                {
                                    foreach (var s in item.Slices)
                                    {
                                        if (!string.IsNullOrWhiteSpace(s.SopInstanceUid))
                                        {
                                            alreadyInMemorySopUids.TryAdd($"{s.SopInstanceUid.Trim()}|{s.FrameIndex}", 1);
                                        }
                                    }

                                    Interlocked.Add(ref receivedImageCount, item.Slices.Count);
                                    incomingQueue.Enqueue(item);
                                }
                            }
                        }
                        catch { }
                        finally
                        {
                            Interlocked.Decrement(ref activeParsers);
                        }
                    });
                }
            }
        }

        PacsService.Instance.InstanceReceived += OnInstanceReceived;

        int expectedCount = seriesFilter != null && seriesFilter.InstanceCount > 0
            ? seriesFilter.InstanceCount
            : (study.InstanceCount > 0 ? study.InstanceCount : (study.Series != null ? study.Series.Sum(s => s.InstanceCount) : 0));
        if (expectedCount <= 0) expectedCount = 50;

        var progress = new Progress<(int current, int total, string message)>(p =>
        {
            double sec = sw.Elapsed.TotalSeconds;
            double ips = sec > 0.2 ? receivedImageCount / sec : 0;
            double mbps = sec > 0.2 ? (totalBytesDownloaded / (1024.0 * 1024.0)) / sec : 0;
            int total = Math.Max(receivedImageCount, p.total > 0 ? p.total : expectedCount);

            if (total > 0)
            {
                double studyFraction = (double)receivedImageCount / total;
                double overall = ((double)batchIndex + Math.Min(1.0, studyFraction)) / batchTotal * 100.0;
                ProgressPercent = Math.Clamp((int)overall, 0, 100);
            }

            string speedInfo = ips > 0 ? $"({ips:F1} img/s, {mbps:F1} MB/s)" : "";
            StatusMessage = batchTotal > 1
                ? $"[{batchIndex + 1}/{batchTotal}] {study.DisplayPatientName}: {receivedImageCount}/{total} imagini {speedInfo}"
                : $"PACS ({server.Description}): {receivedImageCount}/{total} imagini {speedInfo} — {study.DisplayPatientName}";
        });

        using var cts = new CancellationTokenSource();
        var downloadTask = seriesFilter != null && !string.IsNullOrWhiteSpace(seriesFilter.SeriesInstanceUid)
            ? PacsService.Instance.DownloadSeriesAsync(server, study.StudyInstanceUid, seriesFilter.SeriesInstanceUid, expectedCount, progress, cts.Token)
            : PacsService.Instance.DownloadStudyAsync(server, study.StudyInstanceUid, expectedCount, progress, cts.Token);

        // Flusher loop: pushes received items into the UI in smooth real-time batches (~35ms)
        // AI-NOTE: loop must not terminate while download is running OR active parsers are still decoding files in threadpool
        var flusherTask = Task.Run(async () =>
        {
            bool firstSeriesLoaded = appendToCurrentStudy && ActiveViewport?.CurrentSeries != null && ActiveViewport.CurrentSeries.Slices.Count > 0;

            while (!downloadTask.IsCompleted || Volatile.Read(ref activeParsers) > 0 || !incomingQueue.IsEmpty)
            {
                var batch = new List<ParsedSliceBatchItem>();
                while (batch.Count < 25 && incomingQueue.TryDequeue(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count > 0)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProcessIncomingBatch(batch);

                        // If viewport has no series (or we are opening a new study), load the first series of this incoming study!
                        if (!firstSeriesLoaded)
                        {
                            var currentStudyUid = ActiveViewport?.CurrentSeries?.StudyInstanceUid;
                            if (string.IsNullOrEmpty(currentStudyUid) || (!appendToCurrentStudy && currentStudyUid != study.StudyInstanceUid))
                            {
                                var targetStudyUid = _appendTargetStudy?.StudyInstanceUid ?? study.StudyInstanceUid;
                                var grp = GroupedPatientStudies.FirstOrDefault(g => g.StudyInstanceUid == targetStudyUid)
                                          ?? GroupedPatientStudies.FirstOrDefault(g => g.StudyInstanceUid == study.StudyInstanceUid);
                                var firstSe = grp?.SeriesList.FirstOrDefault(s => s.Slices.Count > 0);
                                if (firstSe != null)
                                {
                                    ActiveViewport?.LoadSeries(firstSe);
                                    firstSeriesLoaded = true;
                                }
                            }
                            else
                            {
                                firstSeriesLoaded = true;
                            }
                        }

                        // Live speed update during flusher
                        double sec = sw.Elapsed.TotalSeconds;
                        if (sec > 0.2 && !downloadTask.IsCompleted)
                        {
                            double ips = receivedImageCount / sec;
                            double mbps = (totalBytesDownloaded / (1024.0 * 1024.0)) / sec;
                            int totalToShow = Math.Max(receivedImageCount, expectedCount);
                            string speedInfo = ips > 0 ? $"({ips:F1} img/s, {mbps:F1} MB/s)" : "";
                            StatusMessage = batchTotal > 1
                                ? $"[{batchIndex + 1}/{batchTotal}] {study.DisplayPatientName}: {receivedImageCount}/{totalToShow} imagini {speedInfo}"
                                : $"PACS ({server.Description}): {receivedImageCount}/{totalToShow} imagini {speedInfo} — {study.DisplayPatientName}";
                        }
                    });
                }

                await Task.Delay(35).ConfigureAwait(false);
            }

            // Drain any final items
            var finalBatch = new List<ParsedSliceBatchItem>();
            while (incomingQueue.TryDequeue(out var item))
            {
                finalBatch.Add(item);
            }
            if (finalBatch.Count > 0)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProcessIncomingBatch(finalBatch);
                });
            }
        });

        try
        {
            await Task.WhenAll(downloadTask, flusherTask);

            // Wait briefly for any remaining background parsers just in case
            var parseTimeout = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref activeParsers) > 0 && DateTime.UtcNow < parseTimeout)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }

            // Safety sweep: ensure any downloaded or cached files on disk in targetCacheDir were not missed
            if (Directory.Exists(targetCacheDir))
            {
                var diskFiles = Directory.EnumerateFiles(targetCacheDir, "*", SearchOption.AllDirectories).ToList();
                var missedBatch = new List<ParsedSliceBatchItem>();
                foreach (var df in diskFiles)
                {
                    string fnNoExt = Path.GetFileNameWithoutExtension(df);
                    if (!string.IsNullOrWhiteSpace(fnNoExt) && alreadyInMemorySanitizedSopUids.ContainsKey(fnNoExt))
                    {
                        processedFiles.TryAdd(df, 1);
                        continue;
                    }

                    if (processedFiles.TryAdd(df, 1))
                    {
                        var item = _loaderService.ParseSingleFile(df, seenSeries);
                        if (item != null && item.Slices.Count > 0)
                        {
                            item.Slices.RemoveAll(s =>
                                !string.IsNullOrWhiteSpace(s.SopInstanceUid) &&
                                alreadyInMemorySopUids.ContainsKey($"{s.SopInstanceUid.Trim()}|{s.FrameIndex}"));

                            if (item.Slices.Count > 0)
                            {
                                try
                                {
                                    var fi = new FileInfo(df);
                                    if (fi.Exists) Interlocked.Add(ref totalBytesDownloaded, fi.Length);
                                }
                                catch { }

                                foreach (var s in item.Slices)
                                {
                                    if (!string.IsNullOrWhiteSpace(s.SopInstanceUid))
                                    {
                                        alreadyInMemorySopUids.TryAdd($"{s.SopInstanceUid.Trim()}|{s.FrameIndex}", 1);
                                    }
                                }

                                Interlocked.Add(ref receivedImageCount, item.Slices.Count);
                                missedBatch.Add(item);
                            }
                        }
                    }
                }
                if (missedBatch.Count > 0)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProcessIncomingBatch(missedBatch);
                    });
                }
            }

            // Post-download chronological sort of series & guaranteed viewport display
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var targetStudyUid = _appendTargetStudy?.StudyInstanceUid ?? study.StudyInstanceUid;
                var pGroup = GroupedPatientStudies.FirstOrDefault(g => g.StudyInstanceUid == study.StudyInstanceUid)
                             ?? GroupedPatientStudies.FirstOrDefault(g => g.StudyInstanceUid == targetStudyUid);
                if (pGroup != null)
                {
                    var sorted = pGroup.SeriesList
                        .OrderBy(se => string.IsNullOrWhiteSpace(se.SeriesTime) ? "999999" : se.SeriesTime)
                        .ThenBy(se => se.SeriesNumber)
                        .ToList();

                    for (int i = 0; i < sorted.Count; i++)
                    {
                        int oldIdx = pGroup.SeriesList.IndexOf(sorted[i]);
                        if (oldIdx >= 0 && oldIdx != i)
                        {
                            pGroup.SeriesList.Move(oldIdx, i);
                        }
                    }
                }

                // Guaranteed viewport load: if active viewport has no series (or we opened a new study), load first series
                var curVpSeries = ActiveViewport?.CurrentSeries;
                if (curVpSeries == null || curVpSeries.Slices.Count == 0 || (!appendToCurrentStudy && curVpSeries.StudyInstanceUid != study.StudyInstanceUid))
                {
                    var firstSe = pGroup?.SeriesList.FirstOrDefault(s => s.Slices.Count > 0);
                    if (firstSe != null)
                    {
                        ActiveViewport?.LoadSeries(firstSe);
                    }
                }

                int totalReceivedSlices = pGroup?.SeriesList.Sum(s => s.Slices.Count) ?? 0;
                double totalSec = sw.Elapsed.TotalSeconds;
                double avgIps = totalSec > 0 ? receivedImageCount / totalSec : 0;
                double avgMbps = totalSec > 0 ? (totalBytesDownloaded / (1024.0 * 1024.0)) / totalSec : 0;

                if (batchIndex + 1 >= batchTotal)
                {
                    StatusMessage = appendToCurrentStudy
                        ? $"Serie adăugată / actualizată în studiul curent: {study.DisplayPatientName} ({totalReceivedSlices} imagini în studiu)."
                        : $"Descărcare PACS completă: {study.DisplayPatientName} — {totalReceivedSlices} imagini în {totalSec:F1}s ({avgIps:F1} img/s, {avgMbps:F1} MB/s).";
                    ProgressPercent = 100;
                }
                else
                {
                    StatusMessage = $"[{batchIndex + 1}/{batchTotal}] Finalizat: {study.DisplayPatientName} ({totalReceivedSlices} imagini). Se trece la următorul...";
                    ProgressPercent = ((batchIndex + 1) * 100) / batchTotal;
                }
            });
        }
        catch (Exception ex)
        {
            await flusherTask;
            StatusMessage = $"PACS Download error ({study.DisplayPatientName}): {ex.Message}";
        }
        finally
        {
            PacsService.Instance.InstanceReceived -= OnInstanceReceived;
            if (batchIndex + 1 >= batchTotal)
            {
                _appendTargetPatient = null;
                _appendTargetStudy = null;
                IsLoading = false;
            }
        }
    }

    private void ProcessIncomingBatch(List<ParsedSliceBatchItem> batch)
    {
        var updatedSeries = new HashSet<SeriesModel>();

        foreach (var item in batch)
        {
            // 1. Patient (if appending to current study and study doesn't exist yet, attach to current active patient/study)
            string pKey = string.IsNullOrEmpty(item.PatientId) ? item.PatientName : item.PatientId;
            var patient = Patients.FirstOrDefault(p => p.Studies.Any(s => string.Equals(s.StudyInstanceUid, item.StudyUid, StringComparison.OrdinalIgnoreCase)))
                          ?? _appendTargetPatient
                          ?? Patients.FirstOrDefault(p => (string.IsNullOrEmpty(p.Id) ? p.Name : p.Id) == pKey);
            if (patient == null)
            {
                patient = new PatientModel
                {
                    Id = item.PatientId,
                    Name = item.PatientName,
                    BirthDate = item.BirthDate,
                    Sex = item.Sex,
                    PatientAge = item.PatientAge
                };
                Patients.Add(patient);
            }
            else if (string.IsNullOrWhiteSpace(patient.PatientAge) && !string.IsNullOrWhiteSpace(item.PatientAge))
            {
                patient.PatientAge = item.PatientAge;
            }

            // 2. Study
            var study = patient.Studies.FirstOrDefault(s => string.Equals(s.StudyInstanceUid, item.StudyUid, StringComparison.OrdinalIgnoreCase))
                        ?? _appendTargetStudy;
            if (study == null)
            {
                study = new StudyModel
                {
                    StudyInstanceUid = item.StudyUid,
                    RawStudyDate = item.StudyDate,
                    StudyDate = DicomLoaderService.FormatDicomDate(item.StudyDate),
                    StudyTime = item.StudyTime,
                    StudyDescription = item.StudyDescription,
                    InstitutionName = item.InstitutionName,
                    AccessionNumber = item.AccessionNumber,
                    ReadingPhysician = item.ReadingPhysician,
                    PerformingPhysician = item.PerformingPhysician,
                    ReferringPhysician = item.ReferringPhysician,
                    ContrastBolusAgent = item.ContrastBolusAgent,
                    ContrastBolusRoute = item.ContrastBolusRoute,
                    TotalDlp = item.TotalDlp,
                    PatientAge = item.PatientAge
                };
                patient.Studies.Add(study);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(study.RawStudyDate) && !string.IsNullOrWhiteSpace(item.StudyDate))
                    study.RawStudyDate = item.StudyDate;
                if (string.IsNullOrWhiteSpace(study.StudyTime) && !string.IsNullOrWhiteSpace(item.StudyTime))
                    study.StudyTime = item.StudyTime;
                if (string.IsNullOrWhiteSpace(study.AccessionNumber) && !string.IsNullOrWhiteSpace(item.AccessionNumber))
                    study.AccessionNumber = item.AccessionNumber;
                if (string.IsNullOrWhiteSpace(study.ReadingPhysician) && !string.IsNullOrWhiteSpace(item.ReadingPhysician))
                    study.ReadingPhysician = item.ReadingPhysician;
                if (string.IsNullOrWhiteSpace(study.PerformingPhysician) && !string.IsNullOrWhiteSpace(item.PerformingPhysician))
                    study.PerformingPhysician = item.PerformingPhysician;
                if (string.IsNullOrWhiteSpace(study.ReferringPhysician) && !string.IsNullOrWhiteSpace(item.ReferringPhysician))
                    study.ReferringPhysician = item.ReferringPhysician;
                if (string.IsNullOrWhiteSpace(study.ContrastBolusAgent) && !string.IsNullOrWhiteSpace(item.ContrastBolusAgent))
                    study.ContrastBolusAgent = item.ContrastBolusAgent;
                if (item.TotalDlp > 0 && item.TotalDlp > study.TotalDlp)
                    study.TotalDlp = item.TotalDlp;
                if (string.IsNullOrWhiteSpace(study.PatientAge) && !string.IsNullOrWhiteSpace(item.PatientAge))
                    study.PatientAge = item.PatientAge;
            }

            // 3. PatientGroupModel for UI sidebar
            var pGroup = GroupedPatientStudies.FirstOrDefault(g => string.Equals(g.StudyInstanceUid, study.StudyInstanceUid, StringComparison.OrdinalIgnoreCase));
            if (pGroup == null)
            {
                pGroup = new PatientGroupModel
                {
                    PatientName = patient.Name,
                    PatientId = patient.Id,
                    RawStudyDate = study.RawStudyDate,
                    StudyDate = study.StudyDate,
                    StudyTime = study.StudyTime,
                    StudyDescription = study.StudyDescription,
                    StudyInstanceUid = study.StudyInstanceUid
                };
                GroupedPatientStudies.Add(pGroup);
            }

            // 4. Series
            var series = study.Series.FirstOrDefault(s => string.Equals(s.SeriesInstanceUid, item.SeriesUid, StringComparison.OrdinalIgnoreCase));
            if (series == null)
            {
                series = new SeriesModel
                {
                    SeriesInstanceUid = item.SeriesUid,
                    StudyInstanceUid = study.StudyInstanceUid,
                    StudyDescription = study.StudyDescription,
                    Laterality = item.Laterality,
                    SeriesNumber = item.SeriesNumber,
                    BaseSeriesDescription = item.SeriesDescription,
                    SeriesDescription = item.SeriesDescription,
                    Modality = item.Modality,
                    BodyPart = item.BodyPart,
                    PatientName = patient.Name,
                    PatientId = patient.Id,
                    PatientBirthDate = !string.IsNullOrWhiteSpace(item.BirthDate) ? item.BirthDate : patient.BirthDate,
                    PatientAge = !string.IsNullOrWhiteSpace(item.PatientAge) ? item.PatientAge : patient.PatientAge,
                    StudyDate = study.StudyDate,
                    SeriesTime = item.SeriesTime,
                    CtdiVol = item.CtdiVol,
                    TotalDlp = item.TotalDlp,
                    ContrastBolusAgent = item.ContrastBolusAgent,
                    ThumbnailImage = item.InitialThumbnail
                };
                study.Series.Add(series);
                if (!pGroup.SeriesList.Contains(series))
                {
                    pGroup.SeriesList.Add(series);
                }

                bool matches = string.IsNullOrWhiteSpace(FilterQuery) ||
                    patient.Name.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                    patient.Id.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                    series.SeriesDescription.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                    study.StudyDescription.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                    study.StudyDate.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase);

                if (matches && !FilteredSeries.Contains(series))
                {
                    FilteredSeries.Add(series);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(series.BaseSeriesDescription) && !string.IsNullOrWhiteSpace(item.SeriesDescription))
                    series.BaseSeriesDescription = item.SeriesDescription;
                if (string.IsNullOrWhiteSpace(series.PatientBirthDate) && !string.IsNullOrWhiteSpace(item.BirthDate))
                    series.PatientBirthDate = item.BirthDate;
                if (string.IsNullOrWhiteSpace(series.PatientAge) && !string.IsNullOrWhiteSpace(item.PatientAge))
                    series.PatientAge = item.PatientAge;
                if (string.IsNullOrWhiteSpace(series.SeriesTime) && !string.IsNullOrWhiteSpace(item.SeriesTime))
                    series.SeriesTime = item.SeriesTime;
                if (item.CtdiVol > 0 && series.CtdiVol == 0) series.CtdiVol = item.CtdiVol;
                if (item.TotalDlp > 0 && series.TotalDlp == 0) series.TotalDlp = item.TotalDlp;
                if (string.IsNullOrWhiteSpace(series.ContrastBolusAgent) && !string.IsNullOrWhiteSpace(item.ContrastBolusAgent))
                    series.ContrastBolusAgent = item.ContrastBolusAgent;
                if (series.ThumbnailImage == null && item.InitialThumbnail != null)
                    series.ThumbnailImage = item.InitialThumbnail;

                if (!pGroup.SeriesList.Contains(series))
                {
                    pGroup.SeriesList.Add(series);
                }

                bool matches = string.IsNullOrWhiteSpace(FilterQuery) ||
                    patient.Name.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                    patient.Id.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                    series.SeriesDescription.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                    study.StudyDescription.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                    study.StudyDate.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase);

                if (matches && !FilteredSeries.Contains(series))
                {
                    FilteredSeries.Add(series);
                }
            }

            int beforeCount = series.Slices.Count;
            foreach (var s in item.Slices)
            {
                bool isDuplicate = series.Slices.Any(existing =>
                    existing.FrameIndex == s.FrameIndex &&
                    ((!string.IsNullOrEmpty(s.SopInstanceUid) && !string.IsNullOrEmpty(existing.SopInstanceUid) &&
                      string.Equals(existing.SopInstanceUid.Trim(), s.SopInstanceUid.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                     (!string.IsNullOrEmpty(s.FilePath) && !string.IsNullOrEmpty(existing.FilePath) &&
                      string.Equals(existing.FilePath, s.FilePath, StringComparison.OrdinalIgnoreCase))));

                if (!isDuplicate)
                {
                    series.Slices.Add(s);
                }
            }
            if (series.Slices.Count > beforeCount)
            {
                updatedSeries.Add(series);
            }
        }

        // Update all series that received slices in this batch
        foreach (var series in updatedSeries)
        {
            // Capture active slice & measurement slice references BEFORE SortSlices reorders series.Slices
            var vpSavedSlices = new Dictionary<ViewportViewModel, DicomSliceModel?>();
            var vpSavedMeasurementSlices = new Dictionary<MeasurementItem, DicomSliceModel>();
            foreach (var vp in Viewports)
            {
                if (vp.CurrentSeries == series || vp.OriginalSeries == series)
                {
                    vpSavedSlices[vp] = vp.CurrentSlice;
                    if (vp.CurrentSeries == series)
                    {
                        foreach (var m in vp.Measurements)
                        {
                            if (m.SliceIndex >= 0 && m.SliceIndex < series.Slices.Count)
                            {
                                vpSavedMeasurementSlices[m] = series.Slices[m.SliceIndex];
                            }
                        }
                    }
                }
            }

            DicomLoaderService.SortSlices(series.Slices);

            // Always ensure series thumbnail represents the anatomical middle slice (never an index-0 scout reference image)
            if (series.Slices.Count >= 2)
            {
                var midSlice = series.Slices[series.Slices.Count / 2];
                var midThumb = DicomLoaderService.RenderThumbnail(midSlice);
                if (midThumb != null)
                {
                    series.ThumbnailImage = midThumb;
                }
            }

            if (string.IsNullOrWhiteSpace(series.BaseSeriesDescription))
            {
                series.BaseSeriesDescription = series.SeriesDescription;
            }
            string commentSummary = DicomLoaderService.FormatSpineCommentsSummary(series.Slices.Select(s => s.ImageComments));
            if (!string.IsNullOrWhiteSpace(commentSummary) && !series.BaseSeriesDescription.Contains(commentSummary, StringComparison.OrdinalIgnoreCase))
            {
                series.SeriesDescription = $"{series.BaseSeriesDescription} ({commentSummary})";
            }

            series.NotifySlicesUpdated();

            // Restore measurement slice indices after sorting
            foreach (var kvp in vpSavedMeasurementSlices)
            {
                int newMIdx = series.Slices.IndexOf(kvp.Value);
                if (newMIdx >= 0)
                {
                    kvp.Key.SliceIndex = newMIdx;
                }
            }

            // Real-time update for any viewport currently showing this series
            foreach (var vp in Viewports)
            {
                if (vp.CurrentSeries == series || vp.OriginalSeries == series)
                {
                    vpSavedSlices.TryGetValue(vp, out var curSlice);
                    if (curSlice != null && vp.CurrentSeries == series)
                    {
                        int newIdx = series.Slices.IndexOf(curSlice);
                        if (newIdx < 0)
                        {
                            newIdx = series.Slices.FindIndex(s =>
                                s.FrameIndex == curSlice.FrameIndex &&
                                (!string.IsNullOrEmpty(curSlice.SopInstanceUid) && !string.IsNullOrEmpty(s.SopInstanceUid)
                                    ? string.Equals(s.SopInstanceUid.Trim(), curSlice.SopInstanceUid.Trim(), StringComparison.OrdinalIgnoreCase)
                                    : string.Equals(s.FilePath, curSlice.FilePath, StringComparison.OrdinalIgnoreCase)));
                        }
                        if (newIdx >= 0)
                        {
                            vp.CurrentSliceIndex = newIdx;
                        }
                    }
                    else if (vp.CurrentSeries == series && series.Slices.Count > 0)
                    {
                        vp.CurrentSliceIndex = 0;
                        vp.WindowCenter = series.Slices[0].DefaultWindowCenter;
                        vp.WindowWidth = series.Slices[0].DefaultWindowWidth;
                        vp.RenderCurrentSlice();
                    }

                    vp.NotifyOverlayChanges();
                }
            }
        }
    }

    public void UpdateFilteredSeries()
    {
        _smartLayoutAppliedStudies.Clear();
        FilteredSeries.Clear();
        GroupedPatientStudies.Clear();

        foreach (var p in Patients)
        {
            foreach (var st in p.Studies)
            {
                var pGroup = new PatientGroupModel
                {
                    PatientName = p.Name,
                    PatientId = p.Id,
                    RawStudyDate = st.RawStudyDate,
                    StudyDate = st.StudyDate,
                    StudyTime = st.StudyTime,
                    StudyDescription = st.StudyDescription,
                    StudyInstanceUid = st.StudyInstanceUid
                };

                foreach (var se in st.Series)
                {
                    if (string.IsNullOrWhiteSpace(FilterQuery) ||
                        p.Name.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                        p.Id.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                        se.SeriesDescription.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                        st.StudyDescription.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase) ||
                        st.StudyDate.Contains(FilterQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        FilteredSeries.Add(se);
                        pGroup.SeriesList.Add(se);
                    }
                }

                if (pGroup.SeriesList.Count > 0)
                {
                    GroupedPatientStudies.Add(pGroup);
                }
            }
        }

        // Sort patient groups by patient name, then descending by scan moment (newest study first)
        var sortedGroups = GroupedPatientStudies
            .OrderBy(g => g.PatientName)
            .ThenByDescending(g => g.SortDateTimeKey)
            .ToList();

        for (int i = 0; i < sortedGroups.Count; i++)
        {
            int oldIdx = GroupedPatientStudies.IndexOf(sortedGroups[i]);
            if (oldIdx >= 0 && oldIdx != i)
            {
                GroupedPatientStudies.Move(oldIdx, i);
            }
        }
    }

    private double CalculatePhysicalZoom(ViewportViewModel source, ViewportViewModel target)
    {
        if (source.CurrentSlice != null && target.CurrentSlice != null &&
            source.CurrentSlice.PixelSpacingX > 0 && target.CurrentSlice.PixelSpacingX > 0)
        {
            // Zoom proportional to physical pixel spacing
            return source.Zoom * (target.CurrentSlice.PixelSpacingX / source.CurrentSlice.PixelSpacingX);
        }
        return source.Zoom;
    }

    public static string GetSliceOrientationCategory(DicomSliceModel? slice)
    {
        if (slice?.ImageOrientationPatient == null || slice.ImageOrientationPatient.Length < 6)
            return "Unknown";

        double rx = slice.ImageOrientationPatient[0];
        double ry = slice.ImageOrientationPatient[1];
        double rz = slice.ImageOrientationPatient[2];
        double cx = slice.ImageOrientationPatient[3];
        double cy = slice.ImageOrientationPatient[4];
        double cz = slice.ImageOrientationPatient[5];

        double nx = ry * cz - rz * cy;
        double ny = rz * cx - rx * cz;
        double nz = rx * cy - ry * cx;

        double ax = Math.Abs(nx);
        double ay = Math.Abs(ny);
        double az = Math.Abs(nz);

        if (az >= ax && az >= ay) return "Axial";
        if (ay >= ax && ay >= az) return "Coronal";
        return "Sagittal";
    }

    public void UpdateAdaptiveSensitivity(ViewportViewModel? vp)
    {
        if (vp?.CurrentSeries == null) return;
        int sliceCount = vp.CurrentSeries.Slices.Count;
        if (sliceCount >= 200)
            BrowseSensitivityPixels = 3.0;
        else if (sliceCount >= 50)
            BrowseSensitivityPixels = 6.0;
        else if (sliceCount >= 20)
            BrowseSensitivityPixels = 12.0;
        else
            BrowseSensitivityPixels = 20.0;
    }

    public void SwitchActivePlane(OrthogonalPlane plane)
    {
        if (ActiveViewport == null || ActiveViewport.OriginalSeries == null) return;

        // 1. Look for an existing viewport already open in the target plane
        int? matchedInitialSlice = null;
        if (CurrentSyncMode == SyncMode.Automatic)
        {
            int totalVisible = GridRows * GridColumns;
            string targetCategory = plane.ToString();

            for (int i = 0; i < totalVisible; i++)
            {
                var other = Viewports[i];
                if (other == ActiveViewport || other.CurrentSeries == null || other.CurrentSlice == null) continue;

                string otherCat = GetSliceOrientationCategory(other.CurrentSlice);
                if (otherCat.Equals(targetCategory, StringComparison.OrdinalIgnoreCase))
                {
                    // Found an existing series/viewport in the target plane!
                    // Temporarily check what the new series would be to find the closest slice to this existing viewport
                    var targetSeries = MprService.GenerateOrthogonalSeries(ActiveViewport.OriginalSeries, plane);
                    if (targetSeries != null && targetSeries.Slices.Count > 0)
                    {
                        matchedInitialSlice = SynchronizationService.FindClosestSliceIndex(other.CurrentSlice, targetSeries);
                        break;
                    }
                }
            }
        }

        // 2. Perform switch with sync temporarily suspended so existing series do NOT move!
        IsSyncSuspended = true;
        try
        {
            ActiveViewport.SwitchOrthogonalPlane(plane, matchedInitialSlice);
            UpdateAdaptiveSensitivity(ActiveViewport);
            UpdateScoutLines();
        }
        finally
        {
            IsSyncSuspended = false;
        }

        StatusMessage = $"Viewport plane set to: {plane.ToString().ToUpperInvariant()}";
    }

    private int _mprSavedRows = 1;
    private int _mprSavedCols = 1;
    private readonly List<(int index, SeriesModel? series, int sliceIndex)> _mprSavedState = new();

    public void Start3DMpr(SeriesModel? source = null)
    {
        var series = source ?? ActiveViewport?.OriginalSeries ?? ActiveViewport?.CurrentSeries;
        if (series == null || series.Slices.Count < 3)
        {
            StatusMessage = "3D MPR necesită o serie volumetrică (CT sau RMN) cu cel puțin 3 imagini.";
            return;
        }

        // Save layout state before entering MPR mode
        if (!IsMprMode)
        {
            _mprSavedRows = GridRows;
            _mprSavedCols = GridColumns;
            _mprSavedState.Clear();
            int total = GridRows * GridColumns;
            for (int i = 0; i < total && i < Viewports.Count; i++)
            {
                _mprSavedState.Add((i, Viewports[i].CurrentSeries, Viewports[i].CurrentSliceIndex));
            }
        }

        IsLoading = true;
        StatusMessage = $"Computing 3D MPR reconstruction for '{series.DisplayTitle}'...";

        try
        {
            Clear3DCursor();
            var origPlane = MprService.DetectSeriesOrientation(series);

            // Ensure we have at least 3 viewports initialized
            while (Viewports.Count < 3)
            {
                var vp = new ViewportViewModel(Viewports.Count);
                vp.ImageRendered += OnViewportImageRendered;
                vp.TransformChanged += OnViewportTransformChanged;
                Viewports.Add(vp);
            }

            // Generate the 3 orthogonal series
            SeriesModel? axialSeries = origPlane == OrthogonalPlane.Axial
                ? series
                : MprService.GenerateOrthogonalSeries(series, OrthogonalPlane.Axial);

            SeriesModel? sagittalSeries = origPlane == OrthogonalPlane.Sagittal
                ? series
                : MprService.GenerateOrthogonalSeries(series, OrthogonalPlane.Sagittal);

            SeriesModel? coronalSeries = origPlane == OrthogonalPlane.Coronal
                ? series
                : MprService.GenerateOrthogonalSeries(series, OrthogonalPlane.Coronal);

            if (axialSeries == null || sagittalSeries == null || coronalSeries == null)
            {
                StatusMessage = "Error: Could not generate orthogonal planes for 3D MPR.";
                return;
            }

            // Viewport 0: Sagittal (Top-Left)
            Viewports[0].LoadSeries(sagittalSeries, sagittalSeries.Slices.Count / 2);
            Viewports[0].IsMprViewport = true;
            Viewports[0].MprPlane = OrthogonalPlane.Sagittal;

            // Viewport 1: Axial (Bottom-Left)
            Viewports[1].LoadSeries(axialSeries, axialSeries.Slices.Count / 2);
            Viewports[1].IsMprViewport = true;
            Viewports[1].MprPlane = OrthogonalPlane.Axial;

            // Viewport 2: Coronal (Right column)
            Viewports[2].LoadSeries(coronalSeries, coronalSeries.Slices.Count / 2);
            Viewports[2].IsMprViewport = true;
            Viewports[2].MprPlane = OrthogonalPlane.Coronal;

            IsMprMode = true;
            foreach (var vp in Viewports) vp.ShowScoutLine = false;
            SetActiveViewport(Viewports[1]); // Axial active by default

            UpdateMprLines();
            StatusMessage = "3D MPR mode active: Sagittal (Top-Left), Axial (Bottom-Left), Coronal (Right).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error starting 3D MPR: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Exit3DMpr()
    {
        if (!IsMprMode) return;

        IsMprMode = false;
        foreach (var vp in Viewports)
        {
            vp.IsMprViewport = false;
            vp.MprPlane = null;
            vp.ResetMprOrientation();
            vp.MprLines.Clear();
            vp.NotifyMprLinesChanged();
        }

        SetGridLayout(_mprSavedRows, _mprSavedCols);
        foreach (var (idx, series, sliceIdx) in _mprSavedState)
        {
            if (idx < Viewports.Count && series != null)
            {
                Viewports[idx].LoadSeries(series, sliceIdx);
            }
        }

        UpdateScoutLines();
        StatusMessage = "Ieșit din modul 3D MPR.";
    }

    public void Toggle3DMpr()
    {
        if (IsMprMode)
            Exit3DMpr();
        else
            Start3DMpr();
    }

    public ViewportViewModel? GetMprViewportForPlane(OrthogonalPlane plane)
    {
        if (!IsMprMode || Viewports.Count < 3) return null;
        return plane switch
        {
            OrthogonalPlane.Sagittal => Viewports[0],
            OrthogonalPlane.Axial => Viewports[1],
            OrthogonalPlane.Coronal => Viewports[2],
            _ => null
        };
    }

    public void UpdateMprLines()
    {
        if (!IsMprMode || Viewports.Count < 3) return;

        var sagVp = Viewports[0];
        var axVp = Viewports[1];
        var corVp = Viewports[2];

        if (sagVp.CurrentSlice == null || axVp.CurrentSlice == null || corVp.CurrentSlice == null) return;

        // Modern high-contrast RadiAnt-style medical color palette
        Color axialColor = Color.FromRgb(255, 214, 0);   // Solar Gold (#FFD600)
        Color coronalColor = Color.FromRgb(255, 64, 129); // Coral Rose (#FF4081)
        Color sagittalColor = Color.FromRgb(0, 229, 255); // Electric Cyan (#00E5FF)

        // Helper to compute line with center and dual handle dots (near both ends)
        MprReferenceLine CreateMprLine(OrthogonalPlane targetPlane, Color color, System.Windows.Point p1, System.Windows.Point p2)
        {
            var center = new System.Windows.Point((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5);
            var h1 = new System.Windows.Point(p1.X * 0.85 + p2.X * 0.15, p1.Y * 0.85 + p2.Y * 0.15);
            var h2 = new System.Windows.Point(p1.X * 0.15 + p2.X * 0.85, p1.Y * 0.15 + p2.Y * 0.85);
            return new MprReferenceLine
            {
                TargetPlane = targetPlane,
                Color = color,
                StartPoint = p1,
                EndPoint = p2,
                CenterPoint = center,
                HandlePoint = h1,
                HandlePoint2 = h2
            };
        }

        // 1. Sagittal Viewport (Viewports[0]):
        // - Gold line: cut for Axial (axVp)
        // - Coral line: cut for Coronal (corVp)
        sagVp.MprLines.Clear();
        var axOnSag = SynchronizationService.CalculatePlaneIntersection(axVp.CurrentSlice, sagVp.CurrentSlice);
        if (axOnSag != null)
        {
            sagVp.MprLines.Add(CreateMprLine(OrthogonalPlane.Axial, axialColor, axOnSag.Value.p1, axOnSag.Value.p2));
        }
        var corOnSag = SynchronizationService.CalculatePlaneIntersection(corVp.CurrentSlice, sagVp.CurrentSlice);
        if (corOnSag != null)
        {
            sagVp.MprLines.Add(CreateMprLine(OrthogonalPlane.Coronal, coronalColor, corOnSag.Value.p1, corOnSag.Value.p2));
        }

        // 2. Axial Viewport (Viewports[1]):
        // - Coral line: cut for Coronal (corVp)
        // - Cyan line: cut for Sagittal (sagVp)
        axVp.MprLines.Clear();
        var corOnAx = SynchronizationService.CalculatePlaneIntersection(corVp.CurrentSlice, axVp.CurrentSlice);
        if (corOnAx != null)
        {
            axVp.MprLines.Add(CreateMprLine(OrthogonalPlane.Coronal, coronalColor, corOnAx.Value.p1, corOnAx.Value.p2));
        }
        var sagOnAx = SynchronizationService.CalculatePlaneIntersection(sagVp.CurrentSlice, axVp.CurrentSlice);
        if (sagOnAx != null)
        {
            axVp.MprLines.Add(CreateMprLine(OrthogonalPlane.Sagittal, sagittalColor, sagOnAx.Value.p1, sagOnAx.Value.p2));
        }

        // 3. Coronal Viewport (Viewports[2]):
        // - Gold line: cut for Axial (axVp)
        // - Cyan line: cut for Sagittal (sagVp)
        corVp.MprLines.Clear();
        var axOnCor = SynchronizationService.CalculatePlaneIntersection(axVp.CurrentSlice, corVp.CurrentSlice);
        if (axOnCor != null)
        {
            corVp.MprLines.Add(CreateMprLine(OrthogonalPlane.Axial, axialColor, axOnCor.Value.p1, axOnCor.Value.p2));
        }
        var sagOnCor = SynchronizationService.CalculatePlaneIntersection(sagVp.CurrentSlice, corVp.CurrentSlice);
        if (sagOnCor != null)
        {
            corVp.MprLines.Add(CreateMprLine(OrthogonalPlane.Sagittal, sagittalColor, sagOnCor.Value.p1, sagOnCor.Value.p2));
        }

        sagVp.NotifyMprLinesChanged();
        axVp.NotifyMprLinesChanged();
        corVp.NotifyMprLinesChanged();
    }

    /// <summary>
    /// Obliquely rotates a target MPR orthogonal plane around the normal of the source viewport.
    /// Re-samples the target viewport slice in real-time from the 3D volume.
    /// </summary>
    public void RotateMprPlane(ViewportViewModel sourceVp, OrthogonalPlane targetPlane, double deltaAngleRadians, (double x, double y, double z)? pivot3D = null)
    {
        if (!IsMprMode || Math.Abs(deltaAngleRadians) < 1e-5) return;

        var targetVp = GetMprViewportForPlane(targetPlane);
        if (sourceVp.CurrentSlice == null || targetVp?.CurrentSlice == null) return;

        var sourceSlice = sourceVp.CurrentSlice;
        var targetSlice = targetVp.CurrentSlice;

        var volCtx = MprService.GetVolumeContext(sourceVp.OriginalSeries ?? targetVp.OriginalSeries ?? sourceVp.CurrentSeries!);
        if (volCtx == null) return;

        // Normal of the source viewport plane (the axis of rotation in 3D)
        double[] srcIop = sourceSlice.ImageOrientationPatient!;
        double rx_s = srcIop[0], ry_s = srcIop[1], rz_s = srcIop[2];
        double cx_s = srcIop[3], cy_s = srcIop[4], cz_s = srcIop[5];

        double kx = ry_s * cz_s - rz_s * cy_s;
        double ky = rz_s * cx_s - rx_s * cz_s;
        double kz = rx_s * cy_s - ry_s * cx_s;
        double klen = Math.Sqrt(kx * kx + ky * ky + kz * kz);
        if (klen < 1e-4) return;
        kx /= klen; ky /= klen; kz /= klen;

        // Rodrigues' rotation formula helper
        (double x, double y, double z) RotateVector(double vx, double vy, double vz, double kx, double ky, double kz, double angle)
        {
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            double dot = vx * kx + vy * ky + vz * kz;
            double crossX = ky * vz - kz * vy;
            double crossY = kz * vx - kx * vz;
            double crossZ = kx * vy - ky * vx;

            return (
                vx * cos + crossX * sin + kx * dot * (1.0 - cos),
                vy * cos + crossY * sin + ky * dot * (1.0 - cos),
                vz * cos + crossZ * sin + kz * dot * (1.0 - cos)
            );
        }

        // Current row and column vectors of the target slice
        double[] tgtIop = targetSlice.ImageOrientationPatient!;
        var newRow = RotateVector(tgtIop[0], tgtIop[1], tgtIop[2], kx, ky, kz, deltaAngleRadians);
        var newCol = RotateVector(tgtIop[3], tgtIop[4], tgtIop[5], kx, ky, kz, deltaAngleRadians);

        // Normalize new basis
        double rLen = Math.Sqrt(newRow.x * newRow.x + newRow.y * newRow.y + newRow.z * newRow.z);
        double cLen = Math.Sqrt(newCol.x * newCol.x + newCol.y * newCol.y + newCol.z * newCol.z);
        if (rLen < 1e-4 || cLen < 1e-4) return;

        double[] rVec = new[] { newRow.x / rLen, newRow.y / rLen, newRow.z / rLen };
        double[] cVec = new[] { newCol.x / cLen, newCol.y / cLen, newCol.z / cLen };

        // Center of the target slice in 3D
        double[] tgtIpp = targetSlice.ImagePositionPatient!;
        double oldCenterX = tgtIpp[0] + (targetSlice.Columns - 1) * 0.5 * targetSlice.PixelSpacingX * tgtIop[0]
                                     + (targetSlice.Rows - 1) * 0.5 * targetSlice.PixelSpacingY * tgtIop[3];
        double oldCenterY = tgtIpp[1] + (targetSlice.Columns - 1) * 0.5 * targetSlice.PixelSpacingX * tgtIop[1]
                                     + (targetSlice.Rows - 1) * 0.5 * targetSlice.PixelSpacingY * tgtIop[4];
        double oldCenterZ = tgtIpp[2] + (targetSlice.Columns - 1) * 0.5 * targetSlice.PixelSpacingX * tgtIop[2]
                                     + (targetSlice.Rows - 1) * 0.5 * targetSlice.PixelSpacingY * tgtIop[5];

        double curCenterX = targetVp.MprCenterPoint?[0] ?? oldCenterX;
        double curCenterY = targetVp.MprCenterPoint?[1] ?? oldCenterY;
        double curCenterZ = targetVp.MprCenterPoint?[2] ?? oldCenterZ;

        double[] center3d;
        if (pivot3D != null)
        {
            double px = pivot3D.Value.x;
            double py = pivot3D.Value.y;
            double pz = pivot3D.Value.z;

            // Rotate target center around the 3D crosshair pivot point
            double dx = curCenterX - px;
            double dy = curCenterY - py;
            double dz = curCenterZ - pz;
            var rot = RotateVector(dx, dy, dz, kx, ky, kz, deltaAngleRadians);

            center3d = new[] { px + rot.x, py + rot.y, pz + rot.z };
        }
        else
        {
            center3d = new[] { curCenterX, curCenterY, curCenterZ };
        }

        int outW = targetSlice.Columns;
        int outH = targetSlice.Rows;
        double spX = targetSlice.PixelSpacingX;
        double spY = targetSlice.PixelSpacingY;

        var obliqueSlice = MprService.GenerateObliqueSlice(
            volCtx,
            center3d,
            rVec,
            cVec,
            outW,
            outH,
            spX,
            spY,
            targetVp.ProjectionMode,
            targetVp.SlabThicknessMm);
        if (obliqueSlice != null)
        {
            targetVp.MprRowDir = rVec;
            targetVp.MprColDir = cVec;
            targetVp.MprCenterPoint = center3d;

            if (targetVp.CurrentSeries != null && targetVp.CurrentSliceIndex >= 0 && targetVp.CurrentSliceIndex < targetVp.CurrentSeries.Slices.Count)
            {
                targetVp.CurrentSeries.Slices[targetVp.CurrentSliceIndex] = obliqueSlice;
            }
            targetVp.RenderCurrentSlice();
            targetVp.NotifyOverlayChanges();
            UpdateMprLines();
        }
    }

    /// <summary>
    /// Translates an MPR plane in 3D space. If the viewport was rotated obliquely,
    /// preserves its rotated oblique orientation and generates the shifted slice in real-time.
    /// </summary>
    public void TranslateMprPlane(OrthogonalPlane targetPlane, double newX, double newY, double newZ)
    {
        var targetVp = GetMprViewportForPlane(targetPlane);
        if (targetVp?.CurrentSlice == null) return;

        if (targetVp.HasCustomMprOrientation)
        {
            var r = targetVp.MprRowDir!;
            var c = targetVp.MprColDir!;
            double nx = r[1] * c[2] - r[2] * c[1];
            double ny = r[2] * c[0] - r[0] * c[2];
            double nz = r[0] * c[1] - r[1] * c[0];
            double nlen = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nlen > 1e-4) { nx /= nlen; ny /= nlen; nz /= nlen; }

            var curCenter = targetVp.MprCenterPoint ?? new double[] { 
                targetVp.CurrentSlice.ImagePositionPatient![0], 
                targetVp.CurrentSlice.ImagePositionPatient![1], 
                targetVp.CurrentSlice.ImagePositionPatient![2] 
            };

            double deltaN = (newX - curCenter[0]) * nx + (newY - curCenter[1]) * ny + (newZ - curCenter[2]) * nz;
            double[] newCenter = new double[] {
                curCenter[0] + deltaN * nx,
                curCenter[1] + deltaN * ny,
                curCenter[2] + deltaN * nz
            };
            targetVp.MprCenterPoint = newCenter;

            var volCtx = MprService.GetVolumeContext(targetVp.OriginalSeries ?? targetVp.CurrentSeries!);
            if (volCtx != null)
            {
                var obliqueSlice = MprService.GenerateObliqueSlice(
                    volCtx, 
                    newCenter, 
                    r, 
                    c, 
                    targetVp.CurrentSlice.Columns, 
                    targetVp.CurrentSlice.Rows, 
                    targetVp.CurrentSlice.PixelSpacingX, 
                    targetVp.CurrentSlice.PixelSpacingY,
                    targetVp.ProjectionMode,
                    targetVp.SlabThicknessMm);

                if (obliqueSlice != null)
                {
                    if (targetVp.CurrentSeries != null && targetVp.CurrentSliceIndex >= 0 && targetVp.CurrentSliceIndex < targetVp.CurrentSeries.Slices.Count)
                    {
                        targetVp.CurrentSeries.Slices[targetVp.CurrentSliceIndex] = obliqueSlice;
                    }
                    targetVp.RenderCurrentSlice();
                    targetVp.NotifyOverlayChanges();
                    UpdateMprLines();
                }
            }
        }
        else
        {
            if (targetVp.CurrentSeries != null)
            {
                var proj = SynchronizationService.ProjectPointToSeries(newX, newY, newZ, targetVp.CurrentSeries);
                if (proj != null && proj.Value.sliceIndex != targetVp.CurrentSliceIndex)
                {
                    targetVp.SetSliceIndex(proj.Value.sliceIndex);
                    UpdateMprLines();
                }
            }
        }
    }

    public void CloseSeries(SeriesModel series)
    {
        foreach (var p in Patients)
        {
            foreach (var st in p.Studies)
            {
                st.Series.Remove(series);
            }
        }
        foreach (var vp in Viewports)
        {
            if (vp.CurrentSeries == series || vp.OriginalSeries == series)
            {
                vp.CurrentSeries = null;
                vp.OriginalSeries = null;
                vp.DisplayImage = null;
                vp.NotifyOverlayChanges();
            }
        }
        UpdateFilteredSeries();
        UpdateScoutLines();
        StatusMessage = $"Closed series: {series.DisplayTitle}";
    }

    public void ClosePatient(PatientGroupModel pGroup)
    {
        var patient = Patients.FirstOrDefault(p => p.Id == pGroup.PatientId && p.Name == pGroup.PatientName);
        if (patient != null)
        {
            var study = patient.Studies.FirstOrDefault(s => s.StudyInstanceUid == pGroup.StudyInstanceUid);
            var affectedSeries = study != null 
                ? study.Series.ToHashSet() 
                : patient.Studies.SelectMany(s => s.Series).ToHashSet();

            if (study != null)
            {
                patient.Studies.Remove(study);
                if (patient.Studies.Count == 0)
                {
                    Patients.Remove(patient);
                }
            }
            else
            {
                Patients.Remove(patient);
            }

            foreach (var vp in Viewports)
            {
                if (vp.CurrentSeries != null && (affectedSeries.Contains(vp.CurrentSeries) || (vp.OriginalSeries != null && affectedSeries.Contains(vp.OriginalSeries))))
                {
                    vp.CurrentSeries = null;
                    vp.OriginalSeries = null;
                    vp.DisplayImage = null;
                    vp.NotifyOverlayChanges();
                }
            }
            UpdateFilteredSeries();
            UpdateScoutLines();
            StatusMessage = $"Closed: {pGroup.HeaderText}";
        }
    }

    public void CloseActivePatient()
    {
        var (patient, study, curSeries) = GetCurrentPatientContext();
        if (patient == null && Patients.Count > 0)
        {
            patient = Patients.FirstOrDefault();
        }

        if (patient == null)
        {
            StatusMessage = "No active patient to close.";
            return;
        }

        var affectedSeries = patient.Studies.SelectMany(s => s.Series).ToHashSet();
        Patients.Remove(patient);

        foreach (var vp in Viewports)
        {
            if (vp.CurrentSeries != null && (affectedSeries.Contains(vp.CurrentSeries) || (vp.OriginalSeries != null && affectedSeries.Contains(vp.OriginalSeries))))
            {
                vp.CurrentSeries = null;
                vp.OriginalSeries = null;
                vp.DisplayImage = null;
                vp.NotifyOverlayChanges();
            }
        }
        UpdateFilteredSeries();
        UpdateScoutLines();
        StatusMessage = $"Closed patient: {patient.Name}";
    }

    public void CloseAllPatients()
    {
        if (IsMprMode)
        {
            Exit3DMpr();
        }
        SetGridLayout(1, 1);
        CloseAllPanels();
        Patients.Clear();
        UpdateFilteredSeries();
        UpdateScoutLines();
        StatusMessage = "All patients closed.";
    }

    public void SetActiveSlabProjection(SlabProjectionMode mode, double? thicknessMm = null, bool applyToAllMpr = false)
    {
        if (ActiveViewport == null) return;

        if (applyToAllMpr && IsMprMode)
        {
            for (int i = 0; i < 3 && i < Viewports.Count; i++)
            {
                Viewports[i].SetProjectionMode(mode, thicknessMm);
            }
        }
        else
        {
            ActiveViewport.SetProjectionMode(mode, thicknessMm);
        }

        string thickInfo = ActiveViewport.SlabThicknessMm > 0.1 ? $" ({ActiveViewport.SlabThicknessMm:0.#} mm)" : " (Thin slice)";
        StatusMessage = $"Slab Projection set to: {ActiveViewport.ProjectionModeShortName}{thickInfo}";
    }

    public void SetActiveSlabThickness(double thicknessMm, bool applyToAllMpr = false)
    {
        if (ActiveViewport == null) return;

        if (applyToAllMpr && IsMprMode)
        {
            for (int i = 0; i < 3 && i < Viewports.Count; i++)
            {
                Viewports[i].SetSlabThicknessMm(thicknessMm);
            }
        }
        else
        {
            ActiveViewport.SetSlabThicknessMm(thicknessMm);
        }

        string thickInfo = ActiveViewport.SlabThicknessMm > 0.1 ? $"{ActiveViewport.SlabThicknessMm:0.#} mm" : "Thin (1 slice)";
        StatusMessage = $"Slab Thickness set to: {thickInfo} [{ActiveViewport.ProjectionModeShortName}]";
    }

    public void SplitActiveSeries(SplitCriterion criterion = SplitCriterion.Auto)
    {
        if (ActiveViewport?.CurrentSeries == null)
        {
            StatusMessage = "No active series selected to split.";
            return;
        }

        SplitGivenSeries(ActiveViewport.CurrentSeries, criterion);
    }

    public void SplitGivenSeries(SeriesModel series, SplitCriterion criterion = SplitCriterion.Auto)
    {
        var clickedSeries = series;

        // If user clicked Split on an already-split sub-series, resolve the original parent series
        if (series.SeriesInstanceUid.Contains("_split_"))
        {
            string baseUid = series.SeriesInstanceUid.Split("_split_")[0];
            var parentSeries = Patients
                .SelectMany(p => p.Studies)
                .SelectMany(st => st.Series)
                .FirstOrDefault(s => s.SeriesInstanceUid == baseUid);
            if (parentSeries != null)
            {
                series = parentSeries;
            }
        }

        if (!SplitSeriesService.CanSplit(series))
        {
            StatusMessage = $"Series '{series.DisplayTitle}' cannot be split (no multiple b-values, phases, or distinct TEs found).";
            return;
        }

        var splitResults = SplitSeriesService.SplitSeries(series, criterion);
        if (splitResults.Count <= 1)
        {
            StatusMessage = $"Could not split series '{series.DisplayTitle}'.";
            return;
        }

        // Add split series to parent study (replacing any previous split of the same parent series)
        var patient = Patients.FirstOrDefault(p => p.Studies.Any(st => st.Series.Contains(series)));
        var study = patient?.Studies.FirstOrDefault(st => st.Series.Contains(series));

        if (study != null)
        {
            string splitPrefix = $"{series.SeriesInstanceUid}_split_";
            var oldSplits = study.Series.Where(s => s.SeriesInstanceUid.StartsWith(splitPrefix, StringComparison.Ordinal)).ToList();
            foreach (var oldSub in oldSplits)
            {
                study.Series.Remove(oldSub);
            }

            int originalIdx = study.Series.IndexOf(series);
            foreach (var sub in splitResults)
            {
                if (!study.Series.Any(s => s.SeriesInstanceUid == sub.SeriesInstanceUid))
                {
                    study.Series.Insert(Math.Min(study.Series.Count, ++originalIdx), sub);
                }
            }
            UpdateFilteredSeries();
        }

        // If viewport was maximized, restore grid mode so all viewports are visible
        if (IsMaximizedPanel)
        {
            IsMaximizedPanel = false;
            UpdateDisplayedViewports();
        }

        int currentSlots = GridRows * GridColumns;
        var displayedViewports = Viewports.Take(currentSlots).ToList();

        // 1. Identify the viewport currently displaying this series (or its split child)
        var sourceVp = displayedViewports.FirstOrDefault(v => v.CurrentSeries == clickedSeries || v.CurrentSeries == series);
        if (sourceVp == null && ActiveViewport != null && displayedViewports.Contains(ActiveViewport))
        {
            sourceVp = ActiveViewport;
        }

        var oldSlice = sourceVp?.CurrentSlice;

        if (sourceVp != null)
        {
            // Replace the unsplit series with the first sub-series in the existing viewport
            int initSlice0 = oldSlice != null ? SynchronizationService.FindClosestSliceIndex(oldSlice, splitResults[0]) : 0;
            sourceVp.LoadSeries(splitResults[0], initSlice0);

            var remainingSubSeries = splitResults.Skip(1).ToList();

            // Find all empty viewports currently in the grid (excluding sourceVp)
            var emptyViewports = displayedViewports
                .Where(v => v != sourceVp && (v.CurrentSeries == null || v.DisplayImage == null))
                .ToList();

            // If not enough empty viewports to place the remaining sub-series, expand the grid
            int deficit = remainingSubSeries.Count - emptyViewports.Count;
            if (deficit > 0)
            {
                int totalNeeded = currentSlots + deficit;
                var (newRows, newCols) = CalculateExpandedGrid(GridRows, GridColumns, totalNeeded);
                if (newRows != GridRows || newCols != GridColumns)
                {
                    SetGridLayout(newRows, newCols);
                    int newTotalSlots = GridRows * GridColumns;
                    emptyViewports = Viewports
                        .Take(newTotalSlots)
                        .Where(v => v != sourceVp && (v.CurrentSeries == null || v.DisplayImage == null))
                        .ToList();
                }
            }

            // Assign the remaining sub-series to empty slots
            for (int i = 0; i < remainingSubSeries.Count && i < emptyViewports.Count; i++)
            {
                int initSliceI = oldSlice != null ? SynchronizationService.FindClosestSliceIndex(oldSlice, remainingSubSeries[i]) : 0;
                emptyViewports[i].LoadSeries(remainingSubSeries[i], initSliceI);
            }

            SetActiveViewport(sourceVp);
        }
        else
        {
            // Series being split was not currently loaded in any visible viewport (e.g. triggered from sidebar)
            var emptyViewports = displayedViewports
                .Where(v => v.CurrentSeries == null || v.DisplayImage == null)
                .ToList();

            int deficit = splitResults.Count - emptyViewports.Count;
            if (deficit > 0)
            {
                int totalNeeded = currentSlots + deficit;
                var (newRows, newCols) = CalculateExpandedGrid(GridRows, GridColumns, totalNeeded);
                if (newRows != GridRows || newCols != GridColumns)
                {
                    SetGridLayout(newRows, newCols);
                    int newTotalSlots = GridRows * GridColumns;
                    emptyViewports = Viewports
                        .Take(newTotalSlots)
                        .Where(v => v.CurrentSeries == null || v.DisplayImage == null)
                        .ToList();
                }
            }

            if (emptyViewports.Count == 0 && ActiveViewport != null)
            {
                emptyViewports.Add(ActiveViewport);
            }

            for (int i = 0; i < splitResults.Count && i < emptyViewports.Count; i++)
            {
                emptyViewports[i].LoadSeries(splitResults[i]);
            }

            if (emptyViewports.Count > 0)
            {
                SetActiveViewport(emptyViewports[0]);
            }
        }

        StatusMessage = $"Split '{series.DisplayTitle}' into {splitResults.Count} sub-series. Preserved open series in {GridRows}x{GridColumns} grid.";
    }

    private static (int newRows, int newCols) CalculateExpandedGrid(int currentRows, int currentCols, int totalNeeded)
    {
        int rows = currentRows;
        int cols = currentCols;

        while (rows * cols < totalNeeded)
        {
            if (cols <= rows && cols < 6)
            {
                cols++;
            }
            else if (rows < 5)
            {
                rows++;
            }
            else if (cols < 6)
            {
                cols++;
            }
            else
            {
                break;
            }
        }

        return (rows, cols);
    }

    private void OnViewportImageRendered(ViewportViewModel source)
    {
        UpdateScoutLines();

        if (IsMprMode)
        {
            UpdateMprLines();
            return;
        }

        if (CurrentSyncMode == SyncMode.Off || source != ActiveViewport || IsSyncSuspended || IsMaximizedPanel)
            return;

        int totalVisible = GridRows * GridColumns;
        for (int i = 0; i < totalVisible; i++)
        {
            var other = Viewports[i];
            if (other == source || other.CurrentSeries == null) continue;

            if (source.CurrentSlice == null || other.CurrentSlice == null) continue;

            string sourceCat = GetSliceOrientationCategory(source.CurrentSlice);
            string otherCat = GetSliceOrientationCategory(other.CurrentSlice);

            // Sync slice position ONLY if both viewports share the same plane orientation (e.g. Axial with Axial, Coronal with Coronal)
            // Orthogonal planes (e.g. Coronal vs Axial) must NOT sync slice index (they use Scout reference lines instead!)
            if (SyncSlicePosition && sourceCat == otherCat && sourceCat != "Unknown")
            {
                if (CurrentSyncMode == SyncMode.Automatic)
                {
                    int matchedIndex = SynchronizationService.FindClosestSliceIndex(source.CurrentSlice, other.CurrentSeries);
                    if (matchedIndex != other.CurrentSliceIndex)
                    {
                        other.SetSliceIndex(matchedIndex);
                    }
                }
                else if (CurrentSyncMode == SyncMode.Manual)
                {
                    if (_manualSliceOffsets.TryGetValue(other.ViewportIndex, out int offset))
                    {
                        int targetIndex = source.CurrentSliceIndex + offset;
                        other.SetSliceIndex(targetIndex);
                    }
                }
            }

            // Sync Pan/Zoom ONLY between viewports sharing the same orientation plane
            if (SyncZoomPan && (sourceCat == otherCat || sourceCat == "Unknown"))
            {
                other.Zoom = CalculatePhysicalZoom(source, other);
                other.PanX = source.PanX;
                other.PanY = source.PanY;
            }

            if (SyncWindowLevel)
            {
                other.WindowCenter = source.WindowCenter;
                other.WindowWidth = source.WindowWidth;
                other.RenderCurrentSlice();
            }
        }
    }

    private void OnViewportTransformChanged(ViewportViewModel source)
    {
        if (source != ActiveViewport || IsSyncSuspended || IsMaximizedPanel) return;
        // Never sync transform if sync is OFF
        if (CurrentSyncMode == SyncMode.Off) return;

        string sourceCat = GetSliceOrientationCategory(source.CurrentSlice);

        int totalVisible = GridRows * GridColumns;
        for (int i = 0; i < totalVisible; i++)
        {
            var other = Viewports[i];
            if (other == source || other.CurrentSeries == null) continue;

            // Only sync between matching orientation planes (e.g. Sagittal with Sagittal, Axial with Axial)
            string otherCat = GetSliceOrientationCategory(other.CurrentSlice);
            if (sourceCat != otherCat && sourceCat != "Unknown") continue;

            if (SyncZoomPan)
            {
                double targetZoom = CalculatePhysicalZoom(source, other);
                if (Math.Abs(other.Zoom - targetZoom) > 0.0001)
                    other.Zoom = targetZoom;
                if (Math.Abs(other.PanX - source.PanX) > 0.1)
                    other.PanX = source.PanX;
                if (Math.Abs(other.PanY - source.PanY) > 0.1)
                    other.PanY = source.PanY;
            }
        }
    }
}
