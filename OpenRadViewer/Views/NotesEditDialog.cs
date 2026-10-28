// @purpose: Small dark modal dialog for editing the local clinical "Notes" of a study (PACS search / Local Database).
// @depends: OpenRadViewer.Core.Services (DarkWindowHelper)
// @exports: NotesEditDialog

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public class NotesEditDialog : Window
{
    private readonly TextBox _txt;

    /// <summary>The edited text (valid only when ShowDialog() returned true).</summary>
    public string NotesText => _txt.Text?.Trim() ?? string.Empty;

    public NotesEditDialog(string patientName, string currentNotes)
    {
        Title = "Edit Notes";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(patientName) ? "Notes" : patientName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xAF, 0x28)),
            Margin = new Thickness(0, 0, 0, 8),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        _txt = new TextBox
        {
            Text = currentNotes ?? string.Empty,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12.5,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1)
        };
        root.Children.Add(_txt);

        root.Children.Add(new TextBlock
        {
            Text = "Saved locally only (not sent to PACS). Leave empty to revert to the PACS notes.",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnSave = MakeButton("Save", true);
        var btnCancel = MakeButton("Cancel", false);
        btnSave.Click += (_, _) => { DialogResult = true; };
        btnCancel.Click += (_, _) => { DialogResult = false; };
        buttons.Children.Add(btnSave);
        buttons.Children.Add(btnCancel);
        root.Children.Add(buttons);

        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };

        Loaded += (_, _) =>
        {
            try { DarkWindowHelper.ApplyDarkTitleBar(this); } catch { }
            _txt.Focus();
            _txt.SelectAll();
        };
    }

    private static Button MakeButton(string text, bool primary)
    {
        return new Button
        {
            Content = text,
            MinWidth = 80,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            Cursor = Cursors.Hand,
            Background = primary
                ? new SolidColorBrush(Color.FromRgb(0xF5, 0xAF, 0x28))
                : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground = primary ? Brushes.Black : new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal
        };
    }
}
