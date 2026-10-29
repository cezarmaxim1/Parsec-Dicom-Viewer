// @purpose: Compact modal dialog prompting for optional emergency report header fields (Nr, Clinica, Doctor, FO, Diagnostic) before saving as URG.docx.
// @depends: System.Windows, OpenRadViewer.Core.Services
// @exports: UrgReportMetadataDialog

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public class UrgReportMetadataDialog : Window
{
    private readonly TextBox _txtNr;
    private readonly TextBox _txtClinica;
    private readonly TextBox _txtDoctor;
    private readonly TextBox _txtFo;
    private readonly TextBox _txtDiagnostic;

    public bool IsSkipped { get; private set; }

    public string Nr => _txtNr.Text?.Trim() ?? string.Empty;
    public string Clinica => _txtClinica.Text?.Trim() ?? "UPU";
    public string Doctor => _txtDoctor.Text?.Trim() ?? string.Empty;
    public string Fo => _txtFo.Text?.Trim() ?? string.Empty;
    public string Diagnostic => _txtDiagnostic.Text?.Trim() ?? string.Empty;

    public UrgReportMetadataDialog(
        string patientName,
        string cnp,
        string examDate,
        string examName,
        string contrast,
        string dlp,
        string accessionNumber = "")
    {
        Title = "Completare Antet Urgență (URG CT)";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
        Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));

        var root = new StackPanel { Margin = new Thickness(16) };

        // 1. Header Title & Badge
        var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x20, 0x10)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0x4B, 0x20)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        badge.Child = new TextBlock
        {
            Text = "URGENTA",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26))
        };
        DockPanel.SetDock(badge, Dock.Right);
        headerPanel.Children.Add(badge);

        headerPanel.Children.Add(new TextBlock
        {
            Text = "Antet Buletin CT Urgență",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xAF, 0x28))
        });
        root.Children.Add(headerPanel);

        // 2. Auto-Extracted CT Info Banner (Read-only summary)
        var autoInfoBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var autoGrid = new Grid();
        autoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        autoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        autoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        autoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        autoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddInfoRow(autoGrid, 0, 0, "PACIENT:", patientName, Color.FromRgb(0xFF, 0xFF, 0xFF), isBold: true);
        AddInfoRow(autoGrid, 0, 1, "CNP:", string.IsNullOrWhiteSpace(cnp) ? "-" : cnp, Color.FromRgb(0x40, 0xC0, 0xFF));
        AddInfoRow(autoGrid, 1, 0, "DATA:", examDate, Color.FromRgb(0xAA, 0xAA, 0xAA));
        AddInfoRow(autoGrid, 1, 1, "EXAMEN:", examName, Color.FromRgb(0x80, 0xE8, 0x80));
        AddInfoRow(autoGrid, 2, 0, "CONTRAST:", contrast, contrast == "DA" ? Color.FromRgb(0xFF, 0xA7, 0x26) : Color.FromRgb(0x88, 0x88, 0x88));
        AddInfoRow(autoGrid, 2, 1, "DLP:", string.IsNullOrWhiteSpace(dlp) ? "- mGy*cm" : $"{dlp} mGy*cm", Color.FromRgb(0xFF, 0xD5, 0x4F));

        autoInfoBorder.Child = autoGrid;
        root.Children.Add(autoInfoBorder);

        root.Children.Add(new TextBlock
        {
            Text = "Completați câmpurile dorite sau apăsați Skip pentru a le lăsa goale:",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        // 3. Input fields: Nr, Clinica, Doctor, FO, Diagnostic
        var formGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < 5; i++)
            formGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

        // Row 0: Nr.
        AddFormLabel(formGrid, 0, "Nr. examinare:");
        _txtNr = CreateTextBox("ex. 1234");
        Grid.SetRow(_txtNr, 0); Grid.SetColumn(_txtNr, 1);
        formGrid.Children.Add(_txtNr);

        // Row 1: Clinica
        AddFormLabel(formGrid, 1, "Clinica:");
        _txtClinica = CreateTextBox("UPU");
        _txtClinica.Text = "UPU";
        Grid.SetRow(_txtClinica, 1); Grid.SetColumn(_txtClinica, 1);
        formGrid.Children.Add(_txtClinica);

        // Row 2: Doctor
        AddFormLabel(formGrid, 2, "Doctor (DR.):");
        _txtDoctor = CreateTextBox("ex. NEDELEA");
        Grid.SetRow(_txtDoctor, 2); Grid.SetColumn(_txtDoctor, 1);
        formGrid.Children.Add(_txtDoctor);

        // Row 3: FO
        AddFormLabel(formGrid, 3, "FO:");
        _txtFo = CreateTextBox("Foaie de observație");
        if (!string.IsNullOrWhiteSpace(accessionNumber)) _txtFo.Text = accessionNumber;
        Grid.SetRow(_txtFo, 3); Grid.SetColumn(_txtFo, 1);
        formGrid.Children.Add(_txtFo);

        // Row 4: Diagnostic de trimitere
        AddFormLabel(formGrid, 4, "Diagnostic trimitere:");
        _txtDiagnostic = CreateTextBox("ex. Traumatism toraco-abdominal");
        Grid.SetRow(_txtDiagnostic, 4); Grid.SetColumn(_txtDiagnostic, 1);
        formGrid.Children.Add(_txtDiagnostic);

        root.Children.Add(formGrid);

        // 4. Action Buttons (Ok, Skip, Cancel)
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var btnCancel = new Button
        {
            Content = "Cancel",
            Width = 72,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x3E)),
            Cursor = Cursors.Hand
        };
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        btnPanel.Children.Add(btnCancel);

        var btnSkip = new Button
        {
            Content = "Skip",
            Width = 72,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
            ToolTip = "Salvează fără a completa aceste câmpuri (rămân goale în antet)",
            Cursor = Cursors.Hand
        };
        btnSkip.Click += (_, _) =>
        {
            IsSkipped = true;
            DialogResult = true;
            Close();
        };
        btnPanel.Children.Add(btnSkip);

        var btnOk = new Button
        {
            Content = "OK",
            Width = 84,
            Height = 26,
            Background = new SolidColorBrush(Color.FromRgb(0xC8, 0x4B, 0x20)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00)),
            Cursor = Cursors.Hand,
            IsDefault = true
        };
        btnOk.Click += (_, _) =>
        {
            IsSkipped = false;
            DialogResult = true;
            Close();
        };
        btnPanel.Children.Add(btnOk);

        root.Children.Add(btnPanel);
        Content = root;

        Loaded += (_, _) =>
        {
            DarkWindowHelper.ApplyDarkTitleBar(this);
            _txtDoctor.Focus();
        };

        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    private static void AddInfoRow(Grid grid, int row, int col, string label, string val, Color valColor, bool isBold = false)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(col == 0 ? 0 : 8, 2, 0, 2)
        };
        sp.Children.Add(new TextBlock
        {
            Text = label + " ",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontWeight = FontWeights.SemiBold
        });
        sp.Children.Add(new TextBlock
        {
            Text = val,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(valColor),
            FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetRow(sp, row);
        Grid.SetColumn(sp, col);
        grid.Children.Add(sp);
    }

    private static void AddFormLabel(Grid grid, int row, string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, 0);
        grid.Children.Add(tb);
    }

    private static TextBox CreateTextBox(string placeholder)
    {
        return new TextBox
        {
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 11.5,
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x3E)),
            BorderThickness = new Thickness(1)
        };
    }
}
