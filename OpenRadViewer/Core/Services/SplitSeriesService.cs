// @purpose: Auto-splits mixed DICOM series containing multiple sequences (DWI b-values, multi-echo TE, dynamic phases, or mixed spatial orientations).
// @depends: OpenRadViewer.Core.Models, OpenRadViewer.Core.Services (PixelRenderer)
// @exports: SplitSeriesService, SplitCriterion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRadViewer.Core.Models;

namespace OpenRadViewer.Core.Services;

public enum SplitCriterion
{
    Auto,
    DiffusionBValue,
    EchoTime,
    TemporalPhase,
    SpatialOrientation
}

public static class SplitSeriesService
{
    public static bool CanSplit(SeriesModel series)
    {
        if (series == null || series.Slices.Count < 2) return false;

        // Check b-values
        var bValues = series.Slices
            .Where(s => s.DiffusionBValue.HasValue)
            .Select(s => Math.Round(s.DiffusionBValue!.Value))
            .Distinct()
            .ToList();
        if (bValues.Count > 1) return true;

        // Check spatial orientations (Localizer / mixed planes)
        var orientations = series.Slices
            .Select(s => MprService.DetectOrientation(s).ToString())
            .Distinct()
            .ToList();
        if (orientations.Count > 1) return true;

        // Check Echo Times (TE) with duplicate slice positions
        var teValues = series.Slices
            .Where(s => s.EchoTime > 0)
            .Select(s => Math.Round(s.EchoTime, 1))
            .Distinct()
            .ToList();
        if (teValues.Count > 1 && HasDuplicatePositions(series.Slices)) return true;

        // Check duplicate positions (e.g. multi-pass / multiphase)
        if (HasDuplicatePositions(series.Slices)) return true;

        return false;
    }

    public static SplitCriterion DetectBestSplitCriterion(SeriesModel series)
    {
        if (series == null || series.Slices.Count < 2) return SplitCriterion.Auto;

        var bValues = series.Slices
            .Where(s => s.DiffusionBValue.HasValue)
            .Select(s => Math.Round(s.DiffusionBValue!.Value))
            .Distinct()
            .ToList();
        if (bValues.Count > 1) return SplitCriterion.DiffusionBValue;

        var orientations = series.Slices
            .Select(s => MprService.DetectOrientation(s).ToString())
            .Distinct()
            .ToList();
        if (orientations.Count > 1) return SplitCriterion.SpatialOrientation;

        var teValues = series.Slices
            .Where(s => s.EchoTime > 0)
            .Select(s => Math.Round(s.EchoTime, 1))
            .Distinct()
            .ToList();
        if (teValues.Count > 1 && HasDuplicatePositions(series.Slices)) return SplitCriterion.EchoTime;

        if (HasDuplicatePositions(series.Slices)) return SplitCriterion.TemporalPhase;

        return SplitCriterion.Auto;
    }

