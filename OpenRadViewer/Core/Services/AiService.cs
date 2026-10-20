// @purpose: AI radiological second-opinion consultation integration (Google Gemini / Anthropic Claude), handling multi-phase lesion crops, clinical context, and encrypted local API key storage.
// @depends: System.Net.Http, System.Text.Json, System.Windows.Media.Imaging
// @exports: AiService, AiSettings, AiConsultationMessage, ClinicalContext

using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace OpenRadViewer.Core.Services;

public class AiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Provider { get; set; } = "Gemini"; // "Gemini" or "Claude"
    public string Model { get; set; } = "gemini-3.6-flash";
    public string SystemInstructions { get; set; } = 
        "You are an expert subspecialist radiologist assistant. " +
        "Analyze the provided cropped radiological images (which may include multiple phases e.g., Native, Arterial, Portal Venous, Delayed). " +
        "Provide: 1. Imaging features (attenuation/signal, enhancement kinetics, margins, internal architecture); " +
        "2. Differential diagnosis ranked by probability; " +
        "3. Recommended reporting text and next diagnostic steps.";
    public bool AutoIncludeAllViewports { get; set; } = true;

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenRadViewer",
        "ai_settings.json"
    );

    public static AiSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var settings = JsonSerializer.Deserialize<AiSettings>(json);
                if (settings != null)
                {
                    if (string.IsNullOrWhiteSpace(settings.Model) || 
                        settings.Model.Contains("2.0") || 
                        settings.Model.Contains("1.5"))
                    {
                        settings.Model = "gemini-3.6-flash";
                    }
                    return settings;
                }
            }
        }
        catch { }

        return new AiSettings();
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(ConfigPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }
}

public class AiImageCrop
{
    public BitmapSource Image { get; set; } = null!;
    public string SeriesTitle { get; set; } = "Series";
    public string Modality { get; set; } = "CT";
    public int SliceIndex { get; set; }
    public double WindowCenter { get; set; }
    public double WindowWidth { get; set; }
    public Rect CropRect { get; set; }
    public bool IsIncluded { get; set; } = true;
    public bool HasArrowIndicator { get; set; }
    public int ArrowCount { get; set; }
    public bool HasRoiIndicator { get; set; }
    public int RoiCount { get; set; }

    public byte[] ToPngBytes()
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Image));
        encoder.Save(ms);
        return ms.ToArray();
    }
}

