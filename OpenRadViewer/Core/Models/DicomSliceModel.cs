// @purpose: Core model representing a single 2D DICOM image slice / multi-frame instance with spatial coordinates, pixel buffers, and windowing.
// @depends: FellowOakDicom tags (via DicomLoaderService), System.Windows.Media.Imaging
// @exports: DicomSliceModel

namespace OpenRadViewer.Core.Models;

public class DicomSliceModel
{
    // AI-NOTE: SopInstanceUid is unique per slice. In projection radiography (CR/DX), SeriesInstanceUID can be shared while each slice is a distinct projection.
    public string FilePath { get; set; } = string.Empty;
    public string SopInstanceUid { get; set; } = string.Empty;
    public int InstanceNumber { get; set; } = 1;
    public int FrameIndex { get; set; } = 0;

    public int Rows { get; set; }
    public int Columns { get; set; }

    public double PixelSpacingX { get; set; } = 1.0;
    public double PixelSpacingY { get; set; } = 1.0;
    public double SliceThickness { get; set; } = 1.0;
    public double SliceLocation { get; set; }

    public double[]? ImagePositionPatient { get; set; }
    public double[]? ImageOrientationPatient { get; set; }

    public double DefaultWindowCenter { get; set; } = 0;
    public double DefaultWindowWidth { get; set; } = 0;

    public double RescaleSlope { get; set; } = 1.0;
    public double RescaleIntercept { get; set; } = 0.0;

    public int BitsAllocated { get; set; } = 16;
    public int BitsStored { get; set; } = 12;
    public int PixelRepresentation { get; set; } = 0; // 0 = unsigned, 1 = 2's complement
    public string PhotometricInterpretation { get; set; } = "MONOCHROME2";
    public string PresentationLutShape { get; set; } = string.Empty;

    // Acquisition parameters for OSD
    public double RepetitionTime { get; set; } // TR
    public double EchoTime { get; set; }       // TE
    public string MagneticFieldStrength { get; set; } = string.Empty; // FS (e.g. 1.5T, 3T)
    public string SequenceName { get; set; } = string.Empty;
    public string ImageComments { get; set; } = string.Empty; // e.g. "L1", "L1/L2", "L5/S1"
    public double? DiffusionBValue { get; set; }
    public string AcquisitionTime { get; set; } = string.Empty;
    public int TemporalPosition { get; set; }

    // Spatial orientation letters (Left, Right, Top, Bottom)
    public string OrientationTop { get; set; } = string.Empty;
    public string OrientationBottom { get; set; } = string.Empty;
    public string OrientationLeft { get; set; } = string.Empty;
    public string OrientationRight { get; set; } = string.Empty;

    // Cached raw pixel data (typically ushort[] or short[])
    public ushort[]? RawPixelsUnsigned { get; set; }
    public short[]? RawPixelsSigned { get; set; }
    public byte[]? RawRgbPixels { get; set; } // For RGB/Palette color scans

    public int MinPixelValue { get; set; }
    public int MaxPixelValue { get; set; }

    public void ComputeOrientationLabels()
    {
        if (ImageOrientationPatient == null || ImageOrientationPatient.Length < 6)
            return;

        // Row vector gives direction along columns (Left/Right of viewport)
        double[] rowVector = new[] { ImageOrientationPatient[0], ImageOrientationPatient[1], ImageOrientationPatient[2] };
        // Column vector gives direction down rows (Top/Bottom of viewport)
        double[] colVector = new[] { ImageOrientationPatient[3], ImageOrientationPatient[4], ImageOrientationPatient[5] };

        OrientationLeft = GetOrientationLetter(rowVector, negative: true);
        OrientationRight = GetOrientationLetter(rowVector, negative: false);
        OrientationTop = GetOrientationLetter(colVector, negative: true);
        OrientationBottom = GetOrientationLetter(colVector, negative: false);
    }

    private static string GetOrientationLetter(double[] vector, bool negative)
    {
        double factor = negative ? -1.0 : 1.0;
        double x = vector[0] * factor;
        double y = vector[1] * factor;
        double z = vector[2] * factor;

        string letterX = x < 0 ? "R" : "L";
        string letterY = y < 0 ? "A" : "P";
        string letterZ = z < 0 ? "I" : "S";

        double ax = Math.Abs(x);
        double ay = Math.Abs(y);
        double az = Math.Abs(z);

        if (ax >= ay && ax >= az) return letterX;
        if (ay >= ax && ay >= az) return letterY;
        return letterZ;
    }
}
