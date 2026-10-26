// @purpose: Real-time Multi-Planar Reconstruction (MPR) reslicing 3D volumes into Axial, Coronal, Sagittal, and Oblique cut planes with MIP/MinIP/Average projection.
// @depends: OpenRadViewer.Core.Models, OpenRadViewer.Core.Services (PixelRenderer, DicomLoaderService)
// @exports: MprService, OrthogonalPlane

using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public enum OrthogonalPlane
{
    Axial,
    Coronal,
    Sagittal
}

public enum SlabProjectionMode
{
    Average,
    Mip,
    MinIp
}

public static class MprService
{
    private static readonly ConcurrentDictionary<string, SeriesModel> _mprCache = new();

    public static OrthogonalPlane DetectOrientation(DicomSliceModel slice)
    {
        if (slice.ImageOrientationPatient == null || slice.ImageOrientationPatient.Length < 6)
            return OrthogonalPlane.Axial;

        double rx = slice.ImageOrientationPatient[0];
        double ry = slice.ImageOrientationPatient[1];
        double rz = slice.ImageOrientationPatient[2];
        double cx = slice.ImageOrientationPatient[3];
        double cy = slice.ImageOrientationPatient[4];
        double cz = slice.ImageOrientationPatient[5];

        double nx = ry * cz - rz * cy;
        double ny = rz * cx - rx * cz;
        double nz = rx * cy - ry * cx;

        double ax = Math.Abs(nx);
        double ay = Math.Abs(ny);
        double az = Math.Abs(nz);

        if (az >= ax && az >= ay) return OrthogonalPlane.Axial;
        if (ay >= ax && ay >= az) return OrthogonalPlane.Coronal;
        return OrthogonalPlane.Sagittal;
    }

    public static OrthogonalPlane DetectSeriesOrientation(SeriesModel series)
    {
        if (series == null || series.Slices.Count == 0)
            return OrthogonalPlane.Axial;

        // Use middle slice to avoid being misled by an embedded scout/localizer reference image at index 0
        var midSlice = series.Slices[series.Slices.Count / 2];
        return DetectOrientation(midSlice);
    }

