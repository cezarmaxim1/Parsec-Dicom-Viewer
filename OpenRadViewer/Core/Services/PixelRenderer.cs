// @purpose: High-performance 16-bit / signed / RGB LUT windowing and pixel rendering directly to WPF WriteableBitmap using unsafe pointers.
// @depends: System.Windows.Media.Imaging, OpenRadViewer.Core.Models
// @exports: PixelRenderer, LutHelper

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public static class LutHelper
{
    /// <summary>
    /// Generates a 65536-entry BGRA uint LUT for unsigned 16-bit values.
    /// </summary>
    public static uint[] GenerateUnsignedLut(double windowCenter, double windowWidth, double rescaleSlope, double rescaleIntercept, bool invert)
    {
        var lut = new uint[65536];
        double halfWidth = Math.Max(1.0, windowWidth) / 2.0;
        double minVal = windowCenter - halfWidth;
        double maxVal = windowCenter + halfWidth;
        double range = maxVal - minVal;

        for (int i = 0; i < 65536; i++)
        {
            // Apply rescale slope & intercept to get Hounsfield Units / stored value
            double val = i * rescaleSlope + rescaleIntercept;

            byte gray;
            if (val <= minVal)
                gray = 0;
            else if (val >= maxVal)
                gray = 255;
            else
                gray = (byte)Math.Clamp((val - minVal) / range * 255.0 + 0.5, 0.0, 255.0);

            if (invert)
                gray = (byte)(255 - gray);

            // BGRA 32-bit (Alpha = 255)
            lut[i] = 0xFF000000u | ((uint)gray << 16) | ((uint)gray << 8) | gray;
        }

        return lut;
    }

    /// <summary>
    /// Generates a 65536-entry BGRA uint LUT for signed 16-bit values (-32768 to 32767).
    /// </summary>
    public static uint[] GenerateSignedLut(double windowCenter, double windowWidth, double rescaleSlope, double rescaleIntercept, bool invert)
    {
        var lut = new uint[65536];
        double halfWidth = Math.Max(1.0, windowWidth) / 2.0;
        double minVal = windowCenter - halfWidth;
        double maxVal = windowCenter + halfWidth;
        double range = maxVal - minVal;

        for (int i = 0; i < 65536; i++)
        {
            short signedVal = (short)(i - 32768);
            double val = signedVal * rescaleSlope + rescaleIntercept;

            byte gray;
            if (val <= minVal)
                gray = 0;
            else if (val >= maxVal)
                gray = 255;
            else
                gray = (byte)Math.Clamp((val - minVal) / range * 255.0 + 0.5, 0.0, 255.0);

            if (invert)
                gray = (byte)(255 - gray);

            lut[i] = 0xFF000000u | ((uint)gray << 16) | ((uint)gray << 8) | gray;
        }

        return lut;
    }
}

public static class PixelRenderer
{
    /// <summary>
    /// Renders a DicomSliceModel to a WPF WriteableBitmap using hardware-like memory pointers and the LUT.
    /// </summary>
    public static unsafe void RenderToBitmap(
        DicomSliceModel slice,
        double windowCenter,
        double windowWidth,
        bool invert,
        ref WriteableBitmap? targetBitmap)
    {
        if (slice == null || slice.Columns <= 0 || slice.Rows <= 0)
        {
            targetBitmap = null;
            return;
        }

        try
        {
            DicomLoaderService.LoadPixelData(slice);

            int width = slice.Columns;
            int height = slice.Rows;

            if (targetBitmap == null || targetBitmap.PixelWidth != width || targetBitmap.PixelHeight != height)
            {
                targetBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            }

            bool isMonochrome1 = slice.PhotometricInterpretation == "MONOCHROME1";
            bool isInverseShape = string.Equals(slice.PresentationLutShape, "INVERSE", StringComparison.OrdinalIgnoreCase);
            // AI-NOTE: MONOCHROME1 defines minimum sample as white and maximum as black.
            // On screen (standard MONOCHROME2 RGB), MONOCHROME1 must ALWAYS be inverted so air displays black and bones display white.
            // Modalities like VILLA DR, Canon DX write PresentationLUTShape = INVERSE alongside MONOCHROME1; an XOR (^ isInverseShape) erroneously canceled out the inversion.
            bool baseInvert = isMonochrome1 || (!isMonochrome1 && isInverseShape);
            bool finalInvert = baseInvert ? !invert : invert;

            targetBitmap.Lock();
            try
            {
                uint* pDest = (uint*)targetBitmap.BackBuffer.ToPointer();
                int totalPixels = width * height;

                if (slice.RawPixelsUnsigned != null)
                {
                    var lut = LutHelper.GenerateUnsignedLut(windowCenter, windowWidth, slice.RescaleSlope, slice.RescaleIntercept, finalInvert);
                    int loopLen = Math.Min(totalPixels, slice.RawPixelsUnsigned.Length);
                    fixed (ushort* pSrc = slice.RawPixelsUnsigned)
                    {
                        for (int i = 0; i < loopLen; i++)
                        {
                            pDest[i] = lut[pSrc[i]];
                        }
                    }
                }
                else if (slice.RawPixelsSigned != null)
                {
                    var lut = LutHelper.GenerateSignedLut(windowCenter, windowWidth, slice.RescaleSlope, slice.RescaleIntercept, finalInvert);
                    int loopLen = Math.Min(totalPixels, slice.RawPixelsSigned.Length);
                    fixed (short* pSrc = slice.RawPixelsSigned)
                    {
                        for (int i = 0; i < loopLen; i++)
                        {
                            int index = pSrc[i] + 32768;
                            if (index < 0) index = 0;
                            else if (index > 65535) index = 65535;
                            pDest[i] = lut[index];
                        }
                    }
                }
                else if (slice.RawRgbPixels != null)
                {
                    // RGB 24-bit to BGR 32-bit
                    byte* pDestByte = (byte*)pDest;
                    int maxRgbPixels = Math.Min(totalPixels, slice.RawRgbPixels.Length / 3);
                    fixed (byte* pSrc = slice.RawRgbPixels)
                    {
                        for (int i = 0; i < maxRgbPixels; i++)
                        {
                            int srcIdx = i * 3;
                            int dstIdx = i * 4;
                            pDestByte[dstIdx + 0] = pSrc[srcIdx + 2]; // B
                            pDestByte[dstIdx + 1] = pSrc[srcIdx + 1]; // G
                            pDestByte[dstIdx + 2] = pSrc[srcIdx + 0]; // R
                            pDestByte[dstIdx + 3] = 255;              // A
                        }
                    }
                }

                targetBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                targetBitmap.Unlock();
            }
        }
        catch
        {
            targetBitmap = null;
        }
    }

