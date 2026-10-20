// @purpose: High-level volume rendering manager providing GPU/CPU fallback, transfer function presets (Bones, Angio, Skin, MIP, Airways), and camera rotation controls.
// @depends: OpenRadViewer.Core.Models, OpenRadViewer.Core.Services (D3D11VolumeRenderer)
// @exports: VolumeRenderingService, VolumeRenderingPreset

using System.Windows.Media.Imaging;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public enum VolumeRenderingPreset
{
    Bones,
    Angio,
    Skin,
    MIP,
    Airways
}

public class VolumeRenderingService
{
    public static VolumeRenderingService Instance { get; } = new();

    private readonly D3D11VolumeRenderer? _gpuRenderer;

    public VolumeRenderingService()
    {
        try
        {
            _gpuRenderer = new D3D11VolumeRenderer();
        }
        catch
        {
            _gpuRenderer = null;
        }
    }

    public bool IsGpuAccelerated => _gpuRenderer?.IsAvailable == true;

    public record RenderParams(
        double Yaw,        // in degrees
        double Pitch,      // in degrees
        double Roll,       // in degrees
        double Zoom,
        double PanX,
        double PanY,
        double WindowCenter,
        double WindowWidth,
        VolumeRenderingPreset Preset,
        bool IsFastMode = false,
        double CropPosterior = 0.0,
        double CropAnterior = 0.0,
        double CropSuperior = 0.0,
        double CropInferior = 0.0,
        double CropLeft = 0.0,
        double CropRight = 0.0
    );

    /// <summary>
    /// Renders a 3D raycast volume rendering into a WriteableBitmap.
    /// Uses hardware GPU raymarching (Direct3D 11) for instantaneous 60-120 FPS performance,
    /// with automatic seamless CPU fallback.
    /// </summary>
    public WriteableBitmap? RenderVolume(MprService.VolumeContext vol, RenderParams p, int width, int height, string? volumeKey = null)
    {
        if (vol == null || vol.NumSlices < 2 || vol.OrigCols < 2 || vol.OrigRows < 2)
            return null;

        // Try GPU Hardware Acceleration first (1-3 ms per frame)
        if (_gpuRenderer != null && _gpuRenderer.IsAvailable)
        {
            try
            {
                string key = volumeKey ?? $"{vol.NumSlices}_{vol.OrigCols}_{vol.OrigRows}_{vol.SpX}_{vol.SpY}";
                var gpuResult = _gpuRenderer.RenderVolume(vol, key, p, width, height);
                if (gpuResult != null) return gpuResult;
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "d3d_render_error.log"), ex.ToString());
                }
                catch { }
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        bitmap.Lock();

