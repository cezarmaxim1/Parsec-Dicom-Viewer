// @purpose: Interactive ROI / 2D measurement annotations on viewports (Length, Ellipse, Rectangle, Angle, Arrow, Spine, CobbAngle, SpineLabel, InsallSalvati, AiCrop).
// @depends: System.Windows, OpenRadViewer.Core.Services
// @exports: MeasurementType, MeasurementItem, VertebraLabelItem

using System.Windows;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Core.Models;

public enum MeasurementType
{
    Length,
    Ellipse,
    Rectangle,
    Angle,
    Arrow,
    Pencil,
    InsallSalvati,
    AiCrop
}

public class MeasurementItem
{
    public MeasurementType Type { get; set; }
    public List<Point> Points { get; set; } = new(); // Image coordinate space (pixels)
    public string Label { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public bool IsSelected { get; set; }
    public int SliceIndex { get; set; }

    public double AreaCm2 { get; set; }
    public double MeanHu { get; set; }
    public double SdHu { get; set; }
    public double MinHu { get; set; }
    public double MaxHu { get; set; }

    public double CalculateLengthMm(double spacingX, double spacingY)
    {
        if (Points.Count < 2) return 0.0;
        double dx = (Points[1].X - Points[0].X) * spacingX;
        double dy = (Points[1].Y - Points[0].Y) * spacingY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double CalculateAngleDegrees()
    {
        if (Points.Count < 3) return 0.0;
        var v1 = new Vector(Points[0].X - Points[1].X, Points[0].Y - Points[1].Y);
        var v2 = new Vector(Points[2].X - Points[1].X, Points[2].Y - Points[1].Y);
        double angle = Math.Abs(Vector.AngleBetween(v1, v2));
        return angle;
    }

    public (double ratio, double tlMm, double plMm) CalculateInsallSalvati(double spacingX, double spacingY)
    {
        if (Points.Count < 3) return (0.0, 0.0, 0.0);
        double spX = spacingX > 0 ? spacingX : 1.0;
        double spY = spacingY > 0 ? spacingY : 1.0;

        // Points[0]: Inserție tuberozitate tibie
        // Points[1]: Pol inferior patelă (apex)
        // Points[2]: Pol superior patelă (bază)
        double dx1 = (Points[1].X - Points[0].X) * spX;
        double dy1 = (Points[1].Y - Points[0].Y) * spY;
        double tlMm = Math.Sqrt(dx1 * dx1 + dy1 * dy1);

        double dx2 = (Points[2].X - Points[1].X) * spX;
        double dy2 = (Points[2].Y - Points[1].Y) * spY;
        double plMm = Math.Sqrt(dx2 * dx2 + dy2 * dy2);

        double ratio = plMm > 0.001 ? tlMm / plMm : 0.0;
        return (ratio, tlMm, plMm);
    }

    public static string FormatInsallSalvatiDetails(double ratio, double tlMm, double plMm)
    {
        string status = ratio switch
        {
            > 1.50 => "Patella alta",
            < 0.74 => "Patella baja",
            _ => "Normal"
        };
        return $"IS: {ratio:0.00} [{status}] (TL: {tlMm:0.#}mm, PL: {plMm:0.#}mm)";
    }

    public void ComputeEllipseStatistics(DicomSliceModel slice)
    {
        if (Points.Count < 2) return;

        double x0 = Math.Min(Points[0].X, Points[1].X);
        double x1 = Math.Max(Points[0].X, Points[1].X);
        double y0 = Math.Min(Points[0].Y, Points[1].Y);
        double y1 = Math.Max(Points[0].Y, Points[1].Y);

        double cx = (x0 + x1) / 2.0;
        double cy = (y0 + y1) / 2.0;
        double rx = (x1 - x0) / 2.0;
        double ry = (y1 - y0) / 2.0;

        if (rx < 1 || ry < 1) return;

        // Area in cm^2
        double rxMm = rx * slice.PixelSpacingX;
        double ryMm = ry * slice.PixelSpacingY;
        AreaCm2 = (Math.PI * rxMm * ryMm) / 100.0;

        // Pixel statistics inside ellipse
        int width = slice.Columns;
        int height = slice.Rows;

        int minX = Math.Max(0, (int)Math.Floor(x0));
        int maxX = Math.Min(width - 1, (int)Math.Ceiling(x1));
        int minY = Math.Max(0, (int)Math.Floor(y0));
        int maxY = Math.Min(height - 1, (int)Math.Ceiling(y1));

        double sum = 0;
        double sumSq = 0;
        int count = 0;
        double minVal = double.MaxValue;
        double maxVal = double.MinValue;

        DicomLoaderService.LoadPixelData(slice);

        for (int y = minY; y <= maxY; y++)
        {
            double dyNorm = (y - cy) / ry;
            double dySq = dyNorm * dyNorm;
            if (dySq > 1.0) continue;

            for (int x = minX; x <= maxX; x++)
            {
                double dxNorm = (x - cx) / rx;
                if (dxNorm * dxNorm + dySq <= 1.0)
                {
                    int pixelIndex = y * width + x;
                    double pixelValue = 0;

                    if (slice.RawPixelsUnsigned != null && pixelIndex < slice.RawPixelsUnsigned.Length)
                        pixelValue = slice.RawPixelsUnsigned[pixelIndex] * slice.RescaleSlope + slice.RescaleIntercept;
                    else if (slice.RawPixelsSigned != null && pixelIndex < slice.RawPixelsSigned.Length)
                        pixelValue = slice.RawPixelsSigned[pixelIndex] * slice.RescaleSlope + slice.RescaleIntercept;

                    sum += pixelValue;
                    sumSq += pixelValue * pixelValue;
                    count++;

                    if (pixelValue < minVal) minVal = pixelValue;
                    if (pixelValue > maxVal) maxVal = pixelValue;
                }
            }
        }

        if (count > 0)
        {
            MeanHu = sum / count;
            double variance = (sumSq / count) - (MeanHu * MeanHu);
            SdHu = variance > 0 ? Math.Sqrt(variance) : 0;
            MinHu = minVal;
            MaxHu = maxVal;
            Details = $"Mean: {MeanHu:0.0} HU  SD: {SdHu:0.0}\nMin: {MinHu:0.#}  Max: {MaxHu:0.#}\nArea: {AreaCm2:0.00} cm² ({count} px)";
        }
    }

    public void ComputeRectangleStatistics(DicomSliceModel slice)
    {
        if (Points.Count < 2) return;

        double x0 = Math.Min(Points[0].X, Points[1].X);
        double x1 = Math.Max(Points[0].X, Points[1].X);
        double y0 = Math.Min(Points[0].Y, Points[1].Y);
        double y1 = Math.Max(Points[0].Y, Points[1].Y);

        double w = x1 - x0;
        double h = y1 - y0;
        if (w < 1 || h < 1) return;

        double wMm = w * slice.PixelSpacingX;
        double hMm = h * slice.PixelSpacingY;
        AreaCm2 = (wMm * hMm) / 100.0;

        int width = slice.Columns;
        int height = slice.Rows;

        int minX = Math.Max(0, (int)Math.Floor(x0));
        int maxX = Math.Min(width - 1, (int)Math.Ceiling(x1));
        int minY = Math.Max(0, (int)Math.Floor(y0));
        int maxY = Math.Min(height - 1, (int)Math.Ceiling(y1));

        double sum = 0;
        double sumSq = 0;
        int count = 0;
        double minVal = double.MaxValue;
        double maxVal = double.MinValue;

        DicomLoaderService.LoadPixelData(slice);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int pixelIndex = y * width + x;
                double pixelValue = 0;

                if (slice.RawPixelsUnsigned != null && pixelIndex < slice.RawPixelsUnsigned.Length)
                    pixelValue = slice.RawPixelsUnsigned[pixelIndex] * slice.RescaleSlope + slice.RescaleIntercept;
                else if (slice.RawPixelsSigned != null && pixelIndex < slice.RawPixelsSigned.Length)
                    pixelValue = slice.RawPixelsSigned[pixelIndex] * slice.RescaleSlope + slice.RescaleIntercept;

                sum += pixelValue;
                sumSq += pixelValue * pixelValue;
                count++;

                if (pixelValue < minVal) minVal = pixelValue;
                if (pixelValue > maxVal) maxVal = pixelValue;
            }
        }

        if (count > 0)
        {
            MeanHu = sum / count;
            double variance = (sumSq / count) - (MeanHu * MeanHu);
            SdHu = variance > 0 ? Math.Sqrt(variance) : 0;
            MinHu = minVal;
            MaxHu = maxVal;
            Details = $"Mean: {MeanHu:0.0} HU  SD: {SdHu:0.0}\nMin: {MinHu:0.#}  Max: {MaxHu:0.#}\nArea: {AreaCm2:0.00} cm² ({count} px)";
        }
    }
}