    /// <summary>
    /// Renders a fast downsampled thumbnail bitmap (e.g. 160px max dimension) for series cards & preview bars.
    /// Avoids allocating and windowing multi-megapixel buffers for tiny preview thumbnails.
    /// </summary>
    public static unsafe BitmapSource? RenderThumbnail(
        DicomSliceModel slice,
        double windowCenter,
        double windowWidth,
        int maxDimension = 160)
    {
        if (slice == null || slice.Columns <= 0 || slice.Rows <= 0)
            return null;

        try
        {
            DicomLoaderService.LoadPixelData(slice);

            int origW = slice.Columns;
            int origH = slice.Rows;
            int step = Math.Max(1, Math.Max(origW, origH) / maxDimension);
            int thumbW = Math.Max(1, origW / step);
            int thumbH = Math.Max(1, origH / step);

            var bmp = new WriteableBitmap(thumbW, thumbH, 96, 96, PixelFormats.Bgr32, null);
            bool isMonochrome1 = slice.PhotometricInterpretation == "MONOCHROME1";
            bool isInverseShape = string.Equals(slice.PresentationLutShape, "INVERSE", StringComparison.OrdinalIgnoreCase);
            bool finalInvert = isMonochrome1 || (!isMonochrome1 && isInverseShape);

            bmp.Lock();
            try
            {
                uint* pDest = (uint*)bmp.BackBuffer.ToPointer();

                if (slice.RawPixelsUnsigned != null)
                {
                    var lut = LutHelper.GenerateUnsignedLut(windowCenter, windowWidth, slice.RescaleSlope, slice.RescaleIntercept, finalInvert);
                    fixed (ushort* pSrc = slice.RawPixelsUnsigned)
                    {
                        for (int ty = 0; ty < thumbH; ty++)
                        {
                            int sy = ty * step;
                            if (sy >= origH) sy = origH - 1;
                            int srcRowOffset = sy * origW;
                            int dstRowOffset = ty * thumbW;
                            for (int tx = 0; tx < thumbW; tx++)
                            {
                                int sx = tx * step;
                                if (sx >= origW) sx = origW - 1;
                                pDest[dstRowOffset + tx] = lut[pSrc[srcRowOffset + sx]];
                            }
                        }
                    }
                }
                else if (slice.RawPixelsSigned != null)
                {
                    var lut = LutHelper.GenerateSignedLut(windowCenter, windowWidth, slice.RescaleSlope, slice.RescaleIntercept, finalInvert);
                    fixed (short* pSrc = slice.RawPixelsSigned)
                    {
                        for (int ty = 0; ty < thumbH; ty++)
                        {
                            int sy = ty * step;
                            if (sy >= origH) sy = origH - 1;
                            int srcRowOffset = sy * origW;
                            int dstRowOffset = ty * thumbW;
                            for (int tx = 0; tx < thumbW; tx++)
                            {
                                int sx = tx * step;
                                if (sx >= origW) sx = origW - 1;
                                int val = pSrc[srcRowOffset + sx] + 32768;
                                if (val < 0) val = 0;
                                else if (val > 65535) val = 65535;
                                pDest[dstRowOffset + tx] = lut[val];
                            }
                        }
                    }
                }
                else if (slice.RawRgbPixels != null)
                {
                    byte* pDestByte = (byte*)pDest;
                    fixed (byte* pSrc = slice.RawRgbPixels)
                    {
                        for (int ty = 0; ty < thumbH; ty++)
                        {
                            int sy = ty * step;
                            if (sy >= origH) sy = origH - 1;
                            int srcRowOffset = sy * origW * 3;
                            int dstRowOffset = ty * thumbW * 4;
                            for (int tx = 0; tx < thumbW; tx++)
                            {
                                int sx = tx * step;
                                if (sx >= origW) sx = origW - 1;
                                int srcIdx = srcRowOffset + (sx * 3);
                                int dstIdx = dstRowOffset + (tx * 4);
                                pDestByte[dstIdx + 0] = pSrc[srcIdx + 2]; // B
                                pDestByte[dstIdx + 1] = pSrc[srcIdx + 1]; // G
                                pDestByte[dstIdx + 2] = pSrc[srcIdx + 0]; // R
                                pDestByte[dstIdx + 3] = 255;              // A
                            }
                        }
                    }
                }

                bmp.AddDirtyRect(new Int32Rect(0, 0, thumbW, thumbH));
            }
            finally
            {
                bmp.Unlock();
            }

            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

