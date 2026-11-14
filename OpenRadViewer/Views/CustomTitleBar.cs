// @purpose: Replaces a window's native title bar with its toolbar: WindowChrome setup, maximized inset, native caption drag, and maximize/restore.
// @depends: System.Windows.Shell, user32.dll
// @exports: CustomTitleBar

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace OpenRadViewer.Views;

public static class CustomTitleBar
{
    private const int SM_CXFRAME = 32;
    private const int SM_CYFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Makes dragArea act as the title bar of window. Call after InitializeComponent.
    /// </summary>
    public static void Attach(Window window, FrameworkElement root, UIElement dragArea)
    {
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(5),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false
        });

        window.SourceInitialized += (_, _) => UpdateMaximizedInset(window, root);
        window.StateChanged += (_, _) => UpdateMaximizedInset(window, root);
        dragArea.MouseLeftButtonDown += (_, e) => OnDragAreaMouseDown(window, e);
    }

    public static void ToggleMaximize(Window window)
    {
        if (window is MainWindow main && main.ViewModel.IsFullScreen)
        {
            main.ToggleFullScreen();
        }
        else if (window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(window);
        }
        else
        {
            SystemCommands.MaximizeWindow(window);
        }
    }

    // AI-NOTE: A maximized WindowChrome window extends past the screen by the resize frame, so the content is inset by the same amount.
    private static void UpdateMaximizedInset(Window window, FrameworkElement root)
    {
        if (window.WindowState != WindowState.Maximized)
        {
            root.Margin = new Thickness(0);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        int padded = GetSystemMetrics(SM_CXPADDEDBORDER);
        double insetX = (GetSystemMetrics(SM_CXFRAME) + padded) / dpi.DpiScaleX;
        double insetY = (GetSystemMetrics(SM_CYFRAME) + padded) / dpi.DpiScaleY;
        root.Margin = new Thickness(insetX, insetY, insetX, insetY);
    }

    private static void OnDragAreaMouseDown(Window window, MouseButtonEventArgs e)
    {
        // Popups route mouse events through the toolbar, but they are not part of the title bar
        if (e.OriginalSource is Visual visual && PresentationSource.FromVisual(visual)?.RootVisual != window) return;
        if (window.WindowStyle == WindowStyle.None) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize(window);
            return;
        }

        // Native caption drag, so Aero Snap and drag-to-restore keep working
        var hwnd = new WindowInteropHelper(window).Handle;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }
}
