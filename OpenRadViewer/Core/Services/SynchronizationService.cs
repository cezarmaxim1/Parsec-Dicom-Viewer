// @purpose: Spatial 3D synchronization across viewports (patient coordinate conversions, 3D crosshair projection, scout/localizer reference lines, slice linking).
// @depends: OpenRadViewer.Core.Models, OpenRadViewer.Core.Services (MprService)
// @exports: SynchronizationService

using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public static class SynchronizationService
{
    /// <summary>
    /// Finds the slice in targetSeries that is closest in 3D patient space to referenceSlice.
    /// </summary>
    public static int FindClosestSliceIndex(DicomSliceModel referenceSlice, SeriesModel targetSeries)
    {
        if (targetSeries.Slices.Count == 0 || referenceSlice.ImagePositionPatient == null || referenceSlice.ImagePositionPatient.Length < 3)
            return 0;

        // If target series has a different plane orientation, do not force index 0
        var targetFirst = targetSeries.Slices[0];
        if (referenceSlice.ImageOrientationPatient != null && referenceSlice.ImageOrientationPatient.Length >= 6 &&
            targetFirst.ImageOrientationPatient != null && targetFirst.ImageOrientationPatient.Length >= 6)
        {
            if (MprService.DetectOrientation(referenceSlice) != MprService.DetectOrientation(targetFirst))
            {
                return targetSeries.Slices.Count / 2;
            }
        }

        double refX = referenceSlice.ImagePositionPatient[0];
        double refY = referenceSlice.ImagePositionPatient[1];
        double refZ = referenceSlice.ImagePositionPatient[2];

        bool hasNormal = false;
        double nx = 0, ny = 0, nz = 0;
        if (referenceSlice.ImageOrientationPatient != null && referenceSlice.ImageOrientationPatient.Length >= 6)
        {
            double rx = referenceSlice.ImageOrientationPatient[0];
            double ry = referenceSlice.ImageOrientationPatient[1];
            double rz = referenceSlice.ImageOrientationPatient[2];
            double cx = referenceSlice.ImageOrientationPatient[3];
            double cy = referenceSlice.ImageOrientationPatient[4];
            double cz = referenceSlice.ImageOrientationPatient[5];

            nx = ry * cz - rz * cy;
            ny = rz * cx - rx * cz;
            nz = rx * cy - ry * cx;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0.0001)
            {
                nx /= len;
                ny /= len;
                nz /= len;
                hasNormal = true;
            }
        }

        int bestIndex = 0;
        double minDistance = double.MaxValue;

        for (int i = 0; i < targetSeries.Slices.Count; i++)
        {
            var targetSlice = targetSeries.Slices[i];
            if (targetSlice.ImagePositionPatient == null || targetSlice.ImagePositionPatient.Length < 3)
                continue;

            double dx = targetSlice.ImagePositionPatient[0] - refX;
            double dy = targetSlice.ImagePositionPatient[1] - refY;
            double dz = targetSlice.ImagePositionPatient[2] - refZ;

            double dist;
            if (hasNormal)
            {
                // Orthogonal distance between parallel slice planes along scan direction
                dist = Math.Abs(dx * nx + dy * ny + dz * nz);
            }
            else
            {
                dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            if (dist < minDistance)
            {
                minDistance = dist;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// Computes the 3D patient space coordinate (X, Y, Z in mm) for a pixel (x, y) on a DICOM slice.
    /// </summary>
    public static (double x, double y, double z)? PatientPointFromPixel(DicomSliceModel slice, double pixelX, double pixelY)
    {
        if (slice.ImagePositionPatient == null || slice.ImagePositionPatient.Length < 3 ||
            slice.ImageOrientationPatient == null || slice.ImageOrientationPatient.Length < 6)
        {
            return null;
        }

        double sx = slice.ImagePositionPatient[0];
        double sy = slice.ImagePositionPatient[1];
        double sz = slice.ImagePositionPatient[2];

        double rx = slice.ImageOrientationPatient[0];
        double ry = slice.ImageOrientationPatient[1];
        double rz = slice.ImageOrientationPatient[2];

        double cx = slice.ImageOrientationPatient[3];
        double cy = slice.ImageOrientationPatient[4];
        double cz = slice.ImageOrientationPatient[5];

        double px = sx + (pixelX * slice.PixelSpacingX * rx) + (pixelY * slice.PixelSpacingY * cx);
        double py = sy + (pixelX * slice.PixelSpacingX * ry) + (pixelY * slice.PixelSpacingY * cy);
        double pz = sz + (pixelX * slice.PixelSpacingX * rz) + (pixelY * slice.PixelSpacingY * cz);

        return (px, py, pz);
    }

    /// <summary>
    /// Projects a 3D patient point (px, py, pz) onto a target series, returning the closest slice index,
    /// the corresponding (pixelX, pixelY) on that slice, and whether it lies within the slice boundaries.
    /// </summary>
    public static (int sliceIndex, double pixelX, double pixelY, bool inBounds)? ProjectPointToSeries(double px, double py, double pz, SeriesModel series)
    {
        if (series.Slices.Count == 0) return null;

        int bestIndex = 0;
        double minDistance = double.MaxValue;

        for (int i = 0; i < series.Slices.Count; i++)
        {
            var slice = series.Slices[i];
            if (slice.ImagePositionPatient == null || slice.ImagePositionPatient.Length < 3) continue;

            double sx = slice.ImagePositionPatient[0];
            double sy = slice.ImagePositionPatient[1];
            double sz = slice.ImagePositionPatient[2];

            double dist;
            if (slice.ImageOrientationPatient != null && slice.ImageOrientationPatient.Length >= 6)
            {
                // Normal = Row x Col
                double rx = slice.ImageOrientationPatient[0];
                double ry = slice.ImageOrientationPatient[1];
                double rz = slice.ImageOrientationPatient[2];

                double cx = slice.ImageOrientationPatient[3];
                double cy = slice.ImageOrientationPatient[4];
                double cz = slice.ImageOrientationPatient[5];

                double nx = ry * cz - rz * cy;
                double ny = rz * cx - rx * cz;
                double nz = rx * cy - ry * cx;

                // Orthogonal distance from slice plane
                dist = Math.Abs((px - sx) * nx + (py - sy) * ny + (pz - sz) * nz);
            }
            else
            {
                double dx = px - sx;
                double dy = py - sy;
                double dz = pz - sz;
                dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            if (dist < minDistance)
            {
                minDistance = dist;
                bestIndex = i;
            }
        }

        var bestSlice = series.Slices[bestIndex];
        if (bestSlice.ImagePositionPatient == null || bestSlice.ImagePositionPatient.Length < 3 ||
            bestSlice.ImageOrientationPatient == null || bestSlice.ImageOrientationPatient.Length < 6)
        {
            return (bestIndex, bestSlice.Columns / 2.0, bestSlice.Rows / 2.0, true);
        }

        double ox = bestSlice.ImagePositionPatient[0];
        double oy = bestSlice.ImagePositionPatient[1];
        double oz = bestSlice.ImagePositionPatient[2];

        double r_x = bestSlice.ImageOrientationPatient[0];
        double r_y = bestSlice.ImageOrientationPatient[1];
        double r_z = bestSlice.ImageOrientationPatient[2];

        double c_x = bestSlice.ImageOrientationPatient[3];
        double c_y = bestSlice.ImageOrientationPatient[4];
        double c_z = bestSlice.ImageOrientationPatient[5];

        double vx = px - ox;
        double vy = py - oy;
        double vz = pz - oz;

        double projRow = vx * r_x + vy * r_y + vz * r_z;
        double projCol = vx * c_x + vy * c_y + vz * c_z;

        double spX = Math.Max(0.0001, bestSlice.PixelSpacingX);
        double spY = Math.Max(0.0001, bestSlice.PixelSpacingY);

        double targetPx = projRow / spX;
        double targetPy = projCol / spY;

        bool inBounds = targetPx >= 0 && targetPx <= bestSlice.Columns &&
                        targetPy >= 0 && targetPy <= bestSlice.Rows;

        return (bestIndex, targetPx, targetPy, inBounds);
    }

    /// <summary>
    /// Computes the intersection line segment between two non-parallel slice planes in target image pixel coordinates.
    /// Used to render scout / cross-reference lines.
    /// </summary>
    public static (System.Windows.Point p1, System.Windows.Point p2)? CalculatePlaneIntersection(DicomSliceModel sourceSlice, DicomSliceModel targetSlice)
    {
        if (sourceSlice.ImagePositionPatient == null || sourceSlice.ImagePositionPatient.Length < 3 ||
            sourceSlice.ImageOrientationPatient == null || sourceSlice.ImageOrientationPatient.Length < 6 ||
            targetSlice.ImagePositionPatient == null || targetSlice.ImagePositionPatient.Length < 3 ||
            targetSlice.ImageOrientationPatient == null || targetSlice.ImageOrientationPatient.Length < 6)
        {
            return null;
        }

        // Source slice plane: P_s and normal n_s
        double px_s = sourceSlice.ImagePositionPatient[0];
        double py_s = sourceSlice.ImagePositionPatient[1];
        double pz_s = sourceSlice.ImagePositionPatient[2];

        double rx_s = sourceSlice.ImageOrientationPatient[0];
        double ry_s = sourceSlice.ImageOrientationPatient[1];
        double rz_s = sourceSlice.ImageOrientationPatient[2];
        double cx_s = sourceSlice.ImageOrientationPatient[3];
        double cy_s = sourceSlice.ImageOrientationPatient[4];
        double cz_s = sourceSlice.ImageOrientationPatient[5];

        double nx_s = ry_s * cz_s - rz_s * cy_s;
        double ny_s = rz_s * cx_s - rx_s * cz_s;
        double nz_s = rx_s * cy_s - ry_s * cx_s;
        double len_s = Math.Sqrt(nx_s * nx_s + ny_s * ny_s + nz_s * nz_s);
        if (len_s < 0.0001) return null;
        nx_s /= len_s; ny_s /= len_s; nz_s /= len_s;

        // Target slice plane: P_t and basis vectors
        double px_t = targetSlice.ImagePositionPatient[0];
        double py_t = targetSlice.ImagePositionPatient[1];
        double pz_t = targetSlice.ImagePositionPatient[2];

        double rx_t = targetSlice.ImageOrientationPatient[0];
        double ry_t = targetSlice.ImageOrientationPatient[1];
        double rz_t = targetSlice.ImageOrientationPatient[2];
        double cx_t = targetSlice.ImageOrientationPatient[3];
        double cy_t = targetSlice.ImageOrientationPatient[4];
        double cz_t = targetSlice.ImageOrientationPatient[5];

        double nx_t = ry_t * cz_t - rz_t * cy_t;
        double ny_t = rz_t * cx_t - rx_t * cz_t;
        double nz_t = rx_t * cy_t - ry_t * cx_t;
        double len_t = Math.Sqrt(nx_t * nx_t + ny_t * ny_t + nz_t * nz_t);
        if (len_t < 0.0001) return null;
        nx_t /= len_t; ny_t /= len_t; nz_t /= len_t;

        // Parallel planes check: dot product close to +-1 means parallel, no cross line
        double dotNormals = Math.Abs(nx_s * nx_t + ny_s * ny_t + nz_s * nz_t);
        if (dotNormals > 0.95) return null;

        double spX = Math.Max(0.0001, targetSlice.PixelSpacingX);
        double spY = Math.Max(0.0001, targetSlice.PixelSpacingY);

        // Line equation on target slice: A*u + B*v + C = 0
        double A = spX * (rx_t * nx_s + ry_t * ny_s + rz_t * nz_s);
        double B = spY * (cx_t * nx_s + cy_t * ny_s + cz_t * nz_s);
        double C = (px_t - px_s) * nx_s + (py_t - py_s) * ny_s + (pz_t - pz_s) * nz_s;

        if (Math.Abs(A) < 1e-7 && Math.Abs(B) < 1e-7) return null;

        double W = targetSlice.Columns;
        double H = targetSlice.Rows;

        var pts = new List<System.Windows.Point>();

        const double tol = 2.0;

        if (Math.Abs(B) > 1e-7)
        {
            double v0 = -C / B;
            if (v0 >= -tol && v0 <= H + tol) pts.Add(new System.Windows.Point(0, Math.Clamp(v0, 0, H)));

            double vW = (-C - A * W) / B;
            if (vW >= -tol && vW <= H + tol) pts.Add(new System.Windows.Point(W, Math.Clamp(vW, 0, H)));
        }

        if (Math.Abs(A) > 1e-7)
        {
            double u0 = -C / A;
            if (u0 >= -tol && u0 <= W + tol) pts.Add(new System.Windows.Point(Math.Clamp(u0, 0, W), 0));

            double uH = (-C - B * H) / A;
            if (uH >= -tol && uH <= W + tol) pts.Add(new System.Windows.Point(Math.Clamp(uH, 0, W), H));
        }

        var distinct = new List<System.Windows.Point>();
        foreach (var pt in pts)
        {
            if (!distinct.Any(d => (d - pt).Length < 1.0))
                distinct.Add(pt);
        }

        if (distinct.Count >= 2)
        {
            return (distinct[0], distinct[1]);
        }

        return null;
    }
}
