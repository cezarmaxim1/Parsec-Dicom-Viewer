using System.Windows;
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

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
