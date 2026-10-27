// @purpose: Core domain hierarchy models: PatientModel -> StudyModel -> SeriesModel, plus modality enums and contrast sequence classifiers.
// @depends: CommunityToolkit.Mvvm, OpenRadViewer.Core.Services
// @exports: MedicalModality, PatientModel, StudyModel, SeriesModel, SeriesTagInfo, SeriesGroupInfo

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Core.Models;

public enum MedicalModality
{
    MR,
    CT,
    CR,
    DX,
    XA,
    US,
    NM,
    PT,
    SC,
    Other
}

public class PatientModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = "Anonymous";
    public string BirthDate { get; set; } = string.Empty;
    public string Sex { get; set; } = string.Empty;
    public string PatientAge { get; set; } = string.Empty;

    public List<StudyModel> Studies { get; set; } = new();

    public string LastName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "-";
            var parts = Name.Contains('^') 
                ? Name.Split('^', StringSplitOptions.RemoveEmptyEntries) 
                : Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0].Trim() : Name;
        }
    }

    public string FirstName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "-";
            var parts = Name.Contains('^') 
                ? Name.Split('^', StringSplitOptions.RemoveEmptyEntries) 
                : Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? string.Join(" ", parts.Skip(1)).Trim() : "-";
        }
    }

    public string Cnp
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Id)) return "-";
            var digits = new string(Id.Where(char.IsDigit).ToArray());
            if (digits.Length == 13) return digits;
            return Id;
        }
    }

    public string RawAgeOnly
    {
        get
        {
            var fromCnp = PatientInfoHelper.ParseFromCnp(Id);
            if (fromCnp.age != null) return $"{fromCnp.age}";

            var fromBirth = PatientInfoHelper.ParseAgeFromDicomDate(BirthDate);
            if (fromBirth != null) return $"{fromBirth}";

            if (!string.IsNullOrWhiteSpace(PatientAge))
            {
                string clean = PatientAge.Trim().TrimEnd('Y', 'y', 'M', 'm', 'D', 'd').TrimStart('0');
                if (!string.IsNullOrEmpty(clean)) return clean;
            }

            return "-";
        }
    }

    public string FormattedAge
    {
        get
        {
            string raw = RawAgeOnly;
            if (raw != "-") return $"{raw} years";
            return "Not specified";
        }
    }

    public override string ToString() => $"{Name} ({Id})";
}

public class StudyModel
{
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string RawStudyDate { get; set; } = string.Empty;
    public string StudyDate { get; set; } = string.Empty;
    public string StudyTime { get; set; } = string.Empty;
    public string StudyDescription { get; set; } = "Examination";
    public string AccessionNumber { get; set; } = string.Empty;
    public string ReadingPhysician { get; set; } = string.Empty;
    public string PerformingPhysician { get; set; } = string.Empty;
    public string ReferringPhysician { get; set; } = string.Empty;
    public string InstitutionName { get; set; } = string.Empty;
    public string ContrastBolusAgent { get; set; } = string.Empty;
    public string ContrastBolusRoute { get; set; } = string.Empty;
    public double TotalDlp { get; set; } = 0.0;
    public string PatientAge { get; set; } = string.Empty;

    public List<SeriesModel> Series { get; set; } = new();

    public string ScanTypeDescription
    {
        get
        {
            bool hasAgent = !string.IsNullOrWhiteSpace(ContrastBolusAgent);
            bool hasContrastSeries = false;
            bool hasNativeSeries = false;

            string[] contrastKeywords = { "C+", "+C", "CONTRAST", "POST", "CE", "ARTERIAL", "VENOS", "PORTAL", "TARDIV", "PARENCHIMATOS", "NEFROGRAFIC", "UROGRAFIC", "ANGIO", "BOLUS", "DELAYED" };
            string[] nativeKeywords = { "NATIV", "FARA CONTRAST", "C-", "-C", "NON-CONTRAST", "PRE-CONTRAST", "PRE CONTRAST", "PLAIN" };

            foreach (var se in Series)
            {
                string desc = se.SeriesDescription ?? "";
                if (IsContrastSeries(desc, se.ContrastBolusAgent))
                {
                    hasContrastSeries = true;
                }

                string dUpper = desc.ToUpperInvariant();
                if (nativeKeywords.Any(k => dUpper.Contains(k)))
                {
                    hasNativeSeries = true;
                }
            }

            if (hasAgent || hasContrastSeries)
            {
                if (hasNativeSeries)
                {
                    return hasAgent 
                        ? $"Contrast-Enhanced IV ({ContrastBolusAgent}) - Multiphasic Protocol (Native + Contrast)"
                        : "Contrast-Enhanced (C+) - Multiphasic Protocol (Native + Contrast Phases)";
                }

                return hasAgent 
                    ? $"Contrast-Enhanced IV ({ContrastBolusAgent})"
                    : "Contrast-Enhanced (C+)";
            }

            if (hasNativeSeries)
            {
                return "Native scan (Non-contrast / C-)";
            }

            string mod = Series.FirstOrDefault()?.Modality ?? "";
            if (mod == "MR") return "Native MRI examination (No contrast agent)";
            if (mod == "CT") return "Native CT examination (No contrast agent)";

            return "Native scan (Non-contrast)";
        }
    }

