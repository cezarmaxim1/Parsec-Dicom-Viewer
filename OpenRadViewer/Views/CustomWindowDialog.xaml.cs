// @purpose: Modal input dialog for setting precise numerical Window Center and Window Width values on a viewport.
// @depends: OpenRadViewer.ViewModels
// @exports: CustomWindowDialog

using System.Windows;
using OpenRadViewer.ViewModels;

namespace OpenRadViewer.Views;

public partial class CustomWindowDialog : Window
{
    private readonly ViewportViewModel _viewport;

    public CustomWindowDialog(ViewportViewModel viewport)
    {
        InitializeComponent();
        _viewport = viewport;
        TxtCenter.Text = ((int)_viewport.WindowCenter).ToString();
        TxtWidth.Text = ((int)_viewport.WindowWidth).ToString();
        TxtCenter.Focus();
        TxtCenter.SelectAll();
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
            _viewport.SetWindowPreset(center, width);
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show("Please enter valid numerical values for Center and Width.", "Invalid Values", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
