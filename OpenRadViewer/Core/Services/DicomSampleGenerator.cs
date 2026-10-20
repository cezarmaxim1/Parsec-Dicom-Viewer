// @purpose: Generates synthetic volumetric DICOM MRI studies for automated unit testing, onboarding, and offline demo mode.
// @depends: FellowOakDicom, System.IO
// @exports: DicomSampleGenerator

using System.IO;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

namespace OpenRadViewer.Core.Services;

public static class DicomSampleGenerator
{
    public static string GenerateSyntheticStudy(string baseDir)
    {
        string sampleDir = Path.Combine(baseDir, "SampleData", "Spine_MRI_Study");
        Directory.CreateDirectory(sampleDir);

        string patientName = "DOE^JOHN";
        string patientId = "PAT-2026-9901";
        string studyUid = "1.2.826.0.1.3680043.8.498." + DateTime.UtcNow.Ticks;
        string seriesUid = "1.2.826.0.1.3680043.8.498." + (DateTime.UtcNow.Ticks + 1);

        int numSlices = 15;
        int rows = 256;
        int cols = 256;

        for (int sliceIdx = 0; sliceIdx < numSlices; sliceIdx++)
        {
            var dataset = new DicomDataset
            {
                { DicomTag.FileMetaInformationVersion, new byte[] { 0, 1 } },
                { DicomTag.MediaStorageSOPClassUID, DicomUID.MRImageStorage },
                { DicomTag.MediaStorageSOPInstanceUID, $"{seriesUid}.{sliceIdx + 1}" },
                { DicomTag.TransferSyntaxUID, DicomTransferSyntax.ExplicitVRLittleEndian },

                { DicomTag.PatientName, patientName },
                { DicomTag.PatientID, patientId },
                { DicomTag.PatientBirthDate, "19820322" },
                { DicomTag.PatientSex, "M" },

                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.StudyDate, DateTime.Now.ToString("yyyyMMdd") },
                { DicomTag.StudyTime, "113000" },
                { DicomTag.StudyDescription, "Spine Scout MRI Examination" },
                { DicomTag.InstitutionName, "SP CF IASI COLOANA" },

                { DicomTag.SeriesInstanceUID, seriesUid },
                { DicomTag.SeriesNumber, 15 },
                { DicomTag.SeriesDescription, "AAspine_scout_COMP_MPR_sag" },
                { DicomTag.Modality, "MR" },

                { DicomTag.SOPClassUID, DicomUID.MRImageStorage },
                { DicomTag.SOPInstanceUID, $"{seriesUid}.{sliceIdx + 1}" },
                { DicomTag.InstanceNumber, sliceIdx + 1 },

                { DicomTag.Rows, (ushort)rows },
                { DicomTag.Columns, (ushort)cols },
                { DicomTag.BitsAllocated, (ushort)16 },
                { DicomTag.BitsStored, (ushort)12 },
                { DicomTag.HighBit, (ushort)11 },
                { DicomTag.PixelRepresentation, (ushort)0 },
                { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
                { DicomTag.SamplesPerPixel, (ushort)1 },

                { DicomTag.WindowCenter, "189" },
                { DicomTag.WindowWidth, "455" },
                { DicomTag.RescaleSlope, "1" },
                { DicomTag.RescaleIntercept, "0" },

                { DicomTag.SliceThickness, "1.7" },
                { DicomTag.SliceLocation, $"{(-8.8 + sliceIdx * 2.0):0.0}" },
                { DicomTag.PixelSpacing, new[] { "1.0", "1.0" } },
                { DicomTag.ImagePositionPatient, new[] { "-20.0", "-120.0", $"{(-8.8 + sliceIdx * 2.0):0.0}" } },
                { DicomTag.ImageOrientationPatient, new[] { "0.0", "1.0", "0.0", "0.0", "0.0", "-1.0" } },

                { DicomTag.RepetitionTime, "3.3" },
                { DicomTag.EchoTime, "1.3" },
                { DicomTag.MagneticFieldStrength, "3.0" },
                { DicomTag.SequenceName, "t2_tse_sag" }
            };

            // Generate synthetic vertebral column / spinal canal phantom texture
            var pixelBytes = new byte[rows * cols * 2];
            double sliceZ = (sliceIdx - numSlices / 2.0) / 4.0;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    double dy = (r - rows / 2.0) / 30.0;
                    double dx = (c - cols / 2.0) / 15.0;

                    // Anatomical curved spine shape
                    double curve = Math.Sin(dy * 0.8) * 2.0;
                    double distSpine = Math.Sqrt((dx - curve) * (dx - curve) + sliceZ * sliceZ);

                    ushort val = 40; // background noise
                    if (distSpine < 2.5 && Math.Abs(dy) < 3.8)
                    {
                        // Vertebral bodies and intervertebral discs modulation
                        double discPattern = Math.Cos(dy * 6.28);
                        val = (ushort)(discPattern > 0.2 ? 380 : 160);
                    }
                    else if (distSpine < 4.0 && Math.Abs(dy) < 4.0)
                    {
                        // Surrounding soft tissue
                        val = (ushort)(110 + Math.Sin(dy * 2.0) * 30);
                    }

                    int idx = (r * cols + c) * 2;
                    pixelBytes[idx] = (byte)(val & 0xFF);
                    pixelBytes[idx + 1] = (byte)((val >> 8) & 0xFF);
                }
            }

            var pixelData = DicomPixelData.Create(dataset, true);
            pixelData.AddFrame(new MemoryByteBuffer(pixelBytes));

            var file = new DicomFile(dataset);
            string filePath = Path.Combine(sampleDir, $"IM_{sliceIdx + 1:D4}.dcm");
            file.Save(filePath);
        }

        return sampleDir;
    }
}
