// @purpose: Win32 DWM interop to enforce immersive dark titlebars, custom caption borders, and dark window styling on Windows 10/11.
// @depends: dwmapi.dll, user32.dll, System.Windows.Interop
// @exports: DarkWindowHelper

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenRadViewer.Core.Services;

public static class DarkWindowHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtr", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetClassLong", SetLastError = true)]
    private static extern int SetClassLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr SetClassLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetClassLongPtr64(hWnd, nIndex, dwNewLong);
        else
            return new IntPtr(SetClassLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    /// <summary>
    /// Applies a persistent dark mode title bar (identical to VS Code and Antigravity)
    /// locking caption color to #181818 and text to #E0E0E0 on Windows 10 & 11.
    /// </summary>
    public static void ApplyDarkTitleBar(Window window, int captionColorBgr = 0x00181818, int textColorBgr = 0x00E0E0E0, int borderColorBgr = 0x00282828)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            ApplyToHandle(helper.Handle, captionColorBgr, textColorBgr, borderColorBgr);
        }
        else
        {
            window.SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(window).Handle;
                ApplyToHandle(h, captionColorBgr, textColorBgr, borderColorBgr);
            };
        }
    }

    public static void ApplyToHandle(IntPtr handle, int captionColorBgr = 0x00181818, int textColorBgr = 0x00E0E0E0, int borderColorBgr = 0x00282828)
    {
        if (handle == IntPtr.Zero) return;
        try
        {
            int darkMode = 1;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // Windows 11 build 22000+ title bar color locking
            DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref captionColorBgr, sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref textColorBgr, sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref borderColorBgr, sizeof(int));

            // Set native GDI window class background brush to BLACK_BRUSH (4)
            SetClassLong(handle, -10, GetStockObject(4));
        }
        catch { }
    }
}