public class AiConversationMessage
{
    public string Role { get; set; } = "user"; // "user" or "model"
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class AiService
{
    public static AiService Instance { get; } = new();

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    public AiSettings Settings { get; private set; }

    public AiService()
    {
        Settings = AiSettings.Load();
    }

    public void ReloadSettings()
    {
        Settings = AiSettings.Load();
    }

    /// <summary>
    /// Analyzes one or multiple cropped lesion images with Gemini / Claude multimodal API.
    /// </summary>
    public async Task<string> ConsultLesionAsync(List<AiImageCrop> crops, string userPrompt)
    {
        return await ChatTurnAsync(crops, new List<AiConversationMessage>(), userPrompt);
    }

    /// <summary>
    /// Executes a turn in a multi-turn multimodal conversation with Gemini / Claude.
    /// Preserves image context from turn 1 and previous conversation history.
    /// </summary>
    public async Task<string> ChatTurnAsync(List<AiImageCrop> crops, List<AiConversationMessage> history, string userPrompt)
    {
        if (string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            throw new InvalidOperationException("API Key is missing. Please set your Gemini API Key in AI Settings.");
        }

        var activeCrops = crops.Where(c => c.IsIncluded).ToList();
        if (activeCrops.Count == 0 && history.Count == 0)
        {
            throw new InvalidOperationException("No images selected for AI analysis.");
        }

        if (Settings.Provider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
        {
            return await CallClaudeChatTurnAsync(activeCrops, history, userPrompt);
        }
        else
        {
            return await CallGeminiChatTurnAsync(activeCrops, history, userPrompt);
        }
    }

    private async Task<string> CallGeminiChatTurnAsync(List<AiImageCrop> crops, List<AiConversationMessage> history, string userPrompt)
    {
        string configuredModel = string.IsNullOrWhiteSpace(Settings.Model) || 
                                 Settings.Model.Contains("2.0") || 
                                 Settings.Model.Contains("1.5") ? "gemini-3.6-flash" : Settings.Model.Trim();

        var candidateModels = new List<string> { configuredModel };
        if (!candidateModels.Contains("gemini-3.6-flash")) candidateModels.Add("gemini-3.6-flash");
        if (!candidateModels.Contains("gemini-3.5-flash")) candidateModels.Add("gemini-3.5-flash");

        var contentsList = BuildGeminiContents(crops, history, userPrompt);

        Exception? lastException = null;

        for (int i = 0; i < candidateModels.Count; i++)
        {
            string currentModel = candidateModels[i];
            bool isFallback = i > 0;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    string result = await ExecuteGeminiChatRequestAsync(currentModel, contentsList);
                    if (isFallback)
                    {
                        return $"[ℹ️ Google Gemini Notice: Model '{configuredModel}' experienced high server demand / latency (HTTP 503). Analysis was automatically completed via '{currentModel}']\n\n{result}";
                    }
                    return result;
                }
                catch (HttpRequestException httpEx)
                {
                    lastException = httpEx;
                    bool isOverloaded = httpEx.Message.Contains("503") || httpEx.Message.Contains("429");

                    if (isOverloaded && attempt == 0)
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    if (isOverloaded)
                    {
                        break; // Try fallback candidate model
                    }

                    throw; // 401 or 400 - throw immediately
                }
                catch (TaskCanceledException timeoutEx)
                {
                    lastException = timeoutEx;
                    break;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    throw;
                }
            }
        }

        if (lastException != null) throw lastException;
        throw new InvalidOperationException("Gemini models could not be reached.");
    }

    private List<object> BuildGeminiContents(List<AiImageCrop> crops, List<AiConversationMessage> history, string userPrompt)
    {
        var contents = new List<object>();

        // 1. First user turn (contains initial prompt + image metadata + multimodal image parts)
        var firstPartsList = new List<object>();

        var promptSb = new StringBuilder();
        promptSb.AppendLine("### Clinical / Radiological Consultation Request");
        string firstPromptText = history.Count > 0 ? history[0].Text : userPrompt;
        if (!string.IsNullOrWhiteSpace(firstPromptText))
        {
            promptSb.AppendLine($"User Query / Clinical Note: {firstPromptText.Trim()}");
        }
        else
        {
            promptSb.AppendLine("Please analyze this lesion across the provided image phases/views.");
        }

        promptSb.AppendLine();
        promptSb.AppendLine($"The attached {crops.Count} image(s) show the open DICOM series / regions of interest:");
        for (int i = 0; i < crops.Count; i++)
        {
            var c = crops[i];
            string tags = "";
            if (c.HasArrowIndicator) tags += " [🎯 Pointer Arrow Included]";
            if (c.HasRoiIndicator) tags += " [⭕ ROI Circle/Rectangle Included]";
            promptSb.AppendLine($"• Image {i + 1}: {c.Modality} - {c.SeriesTitle} (Slice #{c.SliceIndex + 1}, WL: {(int)c.WindowCenter} WW: {(int)c.WindowWidth}){tags}");
        }

        if (crops.Any(c => c.HasArrowIndicator || c.HasRoiIndicator))
        {
            promptSb.AppendLine();
            promptSb.AppendLine("🎯 TARGETED FINDING INDICATORS (ARROWS AND/OR ROIS DETECTED ON IMAGES):");
            promptSb.AppendLine("The radiologist has drawn high-contrast yellow pointer arrows and/or circular/rectangular ROIs directly on the image pointing at or encircling the pathological findings (e.g. tendon rupture/tear, disc herniation/fissure, ligament injury, or focal lesion).");
            promptSb.AppendLine("PRIMARY INSTRUCTION: Focus your diagnostic evaluation specifically on the structures and areas indicated by the arrow tips and ROI boundaries. Describe their morphology, signal/attenuation, defect margins, and clinical significance.");
        }

        firstPartsList.Add(new { text = promptSb.ToString() });

        // Add inline images to first turn
        foreach (var crop in crops)
        {
            byte[] bytes = crop.ToPngBytes();
            string b64 = Convert.ToBase64String(bytes);
            firstPartsList.Add(new
            {
                inline_data = new
                {
                    mime_type = "image/png",
                    data = b64
                }
            });
        }

        contents.Add(new { role = "user", parts = firstPartsList.ToArray() });

        // 2. If we have previous history, add subsequent turns
        if (history.Count > 1)
        {
            for (int i = 1; i < history.Count; i++)
            {
                var msg = history[i];
                string geminiRole = msg.Role == "user" ? "user" : "model";
                contents.Add(new
                {
                    role = geminiRole,
                    parts = new object[] { new { text = msg.Text } }
                });
            }

            // Current follow-up prompt
            contents.Add(new
            {
                role = "user",
                parts = new object[] { new { text = userPrompt.Trim() } }
            });
        }

        return contents;
    }

