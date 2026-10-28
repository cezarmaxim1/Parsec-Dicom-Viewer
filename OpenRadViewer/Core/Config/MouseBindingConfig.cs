// @purpose: Configurable mouse button, wheel, and modifier mappings for viewport interaction (pan, zoom, windowing, 3D cursor, measurements).
// @depends: System.Text.Json, System.IO
// @exports: MouseToolAction, MouseBindingConfig

using System.IO;
using System.Text.Json;

namespace OpenRadViewer.Core.Config;

public enum MouseToolAction
{
    None,
    WindowLevel,        // Adjust brightness & contrast
    BrowseSeries,       // Scroll through slices
    Zoom,               // Zoom in/out
    Pan,                // Move/pan image
    Cursor3D,           // 3D Crosshair spatial reference cursor (Q)
    MeasureLength,      // Distance ruler
    MeasureAngle,       // Angle
    MeasureCobbAngle,   // Cobb angle
    MeasureInsallSalvati, // Insall-Salvati ratio (Knee: patellar tendon / patella)
    MeasureEllipse,     // Elliptical ROI & statistics (Mean, Area, SD)
    MeasureRectangle,   // Rectangular ROI
    AiLesionCrop,       // AI Lesion rectangular crop for Gemini / Claude consultation
    Arrow,              // Pointer arrow
    Pencil,             // Freehand pencil tool
    TextAnnotation,     // Text label
    VertebraLabel,      // Spine vertebral body labeling (L5, L4, L3...)
    PixelProbe,         // Probe HU value under cursor
    NextImage,          // Step forward 1 slice
    PreviousImage,      // Step backward 1 slice
    ContextMenu,        // Popup menu
    ResetView           // Reset zoom/pan/window
}

public enum MouseInputTrigger
{
    LeftDrag,
    RightDrag,
    MiddleDrag,
    XButton1Drag,
    XButton2Drag,

    LeftClick,
    RightClick,
    MiddleClick,
    XButton1Click,
    XButton2Click,

    WheelScroll,
    CtrlWheelScroll,
    ShiftWheelScroll,
    AltWheelScroll
}

public class MouseBindingRule
{
    public MouseInputTrigger Trigger { get; set; }
    public MouseToolAction Action { get; set; }

    public MouseBindingRule() { }

    public MouseBindingRule(MouseInputTrigger trigger, MouseToolAction action)
    {
        Trigger = trigger;
        Action = action;
    }
}

public class MouseSettings
{
    public List<MouseBindingRule> Bindings { get; set; } = new();
    public bool MeasurementsCurrentSliceOnly { get; set; } = true;
    public bool InvertScrolling { get; set; } = false;

    public static MouseSettings CreateDefault()
    {
        var settings = new MouseSettings();

        // Exact mapping as requested:
        // 1. Wheel scroll up/down = BrowseSeries (scroll slices)
        // 2. Middle drag (hold wheel down and move) = WindowLevel (brightness/contrast)
        // 3. Right drag = Pan
        // 4. Left drag = BrowseSeries / Active Tool
        settings.Bindings.Add(new MouseBindingRule(MouseInputTrigger.WheelScroll, MouseToolAction.BrowseSeries));
        settings.Bindings.Add(new MouseBindingRule(MouseInputTrigger.MiddleDrag, MouseToolAction.WindowLevel));
        settings.Bindings.Add(new MouseBindingRule(MouseInputTrigger.RightDrag, MouseToolAction.Pan));
        settings.Bindings.Add(new MouseBindingRule(MouseInputTrigger.LeftDrag, MouseToolAction.BrowseSeries));
        
        // Extra mouse buttons (XButton1 and XButton2 for 7-button mouse)
        settings.Bindings.Add(new MouseBindingRule(MouseInputTrigger.XButton1Click, MouseToolAction.PreviousImage));
        settings.Bindings.Add(new MouseBindingRule(MouseInputTrigger.XButton2Click, MouseToolAction.NextImage));
        settings.Bindings.Add(new MouseBindingRule(MouseInputTrigger.XButton1Drag, MouseToolAction.Zoom));
        settings.Bindings.Add(new MouseBindingRule(MouseInputTrigger.XButton2Drag, MouseToolAction.WindowLevel));

        // Modifiers with wheel
        settings.Bindings.Add(new MouseBindingRule(MouseInputTrigger.CtrlWheelScroll, MouseToolAction.Zoom));
        settings.Bindings.Add(new MouseBindingRule(MouseInputTrigger.ShiftWheelScroll, MouseToolAction.WindowLevel));

        return settings;
    }

    public MouseToolAction GetActionFor(MouseInputTrigger trigger)
    {
        var match = Bindings.FirstOrDefault(b => b.Trigger == trigger);
        return match?.Action ?? MouseToolAction.None;
    }

    public void SetActionFor(MouseInputTrigger trigger, MouseToolAction action)
    {
        var match = Bindings.FirstOrDefault(b => b.Trigger == trigger);
        if (match != null)
        {
            match.Action = action;
        }
        else
        {
            Bindings.Add(new MouseBindingRule(trigger, action));
        }
    }

    public void SaveToFile(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public static MouseSettings LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return CreateDefault();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MouseSettings>(json) ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }
}