    public double GetOrResolveTotalDlp()
    {
        // 1. Prioritize explicit Dose Info / Dose Report series if present
        var doseSeries = Series.FirstOrDefault(s =>
            s.SeriesDescription != null &&
            (s.SeriesDescription.Contains("Dose", StringComparison.OrdinalIgnoreCase) ||
             s.SeriesDescription.Contains("Report", StringComparison.OrdinalIgnoreCase) ||
             s.SeriesDescription.Contains("Protocol", StringComparison.OrdinalIgnoreCase)));

        if (doseSeries != null)
        {
            if (doseSeries.TotalDlp <= 0.0 && doseSeries.Slices.Count > 0)
            {
                doseSeries.TotalDlp = OpenRadViewer.Core.Services.DicomLoaderService.ExtractDlpFromFile(doseSeries.Slices[0].FilePath);
            }
            if (doseSeries.TotalDlp > 0.0)
            {
                TotalDlp = doseSeries.TotalDlp;
                return TotalDlp;
            }
        }

        // 2. Lazy fallback: inspect first slice of each CT series and sum distinct acquisition DLPs
        var distinctPhaseDlps = new List<double>();
        foreach (var se in Series)
        {
            if (se.TotalDlp <= 0.0 && se.Slices.Count > 0)
            {
                se.TotalDlp = OpenRadViewer.Core.Services.DicomLoaderService.ExtractDlpFromFile(se.Slices[0].FilePath);
            }
            if (se.TotalDlp > 0.0)
            {
                // Avoid double-counting multiple reconstructions of the same acquisition (which share identical DLP)
                if (!distinctPhaseDlps.Any(existing => Math.Abs(existing - se.TotalDlp) < 0.05))
                {
                    distinctPhaseDlps.Add(se.TotalDlp);
                }
            }
        }

        if (distinctPhaseDlps.Count > 0)
        {
            double summed = distinctPhaseDlps.Sum();
            if (summed > TotalDlp)
            {
                TotalDlp = summed;
            }
        }

        return TotalDlp;
    }

    public string TotalDlpDisplay
    {
        get
        {
            string mod = Series.FirstOrDefault()?.Modality ?? "";
            if (mod == "MR" || mod == "US")
            {
                return "N/A";
            }

            double dlp = GetOrResolveTotalDlp();
            if (dlp > 0.0)
            {
                return $"{dlp:F2} mGycm";
            }

            return "";
        }
    }

    public string DisplayTitle => $"{StudyDate} - {StudyDescription}";

    public static bool IsContrastSeries(string desc, string? agent = null)
    {
        if (!string.IsNullOrWhiteSpace(agent) &&
            !agent.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
            !agent.Equals("NO", StringComparison.OrdinalIgnoreCase) &&
            !agent.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(desc)) return false;

        string dUpper = desc.ToUpperInvariant();

        string[] directContrastWords = {
            "CONTRAST", "POST-CONTRAST", "POST CONTRAST", "POSTCONTRAST",
            "POST-KM", "POST KM", "POSTKM",
            "GADOLINIUM", "GADO", "DOTAREM", "PRIMOVIST", "OMNISCAN", "MAGNEVIST", "PROHANCE",
            "ARTERIAL", "ARTERIELE", "VENOS", "VENOUS", "PORTAL", "PORTAL-VENOUS",
            "TARDIV", "PARENCHIMATOS", "NEFROGRAFIC", "UROGRAFIC", "NEPHROGRAPHIC", "UROGRAPHIC",
            "DELAYED", "EQUILIBRIUM", "WASHOUT", "WASH-OUT", "WASH OUT"
        };

        if (directContrastWords.Any(w => dUpper.Contains(w))) return true;

        if (Regex.IsMatch(dUpper, @"(^|[^A-Z0-9])(\+C|C\+)([^A-Z0-9]|$)")) return true;
        if (Regex.IsMatch(dUpper, @"(^|[^A-Z0-9])CE([^A-Z0-9]|$)")) return true;
        if (Regex.IsMatch(dUpper, @"(^|[^A-Z0-9])GD(\+)?([^A-Z0-9]|$)")) return true;
        if (Regex.IsMatch(dUpper, @"(^|[^A-Z0-9])POST(?!(ERIOR|OP|URE|GRAD))([^A-Z0-9]|$)")) return true;

        return false;
    }
}