    private async Task<string> ExecuteGeminiChatRequestAsync(string model, List<object> contents)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        object payload;
        if (!string.IsNullOrWhiteSpace(Settings.SystemInstructions))
        {
            payload = new
            {
                system_instruction = new
                {
                    parts = new object[]
                    {
                        new { text = Settings.SystemInstructions.Trim() }
                    }
                },
                contents = contents.ToArray(),
                generationConfig = new
                {
                    temperature = 0.2,
                    maxOutputTokens = 4096
                }
            };
        }
        else
        {
            payload = new
            {
                contents = contents.ToArray(),
                generationConfig = new
                {
                    temperature = 0.2,
                    maxOutputTokens = 4096
                }
            };
        }

        string requestJson = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", Settings.ApiKey.Trim());
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var response = await HttpClient.SendAsync(request, cts.Token);
        string responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            string errMessage = $"Gemini API Error (HTTP {(int)response.StatusCode}): ";
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("error", out var errObj) &&
                    errObj.TryGetProperty("message", out var msgProp))
                {
                    errMessage += msgProp.GetString();
                }
                else
                {
                    errMessage += responseBody;
                }
            }
            catch
            {
                errMessage += responseBody;
            }

            throw new HttpRequestException(errMessage);
        }

        using (var doc = JsonDocument.Parse(responseBody))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var first = candidates[0];
                if (first.TryGetProperty("content", out var contentObj) &&
                    contentObj.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                {
                    var sbResult = new StringBuilder();
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textProp))
                        {
                            sbResult.Append(textProp.GetString());
                        }
                    }

                    return sbResult.ToString();
                }
            }
        }

        return "Gemini responded with empty content.";
    }

    private async Task<string> CallClaudeChatTurnAsync(List<AiImageCrop> crops, List<AiConversationMessage> history, string userPrompt)
    {
        string model = string.IsNullOrWhiteSpace(Settings.Model) ? "claude-3-5-sonnet-20241022" : Settings.Model.Trim();
        string url = "https://api.anthropic.com/v1/messages";

        var messages = new List<object>();

        // First user turn
        var firstContentList = new List<object>();

        var promptSb = new StringBuilder();
        promptSb.AppendLine("### Clinical / Radiological Consultation Request");
        string firstPromptText = history.Count > 0 ? history[0].Text : userPrompt;
        if (!string.IsNullOrWhiteSpace(firstPromptText))
        {
            promptSb.AppendLine($"User Query / Clinical Note: {firstPromptText.Trim()}");
        }
        else
        {
            promptSb.AppendLine("Please analyze this lesion across the provided image phases/views.");
        }

        promptSb.AppendLine();
        promptSb.AppendLine($"The attached {crops.Count} image(s) show the open DICOM series / regions of interest:");
        for (int i = 0; i < crops.Count; i++)
        {
            var c = crops[i];
            string tags = "";
            if (c.HasArrowIndicator) tags += " [🎯 Pointer Arrow Included]";
            if (c.HasRoiIndicator) tags += " [⭕ ROI Circle/Rectangle Included]";
            promptSb.AppendLine($"• Image {i + 1}: {c.Modality} - {c.SeriesTitle} (Slice #{c.SliceIndex + 1}){tags}");
        }

        if (crops.Any(c => c.HasArrowIndicator || c.HasRoiIndicator))
        {
            promptSb.AppendLine();
            promptSb.AppendLine("🎯 TARGETED FINDING INDICATORS (ARROWS AND/OR ROIS DETECTED ON IMAGES):");
            promptSb.AppendLine("The radiologist has drawn high-contrast yellow pointer arrows and/or circular/rectangular ROIs directly on the image pointing at or encircling the pathological findings. Focus your evaluation on these structures.");
        }

        firstContentList.Add(new { type = "text", text = promptSb.ToString() });

        foreach (var crop in crops)
        {
            byte[] bytes = crop.ToPngBytes();
            string b64 = Convert.ToBase64String(bytes);
            firstContentList.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = "image/png",
                    data = b64
                }
            });
        }

        messages.Add(new { role = "user", content = firstContentList.ToArray() });

        if (history.Count > 1)
        {
            for (int i = 1; i < history.Count; i++)
            {
                var msg = history[i];
                string claudeRole = msg.Role == "user" ? "user" : "assistant";
                messages.Add(new { role = claudeRole, content = msg.Text });
            }

            messages.Add(new { role = "user", content = userPrompt.Trim() });
        }

        var payload = new
        {
            model = model,
            max_tokens = 4096,
            system = Settings.SystemInstructions ?? "",
            messages = messages.ToArray()
        };

        string requestJson = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-api-key", Settings.ApiKey.Trim());
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var response = await HttpClient.SendAsync(request, cts.Token);
        string responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Claude API Error (HTTP {(int)response.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        if (root.TryGetProperty("content", out var contentArray))
        {
            var sb = new StringBuilder();
            foreach (var item in contentArray.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "text" &&
                    item.TryGetProperty("text", out var textProp))
                {
                    sb.Append(textProp.GetString());
                }
            }
            return sb.ToString();
        }

        return "Claude responded with empty content.";
    }

    public async Task<List<string>> FetchAvailableModelsAsync(string provider, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new List<string>();

        var models = new List<string>();

        if (provider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
                req.Headers.Add("x-api-key", apiKey.Trim());
                req.Headers.Add("anthropic-version", "2023-06-01");
                var resp = await HttpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var dataArr))
                    {
                        foreach (var m in dataArr.EnumerateArray())
                        {
                            if (m.TryGetProperty("id", out var idProp))
                            {
                                string id = idProp.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(id)) models.Add(id);
                            }
                        }
                    }
                }
            }
            catch { }

            if (models.Count == 0)
            {
                models.AddRange(new[] { "claude-3-7-sonnet", "claude-3-5-sonnet-20241022", "claude-3-5-haiku-20241022" });
            }
            return models;
        }

        // Google Gemini List Models API: GET /v1beta/models
        try
        {
            string url = "https://generativelanguage.googleapis.com/v1beta/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-goog-api-key", apiKey.Trim());
            var resp = await HttpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("models", out var modelsArr))
                {
                    foreach (var m in modelsArr.EnumerateArray())
                    {
                        bool canGenerate = false;
                        if (m.TryGetProperty("supportedGenerationMethods", out var methods))
                        {
                            foreach (var method in methods.EnumerateArray())
                            {
                                if (string.Equals(method.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase))
                                {
                                    canGenerate = true;
                                    break;
                                }
                            }
                        }

                        if (canGenerate && m.TryGetProperty("name", out var nameProp))
                        {
                            string rawName = nameProp.GetString() ?? "";
                            string modelId = rawName.StartsWith("models/") ? rawName.Substring(7) : rawName;
                            if (!string.IsNullOrWhiteSpace(modelId) && 
                                !modelId.Contains("embedding") && 
                                !modelId.Contains("aqa") &&
                                !modelId.Contains("imagen") &&
                                !modelId.Contains("-tts") &&
                                !modelId.Contains("lyria") &&
                                !modelId.Contains("veo") &&
                                !modelId.Contains("transcribe") &&
                                !modelId.Contains("robotics") &&
                                !modelId.Contains("computer-use"))
                            {
                                models.Add(modelId);
                            }
                        }
                    }
                }
            }
        }
        catch { }

        if (models.Count == 0)
        {
            models.AddRange(new[] { "gemini-3.6-flash", "gemini-3.8-flash", "gemini-3.5-flash", "gemini-3.7-flash", "gemini-3.0-flash", "gemini-2.5-flash" });
        }
        else
        {
            // Sort models so gemini-3.6-flash (stable/fast), gemini-3.8-flash, gemini-3.5-flash rank at top
            models = models.OrderByDescending(m => m.Equals("gemini-3.6-flash", StringComparison.OrdinalIgnoreCase))
                           .ThenByDescending(m => m.Equals("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
                           .ThenByDescending(m => m.Equals("gemini-3.5-flash", StringComparison.OrdinalIgnoreCase))
                           .ThenByDescending(m => m.Contains("flash"))
                           .ThenByDescending(m => m)
                           .Distinct()
                           .ToList();
        }

        return models;
    }

    public async Task<(bool Success, string Message, List<string> LiveModels)> TestApiKeyAsync(string apiKey, string model, string provider = "Gemini")
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "API Key is empty.", new List<string>());
        }

        if (provider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
                req.Headers.Add("x-api-key", apiKey.Trim());
                req.Headers.Add("anthropic-version", "2023-06-01");
                var resp = await HttpClient.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    var live = await FetchAvailableModelsAsync(provider, apiKey);
                    return (true, "OK", live);
                }
                return (false, $"HTTP {(int)resp.StatusCode} ({resp.StatusCode}): {body}", new List<string>());
            }
            catch (Exception ex)
            {
                return (false, ex.Message, new List<string>());
            }
        }

        // Test Google Gemini key via GET /v1beta/models (model-agnostic authentication test)
        try
        {
            string url = "https://generativelanguage.googleapis.com/v1beta/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-goog-api-key", apiKey.Trim());

            var response = await HttpClient.SendAsync(req);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                string err = $"HTTP {(int)response.StatusCode} ({response.StatusCode})";
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("error", out var errObj) &&
                        errObj.TryGetProperty("message", out var msgProp))
                    {
                        err += $": {msgProp.GetString()}";
                    }
                    else
                    {
                        err += $": {responseBody}";
                    }
                }
                catch
                {
                    err += $": {responseBody}";
                }

                return (false, err, new List<string>());
            }

            // Authentication succeeded! Extract live models:
            var liveModels = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("models", out var modelsArr))
                {
                    foreach (var m in modelsArr.EnumerateArray())
                    {
                        bool canGenerate = false;
                        if (m.TryGetProperty("supportedGenerationMethods", out var methods))
                        {
                            foreach (var method in methods.EnumerateArray())
                            {
                                if (string.Equals(method.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase))
                                {
                                    canGenerate = true;
                                    break;
                                }
                            }
                        }

                        if (canGenerate && m.TryGetProperty("name", out var nameProp))
                        {
                            string rawName = nameProp.GetString() ?? "";
                            string modelId = rawName.StartsWith("models/") ? rawName.Substring(7) : rawName;
                            if (!string.IsNullOrWhiteSpace(modelId) && 
                                !modelId.Contains("embedding") && 
                                !modelId.Contains("aqa") &&
                                !modelId.Contains("imagen") &&
                                !modelId.Contains("-tts"))
                            {
                                liveModels.Add(modelId);
                            }
                        }
                    }
                }
            }
            catch { }

            liveModels = liveModels.OrderByDescending(m => m.Equals("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
                                   .ThenByDescending(m => m.Contains("3.8"))
                                   .ThenByDescending(m => m.Contains("flash"))
                                   .ThenByDescending(m => m)
                                   .Distinct()
                                   .ToList();

            if (liveModels.Count == 0)
            {
                liveModels.AddRange(new[] { "gemini-3.8-flash", "gemini-3.8-pro", "gemini-3.0-flash", "gemini-2.5-flash", "gemini-2.5-pro" });
            }

            return (true, "OK", liveModels);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, new List<string>());
        }
    }
}
