using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace OpenRadViewer.Views;

public partial class PfirrmannClassificationWindow : Window
{
    public PfirrmannClassificationWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        LoadClassificationImage();
    }

    private void LoadClassificationImage()
    {
        // 1. Try pack URI for png or jpg
        foreach (var ext in new[] { "png", "jpg" })
        {
            try
            {
                var packUri = new Uri($"pack://application:,,,/Assets/pfirrmann_classification.{ext}", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = packUri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                PfirrmannImage.Source = bmp;
                return;
            }
            catch
            {
                // continue to next check
            }
        }

        // 2. Try disk paths
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine(baseDir, "Assets", "pfirrmann_classification.png"),
                Path.Combine(baseDir, "Assets", "pfirrmann_classification.jpg"),
                Path.Combine(baseDir, "..", "..", "..", "Assets", "pfirrmann_classification.png"),
                Path.Combine(baseDir, "..", "..", "..", "Assets", "pfirrmann_classification.jpg")
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    PfirrmannImage.Source = bmp;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load Pfirrmann image: {ex.Message}");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
