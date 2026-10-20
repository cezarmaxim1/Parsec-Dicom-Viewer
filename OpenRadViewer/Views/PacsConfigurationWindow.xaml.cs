// @purpose: Configuration management UI for DICOM PACS nodes, local AE Title / port listeners, and C-ECHO connectivity verification.
// @depends: OpenRadViewer.Core.Services (PacsService, PacsModels, DarkWindowHelper)
// @exports: PacsConfigurationWindow

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public partial class PacsConfigurationWindow : Window
{
    public ObservableCollection<PacsServerConfig> Servers { get; set; } = new();
    private PacsServerConfig? _editingServer = null;

    public PacsConfigurationWindow()
    {
        InitializeComponent();
        DataContext = this;
        DarkWindowHelper.ApplyDarkTitleBar(this);
        try
        {
            string iconFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_icon.png");
            if (System.IO.File.Exists(iconFile))
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconFile, UriKind.Absolute));
            }
        }
        catch { }
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        var settings = PacsService.Instance.Settings;
        TxtListenerPort.Text = settings.ListenerPort.ToString();
        TxtLocalAeTitle.Text = settings.LocalAeTitle;

        Servers.Clear();
        foreach (var s in settings.Servers)
        {
            Servers.Add(s);
        }

        int days = settings.CacheRetentionDays;
        CmbCacheRetention.SelectedIndex = days switch
        {
            0 => 0,
            1 => 1,
            3 => 2,
            7 => 3,
            -1 => 4,
            _ => 0
        };

        UpdateListenerUi();
        UpdateCacheUi();
    }

    private void UpdateCacheUi()
    {
        Task.Run(() =>
        {
            var stats = PacsService.Instance.GetCacheStats();
            string sizeStr = FormatBytes(stats.TotalBytes);
            Dispatcher.Invoke(() =>
            {
                TxtCacheSize.Text = $"{sizeStr} ({stats.FileCount} files)";
            });
        });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int digitGroups = (int)(Math.Log(bytes) / Math.Log(1024));
        digitGroups = Math.Min(digitGroups, units.Length - 1);
        return $"{bytes / Math.Pow(1024, digitGroups):F2} {units[digitGroups]}";
    }

    private void OnOpenCacheDirClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = PacsService.Instance.Settings.CacheDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error opening folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClearCacheNowClicked(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show("Are you sure you want to delete all temporarily cached PACS files?\n\n(Studies imported into the local Database will not be affected.)", "Confirm Cache Purge", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res == MessageBoxResult.Yes)
        {
            PacsService.Instance.ClearCache(0);
            UpdateCacheUi();
            MessageBox.Show("Temporary PACS cache was cleared successfully.", "Cache Cleared", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void UpdateListenerUi()
    {
        bool isRunning = PacsService.Instance.IsListenerRunning;
        ListenerLed.Fill = isRunning ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Color.FromRgb(244, 67, 54));
        TxtListenerStatus.Text = PacsService.Instance.ListenerStatusMessage;
        BtnToggleListener.Content = isRunning ? "Stop Receiver" : "Start Receiver";
    }

    private void OnToggleListenerClicked(object sender, RoutedEventArgs e)
    {
        if (PacsService.Instance.IsListenerRunning)
        {
            PacsService.Instance.StopListener();
        }
        else
        {
            if (int.TryParse(TxtListenerPort.Text.Trim(), out int port))
            {
                PacsService.Instance.Settings.ListenerPort = port;
                PacsService.Instance.Settings.LocalAeTitle = TxtLocalAeTitle.Text.Trim();
            }
            PacsService.Instance.StartListener(out string err);
            if (!string.IsNullOrEmpty(err))
            {
                MessageBox.Show(err, "C-STORE Listener Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        UpdateListenerUi();
    }

    private void OnServerSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        bool hasSelection = GridServers.SelectedItem is PacsServerConfig;
        BtnEditServer.IsEnabled = hasSelection;
        BtnDeleteServer.IsEnabled = hasSelection;
    }

    private void OnGridServersDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GridServers.SelectedItem is PacsServerConfig)
        {
            OnEditServerClicked(sender, e);
        }
    }

    private void OnAddServerClicked(object sender, RoutedEventArgs e)
    {
        _editingServer = null;
        TxtEditTitle.Text = "Add New PACS Location";
        TxtEditDesc.Text = "PXDPACS_NEW";
        TxtEditIp.Text = "192.168.1.";
        TxtEditPort.Text = "4020";
        TxtEditAe.Text = "PXDPACS";
        CmbEditMode.SelectedIndex = 0;
        CmbEditSyntax.SelectedIndex = 0;
        EditPanel.Visibility = Visibility.Visible;
    }

    private void OnEditServerClicked(object sender, RoutedEventArgs e)
    {
        if (GridServers.SelectedItem is not PacsServerConfig s) return;
        _editingServer = s;
        TxtEditTitle.Text = $"Edit Location: {s.Description}";
        TxtEditDesc.Text = s.Description;
        TxtEditIp.Text = s.IpAddress;
        TxtEditPort.Text = s.Port.ToString();
        TxtEditAe.Text = s.AeTitle;
        CmbEditMode.SelectedIndex = s.RetrievalMode == "C-GET" ? 1 : 0;
        EditPanel.Visibility = Visibility.Visible;
    }

    private void OnCancelEditClicked(object sender, RoutedEventArgs e)
    {
        EditPanel.Visibility = Visibility.Collapsed;
        _editingServer = null;
    }

    private void OnSaveServerEditClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtEditDesc.Text) || string.IsNullOrWhiteSpace(TxtEditIp.Text))
        {
            MessageBox.Show("Please enter a description and IP address for the PACS server.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(TxtEditPort.Text.Trim(), out int port))
        {
            port = 4020;
        }

        string mode = CmbEditMode.SelectedIndex == 1 ? "C-GET" : "C-MOVE";
        string syntax = CmbEditSyntax.SelectedIndex == 1 ? "Implicit VR Little Endian" : "Explicit VR Little Endian";

        if (_editingServer == null)
        {
            var newServer = new PacsServerConfig
            {
                Description = TxtEditDesc.Text.Trim(),
                IpAddress = TxtEditIp.Text.Trim(),
                Port = port,
                AeTitle = TxtEditAe.Text.Trim(),
                RetrievalMode = mode,
                TransferSyntax = syntax,
                Status = "Untested"
            };
            Servers.Add(newServer);
        }
        else
        {
            _editingServer.Description = TxtEditDesc.Text.Trim();
            _editingServer.IpAddress = TxtEditIp.Text.Trim();
            _editingServer.Port = port;
            _editingServer.AeTitle = TxtEditAe.Text.Trim();
            _editingServer.RetrievalMode = mode;
            _editingServer.TransferSyntax = syntax;
            GridServers.Items.Refresh();
        }

        EditPanel.Visibility = Visibility.Collapsed;
        _editingServer = null;
    }

    private void OnDeleteServerClicked(object sender, RoutedEventArgs e)
    {
        if (GridServers.SelectedItem is not PacsServerConfig s) return;
        var res = MessageBox.Show($"Are you sure you want to delete PACS server '{s.Description}' ({s.IpAddress}:{s.Port})?", "Confirm Deletion", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res == MessageBoxResult.Yes)
        {
            Servers.Remove(s);
        }
    }

    private async void OnTestEchoSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (GridServers.SelectedItem is not PacsServerConfig s)
        {
            MessageBox.Show("Please select a PACS server from the list for C-ECHO test.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        s.Status = "Testing...";
        GridServers.Items.Refresh();

        var (success, latency, msg) = await PacsService.Instance.TestEchoAsync(s);
        GridServers.Items.Refresh();

        if (success)
        {
            MessageBox.Show($"C-ECHO successful!\n\nServer: {s.Description} ({s.AeTitle})\nAddress: {s.IpAddress}:{s.Port}\nPing latency: {latency} ms", "Connection Successful (200 OK)", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"C-ECHO test failed for {s.Description}:\n\n{msg}\n\nPlease check:\n1. PACS server is running and reachable via network/VPN.\n2. Port {s.Port} is open in the firewall.\n3. Server recognizes local AE Title '{TxtLocalAeTitle.Text}'.", "PACS Connection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnTestEchoAllClicked(object sender, RoutedEventArgs e)
    {
        foreach (var s in Servers)
        {
            s.Status = "Testing...";
            GridServers.Items.Refresh();
            await PacsService.Instance.TestEchoAsync(s);
            GridServers.Items.Refresh();
        }
    }

    private void OnSaveAllClicked(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TxtListenerPort.Text.Trim(), out int port))
        {
            PacsService.Instance.Settings.ListenerPort = port;
        }
        PacsService.Instance.Settings.LocalAeTitle = TxtLocalAeTitle.Text.Trim();
        PacsService.Instance.Settings.Servers = Servers.ToList();
        
        int retIndex = CmbCacheRetention.SelectedIndex;
        PacsService.Instance.Settings.CacheRetentionDays = retIndex switch
        {
            0 => 0,
            1 => 1,
            2 => 3,
            3 => 7,
            4 => -1,
            _ => 0
        };

        PacsService.Instance.SaveSettings();

        // Restart listener if port changed
        if (PacsService.Instance.IsListenerRunning)
        {
            PacsService.Instance.StopListener();
            PacsService.Instance.StartListener(out _);
        }

        DialogResult = true;
        Close();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
