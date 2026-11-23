using System;
using System.Windows;
using System.Windows.Media;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public partial class AboutWindow : Window
{
    private bool _isShortcutsExpanded = false;

    public AboutWindow()
    {
        InitializeComponent();
        DarkWindowHelper.ApplyDarkTitleBar(this);
        ChkAnnounceUpdates.IsChecked = AppUpdateService.AnnounceOnStartup;
    }

    private void OnAnnounceUpdatesChanged(object sender, RoutedEventArgs e)
    {
        AppUpdateService.AnnounceOnStartup = ChkAnnounceUpdates.IsChecked == true;
    }

    private void OnToggleShortcuts(object sender, RoutedEventArgs e)
    {
        _isShortcutsExpanded = !_isShortcutsExpanded;
        ShortcutsContainer.Visibility = _isShortcutsExpanded ? Visibility.Visible : Visibility.Collapsed;
        TxtShortcutsHeader.Text = _isShortcutsExpanded 
            ? "Click to hide keyboard shortcuts reference ▴" 
            : "Click here to view keyboard shortcuts reference ▾";
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdates.IsEnabled = false;
        BtnCheckUpdates.Content = "Checking...";
        TxtUpdateStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00E5FF"));
        TxtUpdateStatus.Text = "Checking GitHub for updates...";
        TxtUpdateStatus.Visibility = Visibility.Visible;

        try
        {
            var res = await AppUpdateService.CheckForUpdatesAsync();
            if (!string.IsNullOrWhiteSpace(res.ErrorMessage))
            {
                TxtUpdateStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B6B"));
                TxtUpdateStatus.Text = "Update check failed";
                MessageBox.Show(
                    this,
                    $"Could not check GitHub for updates:\n{res.ErrorMessage}",
                    "Parsec Update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!res.IsUpdateAvailable)
            {
                TxtUpdateStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64D2B4"));
                TxtUpdateStatus.Text = $"Up to date ({AppUpdateService.CurrentDisplayVersion})";
                MessageBox.Show(
                    this,
                    $"Parsec is up to date ({AppUpdateService.CurrentDisplayVersion}).",
                    "Parsec Update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            TxtUpdateStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5AF28"));
            TxtUpdateStatus.Text = $"Update available: {res.LatestVersion}";

            string notes = string.IsNullOrWhiteSpace(res.ReleaseNotes) ? "" : $"\n\n{res.ReleaseNotes}";
            var answer = MessageBox.Show(
                this,
                $"A new version is available on GitHub ({res.LatestVersion}).{notes}\n\nDownload and install it now?",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            BtnCheckUpdates.Content = "Updating...";
            var progress = new Progress<(double percent, string status)>(p =>
            {
                TxtUpdateStatus.Text = p.status;
            });

            await AppUpdateService.DownloadAndInstallUpdateAsync(res.DownloadUrl, progress);
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B6B"));
            TxtUpdateStatus.Text = "Update error";
            MessageBox.Show(
                this,
                $"The update failed:\n{ex.Message}",
                "Update Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            BtnCheckUpdates.IsEnabled = true;
            BtnCheckUpdates.Content = "Check for Updates";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
