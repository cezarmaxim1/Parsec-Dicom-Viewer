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

    /// <summary>
    /// Generates or retrieves an orthogonal MPR series (Coronal, Sagittal, or Axial) from a 3D volumetric series.
    /// Fast multi-threaded volume slicing with exact DICOM coordinates and full pixel preloading.
    /// </summary>
    public static SeriesModel? GenerateOrthogonalSeries(SeriesModel sourceSeries, OrthogonalPlane targetPlane)
    {
        if (sourceSeries.Slices.Count < 3) return null;

        var firstSlice = sourceSeries.Slices[0];
        var originalPlane = DetectOrientation(firstSlice);

        if (originalPlane == targetPlane)
            return sourceSeries;

        string cacheKey = $"{sourceSeries.SeriesInstanceUid}_{targetPlane}_{sourceSeries.Slices.Count}";
        if (_mprCache.TryGetValue(cacheKey, out var cachedSeries))
        {
            return cachedSeries;
        }

        int numSlices = sourceSeries.Slices.Count;
        int origRows = firstSlice.Rows;
        int origCols = firstSlice.Columns;

        if (origRows <= 0 || origCols <= 0) return null;

        // 1. Preload pixel data in fast batch
        DicomLoaderService.LoadSeriesPixelData(sourceSeries);

        double spX = Math.Max(0.0001, firstSlice.PixelSpacingX);
        double spY = Math.Max(0.0001, firstSlice.PixelSpacingY);

        // Calculate slice step vector and through-plane spacing
        double stepX = 0, stepY = 0, stepZ = 0;
        var lastSlice = sourceSeries.Slices[numSlices - 1];

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
                srcSignedSlices[z] = sourceSeries.Slices[z].RawPixelsSigned ?? new short[origRows * origCols];
        }
        else
        {
            srcUnsignedSlices = new ushort[numSlices][];
            for (int z = 0; z < numSlices; z++)
                srcUnsignedSlices[z] = sourceSeries.Slices[z].RawPixelsUnsigned ?? new ushort[origRows * origCols];
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

        var firstSlice = series.Slices[0];
        int numSlices = series.Slices.Count;
        int origRows = firstSlice.Rows;
        int origCols = firstSlice.Columns;
        if (origRows <= 0 || origCols <= 0) return null;

        double spX = Math.Max(0.0001, firstSlice.PixelSpacingX);
        double spY = Math.Max(0.0001, firstSlice.PixelSpacingY);

        double stepX = 0, stepY = 0, stepZ = 0;
        var lastSlice = series.Slices[numSlices - 1];

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
                signedSlices[z] = series.Slices[z].RawPixelsSigned ?? new short[origRows * origCols];
        }
        else
        {
            unsignedSlices = new ushort[numSlices][];
            for (int z = 0; z < numSlices; z++)
                unsignedSlices[z] = series.Slices[z].RawPixelsUnsigned ?? new ushort[origRows * origCols];
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
    /// Resamples an oblique 2D slice from the 3D volume at a given center position and orientation vectors.
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
        double spY)
    {
        if (vol == null || outWidth <= 0 || outHeight <= 0) return null;

        // Origin in 3D: Top-Left pixel of the output slice
        double halfW = (outWidth - 1) * 0.5 * spX;
        double halfH = (outHeight - 1) * 0.5 * spY;

        double originX = centerPt[0] - halfW * rowDir[0] - halfH * colDir[0];
        double originY = centerPt[1] - halfW * rowDir[1] - halfH * colDir[1];
        double originZ = centerPt[2] - halfW * rowDir[2] - halfH * colDir[2];

        var mprSlice = CreateMprSliceMetadata(vol.ReferenceSlice, outWidth, outHeight, spX, spY, (spX + spY) / 2.0);
        mprSlice.ImageOrientationPatient = new double[]
        {
            rowDir[0], rowDir[1], rowDir[2],
            colDir[0], colDir[1], colDir[2]
        };
        mprSlice.ImagePositionPatient = new double[] { originX, originY, originZ };

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

            Parallel.For(0, outHeight, r =>
            {
                int dstRow = r * outWidth;
                double rColX = originX + r * spY * colDir[0];
                double rColY = originY + r * spY * colDir[1];
                double rColZ = originZ + r * spY * colDir[2];

                for (int c = 0; c < outWidth; c++)
                {
                    double px = rColX + c * spX * rowDir[0];
                    double py = rColY + c * spX * rowDir[1];
                    double pz = rColZ + c * spX * rowDir[2];

                    double dX = px - p0x;
                    double dY = py - p0y;
                    double dZ = pz - p0z;

                    // Continuous slice index
                    double w = (dX * stepX + dY * stepY + dZ * stepZ) * invStepSq;
                    if (w < 0 || w > numSlices - 1)
                    {
                        buffer[dstRow + c] = 0;
                        continue;
                    }

                    int w0 = (int)w;
                    int w1 = Math.Min(numSlices - 1, w0 + 1);
                    float tw = (float)(w - w0);

                    // Slice plane origin
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
                            buffer[dstRow + c] = (short)Math.Round((1f - tw) * s0[idx] + tw * s1[idx]);
                        }
                        else
                        {
                            buffer[dstRow + c] = 0;
                        }
                        continue;
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

                    // Bilinear on slice 0
                    float v0 = (1f - tc) * (1f - tr) * sl0[idx00] +
                               tc * (1f - tr) * sl0[idx01] +
                               (1f - tc) * tr * sl0[idx10] +
                               tc * tr * sl0[idx11];

                    // Bilinear on slice 1
                    float v1 = (1f - tc) * (1f - tr) * sl1[idx00] +
                               tc * (1f - tr) * sl1[idx01] +
                               (1f - tc) * tr * sl1[idx10] +
                               tc * tr * sl1[idx11];

                    buffer[dstRow + c] = (short)Math.Round((1f - tw) * v0 + tw * v1);
                }
            });

            mprSlice.RawPixelsSigned = buffer;
        }
        else
        {
            ushort[] buffer = new ushort[totalPixels];
            var unsignedSlices = vol.UnsignedSlices!;

            Parallel.For(0, outHeight, r =>
            {
                int dstRow = r * outWidth;
                double rColX = originX + r * spY * colDir[0];
                double rColY = originY + r * spY * colDir[1];
                double rColZ = originZ + r * spY * colDir[2];

                for (int c = 0; c < outWidth; c++)
                {
                    double px = rColX + c * spX * rowDir[0];
                    double py = rColY + c * spX * rowDir[1];
                    double pz = rColZ + c * spX * rowDir[2];

                    double dX = px - p0x;
                    double dY = py - p0y;
                    double dZ = pz - p0z;

                    double w = (dX * stepX + dY * stepY + dZ * stepZ) * invStepSq;
                    if (w < 0 || w > numSlices - 1)
                    {
                        buffer[dstRow + c] = 0;
                        continue;
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
                            buffer[dstRow + c] = (ushort)Math.Round((1f - tw) * s0[idx] + tw * s1[idx]);
                        }
                        else
                        {
                            buffer[dstRow + c] = 0;
                        }
                        continue;
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

                    buffer[dstRow + c] = (ushort)Math.Round((1f - tw) * v0 + tw * v1);
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
