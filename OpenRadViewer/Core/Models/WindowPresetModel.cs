// @purpose: Data model representing radiological Window/Level (WL/WW) presets, both built-in standard and user-defined custom presets.
// @depends: System
// @exports: WindowPresetModel

namespace OpenRadViewer.Core.Models;

public class WindowPresetModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom Window";
    public double WindowCenter { get; set; } = 40;
    public double WindowWidth { get; set; } = 350;
    public bool IsBuiltIn { get; set; } = false;
    public int? ShortcutNumber { get; set; }

    public string DisplayText => $"{Name} (WL: {(int)WindowCenter}, WW: {(int)WindowWidth})";
}
