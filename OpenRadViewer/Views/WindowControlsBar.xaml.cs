// @purpose: Minimize, maximize/restore, and close buttons for windows whose toolbar replaces the native title bar.
// @depends: OpenRadViewer.Views.CustomTitleBar
// @exports: WindowControlsBar

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenRadViewer.Views;

public partial class WindowControlsBar : UserControl
{
    private Window? _window;

    public WindowControlsBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_window != null) return;

        _window = Window.GetWindow(this);
        if (_window == null) return;

        _window.StateChanged += (_, _) => UpdateMaximizeIcon();
        UpdateMaximizeIcon();
    }

    private void UpdateMaximizeIcon()
    {
        bool isMaximized = _window?.WindowState == WindowState.Maximized;
        MaxRestoreIcon.Data = (Geometry)FindResource(isMaximized ? "IconWindowRestore" : "IconWindowMaximize");
        BtnMaxRestore.ToolTip = isMaximized ? "Restore Down" : "Maximize";
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (_window != null) SystemCommands.MinimizeWindow(_window);
    }

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (_window != null) CustomTitleBar.ToggleMaximize(_window);
    }

    private void OnClose(object sender, RoutedEventArgs e) => _window?.Close();
}
