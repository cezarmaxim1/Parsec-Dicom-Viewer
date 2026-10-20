// @purpose: Voice recording and speech-to-text transcription in Romanian for clinical dictation (Google Gemini Audio API for flawless medical accuracy + Local Whisper Base/Small fallback + Windows Voice Typing).
// @depends: Whisper.net, Whisper.net.Runtime, System.Net.Http, System.Text.Json
// @exports: SpeechRecognitionService

using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Whisper.net;
using Whisper.net.Ggml;

namespace OpenRadViewer.Core.Services;

public class SpeechRecognitionService : IDisposable
{
    private static SpeechRecognitionService? _instance;
    public static SpeechRecognitionService Instance => _instance ??= new SpeechRecognitionService();

    [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_LWIN = 0x5B;
    private const byte VK_H = 0x48;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private readonly string _audioDir;
    private readonly string _tempWavPath;
    private readonly string _modelsDir;
    private readonly string _geminiKeyPath;
    private readonly DispatcherTimer _timer;
    private DateTime _recordStartTime;
    private WhisperFactory? _whisperFactory;
    private string? _loadedModelPath;

    public bool IsRecording { get; private set; }
    public int RecordingSeconds { get; private set; }
    public string StatusMessage { get; private set; } = string.Empty;
    public string GeminiApiKey { get; set; } = string.Empty;
    public string SelectedGeminiModel { get; set; } = "gemini-3.5-flash"; // User can enter any model string
    public string LastAiError { get; private set; } = string.Empty;
    public string SelectedWhisperModel { get; set; } = "base"; // "base", "small", "tiny"
    public string ReferenceReportsDir => _referenceReportsDir;
    public string CustomAiRules { get; set; } = string.Empty;

    private readonly string _referenceReportsDir;
    private readonly string _geminiModelPath;
    private readonly string _customRulesPath;

    public event Action<int>? RecordingDurationChanged;
    public event Action<string>? StatusChanged;

    public SpeechRecognitionService()
    {
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parsec");
        _audioDir = Path.Combine(appData, "Audio");
        _modelsDir = Path.Combine(appData, "Models");
        _geminiKeyPath = Path.Combine(appData, "gemini_key.txt");
        _geminiModelPath = Path.Combine(appData, "gemini_model.txt");
        _customRulesPath = Path.Combine(appData, "ai_custom_rules.txt");
        _referenceReportsDir = Path.Combine(appData, "ReferenceReports");

        Directory.CreateDirectory(_audioDir);
        Directory.CreateDirectory(_modelsDir);
        Directory.CreateDirectory(_referenceReportsDir);

        _tempWavPath = Path.Combine(_audioDir, "temp_dictation.wav");

        // Load saved Gemini API key if present
        try
        {
            if (File.Exists(_geminiKeyPath))
            {
                GeminiApiKey = File.ReadAllText(_geminiKeyPath).Trim();
            }
        }
        catch { }

        // Load saved Gemini Model if present
        try
        {
            if (File.Exists(_geminiModelPath))
            {
                string m = File.ReadAllText(_geminiModelPath).Trim();
                if (!string.IsNullOrEmpty(m))
                {
                    // Auto-migrate retired models or slow defaults to gemini-3.5-flash
                    if (m.Contains("2.5") || m.Contains("2.0") || m.Contains("1.5") || m.Equals("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
                    {
                        m = "gemini-3.5-flash";
                        try { File.WriteAllText(_geminiModelPath, m); } catch { }
                    }
                    SelectedGeminiModel = m;
                }
            }
        }
        catch { }

        // Load saved Custom AI Rules if present
        try
        {
            if (File.Exists(_customRulesPath))
            {
                CustomAiRules = File.ReadAllText(_customRulesPath);
            }
            else
            {
                CustomAiRules = "Concluziile pe scurt.\r\nFara \"-\" inainte la concluzii.";
                File.WriteAllText(_customRulesPath, CustomAiRules);
            }
        }
        catch { }

        // Ensure default unique medical vocabulary & reference notes are available in ReferenceReports
        try
        {
            string targetVocab = Path.Combine(_referenceReportsDir, "vocabular_medical.txt");
            if (!File.Exists(targetVocab))
            {
                string assetVocab = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "vocabular_medical.txt");
                if (File.Exists(assetVocab))
                {
                    File.Copy(assetVocab, targetVocab, true);
                }
            }

            string targetNotes = Path.Combine(_referenceReportsDir, "modele_rezultate.txt");
            if (!File.Exists(targetNotes))
            {
                string assetNotes = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "modele_rezultate.txt");
                if (File.Exists(assetNotes))
                {
                    File.Copy(assetNotes, targetNotes, true);
                }
            }
        }
        catch { }

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (s, e) =>
        {
            RecordingSeconds = (int)(DateTime.UtcNow - _recordStartTime).TotalSeconds;
            RecordingDurationChanged?.Invoke(RecordingSeconds);
        };
    }

    public void SaveGeminiApiKey(string key)
    {
        GeminiApiKey = key?.Trim() ?? string.Empty;
        try
        {
            File.WriteAllText(_geminiKeyPath, GeminiApiKey);
            SetStatus(string.IsNullOrEmpty(GeminiApiKey) ? "Cheie Gemini ștearsă." : "Cheie Gemini salvată cu succes!");
        }
        catch (Exception ex)
        {
            SetStatus($"Eroare salvare cheie: {ex.Message}");
        }
    }

    public void SaveGeminiModel(string model)
    {
        SelectedGeminiModel = string.IsNullOrWhiteSpace(model) ? "gemini-3.5-flash" : model.Trim();
        try
        {
            File.WriteAllText(_geminiModelPath, SelectedGeminiModel);
        }
        catch { }
    }

    public void SaveCustomAiRules(string rules)
    {
        CustomAiRules = rules ?? string.Empty;
        try
        {
            File.WriteAllText(_customRulesPath, CustomAiRules);
        }
        catch { }
    }

    public int GetReferenceReportsCount()
    {
        try
        {
            if (!Directory.Exists(_referenceReportsDir)) return 0;
            return Directory.GetFiles(_referenceReportsDir, "*.*")
                .Count(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }

    public void OpenReferenceReportsFolder()
    {
        try
        {
            Directory.CreateDirectory(_referenceReportsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _referenceReportsDir,
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>
    /// Starts recording audio from the default microphone into 16kHz 16-bit mono WAV.
    /// </summary>
    public bool StartRecording()
    {
        if (IsRecording) return false;

        try
        {
            if (File.Exists(_tempWavPath))
            {
                try { File.Delete(_tempWavPath); } catch { }
            }

            mciSendString("close parsec_rec", null, 0, IntPtr.Zero);
            mciSendString("open new type waveaudio alias parsec_rec", null, 0, IntPtr.Zero);
            mciSendString("set parsec_rec time format ms", null, 0, IntPtr.Zero);
            mciSendString("set parsec_rec bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2", null, 0, IntPtr.Zero);
            int res = mciSendString("record parsec_rec", null, 0, IntPtr.Zero);

            if (res != 0)
            {
                SetStatus($"Eroare inițializare microfon (cod MCI: {res})");
                return false;
            }

            IsRecording = true;
            _recordStartTime = DateTime.UtcNow;
            RecordingSeconds = 0;
            _timer.Start();

            SetStatus("Înregistrare activă... Vorbiți în limba română (F4 pentru stop)");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Eroare pornire înregistrare: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stops audio recording and transcribes using Gemini API (if key available) or local Whisper.
    /// </summary>
    public async Task<string> StopRecordingAndTranscribeAsync(IProgress<string>? progress = null)
    {
        if (!IsRecording) return string.Empty;

        try
        {
            _timer.Stop();
            IsRecording = false;

            mciSendString($"save parsec_rec \"{_tempWavPath}\"", null, 0, IntPtr.Zero);
            mciSendString("close parsec_rec", null, 0, IntPtr.Zero);

            if (!File.Exists(_tempWavPath) || new FileInfo(_tempWavPath).Length < 1000)
            {
                SetStatus("Nu s-a detectat semnal audio util.");
                return string.Empty;
            }

            // 1. Try Google Gemini API first if API key is configured (flawless Romanian medical accuracy)
            if (!string.IsNullOrWhiteSpace(GeminiApiKey))
            {
                SetStatus($"Transcriere cu Gemini AI ({SelectedGeminiModel})...");
                progress?.Report("Se transcrie cu Gemini AI...");
                try
                {
                    string geminiResult = await TranscribeWithGeminiAsync(_tempWavPath, GeminiApiKey, SelectedGeminiModel);
                    if (!string.IsNullOrWhiteSpace(geminiResult))
                    {
                        SetStatus("Dictare transcrisă impecabil de Gemini AI!");
                        return geminiResult;
                    }
                }
                catch (Exception ex)
                {
                    SetStatus($"Gemini API inaccesibil ({ex.Message}), se încearcă Whisper local...");
                }
            }

            // 2. Fallback to Local Whisper
            SetStatus("Transcriere cu Whisper local...");
            progress?.Report("Se transcrie cu Whisper local...");
            string text = await TranscribeWavAsync(_tempWavPath, progress);

            SetStatus(string.IsNullOrWhiteSpace(text) ? "Dictare finalizată (fără text)" : "Dictare transcrisă de Whisper local.");
            return text;
        }
        catch (Exception ex)
        {
            SetStatus($"Eroare transcriere: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            IsRecording = false;
        }
    }

    /// <summary>
    /// Loads unique medical vocabulary from ReferenceReports or application Assets.
    /// </summary>
    public static string LoadMedicalVocabulary(string dir)
    {
        try
        {
            string[] possiblePaths = new[]
            {
                Path.Combine(dir, "vocabular_medical.txt"),
                Path.Combine(dir, "vocabular_medical_cuvinte_unice.txt"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "vocabular_medical.txt")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    string txt = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(txt)) return txt;
                }
            }
        }
        catch { }
        return string.Empty;
    }

    /// <summary>
    /// Reads reference reports (.docx and .txt) from ReferenceReports directory to inject into Gemini prompt.
    /// </summary>
    public static string LoadReferenceReportsText(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return string.Empty;
            var sb = new StringBuilder();
            var files = Directory.GetFiles(dir);
            int count = 0;
            foreach (var file in files)
            {
                if (count >= 5) break; // Keep prompt balanced and fast
                string fname = Path.GetFileName(file);
                if (fname.StartsWith("vocabular", StringComparison.OrdinalIgnoreCase) ||
                    fname.StartsWith("modele_rezultate", StringComparison.OrdinalIgnoreCase) ||
                    fname.StartsWith("notepad", StringComparison.OrdinalIgnoreCase))
                    continue;
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".txt")
                {
                    var fi = new FileInfo(file);
                    if (fi.Length > 50 * 1024) continue; // Large notes files are queried via ExtractRelevantReferenceLines
                    string txt = File.ReadAllText(file);
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        count++;
                        sb.AppendLine($"\n--- EXEMPLU RAPORT ANTERIOR ({Path.GetFileName(file)}) ---\n{txt.Trim()}\n");
                    }
                }
                else if (ext == ".docx")
                {
                    try
                    {
                        using var zip = System.IO.Compression.ZipFile.OpenRead(file);
                        var entry = zip.GetEntry("word/document.xml");
                        if (entry != null)
                        {
                            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                            string xml = reader.ReadToEnd();
                            string docText = System.Text.RegularExpressions.Regex.Replace(xml, @"<w:p[^>]*>", "\n");
                            docText = System.Text.RegularExpressions.Regex.Replace(docText, @"<[^>]+>", " ");
                            docText = System.Net.WebUtility.HtmlDecode(docText);
                            docText = System.Text.RegularExpressions.Regex.Replace(docText, @"[ \t]+", " ").Trim();
                            if (!string.IsNullOrWhiteSpace(docText))
                            {
                                count++;
                                sb.AppendLine($"\n--- EXEMPLU RAPORT ANTERIOR ({Path.GetFileName(file)}) ---\n{docText}\n");
                            }
                        }
                    }
                    catch { }
                }
            }
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Builds full Romanian radiology prompt with terminology rules and reference reports.
    /// </summary>
    public static string BuildMedicalKnowledgePrompt(string refDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ești un sistem avansat de transcriere medicală pentru radiologie și imagistică medicală în limba română.");
        sb.AppendLine("Sarcina ta este să transcrii dictarea audio a medicului radiolog cu acuratețe medicală absolută (100%).");
        sb.AppendLine();
        sb.AppendLine("REGULI CRUCIALE DE TERMINOLOGIE MEDICALĂ ÎN LIMBA ROMÂNĂ:");
        sb.AppendLine("- Niveluri vertebrale: L1-L2, L2-L3, L3-L4, L4-L5, L5-S1, T12-L1, C5-C6 etc. Când medicul spune 'L unu L doi' sau 'T doisprezece L unu', scrie exact 'L1-L2', 'T12-L1'.");
        sb.AppendLine("- Referiri de nivel: 'platoul superior al patru' -> 'platoul superior al patru'; 'la L unu' -> 'la L1'.");
        sb.AppendLine("- Fisuri discale: 'fisură în doi' (scrie 'fisură în doi'), 'fisură în trei' (scrie 'fisură în trei').");
        sb.AppendLine("- Termeni de specialitate obligatorii: con medular, platouri vertebrale, hernii intraspongioase etajate, osteofite marginale antero-laterale/posterioare, punți osoase, artroză interapofizară, protruzie discală circumferențială, hernie de disc intraforaminală/subarticulară, contact disco-radicular, diametru AP, fără modificări de semnal la nivelul corpilor vertebrali.");
        sb.AppendLine("- Secvențe imagistice: T1, T2, FLAIR, DWI, ADC, STIR, 3D T1, MPR.");
        sb.AppendLine("- Folosește întotdeauna diacritice corecte în limba română (ă, â, î, ș, ț).");
        sb.AppendLine();

        string vocabText = LoadMedicalVocabulary(refDir);
        if (!string.IsNullOrWhiteSpace(vocabText))
        {
            sb.AppendLine("GLOSAR DE CUVINTE ȘI VOCABULAR MEDICAL DE SPECIALITATE (FOLOSEȘTE-LE PENTRU RECUNOAȘTERE EXACTĂ):");
            sb.AppendLine(vocabText);
            sb.AppendLine();
        }

        string referenceText = LoadReferenceReportsText(refDir);
        if (!string.IsNullOrWhiteSpace(referenceText))
        {
            sb.AppendLine("Iată exemple de rapoarte medicale reale ale medicului radiolog. Folosește-le ca model de stil:");
            sb.AppendLine(referenceText);
            sb.AppendLine();
        }

        sb.AppendLine("Răspunde STRICT și EXCLUSIV cu textul dictat de medic, fără nicio introducere, fără comentarii, fără ghilimele.");
        return sb.ToString();
    }

    /// <summary>
    /// Transcribes WAV audio using Google Gemini multimodal endpoint (Gemini 2.0 Flash / 1.5 Flash) with full medical context.
    /// </summary>
    public static async Task<string> TranscribeWithGeminiAsync(string wavFilePath, string apiKey, string preferredModel = "gemini-3.5-flash")
    {
        byte[] audioBytes = await File.ReadAllBytesAsync(wavFilePath);
        string base64Audio = Convert.ToBase64String(audioBytes);

        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parsec");
        string refDir = Path.Combine(appData, "ReferenceReports");

        string promptText = BuildMedicalKnowledgePrompt(refDir);

        var partsList = new List<object>
        {
            new { text = promptText },
            new
            {
                inlineData = new
                {
                    mimeType = "audio/wav",
                    data = base64Audio
                }
            }
        };

        var payload = new
        {
            contents = new[]
            {
                new { parts = partsList.ToArray() }
            }
        };

        string json = JsonSerializer.Serialize(payload);

        // Build list of models to try. First priority is ALWAYS the model specified by user/setting
        var modelsToTry = new List<string>();
        string target = preferredModel?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(target) && !target.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            modelsToTry.Add(target);
        }

        // Add active family models in order of proven speed, quota availability & resilience
        var pool = new[]
        {
            "gemini-3.5-flash-lite",
            "gemini-3.1-flash-lite",
            "gemini-flash-lite-latest",
            "gemini-3.8-flash",
            "gemini-3.6-flash",
            "gemini-3.5-flash",
            "gemini-3.7-flash",
            "gemini-flash-latest",
            "gemini-3.5-transcribe"
        };

        foreach (var p in pool)
        {
            if (!modelsToTry.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                modelsToTry.Add(p);
            }
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };

        // Attempt 1: Try with full prompt (including medical vocabulary)
        foreach (var model in modelsToTry)
        {
            try
            {
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey.Trim()}";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    continue; // try next model if 429 quota, 503 high demand or 404
                }

                string respJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(respJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var firstCandidate = candidates[0];
                    if (firstCandidate.TryGetProperty("content", out var cObj) &&
                        cObj.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                    {
                        var firstPart = parts[0];
                        // 1. Standard text field (gemini-3.8-flash, gemini-3.5-flash-lite, etc.)
                        if (firstPart.TryGetProperty("text", out var textProp))
                        {
                            string text = textProp.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                        }
                        // 2. Dedicated transcription field (gemini-3.5-transcribe)
                        if (firstPart.TryGetProperty("audioTranscription", out var atObj) &&
                            atObj.TryGetProperty("text", out var atTextProp))
                        {
                            string text = atTextProp.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                        }
                    }
                }
            }
            catch
            {
                // try next candidate model
            }
        }

        // Attempt 2: If heavy prompt failed or timed out, retry with compact core prompt
        try
        {
            var compactPayload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = "Ești un sistem de transcriere medicală pentru imagistică (CT/IRM/RX) în limba română. Transcrie exact ce auzi rostit de medic în limba română, cu diacritice și termeni medicali corecți (coloană, ficat, plămâni, creier). Răspunde STRICT cu textul dictat." },
                            new
                            {
                                inlineData = new
                                {
                                    mimeType = "audio/wav",
                                    data = base64Audio
                                }
                            }
                        }
                    }
                }
            };
            string compactJson = JsonSerializer.Serialize(compactPayload);

            foreach (var model in modelsToTry)
            {
                try
                {
                    string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey.Trim()}";
                    using var content = new StringContent(compactJson, Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(url, content);
                    if (!response.IsSuccessStatusCode) continue;

                    string respJson = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(respJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var firstCandidate = candidates[0];
                        if (firstCandidate.TryGetProperty("content", out var cObj) &&
                            cObj.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                        {
                            var firstPart = parts[0];
                            if (firstPart.TryGetProperty("text", out var textProp))
                            {
                                string text = textProp.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return string.Empty;
    }

    public class GeminiFileCacheEntry
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileUri { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime UploadTimeUtc { get; set; }
    }

    private static readonly string _cacheFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Parsec", "gemini_files_cache.json");

    private static Dictionary<string, GeminiFileCacheEntry> LoadFileCache()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                string cJson = File.ReadAllText(_cacheFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, GeminiFileCacheEntry>>(cJson);
                if (dict != null) return dict;
            }
        }
        catch { }
        return new Dictionary<string, GeminiFileCacheEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveFileCache(Dictionary<string, GeminiFileCacheEntry> cache)
    {
        try
        {
            string cJson = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cacheFilePath, cJson);
        }
        catch { }
    }

    /// <summary>
    /// Uploads a reference file (PDF) to Gemini Files API and caches the URI for 40 hours.
    /// </summary>
    public static async Task<string?> EnsurePdfUploadedAsync(string pdfPath, string apiKey)
    {
        try
        {
            if (!File.Exists(pdfPath)) return null;
            var fi = new FileInfo(pdfPath);
            var cache = LoadFileCache();

            if (cache.TryGetValue(pdfPath, out var cachedEntry))
            {
                if (cachedEntry.FileSize == fi.Length &&
                    (DateTime.UtcNow - cachedEntry.UploadTimeUtc).TotalHours < 40 &&
                    !string.IsNullOrWhiteSpace(cachedEntry.FileUri))
                {
                    return cachedEntry.FileUri;
                }
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            string initUrl = $"https://generativelanguage.googleapis.com/upload/v1beta/files?key={apiKey.Trim()}";
            using var initReq = new HttpRequestMessage(HttpMethod.Post, initUrl);
            initReq.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            initReq.Headers.Add("X-Goog-Upload-Command", "start");
            initReq.Headers.Add("X-Goog-Upload-Header-Content-Length", fi.Length.ToString());
            initReq.Headers.Add("X-Goog-Upload-Header-Content-Type", "application/pdf");
            initReq.Content = new StringContent(
                JsonSerializer.Serialize(new { file = new { display_name = Path.GetFileName(pdfPath) } }),
                Encoding.UTF8,
                "application/json"
            );

            var initResp = await client.SendAsync(initReq);
            if (!initResp.IsSuccessStatusCode) return null;

            if (!initResp.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls)) return null;
            string uploadUrl = uploadUrls.First();

            byte[] pdfBytes = await File.ReadAllBytesAsync(pdfPath);
            using var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            uploadReq.Headers.Add("X-Goog-Upload-Offset", "0");
            uploadReq.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
            uploadReq.Content = new ByteArrayContent(pdfBytes);

            var uploadResp = await client.SendAsync(uploadReq);
            if (!uploadResp.IsSuccessStatusCode) return null;

            string respJson = await uploadResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respJson);
            if (doc.RootElement.TryGetProperty("file", out var fileObj) &&
                fileObj.TryGetProperty("uri", out var uriProp))
            {
                string uri = uriProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(uri))
                {
                    cache[pdfPath] = new GeminiFileCacheEntry
                    {
                        FilePath = pdfPath,
                        FileUri = uri,
                        FileSize = fi.Length,
                        UploadTimeUtc = DateTime.UtcNow
                    };
                    SaveFileCache(cache);
                    return uri;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Discovers which Gemini models are available for this specific API key.
    /// </summary>
    public static async Task<List<string>> GetAvailableGeminiModelsAsync(string apiKey)
    {
        var list = new List<string>();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string url = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey.Trim()}";
            string json = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var modelsArr))
            {
                foreach (var m in modelsArr.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var nProp))
                    {
                        string name = nProp.GetString() ?? "";
                        if (name.StartsWith("models/")) name = name.Substring(7);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            list.Add(name);
                        }
                    }
                }
            }
        }
        catch { }
        return list;
    }

    public void CancelRecording()
    {
        if (!IsRecording) return;
        try
        {
            _timer.Stop();
            IsRecording = false;
            mciSendString("close parsec_rec", null, 0, IntPtr.Zero);
            if (File.Exists(_tempWavPath))
            {
                try { File.Delete(_tempWavPath); } catch { }
            }
            SetStatus("Înregistrare anulată.");
        }
        catch { }
    }

    public static string GetWhisperPrompt()
    {
        return "Examinare imagistică CT, IRM, RX. Coloană vertebrală: L1-L2, L2-L3, L3-L4, L4-L5, L5-S1, C5-C6, T12-L1, con medular, platouri vertebrale, hernii intraspongioase etajate, osteofite marginale, artroză interapofizară, protruzie discală circumferențială, hernie de disc intraforaminală, fisură în doi, fisură în trei, diametru AP, T1, T2, FLAIR, DWI, ADC, STIR. Ficat fără macronoduli și fără ascită, colecist fără calculi cu pereți subțiri, sistem port permeabil, vene suprahepatice fără distensii sau leziuni focale, splină cu dimensiuni normale structură omogenă, vezică urinară în semirepleție, pancreas, rinichi. Atrofie cortico-subcorticală supra și infratentorială, ventriculomegalie pasageră, spații pericerebrale, fără leziuni demielinizante sau hemoragice acute.";
    }

    /// <summary>
    /// Transcribes an audio file in Romanian using Whisper.net with chosen model and prompt bias.
    /// </summary>
    public async Task<string> TranscribeWavAsync(string wavFilePath, IProgress<string>? progress = null)
    {
        string? modelPath = await EnsureModelAvailableAsync(progress);
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
        {
            SetStatus("Modelul de recunoaștere vocală nu este disponibil.");
            return string.Empty;
        }

        try
        {
            if (_whisperFactory == null || _loadedModelPath != modelPath)
            {
                _whisperFactory?.Dispose();
                _whisperFactory = WhisperFactory.FromPath(modelPath);
                _loadedModelPath = modelPath;
            }

            using var processor = _whisperFactory.CreateBuilder()
                .WithLanguage("ro")
                .WithPrompt(GetWhisperPrompt())
                .Build();

            using var fileStream = File.OpenRead(wavFilePath);
            var sb = new StringBuilder();

            await foreach (var result in processor.ProcessAsync(fileStream))
            {
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    string chunk = result.Text.Trim();
                    if (sb.Length > 0 && !sb.ToString().EndsWith(" ") && !sb.ToString().EndsWith("\n"))
                    {
                        sb.Append(" ");
                    }
                    sb.Append(chunk);
                }
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            SetStatus($"Eroare procesare Whisper: {ex.Message}");
            return string.Empty;
        }
    }

    private async Task<string?> EnsureModelAvailableAsync(IProgress<string>? progress = null)
    {
        string modelName = SelectedWhisperModel.ToLowerInvariant() switch
        {
            "small" => "ggml-small.bin",
            "tiny" => "ggml-tiny.bin",
            _ => "ggml-base.bin" // Default to base (140MB, 4x better than tiny)
        };

        string targetModelPath = Path.Combine(_modelsDir, modelName);
        if (File.Exists(targetModelPath)) return targetModelPath;

        // Check if another existing model is already present
        string baseModel = Path.Combine(_modelsDir, "ggml-base.bin");
        if (File.Exists(baseModel)) return baseModel;

        string tinyModel = Path.Combine(_modelsDir, "ggml-tiny.bin");
        if (File.Exists(tinyModel) && modelName == "ggml-tiny.bin") return tinyModel;

        // Auto-download chosen model
        try
        {
            SetStatus($"Descărcare model Whisper {modelName} (~140MB)...");
            progress?.Report($"Descărcare model Whisper ({modelName})...");

            string tempDownload = targetModelPath + ".download";
            string downloadUrl = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{modelName}";

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
            using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using (var src = await response.Content.ReadAsStreamAsync())
            using (var dst = File.Create(tempDownload))
            {
                await src.CopyToAsync(dst);
            }

            if (File.Exists(targetModelPath)) File.Delete(targetModelPath);
            File.Move(tempDownload, targetModelPath);

            SetStatus("Model Whisper instalat cu succes!");
            return targetModelPath;
        }
        catch (Exception ex)
        {
            // If download of base failed, fallback to tiny if already present
            if (File.Exists(tinyModel)) return tinyModel;

            SetStatus($"Descărcare model eșuată: {ex.Message}.");
            return null;
        }
    }

    /// <summary>
    /// Formats raw dictated clinical text into a structured radiological interpretation (TEXT + CONCLUZII only, without header or footer)
    /// using the Parsec protocol template and the top relevant reference sentences from modele_rezultate.txt (notepad.txt).
    /// </summary>
    public async Task<string> FormatDictationToProtocolAsync(
        string dictatedText,
        ClinicalProtocol protocol,
        Models.PatientModel? patient,
        Models.StudyModel? study,
        Models.SeriesModel? series)
    {
        string apiKey = !string.IsNullOrWhiteSpace(GeminiApiKey)
            ? GeminiApiKey.Trim()
            : (AiService.Instance.Settings.ApiKey?.Trim() ?? string.Empty);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetStatus("Lipsă cheie API Google Gemini! Deschideți ⚙️ pentru a introduce cheia.");
            return string.Empty;
        }

        SetStatus("✨ Se formatează protocolul cu AI...");

        string relevantNotes = ClinicalProtocolsService.ExtractRelevantReferenceLines(dictatedText, protocol, 25);

        var promptSb = new StringBuilder();
        promptSb.AppendLine("Ești un medic primar radiolog român de elită. Sarcina ta este să transformi dictarea brută a medicului într-o interpretare radiologică completă (TEXT + CONCLUZII), impecabil structurată și redactată în limba română (cu diacritice corecte).");
        promptSb.AppendLine();
        promptSb.AppendLine($"1. PROTOCOLUL ȘABLON DE BAZĂ DIN PARSEC ({protocol.Title}):");
        promptSb.AppendLine(protocol.Content.Trim());
        promptSb.AppendLine();

        if (!string.IsNullOrWhiteSpace(relevantNotes))
        {
            promptSb.AppendLine("2. EXEMPLE DE FORMULĂRI REALE ALE ACESTUI MEDIC (din arhiva personală de rezultate - folosește EXACT acest stil și terminologie):");
            promptSb.AppendLine(relevantNotes);
            promptSb.AppendLine();
        }

        promptSb.AppendLine("3. DICTAREA BRUTĂ A MEDICULUI PENTRU PACIENTUL CURENT:");
        promptSb.AppendLine("\"\"\"");
        promptSb.AppendLine(dictatedText.Trim());
        promptSb.AppendLine("\"\"\"");
        promptSb.AppendLine();

        if (!string.IsNullOrWhiteSpace(CustomAiRules))
        {
            promptSb.AppendLine("4. REGULI ȘI PREFERINȚE PERMANENTE ALE MEDICULUI (DE RESPECTAT CU PRIORITATE STRICTĂ):");
            promptSb.AppendLine(CustomAiRules.Trim());
            promptSb.AppendLine();
        }

        promptSb.AppendLine("REGULI STRICTE DE REDACTARE:");
        promptSb.AppendLine("- Răspunde STRICT cu partea de interpretare radiologică (TEXT + CONCLUZII) și nimic altceva.");
        promptSb.AppendLine("- NU introduce antet cu datele pacientului (fără Nume, Prenume, CNP, Vârstă, Zona examinată), NU introduce titlul 'REZULTAT EXAMEN' și NU introduce subsolul cu 'DATA' sau 'MEDIC EXAMINATOR' (acestea vor fi adăugate automat de Parsec la salvare).");
        promptSb.AppendLine("- Începe direct cu subtitlul regiunii examinate din șablon (ex: COLOANA LOMBARA:, COLOANA CERVICALA:, CRANIO-CEREBRAL:, GENUNCHI:, etc.).");
        promptSb.AppendLine("- Integrează toate modificările dictate de medic în structura șablonului de bază, păstrând formulările standard pentru elementele normale și înlocuind/completând segmentele patologice.");
        promptSb.AppendLine("- Corectează automat eventualele erori fonetice din dictare (ex: 'al patru' -> 'L4', 'fisură în doi' / 'fisură în trei' sau niveluri vertebrale L1-L2, L2-L3, L3-L4, L4-L5, L5-S1, T12-L1).");
        promptSb.AppendLine("- La modificările discale etajate, ordonează-le clar cu liniuță pe rânduri separate (ex: '- L2-L3: ...', '- L3-L4: ...', '- L4-L5: ...').");
        promptSb.AppendLine("- După descrierea constatărilor, lasă un rând gol, scrie 'CONCLUZII:' pe rând separat, apoi sintetizează concluziile radiologice pe scurt, clar și concis, pe rânduri separate.");
        promptSb.AppendLine("- NU folosi blocuri markdown (fără ```), NU pune steluțe (**), răspunde EXCLUSIV cu textul curat al interpretării și concluziilor.");

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = promptSb.ToString() }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.15
            }
        };

        string result = await SendGeminiGenerateContentAsync(JsonSerializer.Serialize(payload), apiKey);
        if (!string.IsNullOrWhiteSpace(result))
        {
            result = ClinicalProtocolsService.StripHeaderAndFooter(result);
            SetStatus("✨ Protocol formatat cu succes!");
            return result;
        }

        string errDetail = !string.IsNullOrWhiteSpace(LastAiError) ? $" ({LastAiError})" : "";
        SetStatus($"Eroare la formatarea AI{errDetail}. Verificați conexiunea la internet sau cheia API.");
        return string.Empty;
    }

    /// <summary>
    /// Conversational AI refinement: applies a written instruction from the doctor directly onto the existing report text.
    /// </summary>
    public async Task<string> RefineReportWithTextAsync(
        string currentReportText,
        string instruction,
        ClinicalProtocol? protocol)
    {
        string apiKey = !string.IsNullOrWhiteSpace(GeminiApiKey)
            ? GeminiApiKey.Trim()
            : (AiService.Instance.Settings.ApiKey?.Trim() ?? string.Empty);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetStatus("Lipsă cheie API Google Gemini! Deschideți ⚙️ pentru a introduce cheia.");
            return string.Empty;
        }

        SetStatus("🪄 Se aplică modificarea cerută în raport...");

        string cleanCurrent = ClinicalProtocolsService.StripHeaderAndFooter(currentReportText);
        string relevantNotes = ClinicalProtocolsService.ExtractRelevantReferenceLines(
            $"{instruction} {cleanCurrent}", protocol, 15);

        var promptSb = new StringBuilder();
        promptSb.AppendLine("Ești un medic primar radiolog român de elită, asistentul direct al medicului examinator.");
        promptSb.AppendLine("Ai mai jos interpretarea radiologică curentă (TEXT + CONCLUZII), iar medicul îți dă o instrucțiune scurtă de modificare/retușare.");
        promptSb.AppendLine();
        promptSb.AppendLine("INTERPRETAREA RADIOLOGICĂ CURENTĂ:");
        promptSb.AppendLine("\"\"\"");
        promptSb.AppendLine(cleanCurrent);
        promptSb.AppendLine("\"\"\"");
        promptSb.AppendLine();
        if (!string.IsNullOrWhiteSpace(relevantNotes))
        {
            promptSb.AppendLine("EXEMPLE DE FORMULĂRI REALE ALE ACESTUI MEDIC (folosește același stil concis):");
            promptSb.AppendLine(relevantNotes);
            promptSb.AppendLine();
        }
        promptSb.AppendLine("INSTRUCȚIUNEA MEDICULUI DE MODIFICARE / RETUȘARE:");
        promptSb.AppendLine($"\"{instruction.Trim()}\"");
        promptSb.AppendLine();
        if (!string.IsNullOrWhiteSpace(CustomAiRules))
        {
            promptSb.AppendLine("REGULI ȘI PREFERINȚE PERMANENTE ALE MEDICULUI (DE RESPECTAT CU PRIORITATE):");
            promptSb.AppendLine(CustomAiRules.Trim());
            promptSb.AppendLine();
        }
        promptSb.AppendLine("REGULI STRICTE:");
        promptSb.AppendLine("- Aplică ÎNTOCMAI modificarea cerută de medic direct în textul interpretării/concluziilor (de exemplu: dacă cere scurtarea concluziilor, fă-le scurte, telegrafice și la obiect; dacă cere adăugarea/ștergerea sau modificarea unei observații, opereaz-o la locul potrivit și actualizează concluziile dacă e cazul).");
        promptSb.AppendLine("- Păstrează INTACT restul interpretării și concluziilor care nu sunt vizate de modificare.");
        promptSb.AppendLine("- NU adăuga antet cu datele pacientului (Nume, Prenume, CNP, Vârstă), NU adăuga 'REZULTAT EXAMEN' și NU adăuga subsol cu 'DATA' sau 'MEDIC EXAMINATOR'. Răspunde STRICT cu TEXT + CONCLUZII.");
        promptSb.AppendLine("- NU folosi blocuri markdown (fără ```), NU pune steluțe (**), nu adăuga explicații sau comentarii; răspunde EXCLUSIV cu interpretarea și concluziile actualizate.");

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = promptSb.ToString() }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.15
            }
        };

        string result = await SendGeminiGenerateContentAsync(JsonSerializer.Serialize(payload), apiKey);
        if (!string.IsNullOrWhiteSpace(result))
        {
            result = ClinicalProtocolsService.StripHeaderAndFooter(result);
            SetStatus("🪄 Modificare aplicată cu succes!");
            return result;
        }

        string errDetail = !string.IsNullOrWhiteSpace(LastAiError) ? $" ({LastAiError})" : "";
        SetStatus($"Eroare la retușarea AI{errDetail}. Verificați conexiunea sau cheia API.");
        return string.Empty;
    }

    /// <summary>
    /// Conversational Voice AI refinement: stops microphone recording and applies the spoken voice instruction
    /// directly onto the existing report in a single multimodal Gemini Flash call.
    /// </summary>
    public async Task<string> StopRecordingAndRefineReportAsync(
        string currentReportText,
        ClinicalProtocol? protocol)
    {
        if (!IsRecording) return string.Empty;

        try
        {
            _timer.Stop();
            IsRecording = false;

            mciSendString($"save parsec_rec \"{_tempWavPath}\"", null, 0, IntPtr.Zero);
            mciSendString("close parsec_rec", null, 0, IntPtr.Zero);

            if (!File.Exists(_tempWavPath) || new FileInfo(_tempWavPath).Length < 1000)
            {
                SetStatus("Nu s-a detectat comandă vocală.");
                return string.Empty;
            }

            string apiKey = !string.IsNullOrWhiteSpace(GeminiApiKey)
                ? GeminiApiKey.Trim()
                : (AiService.Instance.Settings.ApiKey?.Trim() ?? string.Empty);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SetStatus("Lipsă cheie API Google Gemini! Deschideți ⚙️ pentru a introduce cheia.");
                return string.Empty;
            }

            SetStatus("🪄 Se ascultă instrucțiunea și se modifică raportul...");

            byte[] audioBytes = await File.ReadAllBytesAsync(_tempWavPath);
            string base64Audio = Convert.ToBase64String(audioBytes);
            string cleanCurrent = ClinicalProtocolsService.StripHeaderAndFooter(currentReportText);
            string relevantNotes = ClinicalProtocolsService.ExtractRelevantReferenceLines(cleanCurrent, protocol, 15);

            var promptSb = new StringBuilder();
            promptSb.AppendLine("Ești un medic primar radiolog român de elită, asistentul direct al medicului examinator.");
            promptSb.AppendLine("Ascultă cu atenție instrucțiunea vocală a medicului din fișierul audio atașat.");
            promptSb.AppendLine("În înregistrarea audio, medicul îți spune ce dorește să modifici, să scurtezi, să corectezi sau să adaugi în interpretarea radiologică de mai jos (de exemplu: 'concluziile mi se par mult prea lungi', 'adaugă o protruzie la L2-L3', 'șterge scolioza', etc.).");
            promptSb.AppendLine();
            promptSb.AppendLine("INTERPRETAREA RADIOLOGICĂ CURENTĂ:");
            promptSb.AppendLine("\"\"\"");
            promptSb.AppendLine(cleanCurrent);
            promptSb.AppendLine("\"\"\"");
            promptSb.AppendLine();
            if (!string.IsNullOrWhiteSpace(relevantNotes))
            {
                promptSb.AppendLine("EXEMPLE DE FORMULĂRI REALE ALE ACESTUI MEDIC (folosește același stil concis și telegrafic):");
                promptSb.AppendLine(relevantNotes);
                promptSb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(CustomAiRules))
            {
                promptSb.AppendLine("REGULI ȘI PREFERINȚE PERMANENTE ALE MEDICULUI (DE RESPECTAT CU PRIORITATE):");
                promptSb.AppendLine(CustomAiRules.Trim());
                promptSb.AppendLine();
            }
            promptSb.AppendLine("REGULI STRICTE:");
            promptSb.AppendLine("- Aplică ÎNTOCMAI modificarea rostită de medic în audio direct asupra textului interpretării/concluziilor.");
            promptSb.AppendLine("- Păstrează INTACT restul interpretării și concluziilor care nu sunt vizate de modificare.");
            promptSb.AppendLine("- NU adăuga antet cu datele pacientului (Nume, Prenume, CNP, Vârstă), NU adăuga 'REZULTAT EXAMEN' și NU adăuga subsol cu 'DATA' sau 'MEDIC EXAMINATOR'. Răspunde STRICT cu TEXT + CONCLUZII.");
            promptSb.AppendLine("- NU transcrie cuvintele comenzii în raport, ci execută modificarea cerută!");
            promptSb.AppendLine("- NU folosi blocuri markdown (fără ```), NU pune steluțe (**), răspunde EXCLUSIV cu interpretarea și concluziile actualizate.");

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = promptSb.ToString() },
                            new
                            {
                                inlineData = new
                                {
                                    mimeType = "audio/wav",
                                    data = base64Audio
                                }
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.15
                }
            };

            string result = await SendGeminiGenerateContentAsync(JsonSerializer.Serialize(payload), apiKey);
            if (!string.IsNullOrWhiteSpace(result))
            {
                result = ClinicalProtocolsService.StripHeaderAndFooter(result);
                SetStatus("🪄 Raport modificat conform instrucțiunii vocale!");
                return result;
            }

            string errDetail = !string.IsNullOrWhiteSpace(LastAiError) ? $" ({LastAiError})" : "";
            SetStatus($"Eroare la retușarea vocală AI{errDetail}.");
            return string.Empty;
        }
        catch (Exception ex)
        {
            SetStatus($"Eroare retuș vocal: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            IsRecording = false;
        }
    }

    private async Task<string> SendGeminiGenerateContentAsync(string jsonPayload, string apiKey)
    {
        LastAiError = string.Empty;
        var modelsToTry = new List<string>();
        string target = SelectedGeminiModel?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(target) && !target.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            modelsToTry.Add(target);
        }

        var pool = new[]
        {
            "gemini-3.5-flash",
            "gemini-3.8-flash",
            "gemini-3.5-flash-lite",
            "gemini-flash-latest"
        };

        foreach (var p in pool)
        {
            if (!modelsToTry.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                modelsToTry.Add(p);
            }
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

        foreach (var model in modelsToTry)
        {
            try
            {
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    LastAiError = $"[{model}] HTTP {(int)response.StatusCode}";
                    continue;
                }

                string respJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(respJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var firstCandidate = candidates[0];
                    if (firstCandidate.TryGetProperty("content", out var cObj) &&
                        cObj.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                    {
                        var firstPart = parts[0];
                        if (firstPart.TryGetProperty("text", out var textProp))
                        {
                            string formatted = textProp.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(formatted))
                            {
                                formatted = System.Text.RegularExpressions.Regex.Replace(formatted.Trim(), @"^```[a-zA-Z]*\s*", "");
                                formatted = System.Text.RegularExpressions.Regex.Replace(formatted, @"\s*```$", "");
                                formatted = formatted.Replace("**", "").Trim();
                                return formatted;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastAiError = $"[{model}] {ex.Message}";
                // try next model
            }
        }

        return string.Empty;
    }

    public static void TriggerWindowsDictation()
    {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_H, 0, 0, UIntPtr.Zero);
        keybd_event(VK_H, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void SetStatus(string msg)
    {
        StatusMessage = msg;
        StatusChanged?.Invoke(msg);
    }

    public void Dispose()
    {
        CancelRecording();
        _whisperFactory?.Dispose();
        _whisperFactory = null;
    }
}
