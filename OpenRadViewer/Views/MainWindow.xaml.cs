// @purpose: Primary application shell hosting the top menu, series thumbnail bar, multi-viewport layout grid, hanging protocols, and keyboard shortcut dispatching.
// @depends: OpenRadViewer.ViewModels, OpenRadViewer.Core.Services, OpenRadViewer.Core.Config, OpenRadViewer.Core.Models
// @exports: MainWindow

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using OpenRadViewer.Core.Config;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;
using OpenRadViewer.ViewModels;

namespace OpenRadViewer.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel => (MainViewModel)DataContext;

    public static List<string> StartupPaths { get; set; } = new();
    public static string? StartupFolder
    {
        get => StartupPaths.FirstOrDefault();
        set
        {
            if (!string.IsNullOrEmpty(value) && !StartupPaths.Contains(value))
                StartupPaths.Add(value);
        }
    }
    private DateTime _lastXKeyPressTime = DateTime.MinValue;

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                Core.Services.SingleInstanceManager.ShowWindow(hwnd, Core.Services.SingleInstanceManager.SW_RESTORE);
                Core.Services.SingleInstanceManager.SetForegroundWindow(hwnd);
            }
        }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        // Enforce dark title bar (matching Antigravity / VS Code)
        DarkWindowHelper.ApplyDarkTitleBar(this);

        // Safely set window icon
        try
        {
            string iconFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_icon.png");
            if (File.Exists(iconFile))
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconFile, UriKind.Absolute));
            }
        }
        catch { }

        // Allow Drag and Drop of DICOM folders/files directly from Windows Explorer
        AllowDrop = true;
        Drop += OnWindowDrop;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Loaded += OnWindowLoaded;

        // Wire 6x5 Grid Matrix interactive selection
        GridMatrixPicker.LayoutSelected += (rows, cols) =>
        {
            ViewModel.SetGridLayout(rows, cols);
            GridPickerPopup.IsOpen = false;
        };

        ReferenceGuideService.Instance.ReferencesChanged += () => Dispatcher.Invoke(RebuildDynamicReferenceMenus);
        RebuildDynamicReferenceMenus();

        ViewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkWindowHelper.ApplyDarkTitleBar(this);
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log");
        File.AppendAllText(logPath, "OnWindowLoaded entered\n");
        try
        {
            if (StartupPaths.Count > 0)
            {
                File.AppendAllText(logPath, "Scanning startup paths: " + string.Join(", ", StartupPaths) + "\n");
                await ViewModel.ScanPathsAsync(StartupPaths);
            }
            else
            {
                File.AppendAllText(logPath, "Started with clean empty workspace\n");
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, "Exception in OnWindowLoaded: " + ex + "\n");
        }

        // Initialize local PACS C-STORE SCP listener in background
        _ = Task.Run(() =>
        {
            try
            {
                PacsService.Instance.StartListener(out _);
            }
            catch { }
        });
    }

    private async void OnLoadSampleStudy(object sender, RoutedEventArgs e)
    {
        try
        {
            string sampleDir = DicomSampleGenerator.GenerateSyntheticStudy(AppDomain.CurrentDomain.BaseDirectory);
            await ViewModel.ScanFolderAsync(sampleDir);
        }
        catch { }
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths != null && paths.Length > 0)
            {
                _ = ViewModel.ScanPathsAsync(paths);
            }
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        var active = vm.ActiveViewport;
        bool isCtrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool isShift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool isAlt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;

        // 0. Voice Dictation Toggle (F4)
        if (key == Key.F4 && !isCtrl && !isShift && !isAlt)
        {
            if (!vm.IsProtocolsPanelOpen)
            {
                vm.IsProtocolsPanelOpen = true;
            }
            OnToggleVoiceDictation(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // 0b. AI Protocol Formatting (F5)
        if (key == Key.F5 && !isCtrl && !isShift && !isAlt)
        {
            if (!vm.IsProtocolsPanelOpen)
            {
                vm.IsProtocolsPanelOpen = true;
            }
            OnFormatProtocolWithAi(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // 0c. Conversational Voice AI Refinement (F6)
        if (key == Key.F6 && !isCtrl && !isShift && !isAlt)
        {
            if (!vm.IsProtocolsPanelOpen)
            {
                vm.IsProtocolsPanelOpen = true;
            }
            OnToggleVoiceRefine(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Allow normal typing, deletion, and copy/paste when a TextBox is focused
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
        {
            if (isCtrl && !isAlt && !isShift && key == Key.S && vm.IsProtocolsPanelOpen)
            {
                OnSaveAsDocx(this, new RoutedEventArgs());
                e.Handled = true;
            }
            return;
        }

        // 0. High priority UI Toggle: Menu Bar (Ctrl+Alt+M)
        if (isCtrl && isAlt && key == Key.M)
        {
            vm.ToggleMenuBar();
            e.Handled = true;
            return;
        }

        // 1. File & Export shortcuts
        if (isCtrl && isShift && e.Key == Key.O)
        {
            OnScanFolder(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (isCtrl && !isShift && !isAlt && e.Key == Key.O)
        {
            OnOpenFiles(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (isCtrl && !isShift && !isAlt && e.Key == Key.E)
        {
            OnExportImages(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (isCtrl && !isShift && !isAlt && e.Key == Key.C)
        {
            OnCopyToClipboard(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Duplicate ROI across series (Ctrl + Shift + D)
        if (isCtrl && isShift && !isAlt && (key == Key.D || e.Key == Key.D))
        {
            vm.DuplicateActiveRoiToOtherSeries();
            e.Handled = true;
            return;
        }

        // Delete active measurements or vertebra labels
        if (e.Key == Key.Delete)
        {
            if (active != null)
            {
                var selVert = active.VertebraLabels.FirstOrDefault(v => v.IsSelected && v.SliceIndex == active.CurrentSliceIndex);
                var selMeas = active.Measurements.FirstOrDefault(m => m.IsSelected);

                if (selVert != null)
                {
                    active.VertebraLabels.Remove(selVert);
                    vm.StatusMessage = $"Deleted vertebra label ({selVert.Level}).";
                }
                else if (selMeas != null)
                {
                    active.Measurements.Remove(selMeas);
                    vm.StatusMessage = "Deleted selected measurement/ROI.";
                }
                else if (vm.LeftTool == MouseToolAction.VertebraLabel)
                {
                    var lastOnSlice = active.VertebraLabels.LastOrDefault(v => v.SliceIndex == active.CurrentSliceIndex);
                    if (lastOnSlice != null)
                    {
                        active.VertebraLabels.Remove(lastOnSlice);
                        vm.StatusMessage = $"Deleted vertebra label ({lastOnSlice.Level}).";
                    }
                }
                else if (active.Measurements.Count > 0)
                {
                    active.Measurements.RemoveAt(active.Measurements.Count - 1);
                    vm.StatusMessage = "Deleted last measurement.";
                }
                else
                {
                    var lastVert = active.VertebraLabels.LastOrDefault(v => v.SliceIndex == active.CurrentSliceIndex);
                    if (lastVert != null)
                    {
                        active.VertebraLabels.Remove(lastVert);
                        vm.StatusMessage = $"Deleted vertebra label ({lastVert.Level}).";
                    }
                }
            }
            e.Handled = true;
            return;
        }

        // Escape / Shift+Escape: close active panel / close all panels
        if (e.Key == Key.Escape)
        {
            if (isShift)
            {
                vm.CloseAllPanels();
                e.Handled = true;
                return;
            }

            // If an ROI is currently selected or 3D cursor active, clear it first
            bool hadSelectionOrCursor = false;
            if (active != null && active.Measurements.Any(m => m.IsSelected))
            {
                foreach (var m in active.Measurements) m.IsSelected = false;
                hadSelectionOrCursor = true;
            }
            if (vm.Viewports.Any(vp => vp.ShowCrosshair3D))
            {
                vm.Clear3DCursor();
                hadSelectionOrCursor = true;
            }

            if (!hadSelectionOrCursor)
            {
                // Escape closes the active series panel
                vm.CloseActivePanel();
            }

            e.Handled = true;
            return;
        }

        // 2. Layout & Panel navigation shortcuts
        if (e.Key == Key.Tab)
        {
            if (isShift) vm.ActivatePreviousPanel();
            else vm.ActivateNextPanel();
            e.Handled = true;
            return;
        }
        if (isCtrl && !isShift && !isAlt && key == Key.M)
        {
            vm.ToggleMaximizeActivePanel();
            e.Handled = true;
            return;
        }
        if (isCtrl && e.Key == Key.F4)
        {
            vm.CloseActivePanel();
            e.Handled = true;
            return;
        }
        if (isShift && e.Key == Key.F4)
        {
            vm.CloseAllPanels();
            e.Handled = true;
            return;
        }
        // 3. UI Toggles
        if (isCtrl && isAlt && (key == Key.F5 || key == Key.M))
        {
            vm.ToggleMenuBar();
            e.Handled = true;
            return;
        }
        if (isCtrl && isAlt && e.Key == Key.F2)
        {
            vm.ShowSeriesPreviewBar = !vm.ShowSeriesPreviewBar;
            e.Handled = true;
            return;
        }
        if (isCtrl && isAlt && e.Key == Key.F3)
        {
            vm.ShowToolbars = !vm.ShowToolbars;
            e.Handled = true;
            return;
        }
        if (!isCtrl && !isAlt && !isShift && e.Key == Key.F5)
        {
            vm.CycleSyncMode();
            e.Handled = true;
            return;
        }
        if (!isCtrl && !isAlt && e.Key == Key.F)
        {
            OnToggleFullScreen(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (isCtrl && !isAlt && !isShift && key == Key.S && vm.IsProtocolsPanelOpen)
        {
            OnSaveAsDocx(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Handle X chord trigger (if X is pressed alone)
        if (key == Key.X && !isCtrl && !isAlt && !isShift)
        {
            _lastXKeyPressTime = DateTime.UtcNow;
            e.Handled = true;
            return;
        }

        // Quick Grid shortcuts: X + [1-9] (both holding X or sequential X -> digit chord)
        bool isXChord = (Keyboard.IsKeyDown(Key.X) || (DateTime.UtcNow - _lastXKeyPressTime).TotalMilliseconds < 1500) && !isCtrl && !isAlt;
        if (isXChord)
        {
            if (key is Key.D1 or Key.NumPad1) { _lastXKeyPressTime = DateTime.MinValue; vm.SetGridLayout(1, 1); e.Handled = true; return; }
            if (key is Key.D2 or Key.NumPad2) { _lastXKeyPressTime = DateTime.MinValue; vm.ApplySmartLayout(1, 2); e.Handled = true; return; }
            if (key is Key.D3 or Key.NumPad3) { _lastXKeyPressTime = DateTime.MinValue; vm.ApplySmartLayout(1, 3); e.Handled = true; return; }
            if (key is Key.D4 or Key.NumPad4) { _lastXKeyPressTime = DateTime.MinValue; vm.ApplySmartLayout(2, 2); e.Handled = true; return; }
            if (key is Key.D5 or Key.NumPad5) { _lastXKeyPressTime = DateTime.MinValue; vm.ApplySmartLayout(1, 5); e.Handled = true; return; }
            if (key is Key.D6 or Key.NumPad6) { _lastXKeyPressTime = DateTime.MinValue; vm.ApplySmartLayout(2, 3); e.Handled = true; return; }
            if (key is Key.D7 or Key.NumPad7) { _lastXKeyPressTime = DateTime.MinValue; vm.ApplySmartLayout(2, 4); e.Handled = true; return; }
            if (key is Key.D8 or Key.NumPad8) { _lastXKeyPressTime = DateTime.MinValue; vm.ApplySmartLayout(2, 4); e.Handled = true; return; }
            if (key is Key.D9 or Key.NumPad9) { _lastXKeyPressTime = DateTime.MinValue; vm.ApplySmartLayout(3, 3); e.Handled = true; return; }
            _lastXKeyPressTime = DateTime.MinValue;
        }
        else
        {
            _lastXKeyPressTime = DateTime.MinValue;
        }

        // MPR Orthogonal Planes: Alt+1 (Coronal), Alt+2 (Sagittal), Alt+3 (Axial) & 3D MPR: Alt+M or Ctrl+M
        if (isAlt && !isCtrl && !isShift)
        {
            if (key is Key.D1 or Key.NumPad1) { vm.SwitchActivePlane(OrthogonalPlane.Coronal); e.Handled = true; return; }
            if (key is Key.D2 or Key.NumPad2) { vm.SwitchActivePlane(OrthogonalPlane.Sagittal); e.Handled = true; return; }
            if (key is Key.D3 or Key.NumPad3) { vm.SwitchActivePlane(OrthogonalPlane.Axial); e.Handled = true; return; }
            if (key == Key.M) { vm.Toggle3DMpr(); e.Handled = true; return; }
        }

        // Database Archive: Ctrl+D
        if (isCtrl && !isShift && !isAlt && (key == Key.D || e.Key == Key.D))
        {
            OnOpenDatabase(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // PACS Archive Search & Retrieve: Ctrl+P
        if (isCtrl && !isShift && !isAlt && (key == Key.P || e.Key == Key.P))
        {
            OnOpenPacsSearch(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // 3D Volume Rendering (3D VR): Ctrl+Shift+V
        if (isCtrl && isShift && !isAlt && e.Key == Key.V)
        {
            OnOpen3DVr(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Scout / Cross-reference lines toggle: Ctrl+F12
        if (isCtrl && e.Key == Key.F12)
        {
            vm.ToggleScoutLines();
            e.Handled = true;
            return;
        }

        // Toggle MPR Reference Axes: H or Ctrl+H
        if ((!isAlt && !isShift && e.Key == Key.H) || (isCtrl && e.Key == Key.H))
        {
            vm.ToggleMprAxes();
            e.Handled = true;
            return;
        }

        // Custom Window: Ctrl+W
        if (isCtrl && !isShift && !isAlt && e.Key == Key.W)
        {
            OnCustomWindow(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // 4. Tools Shortcuts (Q, B, W, M, Z, L, E, U, A, Y, P, T)
        if (!isCtrl && !isAlt && !isShift)
        {
            switch (e.Key)
            {
                case Key.Q: vm.AssignLeftTool(MouseToolAction.Cursor3D); e.Handled = true; return;
                case Key.B: vm.AssignLeftTool(MouseToolAction.BrowseSeries); e.Handled = true; return;
                case Key.W: vm.AssignLeftTool(MouseToolAction.WindowLevel); e.Handled = true; return;
                case Key.M: vm.AssignLeftTool(MouseToolAction.Pan); e.Handled = true; return;
                case Key.Z: vm.AssignLeftTool(MouseToolAction.Zoom); e.Handled = true; return;
                case Key.L: vm.AssignLeftTool(MouseToolAction.MeasureLength); e.Handled = true; return;
                case Key.E: vm.AssignLeftTool(MouseToolAction.MeasureEllipse); e.Handled = true; return;
                case Key.U: vm.AssignLeftTool(MouseToolAction.MeasureRectangle); e.Handled = true; return;
                case Key.A: vm.AssignLeftTool(MouseToolAction.MeasureAngle); e.Handled = true; return;
                case Key.Y: vm.AssignLeftTool(MouseToolAction.Arrow); e.Handled = true; return;
                case Key.P: vm.AssignLeftTool(MouseToolAction.Pencil); e.Handled = true; return;
                case Key.T: vm.AssignLeftTool(MouseToolAction.TextAnnotation); e.Handled = true; return;
            }
        }

        // 5. Active Viewport Slices & Presets
        if (active != null)
        {
            if (e.Key == Key.Space)
            {
                active.ToggleCine();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F11)
            {
                active.ToggleNegative();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up)
            {
                active.PreviousImage();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down)
            {
                active.NextImage();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.PageUp)
            {
                active.StepImages(-10);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.PageDown)
            {
                active.StepImages(10);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Home)
            {
                active.SetSliceIndex(0);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.End)
            {
                active.SetSliceIndex(active.TotalSlices - 1);
                e.Handled = true;
                return;
            }

            // Presets 0-8 (8 = Liver CT)
            if (!isCtrl && !isAlt && e.Key >= Key.D0 && e.Key <= Key.D8)
            {
                int presetNum = e.Key - Key.D0;
                active.ApplyWindowPresetNumber(presetNum);
                e.Handled = true;
                return;
            }

            // Transformations: Rotate / Flip
            if (isCtrl && !isShift && e.Key == Key.OemOpenBrackets) // Ctrl+[
            {
                active.RotateCounterClockwise();
                e.Handled = true;
                return;
            }
            if (isCtrl && !isShift && e.Key == Key.OemCloseBrackets) // Ctrl+]
            {
                active.RotateClockwise();
                e.Handled = true;
                return;
            }
            if (isCtrl && isShift && e.Key == Key.OemOpenBrackets) // Ctrl+Shift+[
            {
                active.FlipHorizontalToggle();
                e.Handled = true;
                return;
            }
            if (isCtrl && isShift && e.Key == Key.OemCloseBrackets) // Ctrl+Shift+]
            {
                active.FlipVerticalToggle();
                e.Handled = true;
                return;
            }
            if (isCtrl && isShift && e.Key == Key.OemBackslash) // Ctrl+Shift+\
            {
                active.ResetTransform();
                e.Handled = true;
                return;
            }

            // Zoom presets
            if (isCtrl)
            {
                if (e.Key == Key.D0 || e.Key == Key.NumPad0) { active.ResetTransform(); e.Handled = true; return; }
                if (e.Key == Key.D1 || e.Key == Key.NumPad1) { active.ApplyZoomCentered(1.0); e.Handled = true; return; }
                if (e.Key == Key.D2 || e.Key == Key.NumPad2) { active.ApplyZoomCentered(2.0); e.Handled = true; return; }
                if (e.Key == Key.D3 || e.Key == Key.NumPad3) { active.ApplyZoomCentered(4.0); e.Handled = true; return; }
                if (e.Key == Key.D4 || e.Key == Key.NumPad4) { active.ApplyZoomCentered(8.0); e.Handled = true; return; }
                if (e.Key == Key.OemPlus || e.Key == Key.Add) { active.ApplyZoomCentered(active.Zoom * 1.25); e.Handled = true; return; }
                if (e.Key == Key.OemMinus || e.Key == Key.Subtract) { active.ApplyZoomCentered(active.Zoom / 1.25); e.Handled = true; return; }
            }

            // Delete / Backspace immediately removes selected measurement
            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                var selected = active.Measurements.Where(m => m.IsSelected).ToList();
                if (selected.Count > 0)
                {
                    foreach (var m in selected) active.Measurements.Remove(m);
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    #region Layout & Grid Matrix Actions
    private void OnToggleGridPicker(object sender, RoutedEventArgs e)
    {
        GridPickerPopup.IsOpen = !GridPickerPopup.IsOpen;
    }

    private void OnToggleMaximizePanel(object sender, RoutedEventArgs e) => ViewModel.ToggleMaximizeActivePanel();
    private void OnCloseActivePanel(object sender, RoutedEventArgs e) => ViewModel.CloseActivePanel();
    private void OnCloseAllPanels(object sender, RoutedEventArgs e) => ViewModel.CloseAllPanels();
    private void OnCloseActivePatient(object sender, RoutedEventArgs e) => ViewModel.CloseActivePatient();
    private void OnCloseAllPatients(object sender, RoutedEventArgs e) => ViewModel.CloseAllPatients();

    private void OnOpenCloseMenu(object sender, RoutedEventArgs e)
    {
        if (BtnCloseMenu?.ContextMenu != null)
        {
            BtnCloseMenu.ContextMenu.PlacementTarget = BtnCloseMenu;
            BtnCloseMenu.ContextMenu.IsOpen = true;
        }
        e.Handled = true;
    }

    private void OnSplitSeriesAuto(object sender, RoutedEventArgs e) => ViewModel.SplitActiveSeries(OpenRadViewer.Core.Services.SplitCriterion.Auto);
    private void OnSplitSeriesByBValue(object sender, RoutedEventArgs e) => ViewModel.SplitActiveSeries(OpenRadViewer.Core.Services.SplitCriterion.DiffusionBValue);
    private void OnSplitSeriesByEchoTime(object sender, RoutedEventArgs e) => ViewModel.SplitActiveSeries(OpenRadViewer.Core.Services.SplitCriterion.EchoTime);
    private void OnSplitSeriesByPhase(object sender, RoutedEventArgs e) => ViewModel.SplitActiveSeries(OpenRadViewer.Core.Services.SplitCriterion.TemporalPhase);
    private void OnSplitSeriesByOrientation(object sender, RoutedEventArgs e) => ViewModel.SplitActiveSeries(OpenRadViewer.Core.Services.SplitCriterion.SpatialOrientation);

    private void OnOpenSplitMenu(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SplitContextMenu != null)
        {
            SplitContextMenu.PlacementTarget = BtnSplitDropdown ?? (sender as UIElement) ?? BtnSplitSeries;
            SplitContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            SplitContextMenu.IsOpen = true;
        }
    }

    private void OnOpenSplitMenuRight(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (SplitContextMenu != null)
        {
            SplitContextMenu.PlacementTarget = BtnSplitSeries ?? (sender as UIElement);
            SplitContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            SplitContextMenu.IsOpen = true;
        }
    }
    #endregion

    #region File & Folder Actions
    private void OnScanFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select DICOM Directory to Scan"
        };
        if (dlg.ShowDialog() == true)
        {
            _ = ViewModel.ScanFolderAsync(dlg.FolderName);
        }
    }

    private void OnOpenFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "DICOM Files (*.dcm;*.dicom)|*.dcm;*.dicom|All files (*.*)|*.*",
            Title = "Open DICOM Files"
        };
        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
        {
            _ = ViewModel.ScanPathsAsync(dlg.FileNames, targetFilePath: dlg.FileNames[0]);
        }
    }

    private void OnExportImages(object sender, RoutedEventArgs e)
    {
        var dlg = new ExportDialog(ViewModel) { Owner = this };
        dlg.ShowDialog();
    }

    private void OnCopyToClipboard(object sender, RoutedEventArgs e)
    {
        var active = ViewModel.ActiveViewport;
        if (active?.DisplayImage != null)
        {
            ExportService.CopyBitmapToClipboard(active.DisplayImage);
            ViewModel.StatusMessage = "Current slice copied to clipboard.";
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
    #endregion

    #region Tool & Measurement Actions
    private void OnToggleSync(object sender, RoutedEventArgs e) => ViewModel.CycleSyncMode();
    private void OnLockManualSync(object sender, RoutedEventArgs e) => ViewModel.LockManualSyncOffsets();

    private void OnOpenWindowMenu(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (WindowPresetsContextMenu != null)
        {
            WindowPresetsContextMenu.PlacementTarget = BtnWindowMenu ?? (sender as UIElement) ?? BtnWindowTool;
            WindowPresetsContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            WindowPresetsContextMenu.IsOpen = true;
        }
    }

    private void OnOpenMeasureMenu(object sender, RoutedEventArgs e)
    {
        MeasureContextMenu.PlacementTarget = BtnMeasureMenu;
        MeasureContextMenu.IsOpen = true;
    }

    // Direct Left, Right & Middle (Hold Wheel) toolbar click handlers
    private void OnToolbarCursor3DLeft(object sender, RoutedEventArgs e) => ViewModel.AssignLeftTool(MouseToolAction.Cursor3D);
    private void OnToolbarCursor3DRight(object sender, MouseButtonEventArgs e)
    {
        ViewModel.AssignRightTool(MouseToolAction.Cursor3D);
        e.Handled = true;
    }
    private void OnToolbarCursor3DMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            ViewModel.AssignMiddleTool(MouseToolAction.Cursor3D);
            e.Handled = true;
        }
    }

    private void OnToolbarBrowseLeft(object sender, RoutedEventArgs e) => ViewModel.AssignLeftTool(MouseToolAction.BrowseSeries);
    private void OnToolbarBrowseRight(object sender, MouseButtonEventArgs e)
    {
        ViewModel.AssignRightTool(MouseToolAction.BrowseSeries);
        e.Handled = true;
    }
    private void OnToolbarBrowseMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            ViewModel.AssignMiddleTool(MouseToolAction.BrowseSeries);
            e.Handled = true;
        }
    }

    private void OnToolbarWindowLevelLeft(object sender, RoutedEventArgs e) => ViewModel.AssignLeftTool(MouseToolAction.WindowLevel);
    private void OnToolbarWindowLevelRight(object sender, MouseButtonEventArgs e)
    {
        ViewModel.AssignRightTool(MouseToolAction.WindowLevel);
        e.Handled = true;
    }
    private void OnToolbarWindowLevelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            ViewModel.AssignMiddleTool(MouseToolAction.WindowLevel);
            e.Handled = true;
        }
    }

    private void OnToolbarPanLeft(object sender, RoutedEventArgs e) => ViewModel.AssignLeftTool(MouseToolAction.Pan);
    private void OnToolbarPanRight(object sender, MouseButtonEventArgs e)
    {
        ViewModel.AssignRightTool(MouseToolAction.Pan);
        e.Handled = true;
    }
    private void OnToolbarPanMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            ViewModel.AssignMiddleTool(MouseToolAction.Pan);
            e.Handled = true;
        }
    }

    private void OnToolbarZoomLeft(object sender, RoutedEventArgs e) => ViewModel.AssignLeftTool(MouseToolAction.Zoom);
    private void OnToolbarZoomRight(object sender, MouseButtonEventArgs e)
    {
        ViewModel.AssignRightTool(MouseToolAction.Zoom);
        e.Handled = true;
    }
    private void OnToolbarZoomMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            ViewModel.AssignMiddleTool(MouseToolAction.Zoom);
            e.Handled = true;
        }
    }

    // Menu handlers
    private void OnToolCursor3D(object sender, RoutedEventArgs e) => ViewModel.AssignLeftTool(MouseToolAction.Cursor3D);
    private void OnToolBrowse(object sender, RoutedEventArgs e) => ViewModel.AssignLeftTool(MouseToolAction.BrowseSeries);
    private void OnToolWindowLevel(object sender, RoutedEventArgs e) => ViewModel.AssignLeftTool(MouseToolAction.WindowLevel);
    private void OnToolPan(object sender, RoutedEventArgs e) => ViewModel.AssignLeftTool(MouseToolAction.Pan);
    private void OnToolZoom(object sender, RoutedEventArgs e) => ViewModel.AssignLeftTool(MouseToolAction.Zoom);

    private void OnToolLength(object sender, RoutedEventArgs e)
    {
        ViewModel.AssignLeftTool(MouseToolAction.MeasureLength);
        ViewModel.StatusMessage = "Tool: Length Ruler (L)";
    }

    private void OnToolEllipse(object sender, RoutedEventArgs e)
    {
        ViewModel.AssignLeftTool(MouseToolAction.MeasureEllipse);
        ViewModel.StatusMessage = "Tool: Ellipse ROI (Area, Mean HU, SD) (E)";
    }

    private void OnToolRectangle(object sender, RoutedEventArgs e)
    {
        ViewModel.AssignLeftTool(MouseToolAction.MeasureRectangle);
        ViewModel.StatusMessage = "Tool: Rectangle ROI (U)";
    }

    private void OnToolAngle(object sender, RoutedEventArgs e)
    {
        ViewModel.AssignLeftTool(MouseToolAction.MeasureAngle);
        ViewModel.StatusMessage = "Tool: Angle (A)";
    }

    private void OnToolInsallSalvati(object sender, RoutedEventArgs e)
    {
        ViewModel.AssignLeftTool(MouseToolAction.MeasureInsallSalvati);
        ViewModel.StatusMessage = "Insall-Salvati Ratio: Click 1: Tibial insertion → Click 2: Inferior patellar pole (apex) → Click 3: Superior patellar pole (base)";
    }

    private void OnInstantAiConsult(object sender, RoutedEventArgs e)
    {
        var viewports = ViewModel.DisplayedViewports
            .Where(vp => vp.DisplayImage != null)
            .ToList();

        if (viewports.Count == 0)
        {
            MessageBox.Show(this, "No active DICOM image or series loaded to consult.", "AI Consultation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!AiService.Instance.Settings.AutoIncludeAllViewports && ViewModel.ActiveViewport?.DisplayImage != null)
        {
            viewports = new List<ViewportViewModel> { ViewModel.ActiveViewport };
        }

        var crops = new List<AiImageCrop>();
        foreach (var vp in viewports)
        {
            var crop = ViewportControl.ExtractImageCrop(vp, null);
            if (crop != null) crops.Add(crop);
        }

        if (crops.Count == 0)
        {
            MessageBox.Show(this, "Could not capture image from open viewports.", "AI Consultation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var consultWin = new AiConsultationWindow(crops)
        {
            Owner = this
        };
        consultWin.Show();
    }

    private void OnToolAiLesionCrop(object sender, RoutedEventArgs e)
    {
        ViewModel.AssignLeftTool(MouseToolAction.AiLesionCrop);
        ViewModel.StatusMessage = "Tool: AI Lesion Crop (Gemini / Claude). Drag a rectangle over the lesion to consult AI...";
    }

    private void OnOpenAiMenu(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (AiContextMenu != null)
        {
            AiContextMenu.PlacementTarget = BtnAiDropdown ?? (sender as UIElement) ?? BtnAiConsult;
            AiContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            AiContextMenu.IsOpen = true;
        }
    }

    private void OnOpenAiMenuRight(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (AiContextMenu != null)
        {
            AiContextMenu.PlacementTarget = BtnAiConsult ?? (sender as UIElement);
            AiContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            AiContextMenu.IsOpen = true;
        }
    }

    private void OnOpenAiSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new AiSettingsWindow { Owner = this };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error opening AI settings: {ex.Message}", "AI Settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnConsultAiOnArrowTarget(object sender, RoutedEventArgs e)
    {
        var activeVp = ViewModel.ActiveViewport;
        if (activeVp == null)
        {
            MessageBox.Show(this, "No active viewport selected.", "AI Consultation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Collect arrows across all slices of the active series, prioritizing the active slice
        int sliceIdx = activeVp.CurrentSliceIndex;
        var arrows = activeVp.Measurements
            .Where(m => m.Type == MeasurementType.Arrow && m.Points.Count >= 2)
            .OrderBy(m => m.SliceIndex == sliceIdx ? 0 : 1)
            .ToList();

        if (arrows.Count == 0)
        {
            MessageBox.Show(this,
                "No arrow indicator found in the active series.\n\nUse the Arrow tool (Hotkey: Y) from the Tools menu to point an arrow at the lesion, pathology, or structure first.",
                "AI Arrow Target",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var vc = FindActiveViewportControl();
        if (vc != null)
        {
            vc.TriggerAiConsultationForArrows(arrows);
        }
        else
        {
            MessageBox.Show(this, "Could not locate active viewport control.", "AI Consultation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private ViewportControl? FindActiveViewportControl()
    {
        var activeVm = ViewModel.ActiveViewport;
        if (activeVm == null) return null;
        return FindVisualChildren<ViewportControl>(this).FirstOrDefault(vc => vc.DataContext == activeVm);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        int count = VisualTreeHelper.GetChildrenCount(depObj);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T t)
            {
                yield return t;
            }
            foreach (var childOfChild in FindVisualChildren<T>(child))
            {
                yield return childOfChild;
            }
        }
    }

    private AdrenalWashoutWindow? _adrenalWashoutWindow;

    private void OnOpenAdrenalWashout(object sender, RoutedEventArgs e)
    {
        if (_adrenalWashoutWindow != null && _adrenalWashoutWindow.IsLoaded)
        {
            _adrenalWashoutWindow.Activate();
            return;
        }

        _adrenalWashoutWindow = new AdrenalWashoutWindow(ViewModel)
        {
            Owner = this
        };
        _adrenalWashoutWindow.Closed += (s, args) => _adrenalWashoutWindow = null;
        _adrenalWashoutWindow.Show();
        ViewModel.StatusMessage = "Opened Adrenal CT Washout Calculator (APW / RPW).";
    }

    private PfirrmannClassificationWindow? _pfirrmannWindow;

    private void OnOpenPfirrmannClassification(object sender, RoutedEventArgs e)
    {
        if (_pfirrmannWindow != null && _pfirrmannWindow.IsLoaded)
        {
            _pfirrmannWindow.WindowState = WindowState.Normal;
            _pfirrmannWindow.Activate();
            return;
        }

        _pfirrmannWindow = new PfirrmannClassificationWindow
        {
            Owner = this
        };
        _pfirrmannWindow.Closed += (s, args) => _pfirrmannWindow = null;
        _pfirrmannWindow.Show();
        ViewModel.StatusMessage = "Opened Pfirrmann Classification Guide (Lumbar Disc Degeneration).";
    }

    private ScheltensClassificationWindow? _scheltensWindow;

    private void OnOpenScheltensClassification(object sender, RoutedEventArgs e)
    {
        if (_scheltensWindow != null && _scheltensWindow.IsLoaded)
        {
            _scheltensWindow.WindowState = WindowState.Normal;
            _scheltensWindow.Activate();
            return;
        }

        _scheltensWindow = new ScheltensClassificationWindow
        {
            Owner = this
        };
        _scheltensWindow.Closed += (s, args) => _scheltensWindow = null;
        _scheltensWindow.Show();
        ViewModel.StatusMessage = "Opened Scheltens Classification Guide (MTA Scale - Medial Temporal Lobe Atrophy).";
    }

    private FazekasClassificationWindow? _fazekasWindow;

    private void OnOpenFazekasClassification(object sender, RoutedEventArgs e)
    {
        if (_fazekasWindow != null && _fazekasWindow.IsLoaded)
        {
            _fazekasWindow.WindowState = WindowState.Normal;
            _fazekasWindow.Activate();
            return;
        }

        _fazekasWindow = new FazekasClassificationWindow
        {
            Owner = this
        };
        _fazekasWindow.Closed += (s, args) => _fazekasWindow = null;
        _fazekasWindow.Show();
        ViewModel.StatusMessage = "Opened Fazekas Scale Guide (White Matter Hyperintensities).";
    }

    private BosniakClassificationWindow? _bosniakWindow;

    private void OnOpenBosniakClassification(object sender, RoutedEventArgs e)
    {
        if (_bosniakWindow != null && _bosniakWindow.IsLoaded)
        {
            _bosniakWindow.WindowState = WindowState.Normal;
            _bosniakWindow.Activate();
            return;
        }

        _bosniakWindow = new BosniakClassificationWindow
        {
            Owner = this
        };
        _bosniakWindow.Closed += (s, args) => _bosniakWindow = null;
        _bosniakWindow.Show();
        ViewModel.StatusMessage = "Opened Bosniak Classification Guide (Cystic Renal Masses).";
    }

    private LiRadsClassificationWindow? _liRadsWindow;

    private void OnOpenLiRadsClassification(object sender, RoutedEventArgs e)
    {
        if (_liRadsWindow != null && _liRadsWindow.IsLoaded)
        {
            _liRadsWindow.WindowState = WindowState.Normal;
            _liRadsWindow.Activate();
            return;
        }

        _liRadsWindow = new LiRadsClassificationWindow
        {
            Owner = this
        };
        _liRadsWindow.Closed += (s, args) => _liRadsWindow = null;
        _liRadsWindow.Show();
        ViewModel.StatusMessage = "Opened LI-RADS Classification Guide (Hepatocellular Carcinoma).";
    }

    private ReferenceGuideViewerWindow? _infarctTableWindow;

    private void OnOpenInfarctTable(object sender, RoutedEventArgs e)
    {
        if (_infarctTableWindow != null && _infarctTableWindow.IsLoaded)
        {
            _infarctTableWindow.WindowState = WindowState.Normal;
            _infarctTableWindow.Activate();
            return;
        }

        var item = new ReferenceGuideItem
        {
            Id = "builtin-infarct-evolution",
            Title = "Ischemic Stroke / Infarct MRI Evolution (DWI / ADC / T2)",
            Category = "Table",
            ImagePath = "Assets/ischemic_infarct_mri_evolution.png",
            Description = "Evolutionary stages of cerebral ischemia from hyperacute (0-6h) to chronic (>2w) on T2/FLAIR, DWI, and ADC",
            IsBuiltIn = true
        };

        _infarctTableWindow = new ReferenceGuideViewerWindow(item)
        {
            Owner = this
        };
        _infarctTableWindow.Closed += (s, args) => _infarctTableWindow = null;
        _infarctTableWindow.Show();
        ViewModel.StatusMessage = "Opened Infarct MRI Evolution Reference Table.";
    }

    private void OnAddCustomClassification(object sender, RoutedEventArgs e)
    {
        var dlg = new AddReferenceGuideDialog("Classification")
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true && dlg.CreatedItem != null)
        {
            ViewModel.StatusMessage = $"Added custom classification: '{dlg.CreatedItem.Title}'";
            OpenCustomReferenceGuide(dlg.CreatedItem);
        }
    }

    private void OnAddCustomTable(object sender, RoutedEventArgs e)
    {
        var dlg = new AddReferenceGuideDialog("Table")
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true && dlg.CreatedItem != null)
        {
            ViewModel.StatusMessage = $"Added custom reference table: '{dlg.CreatedItem.Title}'";
            OpenCustomReferenceGuide(dlg.CreatedItem);
        }
    }

    private void OpenCustomReferenceGuide(ReferenceGuideItem item)
    {
        var win = new ReferenceGuideViewerWindow(item)
        {
            Owner = this
        };
        win.Show();
    }

    private void PopulateMenuWithCustomItems(System.Windows.Controls.MenuItem? menu, string category)
    {
        if (menu == null) return;

        // Remove old custom items
        for (int i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is System.Windows.Controls.MenuItem mi && mi.Tag is ReferenceGuideItem)
            {
                menu.Items.RemoveAt(i);
            }
        }

        // Find separator index (before the "+ Add ..." item)
        int sepIndex = -1;
        for (int i = 0; i < menu.Items.Count; i++)
        {
            if (menu.Items[i] is System.Windows.Controls.Separator)
            {
                sepIndex = i;
                break;
            }
        }
        if (sepIndex == -1) sepIndex = menu.Items.Count;

        var customItems = ReferenceGuideService.Instance.GetCustomItems(category);
        for (int i = 0; i < customItems.Count; i++)
        {
            var item = customItems[i];
            var mi = new System.Windows.Controls.MenuItem
            {
                Header = item.Title,
                Tag = item
            };
            mi.Click += (s, e) => OpenCustomReferenceGuide(item);
            menu.Items.Insert(sepIndex + i, mi);
        }
    }

    private void RebuildDynamicReferenceMenus()
    {
        PopulateMenuWithCustomItems(MenuClassifications, "Classification");
        PopulateMenuWithCustomItems(ToolbarClassificationsMenu, "Classification");
        PopulateMenuWithCustomItems(MenuTables, "Table");
        PopulateMenuWithCustomItems(ToolbarTablesMenu, "Table");
    }

    private void OnToolArrow(object sender, RoutedEventArgs e)
    {
        ViewModel.AssignLeftTool(MouseToolAction.Arrow);
        ViewModel.StatusMessage = "Tool: Arrow (Y)";
    }

    private void OnToolPencil(object sender, RoutedEventArgs e)
    {
        ViewModel.AssignLeftTool(MouseToolAction.Pencil);
        ViewModel.StatusMessage = "Tool: Pencil (P)";
    }

    private void OnDuplicateRoi(object sender, RoutedEventArgs e) => ViewModel.DuplicateActiveRoiToOtherSeries();

    private void OnClearMeasurements(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveViewport != null)
        {
            ViewModel.ActiveViewport.Measurements.Clear();
            ViewModel.StatusMessage = "Cleared measurements on active panel.";
        }
    }

    private void OnWindowPreset0(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ApplyWindowPresetNumber(0);
    private void OnWindowPreset1(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ApplyWindowPresetNumber(1);
    private void OnWindowPreset2(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ApplyWindowPresetNumber(2);
    private void OnWindowPreset3(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ApplyWindowPresetNumber(3);
    private void OnWindowPreset4(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ApplyWindowPresetNumber(4);
    private void OnWindowPreset5(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ApplyWindowPresetNumber(5);
    private void OnWindowPreset6(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ApplyWindowPresetNumber(6);
    private void OnWindowPreset7(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ApplyWindowPresetNumber(7);
    private void OnWindowPreset8(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ApplyWindowPresetNumber(8);
    private void OnToggleNegative(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ToggleNegative();

    private void OnCustomWindow(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveViewport != null)
        {
            var dlg = new CustomWindowDialog(ViewModel.ActiveViewport) { Owner = this };
            dlg.ShowDialog();
        }
    }

    private void OnRotateCW(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.RotateClockwise();
    private void OnRotateCCW(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.RotateCounterClockwise();
    private void OnFlipH(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.FlipHorizontalToggle();
    private void OnFlipV(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.FlipVerticalToggle();
    private void OnResetTransform(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ResetTransform();
    private void OnToggleCine(object sender, RoutedEventArgs e) => ViewModel.ActiveViewport?.ToggleCine();

    private void OnOpenMprMenu(object sender, RoutedEventArgs e)
    {
        if (BtnMprMenu.ContextMenu != null)
        {
            BtnMprMenu.ContextMenu.PlacementTarget = BtnMprMenu;
            BtnMprMenu.ContextMenu.IsOpen = true;
        }
    }
    private void OnToggle3DMpr(object sender, RoutedEventArgs e) => ViewModel.Toggle3DMpr();
    private void OnToggleMprAxes(object sender, RoutedEventArgs e) => ViewModel.ToggleMprAxes();
    private void OnOpen3DVrMenu(object sender, RoutedEventArgs e)
    {
        if (Btn3DVr.ContextMenu != null)
        {
            Btn3DVr.ContextMenu.PlacementTarget = Btn3DVr;
            Btn3DVr.ContextMenu.IsOpen = true;
        }
        e.Handled = true;
    }
    private void OnMprCoronal(object sender, RoutedEventArgs e) => ViewModel.SwitchActivePlane(OrthogonalPlane.Coronal);
    private void OnMprSagittal(object sender, RoutedEventArgs e) => ViewModel.SwitchActivePlane(OrthogonalPlane.Sagittal);
    private void OnMprAxial(object sender, RoutedEventArgs e) => ViewModel.SwitchActivePlane(OrthogonalPlane.Axial);

    private void OnToggleScoutLines(object sender, RoutedEventArgs e) => ViewModel.ToggleScoutLines();

    private void OnToolTextAnnotation(object sender, RoutedEventArgs e)
    {
        ViewModel.AssignLeftTool(MouseToolAction.TextAnnotation);
        ViewModel.StatusMessage = "Tool: Text Annotation (T)";
    }

    private void OnOpenMouseSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new MouseSettingsDialog(ViewModel) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mousesettings.json");
            ViewModel.MouseConfig.SaveToFile(path);
            ViewModel.StatusMessage = "Mouse bindings saved.";
        }
    }

    private void OnShowShortcutsHelp(object sender, RoutedEventArgs e)
    {
        string helpText = 
            "PARSEC DICOM VIEWER SHORTCUTS\n\n" +
            "Navigation & Tools:\n" +
            "  Q : 3D Spatial Crosshair\n" +
            "  B : Browse Series\n" +
            "  W : Window / Level\n" +
            "  M : Pan Image\n" +
            "  Z : Zoom Image\n" +
            "  L : Length Ruler\n" +
            "  E : Ellipse ROI\n" +
            "  U : Rectangle ROI\n" +
            "  A : Angle\n" +
            "  Y : Arrow\n" +
            "  P : Freehand Pencil\n" +
            "  T : Text Annotation\n\n" +
            "MPR Orthogonal Planes:\n" +
            "  Alt+1 : Coronal Plane\n" +
            "  Alt+2 : Sagittal Plane\n" +
            "  Alt+3 : Axial Plane\n\n" +
            "Scout / Cross Reference:\n" +
            "  Ctrl+F12 : Toggle Scout Reference Lines\n\n" +
            "Quick Grid (Casute):\n" +
            "  X + 1 : 1x1 Grid\n" +
            "  X + 2 : 1x2 Grid\n" +
            "  X + 3 : 1x3 Grid\n" +
            "  X + 4 : 2x2 Grid\n" +
            "  X + 6 : 3x2 Grid\n\n" +
            "Window Presets:\n" +
            "  0-7 : Standard Presets\n" +
            "  8 : Liver CT (WL 60, WW 160)\n" +
            "  Ctrl+W : Custom Window / Level\n" +
            "  F11 : Negative / Invert\n\n" +
            "Archive & Connectivity:\n" +
            "  Ctrl+P : Search PACS Archive (C-FIND, C-MOVE / C-GET)\n" +
            "  Ctrl+D : Local Database Archive\n\n" +
            "Layout & Panes:\n" +
            "  F5 : Cycle Sync (Off / Auto / Manual)\n" +
            "  Ctrl+Shift+D : Duplicate ROI across series\n" +
            "  F : Full Screen\n" +
            "  Ctrl+M : Maximize Active Panel\n" +
            "  Esc : Close Active Panel (or clear selection)\n" +
            "  Shift+Esc : Close All Panels";
        MessageBox.Show(helpText, "Parsec Keyboard Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnShowAboutDialog(object sender, RoutedEventArgs e)
    {
        var aboutWin = new AboutWindow
        {
            Owner = this
        };
        aboutWin.ShowDialog();
    }

    private void OnToggleFullScreen(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized && WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            ViewModel.IsFullScreen = false;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            ViewModel.IsFullScreen = true;
        }
    }

    private void OnOpenSyncMenu(object sender, RoutedEventArgs e)
    {
        if (BtnSyncMenu.ContextMenu != null)
        {
            BtnSyncMenu.ContextMenu.PlacementTarget = BtnSyncMenu;
            BtnSyncMenu.ContextMenu.IsOpen = true;
        }
    }

    private void OnSyncOff(object sender, RoutedEventArgs e) => ViewModel.SetSyncMode(SyncMode.Off);
    private void OnSyncAuto(object sender, RoutedEventArgs e) => ViewModel.SetSyncMode(SyncMode.Automatic);
    private void OnSyncManual(object sender, RoutedEventArgs e) => ViewModel.SetSyncMode(SyncMode.Manual);

    private void OnOpenBrowseMenu(object sender, RoutedEventArgs e)
    {
        if (BtnBrowse.ContextMenu != null)
        {
            BtnBrowse.ContextMenu.PlacementTarget = BtnBrowse;
            BtnBrowse.ContextMenu.IsOpen = true;
        }
    }

    private void OnBrowseSensitivityVeryFast(object sender, RoutedEventArgs e)
    {
        ViewModel.BrowseSensitivityPixels = 3.0;
        ViewModel.StatusMessage = "Browse sensitivity: Very Fast (3 px / slice)";
    }

    private void OnBrowseSensitivityFast(object sender, RoutedEventArgs e)
    {
        ViewModel.BrowseSensitivityPixels = 6.0;
        ViewModel.StatusMessage = "Browse sensitivity: Fast (6 px / slice)";
    }

    private void OnBrowseSensitivityMedium(object sender, RoutedEventArgs e)
    {
        ViewModel.BrowseSensitivityPixels = 12.0;
        ViewModel.StatusMessage = "Browse sensitivity: Normal (12 px / slice)";
    }

    private void OnBrowseSensitivityPrecise(object sender, RoutedEventArgs e)
    {
        ViewModel.BrowseSensitivityPixels = 20.0;
        ViewModel.StatusMessage = "Browse sensitivity: Precise (20 px / slice)";
    }
    #endregion

    #region PACS Networking Actions
    public void LoadDicomFolder(string folder)
    {
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            _ = ViewModel.ScanFolderAsync(folder);
        }
    }

    public void StartPacsStreamingStudy(PacsServerConfig server, PacsStudyResult study, PacsSeriesResult? series = null)
    {
        _ = ViewModel.StartPacsStreamingStudyAsync(server, study, series);
    }

    public void StartPacsStreamingStudies(List<(PacsServerConfig server, PacsStudyResult study)> studies)
    {
        _ = ViewModel.StartPacsStreamingStudiesAsync(studies);
    }

    private void OnOpenPacsSearch(object sender, RoutedEventArgs e)
    {
        var pacsWin = new PacsSearchWindow(this)
        {
            Owner = this
        };
        pacsWin.ShowDialog();
    }

    private void OnOpenPacsConfig(object sender, RoutedEventArgs e)
    {
        var cfgWin = new PacsConfigurationWindow
        {
            Owner = this
        };
        cfgWin.ShowDialog();
    }

    private async void OnSendActiveStudyToPacs(object sender, RoutedEventArgs e)
    {
        var activeVp = ViewModel.ActiveViewport;
        var activeSeries = activeVp?.OriginalSeries ?? activeVp?.CurrentSeries;
        StudyModel? activeStudy = null;
        if (activeSeries != null)
        {
            foreach (var p in ViewModel.Patients)
            {
                foreach (var st in p.Studies)
                {
                    if (st.Series.Contains(activeSeries))
                    {
                        activeStudy = st;
                        break;
                    }
                }
                if (activeStudy != null) break;
            }

            if (activeStudy == null)
            {
                activeStudy = new StudyModel
                {
                    StudyInstanceUid = activeSeries.StudyInstanceUid,
                    StudyDate = activeSeries.StudyDate,
                    StudyDescription = activeSeries.StudyDescription
                };
                activeStudy.Series.Add(activeSeries);
            }
        }

        if (activeStudy == null || activeStudy.Series.Count == 0)
        {
            MessageBox.Show(this, "No active study is currently open in the viewer.", "PACS Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var servers = PacsService.Instance.Settings.Servers;
        if (servers.Count == 0)
        {
            MessageBox.Show(this, "No PACS servers are configured. Please configure a PACS server first.", "PACS Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var defaultServer = servers.FirstOrDefault();
        if (defaultServer == null) return;

        int totalSlices = activeStudy.Series.Sum(s => s.Slices.Count);
        string patName = activeSeries?.PatientName ?? "Patient";
        var res = MessageBox.Show(this,
            $"Send active study to PACS server '{defaultServer.Description}' ({defaultServer.IpAddress}:{defaultServer.Port})?\n\nPatient: {patName}\nSeries: {activeStudy.Series.Count}\nTotal images: {totalSlices}",
            "Confirm C-STORE PACS Export",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        ViewModel.StatusMessage = $"Sending {totalSlices} images to PACS ({defaultServer.Description})...";

        var progress = new Progress<(int current, int total, string message)>(p =>
        {
            Dispatcher.Invoke(() =>
            {
                ViewModel.StatusMessage = p.message;
            });
        });

        try
        {
            var result = await PacsService.Instance.SendStudyToPacsAsync(defaultServer, activeStudy, progress);
            int succ = result.successCount;
            int fail = result.failedCount;
            string msg = result.message;
            ViewModel.StatusMessage = msg;
            MessageBox.Show(this, msg, "PACS Export Complete", MessageBoxButton.OK, fail == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"PACS export error: {ex.Message}";
            MessageBox.Show(this, $"Transfer error: {ex.Message}", "PACS Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    #endregion

    #region Database & 3D Volume Rendering Actions
    private void OnOpenDatabase(object sender, RoutedEventArgs e)
    {
        var dbWin = new DatabaseWindow(ViewModel)
        {
            Owner = this
        };
        dbWin.ShowDialog();
    }

    private void OnImportActiveStudyToDb(object sender, RoutedEventArgs e)
    {
        var activeVp = ViewModel.ActiveViewport;
        var activeSeries = activeVp?.OriginalSeries ?? activeVp?.CurrentSeries;
        if (activeSeries == null)
        {
            MessageBox.Show(this, "Nu există niciun studiu deschis în acest moment.", "Import Database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenRadViewer.Core.Models.PatientModel? currentPatient = null;
        OpenRadViewer.Core.Models.StudyModel? currentStudy = null;

        foreach (var p in ViewModel.Patients)
        {
            foreach (var st in p.Studies)
            {
                if (st.Series.Contains(activeSeries))
                {
                    currentPatient = p;
                    currentStudy = st;
                    break;
                }
            }
            if (currentStudy != null) break;
        }

        if (currentStudy == null)
        {
            currentStudy = new OpenRadViewer.Core.Models.StudyModel
            {
                StudyInstanceUid = activeSeries.SeriesInstanceUid + ".study",
                StudyDate = activeSeries.StudyDate,
                StudyDescription = activeSeries.SeriesDescription,
                InstitutionName = ""
            };
            currentStudy.Series.Add(activeSeries);
        }

        try
        {
            var rec = DatabaseService.ImportStudy(
                currentStudy, 
                currentPatient?.Name ?? activeSeries.PatientName, 
                currentPatient?.Id ?? activeSeries.PatientId, 
                currentPatient?.BirthDate ?? "");

            ViewModel.StatusMessage = $"Studiul '{rec.PatientName}' a fost importat cu succes în baza de date.";
            MessageBox.Show(this, $"Studiul '{rec.PatientName}' ({rec.TotalImages} imagini) a fost importat cu succes în Parsec Database.", "Import Database", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Eroare la import:\n{ex.Message}", "Eroare Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpen3DVr(object sender, RoutedEventArgs e)
    {
        var activeVp = ViewModel.ActiveViewport;
        var series = activeVp?.OriginalSeries ?? activeVp?.CurrentSeries;

        if (series == null || series.Slices.Count < 3)
        {
            MessageBox.Show(this, 
                "3D Volume Rendering (3D VR) requires a volumetric series (CT or MRI) with at least 3 images.", 
                "3D VR", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
            return;
        }

        var vrWin = new VolumeRenderingWindow(series)
        {
            Owner = this
        };
        vrWin.Show();
    }

    private void OnOpenExtraToolsMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ContextMenu != null)
        {
            fe.ContextMenu.PlacementTarget = fe;
            fe.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            fe.ContextMenu.IsOpen = true;
        }
        else if (ExtraToolsContextMenu != null)
        {
            ExtraToolsContextMenu.PlacementTarget = BtnExtraToolsMenu;
            ExtraToolsContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            ExtraToolsContextMenu.IsOpen = true;
        }
    }

    private void OnToolVertebraLabel(object sender, RoutedEventArgs e)
    {
        ViewModel.AssignLeftTool(MouseToolAction.VertebraLabel);
        string regionName = ViewModel.SelectedSpineSegment switch
        {
            SpineSegment.Thoracic => "Toracal (T12)",
            SpineSegment.Cervical => "Cervical (C7)",
            _ => "Lombar (L5)"
        };
        ViewModel.StatusMessage = $"Tool: Vertebral Body Labeling [{regionName}]. Click vertebrae sequentially upwards (e.g. L5->L4->L3...).";
    }

    private void OnSelectSpineLumbar(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedSpineSegment = SpineSegment.Lumbar;
        ViewModel.AssignLeftTool(MouseToolAction.VertebraLabel);
        ViewModel.StatusMessage = "Selected Spine Region: Lumbar (L5). Click on vertebrae upwards.";
    }

    private void OnSelectSpineThoracic(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedSpineSegment = SpineSegment.Thoracic;
        ViewModel.AssignLeftTool(MouseToolAction.VertebraLabel);
        ViewModel.StatusMessage = "Selected Spine Region: Thoracic (T12). Click on vertebrae upwards.";
    }

    private void OnSelectSpineCervical(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedSpineSegment = SpineSegment.Cervical;
        ViewModel.AssignLeftTool(MouseToolAction.VertebraLabel);
        ViewModel.StatusMessage = "Selected Spine Region: Cervical (C7). Click on vertebrae upwards.";
    }

    private void OnClearVertebraLabels(object sender, RoutedEventArgs e)
    {
        var vp = ViewModel.ActiveViewport;
        if (vp == null) return;
        vp.VertebraLabels.Clear();
        ViewModel.StatusMessage = "Cleared all vertebra labels.";
    }

    private void OnShowPatientDetails(object sender, RoutedEventArgs e)
    {
        var activeVp = ViewModel.ActiveViewport;
        var activeSeries = activeVp?.CurrentSeries ?? activeVp?.OriginalSeries;

        PatientModel? currentPatient = null;
        StudyModel? currentStudy = null;

        if (activeSeries != null)
        {
            currentPatient = ViewModel.Patients.FirstOrDefault(p => p.Studies.Any(st => st.Series.Contains(activeSeries)));
            currentStudy = currentPatient?.Studies.FirstOrDefault(st => st.Series.Contains(activeSeries));
        }

        if (currentPatient == null)
        {
            currentPatient = ViewModel.Patients.FirstOrDefault();
            currentStudy = currentPatient?.Studies.FirstOrDefault();
        }

        if (currentPatient == null && currentStudy == null && activeSeries == null)
        {
            MessageBox.Show(this, "Niciun pacient sau studiu DICOM nu este încărcat în acest moment.", "Detalii Pacient", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (currentPatient == null && activeSeries != null)
        {
            currentPatient = new PatientModel
            {
                Name = activeSeries.PatientName,
                Id = activeSeries.PatientId,
                BirthDate = activeSeries.StudyDate
            };
        }

        if (currentStudy == null && activeSeries != null)
        {
            currentStudy = new StudyModel
            {
                StudyDescription = activeSeries.StudyDescription,
                StudyDate = activeSeries.StudyDate,
                Series = new List<SeriesModel> { activeSeries }
            };
        }

        var detailsWin = new PatientDetailsWindow(currentPatient, currentStudy)
        {
            Owner = this
        };
        detailsWin.ShowDialog();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        // Do not auto-overwrite protocols.txt on close
    }

    private void OnToggleProtocolsPanel(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleProtocolsPanel();
    }

    private void OnSaveAsDocx(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ProtocolsTextBox == null || string.IsNullOrWhiteSpace(ProtocolsTextBox.Text))
            {
                MessageBox.Show(this, "Documentul este gol. Introduceți text sau alegeți un șablon înainte de a salva.", "Salvare Raport", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (patient, study, curSeries) = ViewModel.GetCurrentPatientContext();
            string rawPatientName = !string.IsNullOrWhiteSpace(patient?.Name)
                ? patient.Name
                : (curSeries?.PatientName ?? "");
            
            // Formatare nume pacient: înlocuim delimitatorii DICOM '^' sau '_' cu spațiu, eliminăm caracterele invalide
            string cleanPatientName = rawPatientName.Replace("^", " ").Replace("_", " ");
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                cleanPatientName = cleanPatientName.Replace(c.ToString(), "");
            }
            cleanPatientName = System.Text.RegularExpressions.Regex.Replace(cleanPatientName, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(cleanPatientName)) cleanPatientName = "Pacient";

            // Preluăm data examinării din metadatele DICOM (la fel ca în raport, nu data de azi)
            var demo = Core.Services.ClinicalProtocolsService.GetPatientDemographics(
                patient, study, curSeries, ViewModel.SelectedClinicalProtocol);
            string examDateStr = !string.IsNullOrWhiteSpace(demo.ExamDate)
                ? demo.ExamDate
                : DateTime.Now.ToString("dd.MM.yyyy");

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Salvează Raport Medical Word (.docx)",
                Filter = "Word Document (*.docx)|*.docx|All Files (*.*)|*.*",
                FileName = $"{cleanPatientName} {examDateStr}.docx",
                DefaultExt = ".docx"
            };

            if (dlg.ShowDialog(this) == true)
            {
                string fullReportForDocx = ViewModel.BuildCompleteReportForDocxExport(ProtocolsTextBox.Text);
                Core.Services.DocxExportService.SaveTextToDocx(fullReportForDocx, dlg.FileName, $"Raport {cleanPatientName}");
                ViewModel.ProtocolsStatusMessage = $"Report saved: {System.IO.Path.GetFileName(dlg.FileName)} ({DateTime.Now:HH:mm:ss})";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error saving Word document: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenVoiceSettings(object sender, RoutedEventArgs e)
    {
        var win = new VoiceSettingsDialog
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private void OnClearProtocolsText(object sender, RoutedEventArgs e)
    {
        if (ProtocolsTextBox == null || string.IsNullOrWhiteSpace(ProtocolsTextBox.Text)) return;
        var res = MessageBox.Show(this, "Are you sure you want to clear all text from the document?", "Clear Text", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res == MessageBoxResult.Yes)
        {
            ViewModel.ProtocolsText = string.Empty;
            ProtocolsTextBox.Text = string.Empty;
            ProtocolsTextBox.Focus();
        }
    }

    private void OnInsertProtocolAtCaret(object sender, RoutedEventArgs e)
    {
        string snippet = ViewModel.GetSelectedProtocolSnippet();
        if (!string.IsNullOrEmpty(snippet))
        {
            InsertTextAtCaret(snippet);
        }
    }

    private void OnLoadFullProtocolReport(object sender, RoutedEventArgs e)
    {
        string fullReport = ViewModel.GenerateFullProtocolReport();
        if (string.IsNullOrEmpty(fullReport)) return;

        if (ProtocolsTextBox != null && !string.IsNullOrWhiteSpace(ProtocolsTextBox.Text))
        {
            var res = MessageBox.Show(this,
                "Doriți să înlocuiți textul existent din raport cu noul șablon complet?\n\n(Dacă alegeți 'No', șablonul va fi adăugat la poziția cursorului)",
                "Șablon Raport Medical",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                ViewModel.ProtocolsText = fullReport;
                ProtocolsTextBox.Text = fullReport;
                ProtocolsTextBox.SelectionStart = fullReport.Length;
                ProtocolsTextBox.Focus();
            }
            else if (res == MessageBoxResult.No)
            {
                InsertTextAtCaret("\n\n" + fullReport);
            }
        }
        else
        {
            ViewModel.ProtocolsText = fullReport;
            if (ProtocolsTextBox != null)
            {
                ProtocolsTextBox.Text = fullReport;
                ProtocolsTextBox.SelectionStart = fullReport.Length;
                ProtocolsTextBox.Focus();
            }
        }
    }

    private async void OnToggleVoiceDictation(object sender, RoutedEventArgs e)
    {
        try
        {
            // If starting, ensure cursor position in editor
            if (!ViewModel.IsVoiceRecording)
            {
                ProtocolsTextBox?.Focus();
                await ViewModel.ToggleVoiceRecordingAsync();
            }
            else
            {
                string text = await ViewModel.ToggleVoiceRecordingAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    InsertTextAtCaret(text + " ");
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.ProtocolsStatusMessage = $"Dictation error: {ex.Message}";
        }
    }

    private readonly Stack<string> _aiReportUndoStack = new();

    private async void OnFormatProtocolWithAi(object sender, RoutedEventArgs e)
    {
        try
        {
            string currentText = ProtocolsTextBox?.Text ?? ViewModel.ProtocolsText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentText))
            {
                ViewModel.ProtocolsStatusMessage = "Please dictate or type text before formatting with AI.";
                return;
            }

            string formatted = await ViewModel.FormatDictatedProtocolWithAiAsync(currentText);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                _aiReportUndoStack.Push(currentText);
                ViewModel.ProtocolsText = formatted;
                if (ProtocolsTextBox != null)
                {
                    ProtocolsTextBox.Text = formatted;
                    ProtocolsTextBox.SelectionStart = formatted.Length;
                    ProtocolsTextBox.Focus();
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.ProtocolsStatusMessage = $"AI formatting error: {ex.Message}";
        }
    }

    private async void OnToggleVoiceRefine(object sender, RoutedEventArgs e)
    {
        try
        {
            string currentText = ProtocolsTextBox?.Text ?? ViewModel.ProtocolsText ?? string.Empty;
            if (!ViewModel.IsVoiceRefineRecording && string.IsNullOrWhiteSpace(currentText))
            {
                ViewModel.ProtocolsStatusMessage = "Please dictate or insert a report first before refining.";
                return;
            }

            string updated = await ViewModel.ToggleVoiceRefineRecordingAsync(currentText);
            if (!string.IsNullOrWhiteSpace(updated))
            {
                _aiReportUndoStack.Push(currentText);
                ViewModel.ProtocolsText = updated;
                if (ProtocolsTextBox != null)
                {
                    ProtocolsTextBox.Text = updated;
                    ProtocolsTextBox.SelectionStart = updated.Length;
                    ProtocolsTextBox.Focus();
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.ProtocolsStatusMessage = $"Voice refinement error: {ex.Message}";
        }
    }

    private async void OnApplyAiRefine(object sender, RoutedEventArgs e)
    {
        try
        {
            // If currently recording a voice refine command, stop and apply it
            if (ViewModel.IsVoiceRefineRecording)
            {
                OnToggleVoiceRefine(sender, e);
                return;
            }

            string currentText = ProtocolsTextBox?.Text ?? ViewModel.ProtocolsText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentText))
            {
                ViewModel.ProtocolsStatusMessage = "Please dictate or insert a report first before refining.";
                return;
            }

            string instruction = TxtAiRefinePrompt?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(instruction))
            {
                ViewModel.ProtocolsStatusMessage = "Please type an instruction or press F6 for voice refinement.";
                return;
            }

            string updated = await ViewModel.ApplyTextRefinementWithAiAsync(currentText, instruction);
            if (!string.IsNullOrWhiteSpace(updated))
            {
                _aiReportUndoStack.Push(currentText);
                ViewModel.ProtocolsText = updated;
                if (TxtAiRefinePrompt != null) TxtAiRefinePrompt.Text = string.Empty;
                if (ProtocolsTextBox != null)
                {
                    ProtocolsTextBox.Text = updated;
                    ProtocolsTextBox.SelectionStart = updated.Length;
                    ProtocolsTextBox.Focus();
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.ProtocolsStatusMessage = $"AI refinement error: {ex.Message}";
        }
    }

    private void OnAiRefinePromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnApplyAiRefine(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnUndoAiRefine(object sender, RoutedEventArgs e)
    {
        if (_aiReportUndoStack.Count > 0)
        {
            string prev = _aiReportUndoStack.Pop();
            ViewModel.ProtocolsText = prev;
            if (ProtocolsTextBox != null)
            {
                ProtocolsTextBox.Text = prev;
                ProtocolsTextBox.SelectionStart = prev.Length;
                ProtocolsTextBox.Focus();
            }
            ViewModel.ProtocolsStatusMessage = "↩ Restored previous version of report.";
        }
        else if (ProtocolsTextBox != null && ProtocolsTextBox.CanUndo)
        {
            ProtocolsTextBox.Undo();
            ViewModel.ProtocolsStatusMessage = "↩ Undo applied.";
        }
        else
        {
            ViewModel.ProtocolsStatusMessage = "No previous changes to undo.";
        }
    }

    private void OnTriggerWindowsVoiceTyping(object sender, RoutedEventArgs e)
    {
        ProtocolsTextBox?.Focus();
        Core.Services.SpeechRecognitionService.TriggerWindowsDictation();
    }

    private void InsertTextAtCaret(string text)
    {
        if (ProtocolsTextBox == null || string.IsNullOrEmpty(text)) return;
        ProtocolsTextBox.Focus();
        int caret = ProtocolsTextBox.SelectionStart;
        string cur = ProtocolsTextBox.Text ?? string.Empty;
        int len = ProtocolsTextBox.SelectionLength;

        string updated = cur.Remove(caret, len).Insert(caret, text);
        ViewModel.ProtocolsText = updated;
        ProtocolsTextBox.Text = updated;
        ProtocolsTextBox.SelectionStart = caret + text.Length;
        ProtocolsTextBox.SelectionLength = 0;
        ProtocolsTextBox.Focus();
    }

    private void OnCloseProtocolsPanel(object sender, RoutedEventArgs e)
    {
        ViewModel.IsProtocolsPanelOpen = false;
    }

    private void OnProtocolsSplitterDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (ProtocolsContentBorder != null)
        {
            double newWidth = ProtocolsContentBorder.ActualWidth - e.HorizontalChange;
            ProtocolsContentBorder.Width = Math.Clamp(newWidth, 200, 850);
        }
    }
    #endregion

    #region Series Bar Collapse / Expand
    private double _previousSeriesBarWidth = 160;
    private bool _isSeriesBarCollapsed = false;

    public void ToggleSeriesBarCollapse()
    {
        _isSeriesBarCollapsed = !_isSeriesBarCollapsed;
        ApplySeriesBarVisualState();
    }

    private void ApplySeriesBarVisualState()
    {
        if (ColSeriesBar == null || SeriesBarControl == null) return;

        if (!ViewModel.ShowSeriesPreviewBar)
        {
            ColSeriesBar.MinWidth = 0;
            ColSeriesBar.MaxWidth = 0;
            ColSeriesBar.Width = new GridLength(0);
            if (SplitterSeriesBar != null)
            {
                SplitterSeriesBar.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (_isSeriesBarCollapsed)
        {
            if (ColSeriesBar.ActualWidth >= 90)
            {
                _previousSeriesBarWidth = ColSeriesBar.ActualWidth;
            }
            ColSeriesBar.MinWidth = 28;
            ColSeriesBar.MaxWidth = 28;
            ColSeriesBar.Width = new GridLength(28, GridUnitType.Pixel);
            if (SplitterSeriesBar != null)
            {
                SplitterSeriesBar.IsEnabled = false;
                SplitterSeriesBar.Visibility = Visibility.Collapsed;
            }
            SeriesBarControl.SetCollapsedState(true);
        }
        else
        {
            ColSeriesBar.MinWidth = 90;
            ColSeriesBar.MaxWidth = 450;
            double targetWidth = _previousSeriesBarWidth >= 90 ? _previousSeriesBarWidth : 160;
            ColSeriesBar.Width = new GridLength(targetWidth, GridUnitType.Pixel);
            if (SplitterSeriesBar != null)
            {
                SplitterSeriesBar.IsEnabled = true;
                SplitterSeriesBar.Visibility = Visibility.Visible;
            }
            SeriesBarControl.SetCollapsedState(false);
        }
    }

    private void OnSplitterSeriesBarDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ToggleSeriesBarCollapse();
        e.Handled = true;
    }

    private void OnMainWindowViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowSeriesPreviewBar))
        {
            ApplySeriesBarVisualState();
        }
    }
    #endregion
}