        try
        {
            unsafe
            {
                byte* backBuffer = (byte*)bitmap.BackBuffer;
                int stride = bitmap.BackBufferStride;

                int cols = vol.OrigCols;
                int rows = vol.OrigRows;
                int slices = vol.NumSlices;

                double spX = Math.Max(0.1, vol.SpX);
                double spY = Math.Max(0.1, vol.SpY);
                double thick = Math.Max(0.1, Math.Sqrt(vol.StepX * vol.StepX + vol.StepY * vol.StepY + vol.StepZ * vol.StepZ));
                if (thick < 0.1) thick = 1.0;

                // Physical size in mm
                double sizeX = cols * spX;
                double sizeY = rows * spY;
                double sizeZ = slices * thick;
                double maxSize = Math.Max(sizeX, Math.Max(sizeY, sizeZ));

                // Bounding box centered at (0,0,0) with optional 6-plane crop
                double minX = -sizeX * 0.5 + sizeX * Math.Clamp(p.CropRight, 0.0, 0.99);
                double maxX = sizeX * 0.5 - sizeX * Math.Clamp(p.CropLeft, 0.0, 0.99);
                double minY = -sizeY * 0.5 + sizeY * Math.Clamp(p.CropAnterior, 0.0, 0.99);
                double maxY = sizeY * 0.5 - sizeY * Math.Clamp(p.CropPosterior, 0.0, 0.99);
                double minZ = -sizeZ * 0.5 + sizeZ * Math.Clamp(p.CropInferior, 0.0, 0.99);
                double maxZ = sizeZ * 0.5 - sizeZ * Math.Clamp(p.CropSuperior, 0.0, 0.99);
                if (minX >= maxX) maxX = minX + 0.01;
                if (minY >= maxY) maxY = minY + 0.01;
                if (minZ >= maxZ) maxZ = minZ + 0.01;

                // Rotation angles in radians
                double radYaw = p.Yaw * Math.PI / 180.0;
                double radPitch = p.Pitch * Math.PI / 180.0;
                double radRoll = p.Roll * Math.PI / 180.0;

                // Compute Camera Basis Vectors: U (Right), V (Down), W (Ray Direction / View)
                // Turntable orbit around vertical body axis +Z (Head is +Z, Feet is -Z, Anterior is -Y, Posterior is +Y)
                double clampedPitch = Math.Clamp(radPitch, -89.0 * Math.PI / 180.0, 89.0 * Math.PI / 180.0);
                double cosPitch = Math.Cos(clampedPitch);
                double sinPitch = Math.Sin(clampedPitch);
                double cosYaw = Math.Cos(radYaw);
                double sinYaw = Math.Sin(radYaw);

                (double x, double y, double z) orbitDir = (
                    sinYaw * cosPitch,
                    -cosYaw * cosPitch,
                    sinPitch
                );

                (double x, double y, double z) camRightBase = (cosYaw, sinYaw, 0.0);
                (double x, double y, double z) upVecBase = (
                    -sinYaw * sinPitch,
                    cosYaw * sinPitch,
                    cosPitch
                );

                double cosRoll = Math.Cos(radRoll);
                double sinRoll = Math.Sin(radRoll);

                (double x, double y, double z) u = (
                    camRightBase.x * cosRoll - upVecBase.x * sinRoll,
                    camRightBase.y * cosRoll - upVecBase.y * sinRoll,
                    camRightBase.z * cosRoll - upVecBase.z * sinRoll
                );

                (double x, double y, double z) up = (
                    upVecBase.x * cosRoll + camRightBase.x * sinRoll,
                    upVecBase.y * cosRoll + camRightBase.y * sinRoll,
                    upVecBase.z * cosRoll + camRightBase.z * sinRoll
                );

                // V is Down on screen
                (double x, double y, double z) v = (-up.x, -up.y, -up.z);
                // W is ray direction into screen (from camera towards volume center)
                (double x, double y, double z) w = (-orbitDir.x, -orbitDir.y, -orbitDir.z);

                // Headlight / key light vector in camera space relative to view direction:
                // Slightly from top-left front of screen: -0.3*U - 0.4*V + 0.86*W
                double lx = -0.3 * u.x - 0.4 * v.x + 0.86 * w.x;
                double ly = -0.3 * u.y - 0.4 * v.y + 0.86 * w.y;
                double lz = -0.3 * u.z - 0.4 * v.z + 0.86 * w.z;
                double lLen = Math.Sqrt(lx * lx + ly * ly + lz * lz);
                lx /= lLen; ly /= lLen; lz /= lLen;

                // Halfway vector H for specular Blinn-Phong highlight with view direction V = -W
                double vxEye = -w.x, vyEye = -w.y, vzEye = -w.z;
                double hx = lx + vxEye, hy = ly + vyEye, hz = lz + vzEye;
                double hLen = Math.Sqrt(hx * hx + hy * hy + hz * hz);
                if (hLen > 1e-4) { hx /= hLen; hy /= hLen; hz /= hLen; }

                // Rescale slope and intercept from reference slice
                double slope = vol.ReferenceSlice?.RescaleSlope ?? 1.0;
                double intercept = vol.ReferenceSlice?.RescaleIntercept ?? 0.0;

                bool isSigned = vol.IsSigned;
                var signedSlices = vol.SignedSlices;
                var unsignedSlices = vol.UnsignedSlices;
                bool reverseZ = vol.StepZ < 0;

                // Step size (mm): maintain tight sampling to prevent topographic woodgrain bands
                double stepSize = p.IsFastMode ? 1.1 : 0.85;
                double invSpX = 1.0 / spX;
                double invSpY = 1.0 / spY;
                double invThick = 1.0 / thick;

                double scaleFactor = (maxSize * 1.1) / (p.Zoom * Math.Min(width, height));

                // Multi-threaded raycasting per scanline
                Parallel.For(0, height, y =>
                {
                    byte* rowPtr = backBuffer + y * stride;
                    double pyNorm = (y - height * 0.5) * scaleFactor - p.PanY;

                    for (int x = 0; x < width; x++)
                    {
                        double pxNorm = (x - width * 0.5) * scaleFactor - p.PanX;

                        // Ray Origin: O = px * U + py * V - (maxSize * 1.5) * W
                        double ox = pxNorm * u.x + pyNorm * v.x - (maxSize * 1.5) * w.x;
                        double oy = pxNorm * u.y + pyNorm * v.y - (maxSize * 1.5) * w.y;
                        double oz = pxNorm * u.z + pyNorm * v.z - (maxSize * 1.5) * w.z;

                        // Ray Direction is W
                        double dx = w.x;
                        double dy = w.y;
                        double dz = w.z;

                        // Ray-Box Intersection
                        if (!IntersectBox(ox, oy, oz, dx, dy, dz, minX, maxX, minY, maxY, minZ, maxZ, out double tNear, out double tFar))
                        {
                            rowPtr[x * 4 + 0] = 0;
                            rowPtr[x * 4 + 1] = 0;
                            rowPtr[x * 4 + 2] = 0;
                            rowPtr[x * 4 + 3] = 255;
                            continue;
                        }

                        tNear = Math.Max(0, tNear);
                        if (tNear >= tFar)
                        {
                            rowPtr[x * 4 + 0] = 0;
                            rowPtr[x * 4 + 1] = 0;
                            rowPtr[x * 4 + 2] = 0;
                            rowPtr[x * 4 + 3] = 255;
                            continue;
                        }

                        if (p.Preset == VolumeRenderingPreset.MIP)
                        {
                            // Maximum Intensity Projection
                            double maxHu = -2000;
                            for (double t = tNear; t <= tFar; t += stepSize)
                            {
                                double rx = ox + t * dx;
                                double ry = oy + t * dy;
                                double rz = oz + t * dz;

                                int ix = (int)((rx - minX) * invSpX);
                                int iy = (int)((ry - minY) * invSpY);
                                int iz = (int)((rz - minZ) * invThick);
                                int sz = reverseZ ? (slices - 1 - iz) : iz;

                                if (ix >= 0 && ix < cols && iy >= 0 && iy < rows && sz >= 0 && sz < slices)
                                {
                                    double hu = isSigned
                                        ? signedSlices![sz][iy * cols + ix] * slope + intercept
                                        : unsignedSlices![sz][iy * cols + ix] * slope + intercept;
                                    if (hu > maxHu) maxHu = hu;
                                }
                            }

                            // Window/Level map
                            double low = p.WindowCenter - p.WindowWidth * 0.5;
                            double high = p.WindowCenter + p.WindowWidth * 0.5;
                            double val = Math.Clamp((maxHu - low) / Math.Max(1.0, high - low), 0.0, 1.0);
                            byte b = (byte)(val * 255.0);

                            rowPtr[x * 4 + 0] = b;
                            rowPtr[x * 4 + 1] = b;
                            rowPtr[x * 4 + 2] = b;
                            rowPtr[x * 4 + 3] = 255;
                        }
                        else
                        {
                            // Full Color Volume Rendering with Lighting & Z-Interpolation
                            double accR = 0, accG = 0, accB = 0, accA = 0;

                            for (double t = tNear; t <= tFar; t += stepSize)
                            {
                                double rx = ox + t * dx;
                                double ry = oy + t * dy;
                                double rz = oz + t * dz;

                                int ix = (int)((rx - minX) * invSpX);
                                int iy = (int)((ry - minY) * invSpY);
                                double fz = (rz - minZ) * invThick;
                                int iz = (int)fz;

                                if (ix < 1 || ix >= cols - 1 || iy < 1 || iy >= rows - 1 || iz < 0 || iz >= slices - 1)
                                    continue;

                                // Linear interpolation along Z slice axis to completely eliminate staircase banding
                                double tz = fz - iz;
                                int sz0 = reverseZ ? (slices - 1 - iz) : iz;
                                int sz1 = reverseZ ? Math.Max(0, slices - 2 - iz) : Math.Min(slices - 1, iz + 1);
                                double hu0 = isSigned
                                    ? signedSlices![sz0][iy * cols + ix] * slope + intercept
                                    : unsignedSlices![sz0][iy * cols + ix] * slope + intercept;
                                double hu1 = isSigned
                                    ? signedSlices![sz1][iy * cols + ix] * slope + intercept
                                    : unsignedSlices![sz1][iy * cols + ix] * slope + intercept;
                                double hu = hu0 + tz * (hu1 - hu0);

                                // Dynamic Transfer function mapped to Window Center & Width
                                SampleTransferFunction(hu, p.Preset, p.WindowCenter, p.WindowWidth, out double r, out double g, out double b, out double alpha);
                                if (alpha <= 0.005) continue;

                                // Adjust alpha by step size
                                alpha = 1.0 - Math.Pow(1.0 - alpha, stepSize / 1.0);

                                // Compute gradient vector: ∇HU = (gx, gy, gz)
                                int izSafe = Math.Clamp(iz, 1, slices - 2);
                                double gx = isSigned
                                    ? (signedSlices![izSafe][iy * cols + (ix + 1)] - signedSlices![izSafe][iy * cols + (ix - 1)]) * slope
                                    : (unsignedSlices![izSafe][iy * cols + (ix + 1)] - unsignedSlices![izSafe][iy * cols + (ix - 1)]) * slope;
                                double gy = isSigned
                                    ? (signedSlices![izSafe][(iy + 1) * cols + ix] - signedSlices![izSafe][(iy - 1) * cols + ix]) * slope
                                    : (unsignedSlices![izSafe][(iy + 1) * cols + ix] - unsignedSlices![izSafe][(iy - 1) * cols + ix]) * slope;
                                double gz = isSigned
                                    ? (signedSlices![izSafe + 1][iy * cols + ix] - signedSlices![izSafe - 1][iy * cols + ix]) * slope
                                    : (unsignedSlices![izSafe + 1][iy * cols + ix] - unsignedSlices![izSafe - 1][iy * cols + ix]) * slope;

                                double gLen = Math.Sqrt(gx * gx + gy * gy + gz * gz);
                                double diffuse = 0.85;
                                double specular = 0.0;

                                if (gLen > 10.0)
                                {
                                    // Outward surface normal is opposite to gradient (points toward lower HU / air)
                                    double nx = -gx / gLen;
                                    double ny = -gy / gLen;
                                    double nz = -gz / gLen;

                                    double nDotL = Math.Max(0.0, nx * lx + ny * ly + nz * lz);
                                    diffuse = 0.28 + 0.72 * nDotL;

                                    double nDotH = Math.Max(0.0, nx * hx + ny * hy + nz * hz);
                                    specular = Math.Pow(nDotH, 16.0) * 0.40;
                                }

                                r = Math.Min(1.0, r * diffuse + specular);
                                g = Math.Min(1.0, g * diffuse + specular);
                                b = Math.Min(1.0, b * diffuse + specular);

                                // Front-to-back compositing
                                double weight = (1.0 - accA) * alpha;
                                accR += weight * r;
                                accG += weight * g;
                                accB += weight * b;
                                accA += weight;

                                if (accA >= 0.96) break; // Early ray termination
                            }

                            rowPtr[x * 4 + 0] = (byte)Math.Clamp(accB * 255.0, 0, 255);
                            rowPtr[x * 4 + 1] = (byte)Math.Clamp(accG * 255.0, 0, 255);
                            rowPtr[x * 4 + 2] = (byte)Math.Clamp(accR * 255.0, 0, 255);
                            rowPtr[x * 4 + 3] = 255;
                        }
                    }
                });
            }

            bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, width, height));
        }
        finally
        {
            bitmap.Unlock();
        }

        return bitmap;
    }

    private static void SampleTransferFunction(double hu, VolumeRenderingPreset preset, double wc, double ww, out double r, out double g, out double b, out double alpha)
    {
        r = 0; g = 0; b = 0; alpha = 0;

        // Dynamic thresholding based on Window Center (wc) and Window Width (ww)
        double minHU = wc - ww * 0.5;
        double maxHU = wc + ww * 0.5;

        // Anything below the window floor is completely invisible / clipped
        if (hu < minHU) return;

        double u = Math.Clamp((hu - minHU) / Math.Max(1.0, ww), 0.0, 1.0);

        switch (preset)
        {
            case VolumeRenderingPreset.Bones:
                // RadiAnt 3D Bones: Warm golden trabecular bone ascending into dense ivory cortical bone
                // Opacity curve: ramps smoothly from 0 at window floor to 0.95 at peak
                alpha = Math.Pow(u, 1.4) * 0.95;
                if (u < 0.35)
                {
                    // Spongy bone / soft tissue boundary (warm caramel/tan)
                    double t = u / 0.35;
                    r = 0.86 + t * 0.08;
                    g = 0.62 + t * 0.16;
                    b = 0.40 + t * 0.18;
                }
                else if (u < 0.75)
                {
                    // Dense bone (rich warm ivory gold)
                    double t = (u - 0.35) / 0.40;
                    r = 0.94 + t * 0.05;
                    g = 0.78 + t * 0.14;
                    b = 0.58 + t * 0.22;
                }
                else
                {
                    // Cortical bone (pure ivory/pearl highlight)
                    double t = (u - 0.75) / 0.25;
                    r = 0.99 + t * 0.01;
                    g = 0.92 + t * 0.06;
                    b = 0.80 + t * 0.15;
                }
                break;

            case VolumeRenderingPreset.Angio:
                // RadiAnt 3D Angio (Matching user reference photo):
                // Lower range: deep crimson/red vessel walls and parenchyma
                // Mid range: high-intensity arterial blood / contrast (glowing red-orange to amber-gold)
                // High range: warm ivory skeleton
                alpha = Math.Pow(u, 1.2) * 0.95;
                if (u < 0.20)
                {
                    // Soft vascular tissue / microvessels (dark crimson mahogany)
                    double t = u / 0.20;
                    r = 0.65 + t * 0.25;
                    g = 0.08 + t * 0.08;
                    b = 0.06 + t * 0.04;
                }
                else if (u < 0.55)
                {
                    // High-contrast arterial lumen (aorta, renal/iliac arteries: glowing blood red into amber gold)
                    double t = (u - 0.20) / 0.35;
                    r = 0.90 + t * 0.08;
                    g = 0.16 + t * 0.52;
                    b = 0.10 + t * 0.05;
                }
                else
                {
                    // Dense skeleton supporting structures (warm ivory bone)
                    double t = (u - 0.55) / 0.45;
                    r = 0.98;
                    g = 0.72 + t * 0.20;
                    b = 0.25 + t * 0.55;
                }
                break;

            case VolumeRenderingPreset.Skin:
                // Natural photorealistic skin tone & surface scars
                alpha = Math.Pow(u, 1.3) * 0.90;
                if (u < 0.40)
                {
                    double t = u / 0.40;
                    r = 0.85 + t * 0.07;
                    g = 0.60 + t * 0.12;
                    b = 0.48 + t * 0.10;
                }
                else
                {
                    double t = (u - 0.40) / 0.60;
                    r = 0.92 + t * 0.06;
                    g = 0.72 + t * 0.16;
                    b = 0.58 + t * 0.22;
                }
                break;

            case VolumeRenderingPreset.Airways:
                // Only render the negative lung window: minHU to minHU + ww (e.g. -950 HU to -350 HU)
                // Anything denser than -350 HU (ribs, chest wall, heart, spine) is completely transparent!
                if (hu > minHU + ww)
                {
                    alpha = 0.0;
                    return;
                }
                double bell = Math.Sin(u * Math.PI);
                alpha = Math.Pow(bell, 1.2) * 0.28;
                r = 0.04 + 0.06 * bell;
                g = 0.60 + 0.36 * bell;
                b = 0.70 + 0.26 * bell;
                break;
        }
    }

    private static bool IntersectBox(double ox, double oy, double oz, double dx, double dy, double dz,
                                     double minX, double maxX, double minY, double maxY, double minZ, double maxZ,
                                     out double tNear, out double tFar)
    {
        tNear = double.MinValue;
        tFar = double.MaxValue;

        // X slab
        if (Math.Abs(dx) < 1e-6)
        {
            if (ox < minX || ox > maxX) return false;
        }
        else
        {
            double t1 = (minX - ox) / dx;
            double t2 = (maxX - ox) / dx;
            if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
            if (t1 > tNear) tNear = t1;
            if (t2 < tFar) tFar = t2;
            if (tNear > tFar || tFar < 0) return false;
        }

        // Y slab
        if (Math.Abs(dy) < 1e-6)
        {
            if (oy < minY || oy > maxY) return false;
        }
        else
        {
            double t1 = (minY - oy) / dy;
            double t2 = (maxY - oy) / dy;
            if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
            if (t1 > tNear) tNear = t1;
            if (t2 < tFar) tFar = t2;
            if (tNear > tFar || tFar < 0) return false;
        }

        // Z slab
        if (Math.Abs(dz) < 1e-6)
        {
            if (oz < minZ || oz > maxZ) return false;
        }
        else
        {
            double t1 = (minZ - oz) / dz;
            double t2 = (maxZ - oz) / dz;
            if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
            if (t1 > tNear) tNear = t1;
            if (t2 < tFar) tFar = t2;
            if (tNear > tFar || tFar < 0) return false;
        }

        return true;
    }

    private static (double x, double y, double z) RotateX((double x, double y, double z) v, double rad)
    {
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        return (v.x, v.y * cos - v.z * sin, v.y * sin + v.z * cos);
    }

    private static (double x, double y, double z) RotateY((double x, double y, double z) v, double rad)
    {
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        return (v.x * cos + v.z * sin, v.y, -v.x * sin + v.z * cos);
    }

    private static (double x, double y, double z) RotateVector((double x, double y, double z) v, (double x, double y, double z) k, double rad)
    {
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double dot = v.x * k.x + v.y * k.y + v.z * k.z;
        double crossX = k.y * v.z - k.z * v.y;
        double crossY = k.z * v.x - k.x * v.z;
        double crossZ = k.x * v.y - k.y * v.x;

        return (
            v.x * cos + crossX * sin + k.x * dot * (1.0 - cos),
            v.y * cos + crossY * sin + k.y * dot * (1.0 - cos),
            v.z * cos + crossZ * sin + k.z * dot * (1.0 - cos)
        );
    }

    private static (double x, double y, double z) RotateZ((double x, double y, double z) v, double rad)
    {
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        return (v.x * cos - v.y * sin, v.x * sin + v.y * cos, v.z);
    }
}
