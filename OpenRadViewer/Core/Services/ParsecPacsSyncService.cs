// @purpose: Non-blocking HTTP client for ParsecPACS REST API (port 8042) to synchronize "Reviewed" status and notes bidirectionally.
// @depends: System.Net.Http, System.Text.Json, OpenRadViewer.Core.Services.DatabaseService
// @exports: ParsecPacsSyncService, NoteSaveStatus

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRadViewer.Core.Services;

public enum NoteSaveStatus
{
    Unchanged,
    SavedInPacs,
    NotInPacs,
    PacsUnreachable,
    LocalSaveFailed
}

/// <summary>
/// Optional, best-effort bidirectional sync of the "Reviewed" marker with a ParsecPACS
/// HTTP API (default port 8042). Servers that do not expose this API (e.g. PXD-PACS or other
/// clinic PACS) are silently ignored: every call has a short timeout, never throws, and a host
/// that fails is temporarily skipped so it never slows down DICOM C-FIND / C-MOVE traffic.
/// The local reviewed marker (DatabaseService) always remains the source of truth in Parsec.
/// </summary>
public static class ParsecPacsSyncService
{
    public const int ApiPort = 8042;
    private const string LocalHost = "127.0.0.1";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan HostBackoff = TimeSpan.FromMinutes(2);

    private static readonly HttpClient _http = new() { Timeout = RequestTimeout };

    /// <summary>
    /// Dedicated HttpClient for downloading large ZIP archives with a longer timeout.
    /// </summary>
    public static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    // host -> time until which the host is considered unavailable (no API / unreachable)
    private static readonly ConcurrentDictionary<string, DateTime> _unavailableUntil = new(StringComparer.OrdinalIgnoreCase);

    private sealed class ReviewedListResponse
    {
        [JsonPropertyName("reviewed_uids")]
        public List<string>? ReviewedUids { get; set; }
    }

    /// <summary>
    /// Returns the candidate API hosts: always the local ParsecPACS (127.0.0.1) plus the IPs
    /// of the given PACS servers (duplicates removed).
    /// </summary>
    public static List<string> GetCandidateHosts(IEnumerable<PacsServerConfig>? servers)
    {
        var hosts = new List<string> { LocalHost };
        if (servers != null)
        {
            foreach (var s in servers)
            {
                string ip = (s?.IpAddress ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(ip)) continue;
                if (IsLoopback(ip)) ip = LocalHost;
                if (!hosts.Contains(ip, StringComparer.OrdinalIgnoreCase)) hosts.Add(ip);
            }
        }
        return hosts;
    }

    /// <summary>
    /// Fetches reviewed Study Instance UIDs from all reachable hosts (in parallel).
    /// Reads from /api/reviewed-studies and /api/studies (is_reviewed / StudyComments == "REVIEWED").
    /// Never throws; unreachable hosts are skipped and temporarily backed off.
    /// </summary>
    public static async Task<Dictionary<string, HashSet<string>>> FetchReviewedUidsAsync(IEnumerable<string> hosts, CancellationToken ct = default)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var tasks = hosts.Where(IsHostAvailable).Select(async host =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeout);

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool anyEndpointSucceeded = false;

