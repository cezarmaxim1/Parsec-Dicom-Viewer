// @purpose: Radiological calculator for adrenal mass characterization calculating Absolute and Relative Washout percentages from CT HU attenuation values.
// @depends: OpenRadViewer.ViewModels, OpenRadViewer.Core.Models
// @exports: AdrenalWashoutWindow

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenRadViewer.Core.Config;
using OpenRadViewer.Core.Models;
using OpenRadViewer.ViewModels;

namespace OpenRadViewer.Views;

public partial class AdrenalWashoutWindow : Window
{
    private enum WashoutTarget
    {
        Pre,
        Portal,
        Delayed
    }

    private readonly MainViewModel? _mainVm;
    private WashoutTarget _currentTarget = WashoutTarget.Pre;

    private readonly SolidColorBrush _activeBorderBrush = new(Color.FromRgb(0, 229, 255));
    private readonly SolidColorBrush _defaultBorderBrush = new(Color.FromRgb(51, 51, 51));
    private readonly SolidColorBrush _adenomaGreenBrush = new(Color.FromRgb(27, 94, 32));
    private readonly SolidColorBrush _nonAdenomaRedBrush = new(Color.FromRgb(183, 28, 28));
    private readonly SolidColorBrush _neutralBgBrush = new(Color.FromRgb(34, 34, 34));

    public AdrenalWashoutWindow(MainViewModel? mainVm)
    {
        InitializeComponent();
        _mainVm = mainVm;

        if (_mainVm != null)
        {
            _mainVm.MeasurementSelected += OnMeasurementSelected;
        }

        Closed += (s, e) =>
        {
            if (_mainVm != null)
            {
                _mainVm.MeasurementSelected -= OnMeasurementSelected;
            }
        };

        UpdateTargetHighlight();
        CheckExistingActiveRoi();
    }

    private void CheckExistingActiveRoi()
    {
        if (_mainVm?.ActiveViewport == null) return;
        var selected = _mainVm.ActiveViewport.Measurements.FirstOrDefault(m => m.IsSelected && m.Type == MeasurementType.Ellipse);
        if (selected != null)
        {
            ApplyRoiToTarget(selected.MeanHu);
        }
    }

    private void OnMeasurementSelected(MeasurementItem m)
    {
        // Adrenal washout requires circular/elliptical ROI, never rectangle/square
        if (m.Type == MeasurementType.Ellipse)
        {
            Dispatcher.Invoke(() =>
            {
                ApplyRoiToTarget(m.MeanHu);
            });
        }
    }

    private void ActivateEllipseRoiTool()
    {
        if (_mainVm != null)
        {
            _mainVm.AssignLeftTool(MouseToolAction.MeasureEllipse);
            string phaseName = _currentTarget switch
            {
                WashoutTarget.Pre => "1. Pre-contrast (Native)",
                WashoutTarget.Portal => "2. Portal Venous",
                WashoutTarget.Delayed => "3. Delayed (15 min)",
                _ => "lesion"
            };
            _mainVm.StatusMessage = $"Tool: Circle/Ellipse ROI (E). Draw circular ROI on {phaseName} to sample Mean HU.";
        }
    }

    private void ApplyRoiToTarget(double hu)
    {
        string valStr = hu.ToString("0.0", CultureInfo.InvariantCulture);

        switch (_currentTarget)
        {
            case WashoutTarget.Pre:
                TxtPre.Text = valStr;
                _currentTarget = WashoutTarget.Portal;
                break;
            case WashoutTarget.Portal:
                TxtPortal.Text = valStr;
                _currentTarget = WashoutTarget.Delayed;
                break;
            case WashoutTarget.Delayed:
                TxtDelayed.Text = valStr;
                break;
        }

        UpdateTargetHighlight();
        Recalculate();
        ActivateEllipseRoiTool();
    }

