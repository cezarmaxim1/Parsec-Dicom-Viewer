// @purpose: Interactive AI consultation chat window displaying lesion crops across imaging phases, clinical prompts, and markdown responses.
// @depends: OpenRadViewer.Core.Services (AiService, DarkWindowHelper), System.Windows.Media.Imaging
// @exports: AiConsultationWindow, CropItemViewModel

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public class CropItemViewModel : INotifyPropertyChanged
{
    public AiImageCrop Crop { get; }

    public BitmapSource Image => Crop.Image;
    public string Modality => Crop.Modality;
    public string SeriesTitle => Crop.SeriesTitle;
    public string DetailsText => $"Slice #{Crop.SliceIndex + 1} | WL:{(int)Crop.WindowCenter} WW:{(int)Crop.WindowWidth}";

    public Visibility ArrowBadgeVisibility => Crop.HasArrowIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RoiBadgeVisibility => Crop.HasRoiIndicator ? Visibility.Visible : Visibility.Collapsed;

    public bool IsIncluded
    {
        get => Crop.IsIncluded;
        set
        {
            if (Crop.IsIncluded != value)
            {
                Crop.IsIncluded = value;
                OnPropertyChanged();
            }
        }
    }

    public CropItemViewModel(AiImageCrop crop)
    {
        Crop = crop;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class AiConsultationWindow : Window
{
    private readonly ObservableCollection<CropItemViewModel> _cropItems = new();
    private readonly List<AiConversationMessage> _chatHistory = new();
    private string _latestModelReply = string.Empty;

    public AiConsultationWindow(List<AiImageCrop> crops)
    {
        InitializeComponent();

        foreach (var crop in crops)
        {
            _cropItems.Add(new CropItemViewModel(crop));
        }

        ItemsCrops.ItemsSource = _cropItems;
        UpdateCropsCount();
        LoadHeaderModels();

        if (crops.Any(c => c.HasArrowIndicator || c.HasRoiIndicator))
        {
            BtnPresetArrowTarget.Visibility = Visibility.Visible;
            TxtPrompt.Text = "Analyze the anatomical findings and regions indicated by the yellow arrows / ROIs on the open series (e.g. lesion/tendon/herniation, morphology, signal, differential diagnosis and conclusion).";
        }
        else
        {
            TxtPrompt.Text = "Analyze the open slice(s) across series, characterizing any abnormal findings, lesions, and providing a differential diagnosis and report.";
        }
    }

    private void UpdateCropsCount()
    {
        int total = _cropItems.Count;
        int active = _cropItems.Count(c => c.IsIncluded);
        TxtCropsCount.Text = $"({active} of {total} selected)";
    }

    private bool _isUpdatingModelCombo;

    private async void LoadHeaderModels()
    {
        _isUpdatingModelCombo = true;
        try
        {
            var s = AiService.Instance.Settings;
            string curModel = string.IsNullOrWhiteSpace(s.Model) ? "gemini-3.6-flash" : s.Model;

            List<string> models;
            if (s.Provider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
            {
                models = new List<string> { "claude-3-5-sonnet-20241022", "claude-3-7-sonnet", "claude-3-5-haiku" };
            }
            else
            {
                models = new List<string> { "gemini-3.6-flash", "gemini-3.8-flash", "gemini-3.5-flash", "gemini-3.7-flash", "gemini-3.0-flash", "gemini-2.5-flash" };
            }

            if (!models.Contains(curModel))
            {
                models.Insert(0, curModel);
            }

            CmbHeaderModel.ItemsSource = models;
            CmbHeaderModel.SelectedItem = curModel;

            if (!string.IsNullOrWhiteSpace(s.ApiKey))
            {
                var live = await AiService.Instance.FetchAvailableModelsAsync(s.Provider, s.ApiKey);
                if (live.Count > 0)
                {
                    if (!live.Contains(curModel)) live.Insert(0, curModel);
                    CmbHeaderModel.ItemsSource = live;
                    CmbHeaderModel.SelectedItem = curModel;
                }
            }
        }
        catch { }
        finally
        {
            _isUpdatingModelCombo = false;
        }
    }

    private void OnHeaderModelSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isUpdatingModelCombo) return;
        if (CmbHeaderModel.SelectedItem is string selectedModel && !string.IsNullOrWhiteSpace(selectedModel))
        {
            AiService.Instance.Settings.Model = selectedModel;
            AiService.Instance.Settings.Save();
            TxtStatus.Text = $"Active model set to: {selectedModel}";
        }
    }

    private void OnPresetArrowTargetClick(object sender, RoutedEventArgs e)
    {
        TxtPrompt.Text = "Focus specifically on the anatomical structure and finding pointed to by the arrows / ROIs (e.g. tendon/ligament integrity, rupture/tear, disc fissure, or focal lesion). Characterize the defect, margins, and clinical significance.";
    }

    private void OnSelectAllCropsClick(object sender, RoutedEventArgs e)
    {
        bool anyUnchecked = _cropItems.Any(c => !c.IsIncluded);
        foreach (var item in _cropItems)
        {
            item.IsIncluded = anyUnchecked;
        }
        UpdateCropsCount();
    }

    private void OnPresetDifferentialClick(object sender, RoutedEventArgs e)
    {
        TxtPrompt.Text = "Evaluate the enhancement pattern, signal characteristics, and provide a differential diagnosis ranked by probability with next suggested diagnostic steps.";
    }

    private void OnPresetReportClick(object sender, RoutedEventArgs e)
    {
        TxtPrompt.Text = "Generate a formal radiological findings and conclusion text for these images, ready to insert into an official medical report.";
    }

    private void OnPresetCharacterizeClick(object sender, RoutedEventArgs e)
    {
        TxtPrompt.Text = "Characterize the findings in detail: dimensions/margins, internal architecture (solid/cystic/calcification), signal/attenuation compared to adjacent structures, and relevant radiological signs.";
    }

    private void OnPresetRomanianClick(object sender, RoutedEventArgs e)
    {
        TxtPrompt.Text = "Analizează imaginile selectate și redactează descrierea imagistică, diagnosticul diferențial și concluzia raportului medical în limba română.";
    }

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            OnAnalyzeClick(sender, e);
        }
    }

    private async void OnAnalyzeClick(object sender, RoutedEventArgs e)
    {
        var activeCrops = _cropItems.Where(c => c.IsIncluded).Select(c => c.Crop).ToList();
        if (activeCrops.Count == 0 && _chatHistory.Count == 0)
        {
            MessageBox.Show(this, "Please check at least one series image to analyze.", "AI Consultation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(AiService.Instance.Settings.ApiKey))
        {
            var res = MessageBox.Show(this, 
                "No API Key is configured yet.\nWould you like to open AI Settings now to enter your Google Gemini API Key?", 
                "API Key Required", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Information);

            if (res == MessageBoxResult.Yes)
            {
                OnOpenSettingsClick(sender, e);
            }
            return;
        }

        string userPrompt = TxtPrompt.Text.Trim();
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            userPrompt = _chatHistory.Count == 0 ? "Analyze the findings on these images and provide a radiological report." : "Please elaborate further on the differential diagnosis.";
        }

        BtnAnalyze.IsEnabled = false;
        PrgLoading.Visibility = Visibility.Visible;
        OverlayLoading.Visibility = Visibility.Visible;
        TxtStatus.Text = _chatHistory.Count == 0 ? "Analyzing DICOM series..." : "Consulting AI follow-up...";

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string responseText = await AiService.Instance.ChatTurnAsync(activeCrops, _chatHistory, userPrompt);
            sw.Stop();

            _chatHistory.Add(new AiConversationMessage { Role = "user", Text = userPrompt, Timestamp = DateTime.Now });
            _chatHistory.Add(new AiConversationMessage { Role = "model", Text = responseText, Timestamp = DateTime.Now });
            _latestModelReply = responseText;

            RenderChatHistory();

            BtnAnalyze.Content = "💬 Send Follow-up";
            BtnResetChat.Visibility = Visibility.Visible;
            TxtPrompt.Text = "";
            TxtStatus.Text = $"Turn #{_chatHistory.Count / 2} completed in {sw.ElapsedMilliseconds} ms.";
        }
        catch (Exception ex)
        {
            TxtResponse.Text += $"\n\n⚠ Error during AI Consultation:\n{ex.Message}\n\n(Tip: Check your API Key or try selecting gemini-3.6-flash in the Model dropdown)";
            TxtStatus.Text = "Error during consultation.";
        }
        finally
        {
            BtnAnalyze.IsEnabled = true;
            PrgLoading.Visibility = Visibility.Collapsed;
            OverlayLoading.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderChatHistory()
    {
        var sb = new StringBuilder();
        var s = AiService.Instance.Settings;
        string modelName = string.IsNullOrWhiteSpace(s.Model) ? "Gemini" : s.Model;

        for (int i = 0; i < _chatHistory.Count; i++)
        {
            var msg = _chatHistory[i];
            if (msg.Role == "user")
            {
                sb.AppendLine($"🧑‍⚕️ MEDIC [{msg.Timestamp:HH:mm:ss}]:");
                sb.AppendLine(msg.Text);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"🤖 PARSEC AI ({modelName}) [{msg.Timestamp:HH:mm:ss}]:");
                sb.AppendLine(msg.Text);
                sb.AppendLine();
                sb.AppendLine(new string('─', 65));
                sb.AppendLine();
            }
        }

        TxtResponse.Text = sb.ToString();
        ScrollResponse?.ScrollToEnd();
    }

    private void OnResetChatClick(object sender, RoutedEventArgs e)
    {
        _chatHistory.Clear();
        _latestModelReply = string.Empty;
        TxtResponse.Text = "Chat reset. You can now start a fresh consultation on the captured series above.";
        BtnAnalyze.Content = "⚡ Consult AI";
        BtnResetChat.Visibility = Visibility.Collapsed;
        TxtPrompt.Text = "Analyze the open slice(s) across series, characterizing any abnormal findings, lesions, and providing a differential diagnosis and report.";
        TxtStatus.Text = "Ready for new consultation.";
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new AiSettingsWindow { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            LoadHeaderModels();
        }
    }

    private void OnCopyResponseClick(object sender, RoutedEventArgs e)
    {
        string textToCopy = !string.IsNullOrWhiteSpace(_latestModelReply) ? _latestModelReply : TxtResponse.Text;
        if (!string.IsNullOrWhiteSpace(textToCopy))
        {
            Clipboard.SetText(textToCopy);
            TxtStatus.Text = "Copied report findings to clipboard!";
            MessageBox.Show(this, "AI report findings copied to clipboard.", "Parsec AI", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