    public static List<SeriesModel> SplitSeries(SeriesModel series, SplitCriterion criterion = SplitCriterion.Auto)
    {
        if (series == null || series.Slices.Count < 2) return new List<SeriesModel> { series! };

        if (criterion == SplitCriterion.Auto)
        {
            criterion = DetectBestSplitCriterion(series);
        }

        var groups = new List<(string Label, List<DicomSliceModel> Slices)>();

        switch (criterion)
        {
            case SplitCriterion.DiffusionBValue:
                var bGroups = series.Slices
                    .GroupBy(s => s.DiffusionBValue.HasValue ? (int)Math.Round(s.DiffusionBValue.Value) : -1)
                    .OrderBy(g => g.Key);
                foreach (var g in bGroups)
                {
                    string label = g.Key >= 0 ? $"b={g.Key}" : "b=Unknown";
                    groups.Add((label, g.ToList()));
                }
                break;

            case SplitCriterion.SpatialOrientation:
                var oGroups = series.Slices
                    .GroupBy(s => MprService.DetectOrientation(s).ToString())
                    .OrderBy(g => g.Key);
                foreach (var g in oGroups)
                {
                    groups.Add((g.Key, g.ToList()));
                }
                break;

            case SplitCriterion.EchoTime:
                var teGroups = series.Slices
                    .GroupBy(s => Math.Round(s.EchoTime, 1))
                    .OrderBy(g => g.Key);
                foreach (var g in teGroups)
                {
                    groups.Add(($"TE {g.Key}ms", g.ToList()));
                }
                break;

            case SplitCriterion.TemporalPhase:
            default:
                // Check if TemporalPosition is populated
                var tempPositions = series.Slices.Select(s => s.TemporalPosition).Distinct().ToList();
                if (tempPositions.Count > 1)
                {
                    var tGroups = series.Slices.GroupBy(s => s.TemporalPosition).OrderBy(g => g.Key);
                    foreach (var g in tGroups)
                    {
                        groups.Add(($"Phase {g.Key}", g.ToList()));
                    }
                }
                else
                {
                    // Split duplicate slice position passes
                    groups = SplitDuplicateLocationPasses(series.Slices);
                }
                break;
        }

        if (groups.Count <= 1)
        {
            return new List<SeriesModel> { series };
        }

        var result = new List<SeriesModel>();
        int subIdx = 1;

        foreach (var (label, slices) in groups)
        {
            var subSeries = new SeriesModel
            {
                SeriesInstanceUid = $"{series.SeriesInstanceUid}_split_{subIdx}",
                StudyInstanceUid = series.StudyInstanceUid,
                StudyDescription = series.StudyDescription,
                Laterality = series.Laterality,
                SeriesNumber = series.SeriesNumber > 0 ? series.SeriesNumber * 100 + subIdx : subIdx,
                SeriesDescription = $"{series.SeriesDescription} [{label}]",
                Modality = series.Modality,
                BodyPart = series.BodyPart,
                PatientName = series.PatientName,
                PatientId = series.PatientId,
                StudyDate = series.StudyDate,
                SeriesTime = series.SeriesTime,
                Slices = slices
            };

            // Generate thumbnail
            if (subSeries.Slices.Count > 0)
            {
                try
                {
                    int midIdx = subSeries.Slices.Count / 2;
                    var midSlice = subSeries.Slices[midIdx];
                    System.Windows.Media.Imaging.WriteableBitmap? thumbBmp = null;
                    PixelRenderer.RenderToBitmap(midSlice, midSlice.DefaultWindowCenter, midSlice.DefaultWindowWidth, false, ref thumbBmp);
                    if (thumbBmp != null)
                    {
                        thumbBmp.Freeze();
                        subSeries.ThumbnailImage = thumbBmp;
                    }
                }
                catch { }
            }

            result.Add(subSeries);
            subIdx++;
        }

        return result;
    }

    private static bool HasDuplicatePositions(List<DicomSliceModel> slices)
    {
        var positions = slices
            .Where(s => s.ImagePositionPatient != null && s.ImagePositionPatient.Length >= 3)
            .Select(s => $"{Math.Round(s.ImagePositionPatient![0], 1)}_{Math.Round(s.ImagePositionPatient[1], 1)}_{Math.Round(s.ImagePositionPatient[2], 1)}")
            .ToList();

        return positions.Count > 0 && positions.Distinct().Count() < positions.Count;
    }

    private static List<(string Label, List<DicomSliceModel> Slices)> SplitDuplicateLocationPasses(List<DicomSliceModel> slices)
    {
        var result = new List<(string Label, List<DicomSliceModel> Slices)>();
        if (slices.Count == 0) return result;

        // Find how many unique positions exist
        var posKeys = slices
            .Select(s => s.ImagePositionPatient != null && s.ImagePositionPatient.Length >= 3
                ? $"{Math.Round(s.ImagePositionPatient[0], 1)}_{Math.Round(s.ImagePositionPatient[1], 1)}_{Math.Round(s.ImagePositionPatient[2], 1)}"
                : s.SliceLocation.ToString("0.0"))
            .ToList();

        int uniqueCount = posKeys.Distinct().Count();
        if (uniqueCount > 0 && slices.Count % uniqueCount == 0)
        {
            int numPasses = slices.Count / uniqueCount;
            for (int p = 0; p < numPasses; p++)
            {
                var passSlices = slices.Skip(p * uniqueCount).Take(uniqueCount).ToList();
                result.Add(($"Pass {p + 1}", passSlices));
            }
            return result;
        }

        // Generic fallback into 2 halves
        int half = slices.Count / 2;
        result.Add(("Part 1", slices.Take(half).ToList()));
        result.Add(("Part 2", slices.Skip(half).ToList()));
        return result;
    }
}
