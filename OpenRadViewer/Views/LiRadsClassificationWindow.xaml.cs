using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public partial class LiRadsClassificationWindow : Window
{
    public LiRadsClassificationWindow()
    {
        InitializeComponent();
        DarkWindowHelper.ApplyDarkTitleBar(this);
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        LoadClassificationImage();
    }

    private void LoadClassificationImage()
    {
        string[] fileNames = new[] { "lirads_classification.png", "lirads_classification.jpg", "lirads.png", "lirads.jpg" };

        // 1. Try pack URIs
        foreach (var file in fileNames)
        {
            try
            {
                var packUri = new Uri($"pack://application:,,,/Assets/{file}", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = packUri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                LiRadsImage.Source = bmp;
                return;
            }
            catch
            {
                // Continue check
            }
        }

        // 2. Try disk paths
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var file in fileNames)
            {
                string[] candidates = new[]
                {
                    Path.Combine(baseDir, "Assets", file),
                    Path.Combine(baseDir, "..", "..", "..", "Assets", file),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file)
                };

                foreach (var path in candidates)
                {
                    if (File.Exists(path))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(Path.GetFullPath(path));
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        LiRadsImage.Source = bmp;
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load LI-RADS image: {ex.Message}");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
