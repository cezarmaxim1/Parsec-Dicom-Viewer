// @purpose: Dialog to configure voice dictation settings (Google Gemini API Key, Gemini Model, reference reports, and persistent AI rules).
// @depends: OpenRadViewer.Core.Services (SpeechRecognitionService)
// @exports: VoiceSettingsDialog

using System.Windows;
using System.Windows.Controls;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public partial class VoiceSettingsDialog : Window
{
    public VoiceSettingsDialog()
    {
        InitializeComponent();
        DarkWindowHelper.ApplyDarkTitleBar(this);

        TxtGeminiKey.Text = SpeechRecognitionService.Instance.GeminiApiKey;

        var availableModels = new List<string>
        {
            "gemini-3.8-flash",
            "gemini-3.5-flash",
            "gemini-3.5-flash-lite",
            "gemini-flash-latest",
            "Auto"
        };

        string currentGeminiModel = SpeechRecognitionService.Instance.SelectedGeminiModel?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(currentGeminiModel)) currentGeminiModel = "gemini-3.8-flash";

        if (!availableModels.Contains(currentGeminiModel, StringComparer.OrdinalIgnoreCase))
        {
            availableModels.Insert(0, currentGeminiModel);
        }

        ComboGeminiModel.ItemsSource = availableModels;
        ComboGeminiModel.SelectedItem = availableModels.FirstOrDefault(m => string.Equals(m, currentGeminiModel, StringComparison.OrdinalIgnoreCase)) ?? currentGeminiModel;
        ComboGeminiModel.Text = currentGeminiModel;

        TxtCustomAiRules.Text = SpeechRecognitionService.Instance.CustomAiRules ?? string.Empty;

        UpdateRefFilesCount();
    }

    private void UpdateRefFilesCount()
    {
        int count = SpeechRecognitionService.Instance.GetReferenceReportsCount();
        TxtRefFilesCount.Text = count == 0
            ? "No reference files added yet (drop PDF or DOCX files here)"
            : $"{count} reference file(s) loaded (automatically used by AI)";
    }

    private void OnOpenRefFolder(object sender, RoutedEventArgs e)
    {
        SpeechRecognitionService.Instance.OpenReferenceReportsFolder();
        UpdateRefFilesCount();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        SpeechRecognitionService.Instance.SaveGeminiApiKey(TxtGeminiKey.Text);

        string modelToSave = ComboGeminiModel.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(modelToSave)) modelToSave = "gemini-3.8-flash";
        SpeechRecognitionService.Instance.SaveGeminiModel(modelToSave);

        SpeechRecognitionService.Instance.SaveCustomAiRules(TxtCustomAiRules.Text);

        MessageBox.Show(this, "Voice dictation & AI rules saved successfully!", "Voice Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
        Close();
    }
}
