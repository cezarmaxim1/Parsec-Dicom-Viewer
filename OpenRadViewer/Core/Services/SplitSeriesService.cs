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
    public static double? GetEffectiveBValue(DicomSliceModel s)
    {
        if (!string.IsNullOrWhiteSpace(s.SequenceName))
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                s.SequenceName,
                @"(?:^|[_\s\*])b[=_]?(\d{1,5})(?![0-9])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matches.Count == 1 && double.TryParse(matches[0].Groups[1].Value, out double seqB))
            {
                return seqB;
            }
        }
        return s.DiffusionBValue;
    }

    public static bool CanSplit(SeriesModel series)
    {
        if (series == null || series.Slices.Count < 2) return false;

        // Check b-values (including per-slice SequenceName b-value)
        var bValues = series.Slices
            .Select(GetEffectiveBValue)
            .Where(b => b.HasValue)
            .Select(b => Math.Round(b!.Value))
            .Distinct()
            .ToList();
        if (bValues.Count > 1) return true;

        // Check distinct per-slice SequenceNames with duplicate slice positions (e.g. *ep_b50d vs *ep_b800t)
        var seqNames = series.Slices
            .Select(s => s.SequenceName?.Trim() ?? "")
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (seqNames.Count > 1 && HasDuplicatePositions(series.Slices)) return true;

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
            .Select(GetEffectiveBValue)
            .Where(b => b.HasValue)
            .Select(b => Math.Round(b!.Value))
            .Distinct()
            .ToList();
        if (bValues.Count > 1) return SplitCriterion.DiffusionBValue;

        var seqNames = series.Slices
            .Select(s => s.SequenceName?.Trim() ?? "")
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (seqNames.Count > 1 && HasDuplicatePositions(series.Slices)) return SplitCriterion.DiffusionBValue;

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
                    .GroupBy(s =>
                    {
                        var b = GetEffectiveBValue(s);
                        return b.HasValue ? (int)Math.Round(b.Value) : -1;
                    })
                    .OrderBy(g => g.Key)
                    .ToList();

                if (bGroups.Count > 1)
                {
                    foreach (var g in bGroups)
                    {
                        string baseLabel = g.Key >= 0 ? $"b={g.Key}" : "b=Unknown";
                        var gSlices = g.ToList();

                        // If a single b-value group still contains multiple passes at the same Z location (e.g. directional vs trace),
                        // split those duplicate positions cleanly so each sub-series is a single 3D volume
                        if (HasDuplicatePositions(gSlices))
                        {
                            var subPasses = SplitDuplicateLocationPasses(gSlices);
                            if (subPasses.Count > 1)
                            {
                                for (int i = 0; i < subPasses.Count; i++)
                                {
                                    groups.Add(($"{baseLabel} ({i + 1})", subPasses[i].Slices));
                                }
                                continue;
                            }
                        }

                        DicomLoaderService.SortSlices(gSlices);
                        groups.Add((baseLabel, gSlices));
                    }
                }
                else
                {
                    // Try grouping by SequenceName if b-values were not explicitly numeric
                    var seqGroups = series.Slices
                        .GroupBy(s => s.SequenceName?.Trim() ?? "")
                        .Where(g => !string.IsNullOrEmpty(g.Key))
                        .OrderBy(g => g.Key)
                        .ToList();

                    if (seqGroups.Count > 1)
                    {
                        foreach (var sg in seqGroups)
                        {
                            var sgSlices = sg.ToList();
                            DicomLoaderService.SortSlices(sgSlices);
                            groups.Add((sg.Key, sgSlices));
                        }
                    }
                    else
                    {
                        groups = SplitDuplicateLocationPasses(series.Slices, series.SeriesDescription);
                    }
                }
                break;

            case SplitCriterion.SpatialOrientation:
                var oGroups = series.Slices
                    .GroupBy(s => MprService.DetectOrientation(s).ToString())
                    .OrderBy(g => g.Key);
                foreach (var g in oGroups)
                {
                    var oSlices = g.ToList();
                    DicomLoaderService.SortSlices(oSlices);
                    groups.Add((g.Key, oSlices));
                }
                break;

            case SplitCriterion.EchoTime:
                var teGroups = series.Slices
                    .GroupBy(s => Math.Round(s.EchoTime, 1))
                    .OrderBy(g => g.Key);
                foreach (var g in teGroups)
                {
                    var teSlices = g.ToList();
                    DicomLoaderService.SortSlices(teSlices);
                    groups.Add(($"TE {g.Key}ms", teSlices));
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
                        var tSlices = g.ToList();
                        DicomLoaderService.SortSlices(tSlices);
                        groups.Add(($"Phase {g.Key}", tSlices));
                    }
                }
                else
                {
                    // Split duplicate slice position passes properly across Z
                    groups = SplitDuplicateLocationPasses(series.Slices, series.SeriesDescription);
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
                PatientBirthDate = series.PatientBirthDate,
                PatientAge = series.PatientAge,
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
                    DicomLoaderService.LoadPixelData(midSlice);
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

    private static string GetPositionKey(DicomSliceModel s)
    {
        return s.ImagePositionPatient != null && s.ImagePositionPatient.Length >= 3
            ? $"{Math.Round(s.ImagePositionPatient[0], 1)}_{Math.Round(s.ImagePositionPatient[1], 1)}_{Math.Round(s.ImagePositionPatient[2], 1)}"
            : s.SliceLocation.ToString("0.0");
    }

    private static bool HasDuplicatePositions(List<DicomSliceModel> slices)
    {
        if (slices.Count < 2) return false;
        var posGroups = slices.GroupBy(GetPositionKey).ToList();
        return posGroups.Count >= 1 && posGroups.Any(g => g.Count() > 1);
    }

    private static List<(string Label, List<DicomSliceModel> Slices)> SplitDuplicateLocationPasses(List<DicomSliceModel> slices, string? seriesDesc = null)
    {
        var result = new List<(string Label, List<DicomSliceModel> Slices)>();
        if (slices.Count < 2) return result;

        // Group slices by their 3D anatomical position key, and within each anatomical position,
        // sort by InstanceNumber (then FrameIndex) so index p is always Pass p at that anatomical location.
        var posGroups = slices
            .GroupBy(GetPositionKey)
            .Select(g => g.OrderBy(s => s.InstanceNumber).ThenBy(s => s.FrameIndex).ToList())
            .ToList();

        int maxPasses = posGroups.Max(g => g.Count);
        if (maxPasses <= 1) return result;

        // Extract b-value hints from series description if present (e.g. "ep2d_diff_b50_b800" or "ep2d_diff_b50_800")
        var bHints = new List<string>();
        if (!string.IsNullOrWhiteSpace(seriesDesc))
        {
            var m = System.Text.RegularExpressions.Regex.Match(seriesDesc, @"b(\d{1,4})(?:[_\s]+b?(\d{1,4}))+(?![0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var nums = System.Text.RegularExpressions.Regex.Matches(m.Value, @"\d{1,4}");
                foreach (System.Text.RegularExpressions.Match nm in nums)
                {
                    bHints.Add($"b={nm.Value}");
                }
            }
        }

        for (int p = 0; p < maxPasses; p++)
        {
            var passSlices = new List<DicomSliceModel>();
            foreach (var locGroup in posGroups)
            {
                if (p < locGroup.Count)
                {
                    passSlices.Add(locGroup[p]);
                }
            }

            if (passSlices.Count > 0)
            {
                DicomLoaderService.SortSlices(passSlices);
                string label;
                if (bHints.Count == maxPasses)
                {
                    label = bHints[p];
                }
                else if (bHints.Count > 0 && maxPasses % bHints.Count == 0)
                {
                    int bIdx = p % bHints.Count;
                    int subPass = (p / bHints.Count) + 1;
                    label = $"{bHints[bIdx]} ({subPass})";
                }
                else
                {
                    label = $"Pass {p + 1}";
                }
                result.Add((label, passSlices));
            }
        }

        return result;
    }
}
