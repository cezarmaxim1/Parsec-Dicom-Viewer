// @purpose: Modal dialog for adding custom user reference images or tables (with title and file browse) to the ExtraTools menu.
// @depends: OpenRadViewer.Core.Models, OpenRadViewer.Core.Services (DarkWindowHelper)
// @exports: AddReferenceGuideDialog

using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenRadViewer.Core.Models;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public partial class AddReferenceGuideDialog : Window
{
    public string Category { get; }
    public ReferenceGuideItem? CreatedItem { get; private set; }

    public AddReferenceGuideDialog(string category = "Classification")
    {
        InitializeComponent();
        Category = category;
        DarkWindowHelper.ApplyDarkTitleBar(this);

        if (category == "Table")
        {
            Title = "Add Custom Reference Table";
            TxtDialogHeader.Text = "Add New Reference Table";
        }
        else
        {
            Title = "Add Custom Classification";
            TxtDialogHeader.Text = "Add New Classification Guide";
        }

        TxtTitle.Focus();
    }

    private void OnBrowseImageClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Reference Image",
            Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (dlg.ShowDialog() == true)
        {
            string path = dlg.FileName;
            TxtImagePath.Text = path;

            try
            {
                var bmp = new BitmapImage();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                }
                ImgPreview.Source = bmp;
                TxtNoPreview.Visibility = Visibility.Collapsed;

                if (string.IsNullOrWhiteSpace(TxtTitle.Text))
                {
                    TxtTitle.Text = Path.GetFileNameWithoutExtension(path).Replace('_', ' ');
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load image preview:\n{ex.Message}", "Image Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string title = TxtTitle.Text.Trim();
        string imagePath = TxtImagePath.Text.Trim();
        string desc = TxtDescription.Text.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            MessageBox.Show(this, "Please enter a title for the reference guide.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtTitle.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            MessageBox.Show(this, "Please select a valid image file from disk.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            CreatedItem = ReferenceGuideService.Instance.AddCustomItem(title, imagePath, Category, desc);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to save reference guide:\n{ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
