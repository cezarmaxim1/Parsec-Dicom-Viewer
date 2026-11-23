// @purpose: Checks GitHub (cezarmaxim1/Parsec-Dicom-Viewer) for newer releases, announces them at startup, and downloads/installs updates.
// @depends: System.Net.Http, System.Text.Json, System.Diagnostics
// @exports: AppUpdateService, UpdateCheckResult

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenRadViewer.Core.Services;

public record UpdateCheckResult(
    bool IsUpdateAvailable,
    string LatestVersion,
    string ReleaseNotes,
    string DownloadUrl,
    string? ErrorMessage = null);

public static class AppUpdateService
{
    public const string CurrentVersion = "1.3.3";
    public const int CurrentBuildNumber = 133;
    public const string CurrentDisplayVersion = "v1.3.3 Beta";

    private const string VersionJsonUrl = "https://raw.githubusercontent.com/cezarmaxim1/Parsec-Dicom-Viewer/main/version.json";
    private const string ReleasesApiUrl = "https://api.github.com/repos/cezarmaxim1/Parsec-Dicom-Viewer/releases/latest";
    private const string DefaultDownloadUrl = "https://github.com/cezarmaxim1/Parsec-Dicom-Viewer/releases/latest/download/Parsec_Setup_v1.3.3_Beta.exe";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Add("User-Agent", "Parsec-Dicom-Viewer");
        return client;
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        string remoteVersionStr = CurrentVersion;
        int remoteBuild = CurrentBuildNumber;
        string displayVer = CurrentDisplayVersion;
        string releaseNotes = string.Empty;
        string downloadUrl = DefaultDownloadUrl;
        bool fetchedAny = false;
        string? lastError = null;