    /// <summary>
    /// Generates or retrieves an orthogonal MPR series (Coronal, Sagittal, or Axial) from a 3D volumetric series.
    /// Fast multi-threaded volume slicing with exact DICOM coordinates and full pixel preloading.
    /// </summary>
    public static SeriesModel? GenerateOrthogonalSeries(SeriesModel sourceSeries, OrthogonalPlane targetPlane)
    {
        if (sourceSeries.Slices.Count < 3) return null;

        var originalPlane = DetectSeriesOrientation(sourceSeries);
        var midRef = sourceSeries.Slices[sourceSeries.Slices.Count / 2];

        // Exclude any embedded reference scout slice that has a different orientation or dimensions
        var validSlices = sourceSeries.Slices
            .Where(s => DetectOrientation(s) == originalPlane && s.Rows == midRef.Rows && s.Columns == midRef.Columns)
            .ToList();
        if (validSlices.Count < 3)
            validSlices = sourceSeries.Slices;

        if (originalPlane == targetPlane)
            return sourceSeries;

        string cacheKey = $"{sourceSeries.SeriesInstanceUid}_{targetPlane}_{validSlices.Count}";
        if (_mprCache.TryGetValue(cacheKey, out var cachedSeries))
        {
            return cachedSeries;
        }

        var firstSlice = validSlices[0];
        int numSlices = validSlices.Count;
        int origRows = firstSlice.Rows;
        int origCols = firstSlice.Columns;

        if (origRows <= 0 || origCols <= 0) return null;

        // 1. Preload pixel data in fast batch
        DicomLoaderService.LoadSeriesPixelData(sourceSeries);

        double spX = Math.Max(0.0001, firstSlice.PixelSpacingX);
        double spY = Math.Max(0.0001, firstSlice.PixelSpacingY);

        // Calculate slice step vector and through-plane spacing
        double stepX = 0, stepY = 0, stepZ = 0;
        var lastSlice = validSlices[numSlices - 1];

        if (firstSlice.ImagePositionPatient != null && lastSlice.ImagePositionPatient != null &&
            firstSlice.ImagePositionPatient.Length >= 3 && lastSlice.ImagePositionPatient.Length >= 3 &&
            numSlices > 1)
        {
            stepX = (lastSlice.ImagePositionPatient[0] - firstSlice.ImagePositionPatient[0]) / (numSlices - 1);
            stepY = (lastSlice.ImagePositionPatient[1] - firstSlice.ImagePositionPatient[1]) / (numSlices - 1);
            stepZ = (lastSlice.ImagePositionPatient[2] - firstSlice.ImagePositionPatient[2]) / (numSlices - 1);
        }
        else
        {
            double[] iop = firstSlice.ImageOrientationPatient ?? new double[] { 1, 0, 0, 0, 1, 0 };
            double nx = iop[1] * iop[5] - iop[2] * iop[4];
            double ny = iop[2] * iop[3] - iop[0] * iop[5];
            double nz = iop[0] * iop[4] - iop[1] * iop[3];
            double thick = firstSlice.SliceThickness > 0 ? firstSlice.SliceThickness : 1.0;
            stepX = nx * thick;
            stepY = ny * thick;
            stepZ = nz * thick;
        }

        double spZ = Math.Sqrt(stepX * stepX + stepY * stepY + stepZ * stepZ);
        if (spZ < 0.001) spZ = firstSlice.SliceThickness > 0 ? firstSlice.SliceThickness : 1.0;

        double uStepX = stepX / spZ;
        double uStepY = stepY / spZ;
        double uStepZ = stepZ / spZ;

        double[] p0 = firstSlice.ImagePositionPatient != null && firstSlice.ImagePositionPatient.Length >= 3
            ? firstSlice.ImagePositionPatient
            : new double[] { 0, 0, 0 };

        double[] srcIop = firstSlice.ImageOrientationPatient != null && firstSlice.ImageOrientationPatient.Length >= 6
            ? firstSlice.ImageOrientationPatient
            : new double[] { 1, 0, 0, 0, 1, 0 };

        double rX = srcIop[0], rY = srcIop[1], rZ = srcIop[2];
        double cX = srcIop[3], cY = srcIop[4], cZ = srcIop[5];

        double targetSpacing = (spX + spY) / 2.0;
        int numZSamples = Math.Max(numSlices, (int)Math.Round((numSlices - 1) * spZ / targetSpacing) + 1);
        numZSamples = Math.Clamp(numZSamples, numSlices, 1024);
        double resampledSpZ = ((numSlices - 1) * spZ) / Math.Max(1, numZSamples - 1);

        bool isSigned = firstSlice.PixelRepresentation == 1;

        // Pre-fetch raw pixel slice arrays for instant parallel access
        short[][]? srcSignedSlices = null;
        ushort[][]? srcUnsignedSlices = null;
        if (isSigned)
        {
            srcSignedSlices = new short[numSlices][];
            for (int z = 0; z < numSlices; z++)
                srcSignedSlices[z] = validSlices[z].RawPixelsSigned ?? new short[origRows * origCols];
        }
        else
        {
            srcUnsignedSlices = new ushort[numSlices][];
            for (int z = 0; z < numSlices; z++)
                srcUnsignedSlices[z] = validSlices[z].RawPixelsUnsigned ?? new ushort[origRows * origCols];
        }

        var mprSeries = new SeriesModel
        {
            SeriesInstanceUid = $"{sourceSeries.SeriesInstanceUid}.MPR.{targetPlane}",
            SeriesNumber = sourceSeries.SeriesNumber + 1000 + (int)targetPlane,
            SeriesDescription = $"{sourceSeries.SeriesDescription} [{targetPlane}]",
            Modality = sourceSeries.Modality,
            BodyPart = sourceSeries.BodyPart,
            PatientName = sourceSeries.PatientName,
            PatientId = sourceSeries.PatientId,
            PatientBirthDate = sourceSeries.PatientBirthDate,
            PatientAge = sourceSeries.PatientAge,
            StudyDate = sourceSeries.StudyDate
        };

        // ==========================================
        // 1. SOURCE IS AXIAL
        // ==========================================
        if (originalPlane == OrthogonalPlane.Axial)
        {
            bool zAscending = stepZ >= 0;

            if (targetPlane == OrthogonalPlane.Coronal)
            {
                int outSlicesCount = origRows;
                int outWidth = origCols;
                int outHeight = numZSamples;
                var slicesArr = new DicomSliceModel[outSlicesCount];

                double dirZX = zAscending ? -uStepX : uStepX;
                double dirZY = zAscending ? -uStepY : uStepY;
                double dirZZ = zAscending ? -uStepZ : uStepZ;

                Parallel.For(0, outSlicesCount, y =>
                {
                    var mprSlice = CreateMprSliceMetadata(firstSlice, outWidth, outHeight, spX, resampledSpZ, spY);
                    mprSlice.OrientationLeft = "R";
                    mprSlice.OrientationRight = "L";
                    mprSlice.OrientationTop = "S";
                    mprSlice.OrientationBottom = "I";
                    mprSlice.ImageOrientationPatient = new double[] { rX, rY, rZ, dirZX, dirZY, dirZZ };

                    double startZOffset = zAscending ? (numSlices - 1) : 0;
                    mprSlice.ImagePositionPatient = new double[]
                    {
                        p0[0] + startZOffset * stepX + y * spY * cX,
                        p0[1] + startZOffset * stepY + y * spY * cY,
                        p0[2] + startZOffset * stepZ + y * spY * cZ
                    };

                    int totalPixels = outWidth * outHeight;
                    if (isSigned)
                    {
                        short[] buffer = new short[totalPixels];
                        for (int r = 0; r < outHeight; r++)
                        {
                            float w = zAscending
                                ? ((numSlices - 1f) - r * (numSlices - 1f) / (outHeight - 1f))
                                : (r * (numSlices - 1f) / (outHeight - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            int dstRow = r * outWidth;
                            int srcRow = y * origCols;
                            var s0 = srcSignedSlices![w0];
                            var s1 = srcSignedSlices![w1];
                            for (int c = 0; c < outWidth; c++)
                                buffer[dstRow + c] = (short)Math.Round((1f - t) * s0[srcRow + c] + t * s1[srcRow + c]);
                        }
                        mprSlice.RawPixelsSigned = buffer;
                    }
                    else
                    {
                        ushort[] buffer = new ushort[totalPixels];
                        for (int r = 0; r < outHeight; r++)
                        {
                            float w = zAscending
                                ? ((numSlices - 1f) - r * (numSlices - 1f) / (outHeight - 1f))
                                : (r * (numSlices - 1f) / (outHeight - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            int dstRow = r * outWidth;
                            int srcRow = y * origCols;
                            var s0 = srcUnsignedSlices![w0];
                            var s1 = srcUnsignedSlices![w1];
                            for (int c = 0; c < outWidth; c++)
                                buffer[dstRow + c] = (ushort)Math.Round((1f - t) * s0[srcRow + c] + t * s1[srcRow + c]);
                        }
                        mprSlice.RawPixelsUnsigned = buffer;
                    }
                    slicesArr[y] = mprSlice;
                });

                mprSeries.Slices.AddRange(slicesArr);
            }
            else if (targetPlane == OrthogonalPlane.Sagittal)
            {
                int outSlicesCount = origCols;
                int outWidth = origRows;
                int outHeight = numZSamples;
                var slicesArr = new DicomSliceModel[outSlicesCount];

                double dirZX = zAscending ? -uStepX : uStepX;
                double dirZY = zAscending ? -uStepY : uStepY;
                double dirZZ = zAscending ? -uStepZ : uStepZ;

                Parallel.For(0, outSlicesCount, x =>
                {
                    var mprSlice = CreateMprSliceMetadata(firstSlice, outWidth, outHeight, spY, resampledSpZ, spX);
                    mprSlice.OrientationLeft = "A";
                    mprSlice.OrientationRight = "P";
                    mprSlice.OrientationTop = "S";
                    mprSlice.OrientationBottom = "I";
                    mprSlice.ImageOrientationPatient = new double[] { cX, cY, cZ, dirZX, dirZY, dirZZ };

                    double startZOffset = zAscending ? (numSlices - 1) : 0;
                    mprSlice.ImagePositionPatient = new double[]
                    {
                        p0[0] + startZOffset * stepX + x * spX * rX,
                        p0[1] + startZOffset * stepY + x * spX * rY,
                        p0[2] + startZOffset * stepZ + x * spX * rZ
                    };

                    int totalPixels = outWidth * outHeight;
                    if (isSigned)
                    {
                        short[] buffer = new short[totalPixels];
                        for (int r = 0; r < outHeight; r++)
                        {
                            float w = zAscending
                                ? ((numSlices - 1f) - r * (numSlices - 1f) / (outHeight - 1f))
                                : (r * (numSlices - 1f) / (outHeight - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            int dstRow = r * outWidth;
                            var s0 = srcSignedSlices![w0];
                            var s1 = srcSignedSlices![w1];
                            for (int c = 0; c < outWidth; c++)
                                buffer[dstRow + c] = (short)Math.Round((1f - t) * s0[c * origCols + x] + t * s1[c * origCols + x]);
                        }
                        mprSlice.RawPixelsSigned = buffer;
                    }
                    else
                    {
                        ushort[] buffer = new ushort[totalPixels];
                        for (int r = 0; r < outHeight; r++)
                        {
                            float w = zAscending
                                ? ((numSlices - 1f) - r * (numSlices - 1f) / (outHeight - 1f))
                                : (r * (numSlices - 1f) / (outHeight - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            int dstRow = r * outWidth;
                            var s0 = srcUnsignedSlices![w0];
                            var s1 = srcUnsignedSlices![w1];
                            for (int c = 0; c < outWidth; c++)
                                buffer[dstRow + c] = (ushort)Math.Round((1f - t) * s0[c * origCols + x] + t * s1[c * origCols + x]);
                        }
                        mprSlice.RawPixelsUnsigned = buffer;
                    }
                    slicesArr[x] = mprSlice;
                });

                mprSeries.Slices.AddRange(slicesArr);
            }
        }
        // ==========================================
        // 2. SOURCE IS CORONAL
        // ==========================================
        else if (originalPlane == OrthogonalPlane.Coronal)
        {
            bool yAscending = stepY >= 0;

            if (targetPlane == OrthogonalPlane.Axial)
            {
                int outSlicesCount = origRows;
                int outWidth = origCols;
                int outHeight = numZSamples;
                var slicesArr = new DicomSliceModel[outSlicesCount];

                double dirYX = yAscending ? uStepX : -uStepX;
                double dirYY = yAscending ? uStepY : -uStepY;
                double dirYZ = yAscending ? uStepZ : -uStepZ;

                Parallel.For(0, outSlicesCount, z =>
                {
                    var mprSlice = CreateMprSliceMetadata(firstSlice, outWidth, outHeight, spX, resampledSpZ, spY);
                    mprSlice.OrientationLeft = "R";
                    mprSlice.OrientationRight = "L";
                    mprSlice.OrientationTop = "A";
                    mprSlice.OrientationBottom = "P";
                    mprSlice.ImageOrientationPatient = new double[] { rX, rY, rZ, dirYX, dirYY, dirYZ };

                    double startYOffset = yAscending ? 0 : (numSlices - 1);
                    mprSlice.ImagePositionPatient = new double[]
                    {
                        p0[0] + startYOffset * stepX + z * spY * cX,
                        p0[1] + startYOffset * stepY + z * spY * cY,
                        p0[2] + startYOffset * stepZ + z * spY * cZ
                    };

                    int totalPixels = outWidth * outHeight;
                    if (isSigned)
                    {
                        short[] buffer = new short[totalPixels];
                        for (int r = 0; r < outHeight; r++)
                        {
                            float w = yAscending
                                ? (r * (numSlices - 1f) / (outHeight - 1f))
                                : ((numSlices - 1f) - r * (numSlices - 1f) / (outHeight - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            int dstRow = r * outWidth;
                            int srcRow = z * origCols;
                            var s0 = srcSignedSlices![w0];
                            var s1 = srcSignedSlices![w1];
                            for (int c = 0; c < outWidth; c++)
                                buffer[dstRow + c] = (short)Math.Round((1f - t) * s0[srcRow + c] + t * s1[srcRow + c]);
                        }
                        mprSlice.RawPixelsSigned = buffer;
                    }
                    else
                    {
                        ushort[] buffer = new ushort[totalPixels];
                        for (int r = 0; r < outHeight; r++)
                        {
                            float w = yAscending
                                ? (r * (numSlices - 1f) / (outHeight - 1f))
                                : ((numSlices - 1f) - r * (numSlices - 1f) / (outHeight - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            int dstRow = r * outWidth;
                            int srcRow = z * origCols;
                            var s0 = srcUnsignedSlices![w0];
                            var s1 = srcUnsignedSlices![w1];
                            for (int c = 0; c < outWidth; c++)
                                buffer[dstRow + c] = (ushort)Math.Round((1f - t) * s0[srcRow + c] + t * s1[srcRow + c]);
                        }
                        mprSlice.RawPixelsUnsigned = buffer;
                    }
                    slicesArr[z] = mprSlice;
                });

                mprSeries.Slices.AddRange(slicesArr);
            }
            else if (targetPlane == OrthogonalPlane.Sagittal)
            {
                int outSlicesCount = origCols;
                int outWidth = numZSamples;
                int outHeight = origRows;
                var slicesArr = new DicomSliceModel[outSlicesCount];

                double dirYX = yAscending ? uStepX : -uStepX;
                double dirYY = yAscending ? uStepY : -uStepY;
                double dirYZ = yAscending ? uStepZ : -uStepZ;

                Parallel.For(0, outSlicesCount, x =>
                {
                    var mprSlice = CreateMprSliceMetadata(firstSlice, outWidth, outHeight, resampledSpZ, spY, spX);
                    mprSlice.OrientationLeft = "A";
                    mprSlice.OrientationRight = "P";
                    mprSlice.OrientationTop = "S";
                    mprSlice.OrientationBottom = "I";
                    mprSlice.ImageOrientationPatient = new double[] { dirYX, dirYY, dirYZ, cX, cY, cZ };

                    double startYOffset = yAscending ? 0 : (numSlices - 1);
                    mprSlice.ImagePositionPatient = new double[]
                    {
                        p0[0] + startYOffset * stepX + x * spX * rX,
                        p0[1] + startYOffset * stepY + x * spX * rY,
                        p0[2] + startYOffset * stepZ + x * spX * rZ
                    };

                    int totalPixels = outWidth * outHeight;
                    if (isSigned)
                    {
                        short[] buffer = new short[totalPixels];
                        for (int c = 0; c < outWidth; c++)
                        {
                            float w = yAscending
                                ? (c * (numSlices - 1f) / (outWidth - 1f))
                                : ((numSlices - 1f) - c * (numSlices - 1f) / (outWidth - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            var s0 = srcSignedSlices![w0];
                            var s1 = srcSignedSlices![w1];
                            for (int r = 0; r < outHeight; r++)
                                buffer[r * outWidth + c] = (short)Math.Round((1f - t) * s0[r * origCols + x] + t * s1[r * origCols + x]);
                        }
                        mprSlice.RawPixelsSigned = buffer;
                    }
                    else
                    {
                        ushort[] buffer = new ushort[totalPixels];
                        for (int c = 0; c < outWidth; c++)
                        {
                            float w = yAscending
                                ? (c * (numSlices - 1f) / (outWidth - 1f))
                                : ((numSlices - 1f) - c * (numSlices - 1f) / (outWidth - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            var s0 = srcUnsignedSlices![w0];
                            var s1 = srcUnsignedSlices![w1];
                            for (int r = 0; r < outHeight; r++)
                                buffer[r * outWidth + c] = (ushort)Math.Round((1f - t) * s0[r * origCols + x] + t * s1[r * origCols + x]);
                        }
                        mprSlice.RawPixelsUnsigned = buffer;
                    }
                    slicesArr[x] = mprSlice;
                });

                mprSeries.Slices.AddRange(slicesArr);
            }
        }
        // ==========================================
        // 3. SOURCE IS SAGITTAL (Spine MRI)
        // ==========================================
        else if (originalPlane == OrthogonalPlane.Sagittal)
        {
            bool xAscending = stepX >= 0;

            if (targetPlane == OrthogonalPlane.Coronal)
            {
                int outSlicesCount = origCols;
                int outWidth = numZSamples;
                int outHeight = origRows;
                var slicesArr = new DicomSliceModel[outSlicesCount];

                double dirXX = xAscending ? uStepX : -uStepX;
                double dirXY = xAscending ? uStepY : -uStepY;
                double dirXZ = xAscending ? uStepZ : -uStepZ;

                Parallel.For(0, outSlicesCount, y =>
                {
                    var mprSlice = CreateMprSliceMetadata(firstSlice, outWidth, outHeight, resampledSpZ, spY, spX);
                    mprSlice.OrientationLeft = "R";
                    mprSlice.OrientationRight = "L";
                    mprSlice.OrientationTop = "S";
                    mprSlice.OrientationBottom = "I";
                    mprSlice.ImageOrientationPatient = new double[] { dirXX, dirXY, dirXZ, cX, cY, cZ };

                    double startXOffset = xAscending ? 0 : (numSlices - 1);
                    mprSlice.ImagePositionPatient = new double[]
                    {
                        p0[0] + startXOffset * stepX + y * spX * rX,
                        p0[1] + startXOffset * stepY + y * spX * rY,
                        p0[2] + startXOffset * stepZ + y * spX * rZ
                    };

                    int totalPixels = outWidth * outHeight;
                    if (isSigned)
                    {
                        short[] buffer = new short[totalPixels];
                        for (int c = 0; c < outWidth; c++)
                        {
                            float w = xAscending
                                ? (c * (numSlices - 1f) / (outWidth - 1f))
                                : ((numSlices - 1f) - c * (numSlices - 1f) / (outWidth - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            var s0 = srcSignedSlices![w0];
                            var s1 = srcSignedSlices![w1];
                            for (int r = 0; r < outHeight; r++)
                                buffer[r * outWidth + c] = (short)Math.Round((1f - t) * s0[r * origCols + y] + t * s1[r * origCols + y]);
                        }
                        mprSlice.RawPixelsSigned = buffer;
                    }
                    else
                    {
                        ushort[] buffer = new ushort[totalPixels];
                        for (int c = 0; c < outWidth; c++)
                        {
                            float w = xAscending
                                ? (c * (numSlices - 1f) / (outWidth - 1f))
                                : ((numSlices - 1f) - c * (numSlices - 1f) / (outWidth - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            var s0 = srcUnsignedSlices![w0];
                            var s1 = srcUnsignedSlices![w1];
                            for (int r = 0; r < outHeight; r++)
                                buffer[r * outWidth + c] = (ushort)Math.Round((1f - t) * s0[r * origCols + y] + t * s1[r * origCols + y]);
                        }
                        mprSlice.RawPixelsUnsigned = buffer;
                    }
                    slicesArr[y] = mprSlice;
                });

                mprSeries.Slices.AddRange(slicesArr);
            }
            else if (targetPlane == OrthogonalPlane.Axial)
            {
                int outSlicesCount = origRows;
                int outWidth = numZSamples;
                int outHeight = origCols;
                var slicesArr = new DicomSliceModel[outSlicesCount];

                double dirXX = xAscending ? uStepX : -uStepX;
                double dirXY = xAscending ? uStepY : -uStepY;
                double dirXZ = xAscending ? uStepZ : -uStepZ;

                Parallel.For(0, outSlicesCount, z =>
                {
                    var mprSlice = CreateMprSliceMetadata(firstSlice, outWidth, outHeight, resampledSpZ, spX, spY);
                    mprSlice.OrientationLeft = "R";
                    mprSlice.OrientationRight = "L";
                    mprSlice.OrientationTop = "A";
                    mprSlice.OrientationBottom = "P";
                    mprSlice.ImageOrientationPatient = new double[] { dirXX, dirXY, dirXZ, rX, rY, rZ };

                    double startXOffset = xAscending ? 0 : (numSlices - 1);
                    mprSlice.ImagePositionPatient = new double[]
                    {
                        p0[0] + startXOffset * stepX + z * spY * cX,
                        p0[1] + startXOffset * stepY + z * spY * cY,
                        p0[2] + startXOffset * stepZ + z * spY * cZ
                    };

                    int totalPixels = outWidth * outHeight;
                    if (isSigned)
                    {
                        short[] buffer = new short[totalPixels];
                        for (int c = 0; c < outWidth; c++)
                        {
                            float w = xAscending
                                ? (c * (numSlices - 1f) / (outWidth - 1f))
                                : ((numSlices - 1f) - c * (numSlices - 1f) / (outWidth - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            var s0 = srcSignedSlices![w0];
                            var s1 = srcSignedSlices![w1];
                            for (int r = 0; r < outHeight; r++)
                                buffer[r * outWidth + c] = (short)Math.Round((1f - t) * s0[z * origCols + r] + t * s1[z * origCols + r]);
                        }
                        mprSlice.RawPixelsSigned = buffer;
                    }
                    else
                    {
                        ushort[] buffer = new ushort[totalPixels];
                        for (int c = 0; c < outWidth; c++)
                        {
                            float w = xAscending
                                ? (c * (numSlices - 1f) / (outWidth - 1f))
                                : ((numSlices - 1f) - c * (numSlices - 1f) / (outWidth - 1f));
                            int w0 = (int)w;
                            int w1 = Math.Min(numSlices - 1, w0 + 1);
                            float t = w - w0;
                            var s0 = srcUnsignedSlices![w0];
                            var s1 = srcUnsignedSlices![w1];
                            for (int r = 0; r < outHeight; r++)
                                buffer[r * outWidth + c] = (ushort)Math.Round((1f - t) * s0[z * origCols + r] + t * s1[z * origCols + r]);
                        }
                        mprSlice.RawPixelsUnsigned = buffer;
                    }
                    slicesArr[z] = mprSlice;
                });

                mprSeries.Slices.AddRange(slicesArr);
            }
        }

        if (mprSeries.Slices.Count > 0)
        {
            // Generate thumbnail from middle slice
            int mid = mprSeries.Slices.Count / 2;
            var midSlice = mprSeries.Slices[mid];
            WriteableBitmap? thumb = null;
            PixelRenderer.RenderToBitmap(midSlice, midSlice.DefaultWindowCenter, midSlice.DefaultWindowWidth, false, ref thumb);
            if (thumb != null)
            {
                thumb.Freeze();
                mprSeries.ThumbnailImage = thumb;
            }
            _mprCache[cacheKey] = mprSeries;
            return mprSeries;
        }

        return null;
    }

    private static DicomSliceModel CreateMprSliceMetadata(DicomSliceModel src, int w, int h, double spX, double spY, double thickness)
    {
        return new DicomSliceModel
        {
            FilePath = src.FilePath,
            Rows = h,
            Columns = w,
            PixelSpacingX = spX,
            PixelSpacingY = spY,
            SliceThickness = thickness,
            DefaultWindowCenter = src.DefaultWindowCenter,
            DefaultWindowWidth = src.DefaultWindowWidth,
            RescaleSlope = src.RescaleSlope,
            RescaleIntercept = src.RescaleIntercept,
            BitsAllocated = src.BitsAllocated,
            BitsStored = src.BitsStored,
            PixelRepresentation = src.PixelRepresentation,
            PhotometricInterpretation = src.PhotometricInterpretation,
            MinPixelValue = src.MinPixelValue,
            MaxPixelValue = src.MaxPixelValue
        };
    }

    /// <summary>
    /// Volume context containing continuous voxel geometry and raw slice data for high-speed oblique sampling.
    /// </summary>
    public class VolumeContext
    {
        public int OrigCols { get; init; }
        public int OrigRows { get; init; }
        public int NumSlices { get; init; }
        public double SpX { get; init; }
        public double SpY { get; init; }
        public double SpZ { get; init; }
        public double P0X { get; init; }
        public double P0Y { get; init; }
        public double P0Z { get; init; }
        public double StepX { get; init; }
        public double StepY { get; init; }
        public double StepZ { get; init; }
        public double InvStepSq { get; init; }
        public double[] Iop { get; init; } = Array.Empty<double>();
        public double Rx { get; init; }
        public double Ry { get; init; }
        public double Rz { get; init; }
        public double Cx { get; init; }
        public double Cy { get; init; }
        public double Cz { get; init; }
        public bool IsSigned { get; init; }
        public short[][]? SignedSlices { get; init; }
        public ushort[][]? UnsignedSlices { get; init; }
        public DicomSliceModel ReferenceSlice { get; init; } = null!;
    }

    private static readonly ConcurrentDictionary<string, VolumeContext> _volumeContextCache = new();

    public static VolumeContext? GetVolumeContext(SeriesModel series)
    {
        if (series.Slices.Count < 2) return null;
        string key = $"{series.SeriesInstanceUid}_{series.Slices.Count}";
        if (_volumeContextCache.TryGetValue(key, out var cached)) return cached;

        DicomLoaderService.LoadSeriesPixelData(series);

        var origPlane = DetectSeriesOrientation(series);
        var midRef = series.Slices[series.Slices.Count / 2];
        var validSlices = series.Slices
            .Where(s => DetectOrientation(s) == origPlane && s.Rows == midRef.Rows && s.Columns == midRef.Columns)
            .ToList();
        if (validSlices.Count < 2)
            validSlices = series.Slices;

        var firstSlice = validSlices[0];
        int numSlices = validSlices.Count;
        int origRows = firstSlice.Rows;
        int origCols = firstSlice.Columns;
        if (origRows <= 0 || origCols <= 0) return null;

        double spX = Math.Max(0.0001, firstSlice.PixelSpacingX);
        double spY = Math.Max(0.0001, firstSlice.PixelSpacingY);

        double stepX = 0, stepY = 0, stepZ = 0;
        var lastSlice = validSlices[numSlices - 1];

        if (firstSlice.ImagePositionPatient != null && lastSlice.ImagePositionPatient != null &&
            firstSlice.ImagePositionPatient.Length >= 3 && lastSlice.ImagePositionPatient.Length >= 3 &&
            numSlices > 1)
        {
            stepX = (lastSlice.ImagePositionPatient[0] - firstSlice.ImagePositionPatient[0]) / (numSlices - 1);
            stepY = (lastSlice.ImagePositionPatient[1] - firstSlice.ImagePositionPatient[1]) / (numSlices - 1);
            stepZ = (lastSlice.ImagePositionPatient[2] - firstSlice.ImagePositionPatient[2]) / (numSlices - 1);
        }
        else
        {
            double[] iop = firstSlice.ImageOrientationPatient ?? new double[] { 1, 0, 0, 0, 1, 0 };
            double nx = iop[1] * iop[5] - iop[2] * iop[4];
            double ny = iop[2] * iop[3] - iop[0] * iop[5];
            double nz = iop[0] * iop[4] - iop[1] * iop[3];
            double thick = firstSlice.SliceThickness > 0 ? firstSlice.SliceThickness : 1.0;
            stepX = nx * thick;
            stepY = ny * thick;
            stepZ = nz * thick;
        }

        double spZ = Math.Sqrt(stepX * stepX + stepY * stepY + stepZ * stepZ);
        if (spZ < 0.001) spZ = firstSlice.SliceThickness > 0 ? firstSlice.SliceThickness : 1.0;

        double invStepSq = (stepX * stepX + stepY * stepY + stepZ * stepZ);
        invStepSq = invStepSq > 1e-8 ? 1.0 / invStepSq : 1.0;

        double[] p0 = firstSlice.ImagePositionPatient != null && firstSlice.ImagePositionPatient.Length >= 3
            ? firstSlice.ImagePositionPatient
            : new double[] { 0, 0, 0 };

        double[] srcIop = firstSlice.ImageOrientationPatient != null && firstSlice.ImageOrientationPatient.Length >= 6
            ? firstSlice.ImageOrientationPatient
            : new double[] { 1, 0, 0, 0, 1, 0 };

        bool isSigned = firstSlice.PixelRepresentation == 1;
        short[][]? signedSlices = null;
        ushort[][]? unsignedSlices = null;

        if (isSigned)
        {
            signedSlices = new short[numSlices][];
            for (int z = 0; z < numSlices; z++)
                signedSlices[z] = validSlices[z].RawPixelsSigned ?? new short[origRows * origCols];
        }
        else
        {
            unsignedSlices = new ushort[numSlices][];
            for (int z = 0; z < numSlices; z++)
                unsignedSlices[z] = validSlices[z].RawPixelsUnsigned ?? new ushort[origRows * origCols];
        }

        var ctx = new VolumeContext
        {
            OrigCols = origCols,
            OrigRows = origRows,
            NumSlices = numSlices,
            SpX = spX,
            SpY = spY,
            SpZ = spZ,
            P0X = p0[0],
            P0Y = p0[1],
            P0Z = p0[2],
            StepX = stepX,
            StepY = stepY,
            StepZ = stepZ,
            InvStepSq = invStepSq,
            Iop = srcIop,
            Rx = srcIop[0],
            Ry = srcIop[1],
            Rz = srcIop[2],
            Cx = srcIop[3],
            Cy = srcIop[4],
            Cz = srcIop[5],
            IsSigned = isSigned,
            SignedSlices = signedSlices,
            UnsignedSlices = unsignedSlices,
            ReferenceSlice = firstSlice
        };

        _volumeContextCache[key] = ctx;
        return ctx;
    }

    /// <summary>
    /// Computes a thick-slab projected slice (MIP, MinIP, or Average) across neighboring slices in an orthogonal/standard series.
    /// </summary>
    public static DicomSliceModel ComputeSlabSlice(
        SeriesModel series,
        int centerSliceIndex,
        SlabProjectionMode mode,
        double slabThicknessMm)
    {
        var slices = series.Slices;
        int count = slices.Count;
        centerSliceIndex = Math.Clamp(centerSliceIndex, 0, count - 1);
        var centerSlice = slices[centerSliceIndex];

        if (count < 2 || slabThicknessMm <= 0.1)
        {
            return centerSlice;
        }

        // Compute physical spacing between adjacent slices in mm
        double sliceStepMm = Math.Max(0.1, centerSlice.SliceThickness);
        if (centerSliceIndex + 1 < count &&
            centerSlice.ImagePositionPatient is { Length: >= 3 } pA &&
            slices[centerSliceIndex + 1].ImagePositionPatient is { Length: >= 3 } pB)
        {
            double dx = pB[0] - pA[0];
            double dy = pB[1] - pA[1];
            double dz = pB[2] - pA[2];
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist > 0.05) sliceStepMm = dist;
        }
        else if (centerSliceIndex > 0 &&
                 centerSlice.ImagePositionPatient is { Length: >= 3 } pC &&
                 slices[centerSliceIndex - 1].ImagePositionPatient is { Length: >= 3 } pD)
        {
            double dx = pC[0] - pD[0];
            double dy = pC[1] - pD[1];
            double dz = pC[2] - pD[2];
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist > 0.05) sliceStepMm = dist;
        }

        int halfRadius = (int)Math.Round((slabThicknessMm * 0.5) / sliceStepMm);
        if (halfRadius < 1 && slabThicknessMm >= 1.5)
        {
            halfRadius = 1;
        }
        if (halfRadius < 1)
        {
            return centerSlice;
        }

        int startIdx = Math.Max(0, centerSliceIndex - halfRadius);
        int endIdx = Math.Min(count - 1, centerSliceIndex + halfRadius);
        if (startIdx >= endIdx)
        {
            return centerSlice;
        }

        int w = centerSlice.Columns;
        int h = centerSlice.Rows;
        int totalPixels = w * h;
        if (totalPixels <= 0) return centerSlice;

        // Ensure pixel buffers are loaded for the slab range
        for (int i = startIdx; i <= endIdx; i++)
        {
            var sl = slices[i];
            if (sl.RawPixelsSigned == null && sl.RawPixelsUnsigned == null && sl.RawRgbPixels == null)
            {
                DicomLoaderService.LoadPixelData(sl);
            }
        }

        double effectiveThickness = Math.Max(slabThicknessMm, (endIdx - startIdx + 1) * sliceStepMm);
        var result = CreateMprSliceMetadata(centerSlice, w, h, centerSlice.PixelSpacingX, centerSlice.PixelSpacingY, effectiveThickness);
        result.SopInstanceUid = centerSlice.SopInstanceUid;
        result.InstanceNumber = centerSlice.InstanceNumber;
        result.FrameIndex = centerSlice.FrameIndex;
        result.SliceLocation = centerSlice.SliceLocation;
        result.ImagePositionPatient = centerSlice.ImagePositionPatient;
        result.ImageOrientationPatient = centerSlice.ImageOrientationPatient;
        result.OrientationLeft = centerSlice.OrientationLeft;
        result.OrientationRight = centerSlice.OrientationRight;
        result.OrientationTop = centerSlice.OrientationTop;
        result.OrientationBottom = centerSlice.OrientationBottom;
        result.RepetitionTime = centerSlice.RepetitionTime;
        result.EchoTime = centerSlice.EchoTime;
        result.MagneticFieldStrength = centerSlice.MagneticFieldStrength;
        result.SequenceName = centerSlice.SequenceName;
        result.ImageComments = centerSlice.ImageComments;
        result.DiffusionBValue = centerSlice.DiffusionBValue;
        result.AcquisitionTime = centerSlice.AcquisitionTime;

        if (centerSlice.RawPixelsSigned != null)
        {
            var validBuffers = new List<short[]>(endIdx - startIdx + 1);
            for (int i = startIdx; i <= endIdx; i++)
            {
                var buf = slices[i].RawPixelsSigned;
                if (buf != null && buf.Length >= totalPixels)
                {
                    validBuffers.Add(buf);
                }
            }
            if (validBuffers.Count <= 1) return centerSlice;

            var buffersArr = validBuffers.ToArray();
            int nBuf = buffersArr.Length;
            short[] dst = new short[totalPixels];

            if (mode == SlabProjectionMode.Mip)
            {
                Parallel.For(0, h, r =>
                {
                    int rowStart = r * w;
                    int rowEnd = rowStart + w;
                    var first = buffersArr[0];
                    for (int p = rowStart; p < rowEnd; p++)
                    {
                        short maxVal = first[p];
                        for (int b = 1; b < nBuf; b++)
                        {
                            short v = buffersArr[b][p];
                            if (v > maxVal) maxVal = v;
                        }
                        dst[p] = maxVal;
                    }
                });
            }
            else if (mode == SlabProjectionMode.MinIp)
            {
                Parallel.For(0, h, r =>
                {
                    int rowStart = r * w;
                    int rowEnd = rowStart + w;
                    var first = buffersArr[0];
                    for (int p = rowStart; p < rowEnd; p++)
                    {
                        short minVal = first[p];
                        for (int b = 1; b < nBuf; b++)
                        {
                            short v = buffersArr[b][p];
                            if (v < minVal) minVal = v;
                        }
                        dst[p] = minVal;
                    }
                });
            }
            else // Average
            {
                Parallel.For(0, h, r =>
                {
                    int rowStart = r * w;
                    int rowEnd = rowStart + w;
                    for (int p = rowStart; p < rowEnd; p++)
                    {
                        int sum = 0;
                        for (int b = 0; b < nBuf; b++)
                        {
                            sum += buffersArr[b][p];
                        }
                        dst[p] = (short)((sum + (nBuf >> 1)) / nBuf);
                    }
                });
            }

            result.RawPixelsSigned = dst;
            return result;
        }
        else if (centerSlice.RawPixelsUnsigned != null)
        {
            var validBuffers = new List<ushort[]>(endIdx - startIdx + 1);
            for (int i = startIdx; i <= endIdx; i++)
            {
                var buf = slices[i].RawPixelsUnsigned;
                if (buf != null && buf.Length >= totalPixels)
                {
                    validBuffers.Add(buf);
                }
            }
            if (validBuffers.Count <= 1) return centerSlice;

            var buffersArr = validBuffers.ToArray();
            int nBuf = buffersArr.Length;
            ushort[] dst = new ushort[totalPixels];

            if (mode == SlabProjectionMode.Mip)
            {
                Parallel.For(0, h, r =>
                {
                    int rowStart = r * w;
                    int rowEnd = rowStart + w;
                    var first = buffersArr[0];
                    for (int p = rowStart; p < rowEnd; p++)
                    {
                        ushort maxVal = first[p];
                        for (int b = 1; b < nBuf; b++)
                        {
                            ushort v = buffersArr[b][p];
                            if (v > maxVal) maxVal = v;
                        }
                        dst[p] = maxVal;
                    }
                });
            }
            else if (mode == SlabProjectionMode.MinIp)
            {
                Parallel.For(0, h, r =>
                {
                    int rowStart = r * w;
                    int rowEnd = rowStart + w;
                    var first = buffersArr[0];
                    for (int p = rowStart; p < rowEnd; p++)
                    {
                        ushort minVal = first[p];
                        for (int b = 1; b < nBuf; b++)
                        {
                            ushort v = buffersArr[b][p];
                            if (v < minVal) minVal = v;
                        }
                        dst[p] = minVal;
                    }
                });
            }
            else // Average
            {
                Parallel.For(0, h, r =>
                {
                    int rowStart = r * w;
                    int rowEnd = rowStart + w;
                    for (int p = rowStart; p < rowEnd; p++)
                    {
                        int sum = 0;
                        for (int b = 0; b < nBuf; b++)
                        {
                            sum += buffersArr[b][p];
                        }
                        dst[p] = (ushort)((sum + (nBuf >> 1)) / nBuf);
                    }
                });
            }

            result.RawPixelsUnsigned = dst;
            return result;
        }

        return centerSlice;
    }

    /// <summary>
    /// Resamples an oblique 2D slice (or thick slab MIP/MinIP/Average) from the 3D volume at a given center position and orientation vectors.
    /// Uses trilinear/bilinear interpolation for smooth, artifact-free medical display.
    /// </summary>
    public static DicomSliceModel? GenerateObliqueSlice(
        VolumeContext vol,
        double[] centerPt,
        double[] rowDir,
        double[] colDir,
        int outWidth,
        int outHeight,
        double spX,
        double spY,
        SlabProjectionMode projectionMode = SlabProjectionMode.Average,
        double slabThicknessMm = 0.0)
    {
        if (vol == null || outWidth <= 0 || outHeight <= 0) return null;

        // Origin in 3D: Top-Left pixel of the output slice
        double halfW = (outWidth - 1) * 0.5 * spX;
        double halfH = (outHeight - 1) * 0.5 * spY;

        double originX = centerPt[0] - halfW * rowDir[0] - halfH * colDir[0];
        double originY = centerPt[1] - halfW * rowDir[1] - halfH * colDir[1];
        double originZ = centerPt[2] - halfW * rowDir[2] - halfH * colDir[2];

        double effectiveThickness = slabThicknessMm > 0.1 ? slabThicknessMm : (spX + spY) / 2.0;
        var mprSlice = CreateMprSliceMetadata(vol.ReferenceSlice, outWidth, outHeight, spX, spY, effectiveThickness);
        mprSlice.ImageOrientationPatient = new double[]
        {
            rowDir[0], rowDir[1], rowDir[2],
            colDir[0], colDir[1], colDir[2]
        };
        mprSlice.ImagePositionPatient = new double[] { originX, originY, originZ };

        // Compute normal vector of the slice plane for thick-slab integration
        double nx = rowDir[1] * colDir[2] - rowDir[2] * colDir[1];
        double ny = rowDir[2] * colDir[0] - rowDir[0] * colDir[2];
        double nz = rowDir[0] * colDir[1] - rowDir[1] * colDir[0];
        double nlen = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (nlen > 1e-5) { nx /= nlen; ny /= nlen; nz /= nlen; }

        double sampleStepMm = Math.Max(0.6, Math.Min(vol.SpX, Math.Max(0.5, vol.SpZ)));
        int halfSteps = slabThicknessMm > 0.5 ? Math.Clamp((int)Math.Round((slabThicknessMm * 0.5) / sampleStepMm), 1, 40) : 0;

        int totalPixels = outWidth * outHeight;
        int origCols = vol.OrigCols;
        int origRows = vol.OrigRows;
        int numSlices = vol.NumSlices;

        double p0x = vol.P0X, p0y = vol.P0Y, p0z = vol.P0Z;
        double rx = vol.Rx, ry = vol.Ry, rz = vol.Rz;
        double cx = vol.Cx, cy = vol.Cy, cz = vol.Cz;
        double stepX = vol.StepX, stepY = vol.StepY, stepZ = vol.StepZ;
        double invStepSq = vol.InvStepSq;
        double invSpX = 1.0 / vol.SpX;
        double invSpY = 1.0 / vol.SpY;

        if (vol.IsSigned)
        {
            short[] buffer = new short[totalPixels];
            var signedSlices = vol.SignedSlices!;

            short SampleSignedPoint(double px, double py, double pz, out bool valid)
            {
                double dX = px - p0x;
                double dY = py - p0y;
                double dZ = pz - p0z;

                double w = (dX * stepX + dY * stepY + dZ * stepZ) * invStepSq;
                if (w < 0 || w > numSlices - 1)
                {
                    valid = false;
                    return 0;
                }

                int w0 = (int)w;
                int w1 = Math.Min(numSlices - 1, w0 + 1);
                float tw = (float)(w - w0);

                double s0x = p0x + w * stepX;
                double s0y = p0y + w * stepY;
                double s0z = p0z + w * stepZ;

                double vX = px - s0x;
                double vY = py - s0y;
                double vZ = pz - s0z;

                double uCol = (vX * rx + vY * ry + vZ * rz) * invSpX;
                double uRow = (vX * cx + vY * cy + vZ * cz) * invSpY;

                if (uCol < 0 || uCol >= origCols - 1 || uRow < 0 || uRow >= origRows - 1)
                {
                    int cu = (int)Math.Round(uCol);
                    int ru = (int)Math.Round(uRow);
                    if (cu >= 0 && cu < origCols && ru >= 0 && ru < origRows)
                    {
                        var s0 = signedSlices[w0];
                        var s1 = signedSlices[w1];
                        int idx = ru * origCols + cu;
                        valid = true;
                        return (short)Math.Round((1f - tw) * s0[idx] + tw * s1[idx]);
                    }
                    valid = false;
                    return 0;
                }

                int c0 = (int)uCol;
                int r0 = (int)uRow;
                float tc = (float)(uCol - c0);
                float tr = (float)(uRow - r0);

                var sl0 = signedSlices[w0];
                var sl1 = signedSlices[w1];

                int idx00 = r0 * origCols + c0;
                int idx01 = idx00 + 1;
                int idx10 = idx00 + origCols;
                int idx11 = idx10 + 1;

                float v0 = (1f - tc) * (1f - tr) * sl0[idx00] +
                           tc * (1f - tr) * sl0[idx01] +
                           (1f - tc) * tr * sl0[idx10] +
                           tc * tr * sl0[idx11];

                float v1 = (1f - tc) * (1f - tr) * sl1[idx00] +
                           tc * (1f - tr) * sl1[idx01] +
                           (1f - tc) * tr * sl1[idx10] +
                           tc * tr * sl1[idx11];

                valid = true;
                return (short)Math.Round((1f - tw) * v0 + tw * v1);
            }

            Parallel.For(0, outHeight, r =>
            {
                int dstRow = r * outWidth;
                double rColX = originX + r * spY * colDir[0];
                double rColY = originY + r * spY * colDir[1];
                double rColZ = originZ + r * spY * colDir[2];

                for (int c = 0; c < outWidth; c++)
                {
                    double basePx = rColX + c * spX * rowDir[0];
                    double basePy = rColY + c * spX * rowDir[1];
                    double basePz = rColZ + c * spX * rowDir[2];

                    if (halfSteps <= 0)
                    {
                        buffer[dstRow + c] = SampleSignedPoint(basePx, basePy, basePz, out _);
                    }
                    else if (projectionMode == SlabProjectionMode.Mip)
                    {
                        short best = short.MinValue;
                        bool anyValid = false;
                        for (int s = -halfSteps; s <= halfSteps; s++)
                        {
                            double off = s * sampleStepMm;
                            short val = SampleSignedPoint(basePx + off * nx, basePy + off * ny, basePz + off * nz, out bool v);
                            if (v)
                            {
                                if (!anyValid || val > best) best = val;
                                anyValid = true;
                            }
                        }
                        buffer[dstRow + c] = anyValid ? best : (short)0;
                    }
                    else if (projectionMode == SlabProjectionMode.MinIp)
                    {
                        short best = short.MaxValue;
                        bool anyValid = false;
                        for (int s = -halfSteps; s <= halfSteps; s++)
                        {
                            double off = s * sampleStepMm;
                            short val = SampleSignedPoint(basePx + off * nx, basePy + off * ny, basePz + off * nz, out bool v);
                            if (v)
                            {
                                if (!anyValid || val < best) best = val;
                                anyValid = true;
                            }
                        }
                        buffer[dstRow + c] = anyValid ? best : (short)0;
                    }
                    else
                    {
                        int sum = 0;
                        int cnt = 0;
                        for (int s = -halfSteps; s <= halfSteps; s++)
                        {
                            double off = s * sampleStepMm;
                            short val = SampleSignedPoint(basePx + off * nx, basePy + off * ny, basePz + off * nz, out bool v);
                            if (v)
                            {
                                sum += val;
                                cnt++;
                            }
                        }
                        buffer[dstRow + c] = cnt > 0 ? (short)((sum + (cnt >> 1)) / cnt) : (short)0;
                    }
                }
            });

            mprSlice.RawPixelsSigned = buffer;
        }
        else
        {
            ushort[] buffer = new ushort[totalPixels];
            var unsignedSlices = vol.UnsignedSlices!;

            ushort SampleUnsignedPoint(double px, double py, double pz, out bool valid)
            {
                double dX = px - p0x;
                double dY = py - p0y;
                double dZ = pz - p0z;

                double w = (dX * stepX + dY * stepY + dZ * stepZ) * invStepSq;
                if (w < 0 || w > numSlices - 1)
                {
                    valid = false;
                    return 0;
                }

                int w0 = (int)w;
                int w1 = Math.Min(numSlices - 1, w0 + 1);
                float tw = (float)(w - w0);

                double s0x = p0x + w * stepX;
                double s0y = p0y + w * stepY;
                double s0z = p0z + w * stepZ;

                double vX = px - s0x;
                double vY = py - s0y;
                double vZ = pz - s0z;

                double uCol = (vX * rx + vY * ry + vZ * rz) * invSpX;
                double uRow = (vX * cx + vY * cy + vZ * cz) * invSpY;

                if (uCol < 0 || uCol >= origCols - 1 || uRow < 0 || uRow >= origRows - 1)
                {
                    int cu = (int)Math.Round(uCol);
                    int ru = (int)Math.Round(uRow);
                    if (cu >= 0 && cu < origCols && ru >= 0 && ru < origRows)
                    {
                        var s0 = unsignedSlices[w0];
                        var s1 = unsignedSlices[w1];
                        int idx = ru * origCols + cu;
                        valid = true;
                        return (ushort)Math.Round((1f - tw) * s0[idx] + tw * s1[idx]);
                    }
                    valid = false;
                    return 0;
                }

                int c0 = (int)uCol;
                int r0 = (int)uRow;
                float tc = (float)(uCol - c0);
                float tr = (float)(uRow - r0);

                var sl0 = unsignedSlices[w0];
                var sl1 = unsignedSlices[w1];

                int idx00 = r0 * origCols + c0;
                int idx01 = idx00 + 1;
                int idx10 = idx00 + origCols;
                int idx11 = idx10 + 1;

                float v0 = (1f - tc) * (1f - tr) * sl0[idx00] +
                           tc * (1f - tr) * sl0[idx01] +
                           (1f - tc) * tr * sl0[idx10] +
                           tc * tr * sl0[idx11];

                float v1 = (1f - tc) * (1f - tr) * sl1[idx00] +
                           tc * (1f - tr) * sl1[idx01] +
                           (1f - tc) * tr * sl1[idx10] +
                           tc * tr * sl1[idx11];

                valid = true;
                return (ushort)Math.Round((1f - tw) * v0 + tw * v1);
            }

            Parallel.For(0, outHeight, r =>
            {
                int dstRow = r * outWidth;
                double rColX = originX + r * spY * colDir[0];
                double rColY = originY + r * spY * colDir[1];
                double rColZ = originZ + r * spY * colDir[2];

                for (int c = 0; c < outWidth; c++)
                {
                    double basePx = rColX + c * spX * rowDir[0];
                    double basePy = rColY + c * spY * rowDir[1];
                    double basePz = rColZ + c * spX * rowDir[2];

                    if (halfSteps <= 0)
                    {
                        buffer[dstRow + c] = SampleUnsignedPoint(basePx, basePy, basePz, out _);
                    }
                    else if (projectionMode == SlabProjectionMode.Mip)
                    {
                        ushort best = ushort.MinValue;
                        bool anyValid = false;
                        for (int s = -halfSteps; s <= halfSteps; s++)
                        {
                            double off = s * sampleStepMm;
                            ushort val = SampleUnsignedPoint(basePx + off * nx, basePy + off * ny, basePz + off * nz, out bool v);
                            if (v)
                            {
                                if (!anyValid || val > best) best = val;
                                anyValid = true;
                            }
                        }
                        buffer[dstRow + c] = anyValid ? best : (ushort)0;
                    }
                    else if (projectionMode == SlabProjectionMode.MinIp)
                    {
                        ushort best = ushort.MaxValue;
                        bool anyValid = false;
                        for (int s = -halfSteps; s <= halfSteps; s++)
                        {
                            double off = s * sampleStepMm;
                            ushort val = SampleUnsignedPoint(basePx + off * nx, basePy + off * ny, basePz + off * nz, out bool v);
                            if (v)
                            {
                                if (!anyValid || val < best) best = val;
                                anyValid = true;
                            }
                        }
                        buffer[dstRow + c] = anyValid ? best : (ushort)0;
                    }
                    else
                    {
                        int sum = 0;
                        int cnt = 0;
                        for (int s = -halfSteps; s <= halfSteps; s++)
                        {
                            double off = s * sampleStepMm;
                            ushort val = SampleUnsignedPoint(basePx + off * nx, basePy + off * ny, basePz + off * nz, out bool v);
                            if (v)
                            {
                                sum += val;
                                cnt++;
                            }
                        }
                        buffer[dstRow + c] = cnt > 0 ? (ushort)((sum + (cnt >> 1)) / cnt) : (ushort)0;
                    }
                }
            });

            mprSlice.RawPixelsUnsigned = buffer;
        }

        // Set anatomical orientation markers
        SetAnatomicalOrientationLabels(mprSlice, rowDir, colDir);

        return mprSlice;
    }

    private static void SetAnatomicalOrientationLabels(DicomSliceModel slice, double[] r, double[] c)
    {
        string GetAxisLabel(double[] vec)
        {
            double ax = Math.Abs(vec[0]);
            double ay = Math.Abs(vec[1]);
            double az = Math.Abs(vec[2]);
            if (ax >= ay && ax >= az) return vec[0] > 0 ? "L" : "R";
            if (ay >= ax && ay >= az) return vec[1] > 0 ? "P" : "A";
            return vec[2] > 0 ? "S" : "I";
        }

        string GetOppositeLabel(string lbl) => lbl switch
        {
            "L" => "R", "R" => "L",
            "A" => "P", "P" => "A",
            "S" => "I", "I" => "S",
            _ => ""
        };

        string rLabel = GetAxisLabel(r);
        string cLabel = GetAxisLabel(c);
        slice.OrientationRight = rLabel;
        slice.OrientationLeft = GetOppositeLabel(rLabel);
        slice.OrientationBottom = cLabel;
        slice.OrientationTop = GetOppositeLabel(cLabel);
    }
}
