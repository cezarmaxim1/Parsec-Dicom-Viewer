// @purpose: Self-contained installer UI extracting the bundled payload.zip, registering Windows file associations (.dcm) & shell context menus, and creating desktop shortcuts.
// @depends: System.IO.Compression, shell32.dll, Microsoft.Win32.Registry
// @exports: Parsec.Setup.MainWindow

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Parsec.Setup;

public partial class MainWindow : Window
{
    private bool _isInstalled = false;

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_IDLIST = 0x0000;

    public MainWindow()
    {
        InitializeComponent();

        // Default path: per-user Programs directory (safe on all clinical PCs, no elevation blocker)
        string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Parsec");
        TxtInstallPath.Text = defaultDir;

        try
        {
            string iconFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_icon.ico");
            if (File.Exists(iconFile))
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconFile, UriKind.Absolute));
            }
        }
        catch { }
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Installation Directory for Parsec",
            InitialDirectory = Directory.Exists(TxtInstallPath.Text) ? TxtInstallPath.Text : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dlg.ShowDialog() == true)
        {
            TxtInstallPath.Text = dlg.FolderName;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnInstallClicked(object sender, RoutedEventArgs e)
    {
        if (_isInstalled)
        {
            // Finish clicked
            if (ChkLaunchAfter.IsChecked == true)
            {
                string exePath = Path.Combine(TxtInstallPath.Text.Trim(), "Parsec.exe");
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = TxtInstallPath.Text.Trim(),
                        UseShellExecute = true
                    });
                }
            }
            Close();
            return;
        }

        string installDir = TxtInstallPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(installDir))
        {
            MessageBox.Show("Please choose a valid destination directory.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnInstall.IsEnabled = false;
        BtnBrowse.IsEnabled = false;
        TxtInstallPath.IsEnabled = false;
        ChkDesktopShortcut.IsEnabled = false;
        ChkStartMenuShortcut.IsEnabled = false;
        ChkFileAssociation.IsEnabled = false;
        BtnCancel.IsEnabled = false;

        PnlProgress.Visibility = Visibility.Visible;
        TxtStatus.Text = "Preparing installation...";

        bool createDesktop = ChkDesktopShortcut.IsChecked == true;
        bool createStartMenu = ChkStartMenuShortcut.IsChecked == true;
        bool registerAssoc = ChkFileAssociation.IsChecked == true;

        bool success = await Task.Run(() => PerformInstallation(installDir, createDesktop, createStartMenu, registerAssoc));

        if (success)
        {
            _isInstalled = true;
            PbInstall.Value = 100;
            TxtStatus.Text = "Parsec Medical DICOM Viewer installed successfully!";
            TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 230, 118));
            PnlFinished.Visibility = Visibility.Visible;
            BtnInstall.Content = "Finish";
            BtnInstall.IsEnabled = true;
            BtnCancel.Visibility = Visibility.Collapsed;
        }
        else
        {
            BtnInstall.IsEnabled = true;
            BtnCancel.IsEnabled = true;
            BtnBrowse.IsEnabled = true;
            TxtInstallPath.IsEnabled = true;
            TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 82, 82));
        }
    }

    private bool PerformInstallation(string installDir, bool createDesktop, bool createStartMenu, bool registerAssoc)
    {
        try
        {
            // 1. Close any running instances of Parsec
            UpdateStatus("Closing running Parsec instances...");
            foreach (var p in Process.GetProcessesByName("Parsec"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }

            // 2. Prepare target directory
            if (!Directory.Exists(installDir))
            {
                Directory.CreateDirectory(installDir);
            }

            // 3. Locate zip payload
            Stream? zipStream = null;
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipStream = asm.GetManifestResourceStream(name);
                    break;
                }
            }

            // Fallback to adjacent payload.zip if not embedded
            if (zipStream == null)
            {
                string adj = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "payload.zip");
                if (File.Exists(adj))
                {
                    zipStream = File.OpenRead(adj);
                }
            }

            if (zipStream == null)
            {
                Dispatcher.Invoke(() => TxtStatus.Text = "Error: payload.zip archive could not be located.");
                return false;
            }

            using (zipStream)
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                int total = archive.Entries.Count;
                int current = 0;

                foreach (var entry in archive.Entries)
                {
                    current++;
                    string targetFilePath = Path.Combine(installDir, entry.FullName);

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        // It's a directory
                        Directory.CreateDirectory(targetFilePath);
                        continue;
                    }

                    string? parent = Path.GetDirectoryName(targetFilePath);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    UpdateStatus($"Extracting ({current}/{total}): {entry.Name}", (double)current / total * 90.0);
                    entry.ExtractToFile(targetFilePath, overwrite: true);
                }
            }

            string parsecExe = Path.Combine(installDir, "Parsec.exe");
            string uninstallExe = Path.Combine(installDir, "uninstall.exe");

            // 4. Create Shortcuts
            if (createDesktop)
            {
                UpdateStatus("Creating Desktop shortcut...", 92);
                string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Parsec.lnk");
                CreateShortcut(desktopPath, parsecExe, installDir, "Parsec Medical DICOM Viewer (v1.0 Beta)");
            }

            if (createStartMenu)
            {
                UpdateStatus("Creating Start Menu shortcut...", 94);
                string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Parsec");
                Directory.CreateDirectory(startMenuDir);
                string startMenuShortcut = Path.Combine(startMenuDir, "Parsec.lnk");
                CreateShortcut(startMenuShortcut, parsecExe, installDir, "Parsec Medical DICOM Viewer (v1.0 Beta)");

                // Also place an uninstall shortcut in the Start Menu folder
                if (File.Exists(uninstallExe))
                {
                    string uninstShortcut = Path.Combine(startMenuDir, "Uninstall Parsec.lnk");
                    CreateShortcut(uninstShortcut, uninstallExe, installDir, "Uninstall Parsec Medical DICOM Viewer");
                }
            }

            // 5. Register File Association (.dcm)
            if (registerAssoc)
            {
                UpdateStatus("Registering DICOM file associations (.dcm)...", 96);
                RegisterDicomAssociation(parsecExe);
            }

            // 6. Register in Windows Programs & Features
            UpdateStatus("Registering in Windows Installed Programs...", 98);
            RegisterUninstaller(installDir, parsecExe, uninstallExe);

            return true;
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = $"Installation failed: {ex.Message}";
                MessageBox.Show($"Installation error:\n\n{ex.Message}", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            });
            return false;
        }
    }

    private void UpdateStatus(string message, double progressPct = -1)
    {
        Dispatcher.Invoke(() =>
        {
            TxtStatus.Text = message;
            if (progressPct >= 0)
            {
                PbInstall.Value = progressPct;
            }
        });
    }

    private static void CreateShortcut(string shortcutPath, string targetExe, string workingDir, string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetExe;
                shortcut.WorkingDirectory = workingDir;
                shortcut.Description = description;
                shortcut.IconLocation = targetExe + ",0";
                shortcut.Save();
            }
        }
        catch { }
    }

    private static void RegisterDicomAssociation(string parsecExe)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.dcm"))
            {
                key?.SetValue("", "Parsec.Dicom");
            }

            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Parsec.Dicom"))
            {
                key?.SetValue("", "DICOM Medical Image");
            }

            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Parsec.Dicom\DefaultIcon"))
            {
                key?.SetValue("", $"\"{parsecExe}\",0");
            }

            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Parsec.Dicom\shell\open\command"))
            {
                key?.SetValue("", $"\"{parsecExe}\" \"%1\"");
            }

            // Folder context menu ("Open with Parsec")
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\Parsec"))
            {
                key?.SetValue("", "Open with Parsec");
                key?.SetValue("Icon", $"\"{parsecExe}\",0");
            }
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\Parsec\command"))
            {
                key?.SetValue("", $"\"{parsecExe}\" \"%1\"");
            }

            // Folder background context menu ("Open with Parsec" inside open folder)
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\Parsec"))
            {
                key?.SetValue("", "Open with Parsec");
                key?.SetValue("Icon", $"\"{parsecExe}\",0");
            }
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\Parsec\command"))
            {
                key?.SetValue("", $"\"{parsecExe}\" \"%V\"");
            }

            // Refresh Windows Explorer file icon cache
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static void RegisterUninstaller(string installDir, string parsecExe, string uninstallExe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Parsec");
            if (key != null)
            {
                key.SetValue("DisplayName", "Parsec Medical DICOM Viewer");
                key.SetValue("DisplayVersion", "1.0 Beta");
                key.SetValue("Publisher", "Parsec Medical Imaging");
                key.SetValue("DisplayIcon", $"\"{parsecExe}\",0");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("UninstallString", $"\"{uninstallExe}\"");
                key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" --cleanup \"{installDir}\"");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

                try
                {
                    long totalBytes = Directory.GetFiles(installDir, "*.*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
                    key.SetValue("EstimatedSize", (int)(totalBytes / 1024), RegistryValueKind.DWord);
                }
                catch { }
            }
        }
        catch { }
    }
}
