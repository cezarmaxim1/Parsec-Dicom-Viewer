// @purpose: XAML WPF ValueConverters for UI bindings (visibility, active borders, series grouping, opacity, sync status).
// @depends: System.Windows.Data, System.Windows.Media
// @exports: ActiveBorderConverter, NullToVisibilityConverter, BoolToVisibilityConverter, InvertBoolToVisibilityConverter, ModalityToBrushConverter, SyncModeToStringConverter

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OpenRadViewer.Views;

public class ActiveBorderConverter : IValueConverter
{
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(245, 175, 40)); // RadiAnt golden amber
    private static readonly Brush InactiveBrush = new SolidColorBrush(Color.FromRgb(35, 35, 35));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value is bool b && b) ? ActiveBrush : InactiveBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value is bool b && b) ? -1.0 : 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNull = value == null;
        if (parameter is string s && s.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }
        return isNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class ModalityToBackgroundConverter : IValueConverter
{
    private static readonly Brush MrBg = new SolidColorBrush(Color.FromRgb(0x1F, 0x35, 0x42));
    private static readonly Brush CtBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x38, 0x30));
    private static readonly Brush UsBg = new SolidColorBrush(Color.FromRgb(0x2E, 0x28, 0x40));
    private static readonly Brush XrBg = new SolidColorBrush(Color.FromRgb(0x38, 0x30, 0x1F));
    private static readonly Brush NmBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x22, 0x26));
    private static readonly Brush XaBg = new SolidColorBrush(Color.FromRgb(0x38, 0x2C, 0x22));
    private static readonly Brush MgBg = new SolidColorBrush(Color.FromRgb(0x38, 0x22, 0x32));
    private static readonly Brush DefBg = new SolidColorBrush(Color.FromRgb(0x28, 0x2B, 0x30));

    static ModalityToBackgroundConverter()
    {
        MrBg.Freeze(); CtBg.Freeze(); UsBg.Freeze(); XrBg.Freeze();
        NmBg.Freeze(); XaBg.Freeze(); MgBg.Freeze(); DefBg.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string mod = (value?.ToString() ?? "").Trim().ToUpperInvariant();
        if (mod.Contains("MR")) return MrBg;
        if (mod.Contains("CT")) return CtBg;
        if (mod.Contains("US")) return UsBg;
        if (mod.Contains("CR") || mod.Contains("DX") || mod.Contains("RG") || mod.Contains("XR")) return XrBg;
        if (mod.Contains("NM") || mod.Contains("PT") || mod.Contains("PET")) return NmBg;
        if (mod.Contains("XA") || mod.Contains("RF") || mod.Contains("ANGIO")) return XaBg;
        if (mod.Contains("MG")) return MgBg;
        return DefBg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class ModalityToForegroundConverter : IValueConverter
{
    private static readonly Brush MrFg = new SolidColorBrush(Color.FromRgb(0x72, 0xC6, 0xE8)); // Soft Cyan
    private static readonly Brush CtFg = new SolidColorBrush(Color.FromRgb(0x64, 0xD2, 0xB4)); // Soft Mint/Teal
    private static readonly Brush UsFg = new SolidColorBrush(Color.FromRgb(0xB3, 0x9D, 0xDB)); // Soft Lavender
    private static readonly Brush XrFg = new SolidColorBrush(Color.FromRgb(0xE6, 0xC2, 0x80)); // Soft Warm Ochre
    private static readonly Brush NmFg = new SolidColorBrush(Color.FromRgb(0xEF, 0x9A, 0x9A)); // Soft Rose
    private static readonly Brush XaFg = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x80)); // Soft Peach
    private static readonly Brush MgFg = new SolidColorBrush(Color.FromRgb(0xF4, 0x8F, 0xB1)); // Soft Pink
    private static readonly Brush DefFg = new SolidColorBrush(Color.FromRgb(0xB0, 0xB8, 0xC4)); // Soft Slate Gray

    static ModalityToForegroundConverter()
    {
        MrFg.Freeze(); CtFg.Freeze(); UsFg.Freeze(); XrFg.Freeze();
        NmFg.Freeze(); XaFg.Freeze(); MgFg.Freeze(); DefFg.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string mod = (value?.ToString() ?? "").Trim().ToUpperInvariant();
        if (mod.Contains("MR")) return MrFg;
        if (mod.Contains("CT")) return CtFg;
        if (mod.Contains("US")) return UsFg;
        if (mod.Contains("CR") || mod.Contains("DX") || mod.Contains("RG") || mod.Contains("XR")) return XrFg;
        if (mod.Contains("NM") || mod.Contains("PT") || mod.Contains("PET")) return NmFg;
        if (mod.Contains("XA") || mod.Contains("RF") || mod.Contains("ANGIO")) return XaFg;
        if (mod.Contains("MG")) return MgFg;
        return DefFg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class ModalityToBorderBrushConverter : IValueConverter
{
    private static readonly Brush MrBd = new SolidColorBrush(Color.FromRgb(0x2E, 0x4D, 0x60));
    private static readonly Brush CtBd = new SolidColorBrush(Color.FromRgb(0x25, 0x52, 0x46));
    private static readonly Brush UsBd = new SolidColorBrush(Color.FromRgb(0x42, 0x39, 0x5C));
    private static readonly Brush XrBd = new SolidColorBrush(Color.FromRgb(0x52, 0x46, 0x2D));
    private static readonly Brush NmBd = new SolidColorBrush(Color.FromRgb(0x54, 0x32, 0x38));
    private static readonly Brush XaBd = new SolidColorBrush(Color.FromRgb(0x52, 0x40, 0x32));
    private static readonly Brush MgBd = new SolidColorBrush(Color.FromRgb(0x52, 0x32, 0x49));
    private static readonly Brush DefBd = new SolidColorBrush(Color.FromRgb(0x3B, 0x3F, 0x47));

    static ModalityToBorderBrushConverter()
    {
        MrBd.Freeze(); CtBd.Freeze(); UsBd.Freeze(); XrBd.Freeze();
        NmBd.Freeze(); XaBd.Freeze(); MgBd.Freeze(); DefBd.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string mod = (value?.ToString() ?? "").Trim().ToUpperInvariant();
        if (mod.Contains("MR")) return MrBd;
        if (mod.Contains("CT")) return CtBd;
        if (mod.Contains("US")) return UsBd;
        if (mod.Contains("CR") || mod.Contains("DX") || mod.Contains("RG") || mod.Contains("XR")) return XrBd;
        if (mod.Contains("NM") || mod.Contains("PT") || mod.Contains("PET")) return NmBd;
        if (mod.Contains("XA") || mod.Contains("RF") || mod.Contains("ANGIO")) return XaBd;
        if (mod.Contains("MG")) return MgBd;
        return DefBd;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

