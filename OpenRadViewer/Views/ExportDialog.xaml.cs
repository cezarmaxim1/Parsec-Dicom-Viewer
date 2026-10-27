// @purpose: Image and series batch export dialog supporting current slice, entire series, all open studies, anonymization, and overlay burning.
// @depends: OpenRadViewer.Core.Services (ExportService, PixelRenderer), OpenRadViewer.ViewModels, OpenRadViewer.Core.Models
// @exports: ExportDialog

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;
using OpenRadViewer.ViewModels;

namespace OpenRadViewer.Views;

public partial class ExportDialog : Window
{
    private readonly MainViewModel _mainVm;
    private PatientModel? _patient;
    private StudyModel? _study;
    private SeriesModel? _currentSeries;
    private int _patientSeriesCount = 0;
    private int _patientSlicesCount = 0;

    public ExportDialog(MainViewModel mainVm)
    {
        InitializeComponent();
        _mainVm = mainVm;
        DarkWindowHelper.ApplyDarkTitleBar(this);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeDialogState();
    }

    private void InitializeDialogState()
    {
        var active = _mainVm.ActiveViewport;
        _currentSeries = active?.CurrentSeries;

        if (_currentSeries == null)
            return;

        // Resolve patient and study
        if (_mainVm.Patients != null)
        {
            foreach (var p in _mainVm.Patients)
            {
                foreach (var st in p.Studies)
                {
                    if (st.Series.Any(s => s == _currentSeries || s.SeriesInstanceUid == _currentSeries.SeriesInstanceUid))
                    {
                        _patient = p;
                        _study = st;
                        break;
                    }
                }
                if (_patient != null) break;
            }
        }

        // Calculate counts for entire patient examination
        List<SeriesModel> patientSeriesList = new();
        if (_patient != null)
        {
            patientSeriesList = _patient.Studies
                .SelectMany(st => st.Series)
                .Where(s => s.Slices.Count > 0)
                .Distinct()
                .ToList();
        }
        else if (_mainVm.GroupedPatientStudies != null)
        {
            var grp = _mainVm.GroupedPatientStudies.FirstOrDefault(g =>
                g.SeriesList.Contains(_currentSeries) ||
                (!string.IsNullOrEmpty(_currentSeries.StudyInstanceUid) && g.StudyInstanceUid == _currentSeries.StudyInstanceUid) ||
                (!string.IsNullOrEmpty(_currentSeries.PatientId) && g.PatientId == _currentSeries.PatientId));
            if (grp != null)
            {
                patientSeriesList = grp.SeriesList.Where(s => s.Slices.Count > 0).ToList();
            }
        }

        if (patientSeriesList.Count == 0)
        {
            patientSeriesList.Add(_currentSeries);
        }

        _patientSeriesCount = patientSeriesList.Count;
        _patientSlicesCount = patientSeriesList.Sum(s => s.Slices.Count);

        string patientName = !string.IsNullOrWhiteSpace(_patient?.Name)
            ? _patient.Name
            : (!string.IsNullOrWhiteSpace(_currentSeries.PatientName) ? _currentSeries.PatientName : "Patient");

        string seriesDesc = !string.IsNullOrWhiteSpace(_currentSeries.SeriesDescription)
            ? _currentSeries.SeriesDescription
            : $"Series {_currentSeries.SeriesNumber}";

        int curSliceIdx = active?.CurrentSliceIndex ?? 0;
        int curSeriesSlices = _currentSeries.Slices.Count;

        // Set clear, informative English labels
        RadioAllSeriesPatient.Content = $"Entire patient study ({patientName} - {_patientSeriesCount} series, {_patientSlicesCount} images)";
        RadioEntireSeries.Content = $"Active series only ({seriesDesc} - {curSeriesSlices} images)";
        RadioCurrentSlice.Content = $"Current slice / image only (Slice {curSliceIdx + 1} of {curSeriesSlices})";

        UpdateScopeSummary();
        UpdateFormatDependencies();
    }

    private void OnScopeChanged(object sender, RoutedEventArgs e)
    {
        UpdateScopeSummary();
    }

    private void UpdateScopeSummary()
    {
        if (TextScopeSummary == null) return;

        if (RadioAllSeriesPatient.IsChecked == true)
        {
            TextScopeSummary.Text = $"• All {_patientSeriesCount} series ({_patientSlicesCount} total images) of the patient will be exported into organized folders.";
        }
        else if (RadioEntireSeries.IsChecked == true)
        {
            int slices = _currentSeries?.Slices.Count ?? 0;
            TextScopeSummary.Text = $"• Only the active series ({slices} images) will be exported. Other series in the study will not be included.";
        }
        else if (RadioCurrentSlice.IsChecked == true)
        {
            TextScopeSummary.Text = "• Only 1 single image (the slice currently displayed on screen) will be saved.";
        }
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFormatDependencies();
    }

