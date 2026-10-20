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
                r.PerformingPhysician.Contains(query, StringComparison.OrdinalIgnoreCase)
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
        OpenSelectedStudy();
    }

    private void OnStudyDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedStudy();
    }

    private void OnSeriesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedStudy();
    }

    private void OnGridStudiesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelectedStudy();
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

    private void OpenSelectedStudy()
    {
        var selected = GridStudies.SelectedItems.OfType<DatabaseRecordModel>().ToList();
        if (selected.Count == 0) return;

        if (selected.Count == 1)
        {
            var record = selected[0];
            if (Directory.Exists(record.FolderPath))
            {
                _ = _mainViewModel.ScanFolderAsync(record.FolderPath);
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
                _ = _mainViewModel.ScanMultipleFoldersAsync(validFolders);
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
