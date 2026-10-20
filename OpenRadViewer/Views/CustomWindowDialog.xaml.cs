// @purpose: Modal input dialog for numerical Window Center/Width and managing/creating user-defined custom window presets.
// @depends: OpenRadViewer.ViewModels, OpenRadViewer.Core.Services (WindowPresetService, DarkWindowHelper), OpenRadViewer.Core.Models (WindowPresetModel)
// @exports: CustomWindowDialog

using System.Windows;
using System.Windows.Controls;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;
using OpenRadViewer.ViewModels;

namespace OpenRadViewer.Views;

public partial class CustomWindowDialog : Window
{
    private readonly ViewportViewModel _viewport;

    public CustomWindowDialog(ViewportViewModel viewport)
    {
        InitializeComponent();
        DarkWindowHelper.ApplyDarkTitleBar(this);

        _viewport = viewport;
        TxtCenter.Text = ((int)_viewport.WindowCenter).ToString();
        TxtWidth.Text = ((int)_viewport.WindowWidth).ToString();

        RefreshCustomPresetsList();

        TxtCenter.Focus();
        TxtCenter.SelectAll();
    }

    private void RefreshCustomPresetsList()
    {
        LstCustomPresets.ItemsSource = WindowPresetService.Instance.GetCustomPresets();
    }

    private void OnSavePresetChecked(object sender, RoutedEventArgs e)
    {
        LblPresetName.Visibility = Visibility.Visible;
        TxtPresetName.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(TxtPresetName.Text))
        {
            TxtPresetName.Text = "Custom Preset";
            TxtPresetName.SelectAll();
            TxtPresetName.Focus();
        }
    }

    private void OnSavePresetUnchecked(object sender, RoutedEventArgs e)
    {
        LblPresetName.Visibility = Visibility.Collapsed;
        TxtPresetName.Visibility = Visibility.Collapsed;
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LstCustomPresets.SelectedItem is WindowPresetModel preset)
        {
            TxtCenter.Text = ((int)preset.WindowCenter).ToString();
            TxtWidth.Text = ((int)preset.WindowWidth).ToString();
        }
    }

    private void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var res = MessageBox.Show("Delete this custom preset?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                WindowPresetService.Instance.DeleteCustomPreset(id);
                RefreshCustomPresetsList();
            }
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(TxtCenter.Text, out double center) && double.TryParse(TxtWidth.Text, out double width))
        {
            if (width < 1) width = 1;

            if (ChkSavePreset.IsChecked == true)
            {
                string name = TxtPresetName.Text.Trim();
                if (string.IsNullOrWhiteSpace(name)) name = $"Custom ({center:0}/{width:0})";
                WindowPresetService.Instance.AddCustomPreset(name, center, width);
            }

            _viewport.SetWindowPreset(center, width);
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show("Please enter valid numerical values for Window Center and Window Width.", "Invalid Values", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
