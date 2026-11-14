// @purpose: Local JSON database catalog for indexed DICOM studies, import history, notes, and local "Reviewed" status storage. Other processes may write the index, so every change re-reads it and replaces it atomically.
// @depends: System.Text.Json, OpenRadViewer.Core.Models
// @exports: DatabaseService, DatabaseRecordModel

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FellowOakDicom;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public class DatabaseRecordModel
{
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string PatientName { get; set; } = "Anonymous";
    public string PatientId { get; set; } = string.Empty;
    public string DateOfBirth { get; set; } = string.Empty;
    public string PatientAge { get; set; } = string.Empty;

    public string Age
    {
        get
        {
            var fromCnp = PatientInfoHelper.ParseFromCnp(PatientId);
            if (fromCnp.age != null) return $"{fromCnp.age} ani";

            var fromBirth = PatientInfoHelper.ParseAgeFromDicomDate(DateOfBirth);
            if (fromBirth != null) return $"{fromBirth} ani";

            var fromAgeTag = PatientInfoHelper.ParseAgeFromDicomAgeString(PatientAge);
            if (fromAgeTag != null) return $"{fromAgeTag} ani";

            return "-";
        }
    }
    public string Modality { get; set; } = "CT";
    public string StudyDate { get; set; } = string.Empty;
    public string StudyTime { get; set; } = string.Empty;
    [JsonIgnore]
    public string FormattedTime => PatientInfoHelper.FormatDicomTime(StudyTime);
    /// <summary>False on records imported before times were stored; see <see cref="DatabaseService.BackfillTimes"/>.</summary>
    public bool TimesBackfilled { get; set; }
    public string StudyDescription { get; set; } = string.Empty;
    public string ReadingPhysician { get; set; } = string.Empty;
    public string PerformingPhysician { get; set; } = string.Empty;
    public string ReferringPhysician { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    /// <summary>Clinical notes synced from the PACS server (ParsecPACS patient_comments).</summary>
    public string PatientComments { get; set; } = string.Empty;
    /// <summary>Time of the last note edit (ISO 8601 UTC), shared with ParsecPACS. The newest edit wins.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PatientCommentsUpdatedAt { get; set; }
    public bool MetadataBackfilled { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public bool IsReviewed { get; set; }
    public DateTime ImportDate { get; set; } = DateTime.Now;
    public int SeriesCount { get; set; }
    public int TotalImages { get; set; }
    /// <summary>Size of the study folder on disk. 0 on records imported before sizes were stored; see <see cref="DatabaseService.BackfillSizes"/>.</summary>
    public long SizeBytes { get; set; }
    [JsonIgnore]
    public string FormattedSize => SizeBytes > 0 ? DatabaseService.FormatFileSize(SizeBytes) : string.Empty;
    public List<DatabaseSeriesRecord> Series { get; set; } = new();
    /// <summary>Fields written by other applications, kept when the record is saved.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

/// <summary>Outcome of <see cref="DatabaseService.DeleteRecords"/>.</summary>
public class DeleteResult
{
    /// <summary>False when database_index.json could not be read or written; nothing was deleted.</summary>
    public bool IndexUpdated { get; set; }
    public int RemovedCount { get; set; }
    /// <summary>Folders that are no longer listed but could not be deleted, usually because a file is in use.</summary>
    public List<string> FoldersLeft { get; } = new();
}

public class DatabaseSeriesRecord
{
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public int SeriesNumber { get; set; }
    public string Modality { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Time { get; set; } = string.Empty;
    [JsonIgnore]
    public string FormattedTime => PatientInfoHelper.FormatDicomTime(Time);
    public int ImageCount { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

public class DatabaseService
{
    private static readonly string DbDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ParsecDatabase");
    private static readonly string IndexFile = Path.Combine(DbDirectory, "database_index.json");
    private static readonly object _fileLock = new();
    private const int IndexFileAttempts = 10;
    private const int IndexFileRetryDelayMs = 100;
    private const int IndexChangeDebounceMs = 400;
    private static readonly object _watcherLock = new();
    private static FileSystemWatcher? _indexWatcher;
    private static Timer? _indexDebounce;
    private static Action? _indexFileChanged;

    public static event Action<string, bool>? OnStudyReviewedChanged;

    static DatabaseService()
    {
        OnStudyReviewedChanged += (studyUid, isReviewed) =>
        {
            try
            {
                var hosts = ParsecPacsSyncService.GetCandidateHosts(PacsService.Instance.Settings.Servers);
                ParsecPacsSyncService.PushReviewedStateInBackground(studyUid, isReviewed, hosts);
            }
            catch { }
        };
    }

    public static string GetDatabaseFolder()
    {
        if (!Directory.Exists(DbDirectory))
        {
            Directory.CreateDirectory(DbDirectory);
        }
        return DbDirectory;
    }

    public static List<DatabaseRecordModel> LoadRecords()
    {
        lock (_fileLock)
        {
            GetDatabaseFolder();
            if (!TryReadRecords(out var records)) return new List<DatabaseRecordModel>();
            try
            {
                bool anyUpdated = false;
                foreach (var rec in records)
                {
                    if (!rec.MetadataBackfilled && !string.IsNullOrWhiteSpace(rec.FolderPath) && Directory.Exists(rec.FolderPath))
                    {
                        try
                        {
                            string? firstFile = Directory.EnumerateFiles(rec.FolderPath, "*", SearchOption.AllDirectories).FirstOrDefault();
                            if (!string.IsNullOrEmpty(firstFile))
                            {
                                var dcm = DicomFile.Open(firstFile, FileReadOption.SkipLargeTags);
                                var ds = dcm.Dataset;
                                string reading = DicomLoaderService.CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.NameOfPhysiciansReadingStudy, ""));
                                string performing = DicomLoaderService.CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.PerformingPhysicianName, ""));
                                if (string.IsNullOrWhiteSpace(performing))
                                    performing = DicomLoaderService.CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.OperatorsName, ""));
                                string referring = DicomLoaderService.CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.ReferringPhysicianName, ""));
                                string accession = DicomLoaderService.CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, ""));

                                if (!string.IsNullOrWhiteSpace(reading)) rec.ReadingPhysician = reading;
                                if (!string.IsNullOrWhiteSpace(performing)) rec.PerformingPhysician = performing;
                                if (!string.IsNullOrWhiteSpace(referring)) rec.ReferringPhysician = referring;
                                if (!string.IsNullOrWhiteSpace(accession)) rec.AccessionNumber = accession;
                            }
                        }
                        catch { }
                        rec.MetadataBackfilled = true;
                        anyUpdated = true;
                    }
                }

                if (anyUpdated) WriteRecords(records);

                return records;
            }
            catch
            {
                return new List<DatabaseRecordModel>();
            }
        }
    }

    /// <summary>
    /// Reads study and series times from the stored DICOM files of records imported before times were saved.
    /// Reads files, so call it off the UI thread. Returns true if any record was updated.
    /// </summary>
    public static bool BackfillTimes()
    {
        var pending = LoadRecords()
            .Where(r => !r.TimesBackfilled && !string.IsNullOrWhiteSpace(r.FolderPath) && Directory.Exists(r.FolderPath))
            .ToList();
        if (pending.Count == 0) return false;

        foreach (var rec in pending) ReadTimesFromFiles(rec);

        UpdateRecords(all =>
        {
            foreach (var rec in pending)
            {
                var current = all.FirstOrDefault(r => r.StudyInstanceUid == rec.StudyInstanceUid);
                if (current == null || current.TimesBackfilled) continue;

                current.StudyTime = rec.StudyTime;
                foreach (var series in current.Series)
                {
                    var source = rec.Series.FirstOrDefault(s => s.SeriesInstanceUid == series.SeriesInstanceUid);
                    if (source != null) series.Time = source.Time;
                }
                current.TimesBackfilled = true;
            }
            return true;
        });
        return true;
    }

    private static void ReadTimesFromFiles(DatabaseRecordModel rec)
    {
        foreach (string dir in Directory.EnumerateDirectories(rec.FolderPath))
        {
            try
            {
                string? file = Directory.EnumerateFiles(dir).FirstOrDefault();
                if (file == null) continue;

                var ds = DicomFile.Open(file, FileReadOption.SkipLargeTags).Dataset;
                if (string.IsNullOrWhiteSpace(rec.StudyTime))
                    rec.StudyTime = ds.GetSingleValueOrDefault(DicomTag.StudyTime, "");

                string time = ds.GetSingleValueOrDefault(DicomTag.SeriesTime, "");
                if (string.IsNullOrWhiteSpace(time))
                    time = ds.GetSingleValueOrDefault(DicomTag.AcquisitionTime, "");

                string uid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "");
                string folderName = Path.GetFileName(dir);
                var series = rec.Series.FirstOrDefault(s => s.SeriesInstanceUid == uid)
                    ?? rec.Series.FirstOrDefault(s => string.IsNullOrEmpty(s.Time) && folderName == $"Series_{s.SeriesNumber}");
                if (series != null) series.Time = time;
            }
            catch { }
        }
    }

    public static void SaveRecords(List<DatabaseRecordModel> records)
    {
        lock (_fileLock)
        {
            WriteRecords(records);
        }
    }

    /// <summary>
    /// Re-reads the index, applies a change, and saves it, so values written by ParsecPACS are kept.
    /// Nothing is written if the file cannot be read or the change returns false. Returns false only on a read or write failure.
    /// </summary>
    private static bool UpdateRecords(Func<List<DatabaseRecordModel>, bool> change)
    {
        lock (_fileLock)
        {
            GetDatabaseFolder();
            if (!TryReadRecords(out var records)) return false;
            return !change(records) || WriteRecords(records);
        }
    }

    private static bool TryReadRecords(out List<DatabaseRecordModel> records)
    {
        records = new List<DatabaseRecordModel>();
        if (!File.Exists(IndexFile)) return true;

        for (int attempt = 0; attempt < IndexFileAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(IndexFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                records = JsonSerializer.Deserialize<List<DatabaseRecordModel>>(reader.ReadToEnd()) ?? new List<DatabaseRecordModel>();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Thread.Sleep(IndexFileRetryDelayMs);
            }
        }
        return false;
    }

    private static bool WriteRecords(List<DatabaseRecordModel> records)
    {
        string tempFile = $"{IndexFile}.{Environment.ProcessId}.tmp";
        try
        {
            GetDatabaseFolder();
            File.WriteAllText(tempFile, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            for (int attempt = 0; attempt < IndexFileAttempts; attempt++)
            {
                try
                {
                    File.Move(tempFile, IndexFile, true);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(IndexFileRetryDelayMs);
                }
            }
        }
        catch { }

        try { File.Delete(tempFile); } catch { }
        return false;
    }

    /// <summary>Raised on a background thread after database_index.json changes on disk.</summary>
    public static event Action? IndexFileChanged
    {
        add
        {
            lock (_watcherLock)
            {
                _indexFileChanged += value;
                if (_indexWatcher != null) return;

                GetDatabaseFolder();
                _indexDebounce = new Timer(_ => _indexFileChanged?.Invoke());
                _indexWatcher = new FileSystemWatcher(DbDirectory, Path.GetFileName(IndexFile))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                _indexWatcher.Changed += OnIndexFileEvent;
                _indexWatcher.Created += OnIndexFileEvent;
                _indexWatcher.Renamed += OnIndexFileEvent;
                _indexWatcher.EnableRaisingEvents = true;
            }
        }
        remove
        {
            lock (_watcherLock) _indexFileChanged -= value;
        }
    }

    private static void OnIndexFileEvent(object sender, FileSystemEventArgs e) => _indexDebounce?.Change(IndexChangeDebounceMs, Timeout.Infinite);

    public static DatabaseRecordModel ImportStudy(StudyModel study, string? patientName, string? patientId, string? birthDate)
    {
        string dbFolder = GetDatabaseFolder();
        string studySafeName = SanitizeFileName(string.IsNullOrWhiteSpace(patientName) ? "Patient" : patientName);
        string dateSafe = SanitizeFileName(study.StudyDate);
        string folderName = $"{dateSafe}_{studySafeName}_{study.StudyInstanceUid.GetHashCode():X8}";
        string targetDir = Path.Combine(dbFolder, folderName);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // Copy files
        int totalCopied = 0;
        var seriesRecords = new List<DatabaseSeriesRecord>();

        foreach (var series in study.Series)
        {
            string sDir = Path.Combine(targetDir, $"Series_{series.SeriesNumber}");
            if (!Directory.Exists(sDir)) Directory.CreateDirectory(sDir);

            // Group slices by unique FilePath so multi-frame files are copied ONCE rather than 160 times!
            var distinctFiles = series.Slices
                .Select(s => s.FilePath)
                .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            int fileIdx = 0;
            foreach (var srcPath in distinctFiles)
            {
                string ext = Path.GetExtension(srcPath);
                if (string.IsNullOrEmpty(ext)) ext = ".dcm";
                string dst = Path.Combine(sDir, $"img_{fileIdx + 1}{ext}");
                try
                {
                    File.Copy(srcPath, dst, true);
                    fileIdx++;
                }
                catch { }
            }

            int seriesTotalImages = series.Slices.Count;
            totalCopied += seriesTotalImages;

            seriesRecords.Add(new DatabaseSeriesRecord
            {
                SeriesInstanceUid = series.SeriesInstanceUid,
                SeriesNumber = series.SeriesNumber,
                Modality = series.Modality,
                Description = series.SeriesDescription,
                Date = series.StudyDate,
                Time = series.SeriesTime,
                ImageCount = seriesTotalImages
            });
        }

        var record = new DatabaseRecordModel
        {
            StudyInstanceUid = study.StudyInstanceUid,
            PatientName = patientName ?? "Anonymous",
            PatientId = patientId ?? "",
            DateOfBirth = birthDate ?? "",
            Modality = study.Series.FirstOrDefault()?.Modality ?? "CT",
            StudyDate = study.StudyDate,
            StudyTime = study.StudyTime,
            TimesBackfilled = true,
            StudyDescription = study.StudyDescription,
            ReadingPhysician = study.ReadingPhysician ?? "",
            PerformingPhysician = !string.IsNullOrWhiteSpace(study.PerformingPhysician) ? study.PerformingPhysician : (study.InstitutionName ?? ""),
            ReferringPhysician = study.ReferringPhysician ?? "",
            AccessionNumber = study.AccessionNumber ?? "",
            MetadataBackfilled = true,
            FolderPath = targetDir,
            IsReviewed = false,
            ImportDate = DateTime.Now,
            SeriesCount = study.Series.Count,
            TotalImages = totalCopied,
            SizeBytes = GetFolderSizeBytes(targetDir),
            Series = seriesRecords
        };

        string? userNote = GetLocalNote(record.StudyInstanceUid);
        UpdateRecords(all =>
        {
            var previous = all.FirstOrDefault(r => r.StudyInstanceUid == record.StudyInstanceUid);
            if (previous != null)
            {
                record.PatientComments = previous.PatientComments;
                record.PatientCommentsUpdatedAt = previous.PatientCommentsUpdatedAt;
                record.ExtraFields = previous.ExtraFields;
            }
            if (userNote != null) record.PatientComments = userNote;
            all.RemoveAll(r => r.StudyInstanceUid == record.StudyInstanceUid);
            all.Insert(0, record);
            return true;
        });

        return record;
    }

    /// <summary>
    /// Stores a note received through C-FIND on a record that has no edit time. C-FIND carries no time,
    /// so it cannot replace a timed note; those are compared by <see cref="ParsecPacsSyncService"/>.
    /// Returns true if the record was updated.
    /// </summary>
    public static bool UpdatePatientComments(string studyUid, string? comments)
    {
        if (string.IsNullOrWhiteSpace(studyUid)) return false;
        string? localOverride = GetLocalNote(studyUid);
        if (localOverride != null) comments = localOverride;
        string value = comments?.Trim() ?? string.Empty;

        bool updated = false;
        UpdateRecords(all =>
        {
            var rec = FindRecord(all, studyUid);
            if (rec == null || !string.IsNullOrEmpty(rec.PatientCommentsUpdatedAt)) return false;
            if (string.Equals(rec.PatientComments ?? string.Empty, value, StringComparison.Ordinal)) return false;
            rec.PatientComments = value;
            updated = true;
            return true;
        });
        return updated;
    }

    /// <summary>Saves a note the user changed on a local record, stamped with the current UTC time. Returns false if the index could not be updated.</summary>
    public static bool ApplyUserNote(string studyUid, string text)
    {
        return UpdateRecords(all =>
        {
            var rec = FindRecord(all, studyUid);
            if (rec == null) return false;
            rec.PatientComments = text;
            rec.PatientCommentsUpdatedAt = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        });
    }

    /// <summary>Stores a note and its edit time as reported by ParsecPACS. Does not stamp the current time.</summary>
    public static bool ApplyServerNote(string studyUid, string? text, string? updatedAt)
    {
        return UpdateRecords(all =>
        {
            var rec = FindRecord(all, studyUid);
            if (rec == null) return false;
            rec.PatientComments = text ?? string.Empty;
            if (updatedAt != null) rec.PatientCommentsUpdatedAt = updatedAt;
            return true;
        });
    }

    private static DatabaseRecordModel? FindRecord(List<DatabaseRecordModel> records, string studyUid)
    {
        string cleanUid = studyUid.Trim().TrimEnd('\0');
        return records.FirstOrDefault(r => string.Equals(r.StudyInstanceUid?.Trim().TrimEnd('\0'), cleanUid, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- Notes kept only on this computer: studies without a local record whose note is not in PACS ----------------
    private static readonly string LocalNotesFile = Path.Combine(DbDirectory, "local_notes.json");
    private static readonly object _notesLock = new();
    private static Dictionary<string, string>? _localNotes;

    private static Dictionary<string, string> EnsureLocalNotesLoaded()
    {
        if (_localNotes != null) return _localNotes;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(LocalNotesFile))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LocalNotesFile));
                if (loaded != null)
                    foreach (var kv in loaded) dict[kv.Key] = kv.Value ?? string.Empty;
            }
        }
        catch { }
        _localNotes = dict;
        return dict;
    }

    /// <summary>Returns the note kept only on this computer for a study, or null if there is none.</summary>
    public static string? GetLocalNote(string studyUid)
    {
        if (string.IsNullOrWhiteSpace(studyUid)) return null;
        lock (_notesLock)
        {
            return EnsureLocalNotesLoaded().TryGetValue(studyUid.Trim().TrimEnd('\0'), out var v) ? v : null;
        }
    }

    /// <summary>Stores a note kept only on this computer. An empty text is a deletion PACS has not received; null removes the entry.</summary>
    public static void SetLocalNote(string studyUid, string? text)
    {
        if (string.IsNullOrWhiteSpace(studyUid)) return;
        string key = studyUid.Trim().TrimEnd('\0');
        lock (_notesLock)
        {
            var dict = EnsureLocalNotesLoaded();
            if (text == null && !dict.ContainsKey(key)) return;
            if (text == null) dict.Remove(key);
            else dict[key] = text;
            try
            {
                GetDatabaseFolder();
                File.WriteAllText(LocalNotesFile, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    public static DeleteResult DeleteRecord(DatabaseRecordModel record)
    {
        return DeleteRecords(new[] { record });
    }

    /// <summary>
    /// Removes records from the index, then deletes their folders. Reads and deletes files, so call it off the UI thread.
    /// If the index cannot be updated nothing is deleted, so a study never loses its files while still listed.
    /// </summary>
    public static DeleteResult DeleteRecords(IEnumerable<DatabaseRecordModel> records)
    {
        var recordList = records.ToList();
        var result = new DeleteResult();
        if (recordList.Count == 0) return result;

        var uids = new HashSet<string>(recordList.Select(r => r.StudyInstanceUid), StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        result.IndexUpdated = UpdateRecords(all =>
        {
            removed = all.RemoveAll(r => uids.Contains(r.StudyInstanceUid));
            return removed > 0;
        });
        if (!result.IndexUpdated) return result;
        result.RemovedCount = removed;

        foreach (var r in recordList)
        {
            if (string.IsNullOrEmpty(r.FolderPath) || !Directory.Exists(r.FolderPath)) continue;
            ForceDeleteDirectory(r.FolderPath);
            if (Directory.Exists(r.FolderPath)) result.FoldersLeft.Add(r.FolderPath);
        }

        CleanOrphanDatabaseFolders();
        return result;
    }

    public static void ClearAllDatabase()
    {
        lock (_fileLock)
        {
            string dbFolder = GetDatabaseFolder();
            SaveRecords(new List<DatabaseRecordModel>());

            if (Directory.Exists(dbFolder))
            {
                foreach (var dir in Directory.GetDirectories(dbFolder))
                {
                    ForceDeleteDirectory(dir);
                }
            }

            try
            {
                _reviewedPacsCache.Clear();
                if (File.Exists(ReviewedPacsFile)) File.Delete(ReviewedPacsFile);
                if (File.Exists(LegacyReviewedPacsFile)) File.Delete(LegacyReviewedPacsFile);
            }
            catch { }
        }
    }

    public static void ForceDeleteDirectory(string dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch { }
            }
            foreach (var sub in Directory.EnumerateDirectories(dirPath, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try { Directory.Delete(sub, false); } catch { }
            }
            Directory.Delete(dirPath, true);
        }
        catch
        {
            try
            {
                Thread.Sleep(50);
                if (Directory.Exists(dirPath)) Directory.Delete(dirPath, true);
            }
            catch { }
        }
    }


    public static async Task<List<DatabaseRecordModel>> ImportFromFolderAsync(string folderPath, IProgress<(int scanned, int total, string currentFile)>? progress = null)
    {
        var loader = new DicomLoaderService();
        var patients = await loader.ScanFolderAsync(folderPath, progress);
        var imported = new List<DatabaseRecordModel>();

        foreach (var p in patients)
        {
            foreach (var st in p.Studies)
            {
                var rec = ImportStudy(st, p.Name, p.Id, p.BirthDate);
                imported.Add(rec);
            }
        }

        return imported;
    }

    public static void ToggleReviewed(DatabaseRecordModel record)
    {
        ToggleReviewed(new[] { record });
    }

    public static void ToggleReviewed(IEnumerable<DatabaseRecordModel> records)
    {
        var recordList = records.ToList();
        if (recordList.Count == 0) return;

        // If any record is NOT reviewed, mark all as reviewed. If all are reviewed, mark all as unreviewed.
        bool targetState = recordList.Any(r => !r.IsReviewed);

        UpdateRecords(all =>
        {
            var allDict = all.ToDictionary(r => r.StudyInstanceUid);
            foreach (var r in recordList)
            {
                r.IsReviewed = targetState;
                if (allDict.TryGetValue(r.StudyInstanceUid, out var item))
                {
                    item.IsReviewed = targetState;
                }
            }
            return true;
        });

        EnsureReviewedPacsLoaded();
        lock (_fileLock)
        {
            foreach (var r in recordList)
            {
                if (targetState) _reviewedPacsCache.Add(r.StudyInstanceUid);
                else _reviewedPacsCache.Remove(r.StudyInstanceUid);
            }
            SaveReviewedPacs();
        }

        foreach (var r in recordList)
        {
            OnStudyReviewedChanged?.Invoke(r.StudyInstanceUid, targetState);
        }
    }

    private static readonly string AppDataParsecDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parsec");
    private static readonly string ReviewedPacsFile = Path.Combine(AppDataParsecDir, "reviewed_pacs.json");
    private static readonly string LegacyReviewedPacsFile = Path.Combine(DbDirectory, "reviewed_pacs.json");
    private static readonly HashSet<string> _reviewedPacsCache = new(StringComparer.OrdinalIgnoreCase);
    private static bool _reviewedPacsLoaded = false;

    private static void EnsureReviewedPacsLoaded()
    {
        if (_reviewedPacsLoaded) return;
        lock (_fileLock)
        {
            if (_reviewedPacsLoaded) return;
            bool migrated = false;
            try
            {
                if (File.Exists(ReviewedPacsFile))
                {
                    string json = File.ReadAllText(ReviewedPacsFile);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null)
                    {
                        foreach (var uid in list)
                        {
                            if (!string.IsNullOrWhiteSpace(uid)) _reviewedPacsCache.Add(uid.Trim().TrimEnd('\0'));
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (File.Exists(LegacyReviewedPacsFile))
                {
                    string json = File.ReadAllText(LegacyReviewedPacsFile);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null)
                    {
                        foreach (var uid in list)
                        {
                            if (!string.IsNullOrWhiteSpace(uid) && _reviewedPacsCache.Add(uid.Trim().TrimEnd('\0')))
                            {
                                migrated = true;
                            }
                        }
                    }
                }
            }
            catch { }

            _reviewedPacsLoaded = true;
            if (migrated)
            {
                SaveReviewedPacs();
            }
        }
    }

    private static void SaveReviewedPacs()
    {
        lock (_fileLock)
        {
            string json = JsonSerializer.Serialize(_reviewedPacsCache.ToList(), new JsonSerializerOptions { WriteIndented = true });
            try
            {
                Directory.CreateDirectory(AppDataParsecDir);
                File.WriteAllText(ReviewedPacsFile, json);
            }
            catch { }

            try
            {
                GetDatabaseFolder();
                File.WriteAllText(LegacyReviewedPacsFile, json);
            }
            catch { }
        }
    }

    public static DatabaseRecordModel? GetRecord(string studyInstanceUid)
    {
        if (string.IsNullOrWhiteSpace(studyInstanceUid)) return null;
        string cleanUid = studyInstanceUid.Trim().TrimEnd('\0');
        lock (_fileLock)
        {
            var records = LoadRecords();
            return records.FirstOrDefault(r => string.Equals(r.StudyInstanceUid?.Trim().TrimEnd('\0'), cleanUid, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static bool IsStudyReviewed(string studyInstanceUid)
    {
        if (string.IsNullOrWhiteSpace(studyInstanceUid)) return false;
        string cleanUid = studyInstanceUid.Trim().TrimEnd('\0');
        EnsureReviewedPacsLoaded();

        lock (_fileLock)
        {
            if (_reviewedPacsCache.Contains(cleanUid)) return true;
            var records = LoadRecords();
            var match = records.FirstOrDefault(r => string.Equals(r.StudyInstanceUid?.Trim().TrimEnd('\0'), cleanUid, StringComparison.OrdinalIgnoreCase));
            if (match != null && match.IsReviewed)
            {
                _reviewedPacsCache.Add(cleanUid);
                SaveReviewedPacs();
                return true;
            }
        }
        return false;
    }

    public static bool ToggleStudyReviewed(string studyInstanceUid)
    {
        if (string.IsNullOrWhiteSpace(studyInstanceUid)) return false;
        string cleanUid = studyInstanceUid.Trim().TrimEnd('\0');
        EnsureReviewedPacsLoaded();

        bool newState;
        lock (_fileLock)
        {
            if (_reviewedPacsCache.Contains(cleanUid))
            {
                _reviewedPacsCache.Remove(cleanUid);
                newState = false;
            }
            else
            {
                _reviewedPacsCache.Add(cleanUid);
                newState = true;
            }
            SaveReviewedPacs();

            UpdateRecords(records =>
            {
                var match = FindRecord(records, cleanUid);
                if (match == null) return false;
                match.IsReviewed = newState;
                return true;
            });
        }
        OnStudyReviewedChanged?.Invoke(cleanUid, newState);
        return newState;
    }

    /// <summary>
    /// Sets the reviewed state explicitly (no toggle). Returns true if the stored state changed.
    /// </summary>
    public static bool SetStudyReviewed(string studyInstanceUid, bool isReviewed)
    {
        if (string.IsNullOrWhiteSpace(studyInstanceUid)) return false;
        string cleanUid = studyInstanceUid.Trim().TrimEnd('\0');
        EnsureReviewedPacsLoaded();

        lock (_fileLock)
        {
            bool current = _reviewedPacsCache.Contains(cleanUid);
            if (current == isReviewed) return false;

            if (isReviewed) _reviewedPacsCache.Add(cleanUid);
            else _reviewedPacsCache.Remove(cleanUid);
            SaveReviewedPacs();

            UpdateRecords(records =>
            {
                var match = FindRecord(records, cleanUid);
                if (match == null || match.IsReviewed == isReviewed) return false;
                match.IsReviewed = isReviewed;
                return true;
            });
        }
        OnStudyReviewedChanged?.Invoke(cleanUid, isReviewed);
        return true;
    }

    /// <summary>Total size of the files in a folder, or 0 if it cannot be read. Reads the disk, so call it off the UI thread.</summary>
    public static long GetFolderSizeBytes(string? folderPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return 0;
            return new DirectoryInfo(folderPath).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Measures the study folder of records that have no stored size. Reads the disk, so call it off the UI thread.
    /// Returns true if any record was updated.
    /// </summary>
    public static bool BackfillSizes()
    {
        var sizes = new Dictionary<string, long>();
        foreach (var rec in LoadRecords().Where(r => r.SizeBytes <= 0))
        {
            long bytes = GetFolderSizeBytes(rec.FolderPath);
            if (bytes > 0) sizes[rec.StudyInstanceUid] = bytes;
        }
        if (sizes.Count == 0) return false;

        bool updated = false;
        UpdateRecords(all =>
        {
            foreach (var rec in all)
            {
                if (rec.SizeBytes <= 0 && sizes.TryGetValue(rec.StudyInstanceUid, out long bytes))
                {
                    rec.SizeBytes = bytes;
                    updated = true;
                }
            }
            return updated;
        });
        return updated;
    }

    public static long GetDatabaseTotalSizeBytes()
    {
        try
        {
            var records = LoadRecords();
            long totalBytes = 0;
            foreach (var r in records)
            {
                if (!string.IsNullOrEmpty(r.FolderPath) && Directory.Exists(r.FolderPath))
                {
                    try
                    {
                        var di = new DirectoryInfo(r.FolderPath);
                        totalBytes += di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
                    }
                    catch { }
                }
            }
            return totalBytes;
        }
        catch
        {
            return 0;
        }
    }

    public static void CleanOrphanDatabaseFolders()
    {
        try
        {
            string dbFolder = GetDatabaseFolder();
            if (!Directory.Exists(dbFolder)) return;
            var records = LoadRecords();
            var validFolders = new HashSet<string>(records.Select(r => Path.GetFullPath(r.FolderPath)), StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(dbFolder))
            {
                string full = Path.GetFullPath(dir);
                if (!validFolders.Contains(full))
                {
                    try { Directory.Delete(full, true); } catch { }
                }
            }
        }
        catch { }
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1024.0)
        {
            double gb = mb / 1024.0;
            return $"{gb:F2} GB";
        }
        return $"{mb:F1} MB";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Trim();
    }
}

