using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;

namespace AIEngineer.AutoCAD
{
    internal sealed class DirectAiConfig
    {
        public const string ProviderGemini = "gemini";
        public const string ProviderClaude = "claude";
        public const string ProviderOpenAI = "openai";
        public const string ProviderLocal = "local";

        public const string SortOrderAsk = "ask";
        public const string SortOrderLeftToRight = "left_to_right";
        public const string SortOrderRightToLeft = "right_to_left";
        public const string SortOrderTopToBottom = "top_to_bottom";
        public const string SortOrderBottomToTop = "bottom_to_top";

        public string Provider = ProviderGemini;
        public string GeminiApiKey = "";
        public string GeminiModel = "gemini-3.5-flash-lite";
        public string ClaudeApiKey = "";
        public string ClaudeModel = "claude-sonnet-4-20250514";
        public string OpenAIApiKey = "";
        public string OpenAIModel = "gpt-5.3-codex";
        public string OpenAIBaseUrl = "https://api.openai.com/v1";
        public string OpenAIApiMode = "responses"; // "responses" or "chat_completions"
        public string PreferredCoordSortOrder = SortOrderAsk;

        public static string ConfigFilePath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(Path.Combine(appData, "AI-Engineer"), "direct_ai_config.json");
            }
        }

        public static DirectAiConfig Load()
        {
            DirectAiConfig cfg = new DirectAiConfig();
            try
            {
                string path = ConfigFilePath;
                if (File.Exists(path))
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    string raw = File.ReadAllText(path, Encoding.UTF8);
                    Dictionary<string, object> d = ser.DeserializeObject(raw) as Dictionary<string, object>;
                    if (d != null)
                    {
                        cfg.Provider = GetStr(d, "provider", cfg.Provider);
                        cfg.GeminiApiKey = GetStr(d, "gemini_api_key", "");
                        cfg.GeminiModel = GetStr(d, "gemini_model", cfg.GeminiModel);
                        cfg.ClaudeApiKey = GetStr(d, "claude_api_key", "");
                        cfg.ClaudeModel = GetStr(d, "claude_model", cfg.ClaudeModel);
                        cfg.OpenAIApiKey = GetStr(d, "openai_api_key", "");
                        cfg.OpenAIModel = GetStr(d, "openai_model", cfg.OpenAIModel);
                        cfg.OpenAIBaseUrl = GetStr(d, "openai_base_url", cfg.OpenAIBaseUrl);
                        cfg.OpenAIApiMode = GetStr(d, "openai_api_mode", cfg.OpenAIApiMode);
                        cfg.PreferredCoordSortOrder = NormalizeSortOrderPref(GetStr(d, "preferred_coord_sort_order", cfg.PreferredCoordSortOrder));
                    }
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(cfg.GeminiApiKey))
                cfg.GeminiApiKey = ReadEnv("GEMINI_API_KEY");
            if (string.IsNullOrWhiteSpace(cfg.ClaudeApiKey))
                cfg.ClaudeApiKey = ReadEnv("ANTHROPIC_API_KEY");
            if (string.IsNullOrWhiteSpace(cfg.OpenAIApiKey))
                cfg.OpenAIApiKey = ReadEnv("OPENAI_API_KEY");

            string envProvider = ReadEnv("AI_ENGINEER_DIRECT_PROVIDER");
            if (!string.IsNullOrWhiteSpace(envProvider))
                cfg.Provider = envProvider.ToLowerInvariant();

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

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["provider"] = Provider ?? ProviderGemini;
            d["gemini_api_key"] = (GeminiApiKey ?? "").Trim();
            d["gemini_model"] = (GeminiModel ?? "gemini-3.5-flash-lite").Trim();
            d["claude_api_key"] = (ClaudeApiKey ?? "").Trim();
            d["claude_model"] = (ClaudeModel ?? "claude-sonnet-4-20250514").Trim();
            d["openai_api_key"] = (OpenAIApiKey ?? "").Trim();
            d["openai_model"] = (OpenAIModel ?? "gpt-5.3-codex").Trim();
            d["openai_base_url"] = (OpenAIBaseUrl ?? "https://api.openai.com/v1").Trim().TrimEnd('/');
            d["openai_api_mode"] = (OpenAIApiMode ?? "responses").Trim();
            d["preferred_coord_sort_order"] = NormalizeSortOrderPref(PreferredCoordSortOrder);

            JavaScriptSerializer ser = new JavaScriptSerializer();
            File.WriteAllText(path, ser.Serialize(d), Encoding.UTF8);
        }

        public static string NormalizeSortOrderPref(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return SortOrderAsk;
            string s = raw.Trim().ToLowerInvariant();
            if (s == SortOrderLeftToRight || s == SortOrderRightToLeft || s == SortOrderTopToBottom || s == SortOrderBottomToTop)
                return s;
            return SortOrderAsk;
        }

        public static string DescribeSortOrderPref(string order, bool isArabic)
        {
            string norm = NormalizeSortOrderPref(order);
            if (norm == SortOrderLeftToRight) return isArabic ? "من الشمال إلى اليمين (left_to_right)" : "Left to Right (left_to_right)";
            if (norm == SortOrderRightToLeft) return isArabic ? "من اليمين إلى الشمال (right_to_left)" : "Right to Left (right_to_left)";
            if (norm == SortOrderTopToBottom) return isArabic ? "من فوق إلى تحت (top_to_bottom)" : "Top to Bottom (top_to_bottom)";
            if (norm == SortOrderBottomToTop) return isArabic ? "من تحت إلى فوق (bottom_to_top)" : "Bottom to Top (bottom_to_top)";
            return isArabic ? "حسب الشات / اسألني قبل التنفيذ" : "Per chat prompt (ask if unspecified)";
        }

        public string GetApiKey(string provider)
        {
            if (provider == ProviderGemini)
                return FirstNonEmpty(GeminiApiKey, ReadEnv("GEMINI_API_KEY"));
            if (provider == ProviderClaude)
                return FirstNonEmpty(ClaudeApiKey, ReadEnv("ANTHROPIC_API_KEY"));
            if (provider == ProviderOpenAI)
                return FirstNonEmpty(OpenAIApiKey, ReadEnv("OPENAI_API_KEY"));
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
            {
                return new string[]
                {
                    "gemini-3.5-flash-lite",
                    "gemini-3.1-flash-lite-preview",
                    "gemini-flash-lite-latest",
                    "gemini-3.8-flash",
                    "gemini-3.5-flash",
                    "gemini-3.1-pro-preview"
                };
            }
            if (provider == ProviderClaude)
            {
                return new string[]
                {
                    "claude-sonnet-4-20250514",
                    "claude-opus-4-20250514",
                    "claude-3-7-sonnet-20250219",
                    "claude-3-5-sonnet-latest",
                    "claude-3-5-haiku-latest"
                };
            }
            if (provider == ProviderOpenAI)
            {
                return new string[]
                {
                    "gpt-5.3-codex",
                    "gpt-5.2-codex",
                    "gpt-5",
                    "gpt-4.1",
                    "o4-mini",
                    "o3",
                    "gpt-4o"
                };
            }
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

        private static string FirstNonEmpty(string a, string b)
        {
            if (!string.IsNullOrWhiteSpace(a)) return a.Trim();
            return !string.IsNullOrWhiteSpace(b) ? b.Trim() : "";
        }

        private static string GetStr(Dictionary<string, object> d, string key, string defVal)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                string s = v.ToString().Trim();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return defVal;
        }
    }

    internal sealed class ApiKeysDialog : Form
    {
        private readonly TextBox _geminiKey = new TextBox();
        private readonly ComboBox _geminiModel = new ComboBox();
        private readonly TextBox _claudeKey = new TextBox();
        private readonly ComboBox _claudeModel = new ComboBox();
        private readonly TextBox _openaiKey = new TextBox();
        private readonly ComboBox _openaiModel = new ComboBox();
        private readonly TextBox _openaiBaseUrl = new TextBox();
        private readonly ComboBox _openaiMode = new ComboBox();
        private readonly CheckBox _showKeys = new CheckBox();
        private readonly Label _info = new Label();
        private readonly DirectAiConfig _config;

        public ApiKeysDialog(DirectAiConfig config, bool isArabic)
        {
            _config = config ?? DirectAiConfig.Load();
            Text = isArabic ? "إعدادات مفاتيح وموديلات الـ API المباشرة" : "Direct AI API Keys & Models";
            Width = 540;
            Height = 470;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = isArabic ? RightToLeft.Yes : RightToLeft.No;
            Padding = new Padding(12);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 2;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
            grid.RowCount = 11;

            _geminiKey.UseSystemPasswordChar = true;
            _claudeKey.UseSystemPasswordChar = true;
            _openaiKey.UseSystemPasswordChar = true;
            _geminiKey.Dock = _claudeKey.Dock = _openaiKey.Dock = _openaiBaseUrl.Dock = DockStyle.Fill;

            ConfigureModelCombo(_geminiModel, DirectAiConfig.ProviderGemini, _config.GeminiModel);
            ConfigureModelCombo(_claudeModel, DirectAiConfig.ProviderClaude, _config.ClaudeModel);
            ConfigureModelCombo(_openaiModel, DirectAiConfig.ProviderOpenAI, _config.OpenAIModel);

            _openaiMode.Dock = DockStyle.Fill;
            _openaiMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _openaiMode.Items.Add("responses");
            _openaiMode.Items.Add("chat_completions");
            _openaiMode.SelectedIndex = string.Equals(_config.OpenAIApiMode, "chat_completions", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            _geminiKey.Text = _config.GetApiKey(DirectAiConfig.ProviderGemini);
            _claudeKey.Text = _config.GetApiKey(DirectAiConfig.ProviderClaude);
            _openaiKey.Text = _config.GetApiKey(DirectAiConfig.ProviderOpenAI);
            _openaiBaseUrl.Text = _config.OpenAIBaseUrl;

            AddRow(grid, 0, isArabic ? "مفتاح Gemini API:" : "Gemini API Key:", _geminiKey);
            AddRow(grid, 1, isArabic ? "موديل Gemini:" : "Gemini Model:", _geminiModel);
            AddRow(grid, 2, isArabic ? "مفتاح Claude API:" : "Claude API Key:", _claudeKey);
            AddRow(grid, 3, isArabic ? "موديل Claude:" : "Claude Model:", _claudeModel);
            AddRow(grid, 4, isArabic ? "مفتاح OpenAI / Codex:" : "OpenAI / Codex Key:", _openaiKey);
            AddRow(grid, 5, isArabic ? "موديل OpenAI / Codex:" : "OpenAI / Codex Model:", _openaiModel);
            AddRow(grid, 6, isArabic ? "نوع OpenAI API:" : "OpenAI Endpoint Mode:", _openaiMode);
            AddRow(grid, 7, isArabic ? "رابط OpenAI الأساسي:" : "OpenAI Base URL:", _openaiBaseUrl);

            _showKeys.Text = isArabic ? "إظهار المفاتيح" : "Show API keys";
            _showKeys.AutoSize = true;
            _showKeys.CheckedChanged += delegate
            {
                bool hide = !_showKeys.Checked;
                _geminiKey.UseSystemPasswordChar = hide;
                _claudeKey.UseSystemPasswordChar = hide;
                _openaiKey.UseSystemPasswordChar = hide;
            };
            grid.Controls.Add(new Label(), 0, 8);
            grid.Controls.Add(_showKeys, 1, 8);

            _info.AutoSize = true;
            _info.MaximumSize = new Size(490, 0);
            _info.ForeColor = Color.DimGray;
            _info.Text = isArabic
                ? "تُحفظ الإعدادات محليًا في: " + DirectAiConfig.ConfigFilePath + "\nوتُقرأ تلقائيًا من متغيرات البيئة (GEMINI_API_KEY / ANTHROPIC_API_KEY / OPENAI_API_KEY) إن لم تُكتب هنا."
                : "Saved locally in: " + DirectAiConfig.ConfigFilePath + "\nFalls back to Windows env vars automatically when empty.";
            grid.SetColumnSpan(_info, 2);
            grid.Controls.Add(_info, 0, 9);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = isArabic ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
            Button saveBtn = new Button();
            saveBtn.Text = isArabic ? "حفظ وإغلاق" : "Save & Close";
            saveBtn.AutoSize = true;
            saveBtn.DialogResult = DialogResult.OK;
            Button cancelBtn = new Button();
            cancelBtn.Text = isArabic ? "إلغاء" : "Cancel";
            cancelBtn.AutoSize = true;
            cancelBtn.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(saveBtn);
            buttons.Controls.Add(cancelBtn);
            grid.SetColumnSpan(buttons, 2);
            grid.Controls.Add(buttons, 0, 10);

            Controls.Add(grid);
            AcceptButton = saveBtn;
            CancelButton = cancelBtn;

            saveBtn.Click += delegate
            {
                _config.GeminiApiKey = (_geminiKey.Text ?? "").Trim();
                _config.GeminiModel = (_geminiModel.Text ?? "gemini-3.5-flash-lite").Trim();
                _config.ClaudeApiKey = (_claudeKey.Text ?? "").Trim();
                _config.ClaudeModel = (_claudeModel.Text ?? "claude-sonnet-4-20250514").Trim();
                _config.OpenAIApiKey = (_openaiKey.Text ?? "").Trim();
                _config.OpenAIModel = (_openaiModel.Text ?? "gpt-5.3-codex").Trim();
                _config.OpenAIApiMode = _openaiMode.SelectedItem != null ? _openaiMode.SelectedItem.ToString() : "responses";
                _config.OpenAIBaseUrl = string.IsNullOrWhiteSpace(_openaiBaseUrl.Text) ? "https://api.openai.com/v1" : _openaiBaseUrl.Text.Trim().TrimEnd('/');
                _config.Save();
            };
        }

        private static void ConfigureModelCombo(ComboBox box, string provider, string current)
        {
            box.Dock = DockStyle.Fill;
            box.DropDownStyle = ComboBoxStyle.DropDown;
            foreach (string preset in DirectAiConfig.GetModelPresets(provider))
                box.Items.Add(preset);
            box.Text = current;
        }

        private static void AddRow(TableLayoutPanel grid, int row, string caption, Control ctrl)
        {
            Label lbl = new Label();
            lbl.Text = caption;
            lbl.AutoSize = true;
            lbl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            grid.Controls.Add(lbl, 0, row);
            grid.Controls.Add(ctrl, 1, row);
        }
    }

    internal sealed class DirectTurnRecord
    {
        public string Role;
        public string Text;
        public string ExecutedToolsSummary;
    }

    internal sealed class DirectAgentReply
    {
        public string Text;
        public List<string> ExecutedTools = new List<string>();
        public string ProviderUsed;
        public string ModelUsed;
        public string FallbackNotice;
    }

    internal sealed class ApiCallException : InvalidOperationException
    {
        public int StatusCode;
        public string RawSnippet;
        public bool IsDailyQuotaExhausted;
        public bool IsRetryable;

        public ApiCallException(int statusCode, string message, string rawSnippet, bool isRetryable, bool isDailyQuota)
            : base(message)
        {
            StatusCode = statusCode;
            RawSnippet = rawSnippet ?? "";
            IsRetryable = isRetryable;
            IsDailyQuotaExhausted = isDailyQuota;
        }
    }

    internal sealed class DirectCadAgentSession
    {
        private const int MaxToolIterations = 14;
        private readonly List<DirectTurnRecord> _history = new List<DirectTurnRecord>();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly HttpClient _http;

        static DirectCadAgentSession()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { } // TLS 1.3 when supported
            try { ServicePointManager.Expect100Continue = false; } catch { }
        }

        public DirectCadAgentSession()
        {
            _json.MaxJsonLength = 16 * 1024 * 1024;
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(120);
        }

        public void ClearHistory()
        {
            _history.Clear();
        }

        public int TurnCount { get { return _history.Count; } }

        public async Task<DirectAgentReply> SendAsync(
            Control uiControl,
            DirectAiConfig config,
            string provider,
            string model,
            string userMessage,
            bool isArabic,
            Action<string> onProgress)
        {
            if (config == null) config = DirectAiConfig.Load();
            string apiKey = config.GetApiKey(provider);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                string envHint = provider == DirectAiConfig.ProviderGemini ? "GEMINI_API_KEY"
                    : (provider == DirectAiConfig.ProviderClaude ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY");
                throw new InvalidOperationException(
                    isArabic
                        ? "مفتاح API غير مضبوط للمزود '" + provider + "'. اضغط زر [🔑 API] أدناه أو اضبط متغير البيئة " + envHint + "."
                        : "API key not configured for '" + provider + "'. Click [🔑 API] or set " + envHint + ".");
            }

            string resolvedModel = string.IsNullOrWhiteSpace(model) ? config.GetModel(provider) : model.Trim();
            string actualModelUsed = resolvedModel;
            string fallbackNotice = null;
            string preferredSortOrder = DirectAiConfig.NormalizeSortOrderPref(config.PreferredCoordSortOrder);
            string systemPrompt = BuildSystemPrompt(isArabic, preferredSortOrder);
            List<DirectTurnRecord> historySnapshot = new List<DirectTurnRecord>(_history);
            List<string> executedTools = new List<string>();
            string msgInferredSort = InferRequestedCoordSortOrderFromText(userMessage);

            List<string> initialSelectedHandles = new List<string>();
            int initialCmdActive = 0;
            MarshalUi(uiControl, delegate
            {
                try
                {
                    Document activeDoc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    if (activeDoc != null)
                        initialSelectedHandles = CadToolExecutor.GetSelectedHandleStrings(activeDoc);
                }
                catch { }
                try
                {
                    object rawCmd = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CMDACTIVE");
                    if (rawCmd != null) initialCmdActive = Convert.ToInt32(rawCmd, CultureInfo.InvariantCulture);
                }
                catch { }
            });
            bool aiModifiedSelectionInTurn = false;

            Func<string, Dictionary<string, object>, Dictionary<string, object>> execToolOnUiThread =
                delegate(string toolName, Dictionary<string, object> toolArgs)
                {
                    string lowerTool = (toolName ?? "").Trim().ToLowerInvariant();
                    if (lowerTool == "select_entities")
                    {
                        bool isClear = toolArgs != null && toolArgs.ContainsKey("clear") && (toolArgs["clear"] is bool) && (bool)toolArgs["clear"];
                        if (!isClear) aiModifiedSelectionInTurn = true;
                    }
                    else if (lowerTool == "convert_matching_shapes_to_block" || lowerTool == "create_block")
                    {
                        if (toolArgs == null) toolArgs = new Dictionary<string, object>();
                        bool hasHandles = toolArgs.ContainsKey("handles") && toolArgs["handles"] != null;
                        bool hasBbox = toolArgs.ContainsKey("bbox") && toolArgs["bbox"] != null;
                        bool hasCustomEntities = toolArgs.ContainsKey("entities") && toolArgs["entities"] != null;
                        bool selOnly = !toolArgs.ContainsKey("selected_only") || !(toolArgs["selected_only"] is bool) || (bool)toolArgs["selected_only"];
                        if (selOnly && !hasHandles && !hasBbox && !hasCustomEntities)
                        {
                            if (initialSelectedHandles.Count == 0)
                            {
                                string blockErrMsg = (initialCmdActive != 0)
                                    ? "ERROR: AutoCAD has an active command running (e.g. COPY/MOVE, CMDACTIVE=" + initialCmdActive + "), so no selection is active. STOP! Ask the engineer to press Esc in AutoCAD first, select ONE sample shape, and retry. Do NOT call select_entities!"
                                    : "ERROR: The engineer had 0 objects selected in AutoCAD before sending this message. NEVER call select_entities to guess a shape for block conversion! Ask the engineer to select the sample shape (lines/circles) in AutoCAD and retry.";
                                Dictionary<string, object> blocked = new Dictionary<string, object> { { "ok", false }, { "error", blockErrMsg } };
                                executedTools.Add(SummarizeToolCall(toolName, blocked));
                                return blocked;
                            }
                            if (aiModifiedSelectionInTurn)
                            {
                                // Always restore the engineer's real initial selection handles so AI select_entities cannot hijack the template
                                toolArgs["handles"] = initialSelectedHandles.ToArray();
                            }
                            else
                            {
                                List<string> curSel = new List<string>();
                                MarshalUi(uiControl, delegate
                                {
                                    Document activeDoc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                                    if (activeDoc != null) curSel = CadToolExecutor.GetSelectedHandleStrings(activeDoc);
                                });
                                if (curSel.Count == 0 && initialSelectedHandles.Count > 0)
                                    toolArgs["handles"] = initialSelectedHandles.ToArray();
                            }
                        }
                    }

                    if (string.Equals(toolName, "annotate_block_coordinates", StringComparison.OrdinalIgnoreCase))
                    {
                        if (toolArgs == null) toolArgs = new Dictionary<string, object>();
                        string argOrder = toolArgs.ContainsKey("sort_order") && toolArgs["sort_order"] != null ? toolArgs["sort_order"].ToString().Trim() : "";
                        string argOrderLow = argOrder.ToLowerInvariant();
                        bool isTableOnly = toolArgs.ContainsKey("table_only") && (toolArgs["table_only"] is bool) && (bool)toolArgs["table_only"];
                        if (string.IsNullOrEmpty(argOrder) || argOrderLow == "ask" || argOrderLow == "auto")
                        {
                            if (!string.IsNullOrEmpty(msgInferredSort))
                                toolArgs["sort_order"] = msgInferredSort;
                            else if (!isTableOnly && preferredSortOrder != DirectAiConfig.SortOrderAsk)
                                toolArgs["sort_order"] = preferredSortOrder;
                        }
                    }

                    if (onProgress != null)
                        MarshalUi(uiControl, delegate {
                            onProgress(isArabic ? "ينفذ داخل أوتوكاد: " + toolName + "..." : "Executing CAD tool: " + toolName + "...");
                        });

                    Dictionary<string, object> result = null;
                    MarshalUi(uiControl, delegate
                    {
                        Document activeDoc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                        result = CadToolExecutor.ExecuteTool(activeDoc, toolName, toolArgs);
                    });

                    executedTools.Add(SummarizeToolCall(toolName, result));
                    return result ?? new Dictionary<string, object> { { "ok", false }, { "error", "Tool execution returned null." } };
                };

            string answerText = await Task.Run<string>(delegate
            {
                if (provider == DirectAiConfig.ProviderGemini)
                {
                    return RunGeminiLoop(
                        apiKey, resolvedModel, systemPrompt, historySnapshot, userMessage,
                        execToolOnUiThread, onProgress, uiControl, isArabic, executedTools,
                        out actualModelUsed, out fallbackNotice);
                }
                if (provider == DirectAiConfig.ProviderClaude)
                    return RunClaudeLoop(apiKey, resolvedModel, systemPrompt, historySnapshot, userMessage, execToolOnUiThread, onProgress, uiControl, isArabic);
                if (provider == DirectAiConfig.ProviderOpenAI)
                    return RunOpenAILoop(apiKey, resolvedModel, config.OpenAIBaseUrl, config.OpenAIApiMode, systemPrompt, historySnapshot, userMessage, execToolOnUiThread, onProgress, uiControl, isArabic);
                throw new InvalidOperationException("Unsupported direct provider: " + provider);
            });

            string cleanAnswer = StripFakeToolLog(answerText ?? "");
            _history.Add(new DirectTurnRecord { Role = "user", Text = userMessage });
            _history.Add(new DirectTurnRecord
            {
                Role = "assistant",
                Text = cleanAnswer,
                ExecutedToolsSummary = executedTools.Count > 0 ? string.Join(", ", executedTools.ToArray()) : null
            });
            while (_history.Count > 30) _history.RemoveAt(0);

            DirectAgentReply reply = new DirectAgentReply();
            reply.Text = cleanAnswer;
            reply.ExecutedTools = executedTools;
            reply.ProviderUsed = provider;
            reply.ModelUsed = actualModelUsed;
            reply.FallbackNotice = fallbackNotice;
            return reply;
        }

        private static string FormatUserTextWithPendingAudit(string userText, ref string pendingAudit)
        {
            if (string.IsNullOrWhiteSpace(pendingAudit)) return userText ?? "";
            string header = "[Prior turn CAD audit (internal log — NEVER copy or echo this bracketed line in your response): " + pendingAudit + "]\n\n";
            pendingAudit = null;
            return header + (userText ?? "");
        }

        private static void MarshalUi(Control uiControl, Action action)
        {
            if (action == null) return;
            if (uiControl != null && !uiControl.IsDisposed && uiControl.IsHandleCreated && uiControl.InvokeRequired)
            {
                uiControl.Invoke(new MethodInvoker(action));
            }
            else
            {
                action();
            }
        }

        private static string SummarizeToolCall(string toolName, Dictionary<string, object> res)
        {
            bool ok = res != null && res.ContainsKey("ok") && res["ok"] is bool && (bool)res["ok"];
            if (!ok)
            {
                string err = res != null && res.ContainsKey("error") && res["error"] != null ? res["error"].ToString() : "failed";
                if (err.Length > 60) err = err.Substring(0, 60);
                return toolName + "(ERR:" + err + ")";
            }
            if (res.ContainsKey("undone_steps")) return toolName + "(undo:" + res["undone_steps"] + ",mod:" + (res.ContainsKey("restored_modified_count") ? res["restored_modified_count"] : 0) + ")";
            if (res.ContainsKey("callouts_drawn") && res.ContainsKey("total_count"))
            {
                string so = res.ContainsKey("sort_order") && res["sort_order"] != null ? res["sort_order"].ToString() : "left_to_right";
                return toolName + "(n=" + res["total_count"] + ",sort:" + so + ",callouts:" + res["callouts_drawn"] + ",table:" + (res.ContainsKey("table_drawn") ? res["table_drawn"] : false) + ")";
            }
            if (res.ContainsKey("matched_instances")) return toolName + "(match:" + res["matched_instances"] + ",conv:" + (res.ContainsKey("converted_count") ? res["converted_count"] : 0) + ")";
            if (res.ContainsKey("legend_bounds")) return toolName + "(rows:" + (res.ContainsKey("row_count") ? res["row_count"] : "ok") + ")";
            if (res.ContainsKey("selected_count") && toolName == "select_entities") return toolName + "(sel:" + res["selected_count"] + ")";
            if (res.ContainsKey("definition_entities") && res.ContainsKey("block_name")) return toolName + "(" + res["block_name"] + ",def:" + res["definition_entities"] + ")";
            if (res.ContainsKey("created_count")) return toolName + "(+" + res["created_count"] + ")";
            if (res.ContainsKey("modified_count")) return toolName + "(mod:" + res["modified_count"] + ")";
            if (res.ContainsKey("copied_count")) return toolName + "(copy:" + res["copied_count"] + ")";
            if (res.ContainsKey("deleted_count")) return toolName + "(del:" + res["deleted_count"] + ")";
            if (res.ContainsKey("total_matched")) return toolName + "(match:" + res["total_matched"] + ")";
            if (res.ContainsKey("total_entities")) return toolName + "(n=" + res["total_entities"] + ")";
            return toolName + "(ok)";
        }

        // ==================== Anti-Hallucination Execution Guard ====================

        private static bool IsActionTool(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            string n = toolName.Trim().ToLowerInvariant();
            return n == "draw_entities" ||
                   n == "draw_block_legend" ||
                   n == "annotate_block_coordinates" ||
                   n == "modify_entities" ||
                   n == "undo_last_action" ||
                   n == "copy_entities" ||
                   n == "delete_entities" ||
                   n == "create_block" ||
                   n == "convert_matching_shapes_to_block" ||
                   n == "manage_layers" ||
                   n == "duplicate_drawing" ||
                   n == "execute_template" ||
                   n == "approve_or_rollback_preview" ||
                   n == "select_entities" ||
                   n == "zoom_view";
        }

        private static bool ContainsFakeToolLog(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return false;
            return reply.IndexOf("[Executed tools in AutoCAD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reply.IndexOf("[CAD tools:", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string StripFakeToolLog(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return reply ?? "";
            if (!ContainsFakeToolLog(reply)) return reply;
            string[] lines = reply.Replace("\r\n", "\n").Split('\n');
            List<string> kept = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string tr = lines[i].Trim();
                if (tr.StartsWith("[Executed tools in AutoCAD", StringComparison.OrdinalIgnoreCase) ||
                    tr.StartsWith("[CAD tools:", StringComparison.OrdinalIgnoreCase))
                {
                    int closeBracket = tr.IndexOf(']');
                    if (closeBracket >= 0 && closeBracket + 1 < tr.Length)
                    {
                        string rem = tr.Substring(closeBracket + 1).Trim();
                        if (!string.IsNullOrEmpty(rem)) kept.Add(rem);
                    }
                    continue;
                }
                kept.Add(lines[i]);
            }
            return string.Join(Environment.NewLine, kept.ToArray()).Trim();
        }

        private static bool HasExecutedActionTool(List<string> executedTools)
        {
            if (executedTools == null) return false;
            foreach (string entry in executedTools)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                int parenIdx = entry.IndexOf('(');
                string toolName = parenIdx > 0 ? entry.Substring(0, parenIdx) : entry;
                if (IsActionTool(toolName) && entry.IndexOf("(ERR:", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;
            }
            return false;
        }

        private static bool HasExecutedUndoTool(List<string> executedTools)
        {
            if (executedTools == null) return false;
            foreach (string entry in executedTools)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                if (entry.StartsWith("undo_last_action(undo:", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool ReplyReportsExplicitFailure(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return false;
            string low = reply.ToLowerInvariant();
            return reply.Contains("لم يتم") || reply.Contains("تعذر") || reply.Contains("غير مدعوم") ||
                   reply.Contains("لا يدعم") || reply.Contains("خطأ") || reply.Contains("مطلوب تعديل الكود") ||
                   reply.Contains("لا توجد تعديلات") || reply.Contains("Ctrl+Z") ||
                   low.Contains("not supported") || low.Contains("cannot execute") || low.Contains("failed") ||
                   low.Contains("error") || low.Contains("limitation") || low.Contains("no ai cad changes");
        }

        private static bool IsClarifyingQuestion(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return false;
            string t = reply.Trim();
            if (t.EndsWith("؟") || t.EndsWith("?")) return !ReplyClaimsCadExecution(t) && !ContainsFakeToolLog(t);
            return (t.Contains("؟") || t.Contains("?")) && !ReplyClaimsCadExecution(t) && !ContainsFakeToolLog(t);
        }

        private static string InferRequestedCoordSortOrderFromText(string userMsg)
        {
            if (string.IsNullOrWhiteSpace(userMsg)) return "";
            string s = userMsg.Trim().ToLowerInvariant()
                .Replace("إ", "ا").Replace("أ", "ا").Replace("آ", "ا").Replace("ى", "ي").Replace("ة", "ه");

            // Guard: when user says "انقل الجدول ... إلى اليسار" without numbering words, do NOT treat table position as sort_order
            bool hasTableMoveOnly = (s.Contains("جدول") && (s.Contains("انقل") || s.Contains("نقل") || s.Contains("move"))) &&
                                    !s.Contains("ترقيم") && !s.Contains("رقم") && !s.Contains("رتب") &&
                                    !s.Contains("ابدا") && !s.Contains("تبدا") && !s.Contains("ترتيب") && !s.Contains("sort") && !s.Contains("number");
            if (hasTableMoveOnly) return "";

            if (s.Contains("right_to_left_bottom_to_top")) return "right_to_left_bottom_to_top";
            if (s.Contains("left_to_right_bottom_to_top")) return "left_to_right_bottom_to_top";
            if (s.Contains("top_to_bottom_rtl") || s.Contains("top_to_bottom_right_to_left")) return "top_to_bottom_rtl";
            if (s.Contains("bottom_to_top_rtl") || s.Contains("bottom_to_top_right_to_left")) return "bottom_to_top_rtl";

            if (s.Contains("right_to_left") || s.Contains("right to left") || s.Contains("right-to-left")) return "right_to_left";
            if (s.Contains("left_to_right") || s.Contains("left to right") || s.Contains("left-to-right")) return "left_to_right";
            if (s.Contains("top_to_bottom") || s.Contains("top to bottom") || s.Contains("top-to-bottom")) return "top_to_bottom";
            if (s.Contains("bottom_to_top") || s.Contains("bottom to top") || s.Contains("bottom-to-top")) return "bottom_to_top";

            bool wantsTop = s.Contains("من فوق") || s.Contains("فوق لتحت") || s.Contains("من اعلي") || s.Contains("من الاعلي") || s.Contains("اعلي لاسفل") || s.Contains("الاعلي للاسفل");
            bool wantsBot = s.Contains("من تحت") || s.Contains("تحت لفوق") || s.Contains("من اسفل") || s.Contains("من الاسفل") || s.Contains("اسفل لاعلي") || s.Contains("الاسفل للاعلي");
            bool wantsRight = s.Contains("من اليمين") || s.Contains("يمين لشمال") || s.Contains("يمين ليسار");
            bool wantsLeft = s.Contains("من الشمال") || s.Contains("من اليسار") || s.Contains("شمال ليمين") || s.Contains("يسار ليمين");

            if (wantsTop && wantsRight) return "top_to_bottom_rtl";
            if (wantsBot && wantsRight) return "bottom_to_top_rtl";
            if (wantsTop) return "top_to_bottom";
            if (wantsBot) return "bottom_to_top";
            if (wantsRight) return "right_to_left";
            if (wantsLeft) return "left_to_right";
            return "";
        }

        private static bool ReplyClaimsCadExecution(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return false;
            if (ContainsFakeToolLog(reply)) return true;
            string cleaned = reply
                .Replace("لم يتم", "")
                .Replace("لم تقم", "")
                .Replace("لا يتم", "")
                .Replace("ما تم", "");
            string[] arClaims = new string[]
            {
                "تم رسم", "قمت برسم", "قمت الآن ب", "قمت بإزالة", "تم إزالة", "تم ازالة",
                "تم مسح", "تم حذف", "تم فصل", "تم تعديل", "تم نسخ", "تم اعتماد",
                "تم تكبير", "تم التحديد", "تم تحديد", "تم تنفيذ الجدول", "تم إعادة رسم",
                "تم اعادة رسم", "الجدول الذي تم اعتماده", "ورسمه بدقة",
                "تم تغيير لون", "تم تغيير اسم", "تمت إعادة تسمية", "تم تسمية",
                "تم تلوين", "تم إرجاع", "تم ارجاع", "تم استعادة", "إعادة باقي المخطط",
                "تم إنشاء بلوك", "تم انشاء بلوك", "تم إنشاء البلوك", "تم انشاء البلوك",
                "تم تعريف بلوك", "تم تحويل", "قمت بتحويل", "قمت بإنشاء بلوك", "قمت بانشاء بلوك",
                "تم استخراج", "تم كتابة", "تم وضع", "تم ترقيم", "تم إضافة", "تمت إضافة", "تم اضافه",
                "تم نقل", "تم ضبط", "تم تجهيز", "تم تنفيذ", "تم تحديث",
                "تم بحمد الله إعادة", "تم بحمد الله استخراج", "تم بحمد الله إضافة", "تم بحمد الله ضبط",
                "تم بحمد الله تعديل", "تم بحمد الله نقل", "تم بحمد الله رسم", "تم بحمد الله مسح",
                "تم بحمد الله إنشاء", "تم بحمد الله تحديد", "تم بحمد الله ترقيم",
                "قمت بمسح", "قمت باستخراج", "قمت بإضافة", "قمت بكتابة", "قمت بنقل", "قمت بترقيم", "قمت بوضع"
            };
            foreach (string c in arClaims)
            {
                if (cleaned.Contains(c)) return true;
            }
            string low = cleaned.ToLowerInvariant()
                .Replace("have not ", "")
                .Replace("has not ", "")
                .Replace("didn't ", "")
                .Replace("did not ", "");
            string[] enClaims = new string[]
            {
                "i have drawn", "i've drawn", "has been drawn", "have been drawn",
                "i have deleted", "i've deleted", "i have removed", "i removed",
                "successfully drawn", "successfully created", "successfully deleted",
                "successfully copied", "successfully selected", "i created the table",
                "i have separated", "i separated the", "table has been created",
                "changed color", "recolored", "renamed ", "has been renamed", "restored the", "reverted the",
                "created block", "created the block", "converted ", "successfully converted",
                "extracted the coordinates", "annotated the blocks", "placed the coordinates",
                "moved the coordinate", "moved the table", "created the coordinate table",
                "drew the leaders", "added leaders", "coordinate table has been"
            };
            foreach (string c in enClaims)
            {
                if (low.Contains(c)) return true;
            }
            return false;
        }

        private static bool ReplyClaimsUndoOrRestore(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return false;
            string cleaned = reply.Replace("لم يتم", "").Replace("تعذر", "");
            string[] arUndo = new string[]
            {
                "إعادة باقي المخطط", "تم إرجاع", "تم ارجاع", "تم استعادة",
                "تم التراجع عن", "لونه الأصلي", "اللون الأصلي"
            };
            foreach (string s in arUndo) { if (cleaned.Contains(s)) return true; }
            string low = cleaned.ToLowerInvariant();
            return low.Contains("restored the drawing") || low.Contains("reverted the previous") || low.Contains("original color");
        }

        private static bool IsDiagnosticQuestion(string userMsg)
        {
            if (string.IsNullOrWhiteSpace(userMsg)) return false;
            string trimmed = userMsg.Trim();
            string low = trimmed.ToLowerInvariant();

            bool startsWhy =
                trimmed.StartsWith("ليه ", StringComparison.Ordinal) ||
                trimmed.StartsWith("لية ", StringComparison.Ordinal) ||
                trimmed.StartsWith("لماذا ", StringComparison.Ordinal) ||
                trimmed.StartsWith("ليه؟", StringComparison.Ordinal) ||
                trimmed.StartsWith("لية؟", StringComparison.Ordinal) ||
                trimmed.StartsWith("لماذا؟", StringComparison.Ordinal) ||
                trimmed.StartsWith("ما سبب", StringComparison.Ordinal) ||
                trimmed.StartsWith("ما السبب", StringComparison.Ordinal) ||
                trimmed.StartsWith("ايه سبب", StringComparison.Ordinal) ||
                trimmed.StartsWith("إيه سبب", StringComparison.Ordinal) ||
                trimmed.StartsWith("اشمعنى", StringComparison.Ordinal) ||
                trimmed.StartsWith("إشمعنى", StringComparison.Ordinal) ||
                trimmed.StartsWith("هل فية مشكلة", StringComparison.Ordinal) ||
                trimmed.StartsWith("هل في مشكلة", StringComparison.Ordinal) ||
                trimmed.StartsWith("هل فيه مشكلة", StringComparison.Ordinal) ||
                trimmed.StartsWith("هل يوجد خطأ", StringComparison.Ordinal) ||
                trimmed.StartsWith("هل ممكن حل", StringComparison.Ordinal) ||
                trimmed.StartsWith("هل في حل", StringComparison.Ordinal) ||
                trimmed.StartsWith("هل فيه حل", StringComparison.Ordinal) ||
                trimmed.StartsWith("هل يوجد حل", StringComparison.Ordinal) ||
                low.StartsWith("why ") ||
                low.StartsWith("why did") ||
                low.StartsWith("what happened") ||
                low.StartsWith("how come");

            bool containsCodeDiag =
                trimmed.Contains("مشكلة بالكود") ||
                trimmed.Contains("مشكله بالكود") ||
                trimmed.Contains("مشكلة في الكود") ||
                trimmed.Contains("مشكله في الكود") ||
                trimmed.Contains("بيستنتج") ||
                trimmed.Contains("للمشكلة دي") ||
                trimmed.Contains("للمشكله دي") ||
                trimmed.Contains("غلط تماما") ||
                trimmed.Contains("ليه تم التراجع") ||
                trimmed.Contains("لية تم التراجع") ||
                trimmed.Contains("ليه رجعت") ||
                trimmed.Contains("لية رجعت") ||
                trimmed.Contains("في الآخر لم يتم") ||
                trimmed.Contains("وفي الاخر لم يتم") ||
                trimmed.Contains("رجع تاني للون") ||
                trimmed.Contains("رجع تاني الي اللون") ||
                trimmed.Contains("رجع تاني إلى اللون") ||
                trimmed.Contains("رجع للون الاصلي") ||
                trimmed.Contains("رجع للون الأصلي") ||
                trimmed.Contains("عاد للون الاصلي") ||
                trimmed.Contains("عاد للون الأصلي") ||
                trimmed.Contains("ولم يغير اسم") ||
                trimmed.Contains("ولم يتم تغيير") ||
                trimmed.Contains("مغيرش اسم") ||
                trimmed.Contains("مش راضي يغير");

            if (!startsWhy && !containsCodeDiag)
                return false;

            string[] explicitImperatives = new string[]
            {
                "ارسم ", "اعد رسم", "أعد رسم", "اعد كتابة", "أعد كتابة",
                "اعد تلوين", "أعد تلوين", "اعد تنفيذ", "أعد تنفيذ",
                "افصل ", "انسخ ", "احذف ", "امسح ", "نفذ الآن", "عدل الآن", "من جديد",
                "ممكن تغير", "ممكن تعدل", "ممكن ترسم", "غيره ", "عدله ", "رجع الخطوة", "تراجع عن التعديل",
                "حول ", "حولها", "حولهم", "احول ", "ابحث عن الاشكال", "ابحث علي الاشكال", "ابحث عن الأشكال", "ابحث على الأشكال",
                "انا عايز موشر", "انا عايز مؤشر", "عايز موشر", "عايز مؤشر", "عايز سهم",
                "ارجو منك نقل", "أرجو منك نقل", "محتاج هنا استخرج", "استخرج احداثيات", "عايز جدول"
            };
            foreach (string imp in explicitImperatives)
            {
                if (trimmed.Contains(imp)) return false;
            }
            if (low.Contains("now draw ") || low.Contains("now change ") || low.Contains("please draw ") || low.Contains("please undo "))
                return false;

            return true;
        }

        private static bool UserRequestsUndoOrRestore(string userMsg)
        {
            if (string.IsNullOrWhiteSpace(userMsg)) return false;
            if (IsDiagnosticQuestion(userMsg)) return false;
            string low = userMsg.ToLowerInvariant();
            string[] arKeys = new string[] { "رجع", "ارجع", "استرجع", "تراجع", "استعد", "اللون السابق", "لونه السابق", "الخطوة السابقة" };
            foreach (string k in arKeys) { if (userMsg.Contains(k)) return true; }
            return low.Contains("undo") || low.Contains("revert") || low.Contains("restore previous") || low.Contains("roll back") || low.Contains("rollback");
        }

        private static bool UserRequestsCadAction(string userMsg)
        {
            if (string.IsNullOrWhiteSpace(userMsg)) return false;
            if (IsDiagnosticQuestion(userMsg)) return false;
            if (userMsg.Contains("لا ترسم") || userMsg.Contains("بدون رسم") || userMsg.Contains("بدون تعديل"))
                return false;
            if (UserRequestsUndoOrRestore(userMsg)) return true;
            string low = userMsg.ToLowerInvariant();
            string[] arVerbs = new string[]
            {
                "ارسم", "اعد رسم", "أعد رسم", "اعد كتابة", "أعد كتابة", "اعادة رسم", "إعادة رسم",
                "افصل", "افصل الجدول", "جدول البلوكات", "شكل البلوك", "انسخ", "اعمل نسخة", "نسخة خارج",
                "اعمل تحديد", "حدد البلوكات", "حدد كل", "احذف", "امسح", "ازل", "أزل",
                "عدل", "غير", "لون", "تلوين", "تغيير اسم", "سمي", "حرك", "انقل", "دور", "ظلل", "تظليل", "اعمل مربع", "رسم دائرة", "رسم مربع",
                "احول", "تحويل", "حول كل", "حول الشكل", "حول العناصر", "حولها", "حولهم",
                "تشبه", "تشبة", "يشبه", "يشبة", "نفس الشكل", "نفس شكل", "مشابه", "متشابه", "شبيه",
                "كبلوك ", " كبلوك", "إلى بلوك", "الي بلوك", " لبلوك", "اعمل بلوك", "انشئ بلوك", "أنشئ بلوك", "تجميعة", "واسمية", "واسميه",
                "استخرج", "استخراج", "اكتب", "تتكتب", "يتكتب", "يكتب",
                "ترقيم", "رقمهم", "رقّم", "رتبهم", "طريقة الترقيم", "اتجاه الترقيم", "ترتيب الترقيم",
                "تبدا من الشمال", "تبدأ من الشمال", "من الشمال", "من اليسار", "من اليمين", "من فوق", "من تحت", "من أعلى", "من أعلي", "من أسفل", "من اسفل",
                "سهم", "أسهم", "اسهم", "ليدر", "مؤشر", "موشر", "مؤشرات",
                "راس السهم", "رأس السهم", "راس الاسهم", "رأس الأسهم", "ثبت سهم", "وضع السهم",
                "عينة", "نقطتين", "نقاط", "صفوف", "وراء بعض", "ورا بعض",
                "جدول الاحداثيات", "جدول الإحداثيات", "عايز جدول", "جدول داخل الاتوكاد",
                "ضيفه", "ضيفة", "أضف", "اضف"
            };
            foreach (string v in arVerbs)
            {
                if (userMsg.Contains(v)) return true;
            }
            string[] enVerbs = new string[]
            {
                "draw ", "redraw", "create a block", "create block", "block legend",
                "draw table", "separate the", "copy ", "delete ", "erase ", "remove ",
                "modify ", "rename ", "select ", "highlight ", "zoom ", "move ", "rotate ", "scale ", "color ", "recolor",
                "convert ", "match shape", "matching shape", "similar shape", "same shape", "blockify", "into a block", "to block", "to a block",
                "annotate", "callout", "coordinate", "coordinates", "leader", "arrow", "move table", "move the table", "extract coord", "coord table", "schedule table",
                "left to right", "right to left", "top to bottom", "bottom to top", "left_to_right", "right_to_left", "top_to_bottom", "bottom_to_top", "sort_order"
            };
            foreach (string v in enVerbs)
            {
                if (low.Contains(v)) return true;
            }
            return false;
        }

        // ==================== Gemini Native Loop (generateContent + Failover) ====================

        private static List<string> BuildGeminiCandidates(string primaryModel)
        {
            List<string> list = new List<string>();
            if (!string.IsNullOrWhiteSpace(primaryModel)) list.Add(primaryModel.Trim());
            string[] fallbacks = new string[]
            {
                "gemini-3.5-flash-lite",
                "gemini-3.1-flash-lite-preview",
                "gemini-flash-lite-latest"
            };
            foreach (string fb in fallbacks)
            {
                bool exists = false;
                foreach (string existing in list)
                {
                    if (string.Equals(existing, fb, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                }
                if (!exists) list.Add(fb);
            }
            return list;
        }

        private string RunGeminiLoop(
            string apiKey,
            string primaryModel,
            string systemPrompt,
            List<DirectTurnRecord> history,
            string userMessage,
            Func<string, Dictionary<string, object>, Dictionary<string, object>> execTool,
            Action<string> onProgress,
            Control uiControl,
            bool isArabic,
            List<string> executedToolsSoFar,
            out string actualModelUsed,
            out string fallbackNotice)
        {
            actualModelUsed = primaryModel;
            fallbackNotice = null;
            List<string> modelCandidates = BuildGeminiCandidates(primaryModel);

            List<object> toolDecls = new List<object>();
            foreach (Dictionary<string, object> spec in BuildToolSpecs())
            {
                toolDecls.Add(new Dictionary<string, object>
                {
                    { "name", spec["name"] },
                    { "description", spec["description"] },
                    { "parameters", spec["parameters"] }
                });
            }

            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                { "x-goog-api-key", apiKey }
            };

            List<object> baseContents = new List<object>();
            string pendingAudit = null;
            foreach (DirectTurnRecord turn in history)
            {
                if (turn.Role == "assistant")
                {
                    baseContents.Add(new Dictionary<string, object>
                    {
                        { "role", "model" },
                        { "parts", new object[] { new Dictionary<string, object> { { "text", turn.Text ?? "" } } } }
                    });
                    if (!string.IsNullOrWhiteSpace(turn.ExecutedToolsSummary))
                        pendingAudit = turn.ExecutedToolsSummary;
                }
                else
                {
                    string uText = FormatUserTextWithPendingAudit(turn.Text, ref pendingAudit);
                    baseContents.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "parts", new object[] { new Dictionary<string, object> { { "text", uText } } } }
                    });
                }
            }
            string currentUserText = FormatUserTextWithPendingAudit(userMessage, ref pendingAudit);
            baseContents.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "parts", new object[] { new Dictionary<string, object> { { "text", currentUserText } } } }
            });

            ApiCallException lastApiError = null;
            for (int mIdx = 0; mIdx < modelCandidates.Count; mIdx++)
            {
                string currentModel = modelCandidates[mIdx];
                string url = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(currentModel) + ":generateContent";

                List<object> contents = new List<object>(baseContents);
                if (mIdx > 0 && executedToolsSoFar != null && executedToolsSoFar.Count > 0)
                {
                    contents.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "parts", new object[] { new Dictionary<string, object> {
                            { "text", "[System continuity notice: The following AutoCAD tools ALREADY executed on this request: " +
                                      string.Join(", ", executedToolsSoFar.ToArray()) +
                                      ". Continue from the current drawing state without duplicating completed edits unless verification shows something missing.]" }
                        } } }
                    });
                }

                try
                {
                    int guardRetries = 0;
                    for (int iter = 0; iter < MaxToolIterations; iter++)
                    {
                        if (onProgress != null)
                        {
                            int roundNo = iter + 1;
                            MarshalUi(uiControl, delegate {
                                onProgress(isArabic
                                    ? "يتصل مباشر بـ Gemini (" + currentModel + ") - جولة " + roundNo + "..."
                                    : "Calling Gemini (" + currentModel + ") round " + roundNo + "...");
                            });
                        }

                        Dictionary<string, object> body = new Dictionary<string, object>
                        {
                            { "systemInstruction", new Dictionary<string, object> { { "parts", new object[] { new Dictionary<string, object> { { "text", systemPrompt } } } } } },
                            { "contents", contents },
                            { "tools", new object[] { new Dictionary<string, object> { { "functionDeclarations", toolDecls } } } },
                            { "generationConfig", new Dictionary<string, object> { { "temperature", 0.2 } } }
                        };

                        Dictionary<string, object> resp = PostJsonSync(url, headers, body, onProgress, uiControl, isArabic);
                        List<object> candidates = AsList(GetObj(resp, "candidates"));
                        if (candidates.Count == 0)
                        {
                            Dictionary<string, object> fb = AsDict(GetObj(resp, "promptFeedback"));
                            throw new InvalidOperationException("Gemini returned no candidates. " + (fb != null ? _json.Serialize(fb) : ""));
                        }

                        Dictionary<string, object> cand0 = AsDict(candidates[0]);
                        Dictionary<string, object> contentObj = AsDict(GetObj(cand0, "content"));
                        if (contentObj == null)
                        {
                            string finish = GetStr(cand0, "finishReason", "UNKNOWN");
                            throw new InvalidOperationException("Gemini candidate missing content (finishReason=" + finish + ").");
                        }

                        List<object> parts = AsList(GetObj(contentObj, "parts"));
                        List<Dictionary<string, object>> functionCalls = new List<Dictionary<string, object>>();
                        StringBuilder textOut = new StringBuilder();

                        foreach (object pObj in parts)
                        {
                            Dictionary<string, object> p = AsDict(pObj);
                            if (p == null) continue;
                            if (p.ContainsKey("functionCall") && p["functionCall"] is Dictionary<string, object>)
                                functionCalls.Add((Dictionary<string, object>)p["functionCall"]);
                            if (p.ContainsKey("text") && p["text"] != null && !(p.ContainsKey("thought") && (p["thought"] is bool) && (bool)p["thought"]))
                                textOut.Append(p["text"].ToString());
                        }

                        if (functionCalls.Count == 0)
                        {
                            string candidateTxt = textOut.ToString().Trim();
                            bool hasMutated = HasExecutedActionTool(executedToolsSoFar);
                            bool hasUndone = HasExecutedUndoTool(executedToolsSoFar);
                            bool hasFakeLog = ContainsFakeToolLog(candidateTxt);
                            bool claimsDone = ReplyClaimsCadExecution(candidateTxt) || hasFakeLog;
                            bool claimsUndone = ReplyClaimsUndoOrRestore(candidateTxt);
                            bool asksClarification = IsClarifyingQuestion(candidateTxt);
                            bool missingRequestedUndo = UserRequestsUndoOrRestore(userMessage) && claimsUndone && !hasUndone && !ReplyReportsExplicitFailure(candidateTxt);
                            bool shouldEnforce = (!hasMutated && (claimsDone || hasFakeLog || (UserRequestsCadAction(userMessage) && !asksClarification && !ReplyReportsExplicitFailure(candidateTxt)))) || missingRequestedUndo;

                            if (shouldEnforce && guardRetries < 2 && iter + 1 < MaxToolIterations)
                            {
                                guardRetries++;
                                if (onProgress != null)
                                {
                                    MarshalUi(uiControl, delegate {
                                        onProgress(isArabic
                                            ? "تحقق تنفيذي: منع الرد النظري وطلب التنفيذ الفعلي داخل أوتوكاد..."
                                            : "Execution guard: demanding actual CAD tool calls...");
                                    });
                                }
                                contents.Add(contentObj);
                                contents.Add(new Dictionary<string, object>
                                {
                                    { "role", "user" },
                                    { "parts", new object[] { new Dictionary<string, object> {
                                        { "text", missingRequestedUndo
                                            ? "[SYSTEM EXECUTION VERIFICATION ERROR]: You claimed you restored/reverted the drawing WITHOUT calling `undo_last_action`! Call `undo_last_action(steps: 1)` NOW to genuinely revert the previous AI change — or if `undo_last_action` reports no snapshots from this session, tell the engineer honestly (do NOT claim the rest of the drawing was restored if `undo_last_action` failed or was skipped!)."
                                            : "[SYSTEM EXECUTION VERIFICATION ERROR]: You did NOT execute any mutating CAD tool in AutoCAD yet (only read-only inspection or plain text occurred). " +
                                              "CRITICAL: NEVER write fake bracketed text like '[Executed tools in AutoCAD: ...]' or pretend drawings/tables/callouts/arrows happened! " +
                                              "Immediately call the actual JSON CAD tool NOW — use `annotate_block_coordinates` for block X/Y coordinates, user-chosen numbering (left_to_right / right_to_left / top_to_bottom / bottom_to_top), arrows, and coordinate tables (`table_only: true` to move the table), or `undo_last_action`, `convert_matching_shapes_to_block`, `create_block`, `modify_entities`, `draw_block_legend`, `delete_entities`, `select_entities`, `copy_entities`, `draw_entities`." }
                                    } } }
                                });
                                continue;
                            }

                            actualModelUsed = currentModel;
                            if (!string.Equals(currentModel, primaryModel, StringComparison.OrdinalIgnoreCase))
                            {
                                string reasonTag = lastApiError != null && lastApiError.StatusCode > 0 ? ("HTTP " + lastApiError.StatusCode) : "انقطاع/ضغط الخادم";
                                fallbackNotice = isArabic
                                    ? "تعذر الاستمرار على (" + primaryModel + ") [" + reasonTag + "] فتم التحويل تلقائياً إلى (" + currentModel + ")."
                                    : "Switched automatically from (" + primaryModel + ") [" + reasonTag + "] to (" + currentModel + ").";
                            }
                            string sanitizedCandidate = StripFakeToolLog(candidateTxt);
                            if ((!hasMutated && (claimsDone || hasFakeLog)) || missingRequestedUndo)
                            {
                                string guardBanner = missingRequestedUndo
                                    ? (isArabic
                                        ? "⚠️ [تحقق النظام]: الموديل لم يستدعِ أداة التراجع الفعلي (`undo_last_action`) لاستعادة حالة المخطط السابقة."
                                        : "⚠️ [System Guard]: Model did not invoke `undo_last_action` to actually revert prior drawing state.")
                                    : (isArabic
                                        ? "⚠️ [تحقق النظام]: الموديل لم ينفذ أي أداة رسم أو حذف أو تعديل فعلية داخل أوتوكاد في هذا الطلب (اقتصر على الفحص فقط). يرجى إعادة الصياغة أو التبديل لموديل أعلى مثل `gemini-3.5-flash`."
                                        : "⚠️ [System Guard]: No mutating CAD tool actually executed in AutoCAD during this turn (read-only only).");
                                return string.IsNullOrEmpty(sanitizedCandidate) ? guardBanner : (guardBanner + "\n\n" + sanitizedCandidate);
                            }
                            return string.IsNullOrEmpty(sanitizedCandidate) ? "(Gemini completed without text output)" : sanitizedCandidate;
                        }

                        // IMPORTANT: Preserve raw contentObj verbatim (including Gemini 3.x thoughtSignature!)
                        contents.Add(contentObj);

                        List<object> fnResponseParts = new List<object>();
                        foreach (Dictionary<string, object> fc in functionCalls)
                        {
                            string fnName = GetStr(fc, "name", "");
                            Dictionary<string, object> fnArgs = AsDict(GetObj(fc, "args")) ?? new Dictionary<string, object>();
                            Dictionary<string, object> toolResult = execTool(fnName, fnArgs);
                            fnResponseParts.Add(new Dictionary<string, object>
                            {
                                { "functionResponse", new Dictionary<string, object>
                                    {
                                        { "name", fnName },
                                        { "response", new Dictionary<string, object> { { "result", toolResult } } }
                                    }
                                }
                            });
                        }
                        contents.Add(new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "parts", fnResponseParts }
                        });
                    }

                    actualModelUsed = currentModel;
                    return isArabic
                        ? "وصل Gemini إلى الحد الأقصى لجولات الأدوات (" + MaxToolIterations + ") — يمكنك متابعة الطلب."
                        : "Reached maximum tool iterations (" + MaxToolIterations + ").";
                }
                catch (ApiCallException apiEx)
                {
                    lastApiError = apiEx;
                    bool canFallback = apiEx.IsRetryable || apiEx.IsDailyQuotaExhausted || apiEx.StatusCode == 404 || apiEx.StatusCode == 429 || apiEx.StatusCode == 503 || apiEx.StatusCode == 0;
                    if (canFallback && mIdx + 1 < modelCandidates.Count)
                    {
                        if (onProgress != null)
                        {
                            string nextM = modelCandidates[mIdx + 1];
                            MarshalUi(uiControl, delegate {
                                onProgress(isArabic
                                    ? "ضغط/حصة على (" + currentModel + ") — التبديل تلقائياً إلى (" + nextM + ")..."
                                    : "Fallback from (" + currentModel + ") to (" + nextM + ")...");
                            });
                        }
                        continue;
                    }
                    throw new InvalidOperationException(FormatFriendlyApiError(apiEx, primaryModel, isArabic));
                }
            }

            if (lastApiError != null)
                throw new InvalidOperationException(FormatFriendlyApiError(lastApiError, primaryModel, isArabic));
            throw new InvalidOperationException("Gemini request failed.");
        }

        private static string FormatFriendlyApiError(ApiCallException ex, string model, bool isArabic)
        {
            if (ex == null) return "API Error";
            if (!isArabic) return ex.Message;

            if (ex.StatusCode == 429 || ex.IsDailyQuotaExhausted)
            {
                return "انتهت الحصة المجانية للموديل (" + model + ") [HTTP 429] (مثلاً حد Gemini 3.5 Flash المجاني 20 طلباً/يوم فقط).\n" +
                       "• تم تجربة الموديلات البديلة تلقائياً.\n" +
                       "• الحل السريع: اختر `gemini-3.5-flash-lite` أو `gemini-3.1-flash-lite-preview` من القائمة، أو انتقل مؤقتاً لـ Claude/OpenAI عبر زر [🔑 API].\n" +
                       "تفاصيل Google: " + ex.RawSnippet;
            }
            if (ex.StatusCode == 503 || ex.StatusCode == 502 || ex.StatusCode == 504)
            {
                return "سيرفرات Google تحت ضغط مؤقت الآن على (" + model + ") والبدائل [HTTP " + ex.StatusCode + "]. أعد المحاولة خلال لحظات أو استخدم Claude/OpenAI عبر [🔑 API].";
            }
            if (ex.StatusCode == 401 || ex.StatusCode == 403)
            {
                return "مفتاح API غير صالح أو مرفوض [HTTP " + ex.StatusCode + "]. راجع المفتاح من زر [🔑 API].\n" + ex.RawSnippet;
            }
            return ex.Message;
        }

        // ==================== Claude Native Loop (Anthropic Messages API) ====================

        private string RunClaudeLoop(
            string apiKey,
            string model,
            string systemPrompt,
            List<DirectTurnRecord> history,
            string userMessage,
            Func<string, Dictionary<string, object>, Dictionary<string, object>> execTool,
            Action<string> onProgress,
            Control uiControl,
            bool isArabic)
        {
            string url = "https://api.anthropic.com/v1/messages";
            List<object> messages = new List<object>();
            string pendingAudit = null;
            foreach (DirectTurnRecord turn in history)
            {
                if (turn.Role == "assistant")
                {
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "assistant" },
                        { "content", turn.Text ?? "" }
                    });
                    if (!string.IsNullOrWhiteSpace(turn.ExecutedToolsSummary))
                        pendingAudit = turn.ExecutedToolsSummary;
                }
                else
                {
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", FormatUserTextWithPendingAudit(turn.Text, ref pendingAudit) }
                    });
                }
            }
            messages.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", FormatUserTextWithPendingAudit(userMessage, ref pendingAudit) }
            });

            List<object> claudeTools = new List<object>();
            foreach (Dictionary<string, object> spec in BuildToolSpecs())
            {
                claudeTools.Add(new Dictionary<string, object>
                {
                    { "name", spec["name"] },
                    { "description", spec["description"] },
                    { "input_schema", spec["parameters"] }
                });
            }

            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                { "x-api-key", apiKey },
                { "anthropic-version", "2023-06-01" }
            };

            bool hasMutated = false;
            bool hasUndone = false;
            int guardRetries = 0;
            for (int iter = 0; iter < MaxToolIterations; iter++)
            {
                if (onProgress != null)
                {
                    int roundNo = iter + 1;
                    MarshalUi(uiControl, delegate {
                        onProgress(isArabic ? "يتصل مباشر بـ Claude (" + model + ") - جولة " + roundNo + "..." : "Calling Claude (" + model + ") round " + roundNo + "...");
                    });
                }

                Dictionary<string, object> body = new Dictionary<string, object>
                {
                    { "model", model },
                    { "max_tokens", 8192 },
                    { "system", systemPrompt },
                    { "messages", messages },
                    { "tools", claudeTools },
                    { "temperature", 0.2 }
                };

                Dictionary<string, object> resp = PostJsonSync(url, headers, body, onProgress, uiControl, isArabic);
                List<object> contentBlocks = AsList(GetObj(resp, "content"));
                List<Dictionary<string, object>> toolUses = new List<Dictionary<string, object>>();
                StringBuilder textOut = new StringBuilder();

                foreach (object blkObj in contentBlocks)
                {
                    Dictionary<string, object> blk = AsDict(blkObj);
                    if (blk == null) continue;
                    string btype = GetStr(blk, "type", "");
                    if (btype == "tool_use") toolUses.Add(blk);
                    else if (btype == "text" && blk.ContainsKey("text") && blk["text"] != null)
                        textOut.Append(blk["text"].ToString());
                }

                if (toolUses.Count == 0)
                {
                    string finalTxt = textOut.ToString().Trim();
                    bool hasFakeLog = ContainsFakeToolLog(finalTxt);
                    bool claimsDone = ReplyClaimsCadExecution(finalTxt) || hasFakeLog;
                    bool claimsUndone = ReplyClaimsUndoOrRestore(finalTxt);
                    bool asksClarification = IsClarifyingQuestion(finalTxt);
                    bool missingRequestedUndo = UserRequestsUndoOrRestore(userMessage) && claimsUndone && !hasUndone && !ReplyReportsExplicitFailure(finalTxt);
                    bool shouldEnforce = (!hasMutated && (claimsDone || hasFakeLog || (UserRequestsCadAction(userMessage) && !asksClarification && !ReplyReportsExplicitFailure(finalTxt)))) || missingRequestedUndo;
                    if (shouldEnforce && guardRetries < 2 && iter + 1 < MaxToolIterations)
                    {
                        guardRetries++;
                        messages.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", string.IsNullOrEmpty(finalTxt) ? "(text omitted)" : finalTxt } });
                        messages.Add(new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", missingRequestedUndo
                                ? "[SYSTEM EXECUTION VERIFICATION ERROR]: You claimed you restored/reverted the drawing WITHOUT calling `undo_last_action`! Call `undo_last_action(steps: 1)` NOW before replying."
                                : "[SYSTEM EXECUTION VERIFICATION ERROR]: You did NOT execute any mutating CAD tool in AutoCAD yet. NEVER write fake bracketed tags like '[Executed tools in AutoCAD: ...]'. Call the actual CAD function tool(s) NOW (`annotate_block_coordinates`, `undo_last_action`, `convert_matching_shapes_to_block`, `create_block`, `modify_entities(block_name=...)`, `draw_block_legend`, `delete_entities`, `draw_entities`, `select_entities`, `copy_entities`, etc.) before replying." }
                        });
                        continue;
                    }
                    string sanitizedClaude = StripFakeToolLog(finalTxt);
                    if ((!hasMutated && (claimsDone || hasFakeLog)) || missingRequestedUndo)
                    {
                        string warnBanner = missingRequestedUndo
                            ? (isArabic ? "⚠️ [تحقق النظام]: الموديل لم يستدعِ `undo_last_action` للتراجع الفعلي." : "⚠️ [System Guard]: Model did not invoke `undo_last_action`.")
                            : (isArabic ? "⚠️ [تحقق النظام]: لم يتم تنفيذ أي أداة رسم أو تعديل فعلية داخل أوتوكاد في هذا الرد." : "⚠️ [System Guard]: No mutating CAD tool was executed in AutoCAD during this reply.");
                        return string.IsNullOrEmpty(sanitizedClaude) ? warnBanner : (warnBanner + "\n\n" + sanitizedClaude);
                    }
                    return string.IsNullOrEmpty(sanitizedClaude) ? "(Claude completed without text output)" : sanitizedClaude;
                }

                messages.Add(new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", contentBlocks }
                });

                List<object> toolResults = new List<object>();
                foreach (Dictionary<string, object> tu in toolUses)
                {
                    string tuId = GetStr(tu, "id", "");
                    string tuName = GetStr(tu, "name", "");
                    Dictionary<string, object> tuInput = AsDict(GetObj(tu, "input")) ?? new Dictionary<string, object>();
                    Dictionary<string, object> res = execTool(tuName, tuInput);
                    bool ok = res.ContainsKey("ok") && res["ok"] is bool && (bool)res["ok"];
                    if (ok && IsActionTool(tuName)) hasMutated = true;
                    if (ok && string.Equals(tuName, "undo_last_action", StringComparison.OrdinalIgnoreCase) && res.ContainsKey("undone_steps")) hasUndone = true;
                    toolResults.Add(new Dictionary<string, object>
                    {
                        { "type", "tool_result" },
                        { "tool_use_id", tuId },
                        { "content", _json.Serialize(res) },
                        { "is_error", !ok }
                    });
                }
                messages.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", toolResults }
                });
            }

            return isArabic
                ? "وصل Claude إلى الحد الأقصى لجولات الأدوات (" + MaxToolIterations + ")."
                : "Reached maximum tool iterations (" + MaxToolIterations + ").";
        }

        // ==================== OpenAI / Codex Native Loop (Responses & Chat Completions) ====================

        private string RunOpenAILoop(
            string apiKey,
            string model,
            string baseUrl,
            string apiMode,
            string systemPrompt,
            List<DirectTurnRecord> history,
            string userMessage,
            Func<string, Dictionary<string, object>, Dictionary<string, object>> execTool,
            Action<string> onProgress,
            Control uiControl,
            bool isArabic)
        {
            string cleanBase = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim().TrimEnd('/');
            bool useChat = string.Equals(apiMode, "chat_completions", StringComparison.OrdinalIgnoreCase);

            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + apiKey }
            };

            if (useChat)
                return RunOpenAIChatCompletionsLoop(cleanBase + "/chat/completions", headers, model, systemPrompt, history, userMessage, execTool, onProgress, uiControl, isArabic);

            return RunOpenAIResponsesLoop(cleanBase + "/responses", headers, model, systemPrompt, history, userMessage, execTool, onProgress, uiControl, isArabic);
        }

        private string RunOpenAIResponsesLoop(
            string url,
            Dictionary<string, string> headers,
            string model,
            string systemPrompt,
            List<DirectTurnRecord> history,
            string userMessage,
            Func<string, Dictionary<string, object>, Dictionary<string, object>> execTool,
            Action<string> onProgress,
            Control uiControl,
            bool isArabic)
        {
            List<object> inputItems = new List<object>();
            string pendingAudit = null;
            foreach (DirectTurnRecord turn in history)
            {
                if (turn.Role == "assistant")
                {
                    inputItems.Add(new Dictionary<string, object>
                    {
                        { "role", "assistant" },
                        { "content", turn.Text ?? "" }
                    });
                    if (!string.IsNullOrWhiteSpace(turn.ExecutedToolsSummary))
                        pendingAudit = turn.ExecutedToolsSummary;
                }
                else
                {
                    inputItems.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", FormatUserTextWithPendingAudit(turn.Text, ref pendingAudit) }
                    });
                }
            }
            inputItems.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", FormatUserTextWithPendingAudit(userMessage, ref pendingAudit) }
            });

            List<object> tools = new List<object>();
            foreach (Dictionary<string, object> spec in BuildToolSpecs())
            {
                tools.Add(new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "name", spec["name"] },
                    { "description", spec["description"] },
                    { "parameters", spec["parameters"] },
                    { "strict", false }
                });
            }

            bool hasMutated = false;
            bool hasUndone = false;
            int guardRetries = 0;
            for (int iter = 0; iter < MaxToolIterations; iter++)
            {
                if (onProgress != null)
                {
                    int roundNo = iter + 1;
                    MarshalUi(uiControl, delegate {
                        onProgress(isArabic ? "يتصل مباشر بـ OpenAI/Codex (" + model + ") - جولة " + roundNo + "..." : "Calling OpenAI/Codex (" + model + ") round " + roundNo + "...");
                    });
                }

                Dictionary<string, object> body = new Dictionary<string, object>
                {
                    { "model", model },
                    { "instructions", systemPrompt },
                    { "input", inputItems },
                    { "tools", tools },
                    { "store", false },
                    { "include", new string[] { "reasoning.encrypted_content" } }
                };

                Dictionary<string, object> resp = PostJsonSync(url, headers, body, onProgress, uiControl, isArabic);
                List<object> output = AsList(GetObj(resp, "output"));
                List<Dictionary<string, object>> fnCalls = new List<Dictionary<string, object>>();
                StringBuilder textOut = new StringBuilder();

                foreach (object itemObj in output)
                {
                    Dictionary<string, object> item = AsDict(itemObj);
                    if (item == null) continue;
                    string itype = GetStr(item, "type", "");
                    if (itype == "function_call") fnCalls.Add(item);
                    else if (itype == "message")
                    {
                        foreach (object partObj in AsList(GetObj(item, "content")))
                        {
                            Dictionary<string, object> part = AsDict(partObj);
                            if (part != null && GetStr(part, "type", "") == "output_text" && part.ContainsKey("text") && part["text"] != null)
                                textOut.Append(part["text"].ToString());
                        }
                    }
                }

                if (fnCalls.Count == 0)
                {
                    string directOut = GetStr(resp, "output_text", "");
                    string finalTxt = !string.IsNullOrWhiteSpace(textOut.ToString()) ? textOut.ToString().Trim() : directOut.Trim();
                    bool hasFakeLog = ContainsFakeToolLog(finalTxt);
                    bool claimsDone = ReplyClaimsCadExecution(finalTxt) || hasFakeLog;
                    bool claimsUndone = ReplyClaimsUndoOrRestore(finalTxt);
                    bool asksClarification = IsClarifyingQuestion(finalTxt);
                    bool missingRequestedUndo = UserRequestsUndoOrRestore(userMessage) && claimsUndone && !hasUndone && !ReplyReportsExplicitFailure(finalTxt);
                    bool shouldEnforce = (!hasMutated && (claimsDone || hasFakeLog || (UserRequestsCadAction(userMessage) && !asksClarification && !ReplyReportsExplicitFailure(finalTxt)))) || missingRequestedUndo;
                    if (shouldEnforce && guardRetries < 2 && iter + 1 < MaxToolIterations)
                    {
                        guardRetries++;
                        foreach (object outItem in output) inputItems.Add(outItem);
                        inputItems.Add(new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", missingRequestedUndo
                                ? "[SYSTEM EXECUTION VERIFICATION ERROR]: You claimed you restored/reverted the drawing WITHOUT calling `undo_last_action`! Call `undo_last_action(steps: 1)` NOW before replying."
                                : "[SYSTEM EXECUTION VERIFICATION ERROR]: You did NOT execute any mutating CAD tool in AutoCAD yet. NEVER write fake bracketed tags like '[Executed tools in AutoCAD: ...]'. Call the actual CAD function tool(s) NOW (`annotate_block_coordinates`, `undo_last_action`, `convert_matching_shapes_to_block`, `create_block`, `modify_entities(block_name=...)`, `draw_block_legend`, `delete_entities`, `draw_entities`, `select_entities`, `copy_entities`, etc.) before replying." }
                        });
                        continue;
                    }
                    string sanitizedResp = StripFakeToolLog(finalTxt);
                    if ((!hasMutated && (claimsDone || hasFakeLog)) || missingRequestedUndo)
                    {
                        string warnBanner = missingRequestedUndo
                            ? (isArabic ? "⚠️ [تحقق النظام]: الموديل لم يستدعِ `undo_last_action` للتراجع الفعلي." : "⚠️ [System Guard]: Model did not invoke `undo_last_action`.")
                            : (isArabic ? "⚠️ [تحقق النظام]: لم يتم تنفيذ أي أداة رسم أو تعديل فعلية داخل أوتوكاد في هذا الرد." : "⚠️ [System Guard]: No mutating CAD tool was executed in AutoCAD during this reply.");
                        return string.IsNullOrEmpty(sanitizedResp) ? warnBanner : (warnBanner + "\n\n" + sanitizedResp);
                    }
                    return string.IsNullOrEmpty(sanitizedResp) ? "(OpenAI completed without text output)" : sanitizedResp;
                }

                // Append raw model output items (including reasoning/encrypted_content & function_call)
                foreach (object outItem in output) inputItems.Add(outItem);

                foreach (Dictionary<string, object> fc in fnCalls)
                {
                    string callId = GetStr(fc, "call_id", "");
                    string fnName = GetStr(fc, "name", "");
                    string rawArgs = GetStr(fc, "arguments", "{}");
                    Dictionary<string, object> parsedArgs = null;
                    try { parsedArgs = _json.DeserializeObject(rawArgs) as Dictionary<string, object>; }
                    catch { parsedArgs = new Dictionary<string, object>(); }

                    Dictionary<string, object> res = execTool(fnName, parsedArgs ?? new Dictionary<string, object>());
                    bool ok = res != null && res.ContainsKey("ok") && res["ok"] is bool && (bool)res["ok"];
                    if (ok && IsActionTool(fnName)) hasMutated = true;
                    if (ok && string.Equals(fnName, "undo_last_action", StringComparison.OrdinalIgnoreCase) && res.ContainsKey("undone_steps")) hasUndone = true;
                    inputItems.Add(new Dictionary<string, object>
                    {
                        { "type", "function_call_output" },
                        { "call_id", callId },
                        { "output", _json.Serialize(res) }
                    });
                }
            }

            return isArabic
                ? "وصل OpenAI/Codex إلى الحد الأقصى لجولات الأدوات (" + MaxToolIterations + ")."
                : "Reached maximum tool iterations (" + MaxToolIterations + ").";
        }

        private string RunOpenAIChatCompletionsLoop(
            string url,
            Dictionary<string, string> headers,
            string model,
            string systemPrompt,
            List<DirectTurnRecord> history,
            string userMessage,
            Func<string, Dictionary<string, object>, Dictionary<string, object>> execTool,
            Action<string> onProgress,
            Control uiControl,
            bool isArabic)
        {
            List<object> messages = new List<object>();
            messages.Add(new Dictionary<string, object> { { "role", "system" }, { "content", systemPrompt } });
            string pendingAudit = null;
            foreach (DirectTurnRecord turn in history)
            {
                if (turn.Role == "assistant")
                {
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "assistant" },
                        { "content", turn.Text ?? "" }
                    });
                    if (!string.IsNullOrWhiteSpace(turn.ExecutedToolsSummary))
                        pendingAudit = turn.ExecutedToolsSummary;
                }
                else
                {
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", FormatUserTextWithPendingAudit(turn.Text, ref pendingAudit) }
                    });
                }
            }
            messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", FormatUserTextWithPendingAudit(userMessage, ref pendingAudit) } });

            List<object> tools = new List<object>();
            foreach (Dictionary<string, object> spec in BuildToolSpecs())
            {
                tools.Add(new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "function", new Dictionary<string, object>
                        {
                            { "name", spec["name"] },
                            { "description", spec["description"] },
                            { "parameters", spec["parameters"] }
                        }
                    }
                });
            }

            bool hasMutated = false;
            bool hasUndone = false;
            int guardRetries = 0;
            for (int iter = 0; iter < MaxToolIterations; iter++)
            {
                if (onProgress != null)
                {
                    int roundNo = iter + 1;
                    MarshalUi(uiControl, delegate {
                        onProgress(isArabic ? "يتصل مباشر بـ OpenAI Chat (" + model + ") - جولة " + roundNo + "..." : "Calling OpenAI Chat (" + model + ") round " + roundNo + "...");
                    });
                }

                Dictionary<string, object> body = new Dictionary<string, object>
                {
                    { "model", model },
                    { "messages", messages },
                    { "tools", tools }
                };

                Dictionary<string, object> resp = PostJsonSync(url, headers, body, onProgress, uiControl, isArabic);
                List<object> choices = AsList(GetObj(resp, "choices"));
                if (choices.Count == 0) throw new InvalidOperationException("OpenAI returned no choices.");

                Dictionary<string, object> msg = AsDict(GetObj(AsDict(choices[0]), "message"));
                if (msg == null) throw new InvalidOperationException("OpenAI choice missing message.");

                List<object> toolCalls = AsList(GetObj(msg, "tool_calls"));
                if (toolCalls.Count == 0)
                {
                    string txt = GetStr(msg, "content", "").Trim();
                    bool hasFakeLog = ContainsFakeToolLog(txt);
                    bool claimsDone = ReplyClaimsCadExecution(txt) || hasFakeLog;
                    bool claimsUndone = ReplyClaimsUndoOrRestore(txt);
                    bool asksClarification = IsClarifyingQuestion(txt);
                    bool missingRequestedUndo = UserRequestsUndoOrRestore(userMessage) && claimsUndone && !hasUndone && !ReplyReportsExplicitFailure(txt);
                    bool shouldEnforce = (!hasMutated && (claimsDone || hasFakeLog || (UserRequestsCadAction(userMessage) && !asksClarification && !ReplyReportsExplicitFailure(txt)))) || missingRequestedUndo;
                    if (shouldEnforce && guardRetries < 2 && iter + 1 < MaxToolIterations)
                    {
                        guardRetries++;
                        messages.Add(msg);
                        messages.Add(new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", missingRequestedUndo
                                ? "[SYSTEM EXECUTION VERIFICATION ERROR]: You claimed you restored/reverted the drawing WITHOUT calling `undo_last_action`! Call `undo_last_action(steps: 1)` NOW before replying."
                                : "[SYSTEM EXECUTION VERIFICATION ERROR]: You did NOT execute any mutating CAD tool in AutoCAD yet. NEVER write fake bracketed tags like '[Executed tools in AutoCAD: ...]'. Call the actual CAD function tool(s) NOW (`annotate_block_coordinates`, `undo_last_action`, `convert_matching_shapes_to_block`, `create_block`, `modify_entities(block_name=...)`, `draw_block_legend`, `delete_entities`, `draw_entities`, `select_entities`, `copy_entities`, etc.) before replying." }
                        });
                        continue;
                    }
                    string sanitizedChat = StripFakeToolLog(txt);
                    if ((!hasMutated && (claimsDone || hasFakeLog)) || missingRequestedUndo)
                    {
                        string warnBanner = missingRequestedUndo
                            ? (isArabic ? "⚠️ [تحقق النظام]: الموديل لم يستدعِ `undo_last_action` للتراجع الفعلي." : "⚠️ [System Guard]: Model did not invoke `undo_last_action`.")
                            : (isArabic ? "⚠️ [تحقق النظام]: لم يتم تنفيذ أي أداة رسم أو تعديل فعلية داخل أوتوكاد في هذا الرد." : "⚠️ [System Guard]: No mutating CAD tool was executed in AutoCAD during this reply.");
                        return string.IsNullOrEmpty(sanitizedChat) ? warnBanner : (warnBanner + "\n\n" + sanitizedChat);
                    }
                    return string.IsNullOrEmpty(sanitizedChat) ? "(OpenAI completed without text output)" : sanitizedChat;
                }

                messages.Add(msg);
                foreach (object tcObj in toolCalls)
                {
                    Dictionary<string, object> tc = AsDict(tcObj);
                    if (tc == null) continue;
                    string tcId = GetStr(tc, "id", "");
                    Dictionary<string, object> fn = AsDict(GetObj(tc, "function"));
                    string fnName = GetStr(fn, "name", "");
                    string rawArgs = GetStr(fn, "arguments", "{}");
                    Dictionary<string, object> parsedArgs = null;
                    try { parsedArgs = _json.DeserializeObject(rawArgs) as Dictionary<string, object>; }
                    catch { parsedArgs = new Dictionary<string, object>(); }

                    Dictionary<string, object> res = execTool(fnName, parsedArgs ?? new Dictionary<string, object>());
                    bool ok = res != null && res.ContainsKey("ok") && res["ok"] is bool && (bool)res["ok"];
                    if (ok && IsActionTool(fnName)) hasMutated = true;
                    if (ok && string.Equals(fnName, "undo_last_action", StringComparison.OrdinalIgnoreCase) && res.ContainsKey("undone_steps")) hasUndone = true;
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "tool" },
                        { "tool_call_id", tcId },
                        { "content", _json.Serialize(res) }
                    });
                }
            }

            return isArabic
                ? "وصل OpenAI إلى الحد الأقصى لجولات الأدوات (" + MaxToolIterations + ")."
                : "Reached maximum tool iterations (" + MaxToolIterations + ").";
        }

        // ==================== HTTP Helper ====================

        private Dictionary<string, object> PostJsonSync(
            string url,
            Dictionary<string, string> headers,
            Dictionary<string, object> body,
            Action<string> onProgress = null,
            Control uiControl = null,
            bool isArabic = false)
        {
            string payload = _json.Serialize(body);
            int[] delaysMs = new int[] { 700, 1600 };
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        req.Headers.TryAddWithoutValidation("User-Agent", "AI-Engineer-AutoCAD-Direct/1.0");
                        if (headers != null)
                        {
                            foreach (KeyValuePair<string, string> kv in headers)
                                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        }
                        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                        HttpResponseMessage resp = _http.SendAsync(req).GetAwaiter().GetResult();
                        string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        int code = (int)resp.StatusCode;
                        if (!resp.IsSuccessStatusCode)
                        {
                            string snippet = text ?? "";
                            try
                            {
                                Dictionary<string, object> errDoc = _json.DeserializeObject(text) as Dictionary<string, object>;
                                Dictionary<string, object> errObj = AsDict(GetObj(errDoc, "error"));
                                string errMsg = GetStr(errObj, "message", "");
                                if (!string.IsNullOrWhiteSpace(errMsg)) snippet = errMsg;
                            }
                            catch { }
                            if (snippet.Length > 700) snippet = snippet.Substring(0, 700);

                            bool isDailyQuota = (code == 429) && (snippet.IndexOf("per day", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                                snippet.IndexOf("free_tier_requests", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                                snippet.IndexOf("Please retry in", StringComparison.OrdinalIgnoreCase) >= 0 && snippet.IndexOf("h", StringComparison.OrdinalIgnoreCase) >= 0);
                            bool transient = !isDailyQuota && (code == 429 || code == 500 || code == 502 || code == 503 || code == 504);

                            if (transient && attempt < 2)
                            {
                                if (onProgress != null)
                                {
                                    int tryNo = attempt + 2;
                                    MarshalUi(uiControl, delegate {
                                        onProgress(isArabic
                                            ? "ضغط مؤقت على الخادم (HTTP " + code + ") - محاولة " + tryNo + "..."
                                            : "Temporary server busy (HTTP " + code + ") - retry " + tryNo + "...");
                                    });
                                }
                                System.Threading.Thread.Sleep(delaysMs[attempt]);
                                continue;
                            }

                            throw new ApiCallException(
                                code,
                                "API HTTP " + code + " (" + resp.ReasonPhrase + "): " + snippet,
                                snippet,
                                transient,
                                isDailyQuota);
                        }

                        Dictionary<string, object> parsed = _json.DeserializeObject(text) as Dictionary<string, object>;
                        if (parsed == null)
                            throw new ApiCallException(code, "API returned non-object JSON.", text, false, false);
                        return parsed;
                    }
                }
                catch (ApiCallException)
                {
                    throw;
                }
                catch (System.Exception netEx)
                {
                    if (attempt < 2 && !(netEx is ArgumentException))
                    {
                        if (onProgress != null)
                        {
                            int tryNo = attempt + 2;
                            MarshalUi(uiControl, delegate {
                                onProgress(isArabic
                                    ? "انقطع الاتصال أو تأخر الرد - إعادة المحاولة (" + tryNo + ")..."
                                    : "Connection dropped/timed out - retrying (" + tryNo + ")...");
                            });
                        }
                        System.Threading.Thread.Sleep(delaysMs[attempt]);
                        continue;
                    }
                    throw new ApiCallException(0, "Network error: " + netEx.Message, netEx.Message, true, false);
                }
            }
            throw new ApiCallException(0, "API request exhausted retries.", "", true, false);
        }

        // ==================== Engineering System Prompt ====================

        private static string BuildSystemPrompt(bool isArabic)
        {
            return BuildSystemPrompt(isArabic, DirectAiConfig.SortOrderAsk);
        }

        private static string BuildSystemPrompt(bool isArabic, string preferredSortOrder)
        {
            string normSort = DirectAiConfig.NormalizeSortOrderPref(preferredSortOrder);
            string uiSortPolicyLine = (normSort == DirectAiConfig.SortOrderAsk)
                ? "[UI Numbering Order Preference: 'ask' — The engineer chose per-drawing choice. If they specify left/right/top/bottom in chat, use that exact `sort_order`; if they have NOT specified any numbering direction yet for this drawing and are requesting callouts (`draw_callouts != false` / not `table_only`), call `inspect_drawing` first to verify the target block(s), then ask ONE brief question offering the 4 directions (`من اليمين للشمال`, `من الشمال لليمين`, `من فوق لتحت`, `من تحت لفوق`) before executing!]"
                : ("[UI Numbering Order Preference: Engineer selected `" + normSort + "` (" + DirectAiConfig.DescribeSortOrderPref(normSort, false) + ") in the UI dropdown for this drawing. Pass `sort_order: \"" + normSort + "\"` unless the engineer explicitly requests a different direction in chat.]");

            return
                "You are directly integrated inside AutoCAD (as a native .NET plugin talking straight to the engineer over API with ZERO intermediary blocking).\n" +
                "You have full engineering authority inside the active AutoCAD drawing through your function tools:\n" +
                "- Inspect & Query & Select: `inspect_drawing`, `query_entities`, `select_entities`, `get_plugin_diagnostics`\n" +
                "- Draft, Legends & Annotate: `draw_block_legend`, `annotate_block_coordinates`, `draw_entities` (circle, line, rect/square, polyline/polygon, arc, ellipse, text, mtext, leader, callout, dimension, linear_dimension, hatch, block_reference), `create_block`, `convert_matching_shapes_to_block`, `execute_template`\n" +
                "- Excel <-> AutoCAD Interop: `export_to_excel` (export AutoCAD tables, coordinate schedules, panel load schedules, BOQ, block legends, or selected tables to `.xlsx`/`.csv`), `import_excel_table` (import `.xlsx`/`.xlsm`/`.csv` spreadsheets into AutoCAD with crisp TrueType Arial Unicode formatting or native AutoCAD Table)\n" +
                "- MEP Load Schedules & Quantity Takeoff: `extract_load_schedule` (extract electrical circuit/panel load schedules from blocks + neighbor texts `63A`/`100A`/`15KW`/`4x16mm2` + sample connected feeder path lengths -> AutoCAD & Excel), `quantity_takeoff` (linear cable/pipe lengths by layer, closed room/polygon areas with optional centroid labels, block counts, vertical/waste allowances -> BOQ table & Excel)\n" +
                "- Lighting Design & DIALux Bridge: `lighting_design_engine` (automated room-by-room lighting calculation & symmetric grid placement using Egyptian Code ECP / EN 12464-1 / CIBSE, real .ies/.ldt photometric file parser, point-by-point Avg/Min Lux & Uniformity U0, 3-phase wiring & CAD/Excel/STF output), `export_dialux_stf` (export AutoCAD rooms + luminaire positions in meters to DIALux .stf + optional clean DWG)\n" +
                "- Attributes, Text, Geometry, QA/Cleanup, Block Library & Layouts/PDF: `manage_attributes` (extract/update/sync block attributes via Excel), `text_tools` (batch find/replace, directional auto-numbering, text style/height standardize), `advanced_geometry_ops` (align, distribute, rectangular/polar array, offset, revision_cloud, explode), `drawing_qa_cleanup` (duplicate block detector, overkill duplicate lines, purge unused, standardize ByLayer, DWG revision compare with clouds + Excel diff, FILEDIA/PICKFIRST guard), `layout_and_plot_tools` (list/create scaled Layouts + Viewports + Title Block, batch plot PDF via DWG To PDF.pc3), `manage_block_library` (persistent Wblock save/list/insert company blocks across drawings)\n" +
                "- Modify & Manage: `modify_entities`, `undo_last_action`, `copy_entities`, `delete_entities`, `manage_layers`, `duplicate_drawing`, `zoom_view`, `approve_or_rollback_preview`\n" +
                "- Measure & Compare: `measure_and_compare` (distances, center-to-center vs clear edge-to-edge gap, overlap detection, entity group totals/areas/lengths/extents comparison)\n\n" +
                "MANDATORY ENGINEERING RULES:\n" +
                "1. NEVER say 'I cannot draw', 'I am read-only', 'Switch to Project Agent', or 'I lack drawing access'. You HAVE full read/write/select CAD tools right here!\n" +
                "2. CHECK UNITS & REAL DRAWING SPACE FIRST: Before drawing, selecting, or answering questions about an existing drawing, call `inspect_drawing` to read `insunits`, `units_per_cm`, `units_per_m`, `current_space`, `named_blocks_total`, `named_visible_blocks_total`, `anonymous_system_blocks_total`, `named_block_counts`, `anonymous_or_system_block_counts`, `paperspace_layouts`, `recent_tail_layer_counts`, `recent_tail_entities`, and `extents`. Convert metric units cleanly (`value_in_drawing_units = cm * units_per_cm` = `m * units_per_m`). Note: when `insunits` is `Undefined` (unitless, `code=0`), check existing extents/entities and mention the scale used!\n" +
                "3. ACCURATE BLOCK COUNTING IN REAL DRAWINGS: In real AutoCAD files, ModelSpace often contains BOTH user-named blocks (`named_blocks_total`, e.g., lamps/panels/PLC/connectors) AND internal anonymous/system blocks (`*X...` dimensions/tables, `AVE_RENDER`, `AVE_GLOBAL` in `anonymous_or_system_block_counts`), plus blocks inside PaperSpace Layouts (`paperspace_layouts`) or hidden Off/Frozen layers. ALWAYS state the **real user-named visible blocks count FIRST** and list anonymous/system/hidden/PaperSpace counts separately so the count matches what the engineer sees on screen! When querying/selecting/copying all user blocks, pass `entity_type: \"BlockReference\", exclude_anonymous: true, exclude_system: true` (omit `block_name` unless filtering a specific block).\n" +
                "4. MANDATORY BLOCK LEGEND TOOL (`draw_block_legend`): Whenever the engineer asks for a Block Legend / Schedule / Key Table ('جدول البلوكات', 'مفاتيح البلوكات', 'شكل البلوك والوصف والعدد', 'block legend', 'separating table from drawing'), NEVER hand-craft lines and SHX `text` via `draw_entities`! ALWAYS call `draw_block_legend` with clear `descriptions` for every named block (e.g. `{block_name, description}` entries). `draw_block_legend` automatically uses TrueType Arial Unicode MText (NO `????` boxes), scales real centered block symbol thumbnails inside `شكل البلوك` while preserving each block's ModelSpace color (Red, Cyan, etc.), sizes grid rows cleanly without merged-header divider glitches, separates the table cleanly outside the drawing bounds, and zooms onto it!\n" +
                "5. REMOVING OLD/BROKEN TABLES ON LARGE DRAWINGS (`query_entities` with `order: 'newest_first'`): On drawings with thousands of entities (like 15,000+), recently drawn or broken overlapping AI tables sit at the TAIL of ModelSpace. Inspect `inspect_drawing.recent_tail_entities` / `recent_tail_layer_counts` AND call `query_entities(order: 'newest_first', limit: 200)` or `query_entities(layer: ..., order: 'newest_first')` to find old table lines/polylines/texts, pass their handles to `delete_entities` or `draw_block_legend(clear_handles: [...], clear_layers: [...])` BEFORE drawing the clean separated legend!\n" +
                "6. REAL ON-SCREEN SELECTION (`select_entities`): Whenever the engineer asks to **select** or **highlight** items in the drawing ('حدد', 'اعمل تحديد', 'select', 'تظليل'), DO NOT just run `query_entities`! ALWAYS call `select_entities` (with `entity_type: \"BlockReference\"` or handles, `zoom: true`, and `highlight_box: true` when framing/highlighting helps) so AutoCAD switches to ModelSpace if needed, highlights them with real blue AutoCAD grips, and zooms directly onto them!\n" +
                "6b. SAFE TARGETED BLOCK RECOLORING, RENAMING & LEGEND SYNC (`modify_entities`): NEVER pass `layer_filter: \"0\"` to `modify_entities` when changing a specific block — blocks normally reside on Layer `0` alongside thousands of unrelated lines! Always pass `block_name` (or `handles`/`selected_only`) directly (e.g. `modify_entities(block_name: \"connector\", color_index: 4, new_block_name: \"terminals\")`). To genuinely rename a CAD block definition (`BlockTableRecord`), pass `new_block_name` inside `modify_entities` (NEVER hack legend MText to fake a block rename!). `modify_entities` automatically syncs matching thumbnails and block names in any existing `_AI_BLOCK_LEGEND` and focuses zoom cleanly — so do NOT call `draw_block_legend` right after `modify_entities` unless no legend exists yet or descriptions changed! Never pass `confirm_bulk_layer: true` unless the user literally asked to alter an entire layer.\n" +
                "6c. REAL UNDO / REVERT (`undo_last_action`): Whenever the engineer asks to undo/revert the previous step or restore previous colors ('رجع الخطوة', 'تراجع', 'اللون السابق', 'undo', 'revert'), ALWAYS call `undo_last_action` FIRST (`steps: 1` or inspect with `action: \"list_history\"`), rather than guessing `modify_entities` handles! Only invoke `undo_last_action(steps: ...)` when the engineer actually asks to undo — NOT for diagnostic 'why did it revert?' questions! If `undo_last_action` reports no snapshots exist from this session, tell the engineer honestly (NEVER claim you restored the drawing if `undo_last_action` did not succeed).\n" +
                "6d. ZERO SHAPE HALLUCINATION & REAL SELECTION-TO-BLOCK CONVERSION (`create_block`, `convert_matching_shapes_to_block`): NEVER guess, synthesize, or manually approximate `entities` inside `create_block` when the engineer asks to turn selected drawing objects or matching shapes into a block!\n" +
                "   - To block ONLY the currently selected AutoCAD objects (without searching for copies): call `create_block(block_name: \"...\", selected_only: true)` — it clones the real AutoCAD entities directly via DeepCloneObjects.\n" +
                "   - Whenever the engineer asks to search the drawing for all shapes matching/similar to what they selected ('ابحث عن الاشكال التي تشبه ما اخترت', 'نفس الشكل بالمخطط', 'convert matching/similar shapes to block') and convert all matching shapes into a block: ALWAYS call `convert_matching_shapes_to_block(block_name: \"...\", selected_only: true)` directly! It deterministically matches every geometrically similar CAD cluster across translations, full 0-360 deg rotations (including 0-180 deg), mirror reflections, uniform scales (`allow_scale` automatically enabled for symbol clusters with circles/arcs or >=5 elements), split `Line` vs `Polyline` / dual-arc `Circle` representations (`normalize_geometry`), and minor drafting tolerances/differences (`tolerance_ratio`, `size_tolerance_ratio`, `fuzzy_match` / `min_match_ratio`), clones the real selected geometry via DeepCloneObjects, replaces every match with `BlockReference`, and supports full `undo_last_action`. Pass `reuse_existing_block: true` when adding newly matched shapes to an existing block, and `dry_run: true` only when asked to preview/highlight matches first. Do NOT pass `layer_filter` unless specifically requested.\n" +
                "   - CRITICAL SELECTION SAFETY: If `convert_matching_shapes_to_block` / `create_block` fails because no objects are selected OR an AutoCAD command (`CMDACTIVE != 0`, e.g. COPY/MOVE) is active, **NEVER** call `select_entities` to guess or grab arbitrary polylines/lines from the drawing! Stop immediately and ask the engineer to press Esc in AutoCAD (if inside a command), select the target symbol cleanly, and retry. Never pass `overwrite: true` unless the engineer explicitly asks to replace an existing block definition.\n" +
                "   - After converting into a new block, call `draw_block_legend` ONLY if the drawing has an existing legend or the engineer asked for/updated the Block Legend.\n" +
                "   - If `inspect_drawing` / `convert_matching_shapes_to_block` reports `selection_count == 0`, NEVER invent fake block geometry — politely ask the engineer to select one sample shape in AutoCAD and retry.\n" +
                "6e. BLOCK COORDINATES, USER-CHOSEN NUMBERING ORDER (RIGHT / LEFT / TOP / BOTTOM), NON-OVERLAPPING LEADER ARROWS & EXTERNAL TABLE (`annotate_block_coordinates`): Whenever the engineer asks to extract block X/Y coordinates, number blocks, write coordinates cleanly in empty space with arrows/leaders pointing to block centers, create an AutoCAD coordinate table (`جدول الإحداثيات: رقم البلوك | X | Y`), change numbering direction, or move/adjust ONLY the coordinate table (`انقل جدول الإحداثيات خارج حدود المخطط`): ALWAYS call `annotate_block_coordinates`! NEVER use `dimension`/`aligned_dimension`/`rotated` for coordinates, NEVER hand-guess `draw_entities` MText/line coordinates from `inspect_drawing`, and NEVER run `undo_last_action` just to move the coordinate table!\n" +
                "   - NUMBERING ORDER (`sort_order`) MUST MATCH THE ENGINEER'S CHOICE PER DRAWING (NEVER blindly hardcode left-to-right for every drawing!):\n" +
                "     * `'left_to_right'` = من الشمال/اليسار لليمين\n" +
                "     * `'right_to_left'` = من اليمين للشمال/لليسار\n" +
                "     * `'top_to_bottom'` = من فوق/أعلى لتحت/أسفل (صفوف من اليسار لليمين) | `'top_to_bottom_rtl'` = من فوق لتحت (صفوف من اليمين لليسار)\n" +
                "     * `'bottom_to_top'` = من تحت/أسفل لفوق/أعلى (صفوف من اليسار لليمين) | `'bottom_to_top_rtl'` = من تحت لفوق (صفوف من اليمين لليسار)\n" +
                "     * Explicit direction in the engineer's chat message ALWAYS overrides any UI dropdown. If the UI dropdown is `'ask'` AND the engineer did NOT specify a numbering direction yet in chat (and isn't merely asking `table_only: true`), call `inspect_drawing` first to check the blocks and ask ONE short clarifying question ending cleanly with options (right-to-left, left-to-right, top-to-bottom, bottom-to-top) so they choose for this drawing. As soon as they answer with a direction (e.g. `'من اليمين'`, `'من فوق لتحت'`, `'من تحت'`), execute `annotate_block_coordinates` immediately with that exact `sort_order`!\n" +
                "     * When asked to CHANGE numbering direction (`غير الترقيم...`, `رقمهم من...`), call `annotate_block_coordinates` with BOTH `draw_callouts: true, draw_table: true` (NEVER `table_only: true`) so both arrows and table update cleanly.\n" +
                "   - If the engineer selected a single block instance (e.g. one `Fuse`) and asked to annotate/extract coordinates for that block, pass `selected_only: true` (or explicit `block_name` if named) and omit `all_instances` — it automatically expands a single sample pick to ALL insertions of that block type in ModelSpace (`selection_expanded_from_sample: true`).\n" +
                "   - Every coordinate callout now draws a native AutoCAD **`Leader`** entity whose `Vertex 0` (closed filled arrowhead tip) points strictly to the exact `(X, Y)` coordinate inside the block (`arrow_target: 'geometric_center'` or `'insertion_point'` or exact picked sample points), sizes readable TrueType Arial Unicode text proportional to drawing units, runs spatial obstacle avoidance so labels/shelves never collide with other callouts or drawing entities, places TrueType text STRICTLY ABOVE the horizontal leader shelf, and places `_AI_COORD_TABLE` cleanly outside drawing bounds AND non-overlapping any existing `_AI_BLOCK_LEGEND`.\n" +
                "   - MULTI-POINT SAMPLE BLOCK PICKING (`pick_sample_points: true`, `multi_point_mode: 'sequential' | 'grouped'`): Whenever the engineer wants to pick 1 or more points inside a sample block (e.g. corners/reference points of a concrete footing block) so `Leader` arrowheads point to ALL those points on every block with the same name: call `annotate_block_coordinates(..., pick_sample_points: true, multi_point_mode: ...)`! AutoCAD lets them click N points inside the sample block (transforming via `BlockTransform` across all rotated/scaled/mirrored insertions of that block). Support BOTH numbering modes:\n" +
                "     * `'sequential'` (كل نقطة تأخذ رقم وراء بعض): `#1, #2, #3, #4...` sequentially across all points of all blocks in order.\n" +
                "     * `'grouped'` (ترقيم البلوك وأمامه صفوف إحداثيات النقط): each block gets `#1, #2...`, leaders show `#1.1, #1.2...`, and the table shows Block `#` with sub-rows (`P1, P2...` and X/Y per point).\n" +
                "     * Note: picked sample points are remembered per block in the active drawing so changing `sort_order`, switching `multi_point_mode`, or moving `table_only: true` does NOT force re-clicking unless `pick_sample_points: true` (re-pick) or `clear_sample_points: true` (reset to block center) is passed.\n" +
                "   - MATCHING AN EXISTING EDITED LEADER ARROWHEAD (`reference_callout_index` / `from_selected_leader`): Whenever the engineer adjusted one Leader's arrowhead on the drawing and asks to keep that block/Leader fixed and update ALL other arrowheads + coordinates to match that same relative offset inside the block (e.g. `'ثبت سهم البلوك رقم 16 وغير باقي الأسهم زيه'` / `'حسب وضع رأس السهم رقم 16 من البلوك'`): **NEVER call `modify_entities` to rotate or move the blocks!** Instead, call `annotate_block_coordinates(reference_callout_index: 16)` (or `from_selected_leader: true` if they selected the edited Leader) directly! It reads Leader #16's current `Vertex 0` arrowhead position, computes its local offset inside Block #16, and updates ALL matching blocks' Leaders, X/Y coordinates, and `_AI_COORD_TABLE` cleanly without touching any block!\n" +
                "   - To move or regenerate ONLY the coordinate table without deleting/regenerating callout leaders on the drawing, call `annotate_block_coordinates(..., table_only: true, sort_order: <same sort_order used for existing leaders>, table_position: 'outside_left' | 'outside_right' | 'outside_top' | 'outside_bottom')`.\n" +
                "   - For single ad-hoc leader callout arrows to arbitrary coordinates, use `draw_entities` with `type: \"leader\"` (`target`, `label_pos`, `text`, `height`, `arrow_size`).\n" +
                "6f. TWO-WAY EXCEL <-> AUTOCAD SCHEDULES (`export_to_excel`, `import_excel_table`): You HAVE native C# OpenXML `.xlsx`/`.xlsm`/`.csv` support built right into the plugin — NEVER say you cannot export to Excel or import from Excel!\n" +
                "   - EXPORT (`export_to_excel`): Whenever the engineer asks to export the coordinate table (`استخرج الجدول ده إلى إكسيل`), block legend/BOQ, currently selected AutoCAD `Table` or text grid, or any table to Excel: call `export_to_excel(source: 'auto' | 'coordinates' | 'block_legend' | 'selected_table' | 'custom', ...)` (or pass `export_excel: true` directly inside `annotate_block_coordinates` / `draw_block_legend`). Pass `browse: true` if they ask to choose where to save (`اختار مكان الحفظ`), or `open_file: true` to open Excel automatically; otherwise save directly cleanly and state the exact returned `file_path`.\n" +
                "   - IMPORT (`import_excel_table`): Whenever the engineer asks to import/pull a spreadsheet table from Excel into AutoCAD (`اسحب جدول من الإكسيل`, `ادخل جدول إكسيل للرسمة`, `import excel`): call `import_excel_table`. If they provided a path, pass `file_path`; if they did NOT specify a path yet, pass `browse: true` (or omit `file_path`) so Windows opens a file picker dialog (`OpenFileDialog`) for them to pick `.xlsx`/`.csv` cleanly! Supports multi-sheet selection (`sheet_name`), Arabic RTL (`_AI_UNICODE` Arial Unicode grid outside drawing extents), or native AutoCAD Table (`use_autocad_table: true`), plus full `undo_last_action`.\n" +
                "6g. ELECTRICAL LOAD SCHEDULES, NEIGHBOR TEXT ASSOCIATION & QUANTITY TAKEOFF (`extract_load_schedule`, `quantity_takeoff`): Whenever the engineer asks to extract a Panel / Breaker / Feeder Load Schedule from blocks + nearby labels (`63A`, `100A`, `SPARE`, `KW`, `4x16mm2`) OR trace sample feeder paths to measure circuit lengths (`استخراج جدول أحمال من مسارات عينة`, `حصر القواطع بالأمبير`): ALWAYS call `extract_load_schedule` (pass `export_excel: true` when they also want `.xlsx`)! Whenever asked to calculate total cable/route lengths by layer, closed room/polygon areas (including stamping area labels inside rooms via `label_areas: true`), or full material BOQ with waste % / vertical allowance: call `quantity_takeoff`!\n" +
                "6h. ATTRIBUTES & BATCH TEXT TOOLS (`manage_attributes`, `text_tools`): Use `manage_attributes` to extract all BlockReference attributes to Excel (`action: 'extract'`), bulk-update attribute tags (`action: 'update'`), or sync edited attributes back from Excel (`action: 'sync_from_excel'`). Use `text_tools` for batch find & replace across text/mtext/attributes (`action: 'find_replace'`), directional auto-numbering `C1..Cn` (`action: 'auto_number'`), or height/Arial Unicode style standardization (`action: 'standardize'`).\n" +
                "6i. ADVANCED GEOMETRY, QA/CLEANUP, DWG COMPARE, BLOCK LIBRARY & LAYOUTS/PDF (`advanced_geometry_ops`, `drawing_qa_cleanup`, `layout_and_plot_tools`, `manage_block_library`): Never manually loop single entity moves/deletes when an exact batch engineering tool exists! Use `advanced_geometry_ops` for `align`, `distribute`, `array_rect`, `array_polar`, `offset`, `revision_cloud`, or `explode`. Use `drawing_qa_cleanup` for `audit_duplicates` (detect/clean stacked duplicate blocks!), `overkill`, `purge`, `standardize_bylayer`, `compare_dwg` (revision clouds + Excel diff), or `check_env`. Use `layout_and_plot_tools` for scaled PaperSpace Layouts/Viewports and `batch_plot_pdf`. Use `manage_block_library` (`save`/`list`/`insert`) for reusable company blocks across projects!\n" +
                "6j. LIGHTING DESIGN, IES/LDT PHOTOMETRY & DIALUX STF EXPORT (`lighting_design_engine`, `export_dialux_stf`): Whenever the engineer asks to design/distribute lighting automatically according to Egyptian Code (`ECP`) / `EN 12464-1` / `CIBSE`, calculate Lux/Uniformity from `.ies`/`.ldt` files, balance lighting on 3 phases, or export an AutoCAD lighting plan to DIALux evo (`.stf`): ALWAYS call `lighting_design_engine` and/or `export_dialux_stf`! Note honestly that DIALux evo imports `.stf` (`File -> Import -> STF`) cleanly to skip manual room/fixture modeling, while official DIALux evo has no external headless API for running DIALux's own internal raytracer or PDF export.\n" +
                "7. SAFE COPYING OUTSIDE LARGE DRAWINGS & FRAMING: Whenever asked to copy elements outside the drawing ('نسخة خارج المخطط') or put ONE frame/box around them ('مربع واحد'), use `copy_entities` with `entity_type: \"BlockReference\"` (or handles/filters), `auto_offset_outside: \"right\"`, `draw_frame: true` + concise `frame_label` if they want one bounding box around all copies, and `select_copied: true` / `zoom: true`.\n" +
                "8. GEOMETRY PRECISION & SELF-VERIFICATION: When placing shapes side-by-side at a clear distance, inspect the exact `bounds` returned by `draw_entities` and call `measure_and_compare` (mode='distance'); if ANY coordinate or gap differs from what the engineer requested, IMMEDIATELY correct it using `modify_entities` BEFORE giving your final answer!\n" +
                "9. STRICT TRUTH & NO HALLUCINATION: NEVER claim you extracted coordinates/schedules/BOQ, calculated/distributed lighting, exported `.stf`/Excel/PDF files, cleaned/purged/compared drawings, added arrows/tables/clouds, deleted, aligned, arrayed, separated, selected, renamed, converted into a block, or drew anything unless the corresponding tool (`lighting_design_engine`, `export_dialux_stf`, `extract_load_schedule`, `quantity_takeoff`, `manage_attributes`, `text_tools`, `advanced_geometry_ops`, `drawing_qa_cleanup`, `layout_and_plot_tools`, `manage_block_library`, `export_to_excel`, `import_excel_table`, `annotate_block_coordinates`, `draw_block_legend`, `draw_entities`, `delete_entities`, `copy_entities`, `modify_entities`, `create_block`, `convert_matching_shapes_to_block`, `select_entities`, `zoom_view`) ACTUALLY ran and returned `ok: true` in this turn! NEVER copy or write bracketed logs like `[Executed tools in AutoCAD: ...]` in your reply!\n" +
                "10. CODE TRANSPARENCY & DIAGNOSTIC QUESTIONS: When the engineer asks WHY something reverted/failed ('ليه تم التراجع', 'هل فية مشكلة بالكود'), do NOT call `undo_last_action(steps: ...)` or repeat mutations — inspect `undo_last_action(action: \"list_history\")` / `get_plugin_diagnostics` read-only and answer clearly and honestly. If any plugin tool fails or lacks a CAD capability, report the EXACT error/limitation, name `integrations/autocad_context_plugin/CadToolExecutor.cs`, and explain the fix.\n" +
                (isArabic
                    ? "11. Respond in clear, natural Arabic matching the engineer's tone, keeping CAD coordinates/handles/measurements exact and concise.\n"
                    : "11. Respond in clear, concise technical English with exact measurements, handles, and layer names.\n") +
                uiSortPolicyLine;
        }

        // ==================== Neutral Schema Builder (Gemini / Claude / OpenAI Compatible) ====================

        public static List<Dictionary<string, object>> BuildToolSpecs()
        {
            List<Dictionary<string, object>> tools = new List<Dictionary<string, object>>();

            tools.Add(Tool("inspect_drawing",
                "Inspect active AutoCAD drawing: file name, insunits, units_per_cm, units_per_m, current_space, total_entities, named_blocks_total, named_visible_blocks_total, anonymous_system_blocks_total, paperspace_layouts, recent_tail_layer_counts, recent_tail_entities, selection_count, selected_handles, selected_type_counts, selected_bounds, selected_entities (actual picked CAD entities with geometry), layers, blocks, extents, text_samples, and entities.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "include_entities", Prop("boolean", "Include serialized entity list (default true; pass false on huge drawings when only counts/layers/blocks are needed).") },
                    { "max_entities", Prop("integer", "Max entities returned, 1..200 (default 120).") }
                })));

            tools.Add(Tool("query_entities",
                "Search and filter entities in ModelSpace (or paperspace/all) by layer, entity_type, block_name, text_contains, selected_only, visibility, handles, or bounding box. Supports order='newest_first' to find recently drawn tables at the tail of huge drawings.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "layer", Prop("string", "Exact layer name filter.") },
                    { "entity_type", Prop("string", "Substring filter on .NET class name (e.g. Circle, Line, Polyline, DBText, MText, BlockReference, Hatch, Dimension).") },
                    { "block_name", Prop("string", "Substring filter on inserted BlockReference name.") },
                    { "text_contains", Prop("string", "Substring filter on DBText, MText, Dimension text, or block attributes.") },
                    { "selected_only", Prop("boolean", "Only inspect currently picked/selected AutoCAD objects.") },
                    { "exclude_anonymous", Prop("boolean", "Exclude anonymous blocks starting with '*' (e.g. *X127 dimension/table blocks).") },
                    { "exclude_system", Prop("boolean", "Exclude internal system blocks (e.g. AVE_RENDER, AVE_GLOBAL, layer ASHADE).") },
                    { "visible_only", Prop("boolean", "Only include visible entities on On/Thawed layers.") },
                    { "space", Prop("string", "Search space: 'model' (default), 'paperspace', or 'all'.") },
                    { "order", Prop("string", "Iteration order: 'oldest_first' (default) or 'newest_first' (tail-first, essential on large drawings to locate recently added entities).") },
                    { "handles", ArrProp("string", "Filter to specific entity hex handles.") },
                    { "bbox", ArrProp("number", "Filter intersecting 2D bounding box [minX, minY, maxX, maxY].") },
                    { "limit", Prop("integer", "Max serialized entities returned, 1..500 (default 120).") },
                    { "offset", Prop("integer", "Pagination offset (default 0).") }
                })));

            tools.Add(Tool("select_entities",
                "Select entities visibly in AutoCAD viewport with blue selection grips (SetImpliedSelection), switch to Model tab if needed, optionally zoom directly onto them, and optionally draw a single highlight frame box.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "handles", ArrProp("string", "Specific hex handles to select (optional if filter criteria are provided).") },
                    { "layer", Prop("string", "Select all entities on this exact layer.") },
                    { "entity_type", Prop("string", "Substring filter on .NET class name (e.g. BlockReference, Circle, Line).") },
                    { "block_name", Prop("string", "Substring filter on inserted BlockReference name.") },
                    { "text_contains", Prop("string", "Substring filter on text/attributes.") },
                    { "exclude_anonymous", Prop("boolean", "Exclude anonymous '*' blocks when filtering BlockReferences.") },
                    { "exclude_system", Prop("boolean", "Exclude internal AVE_* / ASHADE system blocks.") },
                    { "visible_only", Prop("boolean", "Select only entities on visible/thawed layers.") },
                    { "bbox", ArrProp("number", "2D bounding box filter [minX, minY, maxX, maxY].") },
                    { "clear", Prop("boolean", "If true, clears the current AutoCAD selection.") },
                    { "zoom", Prop("boolean", "Zoom directly to selected entities bounds (default true).") },
                    { "highlight_box", Prop("boolean", "Draw one rectangular bounding box around selected entities.") },
                    { "highlight_layer", Prop("string", "Layer for optional highlight box (default _AI_HIGHLIGHT).") },
                    { "highlight_color_index", Prop("integer", "ACI color for optional highlight box (default 2=Yellow).") }
                })));

            Dictionary<string, object> entityItemProps = new Dictionary<string, object>
            {
                { "type", Prop("string", "Entity kind: circle, line, rect, rectangle, square, polyline, polygon, arc, ellipse, text, mtext, leader, callout, dimension, aligned_dimension, linear_dimension, hatch, block_reference, insert.") },
                { "layer", Prop("string", "Optional per-entity layer name.") },
                { "color_index", Prop("integer", "Optional AutoCAD ACI color index: 1=Red, 2=Yellow, 3=Green, 4=Cyan, 5=Blue, 6=Magenta, 7=White/Black, 256=ByLayer.") },
                { "linetype", Prop("string", "Optional linetype if creating a new layer (e.g. Continuous, CENTER, DASHED).") },
                { "center", ArrProp("number", "[x, y] center for circle, arc, ellipse, or rect/square.") },
                { "radius", Prop("number", "Radius in drawing units (circle, arc).") },
                { "diameter", Prop("number", "Diameter in drawing units (circle).") },
                { "start", ArrProp("number", "[x, y] start point for line.") },
                { "end", ArrProp("number", "[x, y] end point for line.") },
                { "x", Prop("number", "Bottom-left X for rect/square/hatch.") },
                { "y", Prop("number", "Bottom-left Y for rect/square/hatch.") },
                { "width", Prop("number", "Width for rect/square/mtext/hatch.") },
                { "height", Prop("number", "Height for rect/square/hatch/text/mtext/leader.") },
                { "side", Prop("number", "Side length for square.") },
                { "p1", ArrProp("number", "[x, y] corner 1 for rect or start point for dimension.") },
                { "p2", ArrProp("number", "[x, y] corner 2 for rect or end point for dimension.") },
                { "dim_line_point", ArrProp("number", "[x, y] location point on dimension line.") },
                { "text_override", Prop("string", "Optional custom dimension text.") },
                { "target", ArrProp("number", "[x, y] exact arrowhead tip point for leader/callout.") },
                { "label_pos", ArrProp("number", "[x, y] shelf text center position for leader/callout (text is drawn strictly above the shelf).") },
                { "arrow_size", Prop("number", "Optional filled arrowhead length in drawing units for leader/callout.") },
                { "arrow_color_index", Prop("integer", "Optional ACI color index for leader/callout arrowhead and line segments.") },
                { "points", PtArrayProp("Array of [x, y] vertices for polyline, polygon, or hatch.") },
                { "closed", Prop("boolean", "Close polyline loop.") },
                { "constant_width", Prop("number", "Polyline constant width in drawing units.") },
                { "start_angle", Prop("number", "Arc start angle in degrees.") },
                { "end_angle", Prop("number", "Arc end angle in degrees.") },
                { "major_radius", Prop("number", "Ellipse major semi-axis.") },
                { "minor_radius", Prop("number", "Ellipse minor semi-axis.") },
                { "rotation_deg", Prop("number", "Rotation angle in degrees (text, mtext, ellipse, block_reference, linear_dimension).") },
                { "position", ArrProp("number", "[x, y] insertion point for text, mtext, or block_reference.") },
                { "text", Prop("string", "Text content for text or mtext.") },
                { "align", Prop("string", "Text alignment: left, center, middle_center, right.") },
                { "pattern_name", Prop("string", "Hatch pattern name (e.g. SOLID, ANSI31, ANSI32, NET).") },
                { "pattern_scale", Prop("number", "Hatch pattern scale.") },
                { "block_name", Prop("string", "Existing BlockTableRecord name for block_reference.") },
                { "scale", ArrProp("number", "Optional [sx, sy] or [sx, sy, sz] scale for block_reference.") }
            };

            tools.Add(Tool("draw_entities",
                "Draw one or more native AutoCAD entities directly in ModelSpace with full Undo support. All coordinates and dimensions are in ACTIVE DRAWING UNITS (check units_per_cm from inspect_drawing first!).",
                ObjSchema(new Dictionary<string, object>
                {
                    { "entities", new Dictionary<string, object>
                        {
                            { "type", "array" },
                            { "description", "List of geometry/text/dimension/hatch/block objects to draw." },
                            { "items", ObjSchema(entityItemProps, new string[] { "type" }) }
                        }
                    },
                    { "layer", Prop("string", "Default layer name for drawn entities (created automatically if missing).") },
                    { "color_index", Prop("integer", "Optional default ACI color index (e.g. 256=ByLayer, 1=Red).") },
                    { "preview_only", Prop("boolean", "If true, draws in red on _AI_PREVIEW_REVIEW for optional review.") },
                    { "zoom", Prop("boolean", "Zoom view after drawing (default true). On large drawings, automatically focuses created entities.") },
                    { "zoom_mode", Prop("string", "Optional zoom mode: 'auto' (default), 'created', or 'extents'.") }
                }, new string[] { "entities" })));

            tools.Add(Tool("draw_block_legend",
                "Draw a separated, clean, non-overlapping AutoCAD Block Legend schedule (مفاتيح البلوكات) outside drawing extents with TrueType Arial Unicode Arabic/English text (never '????'), auto-scaled centered real block symbol thumbnails (شكل البلوك) preserving ModelSpace block colors, engineering descriptions, quantities, and totals. Can also erase old/broken table handles first.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "descriptions", new Dictionary<string, object>
                        {
                            { "type", "array" },
                            { "description", "Engineering descriptions for each block shown in the legend." },
                            { "items", ObjSchema(new Dictionary<string, object>
                                {
                                    { "block_name", Prop("string", "Exact BlockTableRecord name from inspect_drawing / query_entities.") },
                                    { "description", Prop("string", "Clear Arabic or bilingual engineering description of the block's function.") }
                                }, new string[] { "block_name", "description" })
                            }
                        }
                    },
                    { "block_names", ArrProp("string", "Optional subset of block names to include (omit to include all named non-system blocks found in ModelSpace).") },
                    { "title", Prop("string", "Optional table title heading (default 'جدول حصر ومفاتيح البلوكات (BLOCK LEGEND)').") },
                    { "position", Prop("string", "Placement outside drawing extents: 'outside_right' (default), 'outside_left', 'outside_top', or 'outside_bottom'.") },
                    { "gap", Prop("number", "Optional custom clearance gap outside drawing extents in active drawing units.") },
                    { "layer", Prop("string", "Target layer for legend entities (default '_AI_BLOCK_LEGEND').") },
                    { "replace_existing", Prop("boolean", "Erase existing entities on 'layer' before drawing new legend (default true).") },
                    { "clear_handles", ArrProp("string", "Optional hex handles of previously drawn/broken tables to erase BEFORE measuring drawing bounds.") },
                    { "clear_layers", ArrProp("string", "Optional non-0 layer names of old tables to erase BEFORE measuring drawing bounds.") },
                    { "visible_only", Prop("boolean", "Only count visible block insertions on On/Thawed layers (default false).") },
                    { "include_system_blocks", Prop("boolean", "Include internal AVE_* / ASHADE system blocks (default false).") },
                    { "export_excel", Prop("boolean", "Also save this Block Legend schedule directly to an Excel .xlsx file cleanly.") },
                    { "excel_path", Prop("string", "Optional destination .xlsx file path when export_excel=true.") },
                    { "zoom", Prop("boolean", "Zoom viewport directly onto the completed legend (default true).") },
                    { "zoom_mode", Prop("string", "'legend' (default, crisp readable table), 'both' (drawing + legend), or 'all'.") }
                })));

            tools.Add(Tool("annotate_block_coordinates",
                "Deterministically extract block X/Y coordinates, number blocks #1..#N in the engineer-chosen sort_order ('left_to_right', 'right_to_left', 'top_to_bottom', 'bottom_to_top', 'top_to_bottom_rtl', 'bottom_to_top_rtl'), place readable TrueType Arial Unicode coordinate callouts in obstacle-free empty space using native AutoCAD `Leader` entities (Vertex 0 pinned strictly to each exact target coordinate inside the block), and draw an external separated Coordinate Schedule Table outside drawing bounds and non-overlapping _AI_BLOCK_LEGEND. Supports interactive picking of 1 or more points on a sample block (`pick_sample_points: true`) applied across ALL blocks of the same name in either `'sequential'` point numbering or `'grouped'` block-with-subrows mode, plus `table_only=true` to move ONLY the table.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "block_name", Prop("string", "Optional exact or pattern BlockTableRecord name to annotate (e.g. 'Fuse', 'FOOTING'). When omitted with selected_only=true, uses currently selected block(s).") },
                    { "selected_only", Prop("boolean", "Use currently selected AutoCAD block/entities (default true when block_name and handles omitted).") },
                    { "all_instances", Prop("boolean", "When 1 unique block type & sample instance is selected, expand to all ModelSpace insertions of that block (defaults true automatically for single-sample picks).") },
                    { "handles", ArrProp("string", "Optional explicit entity hex handles to annotate.") },
                    { "layer_filter", Prop("string", "Optional layer filter pattern for target blocks.") },
                    { "visible_only", Prop("boolean", "Only include visible entities on On/Thawed layers (default false).") },
                    { "sort_order", Prop("string", "Numbering direction chosen per drawing: 'left_to_right' (من الشمال لليمين), 'right_to_left' (من اليمين للشمال), 'top_to_bottom' (من فوق لتحت), 'bottom_to_top' (من تحت لفوق), 'top_to_bottom_rtl' (من فوق لتحت يمين->يسار), 'bottom_to_top_rtl' (من تحت لفوق يمين->يسار), 'left_to_right_bottom_to_top', 'right_to_left_bottom_to_top'.") },
                    { "pick_sample_points", Prop("boolean", "If true, prompts the engineer directly in AutoCAD to click 1 or more target points inside a sample block (e.g. concrete footing corners), transforms them to local block coordinates, and extracts/annotates those exact points across ALL matching blocks of the same name.") },
                    { "reference_callout_index", Prop("integer", "Adopt the current arrowhead tip (Vertex 0) position of existing callout Leader #N (e.g. 16) relative to its block, and apply that exact local offset to all matching blocks WITHOUT modifying/rotating the blocks.") },
                    { "reference_callout_indices", ArrProp("integer", "Optional list of existing callout Leader numbers (e.g. [16] or [1, 2]) to adopt multiple arrowhead offsets relative to their block.") },
                    { "from_selected_leader", Prop("boolean", "If true, reads the arrowhead position of the currently selected Leader callout in AutoCAD and applies its relative block offset to all matching blocks.") },
                    { "multi_point_mode", Prop("string", "Numbering & table mode when a block has multiple points: 'sequential' (default, #1, #2, #3... consecutive across all points) or 'grouped' (block #1 with sub-rows P1, P2... for each point and #1.1, #1.2... leaders).") },
                    { "sample_points", PtArrayProp("Optional explicit WCS [x, y] target points on the sample block (transformed to local block coordinates for all matching insertions).") },
                    { "local_points", PtArrayProp("Optional explicit local block-definition [x, y] target points applied via BlockTransform to all matching insertions.") },
                    { "use_saved_sample_points", Prop("boolean", "Reuse previously picked sample points for the same block in this drawing (default true).") },
                    { "clear_sample_points", Prop("boolean", "Clear previously picked sample points for this block and return to single-point center/insertion mode.") },
                    { "coordinate_source", Prop("string", "Which coordinate values to extract in single-point mode: 'geometric_center' (default, exact center of visual bounds) or 'insertion_point'.") },
                    { "arrow_target", Prop("string", "Where each Leader arrowhead (Vertex 0) points in single-point mode: 'geometric_center' (default, exact block center) or 'insertion_point'.") },
                    { "decimals", Prop("integer", "Decimal places for X/Y numbers (0..4, default 2).") },
                    { "prefix", Prop("string", "Number prefix before index (default '#').") },
                    { "draw_callouts", Prop("boolean", "Draw non-overlapping on-drawing native AutoCAD Leader callouts (default true; forced false if table_only=true).") },
                    { "draw_table", Prop("boolean", "Draw external Coordinate Schedule Table outside drawing extents (default true).") },
                    { "table_only", Prop("boolean", "If true, redraws/moves ONLY the external coordinate table without erasing or touching existing on-drawing Leader callouts.") },
                    { "table_position", Prop("string", "Placement outside drawing extents: 'outside_right' (default), 'outside_left', 'outside_top', or 'outside_bottom'.") },
                    { "table_gap", Prop("number", "Optional clearance gap outside drawing extents in active drawing units.") },
                    { "table_title", Prop("string", "Optional coordinate table heading.") },
                    { "callout_layer", Prop("string", "Layer for Leader callouts (default '_AI_COORD_CALLOUTS').") },
                    { "table_layer", Prop("string", "Layer for external coordinate schedule table (default '_AI_COORD_TABLE').") },
                    { "replace_existing", Prop("boolean", "Replace previous entities on active output layers (default true).") },
                    { "text_height", Prop("number", "Optional explicit callout text height in active drawing units (auto-scaled to block size & pitch if omitted).") },
                    { "callout_color_index", Prop("integer", "ACI color index for callout text (default 2=Yellow).") },
                    { "arrow_color_index", Prop("integer", "ACI color index for Leader arrowhead, shaft, and horizontal shelf (default 4=Cyan).") },
                    { "include_block_name", Prop("boolean", "Include block name in on-drawing label header alongside index (auto true if multiple block types).") },
                    { "export_excel", Prop("boolean", "Also export ALL extracted block coordinates directly to an Excel .xlsx file cleanly.") },
                    { "excel_path", Prop("string", "Optional destination .xlsx file path when export_excel=true.") },
                    { "zoom", Prop("boolean", "Zoom viewport cleanly after drawing (default true).") },
                    { "zoom_mode", Prop("string", "'auto' (default), 'callouts', 'table', or 'both'.") }
                })));

            tools.Add(Tool("export_to_excel",
                "Export an AutoCAD schedule directly to a real styled OpenXML Excel (.xlsx) or UTF-8 CSV (.csv) file. Automatically extracts from: the latest generated coordinate schedule ('coordinates'), Block Legend ('block_legend'), a currently selected AutoCAD Table or text grid ('selected_table'), live ModelSpace blocks, or custom headers/rows ('custom'). Supports interactive Save dialog (browse=true) and opening in Excel (open_file=true).",
                ObjSchema(new Dictionary<string, object>
                {
                    { "source", Prop("string", "Data source: 'auto' (default smart detection), 'coordinates', 'panel_schedule', 'boq', 'lighting', 'block_legend', 'selected_table', or 'custom'.") },
                    { "file_path", Prop("string", "Optional output .xlsx or .csv file path (auto-named beside the DWG file or Desktop when omitted).") },
                    { "format", Prop("string", "Output format: 'xlsx' (default) or 'csv'.") },
                    { "browse", Prop("boolean", "Open a Windows Save File dialog so the engineer can pick the folder/filename manually (default false).") },
                    { "open_file", Prop("boolean", "Open the saved Excel/CSV file immediately after writing (default false).") },
                    { "title", Prop("string", "Optional spreadsheet banner title override.") },
                    { "sheet_name", Prop("string", "Optional worksheet tab name.") },
                    { "block_name", Prop("string", "Optional block name filter when source='coordinates'.") },
                    { "sort_order", Prop("string", "Optional coordinate sort order when building live coordinates ('left_to_right', 'right_to_left', 'top_to_bottom', 'bottom_to_top', etc.).") },
                    { "decimals", Prop("integer", "Decimal places for live coordinates (default 2).") },
                    { "headers", ArrProp("string", "Column headers array when source='custom'.") },
                    { "rows", new Dictionary<string, object>
                        {
                            { "type", "array" },
                            { "description", "2D array of string cell rows when source='custom'." },
                            { "items", new Dictionary<string, object>
                                {
                                    { "type", "array" },
                                    { "items", new Dictionary<string, object> { { "type", "string" } } }
                                }
                            }
                        }
                    }
                })));

            tools.Add(Tool("import_excel_table",
                "Import an Excel (.xlsx / .xlsm) or CSV/TSV spreadsheet file directly into AutoCAD ModelSpace as a crisp TrueType Arial Unicode engineering table outside drawing bounds (default on _AI_EXCEL_TABLE, Arabic RTL supported cleanly) or as a native AutoCAD Table entity (use_autocad_table=true). Supports Windows File Picker (browse=true or omitted file_path), reading files currently open in Excel, and full undo_last_action.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "file_path", Prop("string", "Path to .xlsx, .xlsm, or .csv file. When omitted or browse=true, opens a Windows Open File dialog.") },
                    { "browse", Prop("boolean", "Open a Windows file picker dialog to let the engineer select an Excel/CSV file (default false; auto-triggered when file_path is omitted).") },
                    { "sheet_name", Prop("string", "Optional worksheet name for multi-sheet .xlsx workbooks (defaults to first sheet).") },
                    { "title", Prop("string", "Optional table banner heading inside AutoCAD (defaults to workbook title or filename).") },
                    { "has_header", Prop("boolean", "Treat first row as styled header row (default true).") },
                    { "use_autocad_table", Prop("boolean", "If true, draws a native Autodesk.AutoCAD.DatabaseServices.Table; if false (default), draws crisp Arial Unicode _AI_UNICODE MText + framing grid matching _AI_BLOCK_LEGEND.") },
                    { "layer", Prop("string", "Target CAD layer (default '_AI_EXCEL_TABLE').") },
                    { "replace_existing", Prop("boolean", "Erase previous entities on 'layer' before drawing (default false).") },
                    { "position", Prop("string", "Placement outside drawing extents: 'outside_right' (default), 'outside_left', 'outside_top', or 'outside_bottom'.") },
                    { "insert_at", ArrProp("number", "Optional explicit top-left placement coordinates [x, y].") },
                    { "gap", Prop("number", "Optional clearance gap outside drawing extents in active drawing units.") },
                    { "text_height", Prop("number", "Optional text height in drawing units (auto-scaled to drawing extents if omitted).") },
                    { "max_rows", Prop("integer", "Maximum rows to import (1..500, default 250).") },
                    { "max_cols", Prop("integer", "Maximum columns to import (1..40, default 25).") },
                    { "zoom", Prop("boolean", "Zoom directly onto the imported table (default true).") },
                    { "zoom_mode", Prop("string", "'table' (default) or 'both'.") }
                })));

            Dictionary<string, object> modifyItemProps = new Dictionary<string, object>
            {
                { "handle", Prop("string", "Entity hex handle to modify.") },
                { "move", ArrProp("number", "[dx, dy] displacement vector.") },
                { "rotate", ObjSchema(new Dictionary<string, object> { { "origin", ArrProp("number", "[x, y]") }, { "angle_deg", Prop("number", "Rotation in degrees.") } }) },
                { "scale", ObjSchema(new Dictionary<string, object> { { "origin", ArrProp("number", "[x, y]") }, { "factor", Prop("number", "Scale multiplier.") } }) },
                { "mirror", ObjSchema(new Dictionary<string, object> { { "p1", ArrProp("number", "Axis point 1 [x, y]") }, { "p2", ArrProp("number", "Axis point 2 [x, y]") } }) },
                { "layer", Prop("string", "Move entity to this layer.") },
                { "color_index", Prop("integer", "Change ACI color index.") },
                { "radius", Prop("number", "New circle radius.") },
                { "diameter", Prop("number", "New circle diameter.") },
                { "center", ArrProp("number", "New circle center [x, y].") },
                { "start", ArrProp("number", "New line start [x, y].") },
                { "end", ArrProp("number", "New line end [x, y].") },
                { "text", Prop("string", "New DBText/MText string.") },
                { "height", Prop("number", "New DBText/MText height.") },
                { "closed", Prop("boolean", "Set polyline closed state.") },
                { "rotation_deg", Prop("number", "New BlockReference rotation in degrees.") },
                { "new_block_name", Prop("string", "Rename BlockTableRecord (or clone a new block definition when targeting a subset of insertions). Use with block_name or handles to genuinely rename blocks across ModelSpace, Properties, and _AI_BLOCK_LEGEND (also supports aliases rename_block / rename_to).") }
            };

            Dictionary<string, object> modifyTopProps = new Dictionary<string, object>(modifyItemProps);
            modifyTopProps.Remove("handle");
            modifyTopProps["items"] = new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", "Per-entity edits array (use this OR handles/selected_only/layer_filter with edits)." },
                { "items", ObjSchema(modifyItemProps, new string[] { "handle" }) }
            };
            modifyTopProps["handles"] = ArrProp("string", "Target handles to apply bulk edits to.");
            modifyTopProps["selected_only"] = Prop("boolean", "Apply bulk edits to currently selected entities.");
            modifyTopProps["block_name"] = Prop("string", "SAFE TARGETED FILTER: Modify only BlockReferences matching this block name (e.g. 'parir ai 4'). Use this instead of layer_filter when targeting a block!");
            modifyTopProps["entity_type"] = Prop("string", "Optional .NET class name filter (e.g. BlockReference, Circle, Line).");
            modifyTopProps["layer_filter"] = Prop("string", "Apply bulk edits to entities on this exact layer (WARNING: do NOT use layer_filter='0' to recolor a specific block!).");
            modifyTopProps["text_contains"] = Prop("string", "Optional text/attribute substring filter.");
            modifyTopProps["bbox"] = ArrProp("number", "Optional 2D bounding box filter [minX, minY, maxX, maxY].");
            modifyTopProps["exclude_anonymous"] = Prop("boolean", "Exclude '*' anonymous blocks when filtering.");
            modifyTopProps["exclude_system"] = Prop("boolean", "Exclude internal AVE_*/ASHADE blocks when filtering.");
            modifyTopProps["visible_only"] = Prop("boolean", "Only modify visible entities on On/Thawed layers.");
            modifyTopProps["include_block_contents"] = Prop("boolean", "When changing color_index on BlockReferences, convert their BlockTableRecord geometry to ByBlock (with full Undo snapshot) so the color visibly updates in AutoCAD (auto true when block_name or all insertions of a block are targeted).");
            modifyTopProps["confirm_bulk_layer"] = Prop("boolean", "Safety flag: must be explicitly true only if the engineer truly requested modifying >100 entities via layer_filter alone.");
            modifyTopProps["edits"] = ObjSchema(new Dictionary<string, object>(modifyItemProps), null);
            modifyTopProps["zoom"] = Prop("boolean", "Zoom directly onto modified entities or drawing extents after edits (default false).");

            tools.Add(Tool("modify_entities",
                "Modify existing entities by block_name, entity_type, handles, selection, bbox, or layer_filter: change color (including BlockReference inner geometry cleanly), rename block definitions via new_block_name (auto-syncing existing _AI_BLOCK_LEGEND without needing draw_block_legend), move, rotate, scale, mirror, layer, radius, text, height. Automatically records full Undo snapshots.",
                ObjSchema(modifyTopProps)));

            tools.Add(Tool("undo_last_action",
                "Undo/revert previous AI CAD changes (restoring exact pre-edit entity colors/layers/positions, re-appearing erased entities, or removing newly drawn/legend/copied entities) or list undo history.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "action", Prop("string", "'undo' (default, reverts last N AI actions) or 'list_history' (shows recorded actions available to undo).") },
                    { "steps", Prop("integer", "Number of recent AI CAD actions to undo cleanly (1..10, default 1).") }
                })));

            tools.Add(Tool("copy_entities",
                "Clone existing entities (by handles, active selection, or filter criteria) with automatic placement outside drawing extents OR explicit (dx, dy) array offset, optional single bounding frame box/label, grip selection, and zoom directly onto the copies.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "handles", ArrProp("string", "Handles to copy (optional if selected_only or filters are provided).") },
                    { "selected_only", Prop("boolean", "Copy currently selected entities.") },
                    { "layer", Prop("string", "Filter entities on this layer when handles omitted.") },
                    { "entity_type", Prop("string", "Filter .NET entity type (e.g. BlockReference) when handles omitted.") },
                    { "block_name", Prop("string", "Filter BlockReference name substring when handles omitted.") },
                    { "text_contains", Prop("string", "Filter text/attribute substring when handles omitted.") },
                    { "exclude_anonymous", Prop("boolean", "Exclude anonymous '*' blocks when filtering.") },
                    { "exclude_system", Prop("boolean", "Exclude AVE_* / ASHADE system blocks when filtering.") },
                    { "visible_only", Prop("boolean", "Only copy visible entities on On/Thawed layers.") },
                    { "auto_offset_outside", Prop("string", "Automatically place copy cleanly outside entire drawing extents: 'right', 'left', 'up', 'down'.") },
                    { "gap", Prop("number", "Custom clearance gap when using auto_offset_outside.") },
                    { "dx", Prop("number", "Explicit step displacement X per copy.") },
                    { "dy", Prop("number", "Explicit step displacement Y per copy.") },
                    { "count", Prop("integer", "Number of copies per source entity (1..100, default 1).") },
                    { "target_layer", Prop("string", "Optional target layer for copies.") },
                    { "color_index", Prop("integer", "Optional color override.") },
                    { "draw_frame", Prop("boolean", "Draw one single rectangular bounding frame around all copied entities.") },
                    { "frame_layer", Prop("string", "Layer for optional bounding frame (default target_layer or _AI_HIGHLIGHT).") },
                    { "frame_color_index", Prop("integer", "ACI color for optional frame/label (default 2=Yellow).") },
                    { "frame_label", Prop("string", "Optional text heading drawn directly above the copied group frame.") },
                    { "select_copied", Prop("boolean", "Visibly select copied entities with AutoCAD grips.") },
                    { "zoom", Prop("boolean", "Zoom viewport directly onto copied_bounds (default true).") }
                })));

            tools.Add(Tool("delete_entities",
                "Delete specific entities by handles, active selection, layer name, or clear_review_layer=true.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "handles", ArrProp("string", "Exact hex handles to erase.") },
                    { "selected_only", Prop("boolean", "Erase currently selected entities.") },
                    { "layer", Prop("string", "Erase all entities on this layer.") },
                    { "clear_review_layer", Prop("boolean", "Erase preview entities on _AI_PREVIEW_REVIEW.") }
                })));

            tools.Add(Tool("create_block",
                "Create or update a reusable AutoCAD Block definition either by cloning REAL selected/existing CAD entities (selected_only=true or handles — NEVER invent entities manually when blocking existing drawing geometry!) OR by drafting new synthetic geometry entities.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "block_name", Prop("string", "Block name.") },
                    { "selected_only", Prop("boolean", "Clone currently selected AutoCAD entities directly into the block definition (recommended whenever blocking the engineer's selection).") },
                    { "handles", ArrProp("string", "Explicit entity hex handles to clone from ModelSpace into the block definition.") },
                    { "replace_selection", Prop("boolean", "When cloning selected/handle entities, replace the source entities with an inserted BlockReference in-place (default true when insert_at is omitted).") },
                    { "color_byblock", Prop("boolean", "Convert cloned definition entities to ByBlock color=0 (default false, preserves original geometry colors).") },
                    { "zoom", Prop("boolean", "Zoom viewport cleanly onto the newly inserted BlockReference (default true).") },
                    { "entities", new Dictionary<string, object>
                        {
                            { "type", "array" },
                            { "description", "New primitive geometry/text entities relative to base_point ONLY when creating a brand-new schematic symbol from scratch (omit when using selected_only/handles!)." },
                            { "items", ObjSchema(entityItemProps, new string[] { "type" }) }
                        }
                    },
                    { "overwrite", Prop("boolean", "Overwrite existing block definition if same name exists (default true).") },
                    { "base_point", ArrProp("number", "[x, y] block origin (defaults to center of selected entities when cloning, or [0, 0]).") },
                    { "insert_at", ArrProp("number", "Optional [x, y] coordinate to insert an instance immediately in ModelSpace.") },
                    { "insert_layer", Prop("string", "Layer for inserted instance (defaults to dominant source layer or '0').") },
                    { "insert_rotation_deg", Prop("number", "Rotation for inserted instance.") }
                }, new string[] { "block_name" })));

            tools.Add(Tool("convert_matching_shapes_to_block",
                "Deterministic similarity CAD pattern matcher & blockifier: reads the real selected AutoCAD entities (or handles/bbox), scans ModelSpace for ALL geometrically similar clusters across translations, full 0-360 deg rotations (covering 0-180 deg), mirror reflections, uniform scale differences (`allow_scale`), split Line/Polyline/Arc representations (`normalize_geometry`), and minor drafting differences (`fuzzy_match` / `tolerance_ratio`), defines the exact block via DeepCloneObjects with zero geometry hallucination, and replaces every match with a scaled/rotated BlockReference (full Undo support).",
                ObjSchema(new Dictionary<string, object>
                {
                    { "block_name", Prop("string", "Target BlockTableRecord name to create and insert for every matching cluster (required unless dry_run=true).") },
                    { "selected_only", Prop("boolean", "Use currently selected AutoCAD entities as the template shape (default true when handles and bbox are omitted).") },
                    { "handles", ArrProp("string", "Explicit template entity hex handles in ModelSpace (optional override for selection).") },
                    { "bbox", ArrProp("number", "Optional 2D template bounding box [minX, minY, maxX, maxY] to pick the sample cluster.") },
                    { "layer_filter", Prop("string", "Optional layer filter pattern to restrict search scope (omit to match across all visible ModelSpace layers).") },
                    { "allow_rotation", Prop("boolean", "Match rotated occurrences across 0-360 deg (including 0-180 deg, default true).") },
                    { "allow_mirror", Prop("boolean", "Match Y-mirrored occurrences (default true for pure geometry, false when template contains text).") },
                    { "allow_scale", Prop("boolean", "Match geometrically similar shapes at different uniform sizes/scales (default true for multi-entity or complex shapes; false by default for single lone Circle/Line unless min_scale/max_scale or allow_scale=true is passed).") },
                    { "min_scale", Prop("number", "Minimum uniform scale factor when allow_scale=true (default 0.25).") },
                    { "max_scale", Prop("number", "Maximum uniform scale factor when allow_scale=true (default 4.0).") },
                    { "tolerance", Prop("number", "Optional absolute geometric position tolerance in drawing units (overrides tolerance_ratio).") },
                    { "tolerance_ratio", Prop("number", "Relative position tolerance as fraction of template span (default 0.03 = 3%). Increase e.g. to 0.06-0.10 for loose hand-drawn shapes.") },
                    { "size_tolerance_ratio", Prop("number", "Allowed relative variation in individual element length/radius (default 0.08 = 8%).") },
                    { "normalize_geometry", Prop("boolean", "Decompose Polylines into segments and merge dual-arc Circles during fallback matching so Line vs Polyline or Arc vs Circle representations still match (default true).") },
                    { "fuzzy_match", Prop("boolean", "After exact matches, also match clusters with minor missing sub-elements above min_match_ratio (default true when template has >= 4 entities).") },
                    { "min_match_ratio", Prop("number", "Minimum fraction of template entities & geometric weight required in fuzzy pass (default 0.80 = 80%).") },
                    { "strict_types", Prop("boolean", "Require exact .NET entity type equality (disables normalize_geometry when true; default false).") },
                    { "ignore_text", Prop("boolean", "Ignore DBText/MText inside the template when matching pure geometry (default false).") },
                    { "ignore_text_content", Prop("boolean", "Match text entities by position/height regardless of string content (default false).") },
                    { "dry_run", Prop("boolean", "If true, only detect/highlight matches without creating a block or modifying entities (default false).") },
                    { "preview_only", Prop("boolean", "Alias for dry_run.") },
                    { "overwrite", Prop("boolean", "Allow overwriting an existing block definition that is already placed in ModelSpace (default false).") },
                    { "reuse_existing_block", Prop("boolean", "If block_name already exists, insert references to it for newly matched loose clusters (default false).") },
                    { "erase_originals", Prop("boolean", "Erase matched loose entities after placing BlockReferences (default true).") },
                    { "color_byblock", Prop("boolean", "Set cloned block definition entities to ByBlock color=0 (default false).") },
                    { "insert_layer", Prop("string", "Target layer for inserted BlockReferences (defaults to each cluster's dominant layer).") },
                    { "select_converted", Prop("boolean", "Select newly inserted BlockReferences visibly with blue AutoCAD grips (default true).") },
                    { "draw_highlight_boxes", Prop("boolean", "Draw highlight frames around matches on _AI_HIGHLIGHT (default true in dry_run).") },
                    { "zoom", Prop("boolean", "Zoom viewport cleanly onto the matched/converted shapes (default true).") }
                }, new string[] { "block_name" })));

            tools.Add(Tool("manage_layers",
                "List, create, update, activate, or delete AutoCAD layers.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "action", Prop("string", "One of: list, create, update, set_current, delete.") },
                    { "name", Prop("string", "Layer name.") },
                    { "color_index", Prop("integer", "ACI color 1..255.") },
                    { "linetype", Prop("string", "Linetype name (e.g. Continuous, DASHED, CENTER).") },
                    { "is_off", Prop("boolean", "Turn layer off/on.") },
                    { "is_frozen", Prop("boolean", "Freeze/thaw layer.") },
                    { "is_locked", Prop("boolean", "Lock/unlock layer.") }
                }, new string[] { "action" })));

            tools.Add(Tool("measure_and_compare",
                "Precisely measure distance & clear bounding-box gap/overlap between two entities or two points, compute entity group stats, or compare two groups side-by-side.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "mode", Prop("string", "One of: distance, entities_summary, compare_groups.") },
                    { "handle_a", Prop("string", "First entity handle (for distance mode).") },
                    { "handle_b", Prop("string", "Second entity handle (for distance mode).") },
                    { "p1", ArrProp("number", "First [x, y] point (for distance mode).") },
                    { "p2", ArrProp("number", "Second [x, y] point (for distance mode).") },
                    { "handles_a", ArrProp("string", "Group A handles (for summary/compare).") },
                    { "layer_a", Prop("string", "Group A layer filter (for summary/compare).") },
                    { "handles_b", ArrProp("string", "Group B handles (for compare_groups).") },
                    { "layer_b", Prop("string", "Group B layer filter (for compare_groups).") }
                }, new string[] { "mode" })));

            tools.Add(Tool("duplicate_drawing",
                "Duplicate the entire active ModelSpace drawing side-by-side as editable entities (or a block) or save an independent DWG file copy.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "mode", Prop("string", "One of: in_drawing, dwg_file.") },
                    { "direction", Prop("string", "right, left, up, down (for in_drawing).") },
                    { "gap", Prop("number", "Optional custom spacing between original and duplicate.") },
                    { "layer", Prop("string", "Optional target layer for duplicate.") },
                    { "as_block", Prop("boolean", "Insert duplicate as one BlockReference on _AI_PREVIEW_REVIEW instead of editable entities.") },
                    { "file_path", Prop("string", "Optional output .dwg path when mode=dwg_file.") }
                })));

            tools.Add(Tool("zoom_view",
                "Zoom AutoCAD viewport to extents, a 2D window bbox, or specific entity handles.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "mode", Prop("string", "extents, window, or handles.") },
                    { "bbox", ArrProp("number", "[minX, minY, maxX, maxY] when mode=window.") },
                    { "handles", ArrProp("string", "Target handles when mode=handles.") }
                })));

            tools.Add(Tool("execute_template",
                "Run a built-in parametric BIM/CAD project template directly inside AutoCAD.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "template", Prop("string", "One of: apartment_150sqm, apartment_150sqm_electrical.") }
                }, new string[] { "template" })));

            tools.Add(Tool("approve_or_rollback_preview",
                "Approve (move/explode to official layers) or rollback/erase elements placed on _AI_PREVIEW_REVIEW.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "action", Prop("string", "One of: approve, rollback.") }
                }, new string[] { "action" })));

            tools.Add(Tool("get_plugin_diagnostics",
                "Get loaded plugin DLL path, build timestamp, AutoCAD version, active document units, and supported CAD tool names (helpful for debugging or reporting missing code capabilities).",
                ObjSchema(new Dictionary<string, object>())));

            tools.Add(Tool("extract_load_schedule",
                "Extract an electrical circuit/panel Load Schedule (جدول الأحمال) from breaker/load blocks, neighboring texts (e.g. 63A, 100A, 125A, SPARE, 15KW, 4x16mm2), and/or connected sample feeder paths (Line/Polyline). Measures feeder route lengths in meters, groups rating totals, draws a crisp Arial Unicode table on _AI_PANEL_SCHEDULE, and optionally exports directly to Excel.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "mode", Prop("string", "Extraction mode: 'auto' (default), 'blocks', 'paths', or 'selected_sample'.") },
                    { "block_name", Prop("string", "Optional breaker/load BlockTableRecord name filter (e.g. 'C.B', 'Fuse').") },
                    { "feeder_layer", Prop("string", "Optional layer filter pattern for feeder cables/paths.") },
                    { "selected_only", Prop("boolean", "Learn target block/path style from current AutoCAD selection when true (default true).") },
                    { "trace_paths", Prop("boolean", "Trace connected feeder polylines/lines from each breaker/sample path to measure route length and downstream labels (default true).") },
                    { "search_radius", Prop("number", "Optional neighbor text search radius in active drawing units.") },
                    { "sort_order", Prop("string", "Row ordering: 'left_to_right', 'right_to_left', 'top_to_bottom', 'bottom_to_top'.") },
                    { "decimals", Prop("integer", "Decimal places for path lengths/kW (default 2).") },
                    { "draw_table", Prop("boolean", "Draw formatted schedule table outside drawing extents on _AI_PANEL_SCHEDULE (default true).") },
                    { "export_excel", Prop("boolean", "Also export the load schedule directly to .xlsx (default false).") },
                    { "excel_path", Prop("string", "Optional Excel file output path.") },
                    { "title", Prop("string", "Optional table banner title.") },
                    { "table_position", Prop("string", "'outside_right' (default), 'outside_left', 'outside_top', or 'outside_bottom'.") },
                    { "replace_existing", Prop("boolean", "Replace previous _AI_PANEL_SCHEDULE table (default true).") },
                    { "zoom", Prop("boolean", "Zoom onto schedule table (default true).") }
                })));

            tools.Add(Tool("quantity_takeoff",
                "Perform full CAD Quantity Takeoff (BOQ - حصر الكميات): linear route/cable/wall lengths per layer with vertical/termination/waste allowances, closed polygon/room/hatch areas in m2 (optionally stamping room area labels inside polygons), and named block counts -> separated Unicode BOQ table on _AI_BOQ_TABLE and optional .xlsx export.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "layers", ArrProp("string", "Optional list of layer names/patterns to include.") },
                    { "layer", Prop("string", "Optional single layer name/pattern filter.") },
                    { "selected_only", Prop("boolean", "Restrict takeoff to currently selected entities (default false).") },
                    { "bbox", ArrProp("number", "Optional 2D filter box [minX, minY, maxX, maxY].") },
                    { "waste_percent", Prop("number", "Waste percentage added to totals (0..100, default 0).") },
                    { "vertical_allowance_m", Prop("number", "Extra vertical riser meters added per linear segment/run (default 0).") },
                    { "termination_allowance_m", Prop("number", "Extra termination meters added per linear segment/run (default 0).") },
                    { "label_areas", Prop("boolean", "Write centered room name + m2 labels inside closed polygons on _AI_AREA_LABELS (default false).") },
                    { "draw_table", Prop("boolean", "Draw BOQ table on _AI_BOQ_TABLE (default true).") },
                    { "export_excel", Prop("boolean", "Also export BOQ directly to .xlsx (default false).") },
                    { "excel_path", Prop("string", "Optional Excel file output path.") },
                    { "title", Prop("string", "Optional BOQ table heading.") },
                    { "table_position", Prop("string", "'outside_right' (default), 'outside_left', 'outside_top', or 'outside_bottom'.") },
                    { "replace_existing", Prop("boolean", "Replace previous _AI_BOQ_TABLE and area labels (default true).") },
                    { "decimals", Prop("integer", "Decimal places (default 2).") },
                    { "zoom", Prop("boolean", "Zoom onto BOQ table (default true).") }
                })));

            tools.Add(Tool("manage_attributes",
                "Extract BlockReference Attribute tags to Excel (.xlsx), bulk-update Attribute values inside AutoCAD with full Undo, or sync modified Attribute columns back from an Excel/CSV file (sync_from_excel).",
                ObjSchema(new Dictionary<string, object>
                {
                    { "action", Prop("string", "One of: 'extract', 'update', 'sync_from_excel'.") },
                    { "block_name", Prop("string", "Optional BlockTableRecord name/pattern filter.") },
                    { "selected_only", Prop("boolean", "Target currently selected BlockReferences (default false).") },
                    { "handles", ArrProp("string", "Optional explicit BlockReference handles.") },
                    { "layer_filter", Prop("string", "Optional layer filter pattern.") },
                    { "tag", Prop("string", "Attribute tag name to update (when action='update').") },
                    { "value", Prop("string", "New value for 'tag' (when action='update').") },
                    { "updates", new Dictionary<string, object>
                        {
                            { "type", "array" },
                            { "description", "Multiple {tag, value} updates when action='update'." },
                            { "items", ObjSchema(new Dictionary<string, object>
                                {
                                    { "tag", Prop("string", "Attribute tag name.") },
                                    { "value", Prop("string", "New text value.") }
                                }, new string[] { "tag", "value" })
                            }
                        }
                    },
                    { "file_path", Prop("string", "Excel .xlsx/.csv file path for extract output or sync_from_excel input.") },
                    { "browse", Prop("boolean", "Show Windows Save/Open dialog for choosing the Excel file.") },
                    { "sheet_name", Prop("string", "Optional worksheet name when action='sync_from_excel'.") },
                    { "export_excel", Prop("boolean", "Write .xlsx when action='extract' (default true).") },
                    { "open_file", Prop("boolean", "Open generated .xlsx after extract (default false).") }
                }, new string[] { "action" })));

            tools.Add(Tool("text_tools",
                "Batch engineering text utilities with full Undo: 'find_replace' across DBText/MText/Attributes, 'auto_number' sequential tagging (C1, C2.. or DB-01, DB-02..) sorted in any direction, and 'standardize' text height / Arial Unicode style / color.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "action", Prop("string", "One of: 'find_replace', 'auto_number', 'standardize'.") },
                    { "find", Prop("string", "Text or regex pattern to find (required for find_replace).") },
                    { "replace", Prop("string", "Replacement text (for find_replace).") },
                    { "use_regex", Prop("boolean", "Treat 'find' as regex (default false).") },
                    { "match_case", Prop("boolean", "Case-sensitive search (default false).") },
                    { "include_attributes", Prop("boolean", "Also search BlockReference attributes (default true).") },
                    { "preview_only", Prop("boolean", "Count and sample matches without editing (default false).") },
                    { "target_type", Prop("string", "For auto_number: 'auto', 'texts', or 'blocks'.") },
                    { "block_name", Prop("string", "Optional block filter for auto_number.") },
                    { "attribute_tag", Prop("string", "For auto_number on blocks: fill this Attribute tag if present instead of placing new MText.") },
                    { "prefix", Prop("string", "Prefix before sequential number (e.g. 'C', 'DB-').") },
                    { "suffix", Prop("string", "Suffix after sequential number.") },
                    { "start", Prop("integer", "Starting number (default 1).") },
                    { "step", Prop("integer", "Number increment (default 1).") },
                    { "pad_digits", Prop("integer", "Zero-padding width 1..6 (e.g. 2 -> 01, 02).") },
                    { "mode", Prop("string", "For auto_number on existing texts: 'replace', 'prepend', or 'append'.") },
                    { "sort_order", Prop("string", "'left_to_right', 'right_to_left', 'top_to_bottom', 'bottom_to_top', etc.") },
                    { "text_height", Prop("number", "New height when action='standardize'.") },
                    { "color_index", Prop("integer", "New ACI color index when action='standardize'.") },
                    { "use_unicode_style", Prop("boolean", "Apply _AI_UNICODE Arial font when action='standardize' (default true).") },
                    { "selected_only", Prop("boolean", "Operate only on currently selected entities (default false).") },
                    { "handles", ArrProp("string", "Optional explicit entity handles.") },
                    { "layer_filter", Prop("string", "Optional layer filter pattern.") },
                    { "bbox", ArrProp("number", "Optional [minX, minY, maxX, maxY] filter.") }
                }, new string[] { "action" })));

            tools.Add(Tool("advanced_geometry_ops",
                "Precision batch CAD geometry operations with full Undo: 'align' (left/right/top/bottom/center_x/center_y), 'distribute' (equal_gaps/equal_centers horizontally or vertically), 'array_rect', 'array_polar', 'offset' (via native GetOffsetCurves), 'revision_cloud' (scalloped polyline around selection/bbox), and 'explode'.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "operation", Prop("string", "One of: 'align', 'distribute', 'array_rect', 'array_polar', 'offset', 'revision_cloud', 'explode'.") },
                    { "handles", ArrProp("string", "Optional explicit target entity handles.") },
                    { "selected_only", Prop("boolean", "Use current AutoCAD selection (default true when handles/filters omitted).") },
                    { "block_name", Prop("string", "Optional block name filter.") },
                    { "entity_type", Prop("string", "Optional .NET class filter (Line, Polyline, Circle, BlockReference, etc.).") },
                    { "layer_filter", Prop("string", "Optional layer filter.") },
                    { "bbox", ArrProp("number", "Optional [minX, minY, maxX, maxY] region or revision cloud box.") },
                    { "alignment", Prop("string", "For align: 'left', 'right', 'top', 'bottom', 'center_x', 'center_y'.") },
                    { "axis", Prop("string", "For distribute: 'horizontal' (default) or 'vertical'.") },
                    { "mode", Prop("string", "For distribute: 'equal_gaps' (default) or 'equal_centers'.") },
                    { "rows", Prop("integer", "For array_rect: row count.") },
                    { "cols", Prop("integer", "For array_rect: column count.") },
                    { "row_spacing", Prop("number", "For array_rect: vertical distance between rows.") },
                    { "col_spacing", Prop("number", "For array_rect: horizontal distance between columns.") },
                    { "count", Prop("integer", "For array_polar: total copies around circle.") },
                    { "center", ArrProp("number", "For array_polar: center point [x, y] (defaults to bounds center).") },
                    { "total_angle_deg", Prop("number", "For array_polar: span angle in degrees (default 360).") },
                    { "rotate_items", Prop("boolean", "For array_polar: rotate copies as they array (default true).") },
                    { "distance", Prop("number", "For offset: offset distance > 0.") },
                    { "side", Prop("string", "For offset: 'positive', 'negative', or 'both'.") },
                    { "padding", Prop("number", "For revision_cloud: extra clearance margin around selection/bbox.") },
                    { "label", Prop("string", "Optional callout text above revision_cloud.") },
                    { "layer", Prop("string", "Optional output layer override.") },
                    { "color_index", Prop("integer", "Optional ACI color index.") },
                    { "zoom", Prop("boolean", "Zoom viewport after completion (default true).") }
                }, new string[] { "operation" })));

            tools.Add(Tool("drawing_qa_cleanup",
                "Drawing QA/QC, cleanup & revision comparison: 'audit_duplicates' (detect/remove stacked overlapping identical blocks and zero-length lines), 'overkill' (remove duplicate overlapping Lines/Circles/Polylines), 'purge' (purge unused blocks/layers/styles), 'standardize_bylayer', 'compare_dwg' (diff against another .dwg file with revision clouds + Excel report), 'check_env' (restore FILEDIA=1, PICKFIRST=1), and 'full_cleanup'.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "action", Prop("string", "One of: 'audit_duplicates', 'overkill', 'purge', 'standardize_bylayer', 'compare_dwg', 'check_env', 'full_cleanup'.") },
                    { "tolerance", Prop("number", "Spatial matching tolerance for duplicates/overkill (default 0.05).") },
                    { "remove_duplicates", Prop("boolean", "Erase duplicate stacked blocks when audit_duplicates runs (default false unless overkill/full_cleanup).") },
                    { "highlight", Prop("boolean", "Frame duplicate blocks in red on _AI_QA_ISSUE when inspecting (default true).") },
                    { "ignore_layer", Prop("boolean", "Match overkill duplicates across different layers too (default false).") },
                    { "compare_file_path", Prop("string", "Reference .dwg file path for compare_dwg (opens file dialog if omitted/browse=true).") },
                    { "browse", Prop("boolean", "Open Windows File Picker for compare_dwg.") },
                    { "draw_clouds", Prop("boolean", "Draw green (Added) / red (Removed) revision clouds on _AI_REV_CLOUD in compare_dwg (default true).") },
                    { "export_excel", Prop("boolean", "Write Revision Diff Excel report for compare_dwg (default true).") }
                }, new string[] { "action" })));

            tools.Add(Tool("layout_and_plot_tools",
                "Manage PaperSpace Layouts, scaled Viewports + Title Blocks, and Batch Plot to multi-page PDF via DWG To PDF.pc3.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "action", Prop("string", "One of: 'list_layouts', 'create_layout', 'batch_plot_pdf'.") },
                    { "layout_name", Prop("string", "Name for new layout (create_layout).") },
                    { "paper_size", Prop("string", "Paper size: 'A0', 'A1', 'A3' (default), or 'A4'.") },
                    { "landscape", Prop("boolean", "Landscape orientation (default true).") },
                    { "scale", Prop("number", "Viewport scale denominator (e.g. 50 for 1:50, 100 for 1:100; 0 or omitted = Fit drawing).") },
                    { "view_bbox", ArrProp("number", "Optional ModelSpace [minX, minY, maxX, maxY] to frame in Viewport.") },
                    { "title", Prop("string", "Title block header text for create_layout.") },
                    { "layouts", ArrProp("string", "Subset of layout names to plot in batch_plot_pdf (omit to plot all PaperSpace layouts).") },
                    { "output_pdf", Prop("string", "Destination .pdf file path for batch_plot_pdf.") }
                }, new string[] { "action" })));

            tools.Add(Tool("manage_block_library",
                "Persistent cross-project Company Block Library (%APPDATA%\\AIEngineer\\BlockLibrary): 'save' any drawing block via native Wblock (.dwg + metadata), 'list' saved library blocks, 'insert' any saved block into the active drawing at [x,y] with Undo, or 'delete'.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "action", Prop("string", "One of: 'save', 'list', 'insert', 'delete'.") },
                    { "block_name", Prop("string", "Block name to save (falls back to selected block), insert, or delete.") },
                    { "save_as", Prop("string", "Optional custom name in library when saving.") },
                    { "description", Prop("string", "Optional engineering description when saving.") },
                    { "category", Prop("string", "Optional category label (e.g. 'Electrical', 'BMS') when saving.") },
                    { "insert_at", ArrProp("number", "Target [x, y] insertion coordinates for action='insert' (default [0, 0]).") },
                    { "scale", Prop("number", "Insertion scale factor (default 1.0).") },
                    { "rotation_deg", Prop("number", "Insertion rotation in degrees (default 0).") },
                    { "layer", Prop("string", "Target layer for inserted block (default '0').") },
                    { "color_index", Prop("integer", "Optional ACI color index override on insert.") },
                    { "zoom", Prop("boolean", "Zoom onto inserted block (default true).") },
                    { "library_dir", Prop("string", "Optional custom library folder path override.") }
                }, new string[] { "action" })));

            tools.Add(Tool("lighting_design_engine",
                "Automated indoor lighting calculation & symmetrical fixture grid placement inside closed CAD room polygons using Egyptian Code (ECP), EN 12464-1, or CIBSE standards + optional real .ies/.ldt photometric files. Computes Lumen Method count + point-by-point Avg/Min Lux & Uniformity U0, places LED fixture blocks, balances 3-phase wiring, draws calculation schedule on _AI_LIGHTING_TABLE, and exports Excel (.xlsx) and/or DIALux (.stf).",
                ObjSchema(new Dictionary<string, object>
                {
                    { "standard", Prop("string", "Lighting standard: 'ECP' (Egyptian Code, default), 'EN12464', or 'CIBSE'.") },
                    { "target_lux", Prop("number", "Optional explicit target illuminance in Lux (overrides room-type lookup).") },
                    { "ies_path", Prop("string", "Optional path to manufacturer .ies (LM-63) or .ldt (EULUMDAT) photometry file.") },
                    { "browse_ies", Prop("boolean", "Open Windows File Picker to select an .ies/.ldt photometry file.") },
                    { "block_name", Prop("string", "Luminaire BlockTableRecord name to insert/create (default 'LED_PANEL_60x60').") },
                    { "fixture_lumens", Prop("number", "Fallback luminaire luminous flux in Lumens when no .ies given (default 3600).") },
                    { "fixture_watts", Prop("number", "Fallback luminaire power in Watts when no .ies given (default 36).") },
                    { "room_height_m", Prop("number", "Ceiling height in meters (default 3.0).") },
                    { "workplane_m", Prop("number", "Working plane height in meters (default 0.80).") },
                    { "mounting_height_m", Prop("number", "Luminaire mounting height in meters (defaults to room_height_m).") },
                    { "maintenance_factor", Prop("number", "Maintenance factor MF 0.4..1.0 (default 0.80).") },
                    { "reflectance_ceiling", Prop("number", "Ceiling reflectance 0.1..0.9 (default 0.70).") },
                    { "reflectance_walls", Prop("number", "Wall reflectance 0.1..0.85 (default 0.50).") },
                    { "reflectance_floor", Prop("number", "Floor reflectance 0.05..0.5 (default 0.20).") },
                    { "room_layer", Prop("string", "Optional layer pattern for closed room boundary Polylines.") },
                    { "selected_only", Prop("boolean", "Restrict design to currently selected room Polylines.") },
                    { "handles", ArrProp("string", "Optional explicit room Polyline hex handles.") },
                    { "draw_fixtures", Prop("boolean", "Place symmetrical grid luminaire blocks inside each room (default true).") },
                    { "balance_circuits", Prop("boolean", "Color-balance fixtures across L1/L2/L3 phases and draw wiring loops (default true).") },
                    { "stamp_labels", Prop("boolean", "Stamp room Lux & fixture count labels on _AI_LIGHTING_LABELS (default true).") },
                    { "draw_table", Prop("boolean", "Draw Lighting Calculation Schedule on _AI_LIGHTING_TABLE (default true).") },
                    { "export_excel", Prop("boolean", "Export lighting calculation schedule to .xlsx (default false).") },
                    { "excel_path", Prop("string", "Optional Excel file output path.") },
                    { "export_stf", Prop("boolean", "Also generate a DIALux .stf file alongside the design (default false).") },
                    { "stf_path", Prop("string", "Optional .stf file output path.") },
                    { "replace_existing", Prop("boolean", "Replace previous _AI_LIGHTING_* and E-LTG-* outputs (default true).") },
                    { "zoom", Prop("boolean", "Zoom onto completed lighting schedule/drawing (default true).") }
                })));

            tools.Add(Tool("export_dialux_stf",
                "Export active AutoCAD closed room polygons (with inner room labels) and existing lighting luminaire blocks in meters to a DIALux .stf (STFF 1.0.5) project file ready for import into DIALux evo / DIALux 4, with optional clean DWG background copy.",
                ObjSchema(new Dictionary<string, object>
                {
                    { "file_path", Prop("string", "Destination .stf file path (auto-named beside DWG or Desktop when omitted).") },
                    { "browse", Prop("boolean", "Open Windows Save File dialog for choosing .stf destination.") },
                    { "block_name", Prop("string", "Optional luminaire block name/pattern (auto-detects lighting blocks/layers if omitted).") },
                    { "fixture_layer", Prop("string", "Optional luminaire layer filter pattern.") },
                    { "room_layer", Prop("string", "Optional closed room boundary polyline layer filter.") },
                    { "ies_path", Prop("string", "Optional .ies/.ldt photometry file path referenced in the STF luminaire definition.") },
                    { "fixture_lumens", Prop("number", "Luminaire luminous flux in Lumens (default 3600).") },
                    { "fixture_watts", Prop("number", "Luminaire wattage (default 36).") },
                    { "room_height_m", Prop("number", "Room height in meters (default 3.0).") },
                    { "workplane_m", Prop("number", "Working plane height in meters (default 0.80).") },
                    { "mounting_height_m", Prop("number", "Luminaire mounting height in meters (defaults to room_height_m).") },
                    { "maintenance_factor", Prop("number", "Maintenance factor MF (default 0.80).") },
                    { "export_clean_dwg", Prop("boolean", "Also save a separate DWG background copy alongside the .stf file without altering the active document.") }
                })));

            return tools;
        }

        private static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> parameters)
        {
            return new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "parameters", parameters }
            };
        }

        private static Dictionary<string, object> ObjSchema(Dictionary<string, object> props, string[] required = null)
        {
            Dictionary<string, object> d = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", props ?? new Dictionary<string, object>() }
            };
            if (required != null && required.Length > 0) d["required"] = required;
            return d;
        }

        private static Dictionary<string, object> Prop(string typeName, string description)
        {
            return new Dictionary<string, object>
            {
                { "type", typeName },
                { "description", description }
            };
        }

        private static Dictionary<string, object> ArrProp(string itemType, string description)
        {
            return new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", description },
                { "items", new Dictionary<string, object> { { "type", itemType } } }
            };
        }

        private static Dictionary<string, object> PtArrayProp(string description)
        {
            return new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", description },
                { "items", new Dictionary<string, object>
                    {
                        { "type", "array" },
                        { "items", new Dictionary<string, object> { { "type", "number" } } }
                    }
                }
            };
        }

        // ==================== JSON Dict Helpers ====================

        private static object GetObj(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        private static string GetStr(Dictionary<string, object> d, string key, string defVal)
        {
            object v = GetObj(d, key);
            return v != null ? v.ToString() : defVal;
        }

        private static Dictionary<string, object> AsDict(object o)
        {
            return o as Dictionary<string, object>;
        }

        private static List<object> AsList(object o)
        {
            List<object> res = new List<object>();
            if (o == null || o is string) return res;
            IEnumerable en = o as IEnumerable;
            if (en != null)
            {
                foreach (object item in en) res.Add(item);
            }
            return res;
        }
    }
}