        try
        {
            string url = $"{VersionJsonUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            using var resp = await HttpClient.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("version", out var vProp) && vProp.ValueKind == JsonValueKind.String)
                    remoteVersionStr = vProp.GetString() ?? CurrentVersion;
                if (root.TryGetProperty("buildNumber", out var bProp) && bProp.TryGetInt32(out int bNum))
                    remoteBuild = bNum;
                if (root.TryGetProperty("displayVersion", out var dProp) && dProp.ValueKind == JsonValueKind.String)
                    displayVer = dProp.GetString() ?? remoteVersionStr;
                if (root.TryGetProperty("releaseNotes", out var nProp) && nProp.ValueKind == JsonValueKind.String)
                    releaseNotes = nProp.GetString() ?? string.Empty;
                if (root.TryGetProperty("downloadUrl", out var uProp) && uProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(uProp.GetString()))
                    downloadUrl = uProp.GetString()!;

                fetchedAny = true;
            }
            else
            {
                lastError = $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})";
            }
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }

        // Also query GitHub Releases latest endpoint to pick up any newer tagged release or direct installer asset URL
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            req.Headers.Add("Accept", "application/vnd.github+json");
            using var relResp = await HttpClient.SendAsync(req);
            if (relResp.IsSuccessStatusCode)
            {
                string relJson = await relResp.Content.ReadAsStringAsync();
                using var relDoc = JsonDocument.Parse(relJson);
                var relRoot = relDoc.RootElement;

                string tagName = relRoot.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? string.Empty) : string.Empty;
                string relBody = relRoot.TryGetProperty("body", out var bodyProp) ? (bodyProp.GetString() ?? string.Empty) : string.Empty;

                string? exeAssetUrl = null;
                if (relRoot.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        string assetName = asset.TryGetProperty("name", out var nameProp) ? (nameProp.GetString() ?? string.Empty) : string.Empty;
                        if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            asset.TryGetProperty("browser_download_url", out var urlProp))
                        {
                            exeAssetUrl = urlProp.GetString();
                            break;
                        }
                    }
                }

                string cleanTag = tagName.TrimStart('v', 'V').Split('-')[0];
                if (Version.TryParse(cleanTag, out var tagVer) && Version.TryParse(remoteVersionStr, out var jsonVer) && tagVer > jsonVer)
                {
                    remoteVersionStr = cleanTag;
                    displayVer = tagName;
                    if (!string.IsNullOrWhiteSpace(relBody)) releaseNotes = relBody;
                    if (!string.IsNullOrWhiteSpace(exeAssetUrl)) downloadUrl = exeAssetUrl;
                }
                else if (string.IsNullOrWhiteSpace(downloadUrl) && !string.IsNullOrWhiteSpace(exeAssetUrl))
                {
                    downloadUrl = exeAssetUrl;
                }

                fetchedAny = true;
            }
        }
        catch (Exception ex)
        {
            if (!fetchedAny) lastError = ex.Message;
        }

        if (!fetchedAny)
        {
            return new UpdateCheckResult(
                false,
                CurrentDisplayVersion,
                string.Empty,
                DefaultDownloadUrl,
                lastError ?? "Nu s-a putut contacta serverul GitHub.");
        }

        bool isNewer = remoteBuild > CurrentBuildNumber;
        if (!isNewer &&
            Version.TryParse(remoteVersionStr.Split('-')[0], out var rVer) &&
            Version.TryParse(CurrentVersion, out var cVer) &&
            rVer > cVer)
        {
            isNewer = true;
        }

        return new UpdateCheckResult(
            isNewer,
            string.IsNullOrWhiteSpace(displayVer) ? remoteVersionStr : displayVer,
            releaseNotes,
            downloadUrl,
            null);
    }

    private static readonly string PrefsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parsec", "update_prefs.json");

    /// <summary>Whether a new version is announced when Parsec starts. Turned off by "Don't remind me", back on in About.</summary>
    public static bool AnnounceOnStartup
    {
        get
        {
            try
            {
                if (!File.Exists(PrefsFile)) return true;
                using var doc = JsonDocument.Parse(File.ReadAllText(PrefsFile));
                return !doc.RootElement.TryGetProperty("announceOnStartup", out var p) || p.ValueKind != JsonValueKind.False;
            }
            catch
            {
                return true;
            }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PrefsFile)!);
                File.WriteAllText(PrefsFile, JsonSerializer.Serialize(new { announceOnStartup = value }));
            }
            catch { }
        }
    }

    /// <summary>
    /// Checks GitHub once, a few seconds after start, and offers a newer version: install now, later (asked again at the
    /// next start), or never announce again. Silent when offline, up to date, or turned off.
    /// </summary>
    public static async Task CheckOnStartupAsync(Window owner)
    {
        if (!AnnounceOnStartup) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var res = await CheckForUpdatesAsync();
            if (!res.IsUpdateAvailable || !string.IsNullOrWhiteSpace(res.ErrorMessage)) return;
            if (!owner.IsLoaded) return;
            ShowUpdatePrompt(owner, res);
        }
        catch { }
    }

    private static void ShowUpdatePrompt(Window owner, UpdateCheckResult res)
    {
        var accent = new SolidColorBrush(Color.FromRgb(0xF5, 0xAF, 0x28));
        var text = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        var muted = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));

        var dlg = new Window
        {
            Title = "Parsec Update",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))
        };

        var status = new TextBlock { Foreground = muted, FontSize = 12, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Margin = new Thickness(22), MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = $"A new version is available: {res.LatestVersion}", Foreground = accent, FontSize = 15, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"Installed version: {CurrentDisplayVersion}", Foreground = muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        if (!string.IsNullOrWhiteSpace(res.ReleaseNotes))
            panel.Children.Add(new TextBlock { Text = res.ReleaseNotes, Foreground = text, FontSize = 12.5, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(status);

        Button MakeButton(string label, bool primary) => new Button
        {
            Content = label,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = primary ? Brushes.Black : text,
            Background = primary ? accent : new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var install = MakeButton("Install now", true);
        var later = MakeButton("Later", false);
        var never = MakeButton("Don't remind me", false);
        install.IsDefault = true;
        later.IsCancel = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(never);
        buttons.Children.Add(later);
        buttons.Children.Add(install);
        panel.Children.Add(buttons);
        dlg.Content = panel;

        later.Click += (_, _) => dlg.Close();
        never.Click += (_, _) =>
        {
            AnnounceOnStartup = false;
            MessageBox.Show(dlg,
                "Parsec will no longer announce new versions at startup.\n\nYou can still use About → Check for Updates, where you can also turn the notice back on.",
                "Parsec Update", MessageBoxButton.OK, MessageBoxImage.Information);
            dlg.Close();
        };
        install.Click += async (_, _) =>
        {
            install.IsEnabled = later.IsEnabled = never.IsEnabled = false;
            status.Visibility = Visibility.Visible;
            var progress = new Progress<(double percent, string status)>(p => status.Text = p.status);
            try
            {
                await DownloadAndInstallUpdateAsync(res.DownloadUrl, progress);
            }
            catch (Exception ex)
            {
                status.Text = $"Download failed: {ex.Message}";
                install.IsEnabled = later.IsEnabled = never.IsEnabled = true;
            }
        };

        dlg.ShowDialog();
    }

    public static async Task DownloadAndInstallUpdateAsync(
        string downloadUrl,
        IProgress<(double percent, string status)>? progress = null)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "Parsec_Setup_Update.exe");
        if (File.Exists(tempFile))
        {
            try { File.Delete(tempFile); } catch { }
        }

        progress?.Report((0, "Connecting to GitHub..."));

        using var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        downloadClient.DefaultRequestHeaders.Add("User-Agent", "Parsec-Dicom-Viewer");

        using var response = await downloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        using (var src = await response.Content.ReadAsStreamAsync())
        using (var dst = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await dst.WriteAsync(buffer, 0, read);
                totalRead += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    double pct = (double)totalRead / totalBytes.Value * 100.0;
                    double mbRead = totalRead / (1024.0 * 1024.0);
                    double mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                    progress?.Report((pct, $"Downloading update... {pct:0}% ({mbRead:0.0} / {mbTotal:0.0} MB)"));
                }
                else
                {
                    double mbRead = totalRead / (1024.0 * 1024.0);
                    progress?.Report((0, $"Downloading update... {mbRead:0.0} MB"));
                }
            }
        }

        progress?.Report((100, "Launching installer..."));

        Process.Start(new ProcessStartInfo
        {
            FileName = tempFile,
            Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true
        });

        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }
}
