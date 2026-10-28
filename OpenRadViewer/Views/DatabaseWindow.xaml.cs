// @purpose: Local DICOM study catalog browser window allowing search, filtering, opening studies, and toggling local "Reviewed" status.
// @depends: OpenRadViewer.Core.Services, OpenRadViewer.ViewModels, OpenRadViewer.Core.Models
// @exports: DatabaseWindow

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;
using OpenRadViewer.ViewModels;

namespace OpenRadViewer.Views;

public partial class DatabaseWindow : Window
{
    private readonly MainViewModel _mainViewModel;
    private List<DatabaseRecordModel> _allRecords = new();
    private List<DatabaseRecordModel> _filteredRecords = new();

    public DatabaseWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        _mainViewModel = mainViewModel;

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

        TxtDbPath.Text = $"Database: {DatabaseService.GetDatabaseFolder()}";
        Loaded += OnWindowLoaded;
        KeyDown += OnWindowKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkWindowHelper.ApplyDarkTitleBar(this);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ReloadDatabase();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    public void ReloadDatabase()
    {
        _allRecords = DatabaseService.LoadRecords();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = TxtSearch.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            _filteredRecords = new List<DatabaseRecordModel>(_allRecords);
        }
        else
        {
            _filteredRecords = _allRecords.Where(r =>
                r.PatientName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.PatientId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.StudyDescription.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.Modality.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.StudyDate.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (r.ReadingPhysician?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.PerformingPhysician?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.ReferringPhysician?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.AccessionNumber?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();
        }

        GridStudies.ItemsSource = _filteredRecords;

        int totalImages = _allRecords.Sum(r => r.TotalImages);
        if (_allRecords.Count == 0)
        {
            TxtDbSummary.Text = "Baza de date este goală.";
        }
        else
        {
            TxtDbSummary.Text = $"{_allRecords.Count} {(_allRecords.Count == 1 ? "studiu" : "studii")} ({totalImages} imagini) în baza de date";
            Task.Run(() =>
            {
                DatabaseService.CleanOrphanDatabaseFolders();
                long bytes = DatabaseService.GetDatabaseTotalSizeBytes();
                string sizeStr = DatabaseService.FormatFileSize(bytes);
                Dispatcher.Invoke(() =>
                {
                    TxtDbSummary.Text = $"{_allRecords.Count} {(_allRecords.Count == 1 ? "studiu" : "studii")} ({totalImages} imagini, {sizeStr}) în baza de date";
                });
            });
        }

        if (_filteredRecords.Count > 0)
        {
            GridStudies.SelectedIndex = 0;
        }
        else
        {
            GridSeries.ItemsSource = null;
        }
    }

    private void OnToggleStudyReviewedClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DatabaseRecordModel record)
        {
            DatabaseService.ToggleReviewed(record);
            GridStudies.Items.Refresh();
            TxtStatus.Text = $"Studiul '{record.PatientName}' marcat ca: {(record.IsReviewed ? "VERIFICAT (✓)" : "NEVERIFICAT")}";
            e.Handled = true;
        }
    }


    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        TxtSearch.Text = string.Empty;
        TxtSearch.Focus();
    }

    private void OnStudySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = GridStudies.SelectedItems.OfType<DatabaseRecordModel>().ToList();
        if (selected.Count > 1)
        {
            int totalImgs = selected.Sum(r => r.TotalImages);
            GridSeries.ItemsSource = null;
            TxtStatus.Text = $"Selectate: {selected.Count} studii ({totalImgs} imagini în total)";
        }
        else if (selected.Count == 1)
        {
            var record = selected[0];
            GridSeries.ItemsSource = record.Series;
            TxtStatus.Text = $"Selectat: {record.PatientName} ({record.Modality}) - {record.StudyDate} - {record.TotalImages} imagini";
        }
        else
        {
            GridSeries.ItemsSource = null;
            TxtStatus.Text = "Ready";
        }
    }

    private void OnOpenInViewer(object sender, RoutedEventArgs e)
    {
        OpenSelectedStudy(appendToCurrentStudy: false);
    }

    private void OnAddStudyToCurrentStudy(object sender, RoutedEventArgs e)
    {
        OpenSelectedStudy(appendToCurrentStudy: true);
    }

    private void OnStudyDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedStudy(appendToCurrentStudy: false);
    }

    private void OnSeriesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedSeries(appendToCurrentStudy: false);
    }

    private void OnOpenSelectedSeriesOnly(object sender, RoutedEventArgs e)
    {
        OpenSelectedSeries(appendToCurrentStudy: false);
    }

    private void OnAddSeriesToCurrentStudy(object sender, RoutedEventArgs e)
    {
        OpenSelectedSeries(appendToCurrentStudy: true);
    }

    private void OnGridStudiesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelectedStudy(appendToCurrentStudy: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            OnToggleReviewed(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            OnDeleteStudy(sender, e);
            e.Handled = true;
        }
    }

    private void OpenSelectedSeries(bool appendToCurrentStudy)
    {
        if (GridStudies.SelectedItem is not DatabaseRecordModel record) return;
        if (!Directory.Exists(record.FolderPath))
        {
            MessageBox.Show(this,
                $"Dosarul de imagini pentru acest studiu nu a fost găsit:\n{record.FolderPath}",
                "Studiu Negăsit",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var selectedSeries = GridSeries.SelectedItems.OfType<DatabaseSeriesRecord>().ToList();
        if (selectedSeries.Count == 0 && GridSeries.SelectedItem is DatabaseSeriesRecord single)
        {
            selectedSeries.Add(single);
        }

        if (selectedSeries.Count == 0)
        {
            OpenSelectedStudy(appendToCurrentStudy);
            return;
        }

        var allowedUids = new HashSet<string>(
            selectedSeries.Where(s => !string.IsNullOrWhiteSpace(s.SeriesInstanceUid)).Select(s => s.SeriesInstanceUid),
            StringComparer.OrdinalIgnoreCase);

        if (!appendToCurrentStudy)
        {
            _mainViewModel.CloseAllPatients();
        }

        _ = _mainViewModel.ScanPathsAsync(
            new[] { record.FolderPath },
            allowedSeriesUids: allowedUids.Count > 0 ? allowedUids : null,
            appendToCurrentStudy: appendToCurrentStudy);
        Close();
    }

    private void OpenSelectedStudy(bool appendToCurrentStudy = false)
    {
        var selected = GridStudies.SelectedItems.OfType<DatabaseRecordModel>().ToList();
        if (selected.Count == 0) return;

        if (selected.Count == 1)
        {
            var record = selected[0];
            if (Directory.Exists(record.FolderPath))
            {
                if (!appendToCurrentStudy)
                {
                    _mainViewModel.CloseAllPatients();
                }
                _ = _mainViewModel.ScanPathsAsync(new[] { record.FolderPath }, appendToCurrentStudy: appendToCurrentStudy);
                Close();
            }
            else
            {
                MessageBox.Show(this, 
                    $"Dosarul de imagini pentru acest studiu nu a fost găsit:\n{record.FolderPath}", 
                    "Studiu Negăsit", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Warning);
            }
        }
        else
        {
            var validFolders = selected.Where(r => !string.IsNullOrWhiteSpace(r.FolderPath) && Directory.Exists(r.FolderPath))
                                       .Select(r => r.FolderPath)
                                       .Distinct()
                                       .ToList();
            if (validFolders.Count > 0)
            {
                if (!appendToCurrentStudy)
                {
                    _mainViewModel.CloseAllPatients();
                }
                _ = _mainViewModel.ScanPathsAsync(validFolders, appendToCurrentStudy: appendToCurrentStudy);
                Close();
            }
            else
            {
                MessageBox.Show(this, 
                    "Niciun dosar valid de imagini nu a fost găsit pentru studiile selectate.", 
                    "Studii Negăsite", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Warning);
            }
        }
    }

    private async void OnImportFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a DICOM folder to import into Parsec Database",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            string folder = dialog.FolderName;
            TxtStatus.Text = $"Se importă studiul din '{Path.GetFileName(folder)}'...";

            try
            {
                var progress = new Progress<(int scanned, int total, string currentFile)>(p =>
                {
                    TxtStatus.Text = $"Importare: {p.scanned}/{p.total} imagini...";
                });

                var imported = await DatabaseService.ImportFromFolderAsync(folder, progress);
                ReloadDatabase();

                if (imported.Count > 0)
                {
                    TxtStatus.Text = $"Import reușit: {imported.Count} studiu(i) adăugate în Parsec Database.";
                    var firstImported = imported.First();
                    var match = _filteredRecords.FirstOrDefault(r => r.StudyInstanceUid == firstImported.StudyInstanceUid);
                    if (match != null) GridStudies.SelectedItem = match;
                }
                else
                {
                    TxtStatus.Text = "Nu s-au găsit fișiere DICOM valide în folderul selectat.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Eroare la importul folderului:\n{ex.Message}", "Eroare Import", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Eroare la import.";
            }
        }
    }

    private void OnImportActiveStudy(object sender, RoutedEventArgs e)
    {
        var activeVp = _mainViewModel.ActiveViewport;
        var activeSeries = activeVp?.OriginalSeries ?? activeVp?.CurrentSeries;

        if (activeSeries == null)
        {
            MessageBox.Show(this, 
                "Nu există niciun studiu deschis în vizualizator în acest moment.", 
                "Import Studiu Activ", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
            return;
        }

        // Find the patient and study
        PatientModel? currentPatient = null;
        StudyModel? currentStudy = null;

        foreach (var p in _mainViewModel.Patients)
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
            // Construct temporary study wrapper if loose series
            currentStudy = new StudyModel
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
            TxtStatus.Text = $"Se salvează studiul '{activeSeries.PatientName}' în Parsec Database...";
            var rec = DatabaseService.ImportStudy(
                currentStudy, 
                currentPatient?.Name ?? activeSeries.PatientName, 
                currentPatient?.Id ?? activeSeries.PatientId, 
                currentPatient?.BirthDate ?? "");

            ReloadDatabase();
            var match = _filteredRecords.FirstOrDefault(r => r.StudyInstanceUid == rec.StudyInstanceUid);
            if (match != null) GridStudies.SelectedItem = match;

            TxtStatus.Text = $"Studiul '{rec.PatientName}' a fost importat cu succes în baza de date.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Eroare la importul studiului curent:\n{ex.Message}", "Eroare Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool _isSendingToPacs;

    private void OnEditNotes(object sender, RoutedEventArgs e)
    {
        if (GridStudies.SelectedItem is not DatabaseRecordModel record) return;
        var dlg = new NotesEditDialog(record.PatientName?.Replace("^", " ") ?? string.Empty, record.PatientComments) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        string text = dlg.NotesText;
        DatabaseService.SetLocalNote(record.StudyInstanceUid, text);
        record.PatientComments = text;
        GridStudies.Items.Refresh();
    }

    private void OnToggleReviewed(object sender, RoutedEventArgs e)
    {
        var selected = GridStudies.SelectedItems.OfType<DatabaseRecordModel>().ToList();
        if (selected.Count == 0) return;

        if (selected.Count == 1)
        {
            var record = selected[0];
            DatabaseService.ToggleReviewed(record);
            GridStudies.Items.Refresh();
            TxtStatus.Text = $"Studiul '{record.PatientName}' marcat ca: {(record.IsReviewed ? "VERIFICAT (✓)" : "NEVERIFICAT")}";
        }
        else
        {
            DatabaseService.ToggleReviewed(selected);
            GridStudies.Items.Refresh();
            bool state = selected[0].IsReviewed;
            TxtStatus.Text = $"{selected.Count} studii marcate ca: {(state ? "VERIFICAT (✓)" : "NEVERIFICAT")}";
        }
    }

    private void OnSendToPacsClick(object sender, RoutedEventArgs e)
    {
        if (_isSendingToPacs)
        {
            MessageBox.Show(this, "Un transfer către PACS este deja în desfășurare.", "Transfer PACS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = GridStudies.SelectedItems.OfType<DatabaseRecordModel>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Selectați una sau mai multe investigații din tabel pentru a le trimite în PACS.", "Send to PACS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var servers = PacsService.Instance.Settings.Servers;
        if (servers == null || servers.Count == 0)
        {
            MessageBox.Show(this, "Nu există niciun server PACS configurat. Adăugați un server în fereastra PACS Settings.", "Send to PACS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (servers.Count == 1)
        {
            _ = SendSelectedStudiesToServerAsync(selected, servers[0]);
            return;
        }

        // Multiple servers configured: show a quick picker menu with the default/last-used PACS server first
        string? defLoc = PacsService.Instance.Settings.DefaultServerLocation;
        bool IsDefaultServer(PacsServerConfig s)
        {
            if (string.IsNullOrWhiteSpace(defLoc)) return false;
            return string.Equals(s.Id, defLoc, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(s.Description, defLoc, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(s.AeTitle, defLoc, StringComparison.OrdinalIgnoreCase);
        }

        var orderedServers = servers
            .OrderByDescending(IsDefaultServer)
            .ToList();

        var menu = new ContextMenu();
        foreach (var srv in orderedServers)
        {
            bool isLast = IsDefaultServer(srv);
            var mi = new MenuItem
            {
                Header = isLast
                    ? $"★ {srv.Description} ({srv.AeTitle} @ {srv.IpAddress}:{srv.Port})"
                    : $"{srv.Description} ({srv.AeTitle} @ {srv.IpAddress}:{srv.Port})",
                FontWeight = isLast ? FontWeights.Bold : FontWeights.Normal
            };
            var capturedServer = srv;
            mi.Click += (_, _) => _ = SendSelectedStudiesToServerAsync(selected, capturedServer);
            menu.Items.Add(mi);
        }

        if (sender == BtnSendToPacs)
        {
            menu.PlacementTarget = BtnSendToPacs;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
        menu.IsOpen = true;
    }

    private async Task SendSelectedStudiesToServerAsync(List<DatabaseRecordModel> selectedStudies, PacsServerConfig server)
    {
        if (_isSendingToPacs) return;

        var allFiles = new List<string>();
        foreach (var rec in selectedStudies)
        {
            if (!string.IsNullOrWhiteSpace(rec.FolderPath) && Directory.Exists(rec.FolderPath))
            {
                try
                {
                    var files = Directory.EnumerateFiles(rec.FolderPath, "*", SearchOption.AllDirectories)
                        .Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    allFiles.AddRange(files);
                }
                catch { }
            }
        }

        allFiles = allFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (allFiles.Count == 0)
        {
            MessageBox.Show(this,
                "Nu s-au găsit fișiere DICOM pe disc pentru investigațiile selectate.",
                "Send to PACS",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string studiesSummary = selectedStudies.Count == 1
            ? $"Pacient: {selectedStudies[0].PatientName}\nDescriere: {selectedStudies[0].StudyDescription}\nFișiere DICOM: {allFiles.Count}"
            : $"Investigații selectate: {selectedStudies.Count} studii\nPacienți: {string.Join(", ", selectedStudies.Select(s => s.PatientName).Distinct().Take(4))}{(selectedStudies.Select(s => s.PatientName).Distinct().Count() > 4 ? "..." : "")}\nTotal fișiere DICOM: {allFiles.Count}";

        var confirm = MessageBox.Show(this,
            $"Doriți să trimiteți {(selectedStudies.Count == 1 ? "investigația selectată" : $"cele {selectedStudies.Count} investigații selectate")} în PACS?\n\n{studiesSummary}\n\nDestinație PACS: {server.Description} ({server.IpAddress}:{server.Port}, AE: {server.AeTitle})",
            "Confirmare Trimitere în PACS (C-STORE)",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _isSendingToPacs = true;
        BtnSendToPacs.IsEnabled = false;
        PbTransfer.Visibility = Visibility.Visible;
        PbTransfer.Maximum = allFiles.Count;
        PbTransfer.Value = 0;
        TxtStatus.Text = $"Se trimit {allFiles.Count} fișiere DICOM către {server.Description}...";

        var progress = new Progress<(int current, int total, string message)>(p =>
        {
            Dispatcher.Invoke(() =>
            {
                PbTransfer.Value = p.current;
                TxtStatus.Text = $"[{server.Description}] {p.message}";
            });
        });

        try
        {
            var result = await PacsService.Instance.SendFilesToPacsAsync(server, allFiles, progress);
            TxtStatus.Text = result.message;
            MessageBox.Show(this,
                result.message,
                "Transfer PACS Finalizat",
                MessageBoxButton.OK,
                result.failedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Eroare C-STORE: {ex.Message}";
            MessageBox.Show(this,
                $"Eroare la trimiterea în PACS:\n{ex.Message}",
                "Eroare Transfer PACS",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isSendingToPacs = false;
            BtnSendToPacs.IsEnabled = true;
            PbTransfer.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDeleteStudy(object sender, RoutedEventArgs e)
    {
        var selected = GridStudies.SelectedItems.OfType<DatabaseRecordModel>().ToList();
        if (selected.Count == 0) return;

        if (selected.Count == 1)
        {
            var record = selected[0];
            var res = MessageBox.Show(this,
                $"Sigur doriți să ștergeți definitiv acest studiu din baza de date?\n\nPacient: {record.PatientName}\nData: {record.StudyDate}\nImagini: {record.TotalImages}",
                "Confirmare Ștergere Studiu",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                DatabaseService.DeleteRecord(record);
                ReloadDatabase();
                TxtStatus.Text = $"Studiul a fost șters.";
            }
        }
        else
        {
            int totalImgs = selected.Sum(r => r.TotalImages);
            var res = MessageBox.Show(this,
                $"Sigur doriți să ștergeți definitiv cele {selected.Count} studii selectate din baza de date?\n\nNumăr total imagini: {totalImgs}",
                "Confirmare Ștergere Multiplă",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                DatabaseService.DeleteRecords(selected);
                ReloadDatabase();
                TxtStatus.Text = $"{selected.Count} studii au fost șterse.";
            }
        }
    }

    private void OnClearAllDatabase(object sender, RoutedEventArgs e)
    {
        var records = DatabaseService.LoadRecords();
        if (records.Count == 0)
        {
            MessageBox.Show(this, 
                "Baza de date locală este deja goală.", 
                "Clear Database", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
            return;
        }

        int totalStudies = records.Count;
        int totalImages = records.Sum(r => r.TotalImages);
        long totalBytes = DatabaseService.GetDatabaseTotalSizeBytes();
        string sizeFormatted = DatabaseService.FormatFileSize(totalBytes);

        var res = MessageBox.Show(this,
            $"Sunteți absolut sigur că doriți să ștergeți COMPLET baza de date locală?\n\n" +
            $"• Toate cele {totalStudies} studii ({totalImages} imagini, ~{sizeFormatted}) vor fi șterse definitiv de pe disc.\n" +
            $"• Toate fișierele și folderele locale asociate vor fi eliminate.\n" +
            $"• Această acțiune este ireversibilă și nu poate fi anulată.\n\n" +
            $"Doriți să continuați ștergerea completă?",
            "Confirmare Ștergere Totală Bază de Date",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res == MessageBoxResult.Yes)
        {
            try
            {
                DatabaseService.ClearAllDatabase();
                ReloadDatabase();
                TxtStatus.Text = "Baza de date locală a fost ștearsă complet.";
                MessageBox.Show(this, 
                    "Baza de date locală a fost curățată complet cu succes.", 
                    "Bază de Date Ștearsă", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, 
                    $"A apărut o problemă la curățarea completă a bazei de date:\n{ex.Message}", 
                    "Eroare Ștergere", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
            }
        }
    }
}