public class VertebraLabelItem
{
    public string Level { get; set; } = "L5";
    public Point ImagePoint { get; set; }
    public int SliceIndex { get; set; }
    public bool IsSelected { get; set; }

    public static readonly string[] SpineLevels = new[]
    {
        "C1", "C2", "C3", "C4", "C5", "C6", "C7",
        "T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8", "T9", "T10", "T11", "T12",
        "L1", "L2", "L3", "L4", "L5", "S1"
    };

    public static int GetLevelIndex(string level)
    {
        for (int i = 0; i < SpineLevels.Length; i++)
        {
            if (SpineLevels[i].Equals(level, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 23; // Default to L5
    }

    public static string ParseNormalizedLevel(string input, string fallbackCurrentLevel)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallbackCurrentLevel;
        string trimmed = input.Trim().ToUpperInvariant();

        // Direct match (e.g. "L1", "T10", "C2", "S1")
        for (int i = 0; i < SpineLevels.Length; i++)
        {
            if (SpineLevels[i].Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                return SpineLevels[i];
        }

        // Just a number provided (e.g. "1" when current is "L3" -> "L1")
        if (int.TryParse(trimmed, out int num))
        {
            char prefix = fallbackCurrentLevel.Length > 0 ? fallbackCurrentLevel[0] : 'L';
            string candidate = $"{prefix}{num}";
            for (int i = 0; i < SpineLevels.Length; i++)
            {
                if (SpineLevels[i].Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    return SpineLevels[i];
            }
        }

        return fallbackCurrentLevel;
    }

    public static string ShiftLevel(string currentLevel, int delta)
    {
        int idx = GetLevelIndex(currentLevel);
        int newIdx = Math.Clamp(idx + delta, 0, SpineLevels.Length - 1);
        return SpineLevels[newIdx];
    }
}
