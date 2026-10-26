// @purpose: PACS Query & Retrieve (C-FIND, C-MOVE, HTTP fast download) window with date/modality filtering, auto-refresh, and bidirectional "Reviewed" study status markers.
// @depends: OpenRadViewer.Core.Services, OpenRadViewer.Core.Models, System.Collections.ObjectModel
// @exports: PacsSearchWindow

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public partial class PacsSearchWindow : Window
{
    public ObservableCollection<PacsStudyResult> FoundStudies { get; set; } = new();
    public ObservableCollection<PacsSeriesResult> SelectedStudySeries { get; set; } = new();

    private readonly MainWindow _mainWindow;
    private CancellationTokenSource? _activeCts;
    private bool _isResetting;
    private bool _isSearching;

    public PacsSearchWindow(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        DataContext = this;

        DarkWindowHelper.ApplyDarkTitleBar(this);
        try
        {
            string iconFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_icon.png");
            if (File.Exists(iconFile))
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconFile, UriKind.Absolute));
            }
        }
        catch { }

        // Auto-start C-STORE listener if not running
        if (!PacsService.Instance.IsListenerRunning)
        {
            PacsService.Instance.StartListener(out _);
        }

        PopulateServerLocations();
        RestoreLastFilterSession();
        SetupAutoRefresh();

        Loaded += (s, e) =>
        {
            if (FoundStudies.Count == 0)
            {
                OnSearchClicked(this, new RoutedEventArgs());
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkWindowHelper.ApplyDarkTitleBar(this);
    }

    private void PopulateServerLocations()
    {
        bool prevResetting = _isResetting;
        _isResetting = true;
        try
        {
            CmbServerLocation.Items.Clear();
            var servers = PacsService.Instance.Settings.Servers;
            string? defLoc = PacsService.Instance.Settings.DefaultServerLocation;

            // Default to "All PACS Servers" as the first item
            var allItem = new ComboBoxItem
            {
                Content = "All PACS Servers",
                Tag = null
            };
            CmbServerLocation.Items.Add(allItem);

            ComboBoxItem? toSelect = null;

            if (string.Equals(defLoc, "ALL", StringComparison.OrdinalIgnoreCase) || 
                string.Equals(defLoc, "All PACS Servers", StringComparison.OrdinalIgnoreCase))
            {
                toSelect = allItem;
            }

            foreach (var s in servers)
            {
                string displayName = !string.IsNullOrWhiteSpace(s.Description) ? s.Description : s.AeTitle;
                var item = new ComboBoxItem
                {
                    Content = displayName,
                    Tag = s
                };
                CmbServerLocation.Items.Add(item);

                if (toSelect == null && !string.IsNullOrEmpty(defLoc))
                {
                    string normDef = defLoc.Replace("_", " ").Trim();
                    string normDesc = (s.Description ?? string.Empty).Replace("_", " ").Trim();
                    if (string.Equals(s.Id, defLoc, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Description, defLoc, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normDesc, normDef, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.AeTitle, defLoc, StringComparison.OrdinalIgnoreCase))
                    {
                        toSelect = item;
                    }
                }
            }

            if (toSelect != null)
            {
                CmbServerLocation.SelectedItem = toSelect;
            }
            else if (CmbServerLocation.Items.Count > 0)
            {
                CmbServerLocation.SelectedIndex = 0;
            }

            UpdateDefaultButtonVisual();
        }
        finally
        {
            _isResetting = prevResetting;
        }
    }

    private void RestoreLastFilterSession()
    {
        bool prevResetting = _isResetting;
        _isResetting = true;
        try
        {
            string? savedMod = PacsService.Instance.Settings.LastModalityFilter;
            if (!string.IsNullOrWhiteSpace(savedMod) &&
                !string.Equals(savedMod, "All", StringComparison.OrdinalIgnoreCase))
            {
                var parts = savedMod.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var specific = GetSpecificModalityCheckBoxes();
                bool anyMatched = false;
                foreach (var cb in specific)
                {
                    string tag = cb.Tag?.ToString() ?? cb.Content?.ToString() ?? "";
                    bool match = parts.Any(p => string.Equals(p, tag, StringComparison.OrdinalIgnoreCase));
                    cb.IsChecked = match;
                    if (match) anyMatched = true;
                }
                ChkModAll.IsChecked = !anyMatched;
                UpdateModalitySummaryText();
            }

            string? savedDate = PacsService.Instance.Settings.LastDatePreset;
            if (!string.IsNullOrWhiteSpace(savedDate))
            {
                foreach (var obj in CmbDatePreset.Items)
                {
                    if (obj is ComboBoxItem cbi &&
                        string.Equals(cbi.Content?.ToString(), savedDate, StringComparison.OrdinalIgnoreCase))
                    {
                        CmbDatePreset.SelectedItem = cbi;
                        break;
                    }
                }
            }
        }
        finally
        {
            _isResetting = prevResetting;
        }
    }

    private PacsServerConfig? GetSelectedServer()
    {
        if (CmbServerLocation.SelectedItem is ComboBoxItem item && item.Tag is PacsServerConfig server)
        {
            return server;
        }
        return PacsService.Instance.Settings.Servers.FirstOrDefault();
    }

    private void OnServerLocationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isResetting && CmbServerLocation.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag is PacsServerConfig s)
            {
                PacsService.Instance.Settings.DefaultServerLocation = !string.IsNullOrWhiteSpace(s.Description) ? s.Description : s.Id;
            }
            else
            {
                PacsService.Instance.Settings.DefaultServerLocation = "ALL";
            }
            PacsService.Instance.SaveSettings();
        }

        UpdateDefaultButtonVisual();
        if (!IsLoaded || _isResetting) return;
        OnSearchClicked(this, new RoutedEventArgs());
    }

    private void UpdateDefaultButtonVisual()
    {
        if (BtnSetDefaultLocation == null) return;

        string? defLoc = PacsService.Instance.Settings.DefaultServerLocation;
        bool isCurrentDefault = false;

        if (CmbServerLocation.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag is PacsServerConfig s)
            {
                string normDef = (defLoc ?? string.Empty).Replace("_", " ").Trim();
                string normDesc = (s.Description ?? string.Empty).Replace("_", " ").Trim();
                isCurrentDefault = !string.IsNullOrEmpty(defLoc) &&
                    (string.Equals(s.Id, defLoc, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(s.Description, defLoc, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normDesc, normDef, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // All PACS Servers
                isCurrentDefault = string.IsNullOrEmpty(defLoc) ||
                    string.Equals(defLoc, "ALL", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(defLoc, "All PACS Servers", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (IconDefaultStar != null)
        {
            IconDefaultStar.Fill = isCurrentDefault 
                ? (SolidColorBrush)new BrushConverter().ConvertFrom("#F5AF28")! 
                : Brushes.Transparent;
        }

        if (TxtDefaultBtn != null)
        {
            TxtDefaultBtn.Foreground = isCurrentDefault
                ? (SolidColorBrush)new BrushConverter().ConvertFrom("#F5AF28")!
                : (SolidColorBrush)new BrushConverter().ConvertFrom("#AAAAAA")!;
        }

        BtnSetDefaultLocation.ToolTip = isCurrentDefault
            ? "Această locație este setată ca implicită la deschidere"
            : "Setează această locație ca implicită la deschidere";
    }

    private void OnSetDefaultLocationClicked(object sender, RoutedEventArgs e)
    {
        if (CmbServerLocation.SelectedItem is ComboBoxItem item)
        {
            string locName;
            if (item.Tag is PacsServerConfig s)
            {
                PacsService.Instance.Settings.DefaultServerLocation = !string.IsNullOrWhiteSpace(s.Description) ? s.Description : s.Id;
                locName = !string.IsNullOrWhiteSpace(s.Description) ? s.Description : s.AeTitle;
            }
            else
            {
                PacsService.Instance.Settings.DefaultServerLocation = "ALL";
                locName = "All PACS Servers";
            }

            PacsService.Instance.SaveSettings();
            UpdateDefaultButtonVisual();

            TxtTransferStatus.Text = $"Locația '{locName}' a fost setată ca implicită.";
        }
    }

    private System.Windows.Threading.DispatcherTimer? _modalityDebounceTimer;
    private bool _modalitySearchPending;

    private CheckBox[] GetSpecificModalityCheckBoxes() => new[]
    {
        ChkModCT, ChkModMR, ChkModCRDX, ChkModUS, ChkModNMPT, ChkModXA
    };

    private void OnModalityAllClicked(object sender, RoutedEventArgs e)
    {
        if (_isResetting) return;
        _isResetting = true;
        try
        {
            ChkModAll.IsChecked = true;
            foreach (var cb in GetSpecificModalityCheckBoxes())
            {
                cb.IsChecked = false;
            }
            UpdateModalitySummaryText();
        }
        finally
        {
            _isResetting = false;
        }
        ScheduleModalityFilterSearch();
    }

    private void OnModalityItemClicked(object sender, RoutedEventArgs e)
    {
        if (_isResetting) return;
        _isResetting = true;
        try
        {
            var specific = GetSpecificModalityCheckBoxes();
            bool anyChecked = specific.Any(cb => cb.IsChecked == true);
            if (anyChecked)
            {
                ChkModAll.IsChecked = false;
            }
            else
            {
                ChkModAll.IsChecked = true;
            }
            UpdateModalitySummaryText();
        }
        finally
        {
            _isResetting = false;
        }
        ScheduleModalityFilterSearch();
    }

    private void UpdateModalitySummaryText()
    {
        if (TxtSelectedModalities == null) return;
        var checkedItems = GetSpecificModalityCheckBoxes()
            .Where(cb => cb.IsChecked == true)
            .Select(cb => cb.Tag?.ToString() ?? cb.Content?.ToString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (ChkModAll.IsChecked == true || checkedItems.Count == 0 || checkedItems.Count == GetSpecificModalityCheckBoxes().Length)
        {
            TxtSelectedModalities.Text = "All";
            BtnModalityDropDown.ToolTip = "Modality: All (Click to select one or multiple modalities)";
        }
        else
        {
            string joined = string.Join(", ", checkedItems);
            TxtSelectedModalities.Text = joined;
            BtnModalityDropDown.ToolTip = $"Selected Modalities: {joined}";
        }
    }

    private string GetSelectedModalitiesString()
    {
        if (ChkModAll == null || ChkModAll.IsChecked == true) return "All";
        var checkedItems = GetSpecificModalityCheckBoxes()
            .Where(cb => cb.IsChecked == true)
            .Select(cb => cb.Tag?.ToString() ?? cb.Content?.ToString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (checkedItems.Count == 0 || checkedItems.Count == GetSpecificModalityCheckBoxes().Length)
        {
            return "All";
        }
        return string.Join(", ", checkedItems);
    }

    private void ResetModalitySelectionToAll()
    {
        if (ChkModAll == null) return;
        ChkModAll.IsChecked = true;
        foreach (var cb in GetSpecificModalityCheckBoxes())
        {
            if (cb != null) cb.IsChecked = false;
        }
        UpdateModalitySummaryText();
    }

    private void ScheduleModalityFilterSearch()
    {
        if (!IsLoaded || _isResetting) return;
        PacsService.Instance.Settings.LastModalityFilter = GetSelectedModalitiesString();
        PacsService.Instance.SaveSettings();
        _modalitySearchPending = true;
        if (_modalityDebounceTimer == null)
        {
            _modalityDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _modalityDebounceTimer.Tick += (s, e) =>
            {
                _modalityDebounceTimer.Stop();
                if (_modalitySearchPending)
                {
                    _modalitySearchPending = false;
                    OnSearchClicked(this, new RoutedEventArgs());
                }
            };
        }
        _modalityDebounceTimer.Stop();
        _modalityDebounceTimer.Start();
    }

    private void OnModalityPopupClosed(object? sender, EventArgs e)
    {
        if (_modalitySearchPending)
        {
            _modalityDebounceTimer?.Stop();
            _modalitySearchPending = false;
            if (IsLoaded && !_isResetting)
            {
                OnSearchClicked(this, new RoutedEventArgs());
            }
        }
    }

    private void OnDatePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PnlCustomDate == null) return;
        string? sel = (CmbDatePreset.SelectedItem as ComboBoxItem)?.Content?.ToString();
        bool isCustom = sel == "Custom..." || sel == "Personalizat...";
        PnlCustomDate.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        if (!IsLoaded || _isResetting) return;
        if (!string.IsNullOrWhiteSpace(sel))
        {
            PacsService.Instance.Settings.LastDatePreset = sel;
            PacsService.Instance.SaveSettings();
        }
        if (!isCustom)
        {
            OnSearchClicked(this, new RoutedEventArgs());
        }
    }

    private void OnCustomDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isResetting) return;
        if (DpFrom.SelectedDate.HasValue && DpTo.SelectedDate.HasValue)
        {
            OnSearchClicked(this, new RoutedEventArgs());
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSearchClicked(sender, e);
        }
    }

    private void OnResetFiltersClicked(object sender, RoutedEventArgs e)
    {
        _isResetting = true;
        try
        {
            TxtSearchQuery.Text = string.Empty;
            ResetModalitySelectionToAll();
            CmbDatePreset.SelectedIndex = 0;
            DpFrom.SelectedDate = null;
            DpTo.SelectedDate = null;
            PacsService.Instance.Settings.LastModalityFilter = "All";
            PacsService.Instance.Settings.LastDatePreset = "Today";
            PacsService.Instance.SaveSettings();
            FoundStudies.Clear();
            SelectedStudySeries.Clear();
            TxtStudiesCount.Text = "Studies found: 0";
            TxtSeriesHeader.Text = "Series in selected study: 0";
            TxtTransferStatus.Text = "Filters reset.";
        }
        finally
        {
            _isResetting = false;
        }
    }

    private void OnOpenConfigClicked(object sender, RoutedEventArgs e)
    {
        var cfgWin = new PacsConfigurationWindow { Owner = this };
        if (cfgWin.ShowDialog() == true)
        {
            PopulateServerLocations();
        }
    }

    private async void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        var selectedItem = CmbServerLocation.SelectedItem as ComboBoxItem;
        bool isAllServers = selectedItem != null && selectedItem.Tag == null;
        List<PacsServerConfig> targetServers;

        if (isAllServers)
        {
            targetServers = PacsService.Instance.Settings.Servers.Where(s => s.IsEnabled).ToList();
        }
        else
        {
            var single = GetSelectedServer();
            if (single == null)
            {
                TxtTransferStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 160, 60));
                TxtTransferStatus.Text = "Niciun server PACS nu este configurat. Deschideți ⚙ Settings pentru configurare.";
                return;
            }
            targetServers = new List<PacsServerConfig> { single };
        }

        if (targetServers.Count == 0)
        {
            TxtTransferStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 160, 60));
            TxtTransferStatus.Text = "Niciun server PACS nu este activ. Vă rugăm să activați cel puțin un server în ⚙ Settings.";
            return;
        }

        _isSearching = true;
        BtnSearch.IsEnabled = false;
        BtnStopSearch.Visibility = Visibility.Visible;
        BtnStopSearch.IsEnabled = true;
        BtnBottomStop.Visibility = Visibility.Visible;
        BtnBottomStop.IsEnabled = true;

        FoundStudies.Clear();
        SelectedStudySeries.Clear();
        TxtStudiesCount.Text = "Searching...";
        TxtTransferStatus.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#AAAAAA")!;
        TxtTransferStatus.ToolTip = null;
        TxtTransferStatus.Text = isAllServers 
            ? $"Searching across {targetServers.Count} PACS servers in parallel..." 
            : $"Sending C-FIND query to {targetServers[0].Description} ({targetServers[0].IpAddress}:{targetServers[0].Port})...";
        PbTransfer.Visibility = Visibility.Visible;
        PbTransfer.IsIndeterminate = true;

        var filter = BuildSearchFilter();
        _activeCts?.Cancel();
        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;

        var failedServers = new System.Collections.Concurrent.ConcurrentBag<(string Name, string Error)>();

        var tasks = targetServers.Select(async s =>
        {
            try
            {
                var results = await PacsService.Instance.QueryStudiesAsync(s, filter, ct);
                Dispatcher.Invoke(() =>
                {
                    foreach (var r in results)
                    {
                        if (filter.Matches(r) && !FoundStudies.Any(existing => existing.StudyInstanceUid == r.StudyInstanceUid))
                        {
                            r.IsReviewed = DatabaseService.IsStudyReviewed(r.StudyInstanceUid);
                            FoundStudies.Add(r);
                        }
                    }

                    // Sort descending by moment of scan (Date + Time) so most recent is first
                    var sortedList = FoundStudies.OrderByDescending(x => x.SortDateTimeKey).ToList();
                    for (int i = 0; i < sortedList.Count; i++)
                    {
                        int oldIdx = FoundStudies.IndexOf(sortedList[i]);
                        if (oldIdx >= 0 && oldIdx != i)
                        {
                            FoundStudies.Move(oldIdx, i);
                        }
                    }
                    TxtStudiesCount.Text = $"Studies found: {FoundStudies.Count}";
                });
            }
            catch (OperationCanceledException)
            {
                // User cancelled
            }
            catch (Exception ex)
            {
                failedServers.Add((s.Description, ex.Message));
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch { }
        finally
        {
            _isSearching = false;
            _secondsUntilRefresh = 60;
            UpdateAutoRefreshLabel();
            BtnSearch.IsEnabled = true;
            BtnStopSearch.Visibility = Visibility.Collapsed;
            BtnBottomStop.Visibility = Visibility.Collapsed;
            PbTransfer.IsIndeterminate = false;
            PbTransfer.Visibility = Visibility.Collapsed;
        }

        // If cancelled by user manually
        if (ct.IsCancellationRequested)
        {
            TxtTransferStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 175, 40));
            TxtTransferStatus.Text = "Căutare PACS oprită manual de utilizator.";
            TxtStudiesCount.Text = $"Studies found: {FoundStudies.Count} (Oprit)";
            return;
        }

        // Best-effort: pull reviewed marks from ParsecPACS (silently ignored if unavailable)
        _ = SyncReviewedFromParsecPacsAsync(targetServers, ct);

        if (FoundStudies.Count > 0)
        {
            var sortedList = FoundStudies.OrderByDescending(x => x.SortDateTimeKey).ToList();
            for (int i = 0; i < sortedList.Count; i++)
            {
                int oldIdx = FoundStudies.IndexOf(sortedList[i]);
                if (oldIdx >= 0 && oldIdx != i)
                {
                    FoundStudies.Move(oldIdx, i);
                }
            }

            GridStudies.SelectedIndex = 0;
            if (failedServers.Count > 0)
            {
                var names = string.Join(", ", failedServers.Select(f => f.Name));
                TxtTransferStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 80));
                TxtTransferStatus.Text = $"Căutare finalizată: {FoundStudies.Count} studii găsite. (Inaccesibil: {names})";
                TxtTransferStatus.ToolTip = string.Join("\n", failedServers.Select(f => $"{f.Name}: {f.Error}"));
            }
            else
            {
                TxtTransferStatus.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#AAAAAA")!;
                TxtTransferStatus.Text = $"Search completed: {FoundStudies.Count} studies found.";
                TxtTransferStatus.ToolTip = null;
            }
        }
        else
        {
            if (isAllServers)
            {
                if (failedServers.Count == targetServers.Count)
                {
                    // Non-intrusive error display in status bar (no loud modal dialog)
                    TxtTransferStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
                    TxtTransferStatus.Text = $"⚠️ Niciun server PACS nu a putut fi contactat ({string.Join(", ", failedServers.Select(f => f.Name))}). Verificați rețeaua/IP/Port.";
                    TxtTransferStatus.ToolTip = "Could not reach any PACS server:\n\n" + string.Join("\n", failedServers.Select(f => $"• {f.Name}: {f.Error}")) + "\n\nVerificați conexiunea la rețeaua spitalului/VPN, adresele IP, porturile și setările firewall.";
                    TxtStudiesCount.Text = "Studies found: 0 (Eroare conectare)";
                }
                else
                {
                    TxtTransferStatus.Foreground = failedServers.Count > 0
                        ? new SolidColorBrush(Color.FromRgb(255, 180, 80))
                        : (SolidColorBrush)new BrushConverter().ConvertFrom("#AAAAAA")!;
                    TxtTransferStatus.Text = failedServers.Count > 0
                        ? $"No studies found. (Unreachable servers: {string.Join(", ", failedServers.Select(f => f.Name))})"
                        : "No studies found matching specified criteria.";
                    TxtStudiesCount.Text = "Studies found: 0";
                }
            }
            else
            {
                if (failedServers.Count > 0)
                {
                    var err = failedServers.First();
                    string shortErr = FormatShortDicomError(err.Error);
                    TxtTransferStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
                    TxtTransferStatus.Text = $"⚠️ Eroare conectare la {err.Name}: {shortErr}";
                    TxtTransferStatus.ToolTip = $"Server: {err.Name}\nEroare detaliată:\n{err.Error}\n\nVerificați conexiunea la rețeaua spitalului/VPN, setările IP/Port/AE Title și setările firewall.";
                    TxtStudiesCount.Text = "Studies found: 0 (Eroare conectare)";
                }
                else
                {
                    TxtTransferStatus.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#AAAAAA")!;
                    TxtTransferStatus.Text = $"No studies found on {targetServers[0].Description}.";
                    TxtTransferStatus.ToolTip = null;
                    TxtStudiesCount.Text = "Studies found: 0";
                }
            }
        }
    }

    private void OnStopSearchClicked(object sender, RoutedEventArgs e)
    {
        if (_activeCts != null && !_activeCts.IsCancellationRequested)
        {
            _activeCts.Cancel();
            TxtTransferStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 175, 40));
            TxtTransferStatus.Text = "Se oprește căutarea PACS...";
            BtnStopSearch.IsEnabled = false;
            BtnBottomStop.IsEnabled = false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _isSearching)
        {
            OnStopSearchClicked(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private static string FormatShortDicomError(string rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError)) return "Eroare necunoscută.";
        if (rawError.Contains("connected party did not properly respond", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("failed because connected host has failed to respond", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("connection attempt failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Serverul nu răspunde (Timeout). Verificați conexiunea/VPN sau dacă serverul PACS este pornit.";
        }
        if (rawError.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
        {
            return "Conexiune refuzată pe portul specificat. Verificați dacă serviciul PACS rulează pe acel port.";
        }
        if (rawError.Contains("No such host is known", StringComparison.OrdinalIgnoreCase))
        {
            return "Adresă IP / Host necunoscut. Verificați IP-ul serverului în setări.";
        }
        if (rawError.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Timpul de conectare a expirat (Timeout).";
        }
        string firstLine = rawError.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? rawError;
        if (firstLine.Length > 95) firstLine = firstLine.Substring(0, 92) + "...";
        return firstLine.Trim();
    }

    private PacsSearchFilter BuildSearchFilter()
    {
        var filter = new PacsSearchFilter();
        string q = TxtSearchQuery.Text.Trim();
        if (!string.IsNullOrEmpty(q))
        {
            // Could be patient name, id, or accession
            filter.PatientName = q;
        }

        string mod = GetSelectedModalitiesString();
        if (mod != "All" && mod != "Toate")
        {
            filter.Modality = mod;
        }

        string datePreset = (CmbDatePreset.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Any time";
        var today = DateTime.Today;

        switch (datePreset)
        {
            case "Today":
            case "Azi":
                filter.DateFrom = today;
                filter.DateTo = today;
                break;
            case "Yesterday":
            case "Ieri":
                filter.DateFrom = today.AddDays(-1);
                filter.DateTo = today.AddDays(-1);
                break;
            case "Last 3 days":
            case "Ultimele 3 zile":
                filter.DateFrom = today.AddDays(-3);
                filter.DateTo = today;
                break;
            case "Last 7 days":
            case "Ultimele 7 zile":
                filter.DateFrom = today.AddDays(-7);
                filter.DateTo = today;
                break;
            case "Last month":
            case "Ultima lună":
                filter.DateFrom = today.AddMonths(-1);
                filter.DateTo = today;
                break;
            case "Custom...":
            case "Personalizat...":
                filter.DateFrom = DpFrom.SelectedDate;
                filter.DateTo = DpTo.SelectedDate;
                break;
        }

        return filter;
    }

    private async void OnStudySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedStudies = GridStudies.SelectedItems.Cast<PacsStudyResult>().ToList();
        if (selectedStudies.Count == 0)
        {
            BtnDownloadAndOpen.IsEnabled = false;
            BtnDownloadAndOpen.Content = "Open";
            BtnImportToDb.IsEnabled = false;
            BtnImportToDb.Content = "Import to Database";
            SelectedStudySeries.Clear();
            TxtSeriesHeader.Text = "Series in selected study: 0";
            return;
        }

        BtnDownloadAndOpen.IsEnabled = true;
        BtnImportToDb.IsEnabled = true;

        if (selectedStudies.Count > 1)
        {
            BtnDownloadAndOpen.Content = $"Open ({selectedStudies.Count})";
            BtnImportToDb.Content = $"Import ({selectedStudies.Count}) to DB";
            TxtSeriesHeader.Text = $"Multiple studies selected ({selectedStudies.Count})";
            SelectedStudySeries.Clear();
            return;
        }

        BtnDownloadAndOpen.Content = "Open";
        BtnImportToDb.Content = "Import to Database";
        var study = selectedStudies[0];

        var server = PacsService.Instance.Settings.Servers.FirstOrDefault(s => s.Id == study.ServerId) ?? GetSelectedServer();
        if (server == null) return;

        SelectedStudySeries.Clear();
        TxtSeriesHeader.Text = "Loading series...";

        try
        {
            var seriesList = await PacsService.Instance.QuerySeriesAsync(server, study.StudyInstanceUid);
            study.Series = seriesList;
            foreach (var s in seriesList)
            {
                SelectedStudySeries.Add(s);
            }
            TxtSeriesHeader.Text = $"Series in selected study: {SelectedStudySeries.Count}";
        }
        catch (Exception ex)
        {
            TxtSeriesHeader.Text = "Error loading series";
            System.Diagnostics.Debug.WriteLine($"[PacsSearch] Error querying series: {ex.Message}");
        }
    }

    private void OnStudyDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridStudies.SelectedItem is PacsStudyResult)
        {
            OnDownloadAndOpenClicked(sender, e);
        }
    }

    private void OnSeriesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridSeries.SelectedItem is PacsSeriesResult)
        {
            OnDownloadSelectedSeriesClicked(sender, e);
        }
    }

    private void OnToggleStudyReviewedClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PacsStudyResult study)
        {
            study.IsReviewed = DatabaseService.ToggleStudyReviewed(study.StudyInstanceUid);
            PushReviewedToParsecPacs(study);
            e.Handled = true;
        }
    }

    private void OnToggleReviewedContextMenu(object sender, RoutedEventArgs e)
    {
        ToggleSelectedStudiesReviewed();
    }

    private void OnGridStudiesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            ToggleSelectedStudiesReviewed();
            e.Handled = true;
        }
    }

    private void ToggleSelectedStudiesReviewed()
    {
        var selected = GridStudies.SelectedItems.OfType<PacsStudyResult>().ToList();
        if (selected.Count == 0 && GridStudies.SelectedItem is PacsStudyResult single)
        {
            selected.Add(single);
        }
        if (selected.Count == 0) return;

        bool targetState = selected.Any(s => !s.IsReviewed);
        foreach (var s in selected)
        {
            if (s.IsReviewed != targetState)
            {
                s.IsReviewed = DatabaseService.ToggleStudyReviewed(s.StudyInstanceUid);
                PushReviewedToParsecPacs(s);
            }
        }
    }

    #region ParsecPACS Reviewed Sync (best-effort, optional)

    /// <summary>Hosts to talk to for a given study: local ParsecPACS + the study's source server IP.</summary>
    private static List<string> GetSyncHostsForStudy(PacsStudyResult study)
    {
        var server = PacsService.Instance.Settings.Servers.FirstOrDefault(s => s.Id == study.ServerId);
        return ParsecPacsSyncService.GetCandidateHosts(server != null ? new[] { server } : null);
    }

    private static void PushReviewedToParsecPacs(PacsStudyResult study)
    {
        // Runs on a background thread, never throws, never blocks the UI
        ParsecPacsSyncService.PushReviewedStateInBackground(study.StudyInstanceUid, study.IsReviewed, GetSyncHostsForStudy(study));
    }

    private async Task SyncReviewedFromParsecPacsAsync(List<PacsServerConfig> servers, CancellationToken ct)
    {
        try
        {
            var hosts = ParsecPacsSyncService.GetCandidateHosts(servers);
            var responses = await ParsecPacsSyncService.FetchReviewedUidsAsync(hosts, ct);
            if (responses.Count == 0 || ct.IsCancellationRequested) return;

            var allReviewed = new HashSet<string>(responses.Values.SelectMany(v => v), StringComparer.OrdinalIgnoreCase);
            var serverById = PacsService.Instance.Settings.Servers
                .Where(s => !string.IsNullOrEmpty(s.Id))
                .GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var study in FoundStudies.ToList())
            {
                if (string.IsNullOrWhiteSpace(study.StudyInstanceUid)) continue;

                bool? newState = null;
                if (allReviewed.Contains(study.StudyInstanceUid))
                {
                    newState = true;
                }
                else if (serverById.TryGetValue(study.ServerId, out var src) &&
                         responses.ContainsKey(ParsecPacsSyncService.NormalizeHost(src.IpAddress)))
                {
                    // The study's own server answered and does not list it -> it was un-marked there
                    newState = false;
                }

                if (newState.HasValue && study.IsReviewed != newState.Value)
                {
                    study.IsReviewed = newState.Value;
                    DatabaseService.SetStudyReviewed(study.StudyInstanceUid, newState.Value);
                }
            }
        }
        catch
        {
            // Silent by design: works as a plain local marker when ParsecPACS is not available
        }
    }

    #endregion

    #region Auto-Refresh (Silent, every 60s)

    private System.Windows.Threading.DispatcherTimer? _autoRefreshTimer;
    private int _secondsUntilRefresh = 60;
    private bool _isAutoRefreshing;
    private string _lastAutoRefreshTime = string.Empty;

    private void SetupAutoRefresh()
    {
        _secondsUntilRefresh = 60;
        _autoRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _autoRefreshTimer.Tick += OnAutoRefreshTick;
        _autoRefreshTimer.Start();

        Closed += (s, e) =>
        {
            _autoRefreshTimer?.Stop();
            _autoRefreshTimer = null;
            _modalityDebounceTimer?.Stop();
            _modalityDebounceTimer = null;
        };
    }

    private void UpdateAutoRefreshLabel()
    {
        if (TxtAutoRefreshCountdown == null) return;
        if (_isAutoRefreshing)
        {
            TxtAutoRefreshCountdown.Text = "🔄 Auto-refresh: se actualizează...";
            TxtAutoRefreshCountdown.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#F5AF28")!;
        }
        else
        {
            string suffix = !string.IsNullOrEmpty(_lastAutoRefreshTime) ? $" • {_lastAutoRefreshTime}" : string.Empty;
            TxtAutoRefreshCountdown.Text = $"🔄 Auto-refresh: {_secondsUntilRefresh}s{suffix}";
            TxtAutoRefreshCountdown.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#888888")!;
        }
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (!IsLoaded || _isResetting) return;

        // If manual search, transfer, or previous auto-refresh is actively running, pause countdown
        if (_isSearching || _isAutoRefreshing || PbTransfer.Visibility == Visibility.Visible)
        {
            return;
        }

        _secondsUntilRefresh--;
        if (_secondsUntilRefresh > 0)
        {
            UpdateAutoRefreshLabel();
            return;
        }

        _secondsUntilRefresh = 60;
        await PerformSilentRefreshAsync();
    }

    private async Task PerformSilentRefreshAsync()
    {
        if (_isSearching || _isAutoRefreshing) return;
        _isAutoRefreshing = true;
        UpdateAutoRefreshLabel();

        try
        {
            var selectedItem = CmbServerLocation.SelectedItem as ComboBoxItem;
            bool isAllServers = selectedItem != null && selectedItem.Tag == null;
            List<PacsServerConfig> targetServers;

            if (isAllServers)
            {
                targetServers = PacsService.Instance.Settings.Servers.Where(s => s.IsEnabled).ToList();
            }
            else
            {
                var single = GetSelectedServer();
                if (single == null) return;
                targetServers = new List<PacsServerConfig> { single };
            }

            if (targetServers.Count == 0) return;

            var filter = BuildSearchFilter();
            int timeoutSec = Math.Max(30, PacsService.Instance.Settings.TimeoutSeconds);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var ct = cts.Token;

            bool selectedStudyChanged = false;
            PacsStudyResult? singleSelectedStudy = null;
            if (GridStudies.SelectedItems.Count == 1 && GridStudies.SelectedItem is PacsStudyResult selSt)
            {
                singleSelectedStudy = selSt;
            }

            var tasks = targetServers.Select(async s =>
            {
                try
                {
                    var results = await PacsService.Instance.QueryStudiesAsync(s, filter, ct);
                    if (_isSearching || _isResetting) return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_isSearching || _isResetting) return;

                        var serverFreshByUid = new Dictionary<string, PacsStudyResult>(StringComparer.OrdinalIgnoreCase);
                        foreach (var r in results)
                        {
                            if (filter.Matches(r) && !serverFreshByUid.ContainsKey(r.StudyInstanceUid))
                            {
                                r.IsReviewed = DatabaseService.IsStudyReviewed(r.StudyInstanceUid);
                                serverFreshByUid[r.StudyInstanceUid] = r;
                            }
                        }

                        // Update existing studies on this server or in table
                        foreach (var existing in FoundStudies.ToList())
                        {
                            if (serverFreshByUid.TryGetValue(existing.StudyInstanceUid, out var fresh))
                            {
                                bool seriesOrInstChanged =
                                    (fresh.SeriesCount > 0 && existing.SeriesCount != fresh.SeriesCount) ||
                                    (fresh.InstanceCount > 0 && existing.InstanceCount != fresh.InstanceCount);

                                if (existing.IsReviewed != fresh.IsReviewed) existing.IsReviewed = fresh.IsReviewed;
                                if (fresh.InstanceCount > 0 && existing.InstanceCount != fresh.InstanceCount) existing.InstanceCount = fresh.InstanceCount;
                                if (fresh.SeriesCount > 0 && existing.SeriesCount != fresh.SeriesCount) existing.SeriesCount = fresh.SeriesCount;
                                if (!string.IsNullOrWhiteSpace(fresh.Modality) && existing.Modality != fresh.Modality) existing.Modality = fresh.Modality;
                                if (!string.IsNullOrWhiteSpace(fresh.StudyDescription) && existing.StudyDescription != fresh.StudyDescription) existing.StudyDescription = fresh.StudyDescription;
                                if (!string.IsNullOrWhiteSpace(fresh.ReadingPhysician) && existing.ReadingPhysician != fresh.ReadingPhysician) existing.ReadingPhysician = fresh.ReadingPhysician;
                                if (!string.IsNullOrWhiteSpace(fresh.PerformingPhysician) && existing.PerformingPhysician != fresh.PerformingPhysician) existing.PerformingPhysician = fresh.PerformingPhysician;
                                if (!string.IsNullOrWhiteSpace(fresh.ReferringPhysician) && existing.ReferringPhysician != fresh.ReferringPhysician) existing.ReferringPhysician = fresh.ReferringPhysician;

                                if (seriesOrInstChanged && singleSelectedStudy != null &&
                                    string.Equals(singleSelectedStudy.StudyInstanceUid, existing.StudyInstanceUid, StringComparison.OrdinalIgnoreCase))
                                {
                                    selectedStudyChanged = true;
                                }
                            }
                        }

                        // Add newly discovered studies
                        var existingUids = new HashSet<string>(FoundStudies.Select(st => st.StudyInstanceUid), StringComparer.OrdinalIgnoreCase);
                        bool anyAdded = false;
                        foreach (var fresh in serverFreshByUid.Values)
                        {
                            if (!existingUids.Contains(fresh.StudyInstanceUid))
                            {
                                FoundStudies.Add(fresh);
                                existingUids.Add(fresh.StudyInstanceUid);
                                anyAdded = true;
                            }
                        }

                        if (anyAdded || FoundStudies.Count > 1)
                        {
                            var sortedList = FoundStudies.OrderByDescending(x => x.SortDateTimeKey).ToList();
                            for (int i = 0; i < sortedList.Count; i++)
                            {
                                int oldIdx = FoundStudies.IndexOf(sortedList[i]);
                                if (oldIdx >= 0 && oldIdx != i)
                                {
                                    FoundStudies.Move(oldIdx, i);
                                }
                            }
                        }

                        if (GridStudies.SelectedItems.Count == 0 && FoundStudies.Count > 0)
                        {
                            GridStudies.SelectedIndex = 0;
                        }

                        TxtStudiesCount.Text = $"Studies found: {FoundStudies.Count}";
                    });
                }
                catch
                {
                    // Silent: no popups or messageboxes on background auto-refresh
                }
            });

            await Task.WhenAll(tasks);

            if (!_isSearching && !_isResetting)
            {
                // Best-effort sync of Reviewed marks from ParsecPACS
                using var syncCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SyncReviewedFromParsecPacsAsync(targetServers, syncCts.Token);

                // If the currently selected study gained new series/instances, refresh its series sub-table too
                if (selectedStudyChanged && singleSelectedStudy != null)
                {
                    var srv = PacsService.Instance.Settings.Servers.FirstOrDefault(x => x.Id == singleSelectedStudy.ServerId) ?? GetSelectedServer();
                    if (srv != null)
                    {
                        try
                        {
                            var seriesList = await PacsService.Instance.QuerySeriesAsync(srv, singleSelectedStudy.StudyInstanceUid);
                            singleSelectedStudy.Series = seriesList;
                            SelectedStudySeries.Clear();
                            foreach (var ser in seriesList)
                            {
                                SelectedStudySeries.Add(ser);
                            }
                            TxtSeriesHeader.Text = $"Series in selected study: {SelectedStudySeries.Count}";
                        }
                        catch { }
                    }
                }

                _lastAutoRefreshTime = DateTime.Now.ToString("HH:mm:ss");
                TxtTransferStatus.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#AAAAAA")!;
                TxtTransferStatus.Text = $"Auto-refreshed at {_lastAutoRefreshTime} ({FoundStudies.Count} studies).";
            }
        }
        catch
        {
            // Fully silent
        }
        finally
        {
            _isAutoRefreshing = false;
            _secondsUntilRefresh = 60;
            UpdateAutoRefreshLabel();
        }
    }

    #endregion

    #region Download Actions

    private void OnDownloadAndOpenClicked(object sender, RoutedEventArgs e)
    {
        var selectedStudies = GridStudies.SelectedItems.Cast<PacsStudyResult>().ToList();
        if (selectedStudies.Count == 0) return;

        var studyList = new List<(PacsServerConfig server, PacsStudyResult study)>();
        foreach (var study in selectedStudies)
        {
            var server = PacsService.Instance.Settings.Servers.FirstOrDefault(s => s.Id == study.ServerId) ?? GetSelectedServer();
            if (server != null)
            {
                studyList.Add((server, study));
            }
        }

        if (studyList.Count == 0)
        {
            MessageBox.Show("Please select a PACS server.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Immediately start progressive live streaming into the viewer and close dialog
        if (studyList.Count == 1)
        {
            _mainWindow.StartPacsStreamingStudy(studyList[0].server, studyList[0].study);
        }
        else
        {
            _mainWindow.StartPacsStreamingStudies(studyList);
        }

        Close();
    }

    private void OnDownloadSelectedSeriesClicked(object sender, RoutedEventArgs e)
    {
        if (GridStudies.SelectedItem is not PacsStudyResult study || GridSeries.SelectedItem is not PacsSeriesResult series)
        {
            MessageBox.Show("Please select a study and a series.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var server = PacsService.Instance.Settings.Servers.FirstOrDefault(s => s.Id == study.ServerId) ?? GetSelectedServer();
        if (server == null) return;

        // Immediately start progressive live streaming of this series into viewer and close dialog
        _mainWindow.StartPacsStreamingStudy(server, study, series);
        Close();
    }

    private async void OnImportToDatabaseClicked(object sender, RoutedEventArgs e)
    {
        var selectedStudies = GridStudies.SelectedItems.Cast<PacsStudyResult>().ToList();
        if (selectedStudies.Count == 0) return;

        int count = selectedStudies.Count;
        int imported = 0;

        int totalBatchExpected = 0;
        foreach (var st in selectedStudies)
        {
            int exp = st.InstanceCount > 0 ? st.InstanceCount : (st.Series != null && st.Series.Count > 0 ? st.Series.Sum(s => s.InstanceCount) : 0);
            totalBatchExpected += Math.Max(1, exp);
        }

        int batchCompletedImagesPrior = 0;
        double currentBatchPercent = 0.0;

        BtnImportToDb.IsEnabled = false;
        BtnDownloadAndOpen.IsEnabled = false;
        PbTransfer.Visibility = Visibility.Visible;
        PbTransfer.Maximum = 100;
        PbTransfer.Value = 0;

        _activeCts?.Cancel();
        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;

        try
        {
            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) break;

                var study = selectedStudies[i];
                var server = PacsService.Instance.Settings.Servers.FirstOrDefault(s => s.Id == study.ServerId) ?? GetSelectedServer();
                if (server == null) continue;

                int studyIndex = i;
                int expected = study.InstanceCount > 0
                    ? study.InstanceCount
                    : (study.Series != null && study.Series.Count > 0 ? study.Series.Sum(s => s.InstanceCount) : 1);

                TxtTransferStatus.Text = count > 1
                    ? $"[{studyIndex + 1}/{count}] {study.DisplayPatientName} — Conectare și descărcare din PACS..."
                    : $"{study.DisplayPatientName} — Conectare și descărcare din PACS...";

                var progress = new Progress<(int current, int total, string message)>(p =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        int currentStudyDone = p.current;
                        int currentStudyTotal = p.total > 0 ? p.total : expected;
                        int totalDone = batchCompletedImagesPrior + currentStudyDone;

                        double studyPct = currentStudyTotal > 0 ? (double)currentStudyDone / currentStudyTotal : 0.0;
                        double patientWeightPct = ((double)studyIndex + Math.Clamp(studyPct, 0.0, 1.0)) / count * 100.0;
                        double imageWeightPct = totalBatchExpected > 0 ? ((double)totalDone / totalBatchExpected) * 100.0 : patientWeightPct;

                        double targetPct = totalBatchExpected > 0 ? Math.Max(imageWeightPct, patientWeightPct * 0.9) : patientWeightPct;
                        currentBatchPercent = Math.Max(currentBatchPercent, Math.Clamp(targetPct, 0.0, 99.0));
                        PbTransfer.Value = currentBatchPercent;

                        TxtTransferStatus.Text = count > 1
                            ? $"[{studyIndex + 1}/{count}] {study.DisplayPatientName}: {p.message} • Total lot: {totalDone}/{totalBatchExpected}"
                            : $"{study.DisplayPatientName}: {p.message}";
                    });
                });

                string? downloadedFolder = null;
                try
                {
                    downloadedFolder = await PacsService.Instance.DownloadStudyAsync(server, study.StudyInstanceUid, expected, progress, ct);
                }
                catch (Exception dlEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[PacsSearch] Error downloading {study.DisplayPatientName}: {dlEx.Message}");
                    TxtTransferStatus.Text = count > 1
                        ? $"[{studyIndex + 1}/{count}] {study.DisplayPatientName} — Eroare descărcare ({dlEx.Message}), se continuă..."
                        : $"{study.DisplayPatientName} — Eroare descărcare: {dlEx.Message}";
                    await Task.Delay(1200);
                    batchCompletedImagesPrior += expected;
                    continue;
                }

                if (string.IsNullOrEmpty(downloadedFolder) || !Directory.Exists(downloadedFolder))
                {
                    batchCompletedImagesPrior += expected;
                    continue;
                }

                TxtTransferStatus.Text = count > 1
                    ? $"[{studyIndex + 1}/{count}] {study.DisplayPatientName} — Salvare și indexare în arhiva locală..."
                    : $"{study.DisplayPatientName} — Salvare și indexare în arhiva locală...";

                int studyImagesCount = expected;
                try
                {
                    await Task.Run(async () =>
                    {
                        var loader = new DicomLoaderService();
                        var patients = await loader.ScanFolderAsync(downloadedFolder);
                        var loadedStudies = patients.SelectMany(p => p.Studies).ToList();
                        if (loadedStudies.Count > 0)
                        {
                            var primaryStudy = loadedStudies[0];
                            foreach (var extra in loadedStudies.Skip(1))
                            {
                                foreach (var ser in extra.Series)
                                {
                                    if (!primaryStudy.Series.Contains(ser)) primaryStudy.Series.Add(ser);
                                }
                            }

                            int actualSlices = primaryStudy.Series.Sum(s => s.SliceCount);
                            if (actualSlices > 0) studyImagesCount = actualSlices;

                            DatabaseService.ImportStudy(primaryStudy, study.DisplayPatientName, study.PatientId, study.BirthDate);
                            Interlocked.Increment(ref imported);
                        }
                    });
                }
                catch (Exception dbEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[Database Import] Error importing {study.DisplayPatientName}: {dbEx.Message}");
                }

                batchCompletedImagesPrior += studyImagesCount;
                currentBatchPercent = Math.Max(currentBatchPercent, ((double)(studyIndex + 1) / count) * 100.0);
                PbTransfer.Value = currentBatchPercent;

                study.IsReviewed = DatabaseService.IsStudyReviewed(study.StudyInstanceUid);

                if (i + 1 < count)
                {
                    await Task.Delay(400);
                }
            }

            TxtTransferStatus.Text = $"Import finalizat: {imported}/{count} studii salvate în baza de date.";
            if (count == 1)
            {
                var study = selectedStudies[0];
                MessageBox.Show($"Studiul '{study.DisplayPatientName}' a fost importat și indexat cu succes în baza locală!", "Import Bază de Date", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Au fost importate cu succes {imported} din cele {count} studii selectate în baza de date locală!", "Import Bază de Date", MessageBoxButton.OK, MessageBoxImage.Information);
            }

        }
        finally
        {
            PbTransfer.Visibility = Visibility.Collapsed;
            BtnImportToDb.IsEnabled = true;
            BtnDownloadAndOpen.IsEnabled = true;
        }
    }

    private void OnFindAllPatientStudiesClicked(object sender, RoutedEventArgs e)
    {
        if (GridStudies.SelectedItem is not PacsStudyResult study) return;
        TxtSearchQuery.Text = string.IsNullOrWhiteSpace(study.PatientId) ? study.PatientName : study.PatientId;
        _isResetting = true;
        try
        {
            ResetModalitySelectionToAll(); // All modalities
            CmbDatePreset.SelectedIndex = 0; // Any time
        }
        finally
        {
            _isResetting = false;
        }
        OnSearchClicked(sender, e);
    }

    #endregion

    #region Upload / C-STORE SCU to PACS

    private async void OnSendActiveStudyToPacsClicked(object sender, RoutedEventArgs e)
    {
        var activeVp = _mainWindow.ViewModel.ActiveViewport;
        var activeSeries = activeVp?.OriginalSeries ?? activeVp?.CurrentSeries;
        StudyModel? activeStudy = null;
        if (activeSeries != null)
        {
            foreach (var p in _mainWindow.ViewModel.Patients)
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
            MessageBox.Show("No study is currently open in the viewer to send to PACS.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var server = GetSelectedServer();
        if (server == null)
        {
            MessageBox.Show("Please select a destination PACS server.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var allFiles = activeStudy.Series
            .SelectMany(s => s.Slices)
            .Select(sl => sl.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct()
            .ToList();

        int totalFiles = allFiles.Count;
        int totalSlices = activeStudy.Series.Sum(s => s.Slices.Count);
        string patName = activeSeries?.PatientName ?? "Patient";
        string sliceNote = totalSlices != totalFiles
            ? $"\nTotal files: {totalFiles} DICOM files ({totalSlices} frames/slices)"
            : $"\nTotal images: {totalFiles}";

        var confirm = MessageBox.Show(
            $"Are you sure you want to send the active study to PACS?\n\nPatient: {patName}\nSeries: {activeStudy.Series.Count}{sliceNote}\n\nDestination PACS: {server.Description} ({server.IpAddress}:{server.Port}, AE: {server.AeTitle})",
            "Confirm PACS C-STORE Export",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        PbTransfer.Visibility = Visibility.Visible;
        PbTransfer.Maximum = totalFiles;
        PbTransfer.Value = 0;
        TxtTransferStatus.Text = $"Sending {totalFiles} DICOM files ({totalSlices} frames) to {server.Description}...";

        var progress = new Progress<(int current, int total, string message)>(p =>
        {
            Dispatcher.Invoke(() =>
            {
                TxtTransferStatus.Text = p.message;
                PbTransfer.Value = p.current;
            });
        });

        try
        {
            var result = await PacsService.Instance.SendStudyToPacsAsync(server, activeStudy, progress);
            int succ = result.successCount;
            int fail = result.failedCount;
            string msg = result.message;
            TxtTransferStatus.Text = msg;
            MessageBox.Show(msg, "PACS Export Finished", MessageBoxButton.OK, fail == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            TxtTransferStatus.Text = $"C-STORE Error: {ex.Message}";
            MessageBox.Show($"Error sending to PACS: {ex.Message}", "PACS Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PbTransfer.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSendStudyToPacsClicked(object sender, RoutedEventArgs e)
    {
        OnSendActiveStudyToPacsClicked(sender, e);
    }

    private async void OnSendSeriesToPacsClicked(object sender, RoutedEventArgs e)
    {
        var activeVp = _mainWindow.ViewModel.ActiveViewport;
        var activeSeries = activeVp?.OriginalSeries ?? activeVp?.CurrentSeries;
        if (activeSeries == null || activeSeries.Slices.Count == 0)
        {
            MessageBox.Show("No active series is selected in the viewer.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var server = GetSelectedServer();
        if (server == null) return;

        var files = activeSeries.Slices
            .Select(sl => sl.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct()
            .ToList();
        int totalFiles = files.Count;
        int totalSlices = activeSeries.Slices.Count;
        string countText = totalSlices != totalFiles
            ? $"{totalFiles} files ({totalSlices} frames)"
            : $"{totalFiles} images";

        var confirm = MessageBox.Show(
            $"Send active series '{activeSeries.SeriesDescription}' ({countText}) to {server.Description}?",
            "Export Series to PACS",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        PbTransfer.Visibility = Visibility.Visible;
        PbTransfer.Maximum = totalFiles;
        PbTransfer.Value = 0;

        var progress = new Progress<(int current, int total, string message)>(p =>
        {
            Dispatcher.Invoke(() =>
            {
                TxtTransferStatus.Text = p.message;
                PbTransfer.Value = p.current;
            });
        });

        try
        {
            var result = await PacsService.Instance.SendSeriesToPacsAsync(server, activeSeries, progress);
            int succ = result.successCount;
            int fail = result.failedCount;
            string msg = result.message;
            TxtTransferStatus.Text = msg;
            MessageBox.Show(msg, "PACS Series Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            TxtTransferStatus.Text = $"C-STORE Error: {ex.Message}";
            MessageBox.Show($"Error: {ex.Message}", "PACS Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PbTransfer.Visibility = Visibility.Collapsed;
        }
    }

    #endregion
}
