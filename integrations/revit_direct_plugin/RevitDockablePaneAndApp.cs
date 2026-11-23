using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AIEngineer.Revit.Direct
{
    /// <summary>
    /// Thread-safe bridge marshaling background AI tool calls onto Revit's Main UI Thread via ExternalEvent.
    /// </summary>
    internal sealed class RevitToolRequest
    {
        public string ToolName;
        public Dictionary<string, object> Args;
        public TaskCompletionSource<Dictionary<string, object>> Completion;
    }

    internal sealed class RevitExternalEventDispatcher : IExternalEventHandler
    {
        private readonly object _queueLock = new object();
        private readonly Queue<RevitToolRequest> _queue = new Queue<RevitToolRequest>();
        private ExternalEvent _externalEvent;

        public void Initialize()
        {
            if (_externalEvent == null)
                _externalEvent = ExternalEvent.Create(this);
        }

        public Task<Dictionary<string, object>> EnqueueAsync(string toolName, Dictionary<string, object> args)
        {
            TaskCompletionSource<Dictionary<string, object>> tcs = new TaskCompletionSource<Dictionary<string, object>>();
            if (_externalEvent == null)
            {
                tcs.SetResult(RevitToolExecutor.Err("Revit ExternalEvent dispatcher is not initialized."));
                return tcs.Task;
            }
            RevitToolRequest req = new RevitToolRequest { ToolName = toolName, Args = args, Completion = tcs };
            lock (_queueLock)
            {
                _queue.Enqueue(req);
            }
            ExternalEventRequest reqStatus = _externalEvent.Raise();
            if (reqStatus != ExternalEventRequest.Accepted && reqStatus != ExternalEventRequest.Pending)
            {
                tcs.TrySetResult(RevitToolExecutor.Err("Revit rejected ExternalEvent (" + reqStatus + "). Exit active Revit modal dialogs and retry."));
            }
            return tcs.Task;
        }

        public void Execute(UIApplication app)
        {
            while (true)
            {
                RevitToolRequest req = null;
                lock (_queueLock)
                {
                    if (_queue.Count > 0) req = _queue.Dequeue();
                }
                if (req == null) break;
                if (req.Completion.Task.IsCompleted) continue; // Skip previously rejected/canceled requests
                try
                {
                    Dictionary<string, object> res = RevitToolExecutor.ExecuteTool(app, req.ToolName, req.Args);
                    req.Completion.TrySetResult(res);
                }
                catch (Exception ex)
                {
                    req.Completion.TrySetResult(RevitToolExecutor.Err(ex.Message));
                }
            }
        }

        public string GetName() { return "AI-Engineer Direct Revit Tool Dispatcher"; }
    }

    /// <summary>
    /// Clean single-pane Dark-Theme WPF DockablePane inside Revit (identical workflow to AutoCAD Direct AI Chat).
    /// </summary>
    internal sealed class RevitCopilotDockablePage : Page, IDockablePaneProvider
    {
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("F4C2A9B1-7D3E-4E88-9A15-6B82C1D4E709"));

        private readonly RevitExternalEventDispatcher _dispatcher;
        private readonly DirectRevitAgentSession _session = new DirectRevitAgentSession();
        private readonly RevitWaveInRecorder _voiceRecorder = new RevitWaveInRecorder();
        private readonly DispatcherTimer _voiceTimer = new DispatcherTimer();
        private RevitDirectAiConfig _config;

        private readonly ComboBox _providerCombo = new ComboBox();
        private readonly ComboBox _modelCombo = new ComboBox();
        private readonly ComboBox _langCombo = new ComboBox();
        private readonly Button _apiBtn = new Button();
        private readonly Button _clearBtn = new Button();
        private readonly TextBlock _statusBanner = new TextBlock();
        private readonly TextBox _transcript = new TextBox();
        private readonly WrapPanel _quickChips = new WrapPanel();
        private readonly TextBox _inputBox = new TextBox();
        private readonly Button _sendBtn = new Button();
        private readonly Button _voiceBtn = new Button();
        private readonly Button _approveBtn = new Button();
        private readonly Button _undoBtn = new Button();
        private readonly TextBlock _footerStatus = new TextBlock();

        private bool _busy;
        private bool _suppressEvents;

        public RevitCopilotDockablePage(RevitExternalEventDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _config = RevitDirectAiConfig.Load();
            BuildUi();
            SyncProviderUi();
            ApplyLanguage();
        }

        private bool IsArabic { get { return _langCombo.SelectedIndex == 0; } }

        private string SelectedProvider
        {
            get
            {
                if (_providerCombo.SelectedIndex == 1) return RevitDirectAiConfig.ProviderClaude;
                if (_providerCombo.SelectedIndex == 2) return RevitDirectAiConfig.ProviderOpenAI;
                return RevitDirectAiConfig.ProviderGemini;
            }
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
        }

        private void BuildUi()
        {
            SolidColorBrush bgMain = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 37, 42));
            SolidColorBrush bgSurf = new SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 48, 56));
            SolidColorBrush bgInput = new SolidColorBrush(System.Windows.Media.Color.FromRgb(26, 29, 33));
            SolidColorBrush fgMain = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 237, 243));
            SolidColorBrush fgMuted = new SolidColorBrush(System.Windows.Media.Color.FromRgb(155, 166, 178));
            SolidColorBrush borderBr = new SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 70, 82));

            Background = bgMain;
            Foreground = fgMain;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12.5;

            System.Windows.Controls.Grid root = new System.Windows.Controls.Grid();
            root.Margin = new Thickness(8);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Status banner
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Chat
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Quick chips
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) }); // Input
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Actions
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer

            // Row 0: Header controls
            WrapPanel header = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            _providerCombo.Items.Add("Gemini Direct");
            _providerCombo.Items.Add("Claude Direct");
            _providerCombo.Items.Add("OpenAI Direct");
            _providerCombo.Width = 115;
            _providerCombo.Margin = new Thickness(2);
            _providerCombo.SelectionChanged += delegate
            {
                if (_suppressEvents) return;
                _config.Provider = SelectedProvider;
                _config.Save();
                PopulateModels();
                UpdateStatusBanner();
            };

            _modelCombo.IsEditable = true;
            _modelCombo.Width = 145;
            _modelCombo.Margin = new Thickness(2);
            _modelCombo.LostFocus += delegate
            {
                if (!_suppressEvents && !string.IsNullOrWhiteSpace(_modelCombo.Text))
                {
                    _config.SetModel(SelectedProvider, _modelCombo.Text);
                    _config.Save();
                }
            };

            _langCombo.Items.Add("العربية");
            _langCombo.Items.Add("English");
            _langCombo.SelectedIndex = 0;
            _langCombo.Width = 72;
            _langCombo.Margin = new Thickness(2);
            _langCombo.SelectionChanged += delegate { ApplyLanguage(); };

            StyleBtn(_apiBtn, "🔑 API", bgSurf, fgMain, borderBr);
            _apiBtn.Click += delegate { OpenApiSettingsDialog(); };

            StyleBtn(_clearBtn, "+ جديد", bgSurf, fgMain, borderBr);
            _clearBtn.Click += delegate
            {
                _session.ClearHistory();
                _transcript.Clear();
                _footerStatus.Text = IsArabic ? "بدأت محادثة جديدة." : "Started new chat.";
            };

            header.Children.Add(_providerCombo);
            header.Children.Add(_modelCombo);
            header.Children.Add(_langCombo);
            header.Children.Add(_apiBtn);
            header.Children.Add(_clearBtn);
            System.Windows.Controls.Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Row 1: Status banner
            _statusBanner.TextWrapping = TextWrapping.Wrap;
            _statusBanner.Margin = new Thickness(2, 2, 2, 6);
            _statusBanner.FontSize = 11.5;
            System.Windows.Controls.Grid.SetRow(_statusBanner, 1);
            root.Children.Add(_statusBanner);

            // Row 2: Chat transcript
            _transcript.IsReadOnly = true;
            _transcript.TextWrapping = TextWrapping.Wrap;
            _transcript.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _transcript.Background = bgInput;
            _transcript.Foreground = fgMain;
            _transcript.BorderBrush = borderBr;
            _transcript.Padding = new Thickness(8);
            System.Windows.Controls.Grid.SetRow(_transcript, 2);
            root.Children.Add(_transcript);

            // Row 3: Quick editable prompt chips
            _quickChips.Margin = new Thickness(0, 5, 0, 4);
            AddQuickChip("💡 إنارة", "💡 Lighting",
                "افحص الغرف ووزّع الإنارة حسب الكود المصري (ECP) وصدّر الجدول لإكسيل وملف DIALux .stf",
                "Inspect rooms, distribute lighting per ECP, and export Excel + DIALux .stf", bgSurf, fgMain, borderBr);
            AddQuickChip("⚡ كابل/قاطع", "⚡ LV Calc",
                "احسب الكابل والقاطع وهبوط الجهد وقصر الدائرة لحمل 3 فاز 25 ك.وات طول 45 متر نحاس XLPE عند 40 درجة وصدّر لإكسيل",
                "Calculate LV cable, breaker, voltage drop, PE & short-circuit for 3P 25kW, 45m Cu/XLPE at 40C and export Excel", bgSurf, fgMain, borderBr);
            AddQuickChip("🔌 أفياش", "🔌 Outlets",
                "وزّع الأفياش في الغرف على ارتفاع 0.30 متر بمسافة 3.5 متر بلون معاينة أحمر",
                "Place electrical outlets along room walls at 0.30m height with 3.5m spacing in red preview", bgSurf, fgMain, borderBr);
            AddQuickChip("📐 حصر BOQ", "📐 BOQ",
                "اعمل حصر كميات MEP شامل (الكشافات والأفياش واللوحات وأطوال المواسير وحوامل الكابلات) مع هالك 10% وصدّر لإكسيل",
                "Run full BIM MEP BOQ quantity takeoff with 10% waste and export to Excel", bgSurf, fgMain, borderBr);
            AddQuickChip("📤 DIALux", "📤 DIALux",
                "صدّر الغرف والكشافات الحالية في الموديل إلى ملف DIALux .stf",
                "Export current Revit rooms and lighting fixtures to DIALux .stf", bgSurf, fgMain, borderBr);
            System.Windows.Controls.Grid.SetRow(_quickChips, 3);
            root.Children.Add(_quickChips);

            // Row 4: Input box
            _inputBox.AcceptsReturn = true;
            _inputBox.TextWrapping = TextWrapping.Wrap;
            _inputBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _inputBox.Background = bgInput;
            _inputBox.Foreground = fgMain;
            _inputBox.CaretBrush = fgMain;
            _inputBox.BorderBrush = borderBr;
            _inputBox.Padding = new Thickness(6);
            _inputBox.PreviewKeyDown += async delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    e.Handled = true;
                    await SendPromptAsync();
                }
            };
            System.Windows.Controls.Grid.SetRow(_inputBox, 4);
            root.Children.Add(_inputBox);

            // Row 5: Action buttons
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 5, 0, 2) };
            StyleBtn(_sendBtn, "اسأل AI", new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)), Brushes.White, borderBr);
            _sendBtn.Click += async delegate { await SendPromptAsync(); };

            StyleBtn(_voiceBtn, "🎤 صوت", bgSurf, fgMain, borderBr);
            _voiceBtn.Click += async delegate { await ToggleVoiceAsync(); };

            StyleBtn(_approveBtn, "✅ اعتماد (تمام)", new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 101, 52)), Brushes.White, borderBr);
            _approveBtn.Click += async delegate { await RunDirectButtonToolAsync("approve_or_rollback_preview", new Dictionary<string, object> { { "action", "approve" } }); };

            StyleBtn(_undoBtn, "↩️ تراجع", new SolidColorBrush(System.Windows.Media.Color.FromRgb(153, 27, 27)), Brushes.White, borderBr);
            _undoBtn.Click += async delegate { await RunDirectButtonToolAsync("undo_last_action", new Dictionary<string, object> { { "steps", 1 } }); };

            actions.Children.Add(_sendBtn);
            actions.Children.Add(_voiceBtn);
            actions.Children.Add(_approveBtn);
            actions.Children.Add(_undoBtn);
            System.Windows.Controls.Grid.SetRow(actions, 5);
            root.Children.Add(actions);

            // Row 6: Footer status
            _footerStatus.Foreground = fgMuted;
            _footerStatus.FontSize = 11;
            _footerStatus.Margin = new Thickness(2, 3, 2, 0);
            _footerStatus.TextWrapping = TextWrapping.Wrap;
            System.Windows.Controls.Grid.SetRow(_footerStatus, 6);
            root.Children.Add(_footerStatus);

            _voiceTimer.Interval = TimeSpan.FromMilliseconds(350);
            _voiceTimer.Tick += delegate
            {
                if (_voiceRecorder.IsRecording)
                    _voiceBtn.Content = (IsArabic ? "⏹ إيقاف (" : "⏹ Stop (") + _voiceRecorder.ElapsedSeconds + "s)";
                else
                    _voiceTimer.Stop();
            };

            Content = root;
        }

        private void AddQuickChip(string arLabel, string enLabel, string arPrompt, string enPrompt, Brush bg, Brush fg, Brush border)
        {
            Button b = new Button();
            StyleBtn(b, arLabel, bg, fg, border);
            b.Tag = new string[] { arLabel, enLabel, arPrompt, enPrompt };
            b.Click += delegate
            {
                string[] meta = (string[])b.Tag;
                _inputBox.Text = IsArabic ? meta[2] : meta[3];
                _inputBox.Focus();
                _inputBox.CaretIndex = _inputBox.Text.Length;
                _footerStatus.Text = IsArabic ? "تم تعبئة الأمر — عدّله كما تحب واضغط Enter." : "Prompt inserted — edit and press Enter.";
            };
            _quickChips.Children.Add(b);
        }

        private static void StyleBtn(Button b, string text, Brush bg, Brush fg, Brush border)
        {
            b.Content = text;
            b.Background = bg;
            b.Foreground = fg;
            b.BorderBrush = border;
            b.Padding = new Thickness(8, 3, 8, 3);
            b.Margin = new Thickness(2);
            b.Cursor = Cursors.Hand;
        }

        private void SyncProviderUi()
        {
            _suppressEvents = true;
            try
            {
                string p = (_config.Provider ?? RevitDirectAiConfig.ProviderGemini).ToLowerInvariant();
                _providerCombo.SelectedIndex = p == RevitDirectAiConfig.ProviderClaude ? 1 : (p == RevitDirectAiConfig.ProviderOpenAI ? 2 : 0);
                PopulateModels();
            }
            finally { _suppressEvents = false; }
        }

        private void PopulateModels()
        {
            _modelCombo.Items.Clear();
            foreach (string m in RevitDirectAiConfig.GetModelPresets(SelectedProvider)) _modelCombo.Items.Add(m);
            _modelCombo.Text = _config.GetModel(SelectedProvider);
        }

        private void ApplyLanguage()
        {
            FlowDirection = IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            _sendBtn.Content = IsArabic ? "اسأل AI" : "Ask AI";
            _clearBtn.Content = IsArabic ? "+ جديد" : "+ New";
            _approveBtn.Content = IsArabic ? "✅ اعتماد (تمام)" : "✅ Approve";
            _undoBtn.Content = IsArabic ? "↩️ تراجع" : "↩️ Undo";
            if (!_voiceRecorder.IsRecording) _voiceBtn.Content = IsArabic ? "🎤 صوت" : "🎤 Voice";
            foreach (UIElement el in _quickChips.Children)
            {
                Button b = el as Button;
                string[] meta = b != null ? b.Tag as string[] : null;
                if (meta != null) b.Content = IsArabic ? meta[0] : meta[1];
            }
            UpdateStatusBanner();
        }

        private void UpdateStatusBanner()
        {
            bool hasKey = !string.IsNullOrWhiteSpace(_config.GetApiKey(SelectedProvider));
            _statusBanner.Foreground = hasKey
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36));
            _statusBanner.Text = hasKey
                ? (IsArabic ? "جاهز — اتصال مباشر مع " + SelectedProvider + " (مشترك مع AutoCAD)." : "Ready — direct Revit AI mode (" + SelectedProvider + ").")
                : (IsArabic ? "أضف مفتاح " + SelectedProvider + " من زر [🔑 API]." : "Missing API key for " + SelectedProvider + ". Click [🔑 API].");
        }

        private async Task SendPromptAsync()
        {
            if (_busy) return;
            string prompt = (_inputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            _inputBox.Clear();
            AppendChat(IsArabic ? "أنت" : "You", prompt);
            _busy = true;
            SetActionControlsEnabled(false);
            _footerStatus.Text = IsArabic ? "جارٍ التفكير والتنفيذ داخل Revit..." : "Working inside Revit...";

            try
            {
                string prov = SelectedProvider;
                string mod = (_modelCombo.Text ?? "").Trim();
                bool ar = IsArabic;
                RevitAgentReply reply = await Task.Run(async delegate
                {
                    return await _session.SendAsync(
                        _config, prov, mod, prompt, ar,
                        delegate(string toolName, Dictionary<string, object> toolArgs)
                        {
                            return _dispatcher.EnqueueAsync(toolName, toolArgs);
                        },
                        delegate(string msg)
                        {
                            Dispatcher.BeginInvoke(new Action(delegate { _footerStatus.Text = msg; }));
                        });
                });
                AppendChat("AI-Engineer", reply.Text);
                string baseStatus = reply.ExecutedTools.Count > 0
                    ? ((IsArabic ? "تم التنفيذ عبر الأدوات: " : "Executed tools: ") + string.Join(", ", reply.ExecutedTools.ToArray()))
                    : (IsArabic ? "تم." : "Done.");
                _footerStatus.Text = reply.HallucinationGuardTriggered
                    ? ((IsArabic ? "[تحذير: تم تصحيح ادعاء غير مدعوم بأداة] " : "[Notice: Anti-hallucination guard active] ") + baseStatus)
                    : baseStatus;
            }
            catch (Exception ex)
            {
                AppendChat("Error", ex.Message);
                _footerStatus.Text = ex.Message;
            }
            finally
            {
                _busy = false;
                SetActionControlsEnabled(true);
            }
        }

        private void SetActionControlsEnabled(bool enabled)
        {
            _sendBtn.IsEnabled = enabled;
            _approveBtn.IsEnabled = enabled;
            _undoBtn.IsEnabled = enabled;
            _clearBtn.IsEnabled = enabled;
            _providerCombo.IsEnabled = enabled;
        }

        private async Task RunDirectButtonToolAsync(string toolName, Dictionary<string, object> args)
        {
            if (_busy) return;
            _busy = true;
            SetActionControlsEnabled(false);
            try
            {
                Dictionary<string, object> res = await _dispatcher.EnqueueAsync(toolName, args);
                AppendChat("AI-Engineer", SimpleJson.Serialize(res));
            }
            catch (Exception ex) { AppendChat("Error", ex.Message); }
            finally
            {
                _busy = false;
                SetActionControlsEnabled(true);
            }
        }

        private async Task ToggleVoiceAsync()
        {
            if (_busy) return;
            try
            {
                if (!_voiceRecorder.IsRecording)
                {
                    _voiceRecorder.Start();
                    _voiceTimer.Start();
                    _voiceBtn.Content = IsArabic ? "⏹ إيقاف (0s)" : "⏹ Stop (0s)";
                    _footerStatus.Text = IsArabic ? "جارٍ التسجيل الصوتي..." : "Recording voice...";
                }
                else
                {
                    _voiceTimer.Stop();
                    _busy = true;
                    SetActionControlsEnabled(false);
                    _voiceBtn.IsEnabled = false;
                    string text = "";
                    try
                    {
                        double dur; int peak;
                        byte[] wav = _voiceRecorder.StopAndGetWavBytes(out dur, out peak);
                        _voiceBtn.Content = IsArabic ? "⏳ تحويل..." : "⏳ Transcribing...";
                        bool ar = IsArabic;
                        text = await Task.Run(delegate { return RevitVoiceTranscriber.TranscribeWav(_config, wav, ar); });
                    }
                    finally
                    {
                        _voiceBtn.Content = IsArabic ? "🎤 صوت" : "🎤 Voice";
                        _voiceBtn.IsEnabled = true;
                        _busy = false;
                        SetActionControlsEnabled(true);
                    }
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _inputBox.Text = text;
                        if (_config.VoiceAutoSend) await SendPromptAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _voiceBtn.Content = IsArabic ? "🎤 صوت" : "🎤 Voice";
                _footerStatus.Text = ex.Message;
            }
        }

        internal void ShutdownCleanup()
        {
            try { _voiceTimer.Stop(); } catch { }
            try { _voiceRecorder.Dispose(); } catch { }
        }

        private void OpenApiSettingsDialog()
        {
            Window win = new Window
            {
                Title = IsArabic ? "إعدادات مفاتيح API المشفّرة (DPAPI)" : "Encrypted API Keys (DPAPI)",
                Width = 460, Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 37, 42)),
                Foreground = Brushes.White
            };
            StackPanel sp = new StackPanel { Margin = new Thickness(14) };
            PasswordBox gBox = new PasswordBox { Password = _config.GetApiKey(RevitDirectAiConfig.ProviderGemini), Margin = new Thickness(0, 2, 0, 8) };
            PasswordBox cBox = new PasswordBox { Password = _config.GetApiKey(RevitDirectAiConfig.ProviderClaude), Margin = new Thickness(0, 2, 0, 8) };
            PasswordBox oBox = new PasswordBox { Password = _config.GetApiKey(RevitDirectAiConfig.ProviderOpenAI), Margin = new Thickness(0, 2, 0, 12) };
            sp.Children.Add(new TextBlock { Text = "Gemini API Key:" }); sp.Children.Add(gBox);
            sp.Children.Add(new TextBlock { Text = "Claude API Key:" }); sp.Children.Add(cBox);
            sp.Children.Add(new TextBlock { Text = "OpenAI API Key:" }); sp.Children.Add(oBox);
            Button save = new Button { Content = IsArabic ? "حفظ مشفّر" : "Save Encrypted", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Right };
            save.Click += delegate
            {
                _config.GeminiApiKey = gBox.Password.Trim();
                _config.ClaudeApiKey = cBox.Password.Trim();
                _config.OpenAIApiKey = oBox.Password.Trim();
                _config.Save();
                _config = RevitDirectAiConfig.Load();
                UpdateStatusBanner();
                win.Close();
            };
            sp.Children.Add(save);
            win.Content = sp;
            try
            {
                IntPtr revitHwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (revitHwnd != IntPtr.Zero)
                    new WindowInteropHelper(win).Owner = revitHwnd;
            }
            catch { }
            win.ShowDialog();
        }

        private void AppendChat(string speaker, string text)
        {
            _transcript.AppendText("[" + speaker + "]\r\n" + (text ?? "") + "\r\n\r\n");
            _transcript.ScrollToEnd();
        }
    }

    public sealed class RevitDirectApplication : IExternalApplication
    {
        private static RevitExternalEventDispatcher _dispatcher;
        private static RevitCopilotDockablePage _panePage;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                _dispatcher = new RevitExternalEventDispatcher();
                _dispatcher.Initialize();
                _panePage = new RevitCopilotDockablePage(_dispatcher);
                application.RegisterDockablePane(RevitCopilotDockablePage.PaneId, "AI-Engineer Copilot", _panePage);

                const string tabName = "AI-Engineer";
                try { application.CreateRibbonTab(tabName); } catch { }
                RibbonPanel panel = application.CreateRibbonPanel(tabName, "AI Copilot");
                string asmPath = Assembly.GetExecutingAssembly().Location;
                PushButtonData btnData = new PushButtonData(
                    "AIEngineerShowCopilot",
                    "AI Copilot",
                    asmPath,
                    typeof(ShowRevitCopilotCommand).FullName);
                btnData.ToolTip = "Open AI-Engineer Direct BIM/MEP Copilot chat panel inside Revit.";
                panel.AddItem(btnData);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("AI-Engineer", "Failed to initialize AI-Engineer Revit Direct Copilot: " + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { if (_panePage != null) _panePage.ShutdownCleanup(); } catch { }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ShowRevitCopilotCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                DockablePane pane = commandData.Application.GetDockablePane(RevitCopilotDockablePage.PaneId);
                if (pane != null)
                {
                    if (pane.IsShown()) pane.Hide();
                    else pane.Show();
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