    private void UpdateTargetHighlight()
    {
        BorderPre.BorderBrush = _currentTarget == WashoutTarget.Pre ? _activeBorderBrush : _defaultBorderBrush;
        BorderPortal.BorderBrush = _currentTarget == WashoutTarget.Portal ? _activeBorderBrush : _defaultBorderBrush;
        BorderDelayed.BorderBrush = _currentTarget == WashoutTarget.Delayed ? _activeBorderBrush : _defaultBorderBrush;

        TxtTargetIndicator.Text = _currentTarget switch
        {
            WashoutTarget.Pre => "Active target: 1. Native",
            WashoutTarget.Portal => "Active target: 2. Portal",
            WashoutTarget.Delayed => "Active target: 3. Delayed",
            _ => ""
        };
    }

    private void OnInputGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender == TxtPre) _currentTarget = WashoutTarget.Pre;
        else if (sender == TxtPortal) _currentTarget = WashoutTarget.Portal;
        else if (sender == TxtDelayed) _currentTarget = WashoutTarget.Delayed;
        UpdateTargetHighlight();
        ActivateEllipseRoiTool();
    }

    private void OnInputValueChanged(object sender, TextChangedEventArgs e)
    {
        Recalculate();
    }

    private void OnPickPreClicked(object sender, RoutedEventArgs e)
    {
        _currentTarget = WashoutTarget.Pre;
        UpdateTargetHighlight();
        TryPickCurrentActiveRoi();
    }

    private void OnPickPortalClicked(object sender, RoutedEventArgs e)
    {
        _currentTarget = WashoutTarget.Portal;
        UpdateTargetHighlight();
        TryPickCurrentActiveRoi();
    }

    private void OnPickDelayedClicked(object sender, RoutedEventArgs e)
    {
        _currentTarget = WashoutTarget.Delayed;
        UpdateTargetHighlight();
        TryPickCurrentActiveRoi();
    }

    private void TryPickCurrentActiveRoi()
    {
        ActivateEllipseRoiTool();

        if (_mainVm?.ActiveViewport == null) return;

        // 1. Check if an Ellipse ROI is already selected
        var selected = _mainVm.ActiveViewport.Measurements.FirstOrDefault(m => m.IsSelected && m.Type == MeasurementType.Ellipse);
        if (selected != null)
        {
            ApplyRoiToTarget(selected.MeanHu);
            return;
        }

        // 2. Check if an Ellipse ROI exists on current slice
        var onSlice = _mainVm.ActiveViewport.Measurements.LastOrDefault(m => m.Type == MeasurementType.Ellipse &&
            (m.SliceIndex == _mainVm.ActiveViewport.CurrentSliceIndex || m.SliceIndex == -1));
        if (onSlice != null)
        {
            foreach (var m in _mainVm.ActiveViewport.Measurements) m.IsSelected = false;
            onSlice.IsSelected = true;
            ApplyRoiToTarget(onSlice.MeanHu);
        }
    }

    private void OnAutoDetectClicked(object sender, RoutedEventArgs e)
    {
        if (_mainVm == null) return;

        bool foundAny = false;
        foreach (var vp in _mainVm.Viewports)
        {
            if (vp.CurrentSeries == null) continue;
            string desc = (vp.CurrentSeries.SeriesDescription ?? "").ToUpperInvariant();
            var roi = vp.Measurements.FirstOrDefault(m => m.Type == MeasurementType.Ellipse);
            if (roi == null) continue;

            if (desc.Contains("NATIV") || desc.Contains("PRE") || desc.Contains("PLAIN") || desc.Contains("C-"))
            {
                TxtPre.Text = roi.MeanHu.ToString("0.0", CultureInfo.InvariantCulture);
                foundAny = true;
            }
            else if (desc.Contains("PORTAL") || desc.Contains("VEN") || desc.Contains("PV"))
            {
                TxtPortal.Text = roi.MeanHu.ToString("0.0", CultureInfo.InvariantCulture);
                foundAny = true;
            }
            else if (desc.Contains("DELAY") || desc.Contains("TARD") || desc.Contains("15 MIN") || desc.Contains("4 MIN"))
            {
                TxtDelayed.Text = roi.MeanHu.ToString("0.0", CultureInfo.InvariantCulture);
                foundAny = true;
            }
        }

        if (foundAny)
        {
            Recalculate();
        }
        else
        {
            MessageBox.Show(this, "No circular (ellipse) ROIs found on open series (Native, Portal, Delayed).\nDraw a circular/ellipse ROI on the adrenal lesion in each phase.", "Auto-detect", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        TxtPre.Text = string.Empty;
        TxtPortal.Text = string.Empty;
        TxtDelayed.Text = string.Empty;
        _currentTarget = WashoutTarget.Pre;
        UpdateTargetHighlight();
        Recalculate();
    }

    private void Recalculate()
    {
        bool hasPre = double.TryParse(TxtPre.Text.Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double pre);
        bool hasPortal = double.TryParse(TxtPortal.Text.Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double portal);
        bool hasDelayed = double.TryParse(TxtDelayed.Text.Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double delayed);

        double? apw = null;
        double? rpw = null;

        // 1. Absolute Washout: [(HU portal - HU delayed) / (HU portal - HU pre)] * 100
        if (hasPre && hasPortal && hasDelayed)
        {
            double denom = portal - pre;
            if (Math.Abs(denom) > 0.001)
            {
                apw = ((portal - delayed) / denom) * 100.0;
            }
        }

        // 2. Relative Washout: [(HU portal - HU delayed) / HU portal] * 100
        if (hasPortal && hasDelayed)
        {
            if (Math.Abs(portal) > 0.001)
            {
                rpw = ((portal - delayed) / portal) * 100.0;
            }
        }

        // Display APW
        if (apw.HasValue)
        {
            TxtApwValue.Text = $"{apw.Value:0.0} %";
            TxtApwValue.Foreground = apw.Value >= 60.0 ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Color.FromRgb(245, 175, 40));
            TxtApwThreshold.Text = apw.Value >= 60.0 ? "≥ 60% → Criteria met for adenoma" : "< 60% → Below adenoma threshold";
        }
        else
        {
            TxtApwValue.Text = "-- %";
            TxtApwValue.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
            TxtApwThreshold.Text = "Threshold: ≥ 60% indicates adenoma (requires Native, Portal, Delayed)";
        }

        // Display RPW
        if (rpw.HasValue)
        {
            TxtRpwValue.Text = $"{rpw.Value:0.0} %";
            TxtRpwValue.Foreground = rpw.Value >= 40.0 ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Color.FromRgb(245, 175, 40));
            TxtRpwThreshold.Text = rpw.Value >= 40.0 ? "≥ 40% → Criteria met for adenoma" : "< 40% → Below adenoma threshold";
        }
        else
        {
            TxtRpwValue.Text = "-- %";
            TxtRpwValue.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
            TxtRpwThreshold.Text = "Threshold: ≥ 40% indicates adenoma (requires Portal and Delayed)";
        }

        // Update Conclusion
        if (hasPre && pre <= 10.0)
        {
            BorderConclusion.Background = _adenomaGreenBrush;
            BorderConclusion.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            TxtConclusionTitle.Text = "✓ LIPID-RICH ADENOMA (Native ≤ 10 HU)";
            TxtConclusionTitle.Foreground = Brushes.White;
            string apwText = apw.HasValue ? $" APW = {apw.Value:0.0}%." : "";
            TxtConclusionBody.Text = $"Baseline unenhanced attenuation ({pre:0.1} HU) is ≤ 10 HU, diagnostic for a lipid-rich adrenal adenoma.{apwText}";
            TxtConclusionBody.Foreground = new SolidColorBrush(Color.FromRgb(230, 255, 230));
        }
        else if ((apw.HasValue && apw.Value >= 60.0) || (rpw.HasValue && rpw.Value >= 40.0))
        {
            BorderConclusion.Background = _adenomaGreenBrush;
            BorderConclusion.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            TxtConclusionTitle.Text = "✓ ADRENAL ADENOMA (Positive Washout)";
            TxtConclusionTitle.Foreground = Brushes.White;

            string details = "";
            if (apw.HasValue && apw.Value >= 60.0 && rpw.HasValue && rpw.Value >= 40.0)
                details = $"APW = {apw.Value:0.0}% (≥ 60%) and RPW = {rpw.Value:0.0}% (≥ 40%).";
            else if (apw.HasValue && apw.Value >= 60.0)
                details = $"APW = {apw.Value:0.0}% (≥ 60%).";
            else if (rpw.HasValue && rpw.Value >= 40.0)
                details = $"RPW = {rpw.Value:0.0}% (≥ 40%).";

            TxtConclusionBody.Text = $"High washout suggestive of adrenal adenoma ({details}). Rapid 15-minute contrast clearance characterizes benign adenomatous lesions.";
            TxtConclusionBody.Foreground = new SolidColorBrush(Color.FromRgb(230, 255, 230));
        }
        else if (apw.HasValue || rpw.HasValue)
        {
            BorderConclusion.Background = _nonAdenomaRedBrush;
            BorderConclusion.BorderBrush = new SolidColorBrush(Color.FromRgb(229, 57, 53));
            TxtConclusionTitle.Text = "⚠ INDETERMINATE / NON-ADENOMA (Low Washout)";
            TxtConclusionTitle.Foreground = Brushes.White;

            string details = "";
            if (apw.HasValue) details += $"APW: {apw.Value:0.0}% (< 60%). ";
            if (rpw.HasValue) details += $"RPW: {rpw.Value:0.0}% (< 40%). ";

            TxtConclusionBody.Text = $"Insufficient washout ({details.Trim()}). Lesion does not meet typical adenoma criteria. Requires clinical/oncologic correlation (pheochromocytoma, adrenal metastasis, adrenocortical carcinoma).";
            TxtConclusionBody.Foreground = new SolidColorBrush(Color.FromRgb(255, 235, 235));
        }
        else
        {
            BorderConclusion.Background = _neutralBgBrush;
            BorderConclusion.BorderBrush = _defaultBorderBrush;
            TxtConclusionTitle.Text = "Enter HU values";
            TxtConclusionTitle.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
            TxtConclusionBody.Text = "Awaiting CT phases to compute adrenal contrast washout.";
            TxtConclusionBody.Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200));
        }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        string preStr = string.IsNullOrWhiteSpace(TxtPre.Text) ? "N/A" : $"{TxtPre.Text.Trim()} HU";
        string portStr = string.IsNullOrWhiteSpace(TxtPortal.Text) ? "N/A" : $"{TxtPortal.Text.Trim()} HU";
        string delStr = string.IsNullOrWhiteSpace(TxtDelayed.Text) ? "N/A" : $"{TxtDelayed.Text.Trim()} HU";

        string report = $"Adrenal CT Washout:\n" +
                        $"• Pre-contrast (Native): {preStr}\n" +
                        $"• Portal Venous (Post-contrast): {portStr}\n" +
                        $"• Delayed 15-min: {delStr}\n" +
                        $"• Absolute Percentage Washout (APW): {TxtApwValue.Text}\n" +
                        $"• Relative Percentage Washout (RPW): {TxtRpwValue.Text}\n" +
                        $"• Impression: {TxtConclusionTitle.Text}\n" +
                        $"  {TxtConclusionBody.Text}";

        Clipboard.SetText(report);
        if (_mainVm != null)
        {
            _mainVm.StatusMessage = "Adrenal Washout report copied to clipboard.";
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
