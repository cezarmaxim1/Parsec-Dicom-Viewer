// @purpose: WPF application entry point initializing crash logs, single-instance enforcement, CLI argument routing, and dark theme resources.
// @depends: System.Windows, OpenRadViewer.Core.Services (SingleInstanceManager, DarkWindowHelper), OpenRadViewer.Views
// @exports: App

using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;

namespace OpenRadViewer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appdomain_crash.log"), args.ExceptionObject.ToString());
            }
            catch { }
        };
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Enforce Single Instance: If another Parsec instance is already running,
        // forward command-line arguments to it via Named Pipe and exit immediately.
        if (!Core.Services.SingleInstanceManager.InitializeAsPrimaryOrForward(e.Args))
        {
            Shutdown();
            return;
        }

        try
        {
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log"), "OnStartup started (Primary instance)\n");
        }
        catch { }

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), args.Exception.ToString());
            }
            catch { }
            // Do not call MessageBox recursively
            args.Handled = false;
        };

        // Collect all startup paths passed as arguments
        var startupPaths = e.Args
            .Where(a => !string.IsNullOrWhiteSpace(a) && (Directory.Exists(a) || File.Exists(a)))
            .ToList();
        Views.MainWindow.StartupPaths = startupPaths;

        var mainWindow = new Views.MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        // Listen for new folders/files forwarded from secondary instances (e.g. right-click "Open with Parsec")
        Core.Services.SingleInstanceManager.PathsReceived += paths =>
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (MainWindow is Views.MainWindow mainWin)
                {
                    mainWin.BringToFront();
                    await mainWin.ViewModel.ScanPathsAsync(paths);
                }
            });
        };

        File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log"), "mainWindow.Show completed\n");

        // Clean any leftover orphan cache files according to retention policy & register shell context menus
        _ = Task.Run(() =>
        {
            try
            {
                Core.Services.ArchiveExtractionService.CleanOldTempArchives(24);
                Core.Services.ArchiveExtractionService.RegisterShellOpenWithContextMenus();
                var s = Core.Services.PacsService.Instance.Settings;
                if (s != null && s.CacheRetentionDays > 0)
                {
                    Core.Services.PacsService.Instance.ClearCache(s.CacheRetentionDays);
                }
            }
            catch { }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        try
        {
            Core.Services.SingleInstanceManager.Cleanup();

            var s = Core.Services.PacsService.Instance.Settings;
            if (s != null)
            {
                if (s.CacheRetentionDays == 0)
                {
                    // Clean all temporary PACS cache immediately on exit
                    Core.Services.PacsService.Instance.ClearCache(0);
                }
                else if (s.CacheRetentionDays > 0)
                {
                    Core.Services.PacsService.Instance.ClearCache(s.CacheRetentionDays);
                }
            }
        }
        catch { }
    }
}

