using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AIEngineer.AutoCAD
{
    internal sealed class WaveInAudioRecorder : IDisposable
    {
        private const int SampleRate = 16000;
        private const short BitsPerSample = 16;
        private const short Channels = 1;
        private const int BufferCount = 4;
        private const int BufferSize = 3200; // 100 ms @ 16kHz 16-bit mono
        private const uint CALLBACK_FUNCTION = 0x00030000;
        private const uint WAVE_MAPPER = unchecked((uint)-1);
        private const uint WIM_DATA = 0x3C0;

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")]
        private static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(
            out IntPtr phwi,
            uint uDeviceID,
            ref WAVEFORMATEX pwfx,
            WaveInProc dwCallback,
            IntPtr dwInstance,
            uint fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hwi);

        private readonly object _lock = new object();
        private IntPtr _hWaveIn = IntPtr.Zero;
        private WaveInProc _callbackKeepAlive;
        private byte[][] _rawBuffers;
        private GCHandle[] _bufferPins;
        private IntPtr[] _headerPtrs;
        private MemoryStream _pcmStream;
        private volatile bool _recording;
        private volatile bool _stopping;
        private DateTime _startedUtc;

        public bool IsRecording { get { return _recording; } }

        public int ElapsedSeconds
        {
            get
            {
                if (!_recording) return 0;
                return Math.Max(0, (int)(DateTime.UtcNow - _startedUtc).TotalSeconds);
            }
        }

        public void Start()
        {
            if (_recording) return;
            if (waveInGetNumDevs() == 0)
                throw new InvalidOperationException("لم يتم العثور على ميكروفون متصل في الجهاز (No recording device found).");

            WAVEFORMATEX fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, // PCM
                nChannels = (ushort)Channels,
                nSamplesPerSec = (uint)SampleRate,
                wBitsPerSample = (ushort)BitsPerSample,
                nBlockAlign = (ushort)(Channels * (BitsPerSample / 8)),
                nAvgBytesPerSec = (uint)(SampleRate * Channels * (BitsPerSample / 8)),
                cbSize = 0
            };

            _pcmStream = new MemoryStream();
            _stopping = false;
            _callbackKeepAlive = OnWaveInMessage;

            int openRes = waveInOpen(out _hWaveIn, WAVE_MAPPER, ref fmt, _callbackKeepAlive, IntPtr.Zero, CALLBACK_FUNCTION);
            if (openRes != 0 || _hWaveIn == IntPtr.Zero)
                throw new InvalidOperationException("تعذر فتح الميكروفون للتسجيل (waveInOpen code=" + openRes + "). تأكد من أذونات الميكروفون في Windows.");

            _rawBuffers = new byte[BufferCount][];
            _bufferPins = new GCHandle[BufferCount];
            _headerPtrs = new IntPtr[BufferCount];

            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            try
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    _rawBuffers[i] = new byte[BufferSize];
                    _bufferPins[i] = GCHandle.Alloc(_rawBuffers[i], GCHandleType.Pinned);
                    WAVEHDR hdr = new WAVEHDR
                    {
                        lpData = _bufferPins[i].AddrOfPinnedObject(),
                        dwBufferLength = (uint)BufferSize,
                        dwBytesRecorded = 0,
                        dwUser = (IntPtr)i,
                        dwFlags = 0,
                        dwLoops = 0,
                        lpNext = IntPtr.Zero,
                        reserved = IntPtr.Zero
                    };
                    IntPtr ptr = Marshal.AllocHGlobal(hdrSize);
                    Marshal.StructureToPtr(hdr, ptr, false);
                    _headerPtrs[i] = ptr;

                    int prepRes = waveInPrepareHeader(_hWaveIn, ptr, hdrSize);
                    if (prepRes != 0)
                        throw new InvalidOperationException("waveInPrepareHeader failed (code=" + prepRes + ").");
                    int addRes = waveInAddBuffer(_hWaveIn, ptr, hdrSize);
                    if (addRes != 0)
                        throw new InvalidOperationException("waveInAddBuffer failed (code=" + addRes + ").");
                }

                int startRes = waveInStart(_hWaveIn);
                if (startRes != 0)
                    throw new InvalidOperationException("waveInStart failed (code=" + startRes + ").");

                _startedUtc = DateTime.UtcNow;
                _recording = true;
            }
            catch
            {
                CleanupDevice();
                throw;
            }
        }

        private void OnWaveInMessage(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (uMsg != WIM_DATA || dwParam1 == IntPtr.Zero) return;
            try
            {
                WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(dwParam1, typeof(WAVEHDR));
                if (hdr.dwBytesRecorded > 0 && hdr.lpData != IntPtr.Zero)
                {
                    int n = (int)hdr.dwBytesRecorded;
                    byte[] chunk = new byte[n];
                    Marshal.Copy(hdr.lpData, chunk, 0, n);
                    lock (_lock)
                    {
                        if (_pcmStream != null)
                            _pcmStream.Write(chunk, 0, n);
                    }
                }
                if (!_stopping && _hWaveIn != IntPtr.Zero)
                {
                    waveInAddBuffer(_hWaveIn, dwParam1, Marshal.SizeOf(typeof(WAVEHDR)));
                }
            }
            catch { }
        }

        public byte[] StopAndGetWavBytes(out double durationSec, out int peakAmplitude)
        {
            durationSec = 0.0;
            peakAmplitude = 0;
            if (!_recording && _hWaveIn == IntPtr.Zero)
                return new byte[0];

            _stopping = true;
            _recording = false;

            CleanupDevice();

            byte[] pcm;
            lock (_lock)
            {
                pcm = _pcmStream != null ? _pcmStream.ToArray() : new byte[0];
                if (_pcmStream != null)
                {
                    _pcmStream.Dispose();
                    _pcmStream = null;
                }
            }

            if (pcm.Length < 2) return new byte[0];
            durationSec = pcm.Length / (double)(SampleRate * Channels * (BitsPerSample / 8));

            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
                int abs = sample == short.MinValue ? 32768 : Math.Abs((int)sample);
                if (abs > peakAmplitude) peakAmplitude = abs;
            }

            using (MemoryStream wav = new MemoryStream(44 + pcm.Length))
            using (BinaryWriter bw = new BinaryWriter(wav, Encoding.ASCII))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + pcm.Length);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16); // PCM chunk size
                bw.Write((short)1); // PCM format
                bw.Write(Channels);
                bw.Write(SampleRate);
                bw.Write(SampleRate * Channels * (BitsPerSample / 8));
                bw.Write((short)(Channels * (BitsPerSample / 8)));
                bw.Write(BitsPerSample);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(pcm.Length);
                bw.Write(pcm);
                bw.Flush();
                return wav.ToArray();
            }
        }

        private void CleanupDevice()
        {
            IntPtr handle = _hWaveIn;
            _hWaveIn = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                try { waveInReset(handle); } catch { }
            }

            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            if (_headerPtrs != null)
            {
                for (int i = 0; i < _headerPtrs.Length; i++)
                {
                    IntPtr ptr = _headerPtrs[i];
                    if (ptr != IntPtr.Zero)
                    {
                        if (handle != IntPtr.Zero)
                        {
                            try { waveInUnprepareHeader(handle, ptr, hdrSize); } catch { }
                        }
                        try { Marshal.FreeHGlobal(ptr); } catch { }
                        _headerPtrs[i] = IntPtr.Zero;
                    }
                }
            }

            if (handle != IntPtr.Zero)
            {
                try { waveInClose(handle); } catch { }
            }

            if (_bufferPins != null)
            {
                for (int i = 0; i < _bufferPins.Length; i++)
                {
                    if (_bufferPins[i].IsAllocated)
                    {
                        try { _bufferPins[i].Free(); } catch { }
                    }
                }
            }

            _rawBuffers = null;
            _bufferPins = null;
            _headerPtrs = null;
        }

        public void Dispose()
        {
            _stopping = true;
            _recording = false;
            CleanupDevice();
            lock (_lock)
            {
                if (_pcmStream != null)
                {
                    try { _pcmStream.Dispose(); } catch { }
                    _pcmStream = null;
                }
            }
        }
    }

    internal static class VoiceTranscriber
    {
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            try
            {
                ServicePointManager.SecurityProtocol |=
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            }
            catch { }
            HttpClient c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(90);
            return c;
        }

        public static bool HasCloudSttKey(DirectAiConfig cfg)
        {
            if (cfg == null) return false;
            return !string.IsNullOrWhiteSpace(cfg.GetApiKey(DirectAiConfig.ProviderGemini)) ||
                   !string.IsNullOrWhiteSpace(cfg.GetApiKey(DirectAiConfig.ProviderOpenAI));
        }

        public static string TranscribeWav(DirectAiConfig cfg, byte[] wavBytes, bool preferArabic)
        {
            if (wavBytes == null || wavBytes.Length <= 44)
                throw new InvalidOperationException(preferArabic ? "لم يتم تسجيل أي بيانات صوتية." : "No audio recorded.");
            if (cfg == null) cfg = DirectAiConfig.Load();

            string geminiKey = cfg.GetApiKey(DirectAiConfig.ProviderGemini);
            string openAiKey = cfg.GetApiKey(DirectAiConfig.ProviderOpenAI);

            List<string> errors = new List<string>();

            bool tryOpenAiFirst = string.Equals(cfg.Provider, DirectAiConfig.ProviderOpenAI, StringComparison.OrdinalIgnoreCase) &&
                                  !string.IsNullOrWhiteSpace(openAiKey);

            if (tryOpenAiFirst)
            {
                try { return TranscribeWithOpenAI(openAiKey, cfg.OpenAIBaseUrl, wavBytes); }
                catch (Exception ex) { errors.Add("OpenAI: " + ex.Message); }

                if (!string.IsNullOrWhiteSpace(geminiKey))
                {
                    try { return TranscribeWithGemini(geminiKey, cfg.GetModel(DirectAiConfig.ProviderGemini), wavBytes); }
                    catch (Exception ex) { errors.Add("Gemini: " + ex.Message); }
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(geminiKey))
                {
                    try { return TranscribeWithGemini(geminiKey, cfg.GetModel(DirectAiConfig.ProviderGemini), wavBytes); }
                    catch (Exception ex) { errors.Add("Gemini: " + ex.Message); }
                }
                if (!string.IsNullOrWhiteSpace(openAiKey))
                {
                    try { return TranscribeWithOpenAI(openAiKey, cfg.OpenAIBaseUrl, wavBytes); }
                    catch (Exception ex) { errors.Add("OpenAI: " + ex.Message); }
                }
            }

            if (string.IsNullOrWhiteSpace(geminiKey) && string.IsNullOrWhiteSpace(openAiKey))
            {
                throw new InvalidOperationException(preferArabic
                    ? "للتواصل الصوتي الدقيق بجميع اللهجات العربية والإنجليزية، أضف مفتاح Gemini أو OpenAI من زر [🔑 API] (ويمكنك الإبقاء على أي مزود للشات)."
                    : "Configure a Gemini or OpenAI API key in [🔑 API] to enable dialect-accurate voice recognition.");
            }

            throw new InvalidOperationException((preferArabic ? "تعذر تحويل الصوت إلى نص: " : "Voice transcription failed: ") +
                                                string.Join(" | ", errors.ToArray()));
        }

        private static string TranscribeWithGemini(string apiKey, string primaryModel, byte[] wavBytes)
        {
            List<string> models = new List<string>();
            Action<string> addModel = delegate(string m)
            {
                if (!string.IsNullOrWhiteSpace(m) && !models.Contains(m.Trim()))
                    models.Add(m.Trim());
            };
            addModel(primaryModel);
            addModel("gemini-2.5-flash");
            addModel("gemini-3.5-flash-lite");
            addModel("gemini-3.1-flash-lite-preview");
            addModel("gemini-flash-lite-latest");
            addModel("gemini-3.5-flash");

            string b64 = Convert.ToBase64String(wavBytes);
            string prompt =
                "Transcribe this engineer voice recording word-for-word.\n" +
                "Rules:\n" +
                "1. The speaker may use ANY Arabic dialect (Egyptian عامية مصرية, Saudi/Gulf خليجية, Levantine شامي, Moroccan/Algerian/Tunisian, Sudanese, or Modern Standard Arabic) OR English, OR naturally mix Arabic with English AutoCAD/CAD/MEP engineering terms.\n" +
                "2. Keep Arabic words in Arabic script matching what was spoken (no unwanted translation into English or rewriting) and keep spoken English technical/CAD words in English (e.g. Leader, MLeader, Polyline, Line, Block, Select, AutoCAD, DIALux, Excel, BOQ, Lux, kW, X, Y, BOLLARD, SPOT, WALL LIGHT, FRAM SITE, flood, ped post, Sidelight, upligh9w).\n" +
                "3. Output ONLY the clean transcribed text with NO quotes, NO preamble, NO markdown, and NO answer to the command.\n" +
                "4. If the audio is silent or unintelligible noise only, return an empty string.";

            Dictionary<string, object> body = new Dictionary<string, object>
            {
                { "contents", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "parts", new object[]
                                {
                                    new Dictionary<string, object>
                                    {
                                        { "inlineData", new Dictionary<string, object>
                                            {
                                                { "mimeType", "audio/wav" },
                                                { "data", b64 }
                                            }
                                        }
                                    },
                                    new Dictionary<string, object> { { "text", prompt } }
                                }
                            }
                        }
                    }
                },
                { "generationConfig", new Dictionary<string, object>
                    {
                        { "temperature", 0.0 },
                        { "maxOutputTokens", 1024 }
                    }
                }
            };

            string jsonPayload = _json.Serialize(body);
            string lastErr = "Gemini audio request failed.";

            for (int i = 0; i < models.Count; i++)
            {
                string m = models[i];
                string url = "https://generativelanguage.googleapis.com/v1beta/models/" +
                             Uri.EscapeDataString(m) + ":generateContent?key=" + Uri.EscapeDataString(apiKey.Trim());
                try
                {
                    using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                        HttpResponseMessage resp = _http.SendAsync(req).GetAwaiter().GetResult();
                        string raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (!resp.IsSuccessStatusCode)
                        {
                            lastErr = "HTTP " + (int)resp.StatusCode + ": " + ExtractApiErr(raw);
                            continue;
                        }
                        Dictionary<string, object> doc = _json.DeserializeObject(raw) as Dictionary<string, object>;
                        string text = ExtractGeminiText(doc);
                        return text ?? "";
                    }
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }
            }
            throw new InvalidOperationException(lastErr);
        }

        private static string TranscribeWithOpenAI(string apiKey, string baseUrl, byte[] wavBytes)
        {
            string b = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim().TrimEnd('/');
            string url = b + "/audio/transcriptions";
            string[] models = new string[] { "gpt-4o-mini-transcribe", "whisper-1" };
            string prompt = "AutoCAD CAD/MEP engineering speech in Arabic dialects (Egyptian, Gulf, Levantine, MSA) mixed with English terms like Leader, Polyline, Line, Block, Select, FRAM SITE, WALL LIGHT, SPOT, BOLLARD, DIALux, Excel, X, Y.";
            string lastErr = "OpenAI transcription failed.";

            for (int i = 0; i < models.Length; i++)
            {
                try
                {
                    using (MultipartFormDataContent form = new MultipartFormDataContent())
                    {
                        ByteArrayContent fileContent = new ByteArrayContent(wavBytes);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                        form.Add(fileContent, "file", "voice.wav");
                        form.Add(new StringContent(models[i]), "model");
                        form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");

                        using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url))
                        {
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                            req.Content = form;
                            HttpResponseMessage resp = _http.SendAsync(req).GetAwaiter().GetResult();
                            string raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            if (!resp.IsSuccessStatusCode)
                            {
                                lastErr = "HTTP " + (int)resp.StatusCode + ": " + ExtractApiErr(raw);
                                continue;
                            }
                            Dictionary<string, object> doc = _json.DeserializeObject(raw) as Dictionary<string, object>;
                            if (doc != null && doc.ContainsKey("text") && doc["text"] != null)
                                return doc["text"].ToString().Trim();
                            return "";
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }
            }
            throw new InvalidOperationException(lastErr);
        }

        private static string ExtractGeminiText(Dictionary<string, object> doc)
        {
            if (doc == null || !doc.ContainsKey("candidates")) return "";
            object[] cands = doc["candidates"] as object[];
            if (cands == null || cands.Length == 0) return "";
            Dictionary<string, object> c0 = cands[0] as Dictionary<string, object>;
            if (c0 == null || !c0.ContainsKey("content")) return "";
            Dictionary<string, object> content = c0["content"] as Dictionary<string, object>;
            if (content == null || !content.ContainsKey("parts")) return "";
            object[] parts = content["parts"] as object[];
            if (parts == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                Dictionary<string, object> p = parts[i] as Dictionary<string, object>;
                if (p == null) continue;
                if (p.ContainsKey("thought") && p["thought"] is bool && (bool)p["thought"]) continue;
                if (p.ContainsKey("text") && p["text"] != null)
                    sb.Append(p["text"].ToString());
            }
            return sb.ToString().Trim().Trim('"', '\'');
        }

        private static string ExtractApiErr(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "empty response";
            try
            {
                Dictionary<string, object> doc = _json.DeserializeObject(raw) as Dictionary<string, object>;
                if (doc != null && doc.ContainsKey("error") && doc["error"] is Dictionary<string, object>)
                {
                    Dictionary<string, object> err = (Dictionary<string, object>)doc["error"];
                    if (err.ContainsKey("message") && err["message"] != null)
                        return err["message"].ToString();
                }
            }
            catch { }
            return raw.Length > 220 ? raw.Substring(0, 220) : raw;
        }
    }

    internal static class VoiceSpeechOutput
    {
        private static readonly object _ttsLock = new object();
        private static object _sapiVoice;
        private static object _arabicToken;
        private static object _englishToken;
        private static bool _tokensScanned;

        public static void SpeakAsync(string rawText)
        {
            string clean = CleanForSpeech(rawText);
            if (string.IsNullOrWhiteSpace(clean)) return;

            Task.Run(delegate
            {
                try
                {
                    lock (_ttsLock)
                    {
                        EnsureVoiceInitialized();
                        if (_sapiVoice == null) return;

                        bool hasArabic = ContainsArabic(clean);
                        object chosen = hasArabic ? (_arabicToken ?? _englishToken) : (_englishToken ?? _arabicToken);
                        if (chosen != null)
                        {
                            try
                            {
                                _sapiVoice.GetType().InvokeMember("Voice", BindingFlags.SetProperty, null, _sapiVoice, new object[] { chosen });
                            }
                            catch { }
                        }

                        // SVSFlagsAsync (1) | SVSFPurgeBeforeSpeak (2) = 3
                        _sapiVoice.GetType().InvokeMember("Speak", BindingFlags.InvokeMethod, null, _sapiVoice, new object[] { clean, 3 });
                    }
                }
                catch { }
            });
        }

        public static void Stop()
        {
            try
            {
                lock (_ttsLock)
                {
                    if (_sapiVoice == null) return;
                    // Speak("", SVSFlagsAsync | SVSFPurgeBeforeSpeak) interrupts immediately
                    _sapiVoice.GetType().InvokeMember("Speak", BindingFlags.InvokeMethod, null, _sapiVoice, new object[] { "", 3 });
                }
            }
            catch { }
        }

        private static void EnsureVoiceInitialized()
        {
            if (_sapiVoice == null)
            {
                Type spType = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (spType != null)
                    _sapiVoice = Activator.CreateInstance(spType);
            }
            if (_sapiVoice == null || _tokensScanned) return;
            _tokensScanned = true;

            try
            {
                ScanTokensCategory(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices");
                if (_arabicToken == null || _englishToken == null)
                    ScanTokensCategory(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices");
            }
            catch { }
        }

        private static void ScanTokensCategory(string regPath)
        {
            Type catType = Type.GetTypeFromProgID("SAPI.SpObjectTokenCategory");
            if (catType == null) return;
            object cat = Activator.CreateInstance(catType);
            catType.InvokeMember("SetId", BindingFlags.InvokeMethod, null, cat, new object[] { regPath, false });
            object tokensObj = catType.InvokeMember("EnumerateTokens", BindingFlags.InvokeMethod, null, cat, null);
            System.Collections.IEnumerable tokens = tokensObj as System.Collections.IEnumerable;
            if (tokens == null) return;

            foreach (object tok in tokens)
            {
                if (tok == null) continue;
                try
                {
                    string id = Convert.ToString(tok.GetType().InvokeMember("Id", BindingFlags.GetProperty, null, tok, null), CultureInfo.InvariantCulture) ?? "";
                    string desc = "";
                    try { desc = Convert.ToString(tok.GetType().InvokeMember("GetDescription", BindingFlags.InvokeMethod, null, tok, null), CultureInfo.InvariantCulture) ?? ""; } catch { }
                    string probe = (id + " " + desc).ToLowerInvariant();
                    if (_arabicToken == null && (probe.Contains("_ar") || probe.Contains("arabic") || probe.Contains("hoda") || probe.Contains("naayf") || probe.Contains("salma") || probe.Contains("shakir")))
                        _arabicToken = tok;
                    else if (_englishToken == null && (probe.Contains("_en") || probe.Contains("english") || probe.Contains("david") || probe.Contains("zira") || probe.Contains("mark")))
                        _englishToken = tok;
                }
                catch { }
            }
        }

        private static bool ContainsArabic(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch >= '\u0600' && ch <= '\u06FF') return true;
            }
            return false;
        }

        public static string CleanForSpeech(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw;
            s = Regex.Replace(s, @"\[[^\]]+\]\([^)]+\)", "");
            s = Regex.Replace(s, @"https?://\S+", "");
            s = Regex.Replace(s, @"[`*_#>|]+", " ");
            s = Regex.Replace(s, @"-{2,}", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (s.Length > 600) s = s.Substring(0, 600) + "...";
            return s;
        }
    }
}
