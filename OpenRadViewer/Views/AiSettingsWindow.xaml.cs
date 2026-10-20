// @purpose: Settings dialog for configuring AI provider credentials (Gemini / Claude), model selection, custom prompts, and connectivity testing.
// @depends: OpenRadViewer.Core.Services (AiService, DarkWindowHelper)
// @exports: AiSettingsWindow

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Views;

public partial class AiSettingsWindow : Window
{
    private string _currentKey = string.Empty;
    private bool _isKeyVisible = false;

    public AiSettingsWindow()
    {
        InitializeComponent();
        LoadSettings();
    }

    private string SelectedModel
    {
        get
        {
            if (ComboModel?.SelectedItem is string s) return s;
            if (ComboModel?.SelectedItem is ComboBoxItem cbi) return cbi.Content as string ?? "";
            return ComboModel?.Text ?? "";
        }
        set
        {
            if (ComboModel == null || string.IsNullOrWhiteSpace(value)) return;

            foreach (var item in ComboModel.Items)
            {
                if (item is string s && string.Equals(s, value, StringComparison.OrdinalIgnoreCase))
                {
                    ComboModel.SelectedItem = item;
                    return;
                }
                if (item is ComboBoxItem cbi && string.Equals(cbi.Content as string, value, StringComparison.OrdinalIgnoreCase))
                {
                    ComboModel.SelectedItem = item;
                    return;
                }
            }

            ComboModel.Items.Add(value);
            ComboModel.SelectedItem = value;
        }
    }

    private void LoadSettings()
    {
        var s = AiService.Instance.Settings;
        _currentKey = s.ApiKey ?? string.Empty;
        TxtApiKeyPassword.Password = _currentKey;
        TxtApiKeyVisible.Text = _currentKey;

        // Select provider
        foreach (ComboBoxItem item in ComboProvider.Items)
        {
            if (string.Equals(item.Tag as string, s.Provider, StringComparison.OrdinalIgnoreCase))
            {
                ComboProvider.SelectedItem = item;
                break;
            }
        }

        PopulateDefaultModels(s.Provider);

        string defaultModel = string.Equals(s.Provider, "Claude", StringComparison.OrdinalIgnoreCase) ? "claude-3-5-sonnet-20241022" : "gemini-3.6-flash";
        SelectedModel = string.IsNullOrWhiteSpace(s.Model) || s.Model.Contains("2.0") || s.Model.Contains("1.5") ? defaultModel : s.Model;
        TxtSystemInstructions.Text = s.SystemInstructions ?? string.Empty;
        ChkMultiViewport.IsChecked = s.AutoIncludeAllViewports;

        UpdateKeyStatus();

        if (!string.IsNullOrWhiteSpace(_currentKey))
        {
            _ = PopulateModelsAsync(silent: true);
        }
    }

    private void PopulateDefaultModels(string provider)
    {
        if (ComboModel == null) return;
        ComboModel.Items.Clear();

        if (string.Equals(provider, "Claude", StringComparison.OrdinalIgnoreCase))
        {
            ComboModel.Items.Add("claude-3-5-sonnet-20241022");
            ComboModel.Items.Add("claude-3-7-sonnet");
            ComboModel.Items.Add("claude-3-5-haiku");
        }
        else
        {
            ComboModel.Items.Add("gemini-3.6-flash");
            ComboModel.Items.Add("gemini-3.8-flash");
            ComboModel.Items.Add("gemini-3.5-flash");
            ComboModel.Items.Add("gemini-3.7-flash");
            ComboModel.Items.Add("gemini-3.0-flash");
            ComboModel.Items.Add("gemini-2.5-flash");
        }
    }

