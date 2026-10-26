// @purpose: Local JSON database catalog for indexed DICOM studies, import history, and local "Reviewed" status storage.
// @depends: System.Text.Json, OpenRadViewer.Core.Models
// @exports: DatabaseService, DatabaseRecordModel

using System.IO;
using System.Text.Json;
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
    public string StudyDescription { get; set; } = string.Empty;
    public string ReadingPhysician { get; set; } = string.Empty;
    public string PerformingPhysician { get; set; } = string.Empty;
    public string ReferringPhysician { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    public bool MetadataBackfilled { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public bool IsReviewed { get; set; }
    public DateTime ImportDate { get; set; } = DateTime.Now;
    public int SeriesCount { get; set; }
    public int TotalImages { get; set; }
    public List<DatabaseSeriesRecord> Series { get; set; } = new();
}

public class DatabaseSeriesRecord
{
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public int SeriesNumber { get; set; }
    public string Modality { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public int ImageCount { get; set; }
}

public class DatabaseService
{
    private static readonly string DbDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ParsecDatabase");
    private static readonly string IndexFile = Path.Combine(DbDirectory, "database_index.json");
    private static readonly object _fileLock = new();

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
            if (!File.Exists(IndexFile)) return new List<DatabaseRecordModel>();
            try
            {
                string json = File.ReadAllText(IndexFile);
                var records = JsonSerializer.Deserialize<List<DatabaseRecordModel>>(json) ?? new List<DatabaseRecordModel>();

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

                if (anyUpdated)
                {
                    try
                    {
                        string updatedJson = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(IndexFile, updatedJson);
                    }
                    catch { }
                }

                return records;
            }
            catch
            {
                return new List<DatabaseRecordModel>();
            }
        }
    }

    public static void SaveRecords(List<DatabaseRecordModel> records)
    {
        lock (_fileLock)
        {
            GetDatabaseFolder();
            try
            {
                string json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(IndexFile, json);
            }
            catch { }
        }
    }

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
            Series = seriesRecords
        };

        var all = LoadRecords();
        all.RemoveAll(r => r.StudyInstanceUid == record.StudyInstanceUid);
        all.Insert(0, record);
        SaveRecords(all);

        return record;
    }

    public static void DeleteRecord(DatabaseRecordModel record)
    {
        DeleteRecords(new[] { record });
    }

    public static void DeleteRecords(IEnumerable<DatabaseRecordModel> records)
    {
        var recordList = records.ToList();
        if (recordList.Count == 0) return;

        var uids = new HashSet<string>(recordList.Select(r => r.StudyInstanceUid));
        var all = LoadRecords();
        all.RemoveAll(r => uids.Contains(r.StudyInstanceUid));
        SaveRecords(all);

        foreach (var r in recordList)
        {
            if (!string.IsNullOrEmpty(r.FolderPath) && Directory.Exists(r.FolderPath))
            {
                ForceDeleteDirectory(r.FolderPath);
            }
        }

        CleanOrphanDatabaseFolders();
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

        var all = LoadRecords();
        var allDict = all.ToDictionary(r => r.StudyInstanceUid);

        // If any record is NOT reviewed, mark all as reviewed. If all are reviewed, mark all as unreviewed.
        bool targetState = recordList.Any(r => !r.IsReviewed);

        foreach (var r in recordList)
        {
            r.IsReviewed = targetState;
            if (allDict.TryGetValue(r.StudyInstanceUid, out var item))
            {
                item.IsReviewed = targetState;
            }
        }

        SaveRecords(all);

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

            var records = LoadRecords();
            var match = records.FirstOrDefault(r => string.Equals(r.StudyInstanceUid?.Trim().TrimEnd('\0'), cleanUid, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                match.IsReviewed = newState;
                SaveRecords(records);
            }
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

            var records = LoadRecords();
            var match = records.FirstOrDefault(r => string.Equals(r.StudyInstanceUid?.Trim().TrimEnd('\0'), cleanUid, StringComparison.OrdinalIgnoreCase));
            if (match != null && match.IsReviewed != isReviewed)
            {
                match.IsReviewed = isReviewed;
                SaveRecords(records);
            }
        }
        OnStudyReviewedChanged?.Invoke(cleanUid, isReviewed);
        return true;
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

