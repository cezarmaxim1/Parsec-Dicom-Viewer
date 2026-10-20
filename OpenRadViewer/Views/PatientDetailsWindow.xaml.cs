using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public partial class PatientDetailsWindow : Window
{
    private readonly PatientModel? _patient;
    private readonly StudyModel? _study;
    private DispatcherTimer? _noticeTimer;

    public PatientDetailsWindow(PatientModel? patient, StudyModel? study)
    {
        InitializeComponent();
        _patient = patient;
        _study = study;

        // Apply permanent dark title bar
        DarkWindowHelper.ApplyDarkTitleBar(this);

        PopulateDetails();
    }

    private void PopulateDetails()
    {
        if (_patient != null)
        {
            TxtLastName.Text = !string.IsNullOrWhiteSpace(_patient.LastName) ? _patient.LastName : "-";
            TxtFirstName.Text = !string.IsNullOrWhiteSpace(_patient.FirstName) ? _patient.FirstName : "-";
            TxtCnp.Text = !string.IsNullOrWhiteSpace(_patient.Cnp) ? _patient.Cnp : "-";
            
            string rawAge = _patient.RawAgeOnly;
            TxtAge.Text = rawAge != "-" ? $"{rawAge} ani" : "-";

            // Birth date
            if (!string.IsNullOrWhiteSpace(_patient.BirthDate))
            {
                TxtBirthDate.Text = DicomLoaderService.FormatDicomDate(_patient.BirthDate);
            }
            else
            {
                var cnpInfo = PatientInfoHelper.ParseFromCnp(_patient.Id);
                TxtBirthDate.Text = cnpInfo.birthDate != null ? cnpInfo.birthDate.Value.ToString("dd.MM.yyyy") : "-";
            }

            TxtRawName.Text = _patient.Name;
        }

        if (_study != null)
        {
            string dateStr = !string.IsNullOrWhiteSpace(_study.StudyDate) ? _study.StudyDate : "-";
            string timeStr = !string.IsNullOrWhiteSpace(_study.StudyTime) ? PatientInfoHelper.FormatDicomTime(_study.StudyTime) : "";
            TxtStudyDate.Text = string.IsNullOrWhiteSpace(timeStr) ? dateStr : $"{dateStr} ({timeStr})";

            TxtTotalDlp.Text = _study.TotalDlpDisplay;
            TxtScanType.Text = _study.ScanTypeDescription;
            TxtStudyDesc.Text = !string.IsNullOrWhiteSpace(_study.StudyDescription) ? _study.StudyDescription : "Examination";

            string mod = _study.Series.FirstOrDefault()?.Modality ?? "CT";
            int totalSlices = _study.Series.Sum(s => s.Slices.Count);
            TxtModalityAndSeries.Text = $"{mod} • {_study.Series.Count} series ({totalSlices} slices)";
        }
    }

    private void CopyToClipboard(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "-") return;
        try
        {
            Clipboard.SetText(text);
            ShowNotice($"Copied {label}: \"{text}\"");
        }
        catch (Exception ex)
        {
            ShowNotice($"Copy failed: {ex.Message}");
        }
    }

    private void OnCopyLastName(object sender, MouseButtonEventArgs e)
    {
        if (_patient != null && !string.IsNullOrWhiteSpace(_patient.LastName))
            CopyToClipboard(_patient.LastName, "Last Name");
    }

    private void OnCopyFirstName(object sender, MouseButtonEventArgs e)
    {
        if (_patient != null && !string.IsNullOrWhiteSpace(_patient.FirstName))
            CopyToClipboard(_patient.FirstName, "First Name");
    }

    private void OnCopyCnp(object sender, MouseButtonEventArgs e)
    {
        if (_patient != null && !string.IsNullOrWhiteSpace(_patient.Cnp))
            CopyToClipboard(_patient.Cnp, "CNP");
    }

    private void OnCopyAge(object sender, MouseButtonEventArgs e)
    {
        if (_patient != null)
        {
            string age = _patient.RawAgeOnly != "-" ? $"{_patient.RawAgeOnly} ani" : _patient.FormattedAge;
            CopyToClipboard(age, "Age");
        }
    }

    /// <summary>
    /// Copies summary exactly formatted as in Image 3, with 5 tabs between columns:
    /// Nume: [LastName]					CNP: [Cnp]
    /// Prenume: [FirstName]					Vârsta: [Age] ani
    /// </summary>
    private void OnCopySummary(object sender, RoutedEventArgs e)
    {
        string lastName = _patient != null && !string.IsNullOrWhiteSpace(_patient.LastName) ? _patient.LastName : "-";
        string firstName = _patient != null && !string.IsNullOrWhiteSpace(_patient.FirstName) ? _patient.FirstName : "-";
        string cnp = _patient != null && !string.IsNullOrWhiteSpace(_patient.Cnp) ? _patient.Cnp : "-";
        string age = _patient != null && _patient.RawAgeOnly != "-" ? $"{_patient.RawAgeOnly} ani" : "-";

        string summary = $"Nume: {lastName}\t\t\t\t\tCNP: {cnp}\r\nPrenume: {firstName}\t\t\t\t\tVârsta: {age}";
        try
        {
            Clipboard.SetText(summary);
            ShowNotice("Copied Patient Summary (2 lines with 5 tabs) to Clipboard!");
        }
        catch (Exception ex)
        {
            ShowNotice($"Copy failed: {ex.Message}");
        }
    }

    private void OnCopyAllDetails(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== PARSEC - PATIENT & STUDY DETAILS ===");
        if (_patient != null)
        {
            sb.AppendLine($"LAST NAME: {_patient.LastName}");
            sb.AppendLine($"FIRST NAME: {_patient.FirstName}");
            sb.AppendLine($"CNP: {_patient.Cnp}");
            sb.AppendLine($"AGE: {_patient.FormattedAge}");
            if (!string.IsNullOrWhiteSpace(_patient.BirthDate))
                sb.AppendLine($"DATE OF BIRTH: {DicomLoaderService.FormatDicomDate(_patient.BirthDate)}");
        }

        if (_study != null)
        {
            sb.AppendLine($"STUDY DATE: {_study.StudyDate} {_study.StudyTime}");
            sb.AppendLine($"SCAN TYPE: {_study.ScanTypeDescription}");
            sb.AppendLine($"TOTAL DLP: {_study.TotalDlpDisplay}");
            sb.AppendLine($"STUDY DESCRIPTION: {_study.StudyDescription}");
            string mod = _study.Series.FirstOrDefault()?.Modality ?? "CT";
            sb.AppendLine($"MODALITY & SERIES: {mod} ({_study.Series.Count} series, {_study.Series.Sum(s => s.Slices.Count)} slices)");
        }
        sb.AppendLine("=========================================");

        try
        {
            Clipboard.SetText(sb.ToString());
            ShowNotice("Copied all details to Clipboard!");
        }
        catch (Exception ex)
        {
            ShowNotice($"Copy failed: {ex.Message}");
        }
    }

    private void ShowNotice(string message)
    {
        TxtNoticeMessage.Text = message;
        NoticeContainer.Visibility = Visibility.Visible;

        _noticeTimer?.Stop();
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _noticeTimer.Tick += (s, e) =>
        {
            _noticeTimer.Stop();
            NoticeContainer.Visibility = Visibility.Collapsed;
        };
        _noticeTimer.Start();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
