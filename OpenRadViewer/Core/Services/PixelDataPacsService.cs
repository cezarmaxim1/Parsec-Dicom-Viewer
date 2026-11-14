// @purpose: PixelData Web PACS integration client for hospital web portals. The portal URL and login come only from the saved server settings.
// @depends: System.Net.Http, OpenRadViewer.Core.Services.PacsModels, System.Text.RegularExpressions
// @exports: PixelDataPacsService

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public class PixelDataPacsService
{
    private static readonly Lazy<PixelDataPacsService> _lazy = new(() => new PixelDataPacsService());
    public static PixelDataPacsService Instance => _lazy.Value;

    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new();
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private class SessionContext
    {
        public HttpClient Client { get; }
        public CookieContainer Cookies { get; }
        public DateTime LastAuthenticated { get; set; } = DateTime.MinValue;
        public string LastPageHtml { get; set; } = string.Empty;
        public Dictionary<string, string> LastFormData { get; set; } = new();

        public SessionContext()
        {
            Cookies = new CookieContainer();
            var handler = new SocketsHttpHandler
            {
                CookieContainer = Cookies,
                UseCookies = true,
                AllowAutoRedirect = true,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = delegate { return true; } // Bypass internal hospital self-signed cert issues
                },
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 20
            };

            Client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            Client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }
    }

    private SessionContext GetOrCreateSession(string serverId)
    {
        return _sessions.GetOrAdd(serverId, _ => new SessionContext());
    }

    /// <summary>
    /// Tests authentication and connection latency against the PixelData Web PACS portal.
    /// </summary>
    public async Task<(bool success, long? pingMs, string status)> TestConnectionAsync(PacsServerConfig server, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var session = GetOrCreateSession(server.Id);
            bool loggedIn = await EnsureAuthenticatedAsync(server, session, forceFresh: true, ct);
            sw.Stop();

            if (loggedIn)
            {
                return (true, sw.ElapsedMilliseconds, $"Online ({sw.ElapsedMilliseconds} ms)");
            }
            else
            {
                return (false, null, "Autentificare eșuată (Utilizator sau parolă incorecte)");
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            Debug.WriteLine($"[PixelDataPacs] TestConnection error: {ex.Message}");
            return (false, null, $"Eroare conexiune: {ex.Message}");
        }
    }

    /// <summary>
    /// Authenticates to Login.aspx if not already authenticated.
    /// </summary>
    private async Task<bool> EnsureAuthenticatedAsync(PacsServerConfig server, SessionContext session, bool forceFresh = false, CancellationToken ct = default)
    {
        if (!forceFresh && (DateTime.UtcNow - session.LastAuthenticated).TotalMinutes < 25 && !string.IsNullOrEmpty(session.LastPageHtml))
        {
            return true;
        }

        // No built-in portal or account: without a configured URL and login there is nothing to sign in to
        if (string.IsNullOrWhiteSpace(server.WebUrl) || string.IsNullOrWhiteSpace(server.WebUsername)) return false;
        string baseUrl = server.WebUrl.TrimEnd('/');
        string loginUrl = $"{baseUrl}/Login.aspx";

        // 1. GET Login.aspx to retrieve ViewState tokens
        var getReq = new HttpRequestMessage(HttpMethod.Get, loginUrl);
        var getResp = await session.Client.SendAsync(getReq, ct);
        if (!getResp.IsSuccessStatusCode)
        {
            return false;
        }

        string loginHtml = await getResp.Content.ReadAsStringAsync(ct);
        var formInputs = ExtractFormInputs(loginHtml);

        // 2. Populate login fields
        formInputs["UserName"] = server.WebUsername;
        formInputs["Password"] = server.WebPassword ?? string.Empty;
        formInputs["LoginButton"] = "Conectare";

        // 3. POST Login credentials
        var postReq = new HttpRequestMessage(HttpMethod.Post, loginUrl)
        {
            Content = new FormUrlEncodedContent(formInputs)
        };
        postReq.Headers.Referrer = new Uri(loginUrl);

        var postResp = await session.Client.SendAsync(postReq, ct);
        string postHtml = await postResp.Content.ReadAsStringAsync(ct);

        // If redirected to PatientHistory2.aspx or title contains PixelData PACS, login is successful
        if (postResp.RequestMessage?.RequestUri?.ToString().Contains("PatientHistory2") == true ||
            postHtml.Contains("gvPatients") ||
            postHtml.Contains("PACIENȚI CU STUDII"))
        {
            session.LastAuthenticated = DateTime.UtcNow;
            session.LastPageHtml = postHtml;
            session.LastFormData = ExtractFormInputs(postHtml);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Queries patient studies from the PixelData worklist (PatientHistory2.aspx).
    /// </summary>
    public async Task<List<PacsStudyResult>> QueryStudiesAsync(PacsServerConfig server, PacsSearchFilter filter, CancellationToken ct = default)
    {
        var results = new List<PacsStudyResult>();
        try
        {
            var session = GetOrCreateSession(server.Id);
            bool ok = await EnsureAuthenticatedAsync(server, session, forceFresh: false, ct);
            if (!ok)
            {
                throw new InvalidOperationException("Autentificarea la portalul PixelData a eșuat.");
            }

            string baseUrl = server.WebUrl.TrimEnd('/');
            string pageHtml = await ExecuteSearchPostbackAsync(baseUrl, session, filter, ct);

            results = ParsePatientsFromHtml(pageHtml, server);

            // The portal is searched by the first word only; the remaining words (first name) are checked here
            if (PacsSearchFilter.NameWords(filter.PatientName).Length > 1)
            {
                results = results.Where(r => PacsSearchFilter.NameContainsAllWords(r.PatientName, filter.PatientName)).ToList();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PixelDataPacs] QueryStudiesAsync error: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Executes a search postback on PatientHistory2.aspx with patient name, CNP, or date range.
    /// </summary>
    private async Task<string> ExecuteSearchPostbackAsync(string baseUrl, SessionContext session, PacsSearchFilter filter, CancellationToken ct)
    {
        string worklistUrl = $"{baseUrl}/PatientHistory2.aspx";
        var form = new Dictionary<string, string>(session.LastFormData);

        form["__EVENTTARGET"] = "Search";
        form["__EVENTARGUMENT"] = string.Empty;
        form["windowWidth"] = "1920";

        if (!string.IsNullOrWhiteSpace(filter.PatientName))
        {
            form["txtPatientName"] = PacsSearchFilter.NameWords(filter.PatientName).FirstOrDefault() ?? filter.PatientName.Trim();
        }
        else
        {
            form.Remove("txtPatientName");
        }

        if (!string.IsNullOrWhiteSpace(filter.PatientId))
        {
            form["txtPID"] = filter.PatientId.Trim();
        }
        else
        {
            form.Remove("txtPID");
        }

        if (filter.DateFrom.HasValue)
        {
            form["cbStudyFrom"] = "on";
            form["txtStudyFrom"] = filter.DateFrom.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        }

        if (filter.DateTo.HasValue)
        {
            form["cbStudyTo"] = "on";
            form["txtStudyTo"] = filter.DateTo.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        }

        var postReq = new HttpRequestMessage(HttpMethod.Post, worklistUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        postReq.Headers.Referrer = new Uri(worklistUrl);

        var resp = await session.Client.SendAsync(postReq, ct);
        string newHtml = await resp.Content.ReadAsStringAsync(ct);

        session.LastPageHtml = newHtml;
        session.LastFormData = ExtractFormInputs(newHtml);
        return newHtml;
    }

    /// <summary>
    /// Parses the gvPatients table into a list of PacsStudyResult items.
    /// </summary>
    private List<PacsStudyResult> ParsePatientsFromHtml(string html, PacsServerConfig server)
    {
        var list = new List<PacsStudyResult>();
        var tableMatch = Regex.Match(html, @"<table[^>]+id=[""']gvPatients[""'][^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!tableMatch.Success)
        {
            return list;
        }

        var rows = Regex.Matches(tableMatch.Groups[1].Value, @"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        for (int i = 0; i < rows.Count; i++)
        {
            var cellMatches = Regex.Matches(rows[i].Groups[1].Value, @"<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (cellMatches.Count < 10)
            {
                continue; // Header row or footer
            }

            var cells = cellMatches.Select(c => StripHtml(c.Groups[1].Value)).ToArray();

            // Row cell mapping from PixelData PACS PatientHistory2.aspx:
            // 0: Image button (skull icon: gvPatients$ctl02$ImageButtonView)
            // 1: Patient Name
            // 2: Patient ID (CNP)
            // 3: Study Date & Time
            // 4: Equipment / Modality
            // 5: Series Count
            // 6: Images Count
            // 7: Description
            // 8: Birth Date
            // 9: Referring Physician
            // 11: Internal StudyID
            // 14: StudyInstanceUID

            string patientName = cells.Length > 1 ? cells[1].Trim() : string.Empty;
            string patientId = cells.Length > 2 ? cells[2].Trim() : string.Empty;
            string studyDateTimeRaw = cells.Length > 3 ? cells[3].Trim() : string.Empty;
            string modalityRaw = cells.Length > 4 ? cells[4].Trim() : "CT";
            int.TryParse(cells.Length > 5 ? cells[5].Trim() : "0", out int seriesCount);
            int.TryParse(cells.Length > 6 ? cells[6].Trim() : "0", out int imageCount);
            string description = cells.Length > 7 ? cells[7].Trim() : string.Empty;
            string birthDateRaw = cells.Length > 8 ? cells[8].Trim() : string.Empty;
            string referringPhysician = cells.Length > 9 ? cells[9].Trim() : string.Empty;
            string studyInternalId = cells.Length > 11 ? cells[11].Trim() : string.Empty;
            bool isArchived = cells.Length > 12 && string.Equals(cells[12].Trim(), "DA", StringComparison.OrdinalIgnoreCase);
            string studyInstanceUid = cells.Length > 14 ? cells[14].Trim() : string.Empty;

            // Parse Date and Time (format: dd.MM.yyyy HH:mm:ss)
            string studyDate = string.Empty;
            string studyTime = string.Empty;
            if (DateTime.TryParseExact(studyDateTimeRaw, "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                studyDate = dt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                studyTime = dt.ToString("HHmmss", CultureInfo.InvariantCulture);
            }
            else
            {
                studyDate = studyDateTimeRaw.Replace(".", "").Replace("/", "");
            }

            // Parse birth date
            string birthDate = string.Empty;
            if (DateTime.TryParseExact(birthDateRaw, "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bdt))
            {
                birthDate = bdt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            }

            // Normalize modality: "CT/SR" -> "CT", "DX/SR" -> "DX"
            string modality = modalityRaw.Split('/')[0].Trim();

            // Extract row postback button name for skull icon click (e.g., "gvPatients$ctl02$ImageButtonView")
            string col0Html = cellMatches[0].Groups[1].Value;
            var btnMatch = Regex.Match(col0Html, @"name=[""']([^""']*ImageButtonView[^""']*)[""']", RegexOptions.IgnoreCase);
            if (!btnMatch.Success)
            {
                btnMatch = Regex.Match(col0Html, @"name=[""'](gvPatients\$ctl\d+\$[a-zA-Z0-9]+)[""']", RegexOptions.IgnoreCase);
            }
            string rowTarget = btnMatch.Success ? btnMatch.Groups[1].Value : string.Empty;

            var study = new PacsStudyResult
            {
                PatientName = patientName,
                PatientId = patientId,
                StudyDate = studyDate,
                StudyTime = studyTime,
                StudyDescription = isArchived && !description.Contains("[ARHIVAT]") ? $"{description} [ARHIVAT]".Trim() : description,
                BirthDate = birthDate,
                ReferringPhysician = referringPhysician,
                SeriesCount = seriesCount,
                InstanceCount = imageCount,
                StudyInstanceUid = studyInstanceUid,
                AccessionNumber = studyInternalId,
                ServerDescription = server.Description,
                ServerId = server.Id,
                Modality = modality
            };

            // Store row postback target and archive status in PacsComments
            study.PacsComments = isArchived ? $"{rowTarget}|ARCHIVED" : rowTarget;

            list.Add(study);
        }

        return list;
    }

    /// <summary>
    /// Downloads all series and slices of the selected study from the PixelData server directly into cache.
    /// Live-streams each slice into Parsec viewer as it is downloaded.
    /// </summary>
    public async Task<string> DownloadStudyAsync(
        PacsServerConfig server,
        PacsStudyResult study,
        int expectedTotalInstances = 0,
        IProgress<(int current, int total, string message)>? progress = null,
        CancellationToken ct = default,
        string? targetSeriesUid = null)
    {
        if (string.IsNullOrWhiteSpace(server.WebUrl))
            throw new InvalidOperationException("Portalul web nu are o adresă configurată. Completați adresa în PACS Settings.");
        string baseUrl = server.WebUrl.TrimEnd('/');
        var session = GetOrCreateSession(server.Id);
        await EnsureAuthenticatedAsync(server, session, forceFresh: false, ct);

        string studyDir = Path.Combine(PacsService.Instance.Settings.CacheDirectory, Sanitize(study.StudyInstanceUid));
        Directory.CreateDirectory(studyDir);

        progress?.Report((0, Math.Max(1, expectedTotalInstances), $"Inițializare sesiune pentru {study.DisplayPatientName}..."));

        // 1. Identify skull button to click
        string buttonName = study.PacsComments?.Split('|')[0] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(buttonName) || !buttonName.Contains("ImageButtonView"))
        {
            // Re-query to find button
            var filter = new PacsSearchFilter
            {
                PatientName = study.PatientName,
                PatientId = study.PatientId
            };
            string freshHtml = await ExecuteSearchPostbackAsync(baseUrl, session, filter, ct);
            var refreshedList = ParsePatientsFromHtml(freshHtml, server);
            var matched = refreshedList.FirstOrDefault(s =>
                (!string.IsNullOrEmpty(study.StudyInstanceUid) && s.StudyInstanceUid == study.StudyInstanceUid) ||
                (!string.IsNullOrEmpty(study.PatientId) && s.PatientId == study.PatientId));
            if (matched != null && !string.IsNullOrEmpty(matched.PacsComments))
            {
                buttonName = matched.PacsComments.Split('|')[0];
            }
        }

        if (string.IsNullOrEmpty(buttonName))
        {
            buttonName = "gvPatients$ctl02$ImageButtonView"; // Default fallback
        }

        // 2. Click skull button to open ViewerPacs.aspx?sid=...
        string worklistUrl = $"{baseUrl}/PatientHistory2.aspx";
        var form = new Dictionary<string, string>(session.LastFormData);
        form.Remove("__EVENTTARGET");
        form.Remove("__EVENTARGUMENT");
        form[buttonName + ".x"] = "15";
        form[buttonName + ".y"] = "15";

        var postReq = new HttpRequestMessage(HttpMethod.Post, worklistUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        postReq.Headers.Referrer = new Uri(worklistUrl);

        var viewerResp = await session.Client.SendAsync(postReq, ct);
        string viewerUrl = viewerResp.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
        string viewerHtml = await viewerResp.Content.ReadAsStringAsync(ct);

        // Extract active SID
        string activeSid = string.Empty;
        var sidMatch = Regex.Match(viewerUrl, @"sid=([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
        if (!sidMatch.Success)
        {
            // Also check response body for redirect location
            sidMatch = Regex.Match(viewerHtml, @"ViewerPacs\.aspx\?sid=([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
        }

        // If still no SID (stale ViewState or session expired), execute fresh targeted search and retry click once
        if (!sidMatch.Success)
        {
            var filter = new PacsSearchFilter
            {
                PatientName = study.PatientName,
                PatientId = study.PatientId
            };
            string freshHtml = await ExecuteSearchPostbackAsync(baseUrl, session, filter, ct);
            var refreshedList = ParsePatientsFromHtml(freshHtml, server);
            var matched = refreshedList.FirstOrDefault(s =>
                (!string.IsNullOrEmpty(study.StudyInstanceUid) && s.StudyInstanceUid == study.StudyInstanceUid) ||
                (!string.IsNullOrEmpty(study.PatientId) && s.PatientId == study.PatientId));
            if (matched != null && !string.IsNullOrEmpty(matched.PacsComments))
            {
                buttonName = matched.PacsComments.Split('|')[0];
            }

            var retryForm = new Dictionary<string, string>(session.LastFormData);
            retryForm.Remove("__EVENTTARGET");
            retryForm.Remove("__EVENTARGUMENT");
            retryForm[buttonName + ".x"] = "15";
            retryForm[buttonName + ".y"] = "15";

            var retryReq = new HttpRequestMessage(HttpMethod.Post, worklistUrl)
            {
                Content = new FormUrlEncodedContent(retryForm)
            };
            retryReq.Headers.Referrer = new Uri(worklistUrl);

            viewerResp = await session.Client.SendAsync(retryReq, ct);
            viewerUrl = viewerResp.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
            viewerHtml = await viewerResp.Content.ReadAsStringAsync(ct);

            sidMatch = Regex.Match(viewerUrl, @"sid=([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
            if (!sidMatch.Success)
            {
                sidMatch = Regex.Match(viewerHtml, @"ViewerPacs\.aspx\?sid=([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
            }
        }

        if (sidMatch.Success)
        {
            activeSid = sidMatch.Groups[1].Value;
        }

        if (string.IsNullOrEmpty(activeSid))
        {
            throw new InvalidOperationException($"Nu s-a putut deschide sesiunea de vizualizare pentru {study.DisplayPatientName}. Serverul web PixelData nu a răspuns.");
        }

        // 3. Extract series IDs from StudiesWithSeriesJSON in ViewerPacs.aspx
        var seriesIds = new List<string>();
        var swsMatch = Regex.Match(viewerHtml, @"var\s+StudiesWithSeriesJSON\s*=\s*(\[[^;]+\]);", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (swsMatch.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(swsMatch.Groups[1].Value);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var firstStudy = doc.RootElement[0];
                    if (firstStudy.TryGetProperty("SeriesIDs", out var sIdsProp) && sIdsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in sIdsProp.EnumerateArray())
                        {
                            seriesIds.Add(el.GetString() ?? string.Empty);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PixelDataPacs] Error parsing StudiesWithSeriesJSON: {ex.Message}");
            }
        }

        // 4. Try fast direct ZIP archive download first (10-50x faster + handles pre-packaged study archives)
        bool zipDownloaded = await TryDownloadStudyZipAsync(baseUrl, session, study, buttonName, seriesIds, studyDir, progress, ct);
        if (zipDownloaded)
        {
            return studyDir;
        }

        // 5. Fallback: Call api/images/CopySeries for each series to populate image files on server and get image URLs
        progress?.Report((0, Math.Max(1, expectedTotalInstances), "Generare imagini DICOM pe serverul PixelData..."));
        string copyUrl = $"{baseUrl}/api/images/CopySeries";

        var allFilesToDownload = new List<(string url, string localPath, string seriesUid)>();
        int seriesToCall = seriesIds.Count > 0 ? seriesIds.Count : Math.Max(1, study.SeriesCount);

        for (int i = 0; i < seriesToCall; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var copyReq = new HttpRequestMessage(HttpMethod.Post, copyUrl);
                copyReq.Headers.Referrer = new Uri($"{baseUrl}/ViewerPacs.aspx?sid={activeSid}");
                copyReq.Headers.Add("X-Requested-With", "XMLHttpRequest");

                var copyResp = await session.Client.SendAsync(copyReq, ct);
                if (copyResp.IsSuccessStatusCode)
                {
                    string jsonStr = await copyResp.Content.ReadAsStringAsync(ct);
                    using var jsonDoc = JsonDocument.Parse(jsonStr);

                    if (jsonDoc.RootElement.TryGetProperty("SerieImagesListJson", out var imgListProp))
                    {
                        string innerJson = imgListProp.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(innerJson))
                        {
                            using var innerDoc = JsonDocument.Parse(innerJson);
                            if (innerDoc.RootElement.TryGetProperty("seriesList", out var sListProp) &&
                                sListProp.ValueKind == JsonValueKind.Array && sListProp.GetArrayLength() > 0)
                            {
                                var seriesObj = sListProp[0];
                                string sUid = seriesObj.TryGetProperty("seriesUid", out var sUidProp) ? sUidProp.GetString() ?? string.Empty : string.Empty;

                                // Filter series if targetSeriesUid is specified
                                if (!string.IsNullOrWhiteSpace(targetSeriesUid) && sUid != targetSeriesUid)
                                {
                                    continue;
                                }

                                if (seriesObj.TryGetProperty("instanceList", out var instListProp) && instListProp.ValueKind == JsonValueKind.Array)
                                {
                                    string sDir = Path.Combine(studyDir, Sanitize(!string.IsNullOrEmpty(sUid) ? sUid : $"Series_{i + 1}"));
                                    Directory.CreateDirectory(sDir);

                                    foreach (var inst in instListProp.EnumerateArray())
                                    {
                                        if (inst.TryGetProperty("imageId", out var imgIdProp))
                                        {
                                            string imgId = imgIdProp.GetString() ?? string.Empty;
                                            if (!string.IsNullOrWhiteSpace(imgId))
                                            {
                                                string fullUrl = $"{baseUrl}/{imgId.TrimStart('/')}";
                                                string fileName = Path.GetFileName(imgId);
                                                string localPath = Path.Combine(sDir, fileName);
                                                allFilesToDownload.Add((fullUrl, localPath, sUid));
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PixelDataPacs] CopySeries call {i + 1} error: {ex.Message}");
            }
        }

        // 5. Fallback: if CopySeries returned 0 instances, discover directly from /ImagesD/{activeSid}/
        if (allFilesToDownload.Count == 0)
        {
            progress?.Report((0, Math.Max(1, expectedTotalInstances), "Scanare fișiere DICOM în directorul sesiunii..."));
            try
            {
                var seriesFolders = await GetSeriesFoldersAsync(baseUrl, activeSid, session.Client, ct);
                foreach (var sFolder in seriesFolders)
                {
                    if (!string.IsNullOrWhiteSpace(targetSeriesUid) && sFolder != targetSeriesUid)
                    {
                        continue;
                    }

                    string seriesUrl = $"{baseUrl}/ImagesD/{activeSid}/{sFolder}/";
                    var files = await GetDcmFilesInFolderAsync(seriesUrl, session.Client, ct);
                    string seriesLocalDir = Path.Combine(studyDir, Sanitize(sFolder));
                    Directory.CreateDirectory(seriesLocalDir);

                    foreach (var f in files)
                    {
                        string dcmFullUrl = $"{baseUrl}{f}";
                        string fileName = Path.GetFileName(f);
                        string localFilePath = Path.Combine(seriesLocalDir, fileName);
                        allFilesToDownload.Add((dcmFullUrl, localFilePath, sFolder));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PixelDataPacs] Direct folder fallback scan error: {ex.Message}");
            }
        }

        int totalFiles = allFilesToDownload.Count;
        if (totalFiles == 0)
        {
            bool wasArchived = study.PacsComments?.Contains("ARCHIVED") == true || study.StudyDescription?.Contains("[ARHIVAT]") == true;
            if (wasArchived)
            {
                throw new InvalidOperationException($"Investigația pentru {study.DisplayPatientName} este marcată ca ARHIVATĂ în PACS-ul spitalului. Feliile DICOM nu sunt stocate activ pe serverul web (necesită dezarhivare din arhiva rece a spitalului).");
            }

            throw new InvalidOperationException($"Nu au fost găsite imagini DICOM active pe server pentru {study.DisplayPatientName}. Verificați dacă investigația a fost procesată.");
        }

        progress?.Report((0, totalFiles, $"Se descarcă {totalFiles} imagini DICOM la viteză maximă..."));

        // 6. Download all .dcm files in parallel (up to 12 concurrent streams)
        int downloaded = 0;
        using var semaphore = new SemaphoreSlim(12);

        var downloadTasks = allFilesToDownload.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                if (File.Exists(item.localPath) && new FileInfo(item.localPath).Length > 1024)
                {
                    // Already in local cache: notify Parsec viewer directly
                    PacsService.Instance.RaiseInstanceReceived(study.StudyInstanceUid, item.seriesUid, item.localPath);
                    int c = Interlocked.Increment(ref downloaded);
                    if (c % 15 == 0 || c == totalFiles)
                    {
                        progress?.Report((c, totalFiles, $"Se încarcă din cache {c}/{totalFiles} imagini..."));
                    }
                    return;
                }

                byte[] data = await session.Client.GetByteArrayAsync(item.url, ct);
                await File.WriteAllBytesAsync(item.localPath, data, ct);

                // Notify Parsec live streaming loop
                PacsService.Instance.RaiseInstanceReceived(study.StudyInstanceUid, item.seriesUid, item.localPath);

                int count = Interlocked.Increment(ref downloaded);
                if (count % 10 == 0 || count == totalFiles)
                {
                    progress?.Report((count, totalFiles, $"Descărcat {count}/{totalFiles} felii DICOM..."));
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks);

        progress?.Report((totalFiles, totalFiles, "Descărcare finalizată cu succes!"));
        return studyDir;
    }

    /// <summary>
    /// Retrieves all series subdirectories under /ImagesD/{sid}/.
    /// </summary>
    private async Task<List<string>> GetSeriesFoldersAsync(string baseUrl, string sid, HttpClient client, CancellationToken ct)
    {
        var series = new List<string>();
        string url = $"{baseUrl}/ImagesD/{sid}/";
        string html = await client.GetStringAsync(url, ct);

        var matches = Regex.Matches(html, @"<A HREF=""/ImagesD/[^/]+/([^/]+)/"">", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            string folderName = m.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(folderName) && !folderName.Contains(".."))
            {
                series.Add(folderName);
            }
        }

        return series;
    }

    /// <summary>
    /// Scrapes all .dcm file links inside a series directory.
    /// </summary>
    private async Task<List<string>> GetDcmFilesInFolderAsync(string folderUrl, HttpClient client, CancellationToken ct)
    {
        var files = new List<string>();
        string html = await client.GetStringAsync(folderUrl, ct);

        var matches = Regex.Matches(html, @"<A HREF=""([^""]+\.dcm)""[^>]*>", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            files.Add(m.Groups[1].Value);
        }

        return files;
    }

    private static Dictionary<string, string> ExtractFormInputs(string html)
    {
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(html, @"<input[^>]+>", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            string tag = m.Value;
            var nameMatch = Regex.Match(tag, @"name=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!nameMatch.Success) continue;

            string name = nameMatch.Groups[1].Value;
            var valMatch = Regex.Match(tag, @"value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            string val = valMatch.Success ? valMatch.Groups[1].Value : string.Empty;

            var typeMatch = Regex.Match(tag, @"type=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            string type = typeMatch.Success ? typeMatch.Groups[1].Value.ToLowerInvariant() : "text";

            if (type is "submit" or "button" or "image") continue;
            if (type == "checkbox" && !tag.Contains("checked", StringComparison.OrdinalIgnoreCase)) continue;

            inputs[name] = val;
        }

        // Also extract selects
        var selectMatches = Regex.Matches(html, @"<select[^>]+name=[""']([^""']+)[""'][^>]*>(.*?)</select>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match sm in selectMatches)
        {
            string name = sm.Groups[1].Value;
            var optMatch = Regex.Match(sm.Groups[2].Value, @"<option[^>]+selected=[""']selected[""'][^>]*value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            if (!optMatch.Success)
            {
                optMatch = Regex.Match(sm.Groups[2].Value, @"<option[^>]*value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            }
            if (optMatch.Success)
            {
                inputs[name] = optMatch.Groups[1].Value;
            }
        }

        return inputs;
    }

    private static string StripHtml(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string clean = Regex.Replace(input, @"<[^>]+>", string.Empty);
        return HttpUtility.HtmlDecode(clean).Trim();
    }

    private static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "Study";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            input = input.Replace(c, '_');
        }
        return input;
    }
    /// <summary>
    /// Attempts to package and download the entire study as a single ZIP archive from PixelData,
    /// extracting it directly into the local study cache directory and notifying the viewer.
    /// Returns true if successful, or false to fall back to individual slice scraping.
    /// </summary>
    private async Task<bool> TryDownloadStudyZipAsync(
        string baseUrl,
        SessionContext session,
        PacsStudyResult study,
        string buttonName,
        List<string> viewerSeriesIds,
        string studyDir,
        IProgress<(int current, int total, string message)>? progress,
        CancellationToken ct)
    {
        try
        {
            progress?.Report((0, 100, $"Pregătire arhivă ZIP pentru {study.DisplayPatientName}..."));

            // 1. Determine grid row index from button name (e.g., ctl02 -> 0, ctl03 -> 1)
            int rowIndex = 0;
            var ctlMatch = Regex.Match(buttonName, @"ctl0*(\d+)", RegexOptions.IgnoreCase);
            if (ctlMatch.Success && int.TryParse(ctlMatch.Groups[1].Value, out int ctlNum))
            {
                rowIndex = Math.Max(0, ctlNum - 2);
            }

            // 2. Call DownloadStudyCallback on PatientHistory2.aspx to get cpSeries metadata
            string worklistUrl = $"{baseUrl}/PatientHistory2.aspx";
            var cbForm = new Dictionary<string, string>(session.LastFormData);
            cbForm["__CALLBACKID"] = "DownloadStudyCallback";
            cbForm["__CALLBACKPARAM"] = $"c0:{rowIndex}";

            var cbReq = new HttpRequestMessage(HttpMethod.Post, worklistUrl)
            {
                Content = new FormUrlEncodedContent(cbForm)
            };
            cbReq.Headers.Referrer = new Uri(worklistUrl);
            cbReq.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var cbResp = await session.Client.SendAsync(cbReq, ct);
            if (!cbResp.IsSuccessStatusCode)
            {
                return false;
            }

            string cbText = await cbResp.Content.ReadAsStringAsync(ct);
            var cpMatch = Regex.Match(cbText, @"'cpSeries':\s*(\{.+?\})\s*\}\s*\}", RegexOptions.Singleline);
            if (!cpMatch.Success)
            {
                cpMatch = Regex.Match(cbText, @"'cpSeries':\s*(\{.*?'SeriesIDs'.*?\})", RegexOptions.Singleline);
            }
            if (!cpMatch.Success)
            {
                return false;
            }

            string cpJson = cpMatch.Groups[1].Value.Replace("'", "\"");
            using var cpDoc = JsonDocument.Parse(cpJson);
            var root = cpDoc.RootElement;

            var seriesIds = new List<string>();
            if (root.TryGetProperty("SeriesIDs", out var sArr) && sArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in sArr.EnumerateArray())
                {
                    string id = el.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(id)) seriesIds.Add(id);
                }
            }

            if (seriesIds.Count == 0 && viewerSeriesIds.Count > 0)
            {
                seriesIds.AddRange(viewerSeriesIds);
            }

            if (seriesIds.Count == 0)
            {
                return false;
            }

            string studyId = root.TryGetProperty("StudyID", out var sIdProp) ? sIdProp.GetString() ?? string.Empty : study.AccessionNumber;
            string patientName = root.TryGetProperty("PatientName", out var pNameProp) ? pNameProp.GetString() ?? string.Empty : study.PatientName;

            // 3. Sequentially trigger MakeZip for each series
            string makeZipUrl = $"{baseUrl}/api/images/MakeZip";
            string fileUrl = string.Empty;
            string zipFileName = string.Empty;

            for (int i = 0; i < seriesIds.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                progress?.Report((i + 1, seriesIds.Count + 1, $"Generare pachet arhivă [{i + 1}/{seriesIds.Count}] pe server..."));

                var payload = new Dictionary<string, object?>
                {
                    ["StudyID"] = studyId,
                    ["EditorRights"] = null,
                    ["StudyInstanceUID"] = null,
                    ["SeriesIDs"] = seriesIds,
                    ["StudyDICOMTags"] = null,
                    ["PatientName"] = patientName,
                    ["CurrentSeriesIndex"] = i,
                    ["DownloadPerStudy"] = "1"
                };

                var zipReq = new HttpRequestMessage(HttpMethod.Post, makeZipUrl);
                zipReq.Headers.Referrer = new Uri(worklistUrl);
                zipReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
                zipReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var zipResp = await session.Client.SendAsync(zipReq, ct);
                if (zipResp.IsSuccessStatusCode)
                {
                    string respStr = await zipResp.Content.ReadAsStringAsync(ct);
                    if (!string.IsNullOrWhiteSpace(respStr) && respStr.Contains("fileURL"))
                    {
                        try
                        {
                            using var resDoc = JsonDocument.Parse(respStr);
                            if (resDoc.RootElement.TryGetProperty("fileURL", out var fUrlProp))
                            {
                                fileUrl = fUrlProp.GetString() ?? string.Empty;
                            }
                            if (resDoc.RootElement.TryGetProperty("fileName", out var fNameProp))
                            {
                                zipFileName = fNameProp.GetString() ?? string.Empty;
                            }
                        }
                        catch { }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(fileUrl))
            {
                return false;
            }

            // 4. Download the generated .ZIP file
            string fullZipDownloadUrl = $"{baseUrl}/{fileUrl.Replace('\\', '/').TrimStart('/')}";
            string localZipPath = Path.Combine(studyDir, Sanitize(!string.IsNullOrWhiteSpace(zipFileName) ? zipFileName : "study.zip"));

            progress?.Report((0, 100, $"Se descarcă arhiva medicală completă ({zipFileName})..."));

            using (var dlResp = await session.Client.GetAsync(fullZipDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!dlResp.IsSuccessStatusCode)
                {
                    return false;
                }

                long totalBytes = dlResp.Content.Headers.ContentLength ?? 0;
                long totalMB = totalBytes > 0 ? totalBytes / (1024 * 1024) : 0;

                using var sIn = await dlResp.Content.ReadAsStreamAsync(ct);
                using var sOut = new FileStream(localZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);

                byte[] buf = new byte[64 * 1024];
                long downloadedBytes = 0;
                int bytesRead;

                while ((bytesRead = await sIn.ReadAsync(buf, ct)) > 0)
                {
                    await sOut.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        int pct = (int)((downloadedBytes * 100) / totalBytes);
                        double dlMB = downloadedBytes / (1024.0 * 1024.0);
                        progress?.Report((pct, 100, $"Se descarcă arhiva: {dlMB:F1} / {totalMB} MB ({pct}%)..."));
                    }
                }
            }

            // 5. Check if archive is non-empty (> 1 KB)
            var fi = new FileInfo(localZipPath);
            if (!fi.Exists || fi.Length <= 1024)
            {
                try { if (File.Exists(localZipPath)) File.Delete(localZipPath); } catch { }
                return false;
            }

            // 6. Extract the archive directly into studyDir
            progress?.Report((50, 100, $"Dezarhivare automată {fi.Length / (1024 * 1024):F1} MB în cache..."));
            try
            {
                ZipFile.ExtractToDirectory(localZipPath, studyDir, overwriteFiles: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PixelDataPacs] ZipFile extraction error: {ex.Message}");
                await ArchiveExtractionService.ExtractArchiveAsync(localZipPath, null, ct);
            }

            try { File.Delete(localZipPath); } catch { }

            // 7. Enumerate extracted DICOM files and notify Parsec live streaming
            var extractedFiles = Directory.EnumerateFiles(studyDir, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (extractedFiles.Count == 0)
            {
                return false;
            }

            int count = 0;
            foreach (var filePath in extractedFiles)
            {
                string parentDir = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);
                string sUid = !string.IsNullOrWhiteSpace(parentDir) && parentDir != Path.GetFileName(studyDir)
                    ? parentDir
                    : "Series_1";

                PacsService.Instance.RaiseInstanceReceived(study.StudyInstanceUid, sUid, filePath);
                count++;
                if (count % 20 == 0 || count == extractedFiles.Count)
                {
                    progress?.Report((count, extractedFiles.Count, $"Încărcare în vizualizator {count}/{extractedFiles.Count} imagini..."));
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PixelDataPacs] TryDownloadStudyZipAsync error: {ex.Message}");
            return false;
        }
    }
}