    private void UpdateFormatDependencies()
    {
        if (CheckBurnOverlays == null || ComboFormat == null) return;

        bool isDicom = ComboFormat.SelectedIndex == 0;
        if (isDicom)
        {
            CheckBurnOverlays.IsEnabled = false;
            CheckBurnOverlays.IsChecked = false;
            if (TextBurnOverlaysNote != null) TextBurnOverlaysNote.Visibility = Visibility.Visible;
            if (CheckAnonymize != null) CheckAnonymize.IsEnabled = true;
        }
        else
        {
            CheckBurnOverlays.IsEnabled = true;
            if (TextBurnOverlaysNote != null) TextBurnOverlaysNote.Visibility = Visibility.Collapsed;
            if (CheckAnonymize != null) CheckAnonymize.IsEnabled = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var active = _mainVm.ActiveViewport;
        if (active?.CurrentSlice == null || active.DisplayImage == null)
        {
            MessageBox.Show(this, "No active DICOM image or series available for export.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var currentSeries = active.CurrentSeries;
        if (currentSeries == null)
        {
            MessageBox.Show(this, "No active series selected.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Determine format: ComboFormat: 0=DICOM, 1=PNG, 2=JPEG, 3=TIFF, 4=BMP
        bool isDicom = ComboFormat.SelectedIndex == 0;
        string imageFormat = ComboFormat.SelectedIndex switch
        {
            2 => "JPEG",
            3 => "TIFF",
            4 => "BMP",
            _ => isDicom ? "DICOM" : "PNG"
        };
        string ext = isDicom ? "dcm" : (imageFormat == "JPEG" ? "jpg" : (imageFormat == "TIFF" ? "tif" : imageFormat.ToLowerInvariant()));

        bool anonymize = CheckAnonymize.IsChecked == true;
        bool burnOverlays = CheckBurnOverlays.IsChecked == true;
        bool organizeFolders = CheckOrganizeFolders.IsChecked == true;

        // 1. SINGLE SLICE EXPORT
        if (RadioCurrentSlice.IsChecked == true)
        {
            string defaultName = isDicom
                ? $"Slice_{active.CurrentSliceIndex + 1:D4}.dcm"
                : $"Image_{active.CurrentSliceIndex + 1:D4}.{ext}";

            var saveDlg = new SaveFileDialog
            {
                Filter = isDicom ? "DICOM File (*.dcm)|*.dcm" : $"{imageFormat} image|*.{ext}",
                FileName = defaultName
            };

            if (saveDlg.ShowDialog(this) == true)
            {
                try
                {
                    if (isDicom)
                    {
                        bool ok = ExportDicomSlice(active.CurrentSlice, saveDlg.FileName, anonymize);
                        if (!ok)
                        {
                            throw new InvalidOperationException("Source DICOM file could not be accessed.");
                        }
                    }
                    else
                    {
                        BitmapSource bmpToExport = active.DisplayImage;
                        if (burnOverlays)
                        {
                            bmpToExport = CreateImageWithOverlays(bmpToExport, active.CurrentSlice, currentSeries, active.WindowCenter, active.WindowWidth);
                        }
                        ExportService.SaveBitmapToFile(bmpToExport, saveDlg.FileName, imageFormat);
                    }

                    MessageBox.Show(this, "Image exported successfully!", "Parsec Export", MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            return;
        }

        // 2. SERIES OR ALL PATIENT SERIES EXPORT
        var folderDlg = new OpenFolderDialog
        {
            Title = RadioAllSeriesPatient.IsChecked == true
                ? "Select destination folder for exporting the entire study"
                : "Select destination folder for exporting the selected series"
        };

        if (folderDlg.ShowDialog(this) != true)
        {
            return;
        }

        string baseDir = folderDlg.FolderName;
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
        {
            return;
        }

        // Resolve patient & study details
        string patientName = !string.IsNullOrWhiteSpace(_patient?.Name)
            ? _patient.Name
            : (!string.IsNullOrWhiteSpace(currentSeries.PatientName) ? currentSeries.PatientName : "Pacient");

        string patientId = !string.IsNullOrWhiteSpace(_patient?.Id)
            ? _patient.Id
            : (!string.IsNullOrWhiteSpace(currentSeries.PatientId) ? currentSeries.PatientId : "");

        string studyDesc = !string.IsNullOrWhiteSpace(_study?.StudyDescription)
            ? _study.StudyDescription
            : (!string.IsNullOrWhiteSpace(currentSeries.StudyDescription) ? currentSeries.StudyDescription : "Examinare");

        string studyDate = !string.IsNullOrWhiteSpace(_study?.StudyDate)
            ? _study.StudyDate
            : (!string.IsNullOrWhiteSpace(currentSeries.StudyDate) ? currentSeries.StudyDate : "");

        // Build series list to export
        List<SeriesModel> seriesToExport = new();

        if (RadioAllSeriesPatient.IsChecked == true)
        {
            if (_patient != null)
            {
                seriesToExport = _patient.Studies
                    .SelectMany(st => st.Series)
                    .Where(s => s.Slices.Count > 0)
                    .Distinct()
                    .ToList();
            }

            if (seriesToExport.Count == 0 && _mainVm.GroupedPatientStudies != null)
            {
                var matchingGroups = _mainVm.GroupedPatientStudies.Where(g =>
                    g.SeriesList.Contains(currentSeries) ||
                    (!string.IsNullOrEmpty(currentSeries.StudyInstanceUid) && g.StudyInstanceUid == currentSeries.StudyInstanceUid) ||
                    (!string.IsNullOrEmpty(currentSeries.PatientId) && g.PatientId == currentSeries.PatientId)).ToList();

                foreach (var g in matchingGroups)
                {
                    foreach (var s in g.SeriesList)
                    {
                        if (s.Slices.Count > 0 && !seriesToExport.Contains(s))
                            seriesToExport.Add(s);
                    }
                }
            }

            if (seriesToExport.Count == 0 && _mainVm.FilteredSeries != null)
            {
                seriesToExport = _mainVm.FilteredSeries
                    .Where(s => string.IsNullOrEmpty(currentSeries.PatientId) || s.PatientId == currentSeries.PatientId)
                    .Where(s => s.Slices.Count > 0)
                    .Distinct()
                    .ToList();
            }

            if (seriesToExport.Count == 0)
            {
                seriesToExport.Add(currentSeries);
            }
        }
        else
        {
            seriesToExport.Add(currentSeries);
        }

        // Folder structure building
        string patientFolderName = SanitizeFolderName(string.IsNullOrEmpty(patientId) ? patientName : $"{patientName}_{patientId}", "Patient");
        string studyFolderName = SanitizeFolderName(string.IsNullOrEmpty(studyDate) ? studyDesc : $"{studyDate}_{studyDesc}", "Study");

        int totalExportedSlices = 0;
        int totalExportedSeries = 0;

        try
        {
            foreach (var series in seriesToExport)
            {
                if (series.Slices.Count == 0) continue;

                string seriesDesc = !string.IsNullOrWhiteSpace(series.SeriesDescription) ? series.SeriesDescription : $"Series_{series.SeriesNumber}";
                string seriesFolderName = SanitizeFolderName($"Series_{series.SeriesNumber:D2}_{seriesDesc}".TrimEnd('_'), $"Series_{series.SeriesNumber:D2}");

                string seriesOutDir;
                if (organizeFolders)
                {
                    seriesOutDir = Path.Combine(baseDir, patientFolderName, studyFolderName, seriesFolderName);
                }
                else
                {
                    seriesOutDir = Path.Combine(baseDir, seriesFolderName);
                }

                Directory.CreateDirectory(seriesOutDir);

                // Determine Window/Level for image rendering
                double wc = series == currentSeries ? active.WindowCenter : (series.Slices[0].DefaultWindowCenter);
                double ww = series == currentSeries ? active.WindowWidth : (series.Slices[0].DefaultWindowWidth);
                bool isNeg = series == currentSeries ? active.IsNegative : false;

                // Check if another viewport currently displays this series to use its adjusted window
                var otherVp = _mainVm.Viewports.FirstOrDefault(v => v.CurrentSeries == series);
                if (otherVp != null)
                {
                    wc = otherVp.WindowCenter;
                    ww = otherVp.WindowWidth;
                    isNeg = otherVp.IsNegative;
                }

                for (int i = 0; i < series.Slices.Count; i++)
                {
                    var slice = series.Slices[i];
                    string fileName = $"Slice_{i + 1:D4}.{ext}";
                    string outFilePath = Path.Combine(seriesOutDir, fileName);

                    if (isDicom)
                    {
                        if (ExportDicomSlice(slice, outFilePath, anonymize))
                        {
                            totalExportedSlices++;
                        }
                    }
                    else
                    {
                        WriteableBitmap? bmp = null;
                        PixelRenderer.RenderToBitmap(slice, wc, ww, isNeg, ref bmp);
                        if (bmp != null)
                        {
                            BitmapSource finalBmp = bmp;
                            if (burnOverlays)
                            {
                                finalBmp = CreateImageWithOverlays(finalBmp, slice, series, wc, ww);
                            }
                            ExportService.SaveBitmapToFile(finalBmp, outFilePath, imageFormat);
                            totalExportedSlices++;
                        }
                    }
                }

                totalExportedSeries++;
            }

            string destinationDisplay = organizeFolders
                ? Path.Combine(baseDir, patientFolderName)
                : baseDir;

            if (totalExportedSlices == 0)
            {
                MessageBox.Show(this,
                    "No images could be exported. Please verify that the source files are accessible on disk.",
                    "Export Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(this,
                $"Export completed successfully!\n\n" +
                $"• Exported series: {totalExportedSeries}\n" +
                $"• Total images: {totalExportedSlices}\n" +
                $"• Format: {(isDicom ? "DICOM (*.dcm)" : imageFormat)}\n\n" +
                $"Destination:\n{destinationDisplay}",
                "Export Complete - Parsec",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error during export: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool ExportDicomSlice(DicomSliceModel slice, string targetPath, bool anonymize)
    {
        if (string.IsNullOrEmpty(slice.FilePath) || !File.Exists(slice.FilePath))
        {
            return false;
        }

        try
        {
            if (anonymize)
            {
                var dcm = FellowOakDicom.DicomFile.Open(slice.FilePath);
                dcm.Dataset.AddOrUpdate(FellowOakDicom.DicomTag.PatientName, "ANONYMOUS");
                dcm.Dataset.AddOrUpdate(FellowOakDicom.DicomTag.PatientID, "ANON_ID");
                dcm.Dataset.AddOrUpdate(FellowOakDicom.DicomTag.PatientBirthDate, "");
                dcm.Dataset.AddOrUpdate(FellowOakDicom.DicomTag.PatientSex, "O");
                dcm.Dataset.AddOrUpdate(FellowOakDicom.DicomTag.PatientAge, "");
                dcm.Dataset.AddOrUpdate(FellowOakDicom.DicomTag.InstitutionName, "ANONYMIZED");
                dcm.Save(targetPath);
                return true;
            }

            File.Copy(slice.FilePath, targetPath, true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ExportDicomSlice failed for {slice.FilePath}: {ex.Message}");
            return false;
        }
    }

    private static BitmapSource CreateImageWithOverlays(BitmapSource baseBmp, DicomSliceModel slice, SeriesModel series, double wc, double ww)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(baseBmp, new Rect(0, 0, baseBmp.PixelWidth, baseBmp.PixelHeight));

            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var brush = new SolidColorBrush(Color.FromArgb(235, 245, 175, 40)); // Radiant yellow
            double fontSize = Math.Max(11, baseBmp.PixelHeight / 36.0);

            // Top-left: Patient info
            string patientText = $"{series.PatientName}\nID: {series.PatientId}";
            var ftTopLeft = new FormattedText(patientText, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, fontSize, brush, 1.0);
            dc.DrawText(ftTopLeft, new Point(12, 12));

            // Top-right: Study / Series info
            string studyText = $"{series.StudyDate}\n{series.Modality} - {series.SeriesDescription}";
            var ftTopRight = new FormattedText(studyText, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, fontSize, brush, 1.0);
            dc.DrawText(ftTopRight, new Point(Math.Max(12, baseBmp.PixelWidth - ftTopRight.Width - 12), 12));

            // Bottom-left: Window / Level
            string wlText = $"WL: {wc:0}  WW: {ww:0}";
            var ftBottomLeft = new FormattedText(wlText, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, fontSize, brush, 1.0);
            dc.DrawText(ftBottomLeft, new Point(12, Math.Max(12, baseBmp.PixelHeight - ftBottomLeft.Height - 12)));

            // Bottom-right: Slice info
            string sliceText = $"Slice: {slice.InstanceNumber} / {series.SliceCount}";
            var ftBottomRight = new FormattedText(sliceText, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, fontSize, brush, 1.0);
            dc.DrawText(ftBottomRight, new Point(Math.Max(12, baseBmp.PixelWidth - ftBottomRight.Width - 12), Math.Max(12, baseBmp.PixelHeight - ftBottomRight.Height - 12)));
        }

        var rtb = new RenderTargetBitmap(baseBmp.PixelWidth, baseBmp.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static string SanitizeFolderName(string name, string fallback = "Folder")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var invalid = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).ToHashSet();
        var sb = new System.Text.StringBuilder();
        foreach (char c in name)
        {
            if (invalid.Contains(c) || c == '^' || c == ':' || c == '/' || c == '\\' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }
        string clean = sb.ToString().Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
    }
}
