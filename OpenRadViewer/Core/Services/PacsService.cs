// @purpose: PACS communication engine: C-ECHO, C-FIND, C-MOVE, pipelined C-STORE, C-STORE SCP listener, ZIP download, and background auto-refresh.
// @depends: FellowOakDicom.Network, System.Net.Http, OpenRadViewer.Core.Models
// @exports: PacsService

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public class PacsService
{
    private static readonly Lazy<PacsService> _lazy = new(() => new PacsService());
    public static PacsService Instance => _lazy.Value;

    private readonly string _configFilePath;
    private readonly string _legacyConfigFilePath;
    private IDicomServer? _listenerServer;
    private readonly object _lock = new();
    private static readonly DicomTag TagStudyComments = new(0x0032, 0x4000);

    public PacsGlobalSettings Settings { get; private set; } = new();
    public bool IsListenerRunning => _listenerServer != null;
    public string ListenerStatusMessage { get; private set; } = "Oprit";

    public event Action<string, string, string>? InstanceReceived; // (studyUid, seriesUid, filePath)
    public event Action<string, string, string, int>? InstanceReceivedWithFrames; // (studyUid, seriesUid, filePath, frames)

    private PacsService()
    {
        string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parsec");
        try { Directory.CreateDirectory(appDataDir); } catch { }
        _configFilePath = Path.Combine(appDataDir, "pacs_config.json");
        _legacyConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pacs_config.json");
        LoadSettings();
        PacsCStoreScpService.OnFileStored += (study, series, file) =>
        {
            InstanceReceived?.Invoke(study, series, file);
        };
        PacsCStoreScpService.OnFileStoredWithFrames += (study, series, file, frames) =>
        {
            InstanceReceivedWithFrames?.Invoke(study, series, file, frames);
        };
    }

    #region Configuration Management

    public void LoadSettings()
    {
        lock (_lock)
        {
            try
            {
                string? pathToRead = null;
                if (File.Exists(_configFilePath))
                {
                    pathToRead = _configFilePath;
                }
                else if (File.Exists(_legacyConfigFilePath))
                {
                    pathToRead = _legacyConfigFilePath;
                }

                if (pathToRead != null)
                {
                    string json = File.ReadAllText(pathToRead);
                    var loaded = JsonSerializer.Deserialize<PacsGlobalSettings>(json);
                    if (loaded != null)
                    {
                        Settings = loaded;
                        if (pathToRead == _legacyConfigFilePath)
                        {
                            try { File.WriteAllText(_configFilePath, json); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PacsService] Error loading settings: {ex.Message}");
            }

            // If no servers configured, pre-populate with user's hospital PACS locations
            if (Settings.Servers == null || Settings.Servers.Count == 0)
            {
                Settings.Servers = new List<PacsServerConfig>
                {
                    new()
                    {
                        Description = "PXDPACS",
                        IpAddress = "192.168.94.20",
                        Port = 4020,
                        AeTitle = "PXDPACS",
                        RetrievalMode = "C-MOVE",
                        TransferSyntax = "Explicit VR Little Endian",
                        IsEnabled = true
                    },
                    new()
                    {
                        Description = "PXDPACS_CFR",
                        IpAddress = "192.168.155.171",
                        Port = 4020,
                        AeTitle = "PXDPACS",
                        RetrievalMode = "C-MOVE",
                        TransferSyntax = "Explicit VR Little Endian",
                        IsEnabled = true
                    }
                };
                Settings.ListenerPort = 46001;
                Settings.LocalAeTitle = "AE_REZIDENTI@@";
                SaveSettings();
            }
        }
    }

    public void SaveSettings()
    {
        lock (_lock)
        {
            try
            {
                string json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
                string? dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_configFilePath, json);
                try { File.WriteAllText(_legacyConfigFilePath, json); } catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PacsService] Error saving settings: {ex.Message}");
            }
        }
    }

    #endregion

    #region Cache Management

    public (long TotalBytes, int FileCount) GetCacheStats()
    {
        try
        {
            if (!Directory.Exists(Settings.CacheDirectory)) return (0, 0);
            var dirInfo = new DirectoryInfo(Settings.CacheDirectory);
            var files = dirInfo.GetFiles("*", SearchOption.AllDirectories);
            return (files.Sum(f => f.Length), files.Length);
        }
        catch
        {
            return (0, 0);
        }
    }

    public void ClearCache(int olderThanDays = 0)
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(Settings.CacheDirectory)) return;

                if (olderThanDays <= 0)
                {
                    var subdirs = Directory.GetDirectories(Settings.CacheDirectory);
                    foreach (var dir in subdirs)
                    {
                        DatabaseService.ForceDeleteDirectory(dir);
                    }
                    var rootFiles = Directory.GetFiles(Settings.CacheDirectory);
                    foreach (var f in rootFiles)
                    {
                        try
                        {
                            File.SetAttributes(f, FileAttributes.Normal);
                            File.Delete(f);
                        }
                        catch { }
                    }
                }
                else
                {
                    var cutoff = DateTime.Now.AddDays(-olderThanDays);
                    var subdirs = Directory.GetDirectories(Settings.CacheDirectory);
                    foreach (var dir in subdirs)
                    {
                        try
                        {
                            var dirInfo = new DirectoryInfo(dir);
                            if (dirInfo.LastWriteTime < cutoff)
                            {
                                DatabaseService.ForceDeleteDirectory(dir);
                            }
                        }
                        catch { }
                    }
                }

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PacsService] Error clearing cache: {ex.Message}");
            }
        }
    }

    #endregion

    #region Listener (C-STORE SCP)

    public bool StartListener(out string errorMessage)
    {
        lock (_lock)
        {
            errorMessage = string.Empty;
            if (_listenerServer != null)
            {
                ListenerStatusMessage = $"Active on port {Settings.ListenerPort} (AE: {Settings.LocalAeTitle})";
                return true;
            }

            try
            {
                Directory.CreateDirectory(Settings.CacheDirectory);
                _listenerServer = DicomServerFactory.Create<PacsCStoreScpService>(Settings.ListenerPort);
                ListenerStatusMessage = $"Active on port {Settings.ListenerPort} (AE: {Settings.LocalAeTitle})";
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Could not start C-STORE SCP on port {Settings.ListenerPort}: {ex.Message}";
                ListenerStatusMessage = $"Port {Settings.ListenerPort} error: {ex.Message}";
                Debug.WriteLine($"[PacsService] {errorMessage}");
                return false;
            }
        }
    }

    public void StopListener()
    {
        lock (_lock)
        {
            try
            {
                _listenerServer?.Dispose();
                _listenerServer = null;
                ListenerStatusMessage = "Stopped";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PacsService] Error stopping listener: {ex.Message}");
            }
        }
    }

    #endregion

    #region DICOM C-ECHO (Ping / Verification SCU)

    public async Task<(bool Success, long LatencyMs, string Message)> TestEchoAsync(PacsServerConfig server)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var client = DicomClientFactory.Create(server.IpAddress, server.Port, false, Settings.LocalAeTitle, server.AeTitle);
            client.NegotiateAsyncOps();

            var echoRequest = new DicomCEchoRequest();
            bool success = false;
            string statusDesc = string.Empty;

            echoRequest.OnResponseReceived += (req, res) =>
            {
                if (res.Status == DicomStatus.Success)
                {
                    success = true;
                    statusDesc = "Succes (200 OK)";
                }
                else
                {
                    statusDesc = $"Respins: {res.Status.Description} (Cod {res.Status.Code})";
                }
            };

            await client.AddRequestAsync(echoRequest);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(10, Settings.TimeoutSeconds)));
            await client.SendAsync(cts.Token);
            sw.Stop();

            if (success)
            {
                server.Status = $"Online ({sw.ElapsedMilliseconds} ms)";
                server.LastPingMs = sw.ElapsedMilliseconds;
                server.LastChecked = DateTime.Now;
                return (true, sw.ElapsedMilliseconds, $"Conexiune reușită în {sw.ElapsedMilliseconds} ms.");
            }
            else
            {
                server.Status = $"Eroare: {statusDesc}";
                server.LastChecked = DateTime.Now;
                return (false, sw.ElapsedMilliseconds, statusDesc);
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            string msg = "Timeout la conectare. Verificați IP-ul, portul și firewall-ul din rețeaua spitalului.";
            server.Status = "Timeout";
            server.LastChecked = DateTime.Now;
            return (false, sw.ElapsedMilliseconds, msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            string msg = $"Eroare de rețea: {ex.Message}";
            server.Status = "Eroare rețea";
            server.LastChecked = DateTime.Now;
            return (false, sw.ElapsedMilliseconds, msg);
        }
    }

    #endregion

    #region DICOM C-FIND (Query Studies & Series)

    public async Task<List<PacsStudyResult>> QueryStudiesAsync(PacsServerConfig server, PacsSearchFilter filter, CancellationToken ct = default)
    {
        var results = new List<PacsStudyResult>();
        try
        {
            var client = DicomClientFactory.Create(server.IpAddress, server.Port, false, Settings.LocalAeTitle, server.AeTitle);
            client.NegotiateAsyncOps();

            List<string> targetModalities = new();
            if (!string.IsNullOrWhiteSpace(filter.Modality) &&
                !filter.Modality.Equals("All", StringComparison.OrdinalIgnoreCase) &&
                !filter.Modality.Equals("Toate", StringComparison.OrdinalIgnoreCase))
            {
                targetModalities = filter.Modality
                    .Split(new[] { '/', '\\', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (targetModalities.Count == 0)
            {
                targetModalities.Add(string.Empty);
            }

            foreach (string reqMod in targetModalities)
            {
                // Study Root Query
                var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);

                // Return keys (empty tags instruct PACS to return these attributes)
                request.Dataset.AddOrUpdate(DicomTag.PatientName, string.IsNullOrWhiteSpace(filter.PatientName) ? string.Empty : $"*{filter.PatientName.Trim()}*");
                request.Dataset.AddOrUpdate(DicomTag.PatientID, string.IsNullOrWhiteSpace(filter.PatientId) ? string.Empty : $"*{filter.PatientId.Trim()}*");
                request.Dataset.AddOrUpdate(DicomTag.PatientBirthDate, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.PatientAge, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.AccessionNumber, string.IsNullOrWhiteSpace(filter.AccessionNumber) ? string.Empty : filter.AccessionNumber.Trim());
                request.Dataset.AddOrUpdate(DicomTag.StudyDescription, string.IsNullOrWhiteSpace(filter.StudyDescription) ? string.Empty : $"*{filter.StudyDescription.Trim()}*");
                request.Dataset.AddOrUpdate(TagStudyComments, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.PatientComments, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.StudyTime, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.NameOfPhysiciansReadingStudy, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.PerformingPhysicianName, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.NumberOfStudyRelatedSeries, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.NumberOfStudyRelatedInstances, string.Empty);

                // Modality query tag
                request.Dataset.AddOrUpdate(DicomTag.Modality, string.Empty);
                request.Dataset.AddOrUpdate(DicomTag.ModalitiesInStudy, reqMod);

                // Study Date Range
                if (filter.DateFrom.HasValue && filter.DateTo.HasValue)
                {
                    string fromStr = filter.DateFrom.Value.ToString("yyyyMMdd");
                    string toStr = filter.DateTo.Value.ToString("yyyyMMdd");
                    if (fromStr == toStr)
                    {
                        request.Dataset.AddOrUpdate(DicomTag.StudyDate, fromStr);
                    }
                    else
                    {
                        request.Dataset.AddOrUpdate(DicomTag.StudyDate, $"{fromStr}-{toStr}");
                    }
                }
                else if (filter.DateFrom.HasValue)
                {
                    request.Dataset.AddOrUpdate(DicomTag.StudyDate, $"{filter.DateFrom.Value:yyyyMMdd}-");
                }
                else if (filter.DateTo.HasValue)
                {
                    request.Dataset.AddOrUpdate(DicomTag.StudyDate, $"-{filter.DateTo.Value:yyyyMMdd}");
                }
                else
                {
                    request.Dataset.AddOrUpdate(DicomTag.StudyDate, string.Empty);
                }

                string queriedMod = reqMod;
                request.OnResponseReceived += (req, res) =>
                {
                    if (res.HasDataset)
                    {
                        var ds = res.Dataset;
                        string studyUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
                        if (string.IsNullOrWhiteSpace(studyUid)) return;

                        string modality = string.Empty;
                        if (ds.Contains(DicomTag.ModalitiesInStudy))
                        {
                            try { modality = string.Join("\\", ds.GetValues<string>(DicomTag.ModalitiesInStudy)); } catch { }
                        }
                        if (string.IsNullOrWhiteSpace(modality) && ds.Contains(DicomTag.Modality))
                        {
                            try { modality = string.Join("\\", ds.GetValues<string>(DicomTag.Modality)); } catch { }
                        }
                        if (string.IsNullOrWhiteSpace(modality))
                        {
                            modality = ds.GetSingleValueOrDefault(DicomTag.ModalitiesInStudy, ds.GetSingleValueOrDefault(DicomTag.Modality, queriedMod));
                        }

                        string studyComments = ds.GetSingleValueOrDefault(TagStudyComments, string.Empty);
                        bool isDicomReviewed = studyComments.Equals("REVIEWED", StringComparison.OrdinalIgnoreCase) ||
                                               studyComments.Contains("REVIEWED", StringComparison.OrdinalIgnoreCase);

                        // Clinical notes (ParsecPACS patient_comments): prefer PatientComments, fall back to StudyComments.
                        string patientComments = CleanClinicalComment(SafeGetString(ds, DicomTag.PatientComments));
                        if (string.IsNullOrWhiteSpace(patientComments))
                        {
                            patientComments = CleanClinicalComment(studyComments);
                        }

                        var item = new PacsStudyResult
                        {
                            StudyInstanceUid = studyUid,
                            PatientName = ds.GetSingleValueOrDefault(DicomTag.PatientName, "Anonymous"),
                            PatientId = ds.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
                            BirthDate = ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty),
                            PatientAge = ds.GetSingleValueOrDefault(DicomTag.PatientAge, string.Empty),
                            AccessionNumber = ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty),
                            StudyDate = ds.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty),
                            StudyTime = ds.GetSingleValueOrDefault(DicomTag.StudyTime, string.Empty),
                            Modality = modality,
                            StudyDescription = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty),
                            PatientComments = patientComments,
                            ReadingPhysician = GetCleanPersonName(ds, DicomTag.NameOfPhysiciansReadingStudy),
                            PerformingPhysician = GetCleanPersonName(ds, DicomTag.PerformingPhysicianName),
                            ReferringPhysician = GetCleanPersonName(ds, DicomTag.ReferringPhysicianName),
                            SeriesCount = ds.GetSingleValueOrDefault(DicomTag.NumberOfStudyRelatedSeries, 0),
                            InstanceCount = ds.GetSingleValueOrDefault(DicomTag.NumberOfStudyRelatedInstances, 0),
                            ServerDescription = server.Description,
                            ServerId = server.Id,
                            IsReviewed = isDicomReviewed || DatabaseService.IsStudyReviewed(studyUid)
                        };

                        if (isDicomReviewed)
                        {
                            DatabaseService.SetStudyReviewed(studyUid, true);
                        }

                        // Post-filter to ensure strict adherence to selected filters
                        if (!filter.Matches(item))
                        {
                            return;
                        }

                        lock (results)
                        {
                            if (!results.Any(r => r.StudyInstanceUid == item.StudyInstanceUid))
                            {
                                results.Add(item);
                            }
                        }
                    }
                };

                await client.AddRequestAsync(request);
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await client.SendAsync(linked.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PacsService] QueryStudies error: {ex.Message}");
            throw;
        }

        return results.OrderByDescending(r => r.SortDateTimeKey).ToList();
    }

    private static string GetCleanPersonName(DicomDataset ds, DicomTag tag)
    {
        if (!ds.Contains(tag)) return string.Empty;
        try
        {
            string raw = ds.GetString(tag) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            return string.Join(" ", raw.Replace("^", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            try
            {
                string val = ds.GetSingleValueOrDefault(tag, string.Empty);
                return string.Join(" ", val.Replace("^", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    private static string SafeGetString(DicomDataset ds, DicomTag tag)
    {
        if (!ds.Contains(tag)) return string.Empty;
        try { return ds.GetString(tag) ?? string.Empty; }
        catch
        {
            try { return ds.GetSingleValueOrDefault(tag, string.Empty); }
            catch { return string.Empty; }
        }
    }

    /// <summary>
    /// Normalizes a clinical note received from PACS: strips the legacy "REVIEWED" status marker
    /// (also transported in StudyComments), collapses whitespace/newlines and trims separators.
    /// </summary>
    internal static string CleanClinicalComment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = System.Text.RegularExpressions.Regex.Replace(raw, @"\bREVIEWED\b", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
        return s.Trim(' ', '|', ';', ',', '-', '\\', '/', ':', '\0');
    }

    public async Task<List<PacsSeriesResult>> QuerySeriesAsync(PacsServerConfig server, string studyInstanceUid, CancellationToken ct = default)
    {
        var results = new List<PacsSeriesResult>();
        try
        {
            var client = DicomClientFactory.Create(server.IpAddress, server.Port, false, Settings.LocalAeTitle, server.AeTitle);
            client.NegotiateAsyncOps();

            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Series);
            request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyInstanceUid);
            request.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.SeriesNumber, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.Modality, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.SeriesDescription, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.NumberOfSeriesRelatedInstances, string.Empty);

            request.OnResponseReceived += (req, res) =>
            {
                if (res.HasDataset)
                {
                    var ds = res.Dataset;
                    string sUid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
                    if (string.IsNullOrWhiteSpace(sUid)) return;

                    var item = new PacsSeriesResult
                    {
                        SeriesInstanceUid = sUid,
                        StudyInstanceUid = studyInstanceUid,
                        SeriesNumber = ds.GetSingleValueOrDefault(DicomTag.SeriesNumber, "1"),
                        Modality = ds.GetSingleValueOrDefault(DicomTag.Modality, string.Empty),
                        SeriesDescription = ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty),
                        InstanceCount = ds.GetSingleValueOrDefault(DicomTag.NumberOfSeriesRelatedInstances, 0)
                    };

                    lock (results)
                    {
                        if (!results.Any(r => r.SeriesInstanceUid == item.SeriesInstanceUid))
                        {
                            results.Add(item);
                        }
                    }
                }
            };

            await client.AddRequestAsync(request);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await client.SendAsync(linked.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PacsService] QuerySeries error: {ex.Message}");
        }

        return results.OrderBy(s => int.TryParse(s.SeriesNumber, out int n) ? n : 999).ToList();
    }

    #endregion

    #region DICOM C-MOVE / C-GET (Download Studies & Series)

    public async Task<string> DownloadStudyAsync(
        PacsServerConfig server,
        string studyInstanceUid,
        int expectedTotalInstances = 0,
        IProgress<(int current, int total, string message)>? progress = null,
        CancellationToken ct = default)
    {
        string studyDir = Path.Combine(Settings.CacheDirectory, Sanitize(studyInstanceUid));
        Directory.CreateDirectory(studyDir);

        // 1. Try fast HTTP ZIP download (ParsecPACS local/remote HTTP API)
        try
        {
            bool httpSuccess = await TryDownloadStudyViaHttpZipAsync(server, studyInstanceUid, studyDir, progress, ct);
            if (httpSuccess)
            {
                return studyDir;
            }
        }
        catch { }

        // 2. Fall back to standard DICOM C-MOVE / C-GET
        if (server.RetrievalMode == "C-GET")
        {
            try
            {
                await DownloadViaCGetAsync(server, studyInstanceUid, null, expectedTotalInstances, studyDir, progress, ct);
            }
            catch (Exception getEx)
            {
                Debug.WriteLine($"[PacsService] C-GET failed ({getEx.Message}), trying C-MOVE as fallback...");
                try
                {
                    progress?.Report((0, expectedTotalInstances, "C-GET unavailable, trying C-MOVE..."));
                    await DownloadViaCMoveAsync(server, studyInstanceUid, null, expectedTotalInstances, studyDir, progress, ct);
                }
                catch (Exception moveEx)
                {
                    throw new InvalidOperationException($"Study download from {server.Description} failed.\n\nC-GET error: {getEx.Message}\nC-MOVE error: {moveEx.Message}");
                }
            }
        }
        else
        {
            // Default: C-MOVE
            try
            {
                await DownloadViaCMoveAsync(server, studyInstanceUid, null, expectedTotalInstances, studyDir, progress, ct);
            }
            catch (Exception moveEx)
            {
                Debug.WriteLine($"[PacsService] C-MOVE failed ({moveEx.Message}), trying C-GET as fallback...");
                try
                {
                    progress?.Report((0, expectedTotalInstances, "C-MOVE unavailable, trying C-GET..."));
                    await DownloadViaCGetAsync(server, studyInstanceUid, null, expectedTotalInstances, studyDir, progress, ct);
                }
                catch (Exception getEx)
                {
                    throw new InvalidOperationException($"Study download from {server.Description} failed.\n\nC-MOVE error: {moveEx.Message}\nC-GET error: {getEx.Message}");
                }
            }
        }

        return studyDir;
    }

    public async Task<string> DownloadSeriesAsync(
        PacsServerConfig server,
        string studyInstanceUid,
        string seriesInstanceUid,
        int expectedTotalInstances = 0,
        IProgress<(int current, int total, string message)>? progress = null,
        CancellationToken ct = default)
    {
        string studyDir = Path.Combine(Settings.CacheDirectory, Sanitize(studyInstanceUid));
        string seriesDir = Path.Combine(studyDir, Sanitize(seriesInstanceUid));
        Directory.CreateDirectory(seriesDir);

        // 1. Try fast HTTP ZIP download (ParsecPACS local/remote HTTP API)
        try
        {
            bool httpSuccess = await TryDownloadSeriesViaHttpZipAsync(server, studyInstanceUid, seriesInstanceUid, seriesDir, progress, ct);
            if (httpSuccess)
            {
                return seriesDir;
            }
        }
        catch { }

        // 2. Fall back to standard DICOM C-MOVE / C-GET
        if (server.RetrievalMode == "C-GET")
        {
            try
            {
                await DownloadViaCGetAsync(server, studyInstanceUid, seriesInstanceUid, expectedTotalInstances, seriesDir, progress, ct);
            }
            catch (Exception getEx)
            {
                Debug.WriteLine($"[PacsService] C-GET failed ({getEx.Message}), trying C-MOVE as fallback...");
                try
                {
                    progress?.Report((0, expectedTotalInstances, "C-GET unavailable, trying C-MOVE..."));
                    await DownloadViaCMoveAsync(server, studyInstanceUid, seriesInstanceUid, expectedTotalInstances, seriesDir, progress, ct);
                }
                catch (Exception moveEx)
                {
                    throw new InvalidOperationException($"Series download from {server.Description} failed.\n\nC-GET error: {getEx.Message}\nC-MOVE error: {moveEx.Message}");
                }
            }
        }
        else
        {
            // C-MOVE
            try
            {
                await DownloadViaCMoveAsync(server, studyInstanceUid, seriesInstanceUid, expectedTotalInstances, seriesDir, progress, ct);
            }
            catch (Exception moveEx)
            {
                Debug.WriteLine($"[PacsService] C-MOVE failed ({moveEx.Message}), trying C-GET as fallback...");
                try
                {
                    progress?.Report((0, expectedTotalInstances, "C-MOVE unavailable, trying C-GET..."));
                    await DownloadViaCGetAsync(server, studyInstanceUid, seriesInstanceUid, expectedTotalInstances, seriesDir, progress, ct);
                }
                catch (Exception getEx)
                {
                    throw new InvalidOperationException($"Series download from {server.Description} failed.\n\nC-MOVE error: {moveEx.Message}\nC-GET error: {getEx.Message}");
                }
            }
        }

        return seriesDir;
    }

    private async Task<bool> TryDownloadStudyViaHttpZipAsync(
        PacsServerConfig server,
        string studyInstanceUid,
        string targetDir,
        IProgress<(int current, int total, string message)>? progress,
        CancellationToken ct)
    {
        string host = ParsecPacsSyncService.NormalizeHost(server.IpAddress);
        if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";

        string url = $"http://{host}:{ParsecPacsSyncService.ApiPort}/api/studies/{Uri.EscapeDataString(studyInstanceUid)}/download";
        string tempZip = Path.Combine(Path.GetTempPath(), $"parsec_study_{Guid.NewGuid():N}.zip");

        try
        {
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectCts.Token);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await ParsecPacsSyncService.DownloadClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            long? totalBytes = resp.Content.Headers.ContentLength;
            progress?.Report((0, 100, $"Descărcare rapidă HTTP ({server.Description})..."));

            using (var httpStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 1024 * 1024, useAsync: true))
            {
                byte[] copyBuffer = new byte[1024 * 1024]; // 1MB buffer
                int bytesRead;
                long downloadedBytes = 0;
                var sw = Stopwatch.StartNew();

                while ((bytesRead = await httpStream.ReadAsync(copyBuffer, 0, copyBuffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(copyBuffer, 0, bytesRead, ct).ConfigureAwait(false);
                    downloadedBytes += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double mb = downloadedBytes / (1024.0 * 1024.0);
                        double totalMb = totalBytes.Value / (1024.0 * 1024.0);
                        double mbps = sw.Elapsed.TotalSeconds > 0.2 ? mb / sw.Elapsed.TotalSeconds : 0;
                        int pct = (int)((downloadedBytes * 100) / totalBytes.Value);
                        progress?.Report((pct, 100, $"Descărcare HTTP ({mb:F1}/{totalMb:F1} MB, {mbps:F1} MB/s)..."));
                    }
                }
            }

            progress?.Report((0, 100, "Despachetare rapidă și indexare progresivă..."));

            using (var zipFs = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true))
            using (var archive = new ZipArchive(zipFs, ZipArchiveMode.Read))
            {
                var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && !e.Name.StartsWith(".")).ToList();
                int totalEntries = entries.Count;
                if (totalEntries == 0) return false;

                int extracted = 0;
                byte[] extractBuffer = new byte[256 * 1024]; // 256 KB buffer for fast extraction

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    string entryName = Path.GetFileName(entry.FullName);
                    string entrySubdir = Path.GetDirectoryName(entry.FullName) ?? "";
                    string targetFolder = string.IsNullOrWhiteSpace(entrySubdir)
                        ? targetDir
                        : Path.Combine(targetDir, Sanitize(entrySubdir));

                    Directory.CreateDirectory(targetFolder);

                    string destFile = Path.Combine(targetFolder, Sanitize(entryName));
                    if (!destFile.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase))
                    {
                        destFile += ".dcm";
                    }

                    try
                    {
                        using (var entryStream = entry.Open())
                        using (var outFs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 256 * 1024, useAsync: true))
                        {
                            int read;
                            while ((read = await entryStream.ReadAsync(extractBuffer, 0, extractBuffer.Length, ct).ConfigureAwait(false)) > 0)
                            {
                                await outFs.WriteAsync(extractBuffer, 0, read, ct).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (IOException) when (File.Exists(destFile) && new FileInfo(destFile).Length > 132)
                    {
                        // Slice file already exists on disk and is currently open in the viewer; keep existing valid file
                    }

                    extracted++;
                    progress?.Report((extracted, totalEntries, $"Despachetare: {extracted}/{totalEntries} fișiere..."));

                    string seriesUid = !string.IsNullOrWhiteSpace(entrySubdir) ? entrySubdir : "1";
                    InstanceReceived?.Invoke(studyInstanceUid, seriesUid, destFile);
                    InstanceReceivedWithFrames?.Invoke(studyInstanceUid, seriesUid, destFile, 1);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PacsService] HTTP ZIP download failed/unavailable ({ex.Message}), falling back to DICOM C-MOVE/C-GET.");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
            }
            catch { }
        }
    }

    private async Task<bool> TryDownloadSeriesViaHttpZipAsync(
        PacsServerConfig server,
        string studyInstanceUid,
        string seriesInstanceUid,
        string targetDir,
        IProgress<(int current, int total, string message)>? progress,
        CancellationToken ct)
    {
        string host = ParsecPacsSyncService.NormalizeHost(server.IpAddress);
        if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";

        string url = $"http://{host}:{ParsecPacsSyncService.ApiPort}/api/series/{Uri.EscapeDataString(seriesInstanceUid)}/download";
        string tempZip = Path.Combine(Path.GetTempPath(), $"parsec_series_{Guid.NewGuid():N}.zip");

        try
        {
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectCts.Token);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await ParsecPacsSyncService.DownloadClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            long? totalBytes = resp.Content.Headers.ContentLength;
            progress?.Report((0, 100, $"Descărcare rapidă serie HTTP ({server.Description})..."));

            using (var httpStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 1024 * 1024, useAsync: true))
            {
                byte[] copyBuffer = new byte[1024 * 1024]; // 1MB buffer
                int bytesRead;
                long downloadedBytes = 0;
                var sw = Stopwatch.StartNew();

                while ((bytesRead = await httpStream.ReadAsync(copyBuffer, 0, copyBuffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(copyBuffer, 0, bytesRead, ct).ConfigureAwait(false);
                    downloadedBytes += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double mb = downloadedBytes / (1024.0 * 1024.0);
                        double totalMb = totalBytes.Value / (1024.0 * 1024.0);
                        double mbps = sw.Elapsed.TotalSeconds > 0.2 ? mb / sw.Elapsed.TotalSeconds : 0;
                        int pct = (int)((downloadedBytes * 100) / totalBytes.Value);
                        progress?.Report((pct, 100, $"Descărcare serie HTTP ({mb:F1}/{totalMb:F1} MB, {mbps:F1} MB/s)..."));
                    }
                }
            }

            progress?.Report((0, 100, "Despachetare serie și indexare..."));

            using (var zipFs = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true))
            using (var archive = new ZipArchive(zipFs, ZipArchiveMode.Read))
            {
                var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && !e.Name.StartsWith(".")).ToList();
                int totalEntries = entries.Count;
                if (totalEntries == 0) return false;

                int extracted = 0;
                byte[] extractBuffer = new byte[256 * 1024]; // 256 KB buffer

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    string entryName = Path.GetFileName(entry.FullName);
                    Directory.CreateDirectory(targetDir);

                    string destFile = Path.Combine(targetDir, Sanitize(entryName));
                    if (!destFile.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase))
                    {
                        destFile += ".dcm";
                    }

                    try
                    {
                        using (var entryStream = entry.Open())
                        using (var outFs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 256 * 1024, useAsync: true))
                        {
                            int read;
                            while ((read = await entryStream.ReadAsync(extractBuffer, 0, extractBuffer.Length, ct).ConfigureAwait(false)) > 0)
                            {
                                await outFs.WriteAsync(extractBuffer, 0, read, ct).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (IOException) when (File.Exists(destFile) && new FileInfo(destFile).Length > 132)
                    {
                        // Slice file already exists on disk and is currently open in the viewer; keep existing valid file
                    }

                    extracted++;
                    progress?.Report((extracted, totalEntries, $"Despachetare: {extracted}/{totalEntries} fișiere..."));

                    InstanceReceived?.Invoke(studyInstanceUid, seriesInstanceUid, destFile);
                    InstanceReceivedWithFrames?.Invoke(studyInstanceUid, seriesInstanceUid, destFile, 1);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PacsService] HTTP series ZIP download failed/unavailable ({ex.Message}), falling back to DICOM C-MOVE/C-GET.");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
            }
            catch { }
        }
    }

    private async Task DownloadViaCMoveAsync(
        PacsServerConfig server,
        string studyInstanceUid,
        string? seriesInstanceUid,
        int expectedTotal,
        string targetDir,
        IProgress<(int current, int total, string message)>? progress,
        CancellationToken ct)
    {
        // 1. Ensure C-STORE listener is running
        if (!IsListenerRunning)
        {
            if (!StartListener(out string err))
            {
                throw new InvalidOperationException($"Cannot download via C-MOVE because the local receiver on port {Settings.ListenerPort} could not be started: {err}\n\nPlease ensure no other application is blocking port {Settings.ListenerPort}.");
            }
        }

        int receivedSliceCount = 0;
        int receivedFileCount = 0;
        int knownTotal = expectedTotal > 0 ? expectedTotal : 100;
        long totalBytes = 0;
        var sw = Stopwatch.StartNew();
        long lastReportTicks = 0;
        var lastReceiveSw = Stopwatch.StartNew();

        void OnFileReceived(string sUid, string serUid, string path, int numFrames)
        {
            if (sUid == studyInstanceUid)
            {
                int curSlices = Interlocked.Add(ref receivedSliceCount, Math.Max(1, numFrames));
                int curFiles = Interlocked.Increment(ref receivedFileCount);
                lastReceiveSw.Restart();
                try { Interlocked.Add(ref totalBytes, new FileInfo(path).Length); } catch { }
                double sec = sw.Elapsed.TotalSeconds;
                double ips = sec > 0.2 ? curSlices / sec : 0;
                double mbps = sec > 0.2 ? (totalBytes / (1024.0 * 1024.0)) / sec : 0;
                int totalToShow = Math.Max(curSlices, knownTotal);

                long now = Stopwatch.GetTimestamp();
                if (now - lastReportTicks >= Stopwatch.Frequency / 10 || curSlices >= totalToShow)
                {
                    lastReportTicks = now;
                    progress?.Report((curSlices, totalToShow, $"{curSlices}/{totalToShow} imagini ({ips:F1} img/s, {mbps:F1} MB/s)"));
                }
            }
        }

        InstanceReceivedWithFrames += OnFileReceived;

        try
        {
            var client = DicomClientFactory.Create(server.IpAddress, server.Port, false, Settings.LocalAeTitle, server.AeTitle);
            client.NegotiateAsyncOps();

            DicomCMoveRequest moveRequest;
            if (!string.IsNullOrWhiteSpace(seriesInstanceUid))
            {
                moveRequest = new DicomCMoveRequest(Settings.LocalAeTitle, studyInstanceUid, seriesInstanceUid);
            }
            else
            {
                moveRequest = new DicomCMoveRequest(Settings.LocalAeTitle, studyInstanceUid);
            }

            string? failureReason = null;
            moveRequest.OnResponseReceived += (req, res) =>
            {
                if (res.Status == DicomStatus.Pending || res.Status == DicomStatus.Success)
                {
                    int comp = res.Completed;
                    int rem = res.Remaining;
                    int total = comp + rem;
                    if (total > 0) knownTotal = total;
                }
                else if (res.Status == DicomStatus.QueryRetrieveMoveDestinationUnknown)
                {
                    failureReason = $"PACS server rejected C-MOVE because destination AE Title '{Settings.LocalAeTitle}' is not configured/recognized in PACS!";
                }
                else if (res.Status.State == DicomState.Failure)
                {
                    failureReason = $"C-MOVE was rejected by PACS: {res.Status.Description} (Code 0x{res.Status.Code:X4})";
                }
            };

            await client.AddRequestAsync(moveRequest);

            using var watchdogCts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

            _ = Task.Run(async () =>
            {
                while (!watchdogCts.Token.IsCancellationRequested && !linked.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, watchdogCts.Token); } catch { break; }
                    int curCount = receivedSliceCount > 0 ? receivedSliceCount : receivedFileCount;
                    if (curCount > 0 && lastReceiveSw.Elapsed.TotalSeconds >= 30)
                    {
                        Debug.WriteLine($"[PacsService] Inactivity timeout: got {curCount}/{knownTotal} images, finishing C-MOVE.");
                        linked.Cancel();
                        break;
                    }
                    if (curCount == 0 && lastReceiveSw.Elapsed.TotalSeconds >= 45)
                    {
                        Debug.WriteLine("[PacsService] Connection timeout: 0 images received after 45s.");
                        linked.Cancel();
                        break;
                    }
                }
            }, watchdogCts.Token);

            try
            {
                await client.SendAsync(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested && !ct.IsCancellationRequested && (receivedSliceCount > 0 || receivedFileCount > 0))
            {
                int c = receivedSliceCount > 0 ? receivedSliceCount : receivedFileCount;
                Debug.WriteLine($"[PacsService] C-MOVE concluded gracefully with {c} images.");
            }
            finally
            {
                watchdogCts.Cancel();
            }

            int finalCount = receivedSliceCount > 0 ? receivedSliceCount : receivedFileCount;
            if (!string.IsNullOrEmpty(failureReason) && finalCount == 0)
            {
                throw new InvalidOperationException(failureReason);
            }

            if (finalCount == 0)
            {
                throw new InvalidOperationException($"No images were received via C-MOVE from the PACS server.\n\nPossible causes:\n1. The PACS server must be configured to send images to port {Settings.ListenerPort} with AE Title '{Settings.LocalAeTitle}'.\n2. Verify that port {Settings.ListenerPort} is not blocked by Windows Firewall or another application.\n3. Verify network/VPN connectivity to the PACS host.");
            }

            double totalSec = sw.Elapsed.TotalSeconds;
            double avgIps = totalSec > 0 ? finalCount / totalSec : 0;
            double avgMbps = totalSec > 0 ? (totalBytes / (1024.0 * 1024.0)) / totalSec : 0;
            progress?.Report((finalCount, finalCount, $"Descărcare completă: {finalCount} imagini în {totalSec:F1}s ({avgIps:F1} img/s, {avgMbps:F1} MB/s)."));
        }
        finally
        {
            InstanceReceivedWithFrames -= OnFileReceived;
        }
    }

    private async Task DownloadViaCGetAsync(
        PacsServerConfig server,
        string studyInstanceUid,
        string? seriesInstanceUid,
        int expectedTotal,
        string targetDir,
        IProgress<(int current, int total, string message)>? progress,
        CancellationToken ct)
    {
        var client = DicomClientFactory.Create(server.IpAddress, server.Port, false, Settings.LocalAeTitle, server.AeTitle);
        client.NegotiateAsyncOps();

        // Register storage presentation contexts required for C-GET sub-operations
        var storageUids = new[]
        {
            DicomUID.CTImageStorage,
            DicomUID.EnhancedCTImageStorage,
            DicomUID.MRImageStorage,
            DicomUID.EnhancedMRImageStorage,
            DicomUID.SecondaryCaptureImageStorage,
            DicomUID.MultiFrameGrayscaleByteSecondaryCaptureImageStorage,
            DicomUID.MultiFrameGrayscaleWordSecondaryCaptureImageStorage,
            DicomUID.UltrasoundImageStorage,
            DicomUID.UltrasoundMultiFrameImageStorage,
            DicomUID.XRayAngiographicImageStorage,
            DicomUID.DigitalXRayImageStorageForPresentation,
            DicomUID.DigitalMammographyXRayImageStorageForPresentation,
            DicomUID.ComputedRadiographyImageStorage,
            DicomUID.PositronEmissionTomographyImageStorage,
            DicomUID.NuclearMedicineImageStorage
        };

        foreach (var uid in storageUids)
        {
            var pc = new DicomPresentationContext(0, uid, userRole: null, providerRole: true);
            pc.AddTransferSyntax(DicomTransferSyntax.ExplicitVRLittleEndian);
            pc.AddTransferSyntax(DicomTransferSyntax.ImplicitVRLittleEndian);
            pc.AddTransferSyntax(DicomTransferSyntax.JPEGProcess1);
            pc.AddTransferSyntax(DicomTransferSyntax.JPEGProcess2_4);
            pc.AddTransferSyntax(DicomTransferSyntax.JPEGLSLossless);
            pc.AddTransferSyntax(DicomTransferSyntax.JPEG2000Lossless);
            pc.AddTransferSyntax(DicomTransferSyntax.RLELossless);
            client.AdditionalPresentationContexts.Add(pc);
        }

        int receivedSliceCount = 0;
        int receivedFileCount = 0;
        int knownTotal = expectedTotal > 0 ? expectedTotal : 100;
        long totalBytes = 0;
        var sw = Stopwatch.StartNew();
        long lastReportTicks = 0;
        var lastReceiveSw = Stopwatch.StartNew();
        var createdDirs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        client.OnCStoreRequest += async (req) =>
        {
            try
            {
                var ds = req.Dataset;
                string sUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, studyInstanceUid);
                string serUid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, seriesInstanceUid ?? "1");
                string sopUid = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, Guid.NewGuid().ToString());
                int numFrames = ds.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);

                string destDir = string.IsNullOrWhiteSpace(seriesInstanceUid)
                    ? Path.Combine(targetDir, Sanitize(serUid))
                    : targetDir;

                if (createdDirs.TryAdd(destDir, 1))
                {
                    Directory.CreateDirectory(destDir);
                }

                string filePath = Path.Combine(destDir, $"{Sanitize(sopUid)}.dcm");
                try
                {
                    var file = new DicomFile(ds);
                    await file.SaveAsync(filePath);
                }
                catch (IOException) when (File.Exists(filePath) && new FileInfo(filePath).Length > 132)
                {
                    // Slice file already exists on disk and is currently open in the viewer; keep existing valid file
                }

                int curSlices = Interlocked.Add(ref receivedSliceCount, Math.Max(1, numFrames));
                int curFiles = Interlocked.Increment(ref receivedFileCount);
                lastReceiveSw.Restart();
                try { Interlocked.Add(ref totalBytes, new FileInfo(filePath).Length); } catch { }

                double sec = sw.Elapsed.TotalSeconds;
                double ips = sec > 0.2 ? curSlices / sec : 0;
                double mbps = sec > 0.2 ? (totalBytes / (1024.0 * 1024.0)) / sec : 0;
                int totalToShow = Math.Max(curSlices, knownTotal);

                long now = Stopwatch.GetTimestamp();
                if (now - lastReportTicks >= Stopwatch.Frequency / 10 || curSlices >= totalToShow)
                {
                    lastReportTicks = now;
                    progress?.Report((curSlices, totalToShow, $"{curSlices}/{totalToShow} imagini ({ips:F1} img/s, {mbps:F1} MB/s)"));
                }

                InstanceReceived?.Invoke(sUid, serUid, filePath);
                InstanceReceivedWithFrames?.Invoke(sUid, serUid, filePath, numFrames);

                return new DicomCStoreResponse(req, DicomStatus.Success);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PacsService] C-GET OnCStoreRequest error: {ex.Message}");
                return new DicomCStoreResponse(req, DicomStatus.ProcessingFailure);
            }
        };

        DicomCGetRequest getRequest;
        if (!string.IsNullOrWhiteSpace(seriesInstanceUid))
        {
            getRequest = new DicomCGetRequest(studyInstanceUid, seriesInstanceUid);
        }
        else
        {
            getRequest = new DicomCGetRequest(studyInstanceUid);
        }

        string? getFailure = null;
        getRequest.OnResponseReceived += (req, res) =>
        {
            if (res.Status == DicomStatus.Pending || res.Status == DicomStatus.Success)
            {
                int comp = res.Completed;
                int rem = res.Remaining;
                int total = comp + rem;
                if (total > 0)
                {
                    knownTotal = Math.Max(knownTotal, total);
                }
            }
            else if (res.Status.State == DicomState.Failure)
            {
                getFailure = $"C-GET was rejected by server: {res.Status.Description} (Code 0x{res.Status.Code:X4})";
            }
        };

        await client.AddRequestAsync(getRequest);

        using var watchdogCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _ = Task.Run(async () =>
        {
            while (!watchdogCts.Token.IsCancellationRequested && !linked.IsCancellationRequested)
            {
                try { await Task.Delay(1000, watchdogCts.Token); } catch { break; }
                int curCount = receivedSliceCount > 0 ? receivedSliceCount : receivedFileCount;
                if (curCount > 0 && lastReceiveSw.Elapsed.TotalSeconds >= 30)
                {
                    Debug.WriteLine($"[PacsService] Inactivity timeout: received {curCount}/{knownTotal} images, concluding C-GET gracefully.");
                    linked.Cancel();
                    break;
                }
                if (curCount == 0 && lastReceiveSw.Elapsed.TotalSeconds >= 45)
                {
                    Debug.WriteLine("[PacsService] Connection timeout: 0 images received after 45s.");
                    linked.Cancel();
                    break;
                }
            }
        }, watchdogCts.Token);

        try
        {
            await client.SendAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !ct.IsCancellationRequested && (receivedSliceCount > 0 || receivedFileCount > 0))
        {
            int c = receivedSliceCount > 0 ? receivedSliceCount : receivedFileCount;
            Debug.WriteLine($"[PacsService] C-GET concluded gracefully with {c} images.");
        }
        finally
        {
            watchdogCts.Cancel();
        }

        if (!string.IsNullOrEmpty(getFailure))
        {
            throw new InvalidOperationException(getFailure);
        }

        int finalCount = receivedSliceCount > 0 ? receivedSliceCount : receivedFileCount;
        if (finalCount == 0)
        {
            throw new InvalidOperationException("The server did not send any images via C-GET.");
        }

        double getSec = sw.Elapsed.TotalSeconds;
        double avgIps = getSec > 0 ? finalCount / getSec : 0;
        double avgMbps = getSec > 0 ? (totalBytes / (1024.0 * 1024.0)) / getSec : 0;
        progress?.Report((finalCount, finalCount, $"Descărcare C-GET completă: {finalCount} imagini în {getSec:F1}s ({avgIps:F1} img/s, {avgMbps:F1} MB/s)."));
    }

    #endregion

    #region DICOM C-STORE SCU (Send / Upload to PACS)

    public async Task<(int successCount, int failedCount, string message)> SendFilesToPacsAsync(
        PacsServerConfig server,
        IEnumerable<string> filePaths,
        IProgress<(int current, int total, string message)>? progress = null,
        CancellationToken ct = default)
    {
        var validFiles = filePaths.Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f)).ToList();
        if (validFiles.Count == 0)
        {
            return (0, 0, "No valid DICOM files to send.");
        }

        int success = 0;
        int failed = 0;
        int total = validFiles.Count;

        try
        {
            var client = DicomClientFactory.Create(server.IpAddress, server.Port, false, Settings.LocalAeTitle, server.AeTitle);

            // Optimization: Single association for all files with unlimited PDU length & async pipeline
            client.ServiceOptions.MaxPDULength = 0; // 0 = unlimited in DICOM protocol
            client.ServiceOptions.TcpNoDelay = true;
            client.ServiceOptions.TcpSendBufferSize = 1024 * 1024;
            client.ServiceOptions.TcpReceiveBufferSize = 1024 * 1024;
            client.ServiceOptions.MaxCommandBuffer = 131072;
            client.ServiceOptions.MaxDataBuffer = 1024 * 1024;
            client.ClientOptions.MaximumNumberOfRequestsPerAssociation = null; // Single association for all files

            // Pipeline 3-4 concurrent requests on the same association
            client.NegotiateAsyncOps(invoked: 4, performed: 4);

            foreach (var file in validFiles)
            {
                ct.ThrowIfCancellationRequested();
                var req = new DicomCStoreRequest(file);
                req.OnResponseReceived += (r, res) =>
                {
                    if (res.Status == DicomStatus.Success)
                    {
                        Interlocked.Increment(ref success);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                    }
                    int current = success + failed;
                    progress?.Report((current, total, $"Sending to PACS: {current}/{total} (Failed: {failed})"));
                };

                await client.AddRequestAsync(req);
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await client.SendAsync(linked.Token);

            string summary = failed == 0
                ? $"All {success} images were successfully transferred to {server.Description} ({server.AeTitle})."
                : $"Transfer completed: {success} sent successfully, {failed} failed.";

            return (success, failed, summary);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PacsService] SendFilesToPacs error: {ex.Message}");
            return (success, failed, $"PACS transfer error: {ex.Message}");
        }
    }

    public async Task<(int successCount, int failedCount, string message)> SendStudyToPacsAsync(
        PacsServerConfig server,
        StudyModel study,
        IProgress<(int current, int total, string message)>? progress = null,
        CancellationToken ct = default)
    {
        var allFiles = study.Series
            .SelectMany(s => s.Slices)
            .Select(sl => sl.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct()
            .ToList();

        return await SendFilesToPacsAsync(server, allFiles, progress, ct);
    }

    public async Task<(int successCount, int failedCount, string message)> SendSeriesToPacsAsync(
        PacsServerConfig server,
        SeriesModel series,
        IProgress<(int current, int total, string message)>? progress = null,
        CancellationToken ct = default)
    {
        var files = series.Slices
            .Select(sl => sl.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct()
            .ToList();

        return await SendFilesToPacsAsync(server, files, progress, ct);
    }

    #endregion

    public static string Sanitize(string input)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            input = input.Replace(c, '_');
        }
        return input;
    }
}
