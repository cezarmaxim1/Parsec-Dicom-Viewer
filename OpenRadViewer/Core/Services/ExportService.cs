// @purpose: Utility for encoding and saving BitmapSource instances into PNG, JPEG, BMP, or TIFF format.
// @depends: System.Windows.Media.Imaging, System.IO
// @exports: ExportService

using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace OpenRadViewer.Core.Services;

public static class ExportService
{
    public static void SaveBitmapToFile(BitmapSource bitmap, string filePath, string format = "PNG")
    {
        BitmapEncoder encoder = format.ToUpperInvariant() switch
        {
            "JPEG" or "JPG" => new JpegBitmapEncoder { QualityLevel = 95 },
            "BMP" => new BmpBitmapEncoder(),
            "TIFF" or "TIF" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };

        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
    }

    public static void CopyBitmapToClipboard(BitmapSource bitmap)
    {
        Clipboard.SetImage(bitmap);
    }
}
