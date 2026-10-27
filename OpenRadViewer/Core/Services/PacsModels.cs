// @purpose: Models, configurations, query filters, and C-STORE SCP service implementation for PACS connectivity.
// @depends: FellowOakDicom.Network, OpenRadViewer.Core.Models
// @exports: PacsServerConfig, PacsSettings, PacsStudyResult, PacsSeriesResult, PacsSearchFilter, PacsCStoreScpService

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public class PacsServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Description { get; set; } = "PACS Server";
    public string IpAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 104;
    public string AeTitle { get; set; } = "REMOTE_PACS";
    public string RetrievalMode { get; set; } = "C-MOVE"; // "C-MOVE" or "C-GET"
    public string TransferSyntax { get; set; } = "Explicit VR Little Endian";
    public bool IsEnabled { get; set; } = true;
    public string Status { get; set; } = "Not Tested";
    public long? LastPingMs { get; set; }
    public DateTime? LastChecked { get; set; }
}

public class PacsGlobalSettings
{
    public int ListenerPort { get; set; } = 46001;
    public string LocalAeTitle { get; set; } = "AE_REZIDENTI@@";
    public int TimeoutSeconds { get; set; } = 30;
    public string CacheDirectory { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PacsCache");
    public int CacheRetentionDays { get; set; } = 0; // 0 = On application exit, 1 = 1 day, 3 = 3 days, 7 = 7 days, -1 = Never
    public List<PacsServerConfig> Servers { get; set; } = new();
    public string? DefaultServerLocation { get; set; } = null;
    public string? LastModalityFilter { get; set; } = null;
    public string? LastDatePreset { get; set; } = null;
}

public class PacsStudyResult : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    private bool _isReviewed;
    public bool IsReviewed
    {
        get => _isReviewed;
        set
        {
            if (_isReviewed != value)
            {
                _isReviewed = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isInLocalDatabase;
    public bool IsInLocalDatabase
    {
        get => _isInLocalDatabase;
        set
        {
            if (_isInLocalDatabase != value)
            {
                _isInLocalDatabase = value;
                OnPropertyChanged();
            }
        }
    }

    public string StudyInstanceUid { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string PatientId { get; set; } = string.Empty;
    public string BirthDate { get; set; } = string.Empty;
    public string PatientAge { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    public string StudyDate { get; set; } = string.Empty;
    public string StudyTime { get; set; } = string.Empty;

    public string Age
    {
        get
        {
            var fromCnp = PatientInfoHelper.ParseFromCnp(PatientId);
            if (fromCnp.age != null) return $"{fromCnp.age} ani";

            var fromBirth = PatientInfoHelper.ParseAgeFromDicomDate(BirthDate);
            if (fromBirth != null) return $"{fromBirth} ani";

            var fromAgeTag = PatientInfoHelper.ParseAgeFromDicomAgeString(PatientAge);
            if (fromAgeTag != null) return $"{fromAgeTag} ani";

            return "-";
        }
    }

    private string _modality = string.Empty;
    public string Modality
    {
        get => _modality;
        set { if (_modality != value) { _modality = value; OnPropertyChanged(); } }
    }

    private string _studyDescription = string.Empty;
    public string StudyDescription
    {
        get => _studyDescription;
        set { if (_studyDescription != value) { _studyDescription = value; OnPropertyChanged(); } }
    }

    private string _readingPhysician = string.Empty;
    public string ReadingPhysician
    {
        get => _readingPhysician;
        set { if (_readingPhysician != value) { _readingPhysician = value; OnPropertyChanged(); } }
    }

    private string _performingPhysician = string.Empty;
    public string PerformingPhysician
    {
        get => _performingPhysician;
        set { if (_performingPhysician != value) { _performingPhysician = value; OnPropertyChanged(); } }
    }

    private string _referringPhysician = string.Empty;
    public string ReferringPhysician
    {
        get => _referringPhysician;
        set { if (_referringPhysician != value) { _referringPhysician = value; OnPropertyChanged(); } }
    }

    private int _seriesCount;
    public int SeriesCount
    {
        get => _seriesCount;
        set { if (_seriesCount != value) { _seriesCount = value; OnPropertyChanged(); } }
    }

    private int _instanceCount;
    public int InstanceCount
    {
        get => _instanceCount;
        set { if (_instanceCount != value) { _instanceCount = value; OnPropertyChanged(); } }
    }

    public string ServerDescription { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public List<PacsSeriesResult> Series { get; set; } = new();

    public string DisplayPatientName => string.IsNullOrWhiteSpace(PatientName) ? "Anonymous" : PatientName.Replace("^", " ");
    public string FormattedDate => FormatDicomDate(StudyDate);
    public string FormattedTime => PatientInfoHelper.FormatDicomTime(StudyTime);

    public string FormattedDateTime
    {
        get
        {
            string d = FormattedDate;
            string t = FormattedTime;
            if (!string.IsNullOrWhiteSpace(d) && !string.IsNullOrWhiteSpace(t))
                return $"{d}  {t}";
            if (!string.IsNullOrWhiteSpace(d)) return d;
            return t;
        }
    }

    public string SortDateTimeKey
    {
        get
        {
            string d = new string((StudyDate ?? "").Where(char.IsDigit).ToArray());
            if (d.Length < 8) d = d.PadRight(8, '0');
            else if (d.Length > 8) d = d.Substring(0, 8);

            string t = new string((StudyTime ?? "").Where(char.IsDigit).ToArray());
            if (t.Length < 6) t = t.PadRight(6, '0');
            else if (t.Length > 6) t = t.Substring(0, 6);

            return $"{d}_{t}";
        }
    }

    private static string FormatDicomDate(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return string.Empty;
        string digits = new string(d.Where(char.IsDigit).ToArray());
        if (digits.Length >= 8)
        {
            return $"{digits.Substring(0, 4)}-{digits.Substring(4, 2)}-{digits.Substring(6, 2)}";
        }
        return d;
    }
}

public class PacsSeriesResult
{
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string SeriesNumber { get; set; } = "1";
    public string Modality { get; set; } = string.Empty;
    public string SeriesDescription { get; set; } = string.Empty;
    public int InstanceCount { get; set; }
}

public class PacsSearchFilter
{
    public string? PatientName { get; set; }
    public string? PatientId { get; set; }
    public string? AccessionNumber { get; set; }
    public string? Modality { get; set; }
    public string? StudyDescription { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    public bool Matches(PacsStudyResult study)
    {
        if (study == null) return false;

        // 1. Text filter (PatientName, PatientId, AccessionNumber, StudyDescription, Physicians)
        if (!string.IsNullOrWhiteSpace(PatientName))
        {
            string q = PatientName.Trim();
            bool nameMatch = !string.IsNullOrEmpty(study.PatientName) && study.PatientName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            bool idMatch = !string.IsNullOrEmpty(study.PatientId) && study.PatientId.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            bool accMatch = !string.IsNullOrEmpty(study.AccessionNumber) && study.AccessionNumber.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            bool descMatch = !string.IsNullOrEmpty(study.StudyDescription) && study.StudyDescription.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            bool readMatch = !string.IsNullOrEmpty(study.ReadingPhysician) && study.ReadingPhysician.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            bool perfMatch = !string.IsNullOrEmpty(study.PerformingPhysician) && study.PerformingPhysician.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            bool refMatch = !string.IsNullOrEmpty(study.ReferringPhysician) && study.ReferringPhysician.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!nameMatch && !idMatch && !accMatch && !descMatch && !readMatch && !perfMatch && !refMatch)
            {
                return false;
            }
        }

        // 2. Modality filter
        if (!string.IsNullOrWhiteSpace(Modality) &&
            !Modality.Equals("All", StringComparison.OrdinalIgnoreCase) &&
            !Modality.Equals("Toate", StringComparison.OrdinalIgnoreCase))
        {
            string studyMod = study.Modality ?? string.Empty;
            if (string.IsNullOrWhiteSpace(studyMod))
            {
                return false;
            }

            var targetMods = Modality.Split(new[] { '/', '\\', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var studyMods = studyMod.Split(new[] { '\\', '/', ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            bool matched = false;
            foreach (var tm in targetMods)
            {
                if (studyMods.Any(sm => sm.Equals(tm, StringComparison.OrdinalIgnoreCase) ||
                    ((tm.Equals("CR", StringComparison.OrdinalIgnoreCase) || tm.Equals("DX", StringComparison.OrdinalIgnoreCase)) && sm.Equals("RG", StringComparison.OrdinalIgnoreCase)) ||
                    (tm.Equals("RG", StringComparison.OrdinalIgnoreCase) && (sm.Equals("CR", StringComparison.OrdinalIgnoreCase) || sm.Equals("DX", StringComparison.OrdinalIgnoreCase)))))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        // 3. Date range filter
        if (DateFrom.HasValue || DateTo.HasValue)
        {
            DateTime? studyDt = null;
            if (!string.IsNullOrWhiteSpace(study.StudyDate))
            {
                string cleanDigits = new string(study.StudyDate.Where(char.IsDigit).ToArray());
                if (cleanDigits.Length >= 8)
                {
                    if (DateTime.TryParseExact(cleanDigits.Substring(0, 8), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
                    {
                        studyDt = parsed.Date;
                    }
                }
                if (!studyDt.HasValue && DateTime.TryParse(study.StudyDate, out var dtFallback))
                {
                    studyDt = dtFallback.Date;
                }
            }

            if (!studyDt.HasValue)
            {
                return false;
            }

            if (DateFrom.HasValue && studyDt.Value < DateFrom.Value.Date)
            {
                return false;
            }
            if (DateTo.HasValue && studyDt.Value > DateTo.Value.Date)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// C-STORE & C-ECHO SCP Service to receive DICOM instances pushed by PACS via C-MOVE.
/// </summary>
public class PacsCStoreScpService : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
{
    public static event Action<string, string, string>? OnFileStored; // (studyUid, seriesUid, filePath)
    public static event Action<string, string, string, int>? OnFileStoredWithFrames; // (studyUid, seriesUid, filePath, frameCount)

    public PacsCStoreScpService(INetworkStream stream, Encoding fallbackEncoding, Microsoft.Extensions.Logging.ILogger log, DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            pc.AcceptTransferSyntaxes(
                DicomTransferSyntax.ExplicitVRLittleEndian,
                DicomTransferSyntax.ImplicitVRLittleEndian,
                DicomTransferSyntax.JPEGProcess14SV1,
                DicomTransferSyntax.JPEGProcess14,
                DicomTransferSyntax.JPEGProcess1,
                DicomTransferSyntax.JPEGProcess2_4,
                DicomTransferSyntax.JPEG2000Lossless,
                DicomTransferSyntax.JPEG2000Lossy,
                DicomTransferSyntax.JPEGLSLossless,
                DicomTransferSyntax.JPEGLSNearLossless,
                DicomTransferSyntax.RLELossless,
                DicomTransferSyntax.DeflatedExplicitVRLittleEndian,
                DicomTransferSyntax.ExplicitVRBigEndian);
        }
        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

    public void OnConnectionClosed(Exception exception) { }

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        try
        {
            var dataset = request.Dataset;
            string studyUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "UnknownStudy");
            string seriesUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "UnknownSeries");
            string sopUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, Guid.NewGuid().ToString());
            int numFrames = dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);

            string cacheDir = PacsService.Instance.Settings.CacheDirectory;
            string studyFolder = Path.Combine(cacheDir, Sanitize(studyUid));
            string seriesFolder = Path.Combine(studyFolder, Sanitize(seriesUid));

            Directory.CreateDirectory(seriesFolder);
            string filePath = Path.Combine(seriesFolder, $"{Sanitize(sopUid)}.dcm");

            if (request.File != null)
            {
                await request.File.SaveAsync(filePath);
            }
            else
            {
                var file = new DicomFile(dataset);
                await file.SaveAsync(filePath);
            }

            OnFileStored?.Invoke(studyUid, seriesUid, filePath);
            OnFileStoredWithFrames?.Invoke(studyUid, seriesUid, filePath, numFrames);

            return new DicomCStoreResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[C-STORE SCP Error] {ex.Message}");
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        Debug.WriteLine($"[C-STORE SCP Exception] {e.Message}");
        return Task.CompletedTask;
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    private static string Sanitize(string input)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            input = input.Replace(c, '_');
        }
        return input;
    }
}
