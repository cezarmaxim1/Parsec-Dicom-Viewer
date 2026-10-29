// @purpose: Service for extracting compressed medical archives (.zip, .rar, .7z, .tar, .gz) into isolated temporary folders so DICOM files can be loaded, viewed, imported to database, or sent to PACS.
// @depends: System.IO.Compression, SharpCompress.Archives, SharpCompress.Common
// @exports: ArchiveExtractionService

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace OpenRadViewer.Core.Services;

public static class ArchiveExtractionService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz"
    };

    public static string TempArchivesRoot =>
        Path.Combine(Path.GetTempPath(), "ParsecArchives");

    /// <summary>
    /// Checks whether the given file path has a supported archive extension.
    /// </summary>
    public static bool IsArchiveFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path);
        return SupportedExtensions.Contains(ext);
    }

    /// <summary>
    /// Extracts an archive file into a unique isolated temporary folder.
    /// Returns the target directory containing the extracted files, or null if extraction failed.
    /// </summary>
    public static async Task<string?> ExtractArchiveAsync(
        string archivePath,
        IProgress<(int current, int total, string message)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return null;

        string safeName = Path.GetFileNameWithoutExtension(archivePath);
        foreach (char c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "Archive";

        string targetDir = Path.Combine(TempArchivesRoot, $"{safeName}_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(targetDir);

            await Task.Run(() =>
            {
                string ext = Path.GetExtension(archivePath).ToLowerInvariant();

                // 1. Try fast native .NET ZipArchive for .zip with entry-by-entry progress
                if (ext == ".zip")
                {
                    try
                    {
                        using var zip = ZipFile.OpenRead(archivePath);
                        var validEntries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                        int totalZip = validEntries.Count;
                        int currZip = 0;

                        foreach (var entry in validEntries)
                        {
                            if (ct.IsCancellationRequested) break;
                            string destPath = Path.Combine(targetDir, entry.FullName);
                            string? dirName = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(dirName)) Directory.CreateDirectory(dirName);
                            entry.ExtractToFile(destPath, overwrite: true);
                            currZip++;

                            if (progress != null && (currZip % 5 == 0 || currZip == totalZip))
                            {
                                progress.Report((currZip, totalZip, $"Dezarhivare {Path.GetFileName(archivePath)} ({currZip}/{totalZip})..."));
                            }
                        }

                        if (currZip > 0) return;
                    }
                    catch (Exception zipEx)
                    {
                        Debug.WriteLine($"[ArchiveExtractionService] Native ZipArchive failed, falling back to SharpCompress: {zipEx.Message}");
                    }
                }

                // 2. Extract with SharpCompress (handles RAR, 7z, TAR, GZ, and fallback ZIP)
                using var archive = ArchiveFactory.OpenArchive(archivePath);
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                int total = entries.Count;
                int current = 0;

                var opt = new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                };

                foreach (var entry in entries)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        entry.WriteToDirectory(targetDir, opt);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ArchiveExtractionService] Error extracting {entry.Key}: {ex.Message}");
                    }

                    current++;
                    if (progress != null && (current % 5 == 0 || current == total))
                    {
                        progress.Report((current, total, $"Dezarhivare {Path.GetFileName(archivePath)} ({current}/{total})..."));
                    }
                }
            }, ct);

            // Verify if at least one file was extracted
            if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
            {
                return targetDir;
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArchiveExtractionService] Failed to extract archive {archivePath}: {ex.Message}");
            try
            {
                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// Removes old temporary archive directories created by Parsec (default: older than 24 hours).
    /// </summary>
    public static void CleanOldTempArchives(int maxAgeHours = 24)
    {
        try
        {
            if (!Directory.Exists(TempArchivesRoot)) return;
            var cutoff = DateTime.Now.AddHours(-maxAgeHours);

            foreach (var dir in Directory.EnumerateDirectories(TempArchivesRoot))
            {
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (di.CreationTime < cutoff && di.LastWriteTime < cutoff)
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Registers Windows Explorer context menu ("Open with Parsec") for .zip, .rar, and .7z
    /// in HKCU\Software\Classes\SystemFileAssociations without requiring administrator rights.
    /// </summary>
    public static void RegisterShellOpenWithContextMenus()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Parsec.exe");
            if (!File.Exists(exePath)) return;

            string[] exts = { ".zip", ".rar", ".7z", ".tar", ".gz" };
            foreach (var ext in exts)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{ext}\shell\Parsec");
                if (key != null)
                {
                    key.SetValue("", "Open with Parsec");
                    key.SetValue("Icon", $"\"{exePath}\",0");
                    using var cmdKey = key.CreateSubKey("command");
                    cmdKey?.SetValue("", $"\"{exePath}\" \"%1\"");
                }
            }
        }
        catch { }
    }
}
