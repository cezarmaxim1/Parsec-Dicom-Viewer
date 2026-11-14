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
                    $"Nu s-a putut verifica actualizarea pe GitHub:\n{res.ErrorMessage}",
                    "Actualizare Parsec",
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
                    $"Aplicația Parsec este deja la cea mai recentă versiune ({AppUpdateService.CurrentDisplayVersion}).",
                    "Actualizare Parsec",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            TxtUpdateStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5AF28"));
            TxtUpdateStatus.Text = $"Update available: {res.LatestVersion}";

            string notes = string.IsNullOrWhiteSpace(res.ReleaseNotes) ? "" : $"\n\n{res.ReleaseNotes}";
            var answer = MessageBox.Show(
                this,
                $"Este disponibilă o versiune nouă pe GitHub ({res.LatestVersion}).{notes}\n\nDoriți să descărcați și să instalați actualizarea acum?",
                "Actualizare Disponibilă",
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
                $"Eroare în timpul actualizării:\n{ex.Message}",
                "Eroare Actualizare",
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
