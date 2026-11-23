using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AIEngineer.Revit.Direct
{
    /// <summary>
    /// Shared DPAPI-encrypted Direct AI configuration (%APPDATA%\AI-Engineer\direct_ai_config.json).
    /// Uses Win32 crypt32.dll CryptProtectData directly so it works identically on .NET Framework 4.x
    /// (Revit 2016-2024) AND .NET 8 (Revit 2025-2026+) while staying 100% binary-compatible with
    /// the AutoCAD plugin's System.Security.Cryptography.ProtectedData storage!
    /// </summary>
    internal sealed class RevitDirectAiConfig
    {
        public const string ProviderGemini = "gemini";
        public const string ProviderClaude = "claude";
        public const string ProviderOpenAI = "openai";

        private const string DpapiPrefix = "dpapi:";
        private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("AI-Engineer.AutoCAD.DirectAiConfig.v1");

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string szDataDescr,
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            IntPtr ppszDataDescr,
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public string Provider = ProviderGemini;
        public string GeminiApiKey = "";
        public string GeminiModel = "gemini-3.5-flash-lite";
        public string ClaudeApiKey = "";
        public string ClaudeModel = "claude-sonnet-4-20250514";
        public string OpenAIApiKey = "";
        public string OpenAIModel = "gpt-5.3-codex";
        public string OpenAIBaseUrl = "https://api.openai.com/v1";
        public string OpenAIApiMode = "responses";
        public bool VoiceAutoSend = true;
        public bool VoiceSpeakReplies = true;

        public static string ConfigFilePath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(Path.Combine(appData, "AI-Engineer"), "direct_ai_config.json");
            }
        }

        internal static string ProtectSecret(string plain)
        {
            string trimmed = (plain ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed)) return "";
            byte[] plainBytes = Encoding.UTF8.GetBytes(trimmed);
            DATA_BLOB inBlob = new DATA_BLOB();
            DATA_BLOB entBlob = new DATA_BLOB();
            DATA_BLOB outBlob = new DATA_BLOB();
            try
            {
                inBlob.cbData = plainBytes.Length;
                inBlob.pbData = Marshal.AllocHGlobal(plainBytes.Length);
                Marshal.Copy(plainBytes, 0, inBlob.pbData, plainBytes.Length);

                entBlob.cbData = DpapiEntropy.Length;
                entBlob.pbData = Marshal.AllocHGlobal(DpapiEntropy.Length);
                Marshal.Copy(DpapiEntropy, 0, entBlob.pbData, DpapiEntropy.Length);

                if (!CryptProtectData(ref inBlob, null, ref entBlob, IntPtr.Zero, IntPtr.Zero, 0, out outBlob))
                    return "";

                byte[] encBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, encBytes, 0, outBlob.cbData);
                return DpapiPrefix + Convert.ToBase64String(encBytes);
            }
            catch { return ""; }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (entBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        internal static string UnprotectSecret(string stored, out bool wasLegacyPlaintext)
        {
            wasLegacyPlaintext = false;
            string trimmed = (stored ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed)) return "";
            if (!trimmed.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                wasLegacyPlaintext = true;
                return trimmed;
            }
            DATA_BLOB inBlob = new DATA_BLOB();
            DATA_BLOB entBlob = new DATA_BLOB();
            DATA_BLOB outBlob = new DATA_BLOB();
            try
            {
                byte[] encBytes = Convert.FromBase64String(trimmed.Substring(DpapiPrefix.Length));
                inBlob.cbData = encBytes.Length;
                inBlob.pbData = Marshal.AllocHGlobal(encBytes.Length);
                Marshal.Copy(encBytes, 0, inBlob.pbData, encBytes.Length);

                entBlob.cbData = DpapiEntropy.Length;
                entBlob.pbData = Marshal.AllocHGlobal(DpapiEntropy.Length);
                Marshal.Copy(DpapiEntropy, 0, entBlob.pbData, DpapiEntropy.Length);

                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, 0, out outBlob))
                    return "";

                byte[] plainBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
                return Encoding.UTF8.GetString(plainBytes).Trim();
            }
            catch { return ""; }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (entBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        public static RevitDirectAiConfig Load()
        {
            RevitDirectAiConfig cfg = new RevitDirectAiConfig();
            bool needsMigration = false;
            try
            {
                string path = ConfigFilePath;
                if (File.Exists(path))
                {
                    Dictionary<string, object> d = SimpleJson.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                    if (d != null)
                    {
                        bool lg, lc, lo;
                        cfg.Provider = GetStr(d, "provider", cfg.Provider);
                        if (cfg.Provider == "local") cfg.Provider = ProviderGemini;
                        cfg.GeminiApiKey = UnprotectSecret(GetStr(d, "gemini_api_key", ""), out lg);
                        cfg.GeminiModel = GetStr(d, "gemini_model", cfg.GeminiModel);
                        cfg.ClaudeApiKey = UnprotectSecret(GetStr(d, "claude_api_key", ""), out lc);
                        cfg.ClaudeModel = GetStr(d, "claude_model", cfg.ClaudeModel);
                        cfg.OpenAIApiKey = UnprotectSecret(GetStr(d, "openai_api_key", ""), out lo);
                        cfg.OpenAIModel = GetStr(d, "openai_model", cfg.OpenAIModel);
                        cfg.OpenAIBaseUrl = GetStr(d, "openai_base_url", cfg.OpenAIBaseUrl);
                        cfg.OpenAIApiMode = GetStr(d, "openai_api_mode", cfg.OpenAIApiMode);
                        cfg.VoiceAutoSend = GetBool(d, "voice_auto_send", cfg.VoiceAutoSend);
                        cfg.VoiceSpeakReplies = GetBool(d, "voice_speak_replies", cfg.VoiceSpeakReplies);
                        needsMigration = lg || lc || lo;
                    }
                }
            }
            catch { }

            if (needsMigration)
            {
                try { cfg.Save(); } catch { }
            }

            if (string.IsNullOrWhiteSpace(cfg.GeminiApiKey)) cfg.GeminiApiKey = ReadEnv("GEMINI_API_KEY");
            if (string.IsNullOrWhiteSpace(cfg.ClaudeApiKey)) cfg.ClaudeApiKey = ReadEnv("ANTHROPIC_API_KEY");
            if (string.IsNullOrWhiteSpace(cfg.OpenAIApiKey)) cfg.OpenAIApiKey = ReadEnv("OPENAI_API_KEY");

            string envProvider = ReadEnv("AI_ENGINEER_DIRECT_PROVIDER");
            if (!string.IsNullOrWhiteSpace(envProvider))
            {
                string ep = envProvider.Trim().ToLowerInvariant();
                if (ep == ProviderGemini || ep == ProviderClaude || ep == ProviderOpenAI)
                    cfg.Provider = ep;
            }
            if (string.IsNullOrWhiteSpace(cfg.GeminiModel) ||
                string.Equals(cfg.GeminiModel, "gemini-2.5-flash", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cfg.GeminiModel, "gemini-2.5-flash-lite", StringComparison.OrdinalIgnoreCase))
            {
                cfg.GeminiModel = "gemini-3.5-flash-lite";
            }
            if (string.IsNullOrWhiteSpace(cfg.ClaudeModel)) cfg.ClaudeModel = "claude-sonnet-4-20250514";
            if (string.IsNullOrWhiteSpace(cfg.OpenAIModel)) cfg.OpenAIModel = "gpt-5.3-codex";
            if (string.IsNullOrWhiteSpace(cfg.OpenAIBaseUrl)) cfg.OpenAIBaseUrl = "https://api.openai.com/v1";
            if (string.IsNullOrWhiteSpace(cfg.OpenAIApiMode)) cfg.OpenAIApiMode = "responses";
            return cfg;
        }

        public void Save()
        {
            string path = ConfigFilePath;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Preserve any extra AutoCAD-specific keys already in shared JSON file
            Dictionary<string, object> d = new Dictionary<string, object>();
            try
            {
                if (File.Exists(path))
                {
                    Dictionary<string, object> existing = SimpleJson.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                    if (existing != null) d = existing;
                }
            }
            catch { }

            d["provider"] = Provider ?? ProviderGemini;
            string encGem = ProtectSecret(GeminiApiKey);
            if (!string.IsNullOrEmpty(encGem) || string.IsNullOrWhiteSpace(GeminiApiKey))
                d["gemini_api_key"] = encGem;
            d["gemini_model"] = (GeminiModel ?? "gemini-3.5-flash-lite").Trim();

            string encClaude = ProtectSecret(ClaudeApiKey);
            if (!string.IsNullOrEmpty(encClaude) || string.IsNullOrWhiteSpace(ClaudeApiKey))
                d["claude_api_key"] = encClaude;
            d["claude_model"] = (ClaudeModel ?? "claude-sonnet-4-20250514").Trim();

            string encOpenAi = ProtectSecret(OpenAIApiKey);
            if (!string.IsNullOrEmpty(encOpenAi) || string.IsNullOrWhiteSpace(OpenAIApiKey))
                d["openai_api_key"] = encOpenAi;
            d["openai_model"] = (OpenAIModel ?? "gpt-5.3-codex").Trim();
            d["openai_base_url"] = (OpenAIBaseUrl ?? "https://api.openai.com/v1").Trim().TrimEnd('/');
            d["openai_api_mode"] = (OpenAIApiMode ?? "responses").Trim();
            d["voice_auto_send"] = VoiceAutoSend;
            d["voice_speak_replies"] = VoiceSpeakReplies;

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, SimpleJson.Serialize(d), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch { }
            }
            File.Copy(tmp, path, true);
            try { File.Delete(tmp); } catch { }
        }

        public string GetApiKey(string provider)
        {
            if (provider == ProviderGemini) return !string.IsNullOrWhiteSpace(GeminiApiKey) ? GeminiApiKey.Trim() : ReadEnv("GEMINI_API_KEY");
            if (provider == ProviderClaude) return !string.IsNullOrWhiteSpace(ClaudeApiKey) ? ClaudeApiKey.Trim() : ReadEnv("ANTHROPIC_API_KEY");
            if (provider == ProviderOpenAI) return !string.IsNullOrWhiteSpace(OpenAIApiKey) ? OpenAIApiKey.Trim() : ReadEnv("OPENAI_API_KEY");
            return "";
        }

        public string GetModel(string provider)
        {
            if (provider == ProviderGemini) return string.IsNullOrWhiteSpace(GeminiModel) ? "gemini-3.5-flash-lite" : GeminiModel.Trim();
            if (provider == ProviderClaude) return string.IsNullOrWhiteSpace(ClaudeModel) ? "claude-sonnet-4-20250514" : ClaudeModel.Trim();
            if (provider == ProviderOpenAI) return string.IsNullOrWhiteSpace(OpenAIModel) ? "gpt-5.3-codex" : OpenAIModel.Trim();
            return "";
        }

        public void SetModel(string provider, string model)
        {
            string m = (model ?? "").Trim();
            if (string.IsNullOrEmpty(m)) return;
            if (provider == ProviderGemini) GeminiModel = m;
            else if (provider == ProviderClaude) ClaudeModel = m;
            else if (provider == ProviderOpenAI) OpenAIModel = m;
        }

        public static string[] GetModelPresets(string provider)
        {
            if (provider == ProviderGemini)
                return new string[] { "gemini-3.5-flash-lite", "gemini-3.5-flash", "gemini-3.5-pro", "gemini-2.5-flash", "gemini-2.5-pro" };
            if (provider == ProviderClaude)
                return new string[] { "claude-sonnet-4-20250514", "claude-opus-4-20250514", "claude-3-7-sonnet-20250219" };
            if (provider == ProviderOpenAI)
                return new string[] { "gpt-5.3-codex", "gpt-5", "gpt-4.1", "o4-mini", "gpt-4o" };
            return new string[0];
        }

        private static string ReadEnv(string name)
        {
            string v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            return !string.IsNullOrWhiteSpace(v) ? v.Trim() : "";
        }

        private static string GetStr(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                string s = v.ToString().Trim();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return def;
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            return (d.TryGetValue(key, out v) && v is bool) ? (bool)v : def;
        }
    }

    /// <summary>
    /// Deadlock-free WinMM WAV recorder (CALLBACK_EVENT + dedicated background reader thread).
    /// </summary>
    internal sealed class RevitWaveInRecorder : IDisposable
    {
        private const int SampleRate = 16000;
        private const short BitsPerSample = 16;
        private const short Channels = 1;
        private const int BufferCount = 4;
        private const int BufferSize = 3200;
        private const uint CALLBACK_EVENT = 0x00050000;
        private const uint WAVE_MAPPER = unchecked((uint)-1);
        private const uint WHDR_DONE = 0x00000001;

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

        [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hwi);
        [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hwi);
        [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hwi);

        private readonly object _lock = new object();
        private IntPtr _hWaveIn = IntPtr.Zero;
        private AutoResetEvent _dataEvent;
        private Thread _readerThread;
        private byte[][] _rawBuffers;
        private GCHandle[] _bufferPins;
        private IntPtr[] _headerPtrs;
        private MemoryStream _pcmStream;
        private volatile bool _recording;
        private volatile bool _stopping;
        private DateTime _startedUtc;

        public bool IsRecording { get { return _recording; } }
        public int ElapsedSeconds { get { return _recording ? Math.Max(0, (int)(DateTime.UtcNow - _startedUtc).TotalSeconds) : 0; } }

        public void Start()
        {
            if (_recording) return;
            if (waveInGetNumDevs() == 0)
                throw new InvalidOperationException("لم يتم العثور على ميكروفون متصل في الجهاز (No microphone found).");

            WAVEFORMATEX fmt = new WAVEFORMATEX
            {
                wFormatTag = 1,
                nChannels = (ushort)Channels,
                nSamplesPerSec = (uint)SampleRate,
                wBitsPerSample = (ushort)BitsPerSample,
                nBlockAlign = (ushort)(Channels * (BitsPerSample / 8)),
                nAvgBytesPerSec = (uint)(SampleRate * Channels * (BitsPerSample / 8)),
                cbSize = 0
            };

            _pcmStream = new MemoryStream();
            _stopping = false;
            _dataEvent = new AutoResetEvent(false);

            int openRes = waveInOpen(out _hWaveIn, WAVE_MAPPER, ref fmt, _dataEvent.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
            if (openRes != 0 || _hWaveIn == IntPtr.Zero)
            {
                CleanupDevice();
                throw new InvalidOperationException("تعذر فتح الميكروفون (waveInOpen=" + openRes + ").");
            }

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
                        dwUser = (IntPtr)i
                    };
                    IntPtr ptr = Marshal.AllocHGlobal(hdrSize);
                    Marshal.StructureToPtr(hdr, ptr, false);
                    _headerPtrs[i] = ptr;
                    if (waveInPrepareHeader(_hWaveIn, ptr, hdrSize) != 0) throw new InvalidOperationException("waveInPrepareHeader failed.");
                    if (waveInAddBuffer(_hWaveIn, ptr, hdrSize) != 0) throw new InvalidOperationException("waveInAddBuffer failed.");
                }
                if (waveInStart(_hWaveIn) != 0) throw new InvalidOperationException("waveInStart failed.");
                _startedUtc = DateTime.UtcNow;
                _recording = true;
                _readerThread = new Thread(ReaderLoop);
                _readerThread.IsBackground = true;
                _readerThread.Name = "AIEngineer.Revit.WaveInReader";
                _readerThread.Start();
            }
            catch
            {
                CleanupDevice();
                throw;
            }
        }

        private void ReaderLoop()
        {
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            while (!_stopping)
            {
                AutoResetEvent ev = _dataEvent;
                if (ev == null) break;
                try { ev.WaitOne(100); } catch { break; }
                DrainCompletedBuffers(hdrSize, !_stopping);
            }
            DrainCompletedBuffers(hdrSize, false);
        }

        private void DrainCompletedBuffers(int hdrSize, bool requeue)
        {
            IntPtr[] ptrs = _headerPtrs;
            IntPtr handle = _hWaveIn;
            if (ptrs == null) return;
            for (int i = 0; i < ptrs.Length; i++)
            {
                IntPtr ptr = ptrs[i];
                if (ptr == IntPtr.Zero) continue;
                try
                {
                    WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(ptr, typeof(WAVEHDR));
                    if ((hdr.dwFlags & WHDR_DONE) == 0) continue;
                    if (hdr.dwBytesRecorded > 0 && hdr.lpData != IntPtr.Zero)
                    {
                        int n = (int)Math.Min(hdr.dwBytesRecorded, (uint)BufferSize);
                        byte[] chunk = new byte[n];
                        Marshal.Copy(hdr.lpData, chunk, 0, n);
                        lock (_lock) { if (_pcmStream != null) _pcmStream.Write(chunk, 0, n); }
                    }
                    hdr.dwBytesRecorded = 0;
                    hdr.dwFlags &= ~WHDR_DONE;
                    Marshal.StructureToPtr(hdr, ptr, false);
                    if (requeue && !_stopping && handle != IntPtr.Zero)
                        waveInAddBuffer(handle, ptr, hdrSize);
                }
                catch { }
            }
        }

        public byte[] StopAndGetWavBytes(out double durationSec, out int peakAmplitude)
        {
            durationSec = 0.0;
            peakAmplitude = 0;
            if (!_recording && _hWaveIn == IntPtr.Zero) return new byte[0];
            _stopping = true;
            _recording = false;
            CleanupDevice();

            byte[] pcm;
            lock (_lock)
            {
                pcm = _pcmStream != null ? _pcmStream.ToArray() : new byte[0];
                if (_pcmStream != null) { _pcmStream.Dispose(); _pcmStream = null; }
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
                bw.Write(16);
                bw.Write((short)1);
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

        private readonly object _cleanupLock = new object();

        private void CleanupDevice()
        {
            lock (_cleanupLock)
            {
                _stopping = true;
                IntPtr handle = _hWaveIn;
                if (handle != IntPtr.Zero) { try { waveInReset(handle); } catch { } }
                AutoResetEvent ev = _dataEvent;
                if (ev != null) { try { ev.Set(); } catch { } }
                Thread reader = _readerThread;
                _readerThread = null;
                bool readerStopped = true;
                if (reader != null && reader.IsAlive && reader != Thread.CurrentThread)
                {
                    try { readerStopped = reader.Join(1500); } catch { readerStopped = false; }
                }
                _hWaveIn = IntPtr.Zero;

                if (handle != IntPtr.Zero)
                {
                    int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
                    if (_headerPtrs != null)
                    {
                        for (int i = 0; i < _headerPtrs.Length; i++)
                        {
                            IntPtr ptr = _headerPtrs[i];
                            if (ptr != IntPtr.Zero)
                            {
                                try { waveInUnprepareHeader(handle, ptr, hdrSize); } catch { }
                                if (readerStopped)
                                {
                                    try { Marshal.FreeHGlobal(ptr); } catch { }
                                    _headerPtrs[i] = IntPtr.Zero;
                                }
                            }
                        }
                    }
                    try { waveInClose(handle); } catch { }
                }

                if (readerStopped)
                {
                    if (_bufferPins != null)
                    {
                        for (int i = 0; i < _bufferPins.Length; i++)
                            if (_bufferPins[i].IsAllocated) { try { _bufferPins[i].Free(); } catch { } }
                    }
                    if (ev != null) { _dataEvent = null; try { ev.Dispose(); } catch { } }
                    _rawBuffers = null; _bufferPins = null; _headerPtrs = null;
                }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _recording = false;
            CleanupDevice();
            lock (_lock) { if (_pcmStream != null) { try { _pcmStream.Dispose(); } catch { } _pcmStream = null; } }
        }
    }

    internal static class RevitVoiceTranscriber
    {
        private static readonly HttpClient _http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
            HttpClient c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(90);
            return c;
        }

        public static string TranscribeWav(RevitDirectAiConfig cfg, byte[] wavBytes, bool preferArabic)
        {
            if (wavBytes == null || wavBytes.Length <= 44)
                throw new InvalidOperationException(preferArabic ? "لم يتم تسجيل أي بيانات صوتية." : "No audio recorded.");
            if (cfg == null) cfg = RevitDirectAiConfig.Load();

            string gKey = cfg.GetApiKey(RevitDirectAiConfig.ProviderGemini);
            string oKey = cfg.GetApiKey(RevitDirectAiConfig.ProviderOpenAI);
            List<string> errors = new List<string>();

            if (!string.IsNullOrWhiteSpace(gKey))
            {
                try { return TranscribeWithGemini(gKey, cfg.GetModel(RevitDirectAiConfig.ProviderGemini), wavBytes); }
                catch (Exception ex) { errors.Add("Gemini: " + ex.Message); }
            }
            if (!string.IsNullOrWhiteSpace(oKey))
            {
                try { return TranscribeWithOpenAI(oKey, cfg.OpenAIBaseUrl, wavBytes); }
                catch (Exception ex) { errors.Add("OpenAI: " + ex.Message); }
            }
            if (string.IsNullOrWhiteSpace(gKey) && string.IsNullOrWhiteSpace(oKey))
                throw new InvalidOperationException(preferArabic ? "أضف مفتاح Gemini أو OpenAI من [🔑 API] لتفعيل الصوت." : "Configure Gemini or OpenAI API key in [🔑 API] for voice.");
            throw new InvalidOperationException(string.Join(" | ", errors.ToArray()));
        }

        private static string TranscribeWithGemini(string apiKey, string model, byte[] wavBytes)
        {
            List<string> models = new List<string>();
            string[] candsList = new string[] { model, "gemini-2.5-flash", "gemini-3.5-flash-lite" };
            for (int c = 0; c < candsList.Length; c++)
            {
                string cm = (candsList[c] ?? "").Trim();
                if (!string.IsNullOrEmpty(cm) && !models.Contains(cm)) models.Add(cm);
            }
            string b64 = Convert.ToBase64String(wavBytes);
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                { "contents", new object[] {
                    new Dictionary<string, object> {
                        { "role", "user" },
                        { "parts", new object[] {
                            new Dictionary<string, object> { { "inlineData", new Dictionary<string, object> { { "mimeType", "audio/wav" }, { "data", b64 } } } },
                            new Dictionary<string, object> { { "text", "Transcribe this Revit BIM/MEP electrical engineer voice recording verbatim. Keep Arabic words in Arabic script and technical English terms (Revit, Family, Panel, Circuit, Lux, DIALux, Excel, BOQ, kW, mm2, Conduit, Cable Tray) in English. Return ONLY the clean transcription." } }
                        } }
                    }
                } },
                { "generationConfig", new Dictionary<string, object> { { "temperature", 0.0 }, { "maxOutputTokens", 1024 } } }
            };
            string payload = SimpleJson.Serialize(body);
            string lastErr = "Gemini STT failed.";
            for (int i = 0; i < models.Count; i++)
            {
                string url = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(models[i]) + ":generateContent";
                try
                {
                    using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey.Trim());
                        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                        using (HttpResponseMessage resp = _http.SendAsync(req).GetAwaiter().GetResult())
                        {
                            string raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            if (!resp.IsSuccessStatusCode) { lastErr = "HTTP " + (int)resp.StatusCode; continue; }
                            Dictionary<string, object> doc = SimpleJson.DeserializeObject(raw);
                            object[] cands = (doc != null && doc.ContainsKey("candidates")) ? doc["candidates"] as object[] : null;
                            if (cands != null && cands.Length > 0)
                            {
                                Dictionary<string, object> c0 = cands[0] as Dictionary<string, object>;
                                Dictionary<string, object> content = (c0 != null && c0.ContainsKey("content")) ? c0["content"] as Dictionary<string, object> : null;
                                object[] parts = (content != null && content.ContainsKey("parts")) ? content["parts"] as object[] : null;
                                if (parts != null)
                                {
                                    StringBuilder sb = new StringBuilder();
                                    for (int p = 0; p < parts.Length; p++)
                                    {
                                        Dictionary<string, object> pd = parts[p] as Dictionary<string, object>;
                                        if (pd == null) continue;
                                        if (pd.ContainsKey("thought") && pd["thought"] is bool && (bool)pd["thought"]) continue;
                                        if (pd.ContainsKey("text") && pd["text"] != null) sb.Append(pd["text"].ToString());
                                    }
                                    return sb.ToString().Trim();
                                }
                            }
                            return "";
                        }
                    }
                }
                catch (Exception ex) { lastErr = ex.Message; }
            }
            throw new InvalidOperationException(lastErr);
        }

        private static string TranscribeWithOpenAI(string apiKey, string baseUrl, byte[] wavBytes)
        {
            string b = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim().TrimEnd('/');
            string url = b + "/audio/transcriptions";
            string[] models = new string[] { "gpt-4o-mini-transcribe", "whisper-1" };
            string lastErr = "OpenAI STT failed.";
            for (int i = 0; i < models.Length; i++)
            {
                try
                {
                    using (MultipartFormDataContent form = new MultipartFormDataContent())
                    {
                        ByteArrayContent fc = new ByteArrayContent(wavBytes);
                        fc.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                        form.Add(fc, "file", "voice.wav");
                        form.Add(new StringContent(models[i]), "model");
                        using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url))
                        {
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                            req.Content = form;
                            using (HttpResponseMessage resp = _http.SendAsync(req).GetAwaiter().GetResult())
                            {
                                string raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                if (!resp.IsSuccessStatusCode) { lastErr = "HTTP " + (int)resp.StatusCode; continue; }
                                Dictionary<string, object> doc = SimpleJson.DeserializeObject(raw);
                                return (doc != null && doc.ContainsKey("text") && doc["text"] != null) ? doc["text"].ToString().Trim() : "";
                            }
                        }
                    }
                }
                catch (Exception ex) { lastErr = ex.Message; }
            }
            throw new InvalidOperationException(lastErr);
        }
    }

    internal sealed class RevitExcelSheet
    {
        public string Title = "";
        public string SheetName = "Sheet1";
        public List<string> Headers = new List<string>();
        public List<List<string>> Rows = new List<List<string>>();
        public List<string> FooterRow = null;
    }

    /// <summary>
    /// Host-agnostic OpenXML (.xlsx) and UTF-8 CSV writer with Arabic RTL support and zero COM/Excel dependency.
    /// </summary>
    internal static class RevitExcelExporter
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string ResolveDefaultPath(string docPathOrTitle, string tag, string ext)
        {
            string cleanExt = string.IsNullOrWhiteSpace(ext) ? ".xlsx" : (ext.StartsWith(".") ? ext : "." + ext);
            string dir = null;
            string baseName = "RevitModel";
            if (!string.IsNullOrWhiteSpace(docPathOrTitle))
            {
                try
                {
                    baseName = Sanitize(Path.GetFileNameWithoutExtension(docPathOrTitle));
                    if (Path.IsPathRooted(docPathOrTitle) && File.Exists(docPathOrTitle))
                        dir = Path.GetDirectoryName(docPathOrTitle);
                }
                catch { }
            }
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AI-Engineer-Exports");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, baseName + "_" + Sanitize(tag) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + cleanExt);
        }

        public static string WriteXlsx(string targetPath, RevitExcelSheet sheet)
        {
            string full = Path.GetFullPath(targetPath);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (full.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                return WriteCsv(full, sheet);
            if (!full.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) full += ".xlsx";

            bool rtl = SheetHasArabic(sheet);
            string safeSheet = SanitizeSheetName(sheet.SheetName);

            using (FileStream fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create, false, Utf8NoBom))
            {
                WriteEntry(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                    "</Types>");
                WriteEntry(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");
                WriteEntry(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                    "</Relationships>");
                WriteEntry(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"" + XmlEsc(safeSheet) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                WriteEntry(zip, "xl/styles.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font></fonts>" +
                    "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1E3A5F\"/></patternFill></fill></fills>" +
                    "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style=\"thin\"/><right style=\"thin\"/><top style=\"thin\"/><bottom style=\"thin\"/><diagonal/></border></borders>" +
                    "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                    "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/></cellXfs>" +
                    "</styleSheet>");

                StringBuilder ws = new StringBuilder();
                ws.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                ws.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                ws.Append("<sheetViews><sheetView workbookViewId=\"0\"").Append(rtl ? " rightToLeft=\"1\"" : "").Append("/></sheetViews>");
                ws.Append("<sheetData>");
                int rIdx = 1;
                if (!string.IsNullOrWhiteSpace(sheet.Title))
                {
                    AppendRow(ws, rIdx++, new List<string> { sheet.Title }, 1);
                }
                if (sheet.Headers != null && sheet.Headers.Count > 0)
                    AppendRow(ws, rIdx++, sheet.Headers, 1);
                if (sheet.Rows != null)
                {
                    for (int i = 0; i < sheet.Rows.Count; i++)
                        AppendRow(ws, rIdx++, sheet.Rows[i], 0);
                }
                if (sheet.FooterRow != null && sheet.FooterRow.Count > 0)
                    AppendRow(ws, rIdx++, sheet.FooterRow, 1);
                ws.Append("</sheetData></worksheet>");
                WriteEntry(zip, "xl/worksheets/sheet1.xml", ws.ToString());
            }
            return full;
        }

        private static string WriteCsv(string full, RevitExcelSheet sheet)
        {
            StringBuilder sb = new StringBuilder();
            if (sheet.Headers != null && sheet.Headers.Count > 0) sb.AppendLine(CsvRow(sheet.Headers));
            if (sheet.Rows != null) for (int i = 0; i < sheet.Rows.Count; i++) sb.AppendLine(CsvRow(sheet.Rows[i]));
            if (sheet.FooterRow != null && sheet.FooterRow.Count > 0) sb.AppendLine(CsvRow(sheet.FooterRow));
            File.WriteAllText(full, sb.ToString(), new UTF8Encoding(true));
            return full;
        }

        private static string CsvRow(List<string> cols)
        {
            string[] arr = new string[cols.Count];
            for (int i = 0; i < cols.Count; i++)
            {
                string s = cols[i] ?? "";
                arr[i] = (s.Contains(",") || s.Contains("\"") || s.Contains("\n")) ? ("\"" + s.Replace("\"", "\"\"") + "\"") : s;
            }
            return string.Join(",", arr);
        }

        private static void AppendRow(StringBuilder ws, int rowIndex, List<string> vals, int styleId)
        {
            ws.Append("<row r=\"").Append(rowIndex).Append("\">");
            for (int c = 0; c < vals.Count; c++)
            {
                string cellRef = ColName(c + 1) + rowIndex.ToString(CultureInfo.InvariantCulture);
                string val = vals[c] ?? "";
                double num;
                if (styleId == 0 && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out num) && !double.IsNaN(num) && !double.IsInfinity(num))
                {
                    ws.Append("<c r=\"").Append(cellRef).Append("\" s=\"0\"><v>").Append(num.ToString("R", CultureInfo.InvariantCulture)).Append("</v></c>");
                }
                else
                {
                    ws.Append("<c r=\"").Append(cellRef).Append("\" t=\"inlineStr\" s=\"").Append(styleId).Append("\"><is><t>").Append(XmlEsc(val)).Append("</t></is></c>");
                }
            }
            ws.Append("</row>");
        }

        private static string ColName(int idx)
        {
            string s = "";
            while (idx > 0) { int m = (idx - 1) % 26; s = (char)('A' + m) + s; idx = (idx - m) / 26; }
            return s;
        }

        private static void WriteEntry(ZipArchive zip, string path, string content)
        {
            ZipArchiveEntry e = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (Stream s = e.Open())
            using (StreamWriter sw = new StreamWriter(s, Utf8NoBom))
                sw.Write(content);
        }

        private static bool SheetHasArabic(RevitExcelSheet sheet)
        {
            if (sheet == null) return false;
            if (HasArabic(sheet.Title)) return true;
            if (sheet.Headers != null)
            {
                for (int i = 0; i < sheet.Headers.Count; i++)
                    if (HasArabic(sheet.Headers[i])) return true;
            }
            if (sheet.Rows != null)
            {
                for (int r = 0; r < sheet.Rows.Count; r++)
                {
                    List<string> row = sheet.Rows[r];
                    if (row == null) continue;
                    for (int c = 0; c < row.Count; c++)
                        if (HasArabic(row[c])) return true;
                }
            }
            if (sheet.FooterRow != null)
            {
                for (int i = 0; i < sheet.FooterRow.Count; i++)
                    if (HasArabic(sheet.FooterRow[i])) return true;
            }
            return false;
        }

        private static bool HasArabic(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= '\u0600' && c <= '\u06FF') || (c >= '\u0750' && c <= '\u077F') ||
                    (c >= '\u08A0' && c <= '\u08FF') || (c >= '\uFB50' && c <= '\uFEFF'))
                    return true;
            }
            return false;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "export";
            string clean = Regex.Replace(s.Trim(), @"[^A-Za-z0-9_\-\u0600-\u06FF]+", "_").Trim('_');
            return string.IsNullOrEmpty(clean) ? "export" : clean;
        }

        private static string SanitizeSheetName(string s)
        {
            string clean = Regex.Replace(string.IsNullOrWhiteSpace(s) ? "Sheet1" : s, @"[\[\]\*\?\/\\:]", "_").Trim();
            if (string.IsNullOrEmpty(clean)) clean = "Sheet1";
            return clean.Length > 31 ? clean.Substring(0, 31) : clean;
        }

        private static string XmlEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // Strip XML 1.0 illegal control characters
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
