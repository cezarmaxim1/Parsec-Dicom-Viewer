// @purpose: Persistence and management service for built-in and user-defined custom radiological window/level presets.
// @depends: System.Text.Json, System.IO, OpenRadViewer.Core.Models (WindowPresetModel)
// @exports: WindowPresetService

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public class WindowPresetService
{
    private static readonly Lazy<WindowPresetService> _lazy = new(() => new WindowPresetService());
    public static WindowPresetService Instance => _lazy.Value;

    private readonly string _storageDir;
    private readonly string _jsonFilePath;
    private readonly object _lock = new();
    private readonly List<WindowPresetModel> _builtInPresets = new();
    private List<WindowPresetModel> _customPresets = new();

    public event Action? PresetsChanged;

    private WindowPresetService()
    {
        _storageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRadViewer");
        _jsonFilePath = Path.Combine(_storageDir, "window_presets.json");

        InitializeBuiltInPresets();
        LoadCustomPresets();
    }

    private void InitializeBuiltInPresets()
    {
        _builtInPresets.Add(new WindowPresetModel { Id = "builtin-0", Name = "Default Window", WindowCenter = 0, WindowWidth = 0, IsBuiltIn = true, ShortcutNumber = 0 });
        _builtInPresets.Add(new WindowPresetModel { Id = "builtin-1", Name = "Full Dynamic", WindowCenter = 0, WindowWidth = 0, IsBuiltIn = true, ShortcutNumber = 1 });
        _builtInPresets.Add(new WindowPresetModel { Id = "builtin-2", Name = "Abdomen CT", WindowCenter = 40, WindowWidth = 350, IsBuiltIn = true, ShortcutNumber = 2 });
        _builtInPresets.Add(new WindowPresetModel { Id = "builtin-3", Name = "Angio CT", WindowCenter = 300, WindowWidth = 600, IsBuiltIn = true, ShortcutNumber = 3 });
        _builtInPresets.Add(new WindowPresetModel { Id = "builtin-4", Name = "Bone CT", WindowCenter = 300, WindowWidth = 1500, IsBuiltIn = true, ShortcutNumber = 4 });
        _builtInPresets.Add(new WindowPresetModel { Id = "builtin-5", Name = "Brain CT", WindowCenter = 40, WindowWidth = 80, IsBuiltIn = true, ShortcutNumber = 5 });
        _builtInPresets.Add(new WindowPresetModel { Id = "builtin-6", Name = "Chest CT", WindowCenter = 50, WindowWidth = 350, IsBuiltIn = true, ShortcutNumber = 6 });
        _builtInPresets.Add(new WindowPresetModel { Id = "builtin-7", Name = "Lung CT", WindowCenter = -600, WindowWidth = 1500, IsBuiltIn = true, ShortcutNumber = 7 });
        _builtInPresets.Add(new WindowPresetModel { Id = "builtin-8", Name = "Liver CT", WindowCenter = 60, WindowWidth = 160, IsBuiltIn = true, ShortcutNumber = 8 });
        _builtInPresets.Add(new WindowPresetModel { Id = "builtin-9", Name = "Stroke CT", WindowCenter = 32, WindowWidth = 8, IsBuiltIn = true, ShortcutNumber = 9 });
    }

    public IReadOnlyList<WindowPresetModel> GetBuiltInPresets() => _builtInPresets;

    public IReadOnlyList<WindowPresetModel> GetCustomPresets()
    {
        lock (_lock)
        {
            return _customPresets.ToList();
        }
    }

    public WindowPresetModel AddCustomPreset(string name, double center, double width)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Custom Window";
        if (width < 1) width = 1;

        var preset = new WindowPresetModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            WindowCenter = center,
            WindowWidth = width,
            IsBuiltIn = false
        };

        lock (_lock)
        {
            _customPresets.Add(preset);
            SaveCustomPresets();
        }

        try
        {
            PresetsChanged?.Invoke();
        }
        catch { }

        return preset;
    }

    public bool DeleteCustomPreset(string id)
    {
        lock (_lock)
        {
            var target = _customPresets.FirstOrDefault(x => x.Id == id);
            if (target == null) return false;

            _customPresets.Remove(target);
            SaveCustomPresets();
        }

        try
        {
            PresetsChanged?.Invoke();
        }
        catch { }

        return true;
    }

    private void LoadCustomPresets()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_jsonFilePath))
                {
                    string json = File.ReadAllText(_jsonFilePath);
                    var items = JsonSerializer.Deserialize<List<WindowPresetModel>>(json);
                    if (items != null)
                    {
                        _customPresets = items;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowPresetService] Error loading presets: {ex.Message}");
            }
            _customPresets = new List<WindowPresetModel>();
        }
    }

    private void SaveCustomPresets()
    {
        try
        {
            if (!Directory.Exists(_storageDir)) Directory.CreateDirectory(_storageDir);
            string json = JsonSerializer.Serialize(_customPresets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_jsonFilePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowPresetService] Error saving presets: {ex.Message}");
        }
    }
}