public class SeriesModel : ObservableObject
{
    private string _seriesInstanceUid = string.Empty;
    public string SeriesInstanceUid { get => _seriesInstanceUid; set => SetProperty(ref _seriesInstanceUid, value); }

    private string _studyInstanceUid = string.Empty;
    public string StudyInstanceUid { get => _studyInstanceUid; set => SetProperty(ref _studyInstanceUid, value); }

    private string _studyDescription = string.Empty;
    public string StudyDescription { get => _studyDescription; set => SetProperty(ref _studyDescription, value); }

    public string Laterality { get; set; } = string.Empty;

    private int _seriesNumber;
    public int SeriesNumber
    {
        get => _seriesNumber;
        set
        {
            if (SetProperty(ref _seriesNumber, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    private string _seriesDescription = string.Empty;
    public string SeriesDescription
    {
        get => _seriesDescription;
        set
        {
            if (SetProperty(ref _seriesDescription, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(IsScoutOrFewSlices));
                OnPropertyChanged(nameof(CardOpacity));
                OnPropertyChanged(nameof(ThumbnailMaxHeight));
            }
        }
    }

    private string _modality = "MR";
    public string Modality
    {
        get => _modality;
        set
        {
            if (SetProperty(ref _modality, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string BodyPart { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string PatientId { get; set; } = string.Empty;
    public string PatientBirthDate { get; set; } = string.Empty;
    public string PatientAge { get; set; } = string.Empty;
    public string StudyDate { get; set; } = string.Empty;

    private string _seriesTime = string.Empty;
    public string SeriesTime
    {
        get => _seriesTime;
        set
        {
            if (SetProperty(ref _seriesTime, value))
            {
                OnPropertyChanged(nameof(FormattedSeriesTime));
            }
        }
    }

    public string FormattedSeriesTime => PatientInfoHelper.FormatDicomTime(SeriesTime);

    public double CtdiVol { get; set; } = 0.0;
    public double TotalDlp { get; set; } = 0.0;
    public string ContrastBolusAgent { get; set; } = string.Empty;

    public List<DicomSliceModel> Slices { get; set; } = new();

    private System.Windows.Media.Imaging.BitmapSource? _thumbnailImage;
    public System.Windows.Media.Imaging.BitmapSource? ThumbnailImage
    {
        get => _thumbnailImage;
        set
        {
            if (SetProperty(ref _thumbnailImage, value))
            {
                OnPropertyChanged(nameof(ThumbnailStretch));
            }
        }
    }

    public int SliceCount => Slices.Count;

    public bool IsScoutOrFewSlices => 
        (Modality is "CT" or "MR") && SliceCount <= 4 || 
        (!string.IsNullOrEmpty(SeriesDescription) && (SeriesDescription.Contains("scout", StringComparison.OrdinalIgnoreCase) || 
                                                      SeriesDescription.Contains("localizer", StringComparison.OrdinalIgnoreCase) ||
                                                      SeriesDescription.Contains("loc", StringComparison.OrdinalIgnoreCase) ||
                                                      SeriesDescription.Contains("surv", StringComparison.OrdinalIgnoreCase) ||
                                                      SeriesDescription.Contains("topogram", StringComparison.OrdinalIgnoreCase) ||
                                                      SeriesDescription.Contains("scanogram", StringComparison.OrdinalIgnoreCase) ||
                                                      SeriesDescription.Contains("smart prep", StringComparison.OrdinalIgnoreCase) ||
                                                      SeriesDescription.Contains("smartprep", StringComparison.OrdinalIgnoreCase) ||
                                                      SeriesDescription.Contains("bolus", StringComparison.OrdinalIgnoreCase) ||
                                                      SeriesDescription.Contains("monitoring", StringComparison.OrdinalIgnoreCase) ||
                                                      SeriesDescription.Contains("dose report", StringComparison.OrdinalIgnoreCase) ||
                                                      SeriesDescription.Contains("screen save", StringComparison.OrdinalIgnoreCase)));

    public double CardOpacity => IsScoutOrFewSlices ? 0.45 : 1.0;

    /// <summary>
    /// Scouts are capped at 80px height (~2/3 of a normal ~125px series thumbnail box).
    /// </summary>
    public double ThumbnailMaxHeight => IsScoutOrFewSlices ? 80.0 : 130.0;

    /// <summary>
    /// Tall panoramic/whole-spine scouts use UniformToFill so they don't force full-spine preview or shrink to a thin strip.
    /// </summary>
    public System.Windows.Media.Stretch ThumbnailStretch =>
        (_thumbnailImage != null && _thumbnailImage.PixelHeight > _thumbnailImage.PixelWidth * 1.25)
            ? System.Windows.Media.Stretch.UniformToFill
            : System.Windows.Media.Stretch.Uniform;

    public string DisplayTitle => string.IsNullOrWhiteSpace(SeriesDescription) 
        ? $"Series {SeriesNumber} ({Modality})" 
        : $"{SeriesDescription}";

    public string FullToolTip => $"{DisplayTitle}\nStudy: {StudyDescription}\nExam: {StudyDate}" + 
        (!string.IsNullOrWhiteSpace(FormattedSeriesTime) ? $" {FormattedSeriesTime}" : "");

    public void NotifySlicesUpdated()
    {
        OnPropertyChanged(nameof(SliceCount));
        OnPropertyChanged(nameof(IsScoutOrFewSlices));
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(ThumbnailMaxHeight));
    }
}

public class PatientGroupModel : ObservableObject
{
    private string _patientName = string.Empty;
    public string PatientName
    {
        get => _patientName;
        set
        {
            if (SetProperty(ref _patientName, value))
            {
                OnPropertyChanged(nameof(HeaderText));
            }
        }
    }

    private string _patientId = string.Empty;
    public string PatientId
    {
        get => _patientId;
        set
        {
            if (SetProperty(ref _patientId, value))
            {
                OnPropertyChanged(nameof(FormattedIdAndAge));
                OnPropertyChanged(nameof(HeaderText));
            }
        }
    }

    public string RawStudyDate { get; set; } = string.Empty;

    private string _studyDate = string.Empty;
    public string StudyDate
    {
        get => _studyDate;
        set
        {
            if (SetProperty(ref _studyDate, value))
            {
                OnPropertyChanged(nameof(StudySubTitle));
                OnPropertyChanged(nameof(FormattedDateTime));
                OnPropertyChanged(nameof(DisplayExamDate));
                OnPropertyChanged(nameof(HeaderText));
            }
        }
    }

    private string _studyTime = string.Empty;
    public string StudyTime
    {
        get => _studyTime;
        set
        {
            if (SetProperty(ref _studyTime, value))
            {
                OnPropertyChanged(nameof(StudySubTitle));
                OnPropertyChanged(nameof(FormattedTime));
                OnPropertyChanged(nameof(FormattedDateTime));
                OnPropertyChanged(nameof(DisplayExamDate));
                OnPropertyChanged(nameof(HeaderText));
            }
        }
    }

    private string _studyDescription = string.Empty;
    public string StudyDescription
    {
        get => _studyDescription;
        set
        {
            if (SetProperty(ref _studyDescription, value))
            {
                OnPropertyChanged(nameof(StudySubTitle));
                OnPropertyChanged(nameof(HeaderText));
            }
        }
    }

    public string StudyInstanceUid { get; set; } = string.Empty;

    public string FormattedIdAndAge => PatientInfoHelper.FormatIdAndAge(PatientId);

    public string FormattedTime => PatientInfoHelper.FormatDicomTime(StudyTime);

    public string FormattedDateTime
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(StudyDate) && !string.IsNullOrWhiteSpace(FormattedTime))
                return $"{StudyDate}  {FormattedTime}";
            return !string.IsNullOrWhiteSpace(StudyDate) ? StudyDate : FormattedTime;
        }
    }

    public string DisplayExamDate => !string.IsNullOrWhiteSpace(FormattedDateTime) 
        ? $"📅  {FormattedDateTime}" 
        : string.Empty;

    /// <summary>
    /// Deterministic chronological sorting key YYYYMMDD_HHmmss.
    /// </summary>
    public string SortDateTimeKey
    {
        get
        {
            string raw = !string.IsNullOrWhiteSpace(RawStudyDate) ? RawStudyDate : StudyDate;
            string d = new string((raw ?? "").Where(char.IsDigit).ToArray());
            string t = new string((StudyTime ?? "").Where(char.IsDigit).ToArray());
            return $"{d.PadRight(8, '0')}_{t.PadRight(6, '0')}";
        }
    }

    public string StudySubTitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(StudyDescription)) parts.Add(StudyDescription);
            if (!string.IsNullOrWhiteSpace(FormattedDateTime)) parts.Add(FormattedDateTime);
            return string.Join(" • ", parts);
        }
    }

    public string HeaderText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(PatientName))
                parts.Add(PatientName);
            if (!string.IsNullOrWhiteSpace(FormattedIdAndAge))
                parts.Add(FormattedIdAndAge);
            if (!string.IsNullOrWhiteSpace(StudyDescription))
                parts.Add(StudyDescription);
            if (!string.IsNullOrWhiteSpace(FormattedDateTime))
                parts.Add(FormattedDateTime);
            return parts.Count > 0 ? string.Join(" • ", parts) : "Patient";
        }
    }

    public ObservableCollection<SeriesModel> SeriesList { get; set; } = new();
}
