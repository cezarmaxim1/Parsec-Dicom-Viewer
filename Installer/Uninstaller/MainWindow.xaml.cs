// @purpose: Uninstaller wizard removing Parsec application files, Windows Registry shell integration, and Start Menu/Desktop shortcuts.
// @depends: Microsoft.Win32.Registry, System.Diagnostics, System.IO
// @exports: Parsec.Uninstaller.MainWindow

using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Parsec.Uninstaller;

public partial class MainWindow : Window
{
    private readonly string _installDir;

    public MainWindow()
    {
        InitializeComponent();
        _installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        TxtLocation.Text = $"Location: {_installDir}";

        try
        {
            string iconFile = Path.Combine(_installDir, "app_icon.ico");
            if (File.Exists(iconFile))
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconFile, UriKind.Absolute));
            }
        }
        catch { }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnUninstallClicked(object sender, RoutedEventArgs e)
    {
        BtnUninstall.IsEnabled = false;
        BtnCancel.IsEnabled = false;
        PbUninstall.Visibility = Visibility.Visible;
        PbUninstall.IsIndeterminate = true;
        TxtStatus.Visibility = Visibility.Visible;
        TxtStatus.Text = "Closing running Parsec instances...";

        await Task.Run(() =>
        {
            // 1. Terminate running Parsec processes
            foreach (var p in Process.GetProcessesByName("Parsec"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }

            // 2. Remove shortcuts
            RemoveShortcuts();

            // 3. Clean Registry
            RemoveRegistryEntries();

            // 4. Optional user data cleanup
            if (Dispatcher.Invoke(() => ChkRemoveData.IsChecked == true))
            {
                RemoveUserData();
            }
        });

        TxtStatus.Text = "Removing application files...";

        // 5. Spawn detached background process to delete the directory after uninstall.exe exits
        try
        {
            string script = $"ping 127.0.0.1 -n 3 > nul & rd /s /q \"{_installDir}\" & powershell -WindowStyle Hidden -Command \"Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('Parsec Medical DICOM Viewer has been successfully uninstalled from your computer.', 'Parsec Uninstall Complete', 'OK', 'Information')\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {script}",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            });

            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error initiating file cleanup: {ex.Message}", "Uninstall Error", MessageBoxButton.OK, MessageBoxImage.Error);
            BtnCancel.IsEnabled = true;
        }
    }

    private static void RemoveShortcuts()
    {
        try
        {
            // Desktop shortcuts
            string userDesktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Parsec.lnk");
            if (File.Exists(userDesktop)) File.Delete(userDesktop);

            string publicDesktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Parsec.lnk");
            if (File.Exists(publicDesktop)) File.Delete(publicDesktop);

            // Start Menu shortcuts
            string userPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Parsec.lnk");
            if (File.Exists(userPrograms)) File.Delete(userPrograms);

            string userFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Parsec");
            if (Directory.Exists(userFolder)) Directory.Delete(userFolder, true);

            string commonPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Parsec.lnk");
            if (File.Exists(commonPrograms)) File.Delete(commonPrograms);

            string commonFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Parsec");
            if (Directory.Exists(commonFolder)) Directory.Delete(commonFolder, true);
        }
        catch { }
    }

    private static void RemoveRegistryEntries()
    {
        try
        {
            // Uninstall entries
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
            {
                key?.DeleteSubKeyTree("Parsec", false);
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
                key?.DeleteSubKeyTree("Parsec", false);
            }
            catch { }

            // File and Directory associations
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true))
            {
                key?.DeleteSubKeyTree(".dcm", false);
                key?.DeleteSubKeyTree("Parsec.Dicom", false);
            }

            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell", true))
            {
                key?.DeleteSubKeyTree("Parsec", false);
            }

            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\Background\shell", true))
            {
                key?.DeleteSubKeyTree("Parsec", false);
            }
        }
        catch { }
    }

    private void RemoveUserData()
    {
        try
        {
            string pacsCache = Path.Combine(_installDir, "PacsCache");
            if (Directory.Exists(pacsCache)) Directory.Delete(pacsCache, true);

            string dbFile = Path.Combine(_installDir, "database.json");
            if (File.Exists(dbFile)) File.Delete(dbFile);

            string pacsCfg = Path.Combine(_installDir, "pacs_config.json");
            if (File.Exists(pacsCfg)) File.Delete(pacsCfg);
        }
        catch { }
    }
}