    private void UpdateKeyStatus()
    {
        if (TxtKeyStatus == null) return;

        if (string.IsNullOrWhiteSpace(_currentKey))
        {
            TxtKeyStatus.Text = "No API Key configured. Click 'Get Free Gemini Key' to generate one in Google AI Studio.";
            TxtKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 175, 40));
        }
        else
        {
            TxtKeyStatus.Text = $"Key configured ({_currentKey.Length} chars). Ready to use.";
            TxtKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (TxtApiKeyPassword == null || TxtApiKeyVisible == null) return;
        if (!_isKeyVisible)
        {
            _currentKey = TxtApiKeyPassword.Password;
            TxtApiKeyVisible.Text = _currentKey;
            UpdateKeyStatus();
        }
    }

    private void OnVisibleKeyChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtApiKeyPassword == null || TxtApiKeyVisible == null) return;
        if (_isKeyVisible)
        {
            _currentKey = TxtApiKeyVisible.Text;
            TxtApiKeyPassword.Password = _currentKey;
            UpdateKeyStatus();
        }
    }

    private void OnToggleKeyVisibilityClick(object sender, RoutedEventArgs e)
    {
        _isKeyVisible = !_isKeyVisible;
        if (_isKeyVisible)
        {
            TxtApiKeyPassword.Visibility = Visibility.Collapsed;
            TxtApiKeyVisible.Visibility = Visibility.Visible;
            BtnToggleKeyVisible.Content = "🔒";
        }
        else
        {
            TxtApiKeyVisible.Visibility = Visibility.Collapsed;
            TxtApiKeyPassword.Visibility = Visibility.Visible;
            BtnToggleKeyVisible.Content = "👁";
        }
    }

    private async Task PopulateModelsAsync(bool silent = true)
    {
        if (ComboModel == null) return;
        if (string.IsNullOrWhiteSpace(_currentKey))
        {
            if (!silent)
            {
                MessageBox.Show(this, "Please enter or paste your API Key first to fetch available models.", "API Key Needed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        string provider = (ComboProvider.SelectedItem as ComboBoxItem)?.Tag as string ?? "Gemini";
        string currentSelection = SelectedModel;

        try
        {
            if (BtnRefreshModels != null)
            {
                BtnRefreshModels.IsEnabled = false;
                BtnRefreshModels.Content = "⏳ Fetching...";
            }

            var models = await AiService.Instance.FetchAvailableModelsAsync(provider, _currentKey);

            if (models.Count > 0)
            {
                ComboModel.Items.Clear();
                foreach (var m in models)
                {
                    ComboModel.Items.Add(m);
                }

                if (!string.IsNullOrWhiteSpace(currentSelection) && models.Contains(currentSelection))
                {
                    SelectedModel = currentSelection;
                }
                else
                {
                    string best = models.FirstOrDefault(m => m.Equals("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
                               ?? models.FirstOrDefault(m => m.Contains("3.8-flash") && !m.Contains("tts"))
                               ?? models.FirstOrDefault(m => m.Contains("flash") && !m.Contains("tts"))
                               ?? models[0];
                    SelectedModel = best;
                }

                if (TxtModelsFetchedInfo != null)
                {
                    TxtModelsFetchedInfo.Text = $"✓ Retrieved {models.Count} live models from {provider} API (Active: {SelectedModel})";
                    TxtModelsFetchedInfo.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                }

                if (!silent)
                {
                    MessageBox.Show(this,
                        $"Successfully fetched {models.Count} models directly from the {provider} API!\n\nNewest models available on your account:\n• " + string.Join("\n• ", models.Take(8)),
                        "Live Models Fetched", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox.Show(this, $"Failed to fetch live models: {ex.Message}", "Fetch Models", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (BtnRefreshModels != null)
            {
                BtnRefreshModels.IsEnabled = true;
                BtnRefreshModels.Content = "🔄 Fetch Live Models";
            }
        }
    }

    private async void OnFetchModelsClick(object sender, RoutedEventArgs e)
    {
        await PopulateModelsAsync(silent: false);
    }

    private async void OnTestKeyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentKey))
        {
            MessageBox.Show(this, "Please paste an API Key before testing.", "Test Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnTestKey.IsEnabled = false;
        BtnTestKey.Content = "Testing...";

        try
        {
            string provider = (ComboProvider.SelectedItem as ComboBoxItem)?.Tag as string ?? "Gemini";
            string model = SelectedModel;
            var (ok, errMsg, liveModels) = await AiService.Instance.TestApiKeyAsync(_currentKey, model, provider);

            if (ok)
            {
                TxtKeyStatus.Text = "✓ Connection successful! API Key is valid and active.";
                TxtKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));

                if (liveModels.Count > 0)
                {
                    ComboModel.Items.Clear();
                    foreach (var m in liveModels) ComboModel.Items.Add(m);
                    string best = liveModels.FirstOrDefault(m => m.Equals("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
                               ?? liveModels.FirstOrDefault(m => m.Contains("3.8-flash") && !m.Contains("tts"))
                               ?? liveModels.FirstOrDefault(m => m.Contains("flash") && !m.Contains("tts"))
                               ?? liveModels[0];
                    SelectedModel = best;
                    if (TxtModelsFetchedInfo != null)
                    {
                        TxtModelsFetchedInfo.Text = $"✓ Live models loaded ({liveModels.Count}). Selected: {SelectedModel}";
                        TxtModelsFetchedInfo.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    }
                }

                MessageBox.Show(this, 
                    $"Connection to {provider} successful!\nYour API Key is valid and active.\n\nLatest active model '{SelectedModel}' was automatically selected!", 
                    "AI Connection Test", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                TxtKeyStatus.Text = "⚠ Connection failed.";
                TxtKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(229, 57, 53));
                MessageBox.Show(this, $"Connection test failed.\n\nDetails from API:\n{errMsg}", "AI Connection Test", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            TxtKeyStatus.Text = $"Error: {ex.Message}";
            TxtKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(229, 57, 53));
            MessageBox.Show(this, $"Error connecting to AI API:\n{ex.Message}", "AI Connection Test", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnTestKey.IsEnabled = true;
            BtnTestKey.Content = "Test Key";
        }
    }

    private void OnGetApiKeyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://aistudio.google.com/app/apikey",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OnResetPromptClick(object sender, RoutedEventArgs e)
    {
        TxtSystemInstructions.Text = 
            "You are an expert subspecialist radiologist assistant. " +
            "Analyze the provided cropped radiological images (which may include multiple phases e.g., Native, Arterial, Portal Venous, Delayed). " +
            "Provide: 1. Imaging features (attenuation/signal, enhancement kinetics, margins, internal architecture); " +
            "2. Differential diagnosis ranked by probability; " +
            "3. Recommended reporting text and next diagnostic steps.";
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboModel == null || ComboProvider == null) return;
        if (ComboProvider.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            PopulateDefaultModels(tag);

            if (tag == "Claude")
            {
                SelectedModel = "claude-5.5-sonnet";
            }
            else
            {
                SelectedModel = "gemini-3.8-flash";
            }

            if (!string.IsNullOrWhiteSpace(_currentKey))
            {
                _ = PopulateModelsAsync(silent: true);
            }
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var s = AiService.Instance.Settings;
        s.ApiKey = _currentKey.Trim();
        s.Provider = (ComboProvider.SelectedItem as ComboBoxItem)?.Tag as string ?? "Gemini";
        string fallbackModel = string.Equals(s.Provider, "Claude", StringComparison.OrdinalIgnoreCase) ? "claude-5.5-sonnet" : "gemini-3.8-flash";
        string chosen = SelectedModel;
        s.Model = string.IsNullOrWhiteSpace(chosen) ? fallbackModel : chosen.Trim();
        s.SystemInstructions = TxtSystemInstructions.Text.Trim();
        s.AutoIncludeAllViewports = ChkMultiViewport.IsChecked == true;

        s.Save();
        AiService.Instance.ReloadSettings();

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
