// @purpose: High-performance streaming scanner, metadata extractor, and pixel loader for DICOM files (CT, MR, CR, DX, multi-frame, compressed).
// @depends: FellowOakDicom, FellowOakDicom.Imaging.Codec (fo-dicom.Codecs), OpenRadViewer.Core.Models
// @exports: DicomLoaderService, ParsedSliceBatchItem, DiscoveredSeriesEntry

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.IO.Buffer;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public class ParsedSliceBatchItem
{
    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string BirthDate { get; set; } = string.Empty;
    public string Sex { get; set; } = string.Empty;
    public string PatientAge { get; set; } = string.Empty;

    public string StudyUid { get; set; } = string.Empty;
    public string StudyDate { get; set; } = string.Empty;
    public string StudyTime { get; set; } = string.Empty;
    public string StudyDescription { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    public string ReadingPhysician { get; set; } = string.Empty;
    public string PerformingPhysician { get; set; } = string.Empty;
    public string ReferringPhysician { get; set; } = string.Empty;
    public string InstitutionName { get; set; } = string.Empty;
    public string ContrastBolusAgent { get; set; } = string.Empty;
    public string ContrastBolusRoute { get; set; } = string.Empty;
    public double TotalDlp { get; set; }

    public string SeriesUid { get; set; } = string.Empty;
    public int SeriesNumber { get; set; }
    public string SeriesDescription { get; set; } = string.Empty;
    public string Modality { get; set; } = "MR";
    public string BodyPart { get; set; } = string.Empty;
    public string Laterality { get; set; } = string.Empty;
    public string SeriesTime { get; set; } = string.Empty;
    public double CtdiVol { get; set; }

    public List<DicomSliceModel> Slices { get; set; } = new();
    public System.Windows.Media.Imaging.BitmapSource? InitialThumbnail { get; set; }
}

public class DicomLoaderService
{
    static DicomLoaderService()
    {
        // Initialize fo-dicom codecs
        try
        {
            new DicomSetupBuilder()
                .RegisterServices(s => s.AddFellowOakDicom().AddTranscoderManager<FellowOakDicom.Imaging.NativeCodec.NativeTranscoderManager>())
                .SkipValidation()
                .Build();
        }
        catch
        {
            // Codecs already initialized or using default setup
        }
    }

    public static bool IsProjectionRadiography(string modality)
    {
        if (string.IsNullOrWhiteSpace(modality)) return false;
        string m = modality.Trim().ToUpperInvariant();
        return m is "DX" or "CR" or "RG" or "XA" or "RF" or "MG" or "IO" or "PX" or "XC";
    }

    public static string ExtractProjectionView(DicomDataset ds)
    {
        string view = "";
        // 1. PositionerType (e.g. "KNEE R LAT", "KNEE R AP")
        if (ds.Contains(new DicomTag(0x0018, 0x1508)))
        {
            view = ds.GetSingleValueOrDefault(new DicomTag(0x0018, 0x1508), "").Trim();
        }
        // 2. PatientPosition (e.g. "AP", "LAT", "PA", "OBL")
        if (string.IsNullOrWhiteSpace(view) && ds.Contains(DicomTag.PatientPosition))
        {
            view = ds.GetSingleValueOrDefault(DicomTag.PatientPosition, "").Trim();
        }
        // 3. ViewPosition (e.g. "AP", "PA", "LAT", "LL", "RL")
        if (string.IsNullOrWhiteSpace(view) && ds.Contains(DicomTag.ViewPosition))
        {
            view = ds.GetSingleValueOrDefault(DicomTag.ViewPosition, "").Trim();
        }
        return CleanDicomString(view);
    }

    /// <summary>
    /// Progressively streams DICOM slices in real time to the caller as batches, utilizing multi-core parallel parsing.
    /// The left preview bar and viewports can display images immediately without waiting for the full study scan to complete.
    /// </summary>
    public Task ScanFolderStreamingAsync(
        string folderPath, 
        Action<List<ParsedSliceBatchItem>> onBatchParsed, 
        IProgress<(int scanned, int total, string currentFile)>? progress = null, 
        CancellationToken ct = default) =>
        ScanPathsStreamingAsync(new[] { folderPath }, onBatchParsed, progress, ct);

    /// <summary>
    /// Progressively streams DICOM slices in real time from specified folders or individual files.
    /// </summary>
    public async Task ScanPathsStreamingAsync(
        IEnumerable<string> paths, 
        Action<List<ParsedSliceBatchItem>> onBatchParsed, 
        IProgress<(int scanned, int total, string currentFile)>? progress = null, 
        CancellationToken ct = default)
    {
        if (paths == null) return;
        var allFiles = new List<string>();

        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (Directory.Exists(p))
            {
                allFiles.AddRange(Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));
            }
            else if (File.Exists(p))
            {
                allFiles.Add(p);
            }
        }

        allFiles = allFiles.Distinct().ToList();
        int total = allFiles.Count;
        if (total == 0) return;

        int scanned = 0;
        var batchQueue = new ConcurrentQueue<ParsedSliceBatchItem>();
        var seenSeries = new ConcurrentDictionary<string, byte>();

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 16)
        };

        var parseTask = Task.Run(() =>
        {
            Parallel.ForEach(allFiles, parallelOptions, file =>
            {
                if (ct.IsCancellationRequested) return;

                int curScanned = Interlocked.Increment(ref scanned);
                if (curScanned % 25 == 0 || curScanned == total)
                {
                    progress?.Report((curScanned, total, Path.GetFileName(file)));
                }

                try
                {
                    var dcm = DicomFile.Open(file, FileReadOption.ReadLargeOnDemand);
                    var ds = dcm.Dataset;

                    bool hasPixelData = ds.Contains(DicomTag.PixelData);
                    int rows = ds.GetSingleValueOrDefault(DicomTag.Rows, 0);
                    int cols = ds.GetSingleValueOrDefault(DicomTag.Columns, 0);
                    if (!hasPixelData || rows <= 0 || cols <= 0)
                        return;

                    string patientId = ds.GetSingleValueOrDefault(DicomTag.PatientID, "UNKNOWN_ID");
                    string patientName = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.PatientName, "Anonymous"));
                    string birthDate = ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, "");
                    string sex = ds.GetSingleValueOrDefault(DicomTag.PatientSex, "");
                    string patientAge = ds.GetSingleValueOrDefault(DicomTag.PatientAge, "");
                    string contrastAgent = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.ContrastBolusAgent, ""));
                    string contrastRoute = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.ContrastBolusRoute, ""));

                    double ctdiVol = ds.GetSingleValueOrDefault(new DicomTag(0x0018, 0x9345), 0.0);
                    double dlp = ExtractDlpFromDataset(ds);

                    string studyUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, Guid.NewGuid().ToString());
                    string studyDate = ds.GetSingleValueOrDefault(DicomTag.StudyDate, "");
                    string studyTime = ds.GetSingleValueOrDefault(DicomTag.StudyTime, "");
                    string studyDesc = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, "Study");
                    string institution = ds.GetSingleValueOrDefault(DicomTag.InstitutionName, "");
                    string accession = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, ""));
                    string readingPhys = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.NameOfPhysiciansReadingStudy, ""));
                    string performingPhys = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.PerformingPhysicianName, ""));
                    if (string.IsNullOrWhiteSpace(performingPhys))
                        performingPhys = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.OperatorsName, ""));
                    string referringPhys = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.ReferringPhysicianName, ""));

                    string rawSeriesUid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, Guid.NewGuid().ToString());
                    string sopUid = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, Guid.NewGuid().ToString());
                    int seriesNum = ds.GetSingleValueOrDefault(DicomTag.SeriesNumber, 0);
                    int instNum = ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0);
                    string seriesDesc = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, $"Series {seriesNum}"));
                    string modality = ds.GetSingleValueOrDefault(DicomTag.Modality, "OT");
                    string bodyPart = ds.GetSingleValueOrDefault(DicomTag.BodyPartExamined, "");
                    string laterality = ds.GetSingleValueOrDefault(DicomTag.Laterality, "");
                    string seriesTime = ds.GetSingleValueOrDefault(DicomTag.SeriesTime, "");
                    if (string.IsNullOrWhiteSpace(seriesTime))
                    {
                        seriesTime = ds.GetSingleValueOrDefault(DicomTag.AcquisitionTime, "");
                    }

                    string seriesUid = rawSeriesUid;
                    if (IsProjectionRadiography(modality))
                    {
                        // In projection radiography, each separate file/SOP instance is an independent projection/view
                        seriesUid = $"{rawSeriesUid}_{sopUid}";
                        if (instNum > 0)
                        {
                            seriesNum = seriesNum > 0 ? seriesNum * 100 + instNum : instNum;
                        }

                        string view = ExtractProjectionView(ds);
                        if (!string.IsNullOrWhiteSpace(view))
                        {
                            if (!seriesDesc.EndsWith($"[{view}]", StringComparison.OrdinalIgnoreCase) &&
                                !seriesDesc.EndsWith($"({view})", StringComparison.OrdinalIgnoreCase))
                            {
                                seriesDesc = $"{seriesDesc} [{view}]";
                            }
                        }
                    }

                    var item = new ParsedSliceBatchItem
                    {
                        PatientId = patientId,
                        PatientName = patientName,
                        BirthDate = birthDate,
                        Sex = sex,
                        PatientAge = patientAge,
                        StudyUid = studyUid,
                        StudyDate = studyDate,
                        StudyTime = studyTime,
                        StudyDescription = studyDesc,
                        InstitutionName = institution,
                        AccessionNumber = accession,
                        ReadingPhysician = readingPhys,
                        PerformingPhysician = performingPhys,
                        ReferringPhysician = referringPhys,
                        ContrastBolusAgent = contrastAgent,
                        ContrastBolusRoute = contrastRoute,
                        TotalDlp = dlp,
                        SeriesUid = seriesUid,
                        SeriesNumber = seriesNum,
                        SeriesDescription = seriesDesc,
                        Modality = modality,
                        BodyPart = bodyPart,
                        Laterality = laterality,
                        SeriesTime = seriesTime,
                        CtdiVol = ctdiVol
                    };

                    int numFrames = ds.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);
                    if (numFrames > 1)
                    {
                        for (int f = 0; f < numFrames; f++)
                        {
                            var slice = ParseSliceMetadata(ds, file, f);
                            item.Slices.Add(slice);
                        }
                    }
                    else
                    {
                        var slice = ParseSliceMetadata(ds, file, 0);
                        item.Slices.Add(slice);
                    }

                    // Render fast initial thumbnail on background thread for first slice seen for this series
                    if (seenSeries.TryAdd(seriesUid, 1) && item.Slices.Count > 0)
                    {
                        item.InitialThumbnail = RenderThumbnail(item.Slices[0]);
                    }

                    batchQueue.Enqueue(item);
                }
                catch
                {
                    // Skip corrupt / non-DICOM files
                }
            });
        }, ct);

        // Progressively drain queue to UI callback in smooth intervals (~35ms / ~30fps)
        while (!parseTask.IsCompleted || !batchQueue.IsEmpty)
        {
            var batch = new List<ParsedSliceBatchItem>();
            while (batch.Count < 50 && batchQueue.TryDequeue(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count > 0)
            {
                onBatchParsed(batch);
            }

            await Task.Delay(35, ct).ConfigureAwait(false);
        }

        await parseTask;

        // Drain any remaining items after task completes
        var finalBatch = new List<ParsedSliceBatchItem>();
        while (batchQueue.TryDequeue(out var item))
        {
            finalBatch.Add(item);
        }
        if (finalBatch.Count > 0)
        {
            onBatchParsed(finalBatch);
        }
    }

    /// <summary>
    /// Parses a single DICOM file on disk into a ParsedSliceBatchItem.
    /// Thread-safe and used for real-time streaming ingestion from PACS.
    /// </summary>
    public ParsedSliceBatchItem? ParseSingleFile(string file, ConcurrentDictionary<string, byte>? seenSeries = null)
    {
        try
        {
            var dcm = DicomFile.Open(file, FileReadOption.ReadLargeOnDemand);
            var ds = dcm.Dataset;

            bool hasPixelData = ds.Contains(DicomTag.PixelData);
            int rows = ds.GetSingleValueOrDefault(DicomTag.Rows, 0);
            int cols = ds.GetSingleValueOrDefault(DicomTag.Columns, 0);
            if (!hasPixelData || rows <= 0 || cols <= 0)
                return null;

            string patientId = ds.GetSingleValueOrDefault(DicomTag.PatientID, "UNKNOWN_ID");
            string patientName = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.PatientName, "Anonymous"));
            string birthDate = ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, "");
            string sex = ds.GetSingleValueOrDefault(DicomTag.PatientSex, "");
            string patientAge = ds.GetSingleValueOrDefault(DicomTag.PatientAge, "");
            string contrastAgent = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.ContrastBolusAgent, ""));
            string contrastRoute = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.ContrastBolusRoute, ""));

            double ctdiVol = ds.GetSingleValueOrDefault(new DicomTag(0x0018, 0x9345), 0.0);
            double dlp = ExtractDlpFromDataset(ds);

            string studyUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, Guid.NewGuid().ToString());
            string studyDate = ds.GetSingleValueOrDefault(DicomTag.StudyDate, "");
            string studyTime = ds.GetSingleValueOrDefault(DicomTag.StudyTime, "");
            string studyDesc = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, "Study");
            string institution = ds.GetSingleValueOrDefault(DicomTag.InstitutionName, "");
            string accession = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, ""));
            string readingPhys = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.NameOfPhysiciansReadingStudy, ""));
            string performingPhys = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.PerformingPhysicianName, ""));
            if (string.IsNullOrWhiteSpace(performingPhys))
                performingPhys = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.OperatorsName, ""));
            string referringPhys = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.ReferringPhysicianName, ""));

            string rawSeriesUid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, Guid.NewGuid().ToString());
            string sopUid = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, Guid.NewGuid().ToString());
            int seriesNum = ds.GetSingleValueOrDefault(DicomTag.SeriesNumber, 0);
            int instNum = ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0);
            string seriesDesc = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, $"Series {seriesNum}"));
            string modality = ds.GetSingleValueOrDefault(DicomTag.Modality, "OT");
            string bodyPart = ds.GetSingleValueOrDefault(DicomTag.BodyPartExamined, "");
            string laterality = ds.GetSingleValueOrDefault(DicomTag.Laterality, "");
            string seriesTime = ds.GetSingleValueOrDefault(DicomTag.SeriesTime, "");
            if (string.IsNullOrWhiteSpace(seriesTime))
            {
                seriesTime = ds.GetSingleValueOrDefault(DicomTag.AcquisitionTime, "");
            }

            string seriesUid = rawSeriesUid;
            if (IsProjectionRadiography(modality))
            {
                seriesUid = $"{rawSeriesUid}_{sopUid}";
                if (instNum > 0)
                {
                    seriesNum = seriesNum > 0 ? seriesNum * 100 + instNum : instNum;
                }

                string view = ExtractProjectionView(ds);
                if (!string.IsNullOrWhiteSpace(view))
                {
                    if (!seriesDesc.EndsWith($"[{view}]", StringComparison.OrdinalIgnoreCase) &&
                        !seriesDesc.EndsWith($"({view})", StringComparison.OrdinalIgnoreCase))
                    {
                        seriesDesc = $"{seriesDesc} [{view}]";
                    }
                }
            }

            var item = new ParsedSliceBatchItem
            {
                PatientId = patientId,
                PatientName = patientName,
                BirthDate = birthDate,
                Sex = sex,
                PatientAge = patientAge,
                StudyUid = studyUid,
                StudyDate = studyDate,
                StudyTime = studyTime,
                StudyDescription = studyDesc,
                InstitutionName = institution,
                AccessionNumber = accession,
                ReadingPhysician = readingPhys,
                PerformingPhysician = performingPhys,
                ReferringPhysician = referringPhys,
                ContrastBolusAgent = contrastAgent,
                ContrastBolusRoute = contrastRoute,
                TotalDlp = dlp,
                SeriesUid = seriesUid,
                SeriesNumber = seriesNum,
                SeriesDescription = seriesDesc,
                Modality = modality,
                BodyPart = bodyPart,
                Laterality = laterality,
                SeriesTime = seriesTime,
                CtdiVol = ctdiVol
            };

            int numFrames = ds.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);
            if (numFrames > 1)
            {
                for (int f = 0; f < numFrames; f++)
                {
                    var slice = ParseSliceMetadata(ds, file, f);
                    item.Slices.Add(slice);
                }
            }
            else
            {
                var slice = ParseSliceMetadata(ds, file, 0);
                item.Slices.Add(slice);
            }

            if (seenSeries != null && seenSeries.TryAdd(seriesUid, 1) && item.Slices.Count > 0)
            {
                item.InitialThumbnail = RenderThumbnail(item.Slices[0]);
            }

            return item;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Scans a folder recursively and groups all DICOM files by Patient -> Study -> Series -> Sorted Slices.
    /// Utilizes parallel scanning with full backward compatibility.
    /// </summary>
    public async Task<List<PatientModel>> ScanFolderAsync(string folderPath, IProgress<(int scanned, int total, string currentFile)>? progress = null, CancellationToken ct = default)
    {
        var patientsMap = new Dictionary<string, PatientModel>();

        await ScanFolderStreamingAsync(folderPath, batch =>
        {
            foreach (var item in batch)
            {
                string patientKey = string.IsNullOrEmpty(item.PatientId) ? item.PatientName : item.PatientId;
                if (!patientsMap.TryGetValue(patientKey, out var patient))
                {
                    patient = new PatientModel
                    {
                        Id = item.PatientId,
                        Name = item.PatientName,
                        BirthDate = item.BirthDate,
                        Sex = item.Sex,
                        PatientAge = item.PatientAge
                    };
                    patientsMap[patientKey] = patient;
                }
                else if (string.IsNullOrWhiteSpace(patient.PatientAge) && !string.IsNullOrWhiteSpace(item.PatientAge))
                {
                    patient.PatientAge = item.PatientAge;
                }

                var study = patient.Studies.FirstOrDefault(s => s.StudyInstanceUid == item.StudyUid);
                if (study == null)
                {
                    study = new StudyModel
                    {
                        StudyInstanceUid = item.StudyUid,
                        RawStudyDate = item.StudyDate,
                        StudyDate = FormatDicomDate(item.StudyDate),
                        StudyTime = item.StudyTime,
                        StudyDescription = item.StudyDescription,
                        InstitutionName = item.InstitutionName,
                        AccessionNumber = item.AccessionNumber,
                        ReadingPhysician = item.ReadingPhysician,
                        PerformingPhysician = item.PerformingPhysician,
                        ReferringPhysician = item.ReferringPhysician,
                        ContrastBolusAgent = item.ContrastBolusAgent,
                        ContrastBolusRoute = item.ContrastBolusRoute,
                        TotalDlp = item.TotalDlp,
                        PatientAge = item.PatientAge
                    };
                    patient.Studies.Add(study);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(study.AccessionNumber) && !string.IsNullOrWhiteSpace(item.AccessionNumber))
                        study.AccessionNumber = item.AccessionNumber;
                    if (string.IsNullOrWhiteSpace(study.ReadingPhysician) && !string.IsNullOrWhiteSpace(item.ReadingPhysician))
                        study.ReadingPhysician = item.ReadingPhysician;
                    if (string.IsNullOrWhiteSpace(study.PerformingPhysician) && !string.IsNullOrWhiteSpace(item.PerformingPhysician))
                        study.PerformingPhysician = item.PerformingPhysician;
                    if (string.IsNullOrWhiteSpace(study.ReferringPhysician) && !string.IsNullOrWhiteSpace(item.ReferringPhysician))
                        study.ReferringPhysician = item.ReferringPhysician;
                    if (string.IsNullOrWhiteSpace(study.ContrastBolusAgent) && !string.IsNullOrWhiteSpace(item.ContrastBolusAgent))
                        study.ContrastBolusAgent = item.ContrastBolusAgent;
                    if (item.TotalDlp > 0 && item.TotalDlp > study.TotalDlp)
                        study.TotalDlp = item.TotalDlp;
                    if (string.IsNullOrWhiteSpace(study.PatientAge) && !string.IsNullOrWhiteSpace(item.PatientAge))
                        study.PatientAge = item.PatientAge;
                }

                var series = study.Series.FirstOrDefault(s => s.SeriesInstanceUid == item.SeriesUid);
                if (series == null)
                {
                    series = new SeriesModel
                    {
                        SeriesInstanceUid = item.SeriesUid,
                        StudyInstanceUid = item.StudyUid,
                        StudyDescription = study.StudyDescription,
                        Laterality = item.Laterality,
                        SeriesNumber = item.SeriesNumber,
                        SeriesDescription = item.SeriesDescription,
                        Modality = item.Modality,
                        BodyPart = item.BodyPart,
                        PatientName = patient.Name,
                        PatientId = patient.Id,
                        StudyDate = study.StudyDate,
                        SeriesTime = item.SeriesTime,
                        CtdiVol = item.CtdiVol,
                        TotalDlp = item.TotalDlp,
                        ContrastBolusAgent = item.ContrastBolusAgent,
                        ThumbnailImage = item.InitialThumbnail
                    };
                    study.Series.Add(series);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(series.SeriesTime) && !string.IsNullOrWhiteSpace(item.SeriesTime))
                        series.SeriesTime = item.SeriesTime;
                    if (item.CtdiVol > 0 && series.CtdiVol == 0) series.CtdiVol = item.CtdiVol;
                    if (item.TotalDlp > 0 && series.TotalDlp == 0) series.TotalDlp = item.TotalDlp;
                    if (string.IsNullOrWhiteSpace(series.ContrastBolusAgent) && !string.IsNullOrWhiteSpace(item.ContrastBolusAgent))
                        series.ContrastBolusAgent = item.ContrastBolusAgent;
                    if (series.ThumbnailImage == null && item.InitialThumbnail != null)
                        series.ThumbnailImage = item.InitialThumbnail;
                }

                foreach (var s in item.Slices)
                {
                    bool isDuplicate = series.Slices.Any(existing =>
                        existing.FrameIndex == s.FrameIndex &&
                        (!string.IsNullOrEmpty(s.SopInstanceUid) && !string.IsNullOrEmpty(existing.SopInstanceUid)
                            ? existing.SopInstanceUid == s.SopInstanceUid
                            : string.Equals(existing.FilePath, s.FilePath, StringComparison.OrdinalIgnoreCase)));

                    if (!isDuplicate)
                    {
                        series.Slices.Add(s);
                    }
                }
            }
        }, progress, ct);

        // Post-processing: sort series chronologically and slices spatially
        foreach (var p in patientsMap.Values)
        {
            foreach (var st in p.Studies)
            {
                st.Series.RemoveAll(se => se.Slices.Count == 0);

                foreach (var se in st.Series)
                {
                    if (string.IsNullOrWhiteSpace(se.SeriesTime) && se.Slices.Count > 0)
                    {
                        var firstAcq = se.Slices.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.AcquisitionTime))?.AcquisitionTime;
                        if (!string.IsNullOrWhiteSpace(firstAcq)) se.SeriesTime = firstAcq;
                    }
                }

                st.Series = st.Series
                    .OrderBy(se => string.IsNullOrWhiteSpace(se.SeriesTime) ? "999999" : se.SeriesTime)
                    .ThenBy(se => se.SeriesNumber)
                    .ToList();

                var doseInfoSeries = st.Series.FirstOrDefault(s =>
                    s.SeriesDescription.Contains("Dose", StringComparison.OrdinalIgnoreCase) && s.TotalDlp > 0);
                if (doseInfoSeries != null && doseInfoSeries.TotalDlp > 0)
                {
                    st.TotalDlp = doseInfoSeries.TotalDlp;
                }
                else if (st.TotalDlp == 0.0 && st.Series.Any(s => s.TotalDlp > 0))
                {
                    double sumDlp = st.Series
                        .Where(s => !s.SeriesDescription.Contains("Surview", StringComparison.OrdinalIgnoreCase) &&
                                    !s.SeriesDescription.Contains("Scout", StringComparison.OrdinalIgnoreCase) &&
                                    s.TotalDlp > 0)
                        .Sum(s => s.TotalDlp);
                    if (sumDlp > 0) st.TotalDlp = sumDlp;
                }

                foreach (var se in st.Series)
                {
                    SortSlices(se.Slices);

                    string commentSummary = FormatSpineCommentsSummary(se.Slices.Select(s => s.ImageComments));
                    if (!string.IsNullOrWhiteSpace(commentSummary) && !se.SeriesDescription.Contains(commentSummary, StringComparison.OrdinalIgnoreCase))
                    {
                        se.SeriesDescription = $"{se.SeriesDescription} ({commentSummary})";
                    }

                    if (se.Slices.Count > 0)
                    {
                        int midIdx = se.Slices.Count / 2;
                        var midThumb = RenderThumbnail(se.Slices[midIdx]);
                        if (midThumb != null) se.ThumbnailImage = midThumb;
                    }
                }
            }
            p.Studies.RemoveAll(st => st.Series.Count == 0);
        }

        return patientsMap.Values.ToList();
    }

    public static DicomSliceModel ParseSliceMetadata(DicomDataset ds, string filePath, int frameIndex = 0)
    {
        int numFrames = ds.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);
        int instNum = ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, frameIndex + 1);
        if (numFrames > 1 && instNum <= 1)
        {
            instNum = frameIndex + 1;
        }

        var slice = new DicomSliceModel
        {
            FilePath = filePath,
            FrameIndex = frameIndex,
            SopInstanceUid = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, ""),
            InstanceNumber = instNum,
            Rows = ds.GetSingleValueOrDefault(DicomTag.Rows, 0),
            Columns = ds.GetSingleValueOrDefault(DicomTag.Columns, 0),
            BitsAllocated = ds.GetSingleValueOrDefault(DicomTag.BitsAllocated, 16),
            BitsStored = ds.GetSingleValueOrDefault(DicomTag.BitsStored, 12),
            PixelRepresentation = ds.GetSingleValueOrDefault(DicomTag.PixelRepresentation, 0),
            PhotometricInterpretation = ds.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2"),
            PresentationLutShape = ds.GetSingleValueOrDefault(DicomTag.PresentationLUTShape, ""),
            RescaleSlope = ds.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0),
            RescaleIntercept = ds.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0),
            SliceThickness = ds.GetSingleValueOrDefault(DicomTag.SliceThickness, 1.0),
            SliceLocation = ds.GetSingleValueOrDefault(DicomTag.SliceLocation, 0.0),
            RepetitionTime = ds.GetSingleValueOrDefault(DicomTag.RepetitionTime, 0.0),
            EchoTime = ds.GetSingleValueOrDefault(DicomTag.EchoTime, 0.0),
            MagneticFieldStrength = ds.GetSingleValueOrDefault(DicomTag.MagneticFieldStrength, 0.0).ToString("0.#") + "T",
            SequenceName = ds.GetSingleValueOrDefault(DicomTag.SequenceName, ""),
            ImageComments = CleanDicomString(ds.GetSingleValueOrDefault(DicomTag.ImageComments, ""))
        };

        // Check sequences for multi-frame functional groups (Siemens, Philips, modern DICOM MR/CT enhanced)
        DicomDataset? frameItem = null;
        if (ds.Contains(DicomTag.PerFrameFunctionalGroupsSequence))
        {
            var perFrameSeq = ds.GetSequence(DicomTag.PerFrameFunctionalGroupsSequence);
            if (perFrameSeq != null && frameIndex < perFrameSeq.Items.Count)
            {
                frameItem = perFrameSeq.Items[frameIndex];
            }
        }

        DicomDataset? sharedItem = null;
        if (ds.Contains(DicomTag.SharedFunctionalGroupsSequence))
        {
            var sharedSeq = ds.GetSequence(DicomTag.SharedFunctionalGroupsSequence);
            if (sharedSeq != null && sharedSeq.Items.Count > 0)
            {
                sharedItem = sharedSeq.Items[0];
            }
        }

        if (frameItem != null && frameItem.Contains(DicomTag.FrameContentSequence))
        {
            var fcSeq = frameItem.GetSequence(DicomTag.FrameContentSequence);
            if (fcSeq != null && fcSeq.Items.Count > 0)
            {
                var fcItem = fcSeq.Items[0];
                if (fcItem.Contains(DicomTag.InStackPositionNumber))
                {
                    slice.InstanceNumber = fcItem.GetSingleValueOrDefault(DicomTag.InStackPositionNumber, slice.InstanceNumber);
                }
            }
        }

        slice.AcquisitionTime = ds.GetSingleValueOrDefault(DicomTag.AcquisitionTime, "");
        slice.TemporalPosition = ds.GetSingleValueOrDefault(DicomTag.TemporalPositionIdentifier, 0);
        slice.DiffusionBValue = ExtractDiffusionBValue(ds, frameItem, sharedItem, frameIndex);

        // Window center and width
        bool foundVoi = false;
        if (frameItem != null && frameItem.Contains(DicomTag.FrameVOILUTSequence))
        {
            var voiSeq = frameItem.GetSequence(DicomTag.FrameVOILUTSequence);
            if (voiSeq != null && voiSeq.Items.Count > 0)
            {
                slice.DefaultWindowCenter = voiSeq.Items[0].GetSingleValueOrDefault(DicomTag.WindowCenter, 0.0);
                slice.DefaultWindowWidth = voiSeq.Items[0].GetSingleValueOrDefault(DicomTag.WindowWidth, 0.0);
                if (slice.DefaultWindowWidth > 1) foundVoi = true;
            }
        }
        if (!foundVoi && sharedItem != null && sharedItem.Contains(DicomTag.FrameVOILUTSequence))
        {
            var voiSeq = sharedItem.GetSequence(DicomTag.FrameVOILUTSequence);
            if (voiSeq != null && voiSeq.Items.Count > 0)
            {
                slice.DefaultWindowCenter = voiSeq.Items[0].GetSingleValueOrDefault(DicomTag.WindowCenter, 0.0);
                slice.DefaultWindowWidth = voiSeq.Items[0].GetSingleValueOrDefault(DicomTag.WindowWidth, 0.0);
                if (slice.DefaultWindowWidth > 1) foundVoi = true;
            }
        }
        if (!foundVoi && ds.Contains(DicomTag.VOILUTSequence))
        {
            try
            {
                var voiSeq = ds.GetSequence(DicomTag.VOILUTSequence);
                if (voiSeq != null && voiSeq.Items.Count > 0)
                {
                    var item = voiSeq.Items[0];
                    if (item.Contains(DicomTag.WindowCenter) && item.Contains(DicomTag.WindowWidth))
                    {
                        slice.DefaultWindowCenter = item.GetSingleValueOrDefault(DicomTag.WindowCenter, 0.0);
                        slice.DefaultWindowWidth = item.GetSingleValueOrDefault(DicomTag.WindowWidth, 0.0);
                        if (slice.DefaultWindowWidth > 1) foundVoi = true;
                    }
                    else if (item.Contains(DicomTag.LUTDescriptor))
                    {
                        var desc = item.GetValues<int>(DicomTag.LUTDescriptor);
                        if (desc.Length >= 3)
                        {
                            int numEntries = desc[0] == 0 ? 65536 : desc[0];
                            int firstMapped = desc[1];
                            slice.DefaultWindowWidth = numEntries;
                            slice.DefaultWindowCenter = firstMapped + (numEntries / 2.0);
                            foundVoi = true;
                        }
                    }
                }
            }
            catch { }
        }
        if (!foundVoi && ds.Contains(DicomTag.WindowCenter) && ds.Contains(DicomTag.WindowWidth))
        {
            try
            {
                slice.DefaultWindowCenter = ds.GetValue<double>(DicomTag.WindowCenter, 0);
                slice.DefaultWindowWidth = ds.GetValue<double>(DicomTag.WindowWidth, 0);
            }
            catch
            {
                slice.DefaultWindowCenter = 0;
                slice.DefaultWindowWidth = 0;
            }
        }

        // Pixel spacing & slice thickness
        bool foundSpacing = false;
        if (frameItem != null && frameItem.Contains(DicomTag.PixelMeasuresSequence))
        {
            var pmSeq = frameItem.GetSequence(DicomTag.PixelMeasuresSequence);
            if (pmSeq != null && pmSeq.Items.Count > 0)
            {
                var pm = pmSeq.Items[0];
                if (pm.Contains(DicomTag.PixelSpacing))
                {
                    var sp = pm.GetValues<double>(DicomTag.PixelSpacing);
                    if (sp.Length >= 2) { slice.PixelSpacingY = sp[0]; slice.PixelSpacingX = sp[1]; foundSpacing = true; }
                }
                if (pm.Contains(DicomTag.SliceThickness))
                {
                    slice.SliceThickness = pm.GetSingleValueOrDefault(DicomTag.SliceThickness, 1.0);
                }
            }
        }
        if (!foundSpacing && sharedItem != null && sharedItem.Contains(DicomTag.PixelMeasuresSequence))
        {
            var pmSeq = sharedItem.GetSequence(DicomTag.PixelMeasuresSequence);
            if (pmSeq != null && pmSeq.Items.Count > 0)
            {
                var pm = pmSeq.Items[0];
                if (pm.Contains(DicomTag.PixelSpacing))
                {
                    var sp = pm.GetValues<double>(DicomTag.PixelSpacing);
                    if (sp.Length >= 2) { slice.PixelSpacingY = sp[0]; slice.PixelSpacingX = sp[1]; foundSpacing = true; }
                }
                if (pm.Contains(DicomTag.SliceThickness))
                {
                    slice.SliceThickness = pm.GetSingleValueOrDefault(DicomTag.SliceThickness, 1.0);
                }
            }
        }
        if (!foundSpacing)
        {
            try
            {
                if (ds.Contains(DicomTag.PixelSpacing))
                {
                    var spacing = ds.GetValues<double>(DicomTag.PixelSpacing);
                    if (spacing.Length >= 2)
                    {
                        slice.PixelSpacingY = spacing[0];
                        slice.PixelSpacingX = spacing[1];
                        foundSpacing = true;
                    }
                }
            }
            catch { }
        }
        if (!foundSpacing && ds.Contains(DicomTag.ImagerPixelSpacing))
        {
            try
            {
                var spacing = ds.GetValues<double>(DicomTag.ImagerPixelSpacing);
                if (spacing.Length >= 2)
                {
                    slice.PixelSpacingY = spacing[0];
                    slice.PixelSpacingX = spacing[1];
                    foundSpacing = true;
                }
            }
            catch { }
        }
        if (!foundSpacing && ds.Contains(DicomTag.NominalScannedPixelSpacing))
        {
            try
            {
                var spacing = ds.GetValues<double>(DicomTag.NominalScannedPixelSpacing);
                if (spacing.Length >= 2)
                {
                    slice.PixelSpacingY = spacing[0];
                    slice.PixelSpacingX = spacing[1];
                    foundSpacing = true;
                }
            }
            catch { }
        }

        // Spatial Orientation & Position
        try
        {
            // Position
            if (frameItem != null && frameItem.Contains(DicomTag.PlanePositionSequence))
            {
                var ppSeq = frameItem.GetSequence(DicomTag.PlanePositionSequence);
                if (ppSeq != null && ppSeq.Items.Count > 0 && ppSeq.Items[0].Contains(DicomTag.ImagePositionPatient))
                {
                    slice.ImagePositionPatient = ppSeq.Items[0].GetValues<double>(DicomTag.ImagePositionPatient);
                }
            }
            if (slice.ImagePositionPatient == null && ds.Contains(DicomTag.ImagePositionPatient))
            {
                slice.ImagePositionPatient = ds.GetValues<double>(DicomTag.ImagePositionPatient);
            }

            // Orientation
            if (frameItem != null && frameItem.Contains(DicomTag.PlaneOrientationSequence))
            {
                var poSeq = frameItem.GetSequence(DicomTag.PlaneOrientationSequence);
                if (poSeq != null && poSeq.Items.Count > 0 && poSeq.Items[0].Contains(DicomTag.ImageOrientationPatient))
                {
                    slice.ImageOrientationPatient = poSeq.Items[0].GetValues<double>(DicomTag.ImageOrientationPatient);
                }
            }
            if (slice.ImageOrientationPatient == null && sharedItem != null && sharedItem.Contains(DicomTag.PlaneOrientationSequence))
            {
                var poSeq = sharedItem.GetSequence(DicomTag.PlaneOrientationSequence);
                if (poSeq != null && poSeq.Items.Count > 0 && poSeq.Items[0].Contains(DicomTag.ImageOrientationPatient))
                {
                    slice.ImageOrientationPatient = poSeq.Items[0].GetValues<double>(DicomTag.ImageOrientationPatient);
                }
            }
            if (slice.ImageOrientationPatient == null && ds.Contains(DicomTag.ImageOrientationPatient))
            {
                slice.ImageOrientationPatient = ds.GetValues<double>(DicomTag.ImageOrientationPatient);
            }

            if (slice.ImageOrientationPatient != null)
            {
                slice.ComputeOrientationLabels();

                if (slice.ImagePositionPatient != null &&
                    slice.ImageOrientationPatient.Length >= 6 && slice.ImagePositionPatient.Length >= 3)
                {
                    double rx = slice.ImageOrientationPatient[0];
                    double ry = slice.ImageOrientationPatient[1];
                    double rz = slice.ImageOrientationPatient[2];
                    double cx = slice.ImageOrientationPatient[3];
                    double cy = slice.ImageOrientationPatient[4];
                    double cz = slice.ImageOrientationPatient[5];
                    double nx = ry * cz - rz * cy;
                    double ny = rz * cx - rx * cz;
                    double nz = rx * cy - ry * cx;
                    slice.SliceLocation = slice.ImagePositionPatient[0] * nx + slice.ImagePositionPatient[1] * ny + slice.ImagePositionPatient[2] * nz;
                }
            }
        }
        catch { }

        return slice;
    }

    /// <summary>
    /// Optimized batch loading of pixel data for an entire series.
    /// Groups slices by FilePath so multi-frame DICOM files are parsed exactly once instead of N times.
    /// </summary>
    public static void LoadSeriesPixelData(SeriesModel series)
    {
        var grouped = series.Slices
            .Where(s => s.RawPixelsUnsigned == null && s.RawPixelsSigned == null && s.RawRgbPixels == null)
            .GroupBy(s => s.FilePath)
            .ToList();

        if (grouped.Count == 0) return;

        Parallel.ForEach(grouped, group =>
        {
            try
            {
                var dcm = DicomFile.Open(group.Key);
                var ds = dcm.Dataset;

                if (ds.InternalTransferSyntax.IsEncapsulated)
                {
                    try
                    {
                        var transcoder = new DicomTranscoder(dcm.FileMetaInfo.TransferSyntax, DicomTransferSyntax.ExplicitVRLittleEndian);
                        dcm = transcoder.Transcode(dcm);
                        ds = dcm.Dataset;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Transcode Error] {ex.Message}");
                    }
                }

                var pixelData = DicomPixelData.Create(ds);

                foreach (var slice in group)
                {
                    if (slice.RawPixelsUnsigned != null || slice.RawPixelsSigned != null || slice.RawRgbPixels != null)
                        continue;

                    slice.BitsAllocated = ds.GetSingleValueOrDefault(DicomTag.BitsAllocated, slice.BitsAllocated);
                    slice.PixelRepresentation = ds.GetSingleValueOrDefault(DicomTag.PixelRepresentation, slice.PixelRepresentation);

                    var frameBuffer = pixelData.GetFrame(slice.FrameIndex);
                    byte[] rawBytes = frameBuffer.Data;
                    int numPixels = slice.Rows * slice.Columns;

                    if (slice.PhotometricInterpretation == "RGB" || slice.PhotometricInterpretation == "YBR_FULL")
                    {
                        slice.RawRgbPixels = rawBytes;
                        continue;
                    }

                    if (slice.BitsAllocated == 16)
                    {
                        if (slice.PixelRepresentation == 1) // Signed
                        {
                            var signedBuffer = new short[numPixels];
                            Buffer.BlockCopy(rawBytes, 0, signedBuffer, 0, Math.Min(rawBytes.Length, numPixels * 2));
                            slice.RawPixelsSigned = signedBuffer;

                            int min = int.MaxValue;
                            int max = int.MinValue;
                            for (int i = 0; i < signedBuffer.Length; i++)
                            {
                                short v = signedBuffer[i];
                                if (v < min) min = v;
                                if (v > max) max = v;
                            }
                            slice.MinPixelValue = min;
                            slice.MaxPixelValue = max;
                        }
                        else // Unsigned
                        {
                            var unsignedBuffer = new ushort[numPixels];
                            Buffer.BlockCopy(rawBytes, 0, unsignedBuffer, 0, Math.Min(rawBytes.Length, numPixels * 2));
                            slice.RawPixelsUnsigned = unsignedBuffer;

                            int min = int.MaxValue;
                            int max = int.MinValue;
                            for (int i = 0; i < unsignedBuffer.Length; i++)
                            {
                                ushort v = unsignedBuffer[i];
                                if (v < min) min = v;
                                if (v > max) max = v;
                            }
                            slice.MinPixelValue = min;
                            slice.MaxPixelValue = max;
                        }
                    }
                    else if (slice.BitsAllocated == 8)
                    {
                        var unsignedBuffer = new ushort[numPixels];
                        for (int i = 0; i < Math.Min(rawBytes.Length, numPixels); i++)
                        {
                            unsignedBuffer[i] = rawBytes[i];
                        }
                        slice.RawPixelsUnsigned = unsignedBuffer;
                        slice.MinPixelValue = 0;
                        slice.MaxPixelValue = 255;
                    }

                    if (slice.DefaultWindowWidth <= 1)
                    {
                        slice.DefaultWindowWidth = Math.Max(1, slice.MaxPixelValue - slice.MinPixelValue);
                        slice.DefaultWindowCenter = slice.MinPixelValue + slice.DefaultWindowWidth / 2.0;
                    }
                }
            }
            catch { }
        });
    }

    /// <summary>
    /// Loads the raw pixel data for a slice into RAM.
    /// Fast 16-bit or 8-bit memory copy.
    /// </summary>
    public static void LoadPixelData(DicomSliceModel slice)
    {
        if (slice.RawPixelsUnsigned != null || slice.RawPixelsSigned != null || slice.RawRgbPixels != null)
            return; // Already cached

        try
        {
            var dcm = DicomFile.Open(slice.FilePath);
            var ds = dcm.Dataset;
            if (!ds.Contains(DicomTag.PixelData))
            {
                slice.RawPixelsUnsigned = new ushort[Math.Max(1, slice.Rows * slice.Columns)];
                return;
            }

            if (ds.InternalTransferSyntax.IsEncapsulated)
            {
                try
                {
                    var transcoder = new DicomTranscoder(dcm.FileMetaInfo.TransferSyntax, DicomTransferSyntax.ExplicitVRLittleEndian);
                    dcm = transcoder.Transcode(dcm);
                    ds = dcm.Dataset;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Transcode Error] {ex.Message}");
                }
            }

            slice.BitsAllocated = ds.GetSingleValueOrDefault(DicomTag.BitsAllocated, slice.BitsAllocated);
            slice.PixelRepresentation = ds.GetSingleValueOrDefault(DicomTag.PixelRepresentation, slice.PixelRepresentation);

            var pixelData = DicomPixelData.Create(ds);
            var frameBuffer = pixelData.GetFrame(slice.FrameIndex);
            byte[] rawBytes = frameBuffer.Data;

            int numPixels = slice.Rows * slice.Columns;
            if (numPixels <= 0) return;

            if (slice.PhotometricInterpretation == "RGB" || slice.PhotometricInterpretation == "YBR_FULL")
            {
                slice.RawRgbPixels = rawBytes;
                return;
            }

            if (slice.BitsAllocated == 16)
            {
                if (slice.PixelRepresentation == 1) // Signed
                {
                    var signedBuffer = new short[numPixels];
                    Buffer.BlockCopy(rawBytes, 0, signedBuffer, 0, Math.Min(rawBytes.Length, numPixels * 2));
                    slice.RawPixelsSigned = signedBuffer;

                    int min = int.MaxValue;
                    int max = int.MinValue;
                    for (int i = 0; i < signedBuffer.Length; i++)
                    {
                        short v = signedBuffer[i];
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    slice.MinPixelValue = min;
                    slice.MaxPixelValue = max;
                }
                else // Unsigned
                {
                    var unsignedBuffer = new ushort[numPixels];
                    Buffer.BlockCopy(rawBytes, 0, unsignedBuffer, 0, Math.Min(rawBytes.Length, numPixels * 2));
                    slice.RawPixelsUnsigned = unsignedBuffer;

                    int min = int.MaxValue;
                    int max = int.MinValue;
                    for (int i = 0; i < unsignedBuffer.Length; i++)
                    {
                        ushort v = unsignedBuffer[i];
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    slice.MinPixelValue = min;
                    slice.MaxPixelValue = max;
                }
            }
            else if (slice.BitsAllocated == 8)
            {
                var unsignedBuffer = new ushort[numPixels];
                for (int i = 0; i < Math.Min(rawBytes.Length, numPixels); i++)
                {
                    unsignedBuffer[i] = rawBytes[i];
                }
                slice.RawPixelsUnsigned = unsignedBuffer;
                slice.MinPixelValue = 0;
                slice.MaxPixelValue = 255;
            }

            // If window settings are missing or degenerate, set defaults based on min/max
            if (slice.DefaultWindowWidth <= 1)
            {
                slice.DefaultWindowWidth = Math.Max(1, slice.MaxPixelValue - slice.MinPixelValue);
                slice.DefaultWindowCenter = slice.MinPixelValue + slice.DefaultWindowWidth / 2.0;
            }
        }
        catch
        {
            slice.RawPixelsUnsigned = new ushort[Math.Max(1, slice.Rows * slice.Columns)];
        }
    }

    public static System.Windows.Media.Imaging.BitmapSource? RenderThumbnail(DicomSliceModel slice)
    {
        try
        {
            LoadPixelData(slice);
            return PixelRenderer.RenderThumbnail(slice, slice.DefaultWindowCenter, slice.DefaultWindowWidth, 160);
        }
        catch { }
        return null;
    }

    public static void SortSlices(List<DicomSliceModel> slices)
    {
        if (slices.Count <= 1) return;

        // Try sorting by position along normal vector
        var first = slices[0];
        if (first.ImageOrientationPatient != null && first.ImageOrientationPatient.Length >= 6 &&
            slices.All(s => s.ImagePositionPatient != null && s.ImagePositionPatient.Length >= 3))
        {
            // Normal vector = rowVector x colVector
            double rx = first.ImageOrientationPatient[0];
            double ry = first.ImageOrientationPatient[1];
            double rz = first.ImageOrientationPatient[2];
            double cx = first.ImageOrientationPatient[3];
            double cy = first.ImageOrientationPatient[4];
            double cz = first.ImageOrientationPatient[5];

            double nx = ry * cz - rz * cy;
            double ny = rz * cx - rx * cz;
            double nz = rx * cy - ry * cx;

            slices.Sort((a, b) =>
            {
                double distA = a.ImagePositionPatient![0] * nx + a.ImagePositionPatient[1] * ny + a.ImagePositionPatient[2] * nz;
                double distB = b.ImagePositionPatient![0] * nx + b.ImagePositionPatient[1] * ny + b.ImagePositionPatient[2] * nz;
                int cmp = distA.CompareTo(distB);
                if (cmp != 0) return cmp;
                cmp = a.InstanceNumber.CompareTo(b.InstanceNumber);
                if (cmp != 0) return cmp;
                return a.FrameIndex.CompareTo(b.FrameIndex);
            });
            return;
        }

        // Fallback: Instance Number, then FrameIndex
        slices.Sort((a, b) =>
        {
            int cmp = a.InstanceNumber.CompareTo(b.InstanceNumber);
            if (cmp != 0) return cmp;
            return a.FrameIndex.CompareTo(b.FrameIndex);
        });
    }

    public static string CleanDicomString(string input) =>
        input.Replace("^", " ").Trim();

    public static string FormatDicomDate(string rawDate)
    {
        if (string.IsNullOrWhiteSpace(rawDate) || rawDate.Length < 8) return rawDate;
        // YYYYMMDD -> DD-MMM-YYYY
        if (DateTime.TryParseExact(rawDate, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
        {
            return dt.ToString("dd-MMM-yyyy");
        }
        return rawDate;
    }

    public static string FormatSpineCommentsSummary(IEnumerable<string?> rawComments)
    {
        var comments = rawComments
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct()
            .ToList();

        if (comments.Count == 0) return string.Empty;

        // Discs typically contain '/' or '-' (e.g. L4/L5, L5/S1, C5/C6, L4-L5)
        var discLevels = comments.Where(c => c.Contains('/') || c.Contains('-')).ToList();

        if (discLevels.Count == 1)
        {
            // Single disc packet (e.g. slices tagged [L5, L4/L5, L4] -> disc is L4-L5)
            return discLevels[0].Replace('/', '-');
        }

        if (discLevels.Count > 1)
        {
            var formatted = discLevels.Select(d => d.Replace('/', '-')).Distinct().ToList();
            if (formatted.Count <= 2)
            {
                return string.Join(", ", formatted);
            }
            return $"{formatted.First()} - {formatted.Last()}";
        }

        // Only single vertebrae (e.g. [L1, L2, L3] or [L4])
        if (comments.Count == 1)
        {
            return comments[0];
        }

        return $"{comments.First()} - {comments.Last()}";
    }

    private static double? ExtractDiffusionBValue(DicomDataset ds, DicomDataset? frameItem, DicomDataset? sharedItem, int frameIndex)
    {
        // 1. Standard DICOM tag (0018,9058)
        if (ds.Contains(DicomTag.DiffusionBValue))
        {
            try { return ds.GetSingleValueOrDefault(DicomTag.DiffusionBValue, 0.0); } catch { }
        }

        // 2. Functional groups sequence (Enhanced MR)
        DicomDataset? target = frameItem ?? sharedItem;
        if (target != null && target.Contains(DicomTag.MRDiffusionSequence))
        {
            var diffSeq = target.GetSequence(DicomTag.MRDiffusionSequence);
            if (diffSeq != null && diffSeq.Items.Count > 0)
            {
                return diffSeq.Items[0].GetSingleValueOrDefault(DicomTag.DiffusionBValue, 0.0);
            }
        }

        // 3. Siemens private tag (0019, 100C)
        var siemensBTag = new DicomTag(0x0019, 0x100C);
        if (ds.Contains(siemensBTag))
        {
            try
            {
                string sVal = ds.GetString(siemensBTag);
                if (double.TryParse(sVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double bVal))
                    return bVal;
            }
            catch { }
        }

        // 4. GE private tag (0043, 1039)
        var geBTag = new DicomTag(0x0043, 0x1039);
        if (ds.Contains(geBTag))
        {
            try
            {
                var vals = ds.GetValues<int>(geBTag);
                if (vals.Length > 0) return (double)(vals[0] % 100000);
            }
            catch { }
        }

        // 5. Sequence Name / Series Description regex heuristics (e.g. "_b50", "_b800")
        string desc = ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, "") + " " +
                      ds.GetSingleValueOrDefault(DicomTag.SequenceName, "") + " " +
                      ds.GetSingleValueOrDefault(DicomTag.ProtocolName, "");
        var match = System.Text.RegularExpressions.Regex.Match(desc, @"[_\s]b[=_]?(\d{1,5})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups[1].Value, out double parsedB))
        {
            return parsedB;
        }

        return null;
    }

    public static double ExtractDlpFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return 0.0;
        try
        {
            var dcm = DicomFile.Open(filePath, FileReadOption.ReadLargeOnDemand);
            return ExtractDlpFromDataset(dcm.Dataset);
        }
        catch
        {
            return 0.0;
        }
    }

    private static bool IsKnownDlpTag(DicomTag tag)
    {
        ushort g = tag.Group;
        ushort e = tag.Element;

        // Standard DICOM Total DLP (0018,9313)
        if (g == 0x0018 && e == 0x9313) return true;

        // Private tags: match group and lower byte of element (handles any PrivateCreator slot 0x10xx, 0x11xx, etc.)
        if (e >= 0x1000)
        {
            ushort low = (ushort)(e & 0x00FF);
            // Philips / Elscint (00E1,xx21) DLP
            if (g == 0x00E1 && low == 0x0021) return true;
            // Philips / Elscint (01E1,xx26/27/34)
            if (g == 0x01E1 && (low == 0x0026 || low == 0x0027 || low == 0x0034)) return true;
            // Philips (2005,140F)
            if (g == 0x2005 && low == 0x000F) return true;
            // Siemens / GE private dose tags
            if (g == 0x0029 && low == 0x0004) return true;
            if (g == 0x0045 && low == 0x0058) return true;
        }

        return false;
    }

    private static double TryReadDlpElement(DicomItem item)
    {
        if (item is not DicomElement elem || elem.Buffer == null || elem.Buffer.Size == 0)
            return 0.0;

        // 1. Parse directly from raw byte buffer (essential for Implicit VR Little Endian where private tags have VR=UN)
        try
        {
            byte[] bytes = elem.Buffer.Data;
            if (bytes != null && bytes.Length > 0 && bytes.Length <= 64)
            {
                int printable = 0;
                for (int i = 0; i < bytes.Length; i++)
                {
                    byte b = bytes[i];
                    if ((b >= '0' && b <= '9') || b == '.' || b == ',' || b == ' ' || b == '+' || b == '-' || b == '\0' || b == '\\')
                        printable++;
                }

                if (printable == bytes.Length)
                {
                    string s = System.Text.Encoding.ASCII.GetString(bytes).Trim('\0', ' ', '\r', '\n');
                    if (s.Contains('\\'))
                    {
                        s = s.Split('\\', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    }
                    if (double.TryParse(s.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double av) &&
                        double.IsFinite(av) && av > 0.0 && av < 100000.0)
                    {
                        return av;
                    }
                }

                if (bytes.Length == 8)
                {
                    double dv = BitConverter.ToDouble(bytes, 0);
                    if (double.IsFinite(dv) && dv > 0.1 && dv < 100000.0) return dv;
                }
                else if (bytes.Length == 4)
                {
                    float fv = BitConverter.ToSingle(bytes, 0);
                    if (float.IsFinite(fv) && fv > 0.1f && fv < 100000.0f) return fv;
                }
            }
        }
        catch { }

        // 2. Fallback to fo-dicom typed value / string access
        try
        {
            string s = elem.Get<string>(0);
            if (!string.IsNullOrWhiteSpace(s) &&
                double.TryParse(s.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sv) &&
                double.IsFinite(sv) && sv > 0.0 && sv < 100000.0)
            {
                return sv;
            }
        }
        catch { }

        try
        {
            double val = elem.Get<double>(0);
            if (double.IsFinite(val) && val > 0.0 && val < 100000.0) return val;
        }
        catch { }

        return 0.0;
    }

    private static double ExtractDlpFromDataset(DicomDataset ds)
    {
        double bestDlp = 0.0;

        // 1. Scan root-level dataset items for standard (0018,9313) and private DLP tags (e.g., Philips 00E1,1021)
        foreach (var item in ds)
        {
            if (item.Tag.Group > 0x2005) break; // Stop before PixelData (7FE0,0010)
            if (IsKnownDlpTag(item.Tag))
            {
                double val = TryReadDlpElement(item);
                if (val > bestDlp) bestDlp = val;
            }
        }

        // 2. Scan sequences / comments if series description indicates dose / report / summary / surview or if no DLP found yet
        string desc = ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, "");
        string mod = ds.GetSingleValueOrDefault(DicomTag.Modality, "");
        bool isDoseOrSr = mod == "SR" ||
                          desc.Contains("Dose", StringComparison.OrdinalIgnoreCase) ||
                          desc.Contains("Protocol", StringComparison.OrdinalIgnoreCase) ||
                          desc.Contains("Report", StringComparison.OrdinalIgnoreCase) ||
                          desc.Contains("Summary", StringComparison.OrdinalIgnoreCase) ||
                          desc.Contains("Surview", StringComparison.OrdinalIgnoreCase);

        if (isDoseOrSr || bestDlp <= 0.0)
        {
            double srDlp = SearchSequencesForDlp(ds);
            if (srDlp > bestDlp) bestDlp = srDlp;
        }

        if (isDoseOrSr && bestDlp <= 0.0)
        {
            var textTags = new[] {
                DicomTag.CommentsOnRadiationDose,
                DicomTag.ImageComments,
                DicomTag.ProtocolName,
                new DicomTag(0x0032, 0x4000) // StudyComments
            };
            foreach (var t in textTags)
            {
                if (ds.Contains(t))
                {
                    try
                    {
                        string txt = ds.GetString(t);
                        var match = System.Text.RegularExpressions.Regex.Match(txt, @"Total\s*DLP[:\s=]+([\d\.,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!match.Success)
                        {
                            match = System.Text.RegularExpressions.Regex.Match(txt, @"DLP[:\s=]+([\d\.,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        }
                        if (match.Success && double.TryParse(match.Groups[1].Value.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double tv))
                        {
                            if (tv > bestDlp) bestDlp = tv;
                        }
                    }
                    catch { }
                }
            }
        }

        return bestDlp;
    }

    private static double SearchSequencesForDlp(DicomDataset ds)
    {
        double bestVal = 0.0;
        double sumPrivateSeqDlp = 0.0;

        foreach (var item in ds)
        {
            if (item.Tag.Group > 0x2005) break;
            if (item is DicomSequence seq)
            {
                double seqSum = 0.0;
                foreach (var child in seq.Items)
                {
                    // Check private/standard DLP tags inside sequence items (e.g. ExposureDoseSequence 0040,030E)
                    foreach (var childItem in child)
                    {
                        if (IsKnownDlpTag(childItem.Tag))
                        {
                            double pv = TryReadDlpElement(childItem);
                            if (pv > 0.0)
                            {
                                if (pv > bestVal) bestVal = pv;
                                seqSum += pv;
                            }
                        }
                    }

                    string concept = "";
                    try
                    {
                        if (child.Contains(DicomTag.ConceptNameCodeSequence))
                        {
                            var cSeq = child.GetSequence(DicomTag.ConceptNameCodeSequence);
                            concept = cSeq.Items.FirstOrDefault()?.GetSingleValueOrDefault(DicomTag.CodeMeaning, "") ?? "";
                        }
                    }
                    catch { }

                    if (concept.Contains("DLP", StringComparison.OrdinalIgnoreCase) || concept.Contains("Dose Length Product", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (child.Contains(DicomTag.MeasuredValueSequence))
                            {
                                var mSeq = child.GetSequence(DicomTag.MeasuredValueSequence);
                                string numStr = mSeq.Items.FirstOrDefault()?.GetString(DicomTag.NumericValue) ?? "";
                                if (double.TryParse(numStr.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0)
                                {
                                    if (v > bestVal) bestVal = v;
                                }
                            }
                        }
                        catch { }
                    }

                    if (child.Contains(DicomTag.NumericValue))
                    {
                        try
                        {
                            string s = child.GetString(DicomTag.NumericValue);
                            if (double.TryParse(s.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double nv) && nv > 100 && nv < 50000)
                            {
                                if (concept.Contains("DLP", StringComparison.OrdinalIgnoreCase) && nv > bestVal)
                                    bestVal = nv;
                            }
                        }
                        catch { }
                    }

                    double sub = SearchSequencesForDlp(child);
                    if (sub > bestVal) bestVal = sub;
                }

                if (seqSum > sumPrivateSeqDlp)
                    sumPrivateSeqDlp = seqSum;
            }
        }

        return Math.Max(bestVal, sumPrivateSeqDlp);
    }
}
