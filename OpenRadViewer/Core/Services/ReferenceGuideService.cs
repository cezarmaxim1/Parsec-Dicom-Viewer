// @purpose: Persistence service for radiological reference guides, diagrams, and tables with built-in asset defaults and user additions.
// @depends: System.Text.Json, System.Windows.Media.Imaging, OpenRadViewer.Core.Models
// @exports: ReferenceGuideService

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public class ReferenceGuideService
{
    private static readonly Lazy<ReferenceGuideService> _lazy = new(() => new ReferenceGuideService());
    public static ReferenceGuideService Instance => _lazy.Value;

    private readonly string _storageDir;
    private readonly string _imagesDir;
    private readonly string _jsonFilePath;
    private readonly object _lock = new();

    public event Action? ReferencesChanged;

    private ReferenceGuideService()
    {
        _storageDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReferenceGuides");
        _imagesDir = Path.Combine(_storageDir, "Images");
        _jsonFilePath = Path.Combine(_storageDir, "custom_references.json");

        try
        {
            if (!Directory.Exists(_storageDir)) Directory.CreateDirectory(_storageDir);
            if (!Directory.Exists(_imagesDir)) Directory.CreateDirectory(_imagesDir);
        }
        catch { }
    }

    public List<ReferenceGuideItem> GetCustomItems(string category)
    {
        lock (_lock)
        {
            var list = LoadCustomItemsInternal();
            return list.Where(x => string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase))
                       .OrderBy(x => x.Title)
                       .ToList();
        }
    }

    public ReferenceGuideItem AddCustomItem(string title, string sourceImagePath, string category, string description = "")
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title cannot be empty.", nameof(title));
        if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath)) throw new FileNotFoundException("Image file not found.", sourceImagePath);

        lock (_lock)
        {
            if (!Directory.Exists(_imagesDir)) Directory.CreateDirectory(_imagesDir);

            string ext = Path.GetExtension(sourceImagePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".png";

            string safeFileName = Guid.NewGuid().ToString("N") + ext;
            string destPath = Path.Combine(_imagesDir, safeFileName);

            File.Copy(sourceImagePath, destPath, true);

            var item = new ReferenceGuideItem
            {
                Id = Guid.NewGuid().ToString(),
                Title = title.Trim(),
                Category = category,
                ImagePath = destPath,
                Description = description?.Trim() ?? string.Empty,
                IsBuiltIn = false,
                DateAdded = DateTime.Now
            };

            var items = LoadCustomItemsInternal();
            items.Add(item);
            SaveCustomItemsInternal(items);

            try
            {
                ReferencesChanged?.Invoke();
            }
            catch { }

            return item;
        }
    }

    public bool DeleteCustomItem(string id)
    {
        lock (_lock)
        {
            var items = LoadCustomItemsInternal();
            var target = items.FirstOrDefault(x => x.Id == id);
            if (target == null) return false;

            items.Remove(target);
            SaveCustomItemsInternal(items);

            if (!string.IsNullOrWhiteSpace(target.ImagePath) && File.Exists(target.ImagePath))
            {
                try
                {
                    File.Delete(target.ImagePath);
                }
                catch { }
            }

            try
            {
                ReferencesChanged?.Invoke();
            }
            catch { }

            return true;
        }
    }

    private List<ReferenceGuideItem> LoadCustomItemsInternal()
    {
        try
        {
            if (File.Exists(_jsonFilePath))
            {
                string json = File.ReadAllText(_jsonFilePath);
                var items = JsonSerializer.Deserialize<List<ReferenceGuideItem>>(json);
                return items ?? new List<ReferenceGuideItem>();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ReferenceGuideService] Error loading items: {ex.Message}");
        }
        return new List<ReferenceGuideItem>();
    }

    private void SaveCustomItemsInternal(List<ReferenceGuideItem> items)
    {
        try
        {
            string json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_jsonFilePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ReferenceGuideService] Error saving items: {ex.Message}");
        }
    }

    public static BitmapImage? LoadBitmapSafely(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;

        // 1. Try disk path (absolute)
        if (File.Exists(imagePath))
        {
            try
            {
                var bmp = new BitmapImage();
                using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch { }
        }

        // 2. Try relative to AppDomain base directory / Assets
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidate = Path.Combine(baseDir, imagePath);
        if (File.Exists(candidate))
        {
            try
            {
                var bmp = new BitmapImage();
                using (var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch { }
        }

        // 3. Try pack URI
        try
        {
            string cleanRel = imagePath.Replace('\\', '/').TrimStart('/');
            var packUri = new Uri($"pack://application:,,,/{cleanRel}", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = packUri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { }

        return null;
    }
}
