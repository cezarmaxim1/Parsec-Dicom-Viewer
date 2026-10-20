// @purpose: Non-blocking HTTP client for ParsecPACS REST API (port 8042) to synchronize "Reviewed" study status bidirectionally.
// @depends: System.Net.Http, System.Text.Json
// @exports: ParsecPacsSyncService

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRadViewer.Core.Services;

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

                if (set.Count > 0)
                {
                    MarkAvailable(host);
                    return (host, (HashSet<string>?)set);
                }

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