                // 1. Try /api/reviewed-studies
                try
                {
                    using var resp = await _http.GetAsync($"http://{host}:{ApiPort}/api/reviewed-studies", cts.Token).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                        var parsed = JsonSerializer.Deserialize<ReviewedListResponse>(json);
                        if (parsed?.ReviewedUids != null)
                        {
                            anyEndpointSucceeded = true;
                            foreach (var u in parsed.ReviewedUids)
                            {
                                if (!string.IsNullOrWhiteSpace(u)) set.Add(u.Trim());
                            }
                        }
                    }
                }
                catch { }

                // 2. Also check /api/studies if available (reads is_reviewed or StudyComments == "REVIEWED")
                try
                {
                    using var resp2 = await _http.GetAsync($"http://{host}:{ApiPort}/api/studies", cts.Token).ConfigureAwait(false);
                    if (resp2.IsSuccessStatusCode)
                    {
                        string json2 = await resp2.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json2);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            anyEndpointSucceeded = true;
                            foreach (var item in doc.RootElement.EnumerateArray())
                            {
                                string? uid = null;
                                if (item.TryGetProperty("study_instance_uid", out var pUid) ||
                                    item.TryGetProperty("study_uid", out pUid) ||
                                    item.TryGetProperty("StudyInstanceUID", out pUid) ||
                                    item.TryGetProperty("uid", out pUid))
                                {
                                    uid = pUid.GetString();
                                }

                                if (string.IsNullOrWhiteSpace(uid)) continue;

                                bool isRev = false;
                                if (item.TryGetProperty("is_reviewed", out var rProp))
                                {
                                    if (rProp.ValueKind == JsonValueKind.Number && rProp.TryGetInt32(out int n)) isRev = (n != 0);
                                    else if (rProp.ValueKind == JsonValueKind.True) isRev = true;
                                }
                                else if (item.TryGetProperty("StudyComments", out var cProp) || item.TryGetProperty("study_comments", out cProp))
                                {
                                    string? sc = cProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(sc) && (sc.Equals("REVIEWED", StringComparison.OrdinalIgnoreCase) || sc.Contains("REVIEWED", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        isRev = true;
                                    }
                                }

                                if (isRev)
                                {
                                    set.Add(uid.Trim());
                                }
                            }
                        }
                    }
                }
                catch { }

                if (!anyEndpointSucceeded)
                {
                    MarkUnavailable(host);
                    return (host, (HashSet<string>?)null);
                }

                MarkAvailable(host);
                return (host, (HashSet<string>?)set);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ParsecPacsSync] GET reviewed-studies failed on {host}: {ex.Message}");
                MarkUnavailable(host);
                return (host, (HashSet<string>?)null);
            }
        }).ToList();

        try
        {
            var all = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var (host, set) in all)
                if (set != null) result[host] = set;
        }
        catch { }

        return result;
    }

    /// <summary>Normalizes a server IP to the host key used by this service.</summary>
    public static string NormalizeHost(string? ip)
    {
        string h = (ip ?? string.Empty).Trim();
        return IsLoopback(h) ? LocalHost : h;
    }

    /// <summary>
    /// Fire-and-forget: pushes the reviewed state of a study to all reachable hosts on a
    /// background thread with payload {"is_reviewed": 1} or 0. Never throws and never blocks the UI.
    /// </summary>
    public static void PushReviewedStateInBackground(string studyInstanceUid, bool isReviewed, IEnumerable<string> hosts)
    {
        if (string.IsNullOrWhiteSpace(studyInstanceUid)) return;
        var hostList = hosts.ToList();

        _ = Task.Run(async () =>
        {
            string payload = JsonSerializer.Serialize(new { is_reviewed = isReviewed ? 1 : 0 });
            string uidEscaped = Uri.EscapeDataString(studyInstanceUid);

            foreach (var host in hostList.Where(IsHostAvailable))
            {
                try
                {
                    using var cts = new CancellationTokenSource(RequestTimeout);
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var resp = await _http.PostAsync($"http://{host}:{ApiPort}/api/studies/{uidEscaped}/reviewed", content, cts.Token).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) MarkAvailable(host);
                    else MarkUnavailable(host);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ParsecPacsSync] POST reviewed failed on {host}: {ex.Message}");
                    MarkUnavailable(host);
                }
            }
        });
    }

    #region Notes

    private enum NoteRequestStatus { Found, NotFound, Failed }

    private sealed record ServerNote(NoteRequestStatus Status, string Text, string? UpdatedAt);

    private const int NoteSyncParallelism = 8;

    /// <summary>
    /// Hosts that may hold a study's note: the study's own server, or for a local study every
    /// DICOM server plus the local ParsecPACS. PixelData web portals have no notes API.
    /// </summary>
    public static List<string> GetNoteHosts(string? serverId = null)
    {
        var servers = PacsService.Instance.Settings.Servers.Where(s => s.ServerType != "PixelDataWeb").ToList();
        if (string.IsNullOrEmpty(serverId)) return GetCandidateHosts(servers);

        var server = PacsService.Instance.Settings.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null) return GetCandidateHosts(servers);
        return servers.Contains(server) ? new List<string> { NormalizeHost(server.IpAddress) } : new List<string>();
    }

    /// <summary>Saves a note the user edited: on the local record if there is one, and in PACS. Never throws.</summary>
    public static async Task<NoteSaveStatus> SaveUserNoteAsync(string studyUid, string? text, string? displayedNote, IEnumerable<string> hosts)
    {
        string value = text?.Trim() ?? string.Empty;
        var record = DatabaseService.GetRecord(studyUid);
        string? localOnly = DatabaseService.GetLocalNote(studyUid);

        string current = record?.PatientComments ?? displayedNote ?? string.Empty;
        if (localOnly == null && string.Equals(current.Trim(), value, StringComparison.Ordinal)) return NoteSaveStatus.Unchanged;

        if (record != null && !DatabaseService.ApplyUserNote(studyUid, value)) return NoteSaveStatus.LocalSaveFailed;

        var result = await PostNoteAsync(studyUid, value, hosts).ConfigureAwait(false);
        if (result.Status == NoteRequestStatus.Found && record != null)
            DatabaseService.ApplyServerNote(studyUid, result.Text, result.UpdatedAt);

        // A study with a local record carries its note there; otherwise the note stays here until PACS has it.
        DatabaseService.SetLocalNote(studyUid, record != null || result.Status == NoteRequestStatus.Found ? null : value);

        return result.Status switch
        {
            NoteRequestStatus.Found => NoteSaveStatus.SavedInPacs,
            NoteRequestStatus.NotFound => NoteSaveStatus.NotInPacs,
            _ => hosts.Any() ? NoteSaveStatus.PacsUnreachable : NoteSaveStatus.NotInPacs
        };
    }

    /// <summary>Returns the most recent note to show for a study. Falls back to the local value if the server does not answer.</summary>
    public static async Task<string> LoadLatestNoteAsync(string studyUid, string? displayedNote, IEnumerable<string> hosts)
    {
        if (DatabaseService.GetRecord(studyUid) != null)
            return await SyncRecordNoteAsync(studyUid, hosts).ConfigureAwait(false) ?? displayedNote ?? string.Empty;

        string? localOnly = DatabaseService.GetLocalNote(studyUid);
        if (localOnly != null) return localOnly;

        var server = await FetchNoteAsync(studyUid, hosts).ConfigureAwait(false);
        return server.Status == NoteRequestStatus.Found ? server.Text : displayedNote ?? string.Empty;
    }

    /// <summary>
    /// Compares a local record's note with PACS by edit time. A newer server note is stored locally
    /// with its own time; a newer local note is sent to PACS. Notes without any time are left alone.
    /// Returns the record's note, or null if the study has no local record.
    /// </summary>
    public static async Task<string?> SyncRecordNoteAsync(string studyUid, IEnumerable<string> hosts)
    {
        var record = DatabaseService.GetRecord(studyUid);
        if (record == null) return null;

        var server = await FetchNoteAsync(studyUid, hosts).ConfigureAwait(false);
        if (server.Status != NoteRequestStatus.Found) return record.PatientComments;

        if (IsNewer(server.UpdatedAt, record.PatientCommentsUpdatedAt))
        {
            DatabaseService.ApplyServerNote(studyUid, server.Text, server.UpdatedAt);
            return server.Text;
        }

        string localText = record.PatientComments?.Trim() ?? string.Empty;
        if (IsNewer(record.PatientCommentsUpdatedAt, server.UpdatedAt) && !string.Equals(localText, server.Text.Trim(), StringComparison.Ordinal))
        {
            var sent = await PostNoteAsync(studyUid, localText, hosts).ConfigureAwait(false);
            if (sent.Status == NoteRequestStatus.Found)
                DatabaseService.ApplyServerNote(studyUid, sent.Text, sent.UpdatedAt);
        }
        return record.PatientComments;
    }

    /// <summary>Compares every local record's note with PACS. Never throws.</summary>
    public static async Task SyncAllRecordNotesAsync(IEnumerable<string> hosts, CancellationToken ct = default)
    {
        var hostList = hosts.ToList();
        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = NoteSyncParallelism, CancellationToken = ct };
            await Parallel.ForEachAsync(DatabaseService.LoadRecords(), options, async (record, _) =>
                await SyncRecordNoteAsync(record.StudyInstanceUid, hostList).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ParsecPacsSync] Note sync failed: {ex.Message}");
        }
    }

    private static async Task<ServerNote> FetchNoteAsync(string studyUid, IEnumerable<string> hosts)
    {
        var replies = await Task.WhenAll(hosts.Where(IsHostAvailable).Select(host => RequestNoteAsync(host, studyUid, null))).ConfigureAwait(false);
        return BestReply(replies);
    }

    private static async Task<ServerNote> PostNoteAsync(string studyUid, string text, IEnumerable<string> hosts)
    {
        var replies = await Task.WhenAll(hosts.Select(host => RequestNoteAsync(host, studyUid, text))).ConfigureAwait(false);
        return BestReply(replies);
    }

    private static ServerNote BestReply(ServerNote[] replies)
    {
        var found = replies.Where(r => r.Status == NoteRequestStatus.Found).ToList();
        if (found.Count > 0) return found.Aggregate((a, b) => IsNewer(b.UpdatedAt, a.UpdatedAt) ? b : a);
        return new ServerNote(replies.Any(r => r.Status == NoteRequestStatus.NotFound) ? NoteRequestStatus.NotFound : NoteRequestStatus.Failed, string.Empty, null);
    }

    /// <summary>GET details when text is null, otherwise POST the note. The POST sends only patient_comments, so StudyDescription is not changed.</summary>
    private static async Task<ServerNote> RequestNoteAsync(string host, string studyUid, string? text)
    {
        try
        {
            string url = $"http://{host}:{ApiPort}/api/studies/{Uri.EscapeDataString(studyUid.Trim().TrimEnd('\0'))}/details";
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var content = text == null ? null : new StringContent(JsonSerializer.Serialize(new { patient_comments = text }), Encoding.UTF8, "application/json");
            using var resp = text == null
                ? await _http.GetAsync(url, cts.Token).ConfigureAwait(false)
                : await _http.PostAsync(url, content, cts.Token).ConfigureAwait(false);

            MarkAvailable(host);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new ServerNote(NoteRequestStatus.NotFound, string.Empty, null);
            if (!resp.IsSuccessStatusCode) return new ServerNote(NoteRequestStatus.Failed, string.Empty, null);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            return new ServerNote(NoteRequestStatus.Found, ReadString(doc.RootElement, "patient_comments") ?? string.Empty, ReadString(doc.RootElement, "notes_updated_at"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ParsecPacsSync] Note request failed on {host}: {ex.GetType().Name}");
            MarkUnavailable(host);
            return new ServerNote(NoteRequestStatus.Failed, string.Empty, null);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsNewer(string? time, string? other)
    {
        if (!TryParseTime(time, out var parsed)) return false;
        return !TryParseTime(other, out var otherParsed) || parsed > otherParsed;
    }

    private static bool TryParseTime(string? time, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out parsed);

    #endregion

    private static bool IsHostAvailable(string host)
    {
        // The local ParsecPACS is always retried (cheap, loopback refuses instantly when down)
        if (string.Equals(host, LocalHost, StringComparison.OrdinalIgnoreCase)) return true;
        return !_unavailableUntil.TryGetValue(host, out var until) || DateTime.UtcNow >= until;
    }

    private static void MarkUnavailable(string host)
    {
        if (string.Equals(host, LocalHost, StringComparison.OrdinalIgnoreCase)) return;
        _unavailableUntil[host] = DateTime.UtcNow.Add(HostBackoff);
    }

    private static void MarkAvailable(string host) => _unavailableUntil.TryRemove(host, out _);

    private static bool IsLoopback(string ip) =>
        ip == "127.0.0.1" || ip.Equals("localhost", StringComparison.OrdinalIgnoreCase) || ip == "::1";
}
