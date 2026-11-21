using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

[assembly: CommandClass(typeof(AIEngineer.AutoCAD.PaletteCommands))]

namespace AIEngineer.AutoCAD
{
    public sealed class PaletteCommands
    {
        private static PaletteSet _palette;

        [CommandMethod("AIENGPALETTE", CommandFlags.Session)]
        public void ShowPalette()
        {
            if (_palette == null)
            {
                _palette = new PaletteSet(
                    "AI-Engineer", "AIENGPALETTE", new Guid("665427ec-261d-49a7-9d28-c96769ac59ef"));
                _palette.Style = PaletteSetStyles.ShowAutoHideButton |
                    PaletteSetStyles.ShowCloseButton |
                    PaletteSetStyles.ShowPropertiesMenu;
                _palette.DockEnabled = DockSides.Left | DockSides.Right;
                _palette.MinimumSize = new Size(340, 480);
                _palette.Add("Copilot", new CopilotPaletteControl());
                _palette.Add("Project Agent", new ProjectAgentPaletteControl());
                _palette.Add("Drafting Preview", new DraftingPreviewPaletteControl());
                _palette.Add("Quantity Takeoff", new QuantityTakeoffPaletteControl());
                _palette.Add("LV Calculations", new LowVoltageCalculationPaletteControl());
                _palette.Add("Panels / SLD", new PanelSldPaletteControl());
                _palette.Add("Code Compliance", new CompliancePaletteControl());
                _palette.Add("Product Discovery", new ManufacturerDiscoveryPaletteControl());
                _palette.Add("Manufacturers", new ManufacturerReviewPaletteControl());
            }
            _palette.Visible = true;
        }
    }

    /// <summary>
    /// Natural-language project work through the existing reviewed developer-agent loop.
    /// The model never receives an unrestricted AutoCAD/OS command channel: it can only
    /// propose registered typed commands, and the exact plan/action remains reviewable.
    /// </summary>
    internal sealed class ProjectAgentPaletteControl : UserControl
    {
        private readonly TextBox _goal = new TextBox();
        private readonly TextBox _activity = new TextBox();
        private readonly Label _status = new Label();
        private readonly CheckBox _autoSafe = new CheckBox();
        private readonly Button _start = new Button();
        private readonly Button _refresh = new Button();
        private readonly Button _approvePlan = new Button();
        private readonly Button _captureComponents = new Button();
        private readonly Button _rejectPlan = new Button();
        private readonly Button _continue = new Button();
        private readonly Button _approveAction = new Button();
        private readonly Button _rejectAction = new Button();
        private readonly Button _approveSensitive = new Button();
        private readonly Button _cancel = new Button();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly HttpClient _client;
        private Dictionary<string, object> _run;
        private bool _busy;

        public ProjectAgentPaletteControl()
        {
            _serializer.MaxJsonLength = 1048576;
            _client = CreateClient();
            BuildLayout();
            Render();
            ShownStatus("وكيل المشروع جاهز. اكتب المطلوب وسيعرض خطة قبل التنفيذ.");
        }

        private static HttpClient CreateClient()
        {
            int port = 8765;
            int parsed;
            string configured = Environment.GetEnvironmentVariable("AI_ENGINEER_API_PORT");
            if (!String.IsNullOrWhiteSpace(configured) &&
                Int32.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
                parsed >= 1024 && parsed <= 65535)
                port = parsed;
            HttpClientHandler handler = new HttpClientHandler();
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            client.Timeout = TimeSpan.FromSeconds(120);
            return client;
        }

        private void BuildLayout()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            RightToLeft = RightToLeft.Yes;
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 6;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _goal.Dock = DockStyle.Fill;
            _goal.Multiline = true;
            _goal.MaxLength = 16384;
            _goal.ScrollBars = ScrollBars.Vertical;
            _goal.Text = "راجع المشروع ونفّذ المطلوب بعد عرض الخطة للموافقة";
            _activity.Dock = DockStyle.Fill;
            _activity.Multiline = true;
            _activity.ReadOnly = true;
            _activity.ScrollBars = ScrollBars.Both;
            _activity.WordWrap = true;
            _status.AutoSize = true;
            _status.MaximumSize = new Size(330, 0);
            _autoSafe.Text = "بعد موافقة الخطة: نفّذ تلقائيًا الخطوات المحلية منخفضة/متوسطة الخطورة";
            _autoSafe.AutoSize = true;
            _autoSafe.Checked = true;

            _start.Text = "جهّز الخطة";
            _refresh.Text = "تحديث";
            _captureComponents.Text = "التقط سياق المكونات";
            _approvePlan.Text = "موافقة على الخطة";
            _rejectPlan.Text = "رفض الخطة";
            _continue.Text = "الخطوة التالية";
            _approveAction.Text = "موافقة على الخطوة";
            _rejectAction.Text = "رفض الخطوة";
            _approveSensitive.Text = "موافقة حساسة نهائية";
            _cancel.Text = "إلغاء التشغيل";

            FlowLayoutPanel top = new FlowLayoutPanel();
            top.AutoSize = true;
            top.Controls.Add(_start);
            top.Controls.Add(_refresh);
            top.Controls.Add(_captureComponents);
            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.Controls.Add(_approvePlan);
            actions.Controls.Add(_rejectPlan);
            actions.Controls.Add(_continue);
            actions.Controls.Add(_approveAction);
            actions.Controls.Add(_rejectAction);
            actions.Controls.Add(_approveSensitive);
            actions.Controls.Add(_cancel);

            layout.Controls.Add(_goal, 0, 0);
            layout.Controls.Add(_autoSafe, 0, 1);
            layout.Controls.Add(top, 0, 2);
            layout.Controls.Add(_activity, 0, 3);
            layout.Controls.Add(actions, 0, 4);
            layout.Controls.Add(_status, 0, 5);
            Controls.Add(layout);

            _start.Click += async delegate { await CreateRun(); };
            _refresh.Click += async delegate { await RefreshRun(); };
            _captureComponents.Click += async delegate { await CaptureComponents(); };
            _approvePlan.Click += async delegate { await ReviewPlan(true); };
            _rejectPlan.Click += async delegate { await ReviewPlan(false); };
            _continue.Click += async delegate { await Advance(); };
            _approveAction.Click += async delegate { await ReviewAction(true); };
            _rejectAction.Click += async delegate { await ReviewAction(false); };
            _approveSensitive.Click += async delegate { await ApproveSensitive(); };
            _cancel.Click += async delegate { await CancelRun(); };
        }

        private async Task CaptureComponents()
        {
            if (!Begin()) return;
            try
            {
                ContextEvidence previous = ContextEvidenceStore.Get();
                await Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.ExecuteInCommandContextAsync(
                    delegate(object state)
                    {
                        new ContextCommands().ExportExplicitSelection();
                        return Task.FromResult(0);
                    }, null);
                ContextEvidence evidence = ContextEvidenceStore.Get();
                if (evidence == null || (previous != null && previous.ContextId == evidence.ContextId))
                {
                    ShownStatus("تم إلغاء التقاط السياق؛ لم يتغير الرسم.");
                    return;
                }
                if (evidence.Truncated || (evidence.DatabaseModified & ~(8 | 16)) != 0)
                    throw new InvalidOperationException("يلزم سياق كامل ورسم بدون تغييرات غير محفوظة.");
                Autodesk.AutoCAD.ApplicationServices.Document document =
                    Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (document == null || !File.Exists(document.Name))
                    throw new InvalidOperationException("يلزم رسم DWG محفوظ على القرص.");
                FileInfo source = new FileInfo(document.Name);
                if (source.Length > 2097152)
                    throw new InvalidOperationException("أداة معاينة التحريك الأولية تدعم رسمًا حتى 2 MiB فقط.");
                string digest;
                using (System.Security.Cryptography.SHA256 hash = System.Security.Cryptography.SHA256.Create())
                using (FileStream stream = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    digest = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                Dictionary<string, object> binding = new Dictionary<string, object>();
                binding["path"] = source.FullName;
                binding["expected_source_sha256"] = digest;
                binding["context_id"] = evidence.ContextId;
                binding["evidence_sha256"] = evidence.Sha256;
                _goal.AppendText("\r\n\r\nSelected component context for read-only preview:\r\n" +
                    _serializer.Serialize(binding) +
                    "\r\nUse autocad.import_drawing_context to inspect exact handles, then autocad.preview_component_moves. " +
                    "Relocation execution is not enabled: report all hold points and never claim the drawing was modified.");
                ShownStatus("تم التقاط السياق محليًا دون تعديل أو إرسال؛ راجع بيانات الربط في الطلب قبل إعداد الخطة.");
            }
            catch (System.Exception error) { ShowError(error); }
            finally { End(); }
        }

        private async Task CreateRun()
        {
            if (!Begin()) return;
            try
            {
                string goal = _goal.Text == null ? String.Empty : _goal.Text.Trim();
                if (goal.Length == 0) throw new InvalidOperationException("اكتب هدفًا واضحًا أولًا.");
                Dictionary<string, object> provider = await Get("/v1/developer/status");
                if (!RequiredBool(provider, "configured"))
                    throw new InvalidOperationException("مزود وكيل المشروع غير مفعّل.");
                DialogResult egress = MessageBox.Show(this,
                    "سيُرسل نص الهدف الظاهر، بما فيه مراجع السياق أو أسماء الملفات التي أضفتها، إلى " +
                    RequiredString(provider, "provider_name") +
                    ". وقد تُرسل نتائج الأدوات المعتمدة المحدودة في مراحل التنفيذ والتحقق. " +
                    "لن يُرسل ملف DWG تلقائيًا. راجع الطلب أولًا. هل توافق على إعداد الخطة؟\r\n\r\n" + goal,
                    "مراجعة إرسال هدف المشروع", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (egress != DialogResult.Yes) return;
                Dictionary<string, object> request = new Dictionary<string, object>();
                request["goal"] = goal;
                request["maximum_actions"] = 10;
                ShownStatus("جارٍ إعداد خطة محدودة وقابلة للمراجعة...");
                _run = await Post("/v1/developer/runs", request);
                Render();
            }
            catch (System.Exception error) { ShowError(error); }
            finally { End(); }
        }

        private async Task RefreshRun()
        {
            if (!Begin()) return;
            try
            {
                if (_run == null)
                {
                    Dictionary<string, object> provider = await Get("/v1/developer/status");
                    ShownStatus(RequiredBool(provider, "configured")
                        ? "الاتصال جاهز والمزود: " + RequiredString(provider, "provider_name")
                        : "الخدمة متصلة، لكن مزود وكيل المشروع غير مفعّل.");
                    return;
                }
                _run = await Get(RunPath());
                Render();
            }
            catch (System.Exception error) { ShowError(error); }
            finally { End(); }
        }

        private async Task ReviewPlan(bool approved)
        {
            if (!Begin()) return;
            try
            {
                RequireStatus("awaiting_plan_review");
                if (approved)
                {
                    DialogResult answer = MessageBox.Show(this,
                        "أنت توافق على الخطة الظاهرة فقط. التنفيذ سيظل محصورًا في الأدوات المسجلة، وأي أمر حساس سيطلب موافقة إضافية. هل نبدأ؟",
                        "موافقة تنفيذ الخطة", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes) return;
                }
                _run = await Post(RunPath() + "/plan-review", BoolRequest(approved));
                Render();
                if (approved && _autoSafe.Checked) await AutoContinueSafe();
            }
            catch (System.Exception error) { ShowError(error); }
            finally { End(); }
        }

        private async Task Advance()
        {
            if (!Begin()) return;
            try
            {
                RequireStatus("ready");
                _run = await Post(RunPath() + "/advance", new Dictionary<string, object>());
                Render();
                if (_autoSafe.Checked) await AutoContinueSafe();
            }
            catch (System.Exception error) { ShowError(error); }
            finally { End(); }
        }

        private async Task ReviewAction(bool approved)
        {
            if (!Begin()) return;
            try
            {
                RequireStatus("awaiting_action_review");
                Dictionary<string, object> action = RequiredObject(_run, "pending_action");
                if (approved)
                {
                    string exact = "الأداة: " + RequiredString(action, "command_name") + "\r\n" +
                        "المخاطرة: " + RequiredString(action, "risk") + "\r\n" +
                        "السبب: " + RequiredString(action, "rationale") + "\r\n\r\n" +
                        "المدخلات الدقيقة:\r\n" + _serializer.Serialize(RequiredObject(action, "payload")) +
                        "\r\n\r\nهل توافق على هذه الخطوة؟";
                    DialogResult answer = MessageBox.Show(this, exact, "مراجعة الخطوة",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes) return;
                }
                _run = await Post(RunPath() + "/action-review", BoolRequest(approved));
                Render();
                if (approved && _autoSafe.Checked) await AutoContinueSafe();
            }
            catch (System.Exception error) { ShowError(error); }
            finally { End(); }
        }

        private async Task ApproveSensitive()
        {
            if (!Begin()) return;
            try
            {
                RequireStatus("awaiting_command_approval");
                Dictionary<string, object> action = RequiredObject(_run, "pending_action");
                string approvalId = RequiredString(action, "approval_id");
                string warning = "هذه موافقة نهائية على أمر حساس/عالِ الخطورة:\r\n\r\n" +
                    RequiredString(action, "command_name") + "\r\n" +
                    _serializer.Serialize(RequiredObject(action, "payload")) +
                    "\r\n\r\nلن تُعتبر موافقة دائمة. هل تنفذ هذه العملية مرة واحدة؟";
                DialogResult answer = MessageBox.Show(this, warning, "تأكيد حساس نهائي",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
                await Post("/v1/approvals/" + Uri.EscapeDataString(approvalId) + "/decision", BoolRequest(true));
                _run = await Post(RunPath() + "/advance", new Dictionary<string, object>());
                Render();
                if (_autoSafe.Checked) await AutoContinueSafe();
            }
            catch (System.Exception error) { ShowError(error); }
            finally { End(); }
        }

        private async Task CancelRun()
        {
            if (!Begin()) return;
            try
            {
                if (_run == null) return;
                _run = await Post(RunPath() + "/cancel", new Dictionary<string, object>());
                Render();
            }
            catch (System.Exception error) { ShowError(error); }
            finally { End(); }
        }

        private async Task AutoContinueSafe()
        {
            for (int count = 0; count < 24; count++)
            {
                string status = RequiredString(_run, "status");
                if (status == "ready")
                {
                    ShownStatus("يحلل الوكيل الخطوة التالية...");
                    _run = await Post(RunPath() + "/advance", new Dictionary<string, object>());
                    Render();
                    continue;
                }
                if (status == "awaiting_action_review")
                {
                    Dictionary<string, object> action = RequiredObject(_run, "pending_action");
                    string risk = RequiredString(action, "risk").ToLowerInvariant();
                    if (risk != "low" && risk != "moderate") return;
                    ShownStatus("ينفذ ويتحقق من: " + RequiredString(action, "command_name"));
                    _run = await Post(RunPath() + "/action-review", BoolRequest(true));
                    Render();
                    continue;
                }
                return;
            }
            ShownStatus("توقف التنفيذ التلقائي عند حد الأمان؛ راجع الحالة واضغط الخطوة التالية.");
        }

        private void Render()
        {
            bool has = _run != null;
            string status = has ? RequiredString(_run, "status") : String.Empty;
            _approvePlan.Enabled = !_busy && status == "awaiting_plan_review";
            _rejectPlan.Enabled = !_busy && status == "awaiting_plan_review";
            _continue.Enabled = !_busy && status == "ready";
            _approveAction.Enabled = !_busy && status == "awaiting_action_review";
            _rejectAction.Enabled = !_busy && status == "awaiting_action_review";
            _approveSensitive.Enabled = !_busy && status == "awaiting_command_approval";
            _cancel.Enabled = !_busy && has && status != "succeeded" && status != "failed" &&
                status != "cancelled" && status != "limit_reached";
            _start.Enabled = !_busy;
            _refresh.Enabled = !_busy;
            _captureComponents.Enabled = !_busy;
            if (!has)
            {
                _activity.Text = "لا يوجد تشغيل حالي. اكتب طلبًا ثم اختر «جهّز الخطة».";
                return;
            }
            StringBuilder text = new StringBuilder();
            text.AppendLine("الحالة: " + status);
            text.AppendLine("المزود: " + RequiredString(_run, "provider_name"));
            text.AppendLine("التقدم: " + RequiredInt(_run, "action_count") + "/" + RequiredInt(_run, "maximum_actions"));
            object raw;
            if (_run.TryGetValue("plan_summary", out raw) && raw is string)
                text.AppendLine("\r\nملخص الخطة: " + (string)raw);
            if (_run.TryGetValue("plan_steps", out raw) && raw is IEnumerable)
            {
                text.AppendLine("\r\nخطوات الخطة:");
                int number = 1;
                foreach (object item in (IEnumerable)raw)
                    text.AppendLine((number++).ToString(CultureInfo.InvariantCulture) + ". " + Convert.ToString(item, CultureInfo.InvariantCulture));
            }
            if (_run.TryGetValue("recent_history", out raw) && raw is IEnumerable)
            {
                text.AppendLine("\r\nسجل التنفيذ:");
                foreach (object item in (IEnumerable)raw)
                {
                    Dictionary<string, object> record = item as Dictionary<string, object>;
                    if (record == null) continue;
                    text.AppendLine("• " + OptionalString(record, "command_name") + " — " +
                        OptionalString(record, "task_status") + " — " + OptionalString(record, "verification_reason"));
                }
            }
            if (_run.TryGetValue("pending_action", out raw) && raw is Dictionary<string, object>)
            {
                Dictionary<string, object> action = (Dictionary<string, object>)raw;
                text.AppendLine("\r\nالخطوة المنتظرة:");
                text.AppendLine("الأداة: " + RequiredString(action, "command_name"));
                text.AppendLine("درجة المخاطرة: " + RequiredString(action, "risk"));
                text.AppendLine("السبب: " + RequiredString(action, "rationale"));
                text.AppendLine("المدخلات: " + _serializer.Serialize(RequiredObject(action, "payload")));
            }
            if (_run.TryGetValue("completion_summary", out raw) && raw is string)
                text.AppendLine("\r\nاكتمل: " + (string)raw);
            if (_run.TryGetValue("error", out raw) && raw is string)
                text.AppendLine("\r\nخطأ: " + (string)raw);
            _activity.Text = text.ToString();
            ShownStatus(StatusMessage(status));
        }

        private static string StatusMessage(string status)
        {
            if (status == "awaiting_plan_review") return "راجع الخطة ثم وافق أو ارفض.";
            if (status == "ready") return "الخطة معتمدة؛ الوكيل جاهز للخطوة التالية.";
            if (status == "awaiting_action_review") return "خطوة تنتظر مراجعتك.";
            if (status == "awaiting_command_approval") return "أمر حساس ينتظر موافقة نهائية منفصلة.";
            if (status == "succeeded") return "اكتمل الهدف وتم التحقق من النتائج.";
            if (status == "failed") return "توقف التشغيل بسبب فشل موضح في السجل.";
            if (status == "cancelled") return "تم إلغاء التشغيل.";
            if (status == "limit_reached") return "تم الوصول إلى الحد الأقصى للخطوات.";
            return "الحالة: " + status;
        }

        private Dictionary<string, object> BoolRequest(bool value)
        {
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["approved"] = value;
            return request;
        }

        private string RunPath()
        {
            if (_run == null) throw new InvalidOperationException("لا يوجد تشغيل حالي.");
            return "/v1/developer/runs/" + Uri.EscapeDataString(RequiredString(_run, "run_id"));
        }

        private void RequireStatus(string expected)
        {
            string actual = _run == null ? String.Empty : RequiredString(_run, "status");
            if (actual != expected) throw new InvalidOperationException("الحالة الحالية لا تسمح بهذه العملية: " + actual);
        }

        private async Task<Dictionary<string, object>> Get(string path)
        {
            return await Request(HttpMethod.Get, path, null);
        }

        private async Task<Dictionary<string, object>> Post(string path, Dictionary<string, object> value)
        {
            return await Request(HttpMethod.Post, path, value);
        }

        private async Task<Dictionary<string, object>> Request(
            HttpMethod method, string path, Dictionary<string, object> value)
        {
            string token = Environment.GetEnvironmentVariable("AI_ENGINEER_API_TOKEN");
            if (String.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("AI_ENGINEER_API_TOKEN غير مضبوط لبرنامج AutoCAD.");
            using (HttpRequestMessage request = new HttpRequestMessage(method, path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (value != null)
                    request.Content = new StringContent(_serializer.Serialize(value), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string content = await response.Content.ReadAsStringAsync();
                    if (content.Length > 1048576)
                        throw new InvalidOperationException("استجابة الخدمة المحلية تجاوزت الحد المسموح.");
                    if (!response.IsSuccessStatusCode)
                    {
                        string detail = content;
                        try
                        {
                            Dictionary<string, object> error = _serializer.DeserializeObject(content) as Dictionary<string, object>;
                            object raw;
                            if (error != null && error.TryGetValue("detail", out raw)) detail = Convert.ToString(raw, CultureInfo.InvariantCulture);
                        }
                        catch (System.Exception) { }
                        if (detail != null && detail.Length > 512) detail = detail.Substring(0, 512);
                        throw new InvalidOperationException("Local API HTTP " + (int)response.StatusCode + ": " + detail);
                    }
                    Dictionary<string, object> document = _serializer.DeserializeObject(content) as Dictionary<string, object>;
                    if (document == null) throw new InvalidOperationException("استجابة الخدمة المحلية غير صالحة.");
                    return document;
                }
            }
        }

        private bool Begin()
        {
            if (_busy) return false;
            _busy = true;
            Render();
            return true;
        }

        private void End()
        {
            _busy = false;
            Render();
        }

        private void ShowError(System.Exception error)
        {
            ShownStatus("تعذر الطلب: " + error.Message);
        }

        private void ShownStatus(string value)
        {
            _status.Text = value;
        }

        private static Dictionary<string, object> RequiredObject(Dictionary<string, object> source, string key)
        {
            object raw;
            Dictionary<string, object> value;
            if (!source.TryGetValue(key, out raw) || (value = raw as Dictionary<string, object>) == null)
                throw new InvalidOperationException("Local API field is missing or invalid: " + key);
            return value;
        }

        private static string RequiredString(Dictionary<string, object> source, string key)
        {
            object raw;
            string value;
            if (!source.TryGetValue(key, out raw) || (value = raw as string) == null || value.Length == 0)
                throw new InvalidOperationException("Local API field is missing or invalid: " + key);
            return value;
        }

        private static string OptionalString(Dictionary<string, object> source, string key)
        {
            object raw;
            return source.TryGetValue(key, out raw) && raw != null
                ? Convert.ToString(raw, CultureInfo.InvariantCulture) : String.Empty;
        }

        private static int RequiredInt(Dictionary<string, object> source, string key)
        {
            object raw;
            if (!source.TryGetValue(key, out raw) || !(raw is int))
                throw new InvalidOperationException("Local API field is missing or invalid: " + key);
            return (int)raw;
        }

        private static bool RequiredBool(Dictionary<string, object> source, string key)
        {
            object raw;
            if (!source.TryGetValue(key, out raw) || !(raw is bool))
                throw new InvalidOperationException("Local API field is missing or invalid: " + key);
            return (bool)raw;
        }
    }

    internal sealed class CopilotPaletteControl : UserControl
    {
        private readonly ComboBox _language = new ComboBox();
        private readonly ComboBox _providerCombo = new ComboBox();
        private readonly ComboBox _modelCombo = new ComboBox();
        private readonly Button _apiSettings = new Button();
        private readonly Label _coordSortLabel = new Label();
        private readonly ComboBox _coordSortCombo = new ComboBox();
        private readonly FlowLayoutPanel _sortRow = new FlowLayoutPanel();
        private readonly Label _context = new Label();
        private readonly Label _uncertainty = new Label();
        private readonly TextBox _transcript = new TextBox();
        private readonly TextBox _message = new TextBox();
        private readonly Button _refresh = new Button();
        private readonly Button _start = new Button();
        private readonly Button _send = new Button();
        private readonly Button _voiceMic = new Button();
        private readonly CheckBox _voiceAutoSend = new CheckBox();
        private readonly CheckBox _voiceSpeakReply = new CheckBox();
        private readonly Button _cancel = new Button();
        private readonly Button _commitDrawing = new Button();
        private readonly Button _rollbackDrawing = new Button();
        private readonly Button _prepareReview = new Button();
        private readonly Button _approveReview = new Button();
        private readonly Button _rejectReview = new Button();
        private readonly CheckBox _includeDrawingContext = new CheckBox();
        private readonly Label _status = new Label();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly HttpClient _client;
        private DirectAiConfig _directConfig;
        private readonly DirectCadAgentSession _directSession = new DirectCadAgentSession();
        private readonly WaveInAudioRecorder _voiceRecorder = new WaveInAudioRecorder();
        private readonly System.Windows.Forms.Timer _voiceTimer = new System.Windows.Forms.Timer();
        private bool _voiceTranscribing;
        private bool _suppressProviderEvents;
        private string _sessionId;
        private string _ownerKey;
        private string _pendingReviewId;
        private string _lastMessage;
        private string _summaryText;
        private int _deviceCount;
        private bool _waitingForDrawingClarification = false;
        private int _routeCount;
        private int _unknownCount;
        private int _warningCount;
        private int _blockReferenceCount;
        private int _routePrimitiveCount;
        private bool _hasSessionSummary;
        private bool _generalSession;
        private bool _busy;

        public CopilotPaletteControl()
        {
            _serializer.MaxJsonLength = 1048576;
            _directConfig = DirectAiConfig.Load();
            _client = CreateClient();
            BuildLayout();
            SyncProviderModelUi();
            ApplyLanguage();
        }

        private static HttpClient CreateClient()
        {
            int port = 8765;
            string configured = Environment.GetEnvironmentVariable("AI_ENGINEER_API_PORT");
            int parsed;
            if (!String.IsNullOrWhiteSpace(configured) &&
                Int32.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
                parsed >= 1024 && parsed <= 65535)
                port = parsed;
            HttpClientHandler handler = new HttpClientHandler();
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            client.Timeout = TimeSpan.FromSeconds(75);
            return client;
        }

        private void BuildLayout()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowCount = 10;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _language.DropDownStyle = ComboBoxStyle.DropDownList;
            _language.Width = 68;
            _language.DropDownWidth = 96;
            _language.Margin = new Padding(2);
            _language.Items.Add("العربية");
            _language.Items.Add("English");
            _language.SelectedIndex = 0;
            _language.SelectedIndexChanged += delegate { ApplyLanguage(); };

            _providerCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _providerCombo.Width = 176;
            _providerCombo.DropDownWidth = 225;
            _providerCombo.Margin = new Padding(2);
            _providerCombo.Items.Add("Gemini Direct (API)");
            _providerCombo.Items.Add("Claude Direct (API)");
            _providerCombo.Items.Add("OpenAI / Codex Direct (API)");
            _providerCombo.Items.Add("Local Bridge (127.0.0.1)");
            _providerCombo.SelectedIndexChanged += delegate
            {
                if (_suppressProviderEvents) return;
                _directConfig.Provider = SelectedProvider;
                _directConfig.Save();
                PopulateModelPresets();
                ApplyLanguage();
                RefreshContext();
            };

            _modelCombo.DropDownStyle = ComboBoxStyle.DropDown;
            _modelCombo.Width = 176;
            _modelCombo.DropDownWidth = 235;
            _modelCombo.Margin = new Padding(2);
            _modelCombo.Leave += delegate
            {
                if (!_suppressProviderEvents && IsDirectMode && !string.IsNullOrWhiteSpace(_modelCombo.Text))
                {
                    _directConfig.SetModel(SelectedProvider, _modelCombo.Text);
                    _directConfig.Save();
                }
            };

            _apiSettings.Text = "🔑 API";
            _apiSettings.AutoSize = true;
            _apiSettings.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _apiSettings.Margin = new Padding(2);
            _apiSettings.Padding = new Padding(4, 2, 4, 2);
            _apiSettings.Click += delegate
            {
                using (ApiKeysDialog dlg = new ApiKeysDialog(_directConfig, IsArabic))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _directConfig = DirectAiConfig.Load();
                        SyncProviderModelUi();
                        RefreshContext();
                        UpdateActionState();
                    }
                }
            };

            FlowLayoutPanel topRow = new FlowLayoutPanel();
            topRow.Dock = DockStyle.Fill;
            topRow.AutoSize = true;
            topRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            topRow.WrapContents = true;
            topRow.MaximumSize = new Size(220, 0);
            topRow.Margin = new Padding(0, 2, 0, 2);
            topRow.Controls.Add(_providerCombo);
            topRow.Controls.Add(_language);

            FlowLayoutPanel modelRow = new FlowLayoutPanel();
            modelRow.Dock = DockStyle.Fill;
            modelRow.AutoSize = true;
            modelRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            modelRow.WrapContents = true;
            modelRow.MaximumSize = new Size(220, 0);
            modelRow.Margin = new Padding(0, 2, 0, 2);
            modelRow.Controls.Add(_modelCombo);
            modelRow.Controls.Add(_apiSettings);

            _coordSortLabel.AutoSize = true;
            _coordSortLabel.Margin = new Padding(2, 5, 2, 2);
            _coordSortCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _coordSortCombo.Width = 158;
            _coordSortCombo.DropDownWidth = 225;
            _coordSortCombo.Margin = new Padding(2);
            _coordSortCombo.SelectedIndexChanged += delegate
            {
                if (_suppressProviderEvents) return;
                _directConfig.PreferredCoordSortOrder = IndexToSortOrder(_coordSortCombo.SelectedIndex);
                _directConfig.Save();
                SetStatus((IsArabic ? "اتجاه ترقيم الإحداثيات: " : "Coordinate numbering order: ") +
                          DirectAiConfig.DescribeSortOrderPref(_directConfig.PreferredCoordSortOrder, IsArabic));
            };

            _sortRow.Dock = DockStyle.Fill;
            _sortRow.AutoSize = true;
            _sortRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _sortRow.WrapContents = true;
            _sortRow.MaximumSize = new Size(220, 0);
            _sortRow.Margin = new Padding(0, 1, 0, 2);
            _sortRow.Controls.Add(_coordSortLabel);
            _sortRow.Controls.Add(_coordSortCombo);

            _context.AutoSize = true;
            _context.MaximumSize = new Size(260, 0);
            _uncertainty.AutoSize = true;
            _uncertainty.MaximumSize = new Size(260, 0);
            _uncertainty.ForeColor = Color.DarkOrange;
            _transcript.Dock = DockStyle.Fill;
            _transcript.Multiline = true;
            _transcript.ReadOnly = true;
            _transcript.ScrollBars = ScrollBars.Vertical;
            _message.Dock = DockStyle.Fill;
            _message.Multiline = true;
            _message.MaxLength = 16384;
            _message.ScrollBars = ScrollBars.Vertical;
            _message.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
                {
                    e.SuppressKeyPress = true;
                    await SendMessage();
                }
            };
            _status.AutoSize = true;
            _status.MaximumSize = new Size(260, 0);

            Button[] compactButtons = new Button[] { _refresh, _start, _send, _voiceMic, _commitDrawing, _rollbackDrawing, _cancel };
            foreach (Button btn in compactButtons)
            {
                btn.AutoSize = true;
                btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                btn.Margin = new Padding(2);
                btn.Padding = new Padding(4, 2, 4, 2);
            }
            _includeDrawingContext.AutoSize = true;
            _includeDrawingContext.Margin = new Padding(2);
            _voiceAutoSend.AutoSize = true;
            _voiceAutoSend.Margin = new Padding(4, 5, 2, 2);
            _voiceAutoSend.Checked = _directConfig != null ? _directConfig.VoiceAutoSend : true;
            _voiceAutoSend.CheckedChanged += delegate
            {
                if (_suppressProviderEvents || _directConfig == null) return;
                _directConfig.VoiceAutoSend = _voiceAutoSend.Checked;
                _directConfig.Save();
            };
            _voiceSpeakReply.AutoSize = true;
            _voiceSpeakReply.Margin = new Padding(4, 5, 2, 2);
            _voiceSpeakReply.Checked = _directConfig != null ? _directConfig.VoiceSpeakReplies : true;
            _voiceSpeakReply.CheckedChanged += delegate
            {
                if (_suppressProviderEvents || _directConfig == null) return;
                _directConfig.VoiceSpeakReplies = _voiceSpeakReply.Checked;
                _directConfig.Save();
                if (!_voiceSpeakReply.Checked) VoiceSpeechOutput.Stop();
            };
            _voiceTimer.Interval = 300;
            _voiceTimer.Tick += async delegate
            {
                if (!_voiceRecorder.IsRecording)
                {
                    _voiceTimer.Stop();
                    return;
                }
                int sec = _voiceRecorder.ElapsedSeconds;
                UpdateVoiceMicVisuals();
                if (sec >= 120)
                {
                    _voiceTimer.Stop();
                    await ToggleVoiceRecordingAsync();
                }
            };

            FlowLayoutPanel contextButtons = new FlowLayoutPanel();
            contextButtons.Dock = DockStyle.Fill;
            contextButtons.AutoSize = true;
            contextButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            contextButtons.WrapContents = true;
            contextButtons.Margin = new Padding(0, 2, 0, 2);
            contextButtons.Controls.Add(_refresh);
            contextButtons.Controls.Add(_start);
            contextButtons.Controls.Add(_includeDrawingContext);
            FlowLayoutPanel actionButtons = new FlowLayoutPanel();
            actionButtons.Dock = DockStyle.Fill;
            actionButtons.AutoSize = true;
            actionButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actionButtons.WrapContents = true;
            actionButtons.Margin = new Padding(0, 2, 0, 2);
            actionButtons.Controls.Add(_send);
            actionButtons.Controls.Add(_voiceMic);
            actionButtons.Controls.Add(_voiceAutoSend);
            actionButtons.Controls.Add(_voiceSpeakReply);
            actionButtons.Controls.Add(_commitDrawing);
            actionButtons.Controls.Add(_rollbackDrawing);
            actionButtons.Controls.Add(_cancel);

            layout.SizeChanged += delegate
            {
                int clientW = Math.Max(160, layout.ClientSize.Width);
                int wrapW = Math.Max(160, clientW - 10);
                Size maxSz = new Size(wrapW, 0);
                Size rowMaxSz = new Size(Math.Max(160, clientW - 4), 0);
                topRow.MaximumSize = rowMaxSz;
                modelRow.MaximumSize = rowMaxSz;
                _sortRow.MaximumSize = rowMaxSz;
                contextButtons.MaximumSize = rowMaxSz;
                actionButtons.MaximumSize = rowMaxSz;
                int singleComboW = Math.Min(215, Math.Max(168, wrapW - 4));
                _providerCombo.Width = (wrapW >= 254) ? 176 : singleComboW;
                int apiPrefW = (_apiSettings.PreferredSize.Width > 0 ? _apiSettings.PreferredSize.Width : _apiSettings.Width) + 8;
                _modelCombo.Width = (wrapW - apiPrefW >= 168)
                    ? Math.Min(215, wrapW - apiPrefW)
                    : singleComboW;
                int sortLblPrefW = (_coordSortLabel.PreferredSize.Width > 0 ? _coordSortLabel.PreferredSize.Width : 38) + 8;
                _coordSortCombo.Width = (wrapW - sortLblPrefW >= 145)
                    ? Math.Min(215, wrapW - sortLblPrefW)
                    : singleComboW;
                _context.MaximumSize = maxSz;
                _uncertainty.MaximumSize = maxSz;
                _status.MaximumSize = maxSz;
            };

            layout.Controls.Add(topRow, 0, 0);
            layout.Controls.Add(modelRow, 0, 1);
            layout.Controls.Add(_sortRow, 0, 2);
            layout.Controls.Add(_context, 0, 3);
            layout.Controls.Add(_uncertainty, 0, 4);
            layout.Controls.Add(contextButtons, 0, 5);
            layout.Controls.Add(_transcript, 0, 6);
            layout.Controls.Add(_message, 0, 7);
            layout.Controls.Add(actionButtons, 0, 8);
            layout.Controls.Add(_status, 0, 9);
            Controls.Add(layout);

            _refresh.Click += delegate { RefreshContext(); };
            _start.Click += async delegate { await StartSession(); };
            _send.Click += async delegate { await SendMessage(); };
            _voiceMic.Click += async delegate { await ToggleVoiceRecordingAsync(); };
            _commitDrawing.Click += delegate {
                _waitingForDrawingClarification = false;
                Autodesk.AutoCAD.ApplicationServices.Document doc =
                    Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                DrawingExecutionCommands.ApproveReview(doc);
                _transcript.AppendText("copilot: تم اعتماد وتثبيت الرسم بنجاح! 🏛️" + Environment.NewLine + Environment.NewLine);
                _transcript.SelectionStart = _transcript.TextLength;
                _transcript.ScrollToCaret();
                SetStatus(IsArabic ? "تم اعتماد وتثبيت الرسم بنجاح." : "Drawing committed.");
            };
            _rollbackDrawing.Click += delegate {
                _waitingForDrawingClarification = false;
                Autodesk.AutoCAD.ApplicationServices.Document doc =
                    Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (CadToolExecutor.GetUndoHistoryCount(doc) > 0)
                {
                    Dictionary<string, object> undoRes = CadToolExecutor.RollbackLastDirectAction(doc);
                    bool uok = undoRes != null && undoRes.ContainsKey("ok") && (undoRes["ok"] is bool) && (bool)undoRes["ok"];
                    if (uok)
                    {
                        int rMod = undoRes.ContainsKey("restored_modified_count") ? Convert.ToInt32(undoRes["restored_modified_count"], CultureInfo.InvariantCulture) : 0;
                        int rEr = undoRes.ContainsKey("erased_created_count") ? Convert.ToInt32(undoRes["erased_created_count"], CultureInfo.InvariantCulture) : 0;
                        int rUn = undoRes.ContainsKey("unerased_count") ? Convert.ToInt32(undoRes["unerased_count"], CultureInfo.InvariantCulture) : 0;
                        int rem = undoRes.ContainsKey("remaining_undo_steps") ? Convert.ToInt32(undoRes["remaining_undo_steps"], CultureInfo.InvariantCulture) : 0;
                        string msgAr = "copilot: تم التراجع بنجاح عن آخر إجراء للذكاء الصناعي (استعادة حالة: " + rMod + " | حذف مرسوم: " + rEr + " | استرجاع محذوف: " + rUn + " | متبقي بالتاريخ: " + rem + ").";
                        string msgEn = "copilot: Reverted last AI CAD action (restored=" + rMod + ", erased=" + rEr + ", unerased=" + rUn + ", remaining=" + rem + ").";
                        _transcript.AppendText((IsArabic ? msgAr : msgEn) + Environment.NewLine + Environment.NewLine);
                        SetStatus(IsArabic ? "تم التراجع عن آخر تعديل فعلياً." : "Last AI CAD action reverted.");
                    }
                    else
                    {
                        string uerr = (undoRes != null && undoRes.ContainsKey("error") && undoRes["error"] != null) ? undoRes["error"].ToString() : "Undo failed.";
                        _transcript.AppendText("copilot: " + uerr + Environment.NewLine + Environment.NewLine);
                        SetStatus(uerr);
                    }
                }
                else if (DrawingExecutionCommands.HasReviewEntities(doc))
                {
                    DrawingExecutionCommands.RollbackReview(doc);
                    _transcript.AppendText("copilot: تم التراجع عن الرسم وحذف خطوط المعاينة من المخطط بنجاح." + Environment.NewLine + Environment.NewLine);
                    SetStatus(IsArabic ? "تم التراجع وحذف المقترح." : "Preview rolled back.");
                }
                else
                {
                    _transcript.AppendText((IsArabic
                        ? "copilot: لا توجد تعديلات ذكاء صناعي أو خطوط معاينة مسجلة للتراجع عنها حالياً (للتراجع عن تعديلات سابقة استخدم Ctrl+Z في أوتوكاد)."
                        : "copilot: No AI CAD changes or preview entities recorded to rollback (use Ctrl+Z in AutoCAD for prior session changes).") + Environment.NewLine + Environment.NewLine);
                    SetStatus(IsArabic ? "لا يوجد تعديل مسجل للتراجع." : "Nothing recorded to rollback.");
                }
                _transcript.SelectionStart = _transcript.TextLength;
                _transcript.ScrollToCaret();
            };
            _prepareReview.Click += async delegate { await PrepareReview(); };
            _approveReview.Click += async delegate { await DecideAndRequestAnswer(true); };
            _rejectReview.Click += async delegate { await DecideAndRequestAnswer(false); };
            _cancel.Click += async delegate { await CancelSession(); };
        }

        private string SelectedProvider
        {
            get
            {
                if (_providerCombo.SelectedIndex == 1) return DirectAiConfig.ProviderClaude;
                if (_providerCombo.SelectedIndex == 2) return DirectAiConfig.ProviderOpenAI;
                if (_providerCombo.SelectedIndex == 3) return DirectAiConfig.ProviderLocal;
                return DirectAiConfig.ProviderGemini;
            }
        }

        private bool IsDirectMode { get { return SelectedProvider != DirectAiConfig.ProviderLocal; } }

        private string SelectedModel { get { return (_modelCombo.Text ?? "").Trim(); } }

        private void SyncProviderModelUi()
        {
            _suppressProviderEvents = true;
            try
            {
                string p = (_directConfig.Provider ?? DirectAiConfig.ProviderGemini).ToLowerInvariant();
                if (p == DirectAiConfig.ProviderClaude) _providerCombo.SelectedIndex = 1;
                else if (p == DirectAiConfig.ProviderOpenAI) _providerCombo.SelectedIndex = 2;
                else if (p == DirectAiConfig.ProviderLocal) _providerCombo.SelectedIndex = 3;
                else _providerCombo.SelectedIndex = 0;
                PopulateModelPresets();
                PopulateSortOrderCombo();
            }
            finally
            {
                _suppressProviderEvents = false;
            }
        }

        private static string IndexToSortOrder(int index)
        {
            if (index == 1) return DirectAiConfig.SortOrderLeftToRight;
            if (index == 2) return DirectAiConfig.SortOrderRightToLeft;
            if (index == 3) return DirectAiConfig.SortOrderTopToBottom;
            if (index == 4) return DirectAiConfig.SortOrderBottomToTop;
            return DirectAiConfig.SortOrderAsk;
        }

        private static int SortOrderToIndex(string sortOrder)
        {
            string norm = DirectAiConfig.NormalizeSortOrderPref(sortOrder);
            if (norm == DirectAiConfig.SortOrderLeftToRight) return 1;
            if (norm == DirectAiConfig.SortOrderRightToLeft) return 2;
            if (norm == DirectAiConfig.SortOrderTopToBottom) return 3;
            if (norm == DirectAiConfig.SortOrderBottomToTop) return 4;
            return 0;
        }

        private void PopulateSortOrderCombo()
        {
            bool prevSuppress = _suppressProviderEvents;
            _suppressProviderEvents = true;
            try
            {
                int targetIdx = SortOrderToIndex(_directConfig != null ? _directConfig.PreferredCoordSortOrder : DirectAiConfig.SortOrderAsk);
                _coordSortLabel.Text = IsArabic ? "الترقيم:" : "Order:";
                _coordSortCombo.Items.Clear();
                if (IsArabic)
                {
                    _coordSortCombo.Items.Add("حسب الشات (اسألني)");
                    _coordSortCombo.Items.Add("من الشمال لليمين (L → R)");
                    _coordSortCombo.Items.Add("من اليمين للشمال (R → L)");
                    _coordSortCombo.Items.Add("من فوق لتحت (T → B)");
                    _coordSortCombo.Items.Add("من تحت لفوق (B → T)");
                }
                else
                {
                    _coordSortCombo.Items.Add("Per chat (Ask me)");
                    _coordSortCombo.Items.Add("Left to Right (L → R)");
                    _coordSortCombo.Items.Add("Right to Left (R → L)");
                    _coordSortCombo.Items.Add("Top to Bottom (T → B)");
                    _coordSortCombo.Items.Add("Bottom to Top (B → T)");
                }
                if (targetIdx < 0 || targetIdx >= _coordSortCombo.Items.Count) targetIdx = 0;
                _coordSortCombo.SelectedIndex = targetIdx;
            }
            finally
            {
                _suppressProviderEvents = prevSuppress;
            }
        }

        private void PopulateModelPresets()
        {
            _modelCombo.Items.Clear();
            if (!IsDirectMode)
            {
                _modelCombo.Enabled = false;
                _modelCombo.Text = "local-bridge";
                return;
            }
            _modelCombo.Enabled = true;
            foreach (string m in DirectAiConfig.GetModelPresets(SelectedProvider))
                _modelCombo.Items.Add(m);
            _modelCombo.Text = _directConfig.GetModel(SelectedProvider);
        }

        private bool IsArabic { get { return _language.SelectedIndex == 0; } }

        private void ApplyLanguage()
        {
            RightToLeft rtl = IsArabic ? RightToLeft.Yes : RightToLeft.No;
            if (RightToLeft != rtl) RightToLeft = rtl;
            PopulateSortOrderCombo();
            _sortRow.Visible = IsDirectMode;
            _refresh.Text = IsArabic ? "تحديث السياق" : "Refresh context";
            _send.Text = IsArabic ? "اسأل AI" : "Ask AI";
            _voiceAutoSend.Text = IsArabic ? "إرسال تلقائي" : "Auto-send";
            _voiceSpeakReply.Text = IsArabic ? "🔊 نطق الرد" : "🔊 Speak";
            UpdateVoiceMicVisuals();
            _commitDrawing.Text = IsArabic ? "✅ اعتماد الرسم (تمام)" : "✅ Commit (تمام)";
            _rollbackDrawing.Text = IsArabic ? "❌ تراجع" : "❌ Rollback";
            _commitDrawing.BackColor = Color.FromArgb(220, 245, 220);
            _rollbackDrawing.BackColor = Color.FromArgb(255, 228, 225);
            _commitDrawing.FlatStyle = FlatStyle.Flat;
            _rollbackDrawing.FlatStyle = FlatStyle.Flat;
            _commitDrawing.AutoSize = true;
            _rollbackDrawing.AutoSize = true;
            _includeDrawingContext.Text = IsArabic ? "استخدام سياق الرسم" : "Use drawing context";
            _includeDrawingContext.Visible = !IsDirectMode;
            _prepareReview.Text = IsArabic ? "معاينة إرسال AI" : "Preview AI egress";
            _approveReview.Text = IsArabic ? "موافقة وسؤال" : "Approve and ask";
            _rejectReview.Text = IsArabic ? "رفض" : "Reject";
            _cancel.Text = IsArabic ? (IsDirectMode ? "مسح الشات" : "إلغاء") : (IsDirectMode ? "Clear chat" : "Cancel");
            _message.AccessibleName = IsArabic ? "رسالة المهندس" : "Engineer message";
            UpdateActionState();
            RefreshContext();
        }

        private void RefreshContext()
        {
            if (IsDirectMode)
            {
                bool hasKey = !string.IsNullOrWhiteSpace(_directConfig.GetApiKey(SelectedProvider));
                try
                {
                    Autodesk.AutoCAD.ApplicationServices.Document doc =
                        Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    if (doc != null)
                    {
                        Dictionary<string, object> info = CadToolExecutor.ExecuteTool(
                            doc, "inspect_drawing", new Dictionary<string, object> { { "include_entities", false } });
                        if (info != null && info.ContainsKey("ok") && info["ok"] is bool && (bool)info["ok"])
                        {
                            string fname = info.ContainsKey("file_name") ? Convert.ToString(info["file_name"], CultureInfo.InvariantCulture) : "Drawing.dwg";
                            string units = info.ContainsKey("insunits_name") ? Convert.ToString(info["insunits_name"], CultureInfo.InvariantCulture) : "Unknown";
                            string total = info.ContainsKey("total_entities") ? Convert.ToString(info["total_entities"], CultureInfo.InvariantCulture) : "0";
                            _context.Text = IsArabic
                                ? "اتصال مباشر (" + SelectedProvider + "): " + fname + " | عناصر: " + total + " | الوحدات: " + units
                                : "Direct (" + SelectedProvider + "): " + fname + " | entities: " + total + " | units: " + units;
                        }
                        else
                        {
                            _context.Text = IsArabic ? "اتصال مباشر بـ AI (" + SelectedProvider + ")" : "Direct AI mode (" + SelectedProvider + ")";
                        }
                    }
                    else
                    {
                        _context.Text = IsArabic ? "اتصال مباشر بـ AI — افتح ملف DWG للرسم" : "Direct AI mode — open a DWG drawing";
                    }
                }
                catch
                {
                    _context.Text = IsArabic ? "اتصال مباشر بـ AI (" + SelectedProvider + ")" : "Direct AI mode (" + SelectedProvider + ")";
                }

                _uncertainty.ForeColor = hasKey ? Color.DarkGreen : Color.DarkOrange;
                _uncertainty.Text = hasKey
                    ? (IsArabic ? "مفتاح API جاهز — كامل صلاحيات الرسم والتعديل والفحص مفعلة بدون وسيط." : "API key ready — full direct CAD read/write/measure tools enabled.")
                    : (IsArabic ? "تنبيه: مفتاح API غير مضبوط لـ " + SelectedProvider + ". اضغط [🔑 API] للإدخال." : "Warning: API key missing for " + SelectedProvider + ". Click [🔑 API].");
                return;
            }
            _uncertainty.ForeColor = Color.DarkOrange;
            if (_hasSessionSummary && !String.IsNullOrEmpty(_sessionId))
            {
                ShowSummary();
                return;
            }
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null)
            {
                _context.Text = IsArabic
                    ? "لا يوجد سياق. شغّل AIENGCONTEXT بعد تحديد العناصر."
                    : "No context. Run AIENGCONTEXT after selecting objects.";
                _uncertainty.Text = IsArabic
                    ? "لن يتم إرسال الرسم أو تعديله."
                    : "The drawing will not be sent or modified.";
                return;
            }
            _context.Text = (IsArabic ? "السياق: " : "Context: ") + evidence.SourceMode +
                " | " + evidence.EntityCount.ToString(CultureInfo.InvariantCulture) +
                (IsArabic ? " عنصر" : " entities") + " | DBMOD=" +
                evidence.DatabaseModified.ToString(CultureInfo.InvariantCulture);
            _uncertainty.Text = evidence.DatabaseModified == 0
                ? (IsArabic ? "السياق للقراءة فقط ومحفوظ." : "Read-only context; drawing is clean.")
                : (IsArabic ? "تنبيه: الرسم به تعديلات غير محفوظة." : "Warning: drawing has unsaved changes.");
        }

        private async Task StartSession()
        {
            VoiceSpeechOutput.Stop();
            if (IsDirectMode)
            {
                _directSession.ClearHistory();
                _transcript.Clear();
                RefreshContext();
                UpdateActionState();
                string p = SelectedProvider;
                string m = string.IsNullOrWhiteSpace(SelectedModel) ? _directConfig.GetModel(p) : SelectedModel;
                SetStatus(IsArabic
                    ? "تم بدء محادثة مباشرة جديدة مع " + p + " (" + m + ")."
                    : "Started new direct chat with " + p + " (" + m + ").");
                return;
            }
            if (!BeginOperation()) return;
            try
            {
                await CreateSession();
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private async Task SendMessage()
        {
            if (IsDirectMode)
            {
                await SendDirectMessage();
                return;
            }
            if (!BeginOperation()) return;
            try
            {
                if (String.IsNullOrEmpty(_sessionId) || String.IsNullOrEmpty(_ownerKey))
                    await CreateSession();
                string content = NormalizeEgressText(_message.Text);
                if (String.IsNullOrWhiteSpace(content))
                    throw new InvalidOperationException(IsArabic ? "اكتب رسالة أولًا." : "Enter a message first.");

                if (IsRollbackText(content))
                {
                    _message.Clear();
                    _waitingForDrawingClarification = false;
                    _transcript.AppendText("engineer: " + content + Environment.NewLine);
                    Autodesk.AutoCAD.ApplicationServices.Document doc =
                        Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    DrawingExecutionCommands.RollbackReview(doc);
                    _transcript.AppendText("copilot: تم التراجع عن الرسم وحذف خطوط المعاينة من المخطط بنجاح." + Environment.NewLine + Environment.NewLine);
                    _transcript.SelectionStart = _transcript.TextLength;
                    _transcript.ScrollToCaret();
                    SetStatus(IsArabic ? "تم التراجع وحذف المقترح." : "Preview rolled back.");
                    return;
                }

                if (IsApprovalText(content))
                {
                    _message.Clear();
                    _waitingForDrawingClarification = false;
                    _transcript.AppendText("engineer: " + content + Environment.NewLine);
                    Autodesk.AutoCAD.ApplicationServices.Document doc =
                        Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    DrawingExecutionCommands.ApproveReview(doc);
                    _transcript.AppendText("copilot: تم اعتماد وتثبيت الرسم بنجاح! 🏛️ تم تحويل العناصر للطبقات الرسمية وحفظ التعديل داخل المخطط." + Environment.NewLine + Environment.NewLine);
                    _transcript.SelectionStart = _transcript.TextLength;
                    _transcript.ScrollToCaret();
                    SetStatus(IsArabic ? "تم اعتماد وتثبيت الرسم بنجاح." : "Drawing committed.");
                    return;
                }

                if (IsCopyDrawingRequest(content))
                {
                    _message.Clear();
                    _waitingForDrawingClarification = false;
                    _transcript.AppendText("engineer: " + content + Environment.NewLine);
                    Autodesk.AutoCAD.ApplicationServices.Document doc =
                        Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    string desc;
                    string err;
                    bool isClarification;
                    bool copied = DrawingExecutionCommands.DuplicateActiveDrawing(doc, content, out desc, out isClarification, out err);
                    if (!copied)
                    {
                        string failMsg = IsArabic
                            ? "copilot: تنبيه أثناء النسخ: " + (err ?? "تأكد من فتح ملف رسم نشط في أوتوكاد.") + Environment.NewLine + Environment.NewLine
                            : "copilot: Copy notice: " + (err ?? "Ensure an active drawing is open.") + Environment.NewLine + Environment.NewLine;
                        _transcript.AppendText(failMsg);
                        _transcript.SelectionStart = _transcript.TextLength;
                        _transcript.ScrollToCaret();
                        SetStatus(err ?? "Copy error.");
                        return;
                    }
                    if (isClarification)
                    {
                        _transcript.AppendText("copilot: " + desc + Environment.NewLine + Environment.NewLine);
                        _transcript.SelectionStart = _transcript.TextLength;
                        _transcript.ScrollToCaret();
                        SetStatus(IsArabic ? "تم حفظ نسخة من الملف بنجاح." : "Copy saved.");
                        return;
                    }
                    string copyResponse = "copilot: " + desc + "\r\n\r\n" +
                        (IsArabic
                            ? "📌 دورة المراجعة والاعتماد:\r\n" +
                              "• إذا أعجبتك النسخة: اكتب «تمام» أو اضغط زر [✅ اعتماد الرسم (تمام)] لتثبيتها في الطبقات الأصلية.\r\n" +
                              "• للتراجع والحذف: اكتب «تراجع» أو اضغط زر [❌ تراجع].\r\n"
                            : "Review workflow:\r\n• To commit: type 'تمام' or click [✅ Commit].\r\n• To rollback: type 'تراجع' or click [❌ Rollback].\r\n");
                    _transcript.AppendText(copyResponse + Environment.NewLine);
                    _transcript.SelectionStart = _transcript.TextLength;
                    _transcript.ScrollToCaret();
                    SetStatus(IsArabic ? "تم نسخ المخطط في طبقة المراجعة بالأحمر؛ بانتظار اعتمادك (تمام)." : "Copy drawn in red; awaiting approval.");
                    return;
                }

                if (_waitingForDrawingClarification && !IsPureQuestion(content))
                {
                    _message.Clear();
                    _transcript.AppendText("engineer: " + content + Environment.NewLine);
                    Autodesk.AutoCAD.ApplicationServices.Document doc =
                        Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    string desc;
                    string err;
                    bool isClarification;
                    bool drawn = DrawingExecutionCommands.ExecuteNaturalLanguageDrawing(doc, content, out desc, out isClarification, out err);
                    if (isClarification)
                    {
                        _transcript.AppendText("copilot: " + desc + Environment.NewLine + Environment.NewLine);
                        _transcript.SelectionStart = _transcript.TextLength;
                        _transcript.ScrollToCaret();
                        SetStatus(IsArabic ? "بانتظار توضيحك..." : "Waiting for clarification...");
                        return;
                    }
                    _waitingForDrawingClarification = false;
                    if (!drawn)
                    {
                        string failMsg = IsArabic
                            ? "copilot: تنبيه أثناء الرسم: " + (err ?? "تأكد من فتح ملف رسم نشط في أوتوكاد.") + Environment.NewLine + Environment.NewLine
                            : "copilot: Drawing notice: " + (err ?? "Ensure an active drawing is open.") + Environment.NewLine + Environment.NewLine;
                        _transcript.AppendText(failMsg);
                        _transcript.SelectionStart = _transcript.TextLength;
                        _transcript.ScrollToCaret();
                        SetStatus(err ?? "Drawing error.");
                        return;
                    }
                    string drawingResponse = "copilot: " + desc + "\r\n\r\n" +
                        (IsArabic
                            ? "📌 دورة المراجعة والاعتماد:\r\n" +
                              "• إذا أعجبك الرسم: اكتب «تمام» أو اضغط زر [✅ اعتماد الرسم (تمام)] لتثبيته في الطبقات الأصلية.\r\n" +
                              "• للتراجع والحذف: اكتب «تراجع» أو اضغط زر [❌ تراجع].\r\n"
                            : "Review workflow:\r\n• To commit: type 'تمام' or click [✅ Commit].\r\n• To rollback: type 'تراجع' or click [❌ Rollback].\r\n");
                    _transcript.AppendText(drawingResponse + Environment.NewLine);
                    _transcript.SelectionStart = _transcript.TextLength;
                    _transcript.ScrollToCaret();
                    SetStatus(IsArabic ? "تم رسم العنصر بالأحمر في أوتوكاد؛ بانتظار اعتمادك (تمام)." : "Preview drawn in red; awaiting approval.");
                    return;
                }

                string lower = content.Trim().ToLowerInvariant();

                if (lower.Contains("بنفسجي") || lower.Contains("ماجينتا") || lower.Contains("اللون البنفسجي") || lower.Contains("magenta"))
                {
                    _message.Clear();
                    _waitingForDrawingClarification = false;
                    _transcript.AppendText("engineer: " + content + Environment.NewLine);
                    string violetExplanation = IsArabic
                        ? "copilot: اللون البنفسجي (Magenta / كود 6 في أوتوكاد) في هذا المخطط يمثل طبقة التجهيزات والأجهزة المعمارية والصحية (A-FIXTURE):\r\n" +
                          "• في المطبخ: حوض المطبخ وكونتر العمل الرخامي (Kitchen Counter & Sink).\r\n" +
                          "• في الحمام: البانيو / الشاور (Bathtub)، حوض غسيل الأيدي والرخامة (Vanity Sink)، وقاعدة التواليت الدائرية (Water Closet).\r\n" +
                          "وقد تم تمييزها بهذا اللون طبقاً للمعايير الهندسية العالمية (AIA CAD Standards) لتمييز الأدوات الصحية عن الحوائط والأبواب والشبابيك."
                        : "copilot: The magenta/violet color (AutoCAD Color 6) represents architectural sanitary & kitchen fixtures (A-FIXTURE): counter/sink, bathtub, vanity sink, and water closet.";
                    _transcript.AppendText(violetExplanation + Environment.NewLine + Environment.NewLine);
                    _transcript.SelectionStart = _transcript.TextLength;
                    _transcript.ScrollToCaret();
                    SetStatus(IsArabic ? "اللون البنفسجي: أجهزة صحية ومطبخ (A-FIXTURE)." : "Magenta: Sanitary/Kitchen Fixtures.");
                    return;
                }

                if (IsDrawingRequest(content))
                {
                    _message.Clear();
                    _transcript.AppendText("engineer: " + content + Environment.NewLine);
                    Autodesk.AutoCAD.ApplicationServices.Document doc =
                        Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    string desc;
                    string err;
                    bool isClarification;
                    bool drawn = DrawingExecutionCommands.ExecuteNaturalLanguageDrawing(doc, content, out desc, out isClarification, out err);
                    if (isClarification)
                    {
                        _transcript.AppendText("copilot: " + desc + Environment.NewLine + Environment.NewLine);
                        _transcript.SelectionStart = _transcript.TextLength;
                        _transcript.ScrollToCaret();
                        SetStatus(IsArabic ? "بانتظار توضيحك..." : "Waiting for clarification...");
                        _waitingForDrawingClarification = true;
                        return;
                    }
                    _waitingForDrawingClarification = false;
                    if (!drawn)
                    {
                        string failMsg = IsArabic
                            ? "copilot: تنبيه أثناء الرسم: " + (err ?? "تأكد من فتح ملف رسم نشط في أوتوكاد.") + Environment.NewLine + Environment.NewLine
                            : "copilot: Drawing notice: " + (err ?? "Ensure an active drawing is open.") + Environment.NewLine + Environment.NewLine;
                        _transcript.AppendText(failMsg);
                        _transcript.SelectionStart = _transcript.TextLength;
                        _transcript.ScrollToCaret();
                        SetStatus(err ?? "Drawing error.");
                        return;
                    }
                    string drawingResponse = "copilot: " + desc + "\r\n\r\n" +
                        (IsArabic
                            ? "📌 دورة المراجعة والاعتماد:\r\n" +
                              "• إذا أعجبك الرسم: اكتب «تمام» أو اضغط زر [✅ اعتماد الرسم (تمام)] لتثبيته في الطبقات الأصلية.\r\n" +
                              "• للتراجع والحذف: اكتب «تراجع» أو اضغط زر [❌ تراجع].\r\n"
                            : "Review workflow:\r\n• To commit: type 'تمام' or click [✅ Commit].\r\n• To rollback: type 'تراجع' or click [❌ Rollback].\r\n");
                    _transcript.AppendText(drawingResponse + Environment.NewLine);
                    _transcript.SelectionStart = _transcript.TextLength;
                    _transcript.ScrollToCaret();
                    SetStatus(IsArabic ? "تم رسم العنصر بالأحمر في أوتوكاد؛ بانتظار اعتمادك (تمام)." : "Preview drawn in red; awaiting approval.");
                    return;
                }
                _waitingForDrawingClarification = false;
                Dictionary<string, object> providerStatus = await Get("/v1/copilot/provider-status", null);
                object configuredRaw;
                if (!providerStatus.TryGetValue("configured", out configuredRaw) ||
                    !(configuredRaw is bool) || !(bool)configuredRaw)
                    throw new InvalidOperationException(IsArabic ? "لا يوجد مزود AI مفعّل." : "No AI provider is configured.");
                string providerName = RequiredString(providerStatus, "provider_name");
                _lastMessage = content;
                _message.Clear();
                _transcript.AppendText("engineer: " + content + Environment.NewLine);
                _transcript.SelectionStart = _transcript.TextLength;
                _transcript.ScrollToCaret();
                Dictionary<string, object> request = new Dictionary<string, object>();
                request["language"] = IsArabic ? "ar" : "en";
                request["content"] = content;
                Dictionary<string, object> response = await Post(
                    "/v1/copilot/sessions/" + _sessionId + "/messages", request, _ownerKey);
                Dictionary<string, object> session = RequiredObject(response, "session");
                _hasSessionSummary = true;

                Autodesk.AutoCAD.ApplicationServices.Document activeDoc =
                    Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                string liveInspection = DrawingExecutionCommands.InspectActiveDrawing(activeDoc);
                string combinedSummary;
                if (!string.IsNullOrWhiteSpace(liveInspection))
                {
                    combinedSummary = _generalSession
                        ? ("mode=live_autocad_inspection;capabilities=inspect,copy,draw | " + liveInspection)
                        : ((_summaryText ?? "mode=drawing") + " | " + liveInspection);
                }
                else
                {
                    combinedSummary = (_summaryText ?? "mode=general;drawing_context=none");
                }

                ArrayList fields = new ArrayList();
                fields.Add(new Dictionary<string, object> {
                    {"field_name", "question.text"}, {"value", _lastMessage}
                });
                fields.Add(new Dictionary<string, object> {
                    {"field_name", "conversation.history"}, {"value", BoundedConversationHistory()}
                });
                fields.Add(new Dictionary<string, object> {
                    {"field_name", "context.summary"}, {"value", SafeText(combinedSummary, 4000)}
                });
                Dictionary<string, object> reviewRequest = new Dictionary<string, object>();
                reviewRequest["destination"] = providerName;
                reviewRequest["privacy"] = "project_confidential";
                reviewRequest["fields"] = fields;
                reviewRequest["ttl_minutes"] = 10;
                Dictionary<string, object> reviewResponse = await Post(
                    "/v1/copilot/sessions/" + _sessionId + "/egress-reviews", reviewRequest, _ownerKey);
                Dictionary<string, object> review = RequiredObject(reviewResponse, "review");
                _pendingReviewId = RequiredString(review, "review_id");
                Dictionary<string, object> decisionRequest = new Dictionary<string, object>();
                decisionRequest["approved"] = true;
                await Post(
                    "/v1/copilot/sessions/" + _sessionId + "/egress-reviews/" +
                    _pendingReviewId + "/decision", decisionRequest, _ownerKey);
                Dictionary<string, object> answerRequest = new Dictionary<string, object>();
                answerRequest["review_id"] = _pendingReviewId;
                Dictionary<string, object> answerResponse = await Post(
                    "/v1/copilot/sessions/" + _sessionId + "/answers", answerRequest, _ownerKey);
                AppendLatestAssistantMessage(RequiredObject(answerResponse, "session"));
                _pendingReviewId = null;
                SetStatus(IsArabic ? "تم استلام الإجابة." : "Answer received.");
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private static bool IsApprovalText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim().ToLowerInvariant();
            return t == "تمام" || t == "تم" || t == "اعتمد" || t == "اعتمدها" || t == "موافق" ||
                   t == "ثبت" || t == "تثبيت" || t == "احفظ" || t == "احفظها" || t == "حفظ" ||
                   t == "نعم" || t == "ممتاز" || t == "ok" || t == "approve" || t == "confirm" ||
                   t == "commit" || t.StartsWith("تمام") || t.StartsWith("اعتمد") || t.StartsWith("موافق");
        }

        private static bool IsRollbackText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim().ToLowerInvariant();
            return t == "تراجع" || t == "الغاء" || t == "إلغاء" || t == "ارفض" || t == "رفض" ||
                   t == "مش تمام" || t == "احذف" || t == "احذفها" || t == "حذف" || t == "امسح" ||
                   t == "امسحها" || t == "مسح" || t == "cancel" || t == "revert" || t == "rollback" ||
                   t == "reject" || t == "delete" || t.StartsWith("تراجع") || t.StartsWith("الغاء") ||
                   t.StartsWith("إلغاء") || t.Contains("مش تمام");
        }

        private static bool IsCopyDrawingRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim().ToLowerInvariant();
            if (t.StartsWith("كيف ") || t.StartsWith("ازاي ") || t.Contains("ما هو النسخ") || t.Contains("ما فائدة"))
                return false;
            bool hasCopyVerb = t.Contains("نسخ") || t.Contains("نسخة") || t.Contains("نسخه") ||
                               t.Contains("كرر") || t.Contains("تكرار") || t.Contains("دوبليكيت") ||
                               Regex.IsMatch(t, @"\b(copy|duplicate|clone)\b");
            if (!hasCopyVerb) return false;
            bool hasTarget = t.Contains("مخطط") || t.Contains("رسم") || t.Contains("لوح") ||
                             t.Contains("ملف") || t.Contains("مفتوح") || t.Contains("dwg") ||
                             t.Contains("هارد") || t.Contains("جنب") || t.Contains("بجانب") ||
                             t.Contains("كامل") || t.Contains("الشقة") || t.Contains("شقة") ||
                             Regex.IsMatch(t, @"(?:^|\s)(?:دا|ده|دي|هذا|هذه)(?:\s|$)") ||
                             Regex.IsMatch(t, @"\b(drawing|plan|file|this)\b");
            return hasTarget || t == "نسخ" || t == "انسخ" || t == "انسخه" || t == "احفظ نسخة";
        }

        private static bool IsPureQuestion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim().ToLowerInvariant();

            if (t.Contains("مش شايف") || t.Contains("لم يتم") || t.Contains("لم يرسم") ||
                t.Contains("ما في") || t.Contains("مفيش") || t.Contains("لا ارى") ||
                t.StartsWith("كيف ") || t.StartsWith("ازاي ") || t.Contains("ازاي اخليك") ||
                t.StartsWith("ليه ") || t.StartsWith("لماذا ") || t.StartsWith("فين ") ||
                t.StartsWith("اين ") || t.StartsWith("هل "))
                return true;

            bool hasExplicitDrawVerb = Regex.IsMatch(t,
                @"(?:^|\s)(?:ارسم|ترسم|نرسم|صمم|تصمم|وزع|توزع)(?:\s|$)|\b(?:draw|draft|design)\b");

            bool hasQuestionPhrase = t.StartsWith("ما ") ||
                t.Contains("عبارة عن") || t.Contains("عباره عن") ||
                t.Contains("تقولي") || t.Contains("تقول لي") || t.Contains("قولي") || t.Contains("قل لي") ||
                t.Contains("اخبرني") || t.Contains("أخبرني") ||
                t.Contains("ايه هو") || t.Contains("إيه هو") || t.Contains("ما هو") || t.Contains("ما هي") ||
                t.Contains("ايه دا") || t.Contains("ايه ده") || t.Contains("خاص بايه") || t.Contains("خاص بإيه") ||
                t.Contains("مخطط لايه") || t.Contains("مخطط لإيه") || t.Contains("لايه") || t.Contains("لإيه") ||
                t.Contains("ايه الفرق") || t.Contains("ما الفرق") ||
                t.Contains("اشرح") || t.Contains("تشرح") || t.Contains("اوصف") || t.Contains("صف لي") ||
                t.Contains("لخص") || t.Contains("توضح") || t.Contains("معلومات عن") || t.Contains("اشتراطات") ||
                t.Contains("كم عدد") || t.Contains("كم مساحة") || t.Contains("ما هي ابعاد") ||
                t.Contains("ما ابعاد") || t.Contains("محتويات") || t.Contains("يحتوي على") ||
                t.Contains("اسال عن") || t.Contains("اسأل عن") ||
                Regex.IsMatch(t, @"\b(what is|describe|explain|summarize|tell me|how many|why|where)\b");

            if (hasQuestionPhrase && !hasExplicitDrawVerb)
                return true;

            if (t.EndsWith("؟") || t.EndsWith("?"))
                return !hasExplicitDrawVerb;

            return false;
        }

        private static bool IsDrawingRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (IsPureQuestion(text)) return false;
            string t = text.Trim().ToLowerInvariant();
            bool hasDrawVerb = Regex.IsMatch(t,
                @"(?:^|\s)(?:ارسم|ترسم|نرسم|صمم|تصمم|وزع|توزع)(?:\s|$)|\b(?:draw|draft|design)\b") ||
                t.Contains("عايز ارسم") || t.Contains("محتاج ارسم") ||
                t.Contains("ارسم لي") || t.Contains("اعمل رسم") || t.Contains("رسم شقة") ||
                t.Contains("رسم شقه") || t.Contains("رسم مخطط") || t.Contains("صمم مخطط") ||
                t.Contains("want to draw");
            if (hasDrawVerb) return true;

            return t.Contains("دوائر") || t.Contains("دائرة") || t.Contains("دايرة") || t.Contains("دايره") ||
                   t.Contains("مستطيل") || t.Contains("مربع") || t.Contains("مثلث") ||
                   Regex.IsMatch(t, @"(?:^|\s)خط(?:\s|$)|\bline\b") ||
                   t.Contains("بلوك") || t.Contains("بلوكات") ||
                   t.Contains("لمبة") || t.Contains("لمبه") || t.Contains("كشاف") ||
                   t.Contains("فيش") || t.Contains("افياش") || t.Contains("أفياش") ||
                   t.Contains("بريزة") || t.Contains("برايز") || t.Contains("توزيع كهرب") ||
                   t.Contains("خزان") || t.Contains("قاعدة") || t.Contains("block ");
        }

        private async Task CreateSession()
        {
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["language"] = IsArabic ? "ar" : "en";
            request["ttl_minutes"] = 60;
            string path = "/v1/copilot/general-sessions";
            if (_includeDrawingContext.Checked)
            {
                ContextEvidence evidence = ContextEvidenceStore.Get();
                if (evidence == null) throw new InvalidOperationException(
                    IsArabic ? "لا يوجد سياق رسم؛ حدّد العناصر وشغّل AIENGCONTEXT أو ألغِ اختيار سياق الرسم."
                        : "No drawing context; select objects and run AIENGCONTEXT or turn off drawing context.");
                request["context_id"] = evidence.ContextId;
                request["evidence_sha256"] = evidence.Sha256;
                path = "/v1/copilot/sessions";
            }
            Dictionary<string, object> response = await Post(path, request, null);
            _ownerKey = RequiredString(response, "owner_key");
            Dictionary<string, object> session = RequiredObject(response, "session");
            _sessionId = RequiredString(session, "session_id");
            ShowSession(session);
            SetStatus(IsArabic ? "المحادثة جاهزة؛ اكتب بصورة طبيعية." : "Chat is ready; type naturally.");
        }

        private string BoundedConversationHistory()
        {
            string value = NormalizeEgressText(_transcript.Text);
            if (String.IsNullOrWhiteSpace(value))
                return IsArabic ? "لا يوجد سجل سابق." : "No previous conversation.";
            const int maximum = 32768;
            if (value.Length <= maximum) return value;
            return "[earlier messages omitted]\n" + value.Substring(value.Length - maximum);
        }

        private static string NormalizeEgressText(string value)
        {
            return String.IsNullOrEmpty(value)
                ? value
                : value.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private async Task PrepareReview()
        {
            if (!BeginOperation()) return;
            try
            {
                RequireSession();
                if (String.IsNullOrEmpty(_lastMessage))
                    throw new InvalidOperationException(IsArabic ? "أرسل رسالة محلية أولًا." : "Send a local message first.");
                Dictionary<string, object> status = await Get("/v1/copilot/provider-status", null);
                object configuredRaw;
                if (!status.TryGetValue("configured", out configuredRaw) || !(configuredRaw is bool) || !(bool)configuredRaw)
                    throw new InvalidOperationException(IsArabic ? "لا يوجد مزود AI مفعّل." : "No AI provider is configured.");
                string providerName = RequiredString(status, "provider_name");
                ArrayList fields = new ArrayList();
                fields.Add(new Dictionary<string, object> {
                    {"field_name", "question.text"}, {"value", _lastMessage}
                });
                fields.Add(new Dictionary<string, object> {
                    {"field_name", "context.summary"}, {"value", _summaryText ?? "summary unavailable"}
                });
                Dictionary<string, object> request = new Dictionary<string, object>();
                request["destination"] = providerName;
                request["privacy"] = "project_confidential";
                request["fields"] = fields;
                request["ttl_minutes"] = 10;
                Dictionary<string, object> response = await Post(
                    "/v1/copilot/sessions/" + _sessionId + "/egress-reviews", request, _ownerKey);
                Dictionary<string, object> review = RequiredObject(response, "review");
                _pendingReviewId = RequiredString(review, "review_id");
                ShowReview(review);
                SetStatus(IsArabic
                    ? "راجع القيم أعلاه ثم اختر موافقة أو رفض. لم يتم الإرسال."
                    : "Review the exact values above, then approve or reject. Nothing was sent.");
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private async Task DecideAndRequestAnswer(bool approved)
        {
            if (!BeginOperation()) return;
            try
            {
                RequireSession();
                if (String.IsNullOrEmpty(_pendingReviewId))
                    throw new InvalidOperationException(IsArabic ? "أنشئ معاينة إرسال أولًا." : "Create an egress preview first.");
                Dictionary<string, object> decision = new Dictionary<string, object>();
                decision["approved"] = approved;
                await Post(
                    "/v1/copilot/sessions/" + _sessionId + "/egress-reviews/" +
                    _pendingReviewId + "/decision", decision, _ownerKey);
                if (!approved)
                {
                    _pendingReviewId = null;
                    SetStatus(IsArabic ? "تم رفض إرسال البيانات." : "Data egress rejected.");
                    return;
                }
                Dictionary<string, object> answerRequest = new Dictionary<string, object>();
                answerRequest["review_id"] = _pendingReviewId;
                Dictionary<string, object> response = await Post(
                    "/v1/copilot/sessions/" + _sessionId + "/answers", answerRequest, _ownerKey);
                Dictionary<string, object> updatedSession = RequiredObject(response, "session");
                ShowSession(updatedSession);
                if (_voiceSpeakReply.Checked)
                {
                    string lastReply = ExtractLastAssistantMessage(updatedSession);
                    if (!string.IsNullOrWhiteSpace(lastReply))
                        VoiceSpeechOutput.SpeakAsync(lastReply);
                }
                _pendingReviewId = null;
                SetStatus(IsArabic ? "تم استلام إجابة مقيدة وموثقة." : "Bounded cited answer received.");
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private async Task SendDirectMessage()
        {
            string content = NormalizeEgressText(_message.Text);
            if (String.IsNullOrWhiteSpace(content))
            {
                SetStatus(IsArabic ? "اكتب رسالة أولًا." : "Enter a message first.");
                return;
            }
            if (!BeginOperation()) return;
            try
            {
                VoiceSpeechOutput.Stop();
                string provider = SelectedProvider;
                string model = SelectedModel;
                if (!string.IsNullOrWhiteSpace(model))
                {
                    _directConfig.SetModel(provider, model);
                    _directConfig.Save();
                }

                _message.Clear();
                _transcript.AppendText("engineer: " + content.Trim() + Environment.NewLine + Environment.NewLine);
                _transcript.SelectionStart = _transcript.TextLength;
                _transcript.ScrollToCaret();

                DirectAgentReply reply = await _directSession.SendAsync(
                    this, _directConfig, provider, model, content.Trim(), IsArabic, SetStatus);

                if (!string.IsNullOrEmpty(reply.FallbackNotice))
                {
                    _transcript.AppendText(
                        (IsArabic ? "ℹ️ [تنبيه/تحويل تلقائي]: " : "ℹ️ [Auto fallback]: ") +
                        reply.FallbackNotice + Environment.NewLine);
                }
                if (reply.ExecutedTools != null && reply.ExecutedTools.Count > 0)
                {
                    _transcript.AppendText(
                        (IsArabic ? "🛠️ [أدوات أوتوكاد]: " : "🛠️ [CAD tools]: ") +
                        string.Join(" | ", reply.ExecutedTools.ToArray()) + Environment.NewLine);
                }
                _transcript.AppendText(
                    reply.ProviderUsed + " (" + reply.ModelUsed + "): " + reply.Text + Environment.NewLine + Environment.NewLine);
                _transcript.SelectionStart = _transcript.TextLength;
                _transcript.ScrollToCaret();
                if (_voiceSpeakReply.Checked && !string.IsNullOrWhiteSpace(reply.Text))
                    VoiceSpeechOutput.SpeakAsync(reply.Text);
                RefreshContext();
                SetStatus(IsArabic
                    ? "تم الرد مباشرة من " + reply.ProviderUsed + " (" + reply.ModelUsed + ")."
                    : "Direct reply from " + reply.ProviderUsed + " (" + reply.ModelUsed + ").");
            }
            catch (System.Exception error)
            {
                _transcript.AppendText(
                    (IsArabic ? "⚠️ [خطأ API مباشر]: " : "⚠️ [Direct API Error]: ") +
                    error.Message + Environment.NewLine + Environment.NewLine);
                _transcript.SelectionStart = _transcript.TextLength;
                _transcript.ScrollToCaret();
                ShowError(error);
            }
            finally
            {
                EndOperation();
            }
        }

        private async Task CancelSession()
        {
            VoiceSpeechOutput.Stop();
            if (IsDirectMode)
            {
                _directSession.ClearHistory();
                _transcript.Clear();
                UpdateActionState();
                SetStatus(IsArabic ? "تم مسح سجل المحادثة المباشرة." : "Direct chat cleared.");
                return;
            }
            if (!BeginOperation()) return;
            try
            {
                RequireSession();
                Dictionary<string, object> response = await Post(
                    "/v1/copilot/sessions/" + _sessionId + "/cancel",
                    new Dictionary<string, object>(), _ownerKey);
                ShowSession(RequiredObject(response, "session"));
                _sessionId = null; _ownerKey = null;
                _pendingReviewId = null; _lastMessage = null;
                _hasSessionSummary = false;
                _generalSession = false;
                _summaryText = null;
                RefreshContext();
                SetStatus(IsArabic ? "تم إلغاء الجلسة." : "Session cancelled.");
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private async Task<Dictionary<string, object>> Post(
            string path, Dictionary<string, object> value, string ownerKey)
        {
            string token = Environment.GetEnvironmentVariable("AI_ENGINEER_API_TOKEN");
            if (String.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("AI_ENGINEER_API_TOKEN is not configured for AutoCAD.");
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (!String.IsNullOrEmpty(ownerKey))
                    request.Headers.Add("X-AI-Engineer-Session-Key", ownerKey);
                request.Content = new StringContent(
                    _serializer.Serialize(value), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string content = await response.Content.ReadAsStringAsync();
                    if (content.Length > 1048576)
                        throw new InvalidOperationException("Local API response exceeded its byte limit.");
                    if (!response.IsSuccessStatusCode)
                        throw LocalApiFailure(response, content);
                    object parsed = _serializer.DeserializeObject(content);
                    Dictionary<string, object> document = parsed as Dictionary<string, object>;
                    if (document == null) throw new InvalidOperationException("Local API response is invalid.");
                    return document;
                }
            }
        }

        private async Task<Dictionary<string, object>> Get(string path, string ownerKey)
        {
            string token = Environment.GetEnvironmentVariable("AI_ENGINEER_API_TOKEN");
            if (String.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("AI_ENGINEER_API_TOKEN is not configured for AutoCAD.");
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (!String.IsNullOrEmpty(ownerKey))
                    request.Headers.Add("X-AI-Engineer-Session-Key", ownerKey);
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string content = await response.Content.ReadAsStringAsync();
                    if (content.Length > 1048576)
                        throw new InvalidOperationException("Local API response exceeded its byte limit.");
                    if (!response.IsSuccessStatusCode)
                        throw LocalApiFailure(response, content);
                    Dictionary<string, object> document = _serializer.DeserializeObject(content) as Dictionary<string, object>;
                    if (document == null) throw new InvalidOperationException("Local API response is invalid.");
                    return document;
                }
            }
        }

        private void UpdateSessionSummaryState(Dictionary<string, object> session)
        {
            Dictionary<string, object> binding = RequiredObject(session, "context");
            _generalSession = RequiredString(binding, "scope") == "general";
            Dictionary<string, object> summary = RequiredObject(session, "context_summary");
            _deviceCount = RequiredInt(summary, "device_count");
            _routeCount = RequiredInt(summary, "route_count");
            _unknownCount = RequiredInt(summary, "unknown_count");
            _warningCount = RequiredInt(summary, "warning_count");
            _blockReferenceCount = RequiredInt(summary, "block_reference_count");
            _routePrimitiveCount = RequiredInt(summary, "route_primitive_count");
            _hasSessionSummary = true;
            ShowSummary();
        }

        private void AppendLatestAssistantMessage(Dictionary<string, object> session)
        {
            UpdateSessionSummaryState(session);
            string latestRole = "copilot";
            string latestContent = null;
            object raw;
            if (session.TryGetValue("messages", out raw))
            {
                IEnumerable messages = raw as IEnumerable;
                if (messages != null)
                {
                    foreach (object item in messages)
                    {
                        Dictionary<string, object> message = item as Dictionary<string, object>;
                        if (message == null) continue;
                        string role = RequiredString(message, "role");
                        string content = RequiredString(message, "content");
                        if (role == "copilot")
                        {
                            latestRole = role;
                            latestContent = content;
                        }
                    }
                }
            }
            if (!String.IsNullOrEmpty(latestContent))
            {
                _transcript.AppendText(latestRole + ": " + latestContent + Environment.NewLine + Environment.NewLine);
                _transcript.SelectionStart = _transcript.TextLength;
                _transcript.ScrollToCaret();
            }
        }

        private void ShowSession(Dictionary<string, object> session)
        {
            UpdateSessionSummaryState(session);
            StringBuilder transcript = new StringBuilder();
            object raw;
            if (session.TryGetValue("messages", out raw))
            {
                IEnumerable messages = raw as IEnumerable;
                if (messages != null)
                {
                    foreach (object item in messages)
                    {
                        Dictionary<string, object> message = item as Dictionary<string, object>;
                        if (message == null) continue;
                        transcript.Append(RequiredString(message, "role"));
                        transcript.Append(": ");
                        transcript.AppendLine(RequiredString(message, "content"));
                    }
                }
            }
            _transcript.Text = transcript.ToString();
            _transcript.SelectionStart = _transcript.TextLength;
            _transcript.ScrollToCaret();
        }

        private void ShowSummary()
        {
            if (_generalSession)
            {
                _context.Text = IsArabic
                    ? "محادثة عامة — بدون إرسال أو قراءة ملف الرسم"
                    : "General chat — no drawing file is read or sent";
                _uncertainty.Text = IsArabic
                    ? "طلبات تعديل الرسم ستحتاج سياقًا وخطة وموافقة منفصلة."
                    : "Drawing changes require separate context, preview, and approval.";
                _summaryText = "mode=general;drawing_context=none;drawing_write_allowed=false";
                return;
            }
            _context.Text = IsArabic
                ? "مصنف: أجهزة " + _deviceCount + " | مسارات " + _routeCount +
                    " | خام: بلوكات " + _blockReferenceCount + " | خطوط/مسارات " + _routePrimitiveCount
                : "Classified: devices " + _deviceCount + " | routes " + _routeCount +
                    " | Raw: blocks " + _blockReferenceCount + " | lines/routes " + _routePrimitiveCount;
            _uncertainty.Text = IsArabic
                ? "غير مصنف/ناقص: " + _unknownCount + " | تحذيرات: " + _warningCount
                : "Unknown/missing: " + _unknownCount + " | Warnings: " + _warningCount;
            _summaryText = "devices=" + _deviceCount.ToString(CultureInfo.InvariantCulture) +
                ";raw_blocks=" + _blockReferenceCount.ToString(CultureInfo.InvariantCulture) +
                ";raw_route_primitives=" + _routePrimitiveCount.ToString(CultureInfo.InvariantCulture) +
                ";routes=" + _routeCount.ToString(CultureInfo.InvariantCulture) +
                ";unknown=" + _unknownCount.ToString(CultureInfo.InvariantCulture) +
                ";warnings=" + _warningCount.ToString(CultureInfo.InvariantCulture);
        }

        private void ShowReview(Dictionary<string, object> review)
        {
            StringBuilder preview = new StringBuilder(_transcript.Text);
            preview.AppendLine(IsArabic ? "--- معاينة البيانات ---" : "--- Data egress preview ---");
            object raw;
            IEnumerable fields;
            if (!review.TryGetValue("fields", out raw) || (fields = raw as IEnumerable) == null)
                throw new InvalidOperationException("Local API response is missing review fields.");
            foreach (object item in fields)
            {
                Dictionary<string, object> field = item as Dictionary<string, object>;
                if (field == null) continue;
                preview.Append(RequiredString(field, "field_name"));
                preview.Append(" [");
                preview.Append(RequiredInt(field, "utf8_bytes").ToString(CultureInfo.InvariantCulture));
                preview.AppendLine(" bytes]");
                preview.AppendLine(RequiredString(field, "value"));
            }
            preview.AppendLine("SHA-256: " + RequiredString(review, "preview_sha256"));
            _transcript.Text = preview.ToString();
        }

        private bool BeginOperation()
        {
            if (_busy) return false;
            _busy = true;
            UpdateActionState();
            SetStatus(IsDirectMode
                ? (IsArabic ? "جارٍ التواصل المباشر مع الذكاء الاصطناعي..." : "Contacting AI API directly...")
                : (IsArabic ? "جارٍ التنفيذ محليًا..." : "Working locally..."));
            return true;
        }

        private void EndOperation()
        {
            _busy = false;
            UpdateActionState();
        }

        private void UpdateActionState()
        {
            bool active = !String.IsNullOrEmpty(_sessionId) && !String.IsNullOrEmpty(_ownerKey);
            bool voiceBlocked = _busy || _voiceTranscribing;
            _voiceMic.Enabled = !voiceBlocked || _voiceRecorder.IsRecording;
            _voiceAutoSend.Enabled = !voiceBlocked;
            _voiceSpeakReply.Enabled = true;

            if (IsDirectMode)
            {
                _start.Text = IsArabic ? "محادثة جديدة" : "New chat";
                _providerCombo.Enabled = !voiceBlocked;
                _modelCombo.Enabled = !voiceBlocked;
                _coordSortCombo.Enabled = !voiceBlocked;
                _apiSettings.Enabled = !voiceBlocked;
                if (voiceBlocked)
                {
                    _start.Enabled = _send.Enabled = _cancel.Enabled = false;
                    _prepareReview.Enabled = _approveReview.Enabled = _rejectReview.Enabled = false;
                    return;
                }
                _start.Enabled = true;
                _send.Enabled = true;
                _cancel.Enabled = _directSession.TurnCount > 0;
                _prepareReview.Enabled = _approveReview.Enabled = _rejectReview.Enabled = false;
                return;
            }
            _providerCombo.Enabled = !voiceBlocked;
            _apiSettings.Enabled = !voiceBlocked;
            _start.Text = IsArabic
                ? (active ? "المحادثة نشطة" : "محادثة جديدة")
                : (active ? "Chat active" : "New chat");
            if (voiceBlocked)
            {
                _start.Enabled = _send.Enabled = _cancel.Enabled = false;
                _prepareReview.Enabled = _approveReview.Enabled = _rejectReview.Enabled = false;
                return;
            }
            _start.Enabled = !active;
            _send.Enabled = active;
            _cancel.Enabled = active;
            _prepareReview.Enabled = active && !String.IsNullOrEmpty(_lastMessage);
            bool reviewPending = active && !String.IsNullOrEmpty(_pendingReviewId);
            _approveReview.Enabled = reviewPending;
            _rejectReview.Enabled = reviewPending;
        }

        private void UpdateVoiceMicVisuals()
        {
            if (_voiceTranscribing)
            {
                _voiceMic.Text = IsArabic ? "⏳ تحويل الصوت..." : "⏳ Transcribing...";
                _voiceMic.BackColor = Color.FromArgb(255, 243, 205);
                _voiceMic.FlatStyle = FlatStyle.Flat;
                return;
            }
            if (_voiceRecorder.IsRecording)
            {
                int sec = _voiceRecorder.ElapsedSeconds;
                _voiceMic.Text = (IsArabic ? "⏹ إيقاف (" : "⏹ Stop (") +
                                 sec.ToString(CultureInfo.InvariantCulture) + "s)";
                _voiceMic.BackColor = Color.FromArgb(255, 214, 214);
                _voiceMic.FlatStyle = FlatStyle.Flat;
                return;
            }
            _voiceMic.Text = IsArabic ? "🎤 صوت" : "🎤 Voice";
            _voiceMic.BackColor = SystemColors.Control;
            _voiceMic.FlatStyle = FlatStyle.Standard;
        }

        private async Task ToggleVoiceRecordingAsync()
        {
            if (_voiceTranscribing || _busy) return;

            if (!_voiceRecorder.IsRecording)
            {
                VoiceSpeechOutput.Stop();
                _directConfig = DirectAiConfig.Load();
                if (!VoiceTranscriber.HasCloudSttKey(_directConfig))
                {
                    string keyMsg = IsArabic
                        ? "للتواصل الصوتي الدقيق بجميع اللهجات العربية والإنجليزية، أضف مفتاح Gemini أو OpenAI من زر [🔑 API] (ويمكنك الإبقاء على أي مزود للشات)."
                        : "Configure a Gemini or OpenAI API key in [🔑 API] to enable dialect-accurate voice recognition.";
                    _transcript.AppendText((IsArabic ? "ℹ️ [الصوت]: " : "ℹ️ [Voice]: ") + keyMsg + Environment.NewLine + Environment.NewLine);
                    _transcript.SelectionStart = _transcript.TextLength;
                    _transcript.ScrollToCaret();
                    SetStatus(keyMsg);
                    return;
                }

                try
                {
                    _voiceRecorder.Start();
                    _voiceTimer.Start();
                    UpdateVoiceMicVisuals();
                    UpdateActionState();
                    SetStatus(IsArabic
                        ? "🎤 جاري التسجيل... تحدث بالعربية (أي لهجة) أو بالإنجليزية ثم اضغط إيقاف."
                        : "🎤 Recording... speak in Arabic (any dialect) or English, then click Stop.");
                }
                catch (System.Exception ex)
                {
                    _voiceTimer.Stop();
                    UpdateVoiceMicVisuals();
                    UpdateActionState();
                    SetStatus((IsArabic ? "تعذر بدء التسجيل: " : "Recording failed: ") + ex.Message);
                }
                return;
            }

            _voiceTimer.Stop();
            double durationSec;
            int peak;
            byte[] wav = _voiceRecorder.StopAndGetWavBytes(out durationSec, out peak);
            if (wav == null || wav.Length <= 44 || durationSec < 0.25)
            {
                UpdateVoiceMicVisuals();
                UpdateActionState();
                SetStatus(IsArabic
                    ? "التسجيل قصير جداً أو فارغ — اضغط 🎤 وتحدث ثم اضغط إيقاف."
                    : "Recording too short or empty — click 🎤, speak, then click Stop.");
                return;
            }
            if (peak < 80)
            {
                UpdateVoiceMicVisuals();
                UpdateActionState();
                SetStatus(IsArabic
                    ? "لم يُلتقط صوت واضح من الميكروفون (تأكد أن المايك غير مكتوم)."
                    : "No clear microphone audio detected (check mute/input device).");
                return;
            }

            _voiceTranscribing = true;
            UpdateVoiceMicVisuals();
            UpdateActionState();
            SetStatus(IsArabic ? "⏳ جاري تحويل الصوت إلى نص..." : "⏳ Transcribing speech...");

            string transcribed = "";
            try
            {
                DirectAiConfig cfgSnapshot = _directConfig ?? DirectAiConfig.Load();
                bool arabicUi = IsArabic;
                transcribed = await Task.Run(delegate
                {
                    return VoiceTranscriber.TranscribeWav(cfgSnapshot, wav, arabicUi);
                });
            }
            catch (System.Exception ex)
            {
                _transcript.AppendText(
                    (IsArabic ? "⚠️ [خطأ الصوت]: " : "⚠️ [Voice Error]: ") +
                    ex.Message + Environment.NewLine + Environment.NewLine);
                _transcript.SelectionStart = _transcript.TextLength;
                _transcript.ScrollToCaret();
                ShowError(ex);
                return;
            }
            finally
            {
                _voiceTranscribing = false;
                UpdateVoiceMicVisuals();
                UpdateActionState();
            }

            if (string.IsNullOrWhiteSpace(transcribed))
            {
                SetStatus(IsArabic ? "لم يتم التعرف على كلام واضح في التسجيل." : "No recognizable speech detected.");
                return;
            }

            string trimmed = transcribed.Trim();
            _message.Text = string.IsNullOrWhiteSpace(_message.Text)
                ? trimmed
                : (_message.Text.Trim() + " " + trimmed);

            if (_voiceAutoSend.Checked)
            {
                await SendMessage();
            }
            else
            {
                _message.Focus();
                _message.SelectionStart = _message.TextLength;
                _message.SelectionLength = 0;
                SetStatus(IsArabic
                    ? "✅ تم تحويل الصوت إلى نص — راجعه ثم اضغط [اسأل AI] أو Enter."
                    : "✅ Transcribed — review and press [Ask AI] or Enter.");
            }
        }

        private static string ExtractLastAssistantMessage(Dictionary<string, object> session)
        {
            if (session == null) return "";
            object raw;
            if (!session.TryGetValue("messages", out raw)) return "";
            IEnumerable messages = raw as IEnumerable;
            if (messages == null) return "";
            string last = "";
            foreach (object item in messages)
            {
                Dictionary<string, object> msg = item as Dictionary<string, object>;
                if (msg == null) continue;
                object roleObj;
                object contentObj;
                if (msg.TryGetValue("role", out roleObj) && roleObj != null &&
                    string.Equals(roleObj.ToString(), "assistant", StringComparison.OrdinalIgnoreCase) &&
                    msg.TryGetValue("content", out contentObj) && contentObj != null)
                {
                    last = contentObj.ToString();
                }
            }
            return last;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _voiceTimer.Stop(); _voiceTimer.Dispose(); } catch { }
                try { _voiceRecorder.Dispose(); } catch { }
                try { VoiceSpeechOutput.Stop(); } catch { }
            }
            base.Dispose(disposing);
        }

        private void RequireSession()
        {
            if (String.IsNullOrEmpty(_sessionId) || String.IsNullOrEmpty(_ownerKey))
                throw new InvalidOperationException(IsArabic ? "ابدأ جلسة أولًا." : "Start a session first.");
        }

        private void ShowError(System.Exception error)
        {
            string message = error is InvalidOperationException ? error.Message : error.GetType().Name;
            SetStatus((IsArabic ? "تعذر الطلب: " : "Request failed: ") + SafeText(message, 256));
        }

        private void SetStatus(string value) { _status.Text = value; }

        private static Dictionary<string, object> RequiredObject(Dictionary<string, object> value, string name)
        {
            object raw;
            Dictionary<string, object> result;
            if (!value.TryGetValue(name, out raw) || (result = raw as Dictionary<string, object>) == null)
                throw new InvalidOperationException("Local API response is missing " + name + ".");
            return result;
        }

        private static string RequiredString(Dictionary<string, object> value, string name)
        {
            object raw; string result;
            if (!value.TryGetValue(name, out raw) || String.IsNullOrEmpty(result = raw as string) || result.Length > 16384)
                throw new InvalidOperationException("Local API response has invalid " + name + ".");
            return result;
        }

        private static int RequiredInt(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw))
                throw new InvalidOperationException("Local API response is missing " + name + ".");
            int result = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            if (result < 0 || result > 1000000)
                throw new InvalidOperationException("Local API response has invalid " + name + ".");
            return result;
        }

        private InvalidOperationException LocalApiFailure(
            HttpResponseMessage response, string content)
        {
            string prefix = "Local API returned HTTP " +
                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".";
            try
            {
                Dictionary<string, object> document =
                    _serializer.DeserializeObject(content) as Dictionary<string, object>;
                object raw;
                string detail;
                if (document != null && document.TryGetValue("detail", out raw) &&
                    !String.IsNullOrWhiteSpace(detail = raw as string))
                    return new InvalidOperationException(prefix + " " + SafeText(detail, 384));
            }
            catch { }
            return new InvalidOperationException(prefix);
        }

        private static string SafeText(string value, int maximum)
        {
            if (String.IsNullOrEmpty(value)) return "error";
            StringBuilder result = new StringBuilder();
            foreach (char character in value)
            {
                if (result.Length >= maximum) break;
                result.Append(Char.IsControl(character) ? ' ' : character);
            }
            return result.ToString();
        }
    }

    internal sealed class DraftingPreviewPaletteControl : UserControl
    {
        private const int MaximumJsonBytes = 1048576;
        private readonly ComboBox _language = new ComboBox();
        private readonly Label _context = new Label();
        private readonly Label _proposal = new Label();
        private readonly TextBox _preview = new TextBox();
        private readonly Button _refresh = new Button();
        private readonly Button _select = new Button();
        private readonly Button _create = new Button();
        private readonly Button _reviewExecution = new Button();
        private readonly Button _approveExecution = new Button();
        private readonly Button _rejectExecution = new Button();
        private readonly Button _execute = new Button();
        private readonly Button _clear = new Button();
        private readonly Label _status = new Label();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly HttpClient _client;
        private Dictionary<string, object> _proposalValue;
        private string _runId;
        private string _ownerKey;
        private string _sourcePath;
        private string _sourceSha256;
        private string _executionRequestSha256;
        private bool _executionApproved;
        private bool _busy;

        public DraftingPreviewPaletteControl()
        {
            _serializer.MaxJsonLength = MaximumJsonBytes;
            _client = CreateClient();
            BuildLayout();
            ApplyLanguage();
            RefreshContext();
        }

        private static HttpClient CreateClient()
        {
            int port = 8765;
            string configured = Environment.GetEnvironmentVariable("AI_ENGINEER_API_PORT");
            int parsed;
            if (!String.IsNullOrWhiteSpace(configured) &&
                Int32.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
                parsed >= 1024 && parsed <= 65535)
                port = parsed;
            HttpClientHandler handler = new HttpClientHandler();
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            client.Timeout = TimeSpan.FromSeconds(15);
            return client;
        }

        private void BuildLayout()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 7;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _language.DropDownStyle = ComboBoxStyle.DropDownList;
            _language.Items.Add("العربية");
            _language.Items.Add("English");
            _language.SelectedIndex = 0;
            _language.SelectedIndexChanged += delegate { ApplyLanguage(); };
            _context.AutoSize = true;
            _context.MaximumSize = new Size(320, 0);
            _proposal.AutoSize = true;
            _proposal.MaximumSize = new Size(320, 0);
            _preview.Dock = DockStyle.Fill;
            _preview.Multiline = true;
            _preview.ReadOnly = true;
            _preview.ScrollBars = ScrollBars.Both;
            _preview.WordWrap = false;
            _status.AutoSize = true;
            _status.MaximumSize = new Size(320, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.WrapContents = true;
            buttons.MaximumSize = new Size(320, 0);
            _refresh.AutoSize = _select.AutoSize = _create.AutoSize = _clear.AutoSize = true;
            _reviewExecution.AutoSize = _approveExecution.AutoSize =
                _rejectExecution.AutoSize = _execute.AutoSize = true;
            buttons.Controls.Add(_refresh);
            buttons.Controls.Add(_select);
            buttons.Controls.Add(_create);
            buttons.Controls.Add(_reviewExecution);
            buttons.Controls.Add(_approveExecution);
            buttons.Controls.Add(_rejectExecution);
            buttons.Controls.Add(_execute);
            buttons.Controls.Add(_clear);
            layout.Controls.Add(_language, 0, 0);
            layout.Controls.Add(_context, 0, 1);
            layout.Controls.Add(_proposal, 0, 2);
            layout.Controls.Add(buttons, 0, 3);
            layout.Controls.Add(_preview, 0, 4);
            layout.Controls.Add(_status, 0, 5);
            Controls.Add(layout);

            _refresh.Click += delegate { RefreshContext(); };
            _select.Click += delegate { SelectProposal(); };
            _create.Click += async delegate { await CreatePreview(); };
            _reviewExecution.Click += async delegate { await ReviewExecution(); };
            _approveExecution.Click += async delegate { await DecideExecution(true); };
            _rejectExecution.Click += async delegate { await DecideExecution(false); };
            _execute.Click += async delegate { await ExecuteReviewedPlan(); };
            _clear.Click += delegate { ClearPreview(); };
        }

        private bool IsArabic { get { return _language.SelectedIndex == 0; } }

        private void ApplyLanguage()
        {
            RightToLeft rtl = IsArabic ? RightToLeft.Yes : RightToLeft.No;
            if (RightToLeft != rtl) RightToLeft = rtl;
            _refresh.Text = IsArabic ? "تحديث السياق" : "Refresh context";
            _select.Text = IsArabic ? "اختيار المقترح" : "Select proposal";
            _create.Text = IsArabic ? "إنشاء المعاينة" : "Create preview";
            _reviewExecution.Text = IsArabic
                ? "\u0645\u0631\u0627\u062c\u0639\u0629 \u0627\u0644\u062a\u0646\u0641\u064a\u0630"
                : "Review execution";
            _approveExecution.Text = IsArabic
                ? "\u0645\u0648\u0627\u0641\u0642\u0629" : "Approve";
            _rejectExecution.Text = IsArabic ? "\u0631\u0641\u0636" : "Reject";
            _execute.Text = IsArabic ? "\u062a\u0646\u0641\u064a\u0630" : "Execute";
            _clear.Text = IsArabic ? "مسح" : "Clear";
            RefreshContext();
            ShowProposalLabel();
        }

        private void RefreshContext()
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null)
            {
                _context.Text = IsArabic
                    ? "لا يوجد تحديد صريح للمعاينة."
                    : "No explicit selection is available for preview.";
                _create.Enabled = false;
                return;
            }
            _context.Text = (IsArabic ? "السياق: " : "Context: ") + evidence.SourceMode +
                " | " + evidence.EntityCount.ToString(CultureInfo.InvariantCulture) +
                (IsArabic ? " عنصر" : " entities") + " | DBMOD=" +
                evidence.DatabaseModified.ToString(CultureInfo.InvariantCulture);
            _create.Enabled = _proposalValue != null &&
                !HasPersistentDatabaseChanges(evidence.DatabaseModified);
            UpdateExecutionButtons();
        }

        private void SelectProposal()
        {
            if (_busy) return;
            try
            {
                string workspaceRoot = LoadWorkspaceRoot();
                using (System.Windows.Forms.OpenFileDialog dialog =
                    new System.Windows.Forms.OpenFileDialog())
                {
                    dialog.Filter = "JSON (*.json)|*.json";
                    dialog.InitialDirectory = workspaceRoot;
                    dialog.CheckFileExists = true;
                    dialog.Multiselect = false;
                    if (dialog.ShowDialog() != DialogResult.OK) return;
                    string path = Path.GetFullPath(dialog.FileName);
                    EnsureContained(path, workspaceRoot);
                    FileInfo file = new FileInfo(path);
                    if (file.Length <= 0 || file.Length > MaximumJsonBytes)
                        throw new InvalidOperationException("Drafting proposal file is empty or exceeds its byte limit.");
                    Dictionary<string, object> value = _serializer.DeserializeObject(
                        File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                    if (value == null)
                        throw new InvalidOperationException("Drafting proposal JSON is invalid.");
                    ValidateProposalBinding(value);
                    _proposalValue = value;
                    ResetRunState();
                    _preview.Clear();
                    ShowProposalLabel();
                    RefreshContext();
                    SetStatus(IsArabic ? "تم تحميل المقترح محليًا." : "Proposal loaded locally.");
                }
            }
            catch (System.Exception error) { ShowError(error); }
        }

        private async Task CreatePreview()
        {
            if (!BeginOperation()) return;
            try
            {
                if (_proposalValue == null)
                    throw new InvalidOperationException("Select a reviewed drafting proposal first.");
                ValidateProposalBinding(_proposalValue);
                Dictionary<string, object> createRequest = new Dictionary<string, object>();
                createRequest["proposal"] = _proposalValue;
                createRequest["ttl_minutes"] = 60;
                Dictionary<string, object> created = await Post(
                    "/v1/drafting/runs", createRequest, null);
                string ownerKey = RequiredString(created, "owner_key");
                Dictionary<string, object> run = RequiredObject(created, "run");
                string runId = RequiredString(run, "run_id");
                Dictionary<string, object> previewRequest = new Dictionary<string, object>();
                previewRequest["proposal"] = _proposalValue;
                previewRequest["ttl_minutes"] = 30;
                Dictionary<string, object> response = await Post(
                    "/v1/drafting/runs/" + runId + "/preview", previewRequest, ownerKey);
                Dictionary<string, object> preview = RequiredObject(response, "preview");
                ValidatePreview(preview);
                _runId = runId;
                _ownerKey = ownerKey;
                _executionApproved = false;
                ShowPreview(preview);
                UpdateExecutionButtons();
                SetStatus(IsArabic
                    ? "معاينة للقراءة فقط؛ لم يتغير الرسم ولم يتم اعتماد التنفيذ."
                    : "Read-only preview; no drawing mutation or execution approval occurred.");
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private async Task ReviewExecution()
        {
            if (!BeginOperation()) return;
            try
            {
                if (_proposalValue == null || String.IsNullOrEmpty(_runId) ||
                    String.IsNullOrEmpty(_ownerKey))
                    throw new InvalidOperationException("Create and inspect a preview first.");
                string workspaceRoot = LoadWorkspaceRoot();
                using (System.Windows.Forms.OpenFileDialog dialog =
                    new System.Windows.Forms.OpenFileDialog())
                {
                    dialog.Filter = "AutoCAD drawing (*.dwg;*.dwt)|*.dwg;*.dwt";
                    dialog.InitialDirectory = workspaceRoot;
                    dialog.CheckFileExists = true;
                    dialog.Multiselect = false;
                    if (dialog.ShowDialog() != DialogResult.OK) return;
                    _sourcePath = Path.GetFullPath(dialog.FileName);
                    EnsureContained(_sourcePath, workspaceRoot);
                    _sourceSha256 = CalculateSha256(_sourcePath);
                }
                Dictionary<string, object> response = await Post(
                    "/v1/drafting/runs/" + _runId + "/execution-review",
                    BuildExecutionRequest(), _ownerKey);
                Dictionary<string, object> run = RequiredObject(response, "run");
                Dictionary<string, object> execution = RequiredObject(response, "execution");
                if (!String.Equals(RequiredString(run, "status"),
                    "execution_review_pending", StringComparison.Ordinal))
                    throw new InvalidOperationException("Execution review did not become pending.");
                if (RequiredBool(execution, "source_will_be_modified") ||
                    !RequiredBool(execution, "scratch_copy_will_be_modified") ||
                    !RequiredBool(execution, "handoff_requires_separate_approval"))
                    throw new InvalidOperationException("Execution review returned unsafe boundaries.");
                _executionRequestSha256 = RequiredString(
                    execution, "execution_request_sha256");
                _executionApproved = false;
                _preview.AppendText(Environment.NewLine + Environment.NewLine +
                    "Execution review SHA-256: " + _executionRequestSha256 +
                    Environment.NewLine + "Source: " + _sourcePath +
                    Environment.NewLine + "Source SHA-256: " + _sourceSha256 +
                    Environment.NewLine +
                    "Source modification: false; scratch modification: true" +
                    Environment.NewLine);
                SetStatus(IsArabic
                    ? "\u0645\u0631\u0627\u062c\u0639\u0629 \u0627\u0644\u062a\u0646\u0641\u064a\u0630 \u062c\u0627\u0647\u0632\u0629\u061b \u0644\u0645 \u064a\u062a\u0645 \u0627\u0644\u062a\u0646\u0641\u064a\u0630."
                    : "Execution review is pending; nothing was executed.");
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private async Task DecideExecution(bool approved)
        {
            if (!BeginOperation()) return;
            try
            {
                if (String.IsNullOrEmpty(_executionRequestSha256))
                    throw new InvalidOperationException("Review the exact execution first.");
                if (approved)
                {
                    DialogResult choice = MessageBox.Show(
                        IsArabic
                            ? "\u0627\u0639\u062a\u0645\u0627\u062f \u0647\u0630\u0647 \u0627\u0644\u062e\u0637\u0629 \u0644\u062a\u0646\u0641\u064a\u0630 \u0645\u0631\u0629 \u0648\u0627\u062d\u062f\u0629 \u0639\u0644\u0649 \u0646\u0633\u062e\u0629 scratch\u061f"
                            : "Approve this exact plan for one scratch execution?",
                        "AI-Engineer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (choice != DialogResult.Yes) return;
                }
                Dictionary<string, object> decision = new Dictionary<string, object>();
                decision["approved"] = approved;
                Dictionary<string, object> response = await Post(
                    "/v1/drafting/runs/" + _runId +
                    "/execution-review/decision", decision, _ownerKey);
                Dictionary<string, object> run = RequiredObject(response, "run");
                string expected = approved ? "approved" : "rejected";
                if (!String.Equals(RequiredString(run, "status"), expected,
                    StringComparison.Ordinal))
                    throw new InvalidOperationException("Execution decision was not persisted.");
                _executionApproved = approved;
                if (!approved) _executionRequestSha256 = null;
                SetStatus(approved
                    ? (IsArabic
                        ? "\u062a\u0645\u062a \u0627\u0644\u0645\u0648\u0627\u0641\u0642\u0629\u061b \u0644\u0645 \u064a\u062a\u0645 \u0627\u0644\u062a\u0646\u0641\u064a\u0630 \u0628\u0639\u062f."
                        : "Approved; execution has not started.")
                    : (IsArabic ? "\u062a\u0645 \u0627\u0644\u0631\u0641\u0636."
                        : "Execution rejected."));
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private async Task ExecuteReviewedPlan()
        {
            if (!BeginOperation()) return;
            try
            {
                if (!_executionApproved)
                    throw new InvalidOperationException("Execution is not approved.");
                DialogResult choice = MessageBox.Show(
                    IsArabic
                        ? "\u0625\u0646\u0634\u0627\u0621 \u0646\u0633\u062e\u0629 scratch \u0627\u0644\u0622\u0646\u061f \u0644\u0646 \u064a\u062a\u0645 \u0627\u0633\u062a\u0628\u062f\u0627\u0644 \u0627\u0644\u0645\u0644\u0641 \u0627\u0644\u0623\u0635\u0644\u064a."
                        : "Create the reviewed scratch output now? The source will not be replaced.",
                    "AI-Engineer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (choice != DialogResult.Yes) return;
                Dictionary<string, object> response = await Post(
                    "/v1/drafting/runs/" + _runId + "/execute",
                    BuildExecutionRequest(), _ownerKey);
                Dictionary<string, object> run = RequiredObject(response, "run");
                Dictionary<string, object> recovery = RequiredObject(response, "recovery");
                if (!String.Equals(RequiredString(run, "status"), "executed",
                    StringComparison.Ordinal) || !RequiredBool(run, "drawing_mutation_performed"))
                    throw new InvalidOperationException("Execution verification is incomplete.");
                _executionApproved = false;
                _executionRequestSha256 = null;
                _preview.AppendText(Environment.NewLine +
                    "EXECUTED ON SCRATCH COPY" + Environment.NewLine +
                    "Output SHA-256: " + RequiredString(run, "output_sha256") +
                    Environment.NewLine + "Recovery evidence SHA-256: " +
                    RequiredString(recovery, "evidence_sha256") + Environment.NewLine +
                    "Original source replacement: false" + Environment.NewLine);
                SetStatus(IsArabic
                    ? "\u0646\u062c\u062d \u0627\u0644\u062a\u0646\u0641\u064a\u0630 \u0639\u0644\u0649 \u0646\u0633\u062e\u0629 scratch\u061b \u0627\u0644\u0645\u0635\u062f\u0631 \u0644\u0645 \u064a\u062a\u063a\u064a\u0631."
                    : "Verified scratch execution completed; source unchanged.");
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private Dictionary<string, object> BuildExecutionRequest()
        {
            if (_proposalValue == null || String.IsNullOrEmpty(_sourcePath) ||
                String.IsNullOrEmpty(_sourceSha256))
                throw new InvalidOperationException("Execution source is not selected.");
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["proposal"] = _proposalValue;
            request["source_path"] = _sourcePath;
            request["source_sha256"] = _sourceSha256;
            return request;
        }

        private void ValidateProposalBinding(Dictionary<string, object> proposal)
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null)
                throw new InvalidOperationException("Export an explicit selection first.");
            if (HasPersistentDatabaseChanges(evidence.DatabaseModified))
                throw new InvalidOperationException(
                    "Drafting preview rejects object, database-variable, or field changes in DBMOD.");
            Dictionary<string, object> binding = RequiredObject(proposal, "binding");
            if (!String.Equals(RequiredString(binding, "context_id"), evidence.ContextId, StringComparison.Ordinal) ||
                !String.Equals(RequiredString(binding, "context_evidence_sha256"), evidence.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Drafting proposal does not match the current context evidence.");
        }

        private void ValidatePreview(Dictionary<string, object> preview)
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            Dictionary<string, object> binding = RequiredObject(preview, "binding");
            if (evidence == null ||
                !String.Equals(RequiredString(binding, "context_id"), evidence.ContextId, StringComparison.Ordinal) ||
                !String.Equals(RequiredString(binding, "context_evidence_sha256"), evidence.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Drafting preview is not bound to the current context.");
            if (RequiredBool(preview, "drawing_mutation_performed") ||
                RequiredBool(preview, "execution_approved"))
                throw new InvalidOperationException("Drafting preview returned an unsafe state.");
        }

        private void ShowPreview(Dictionary<string, object> preview)
        {
            Dictionary<string, object> proposal = RequiredObject(preview, "proposal");
            object rawItems;
            IEnumerable items;
            if (!preview.TryGetValue("items", out rawItems) || (items = rawItems as IEnumerable) == null)
                throw new InvalidOperationException("Drafting preview is missing ordered items.");
            StringBuilder text = new StringBuilder();
            text.AppendLine("Preview SHA-256: " + RequiredString(preview, "preview_sha256"));
            text.AppendLine("Expires: " + RequiredString(preview, "expires_at"));
            text.AppendLine("drawing_mutation_performed=false");
            text.AppendLine("execution_approved=false");
            text.AppendLine();
            int index = 0;
            foreach (object raw in items)
            {
                Dictionary<string, object> item = raw as Dictionary<string, object>;
                if (item == null) throw new InvalidOperationException("Drafting preview item is invalid.");
                string kind = RequiredString(item, "kind");
                string objectId = RequiredString(item, "object_id");
                Dictionary<string, object> geometry = FindProposalObject(proposal, kind, objectId);
                index++;
                text.Append(index.ToString(CultureInfo.InvariantCulture));
                text.Append(". ");
                text.Append(RequiredString(item, "action").ToUpperInvariant());
                text.Append(" ");
                text.Append(kind);
                text.Append(" ");
                text.AppendLine(objectId);
                text.AppendLine(_serializer.Serialize(geometry));
            }
            _preview.Text = text.ToString();
        }

        private static Dictionary<string, object> FindProposalObject(
            Dictionary<string, object> proposal, string kind, string objectId)
        {
            string collection;
            string idField;
            switch (kind)
            {
                case "layer": collection = "layers"; idField = "layer_id"; break;
                case "device": collection = "devices"; idField = "device_id"; break;
                case "terminal": collection = "terminals"; idField = "terminal_id"; break;
                case "circuit": collection = "circuits"; idField = "circuit_id"; break;
                case "route": collection = "routes"; idField = "route_id"; break;
                default: throw new InvalidOperationException("Drafting preview item kind is unsupported.");
            }
            object raw;
            IEnumerable values;
            if (!proposal.TryGetValue(collection, out raw) || (values = raw as IEnumerable) == null)
                throw new InvalidOperationException("Drafting proposal is missing " + collection + ".");
            foreach (object value in values)
            {
                Dictionary<string, object> candidate = value as Dictionary<string, object>;
                object identifier;
                if (candidate != null && candidate.TryGetValue(idField, out identifier) &&
                    String.Equals(identifier as string, objectId, StringComparison.Ordinal))
                    return candidate;
            }
            throw new InvalidOperationException("Drafting preview object is missing from its exact proposal.");
        }

        private async Task<Dictionary<string, object>> Post(
            string path, Dictionary<string, object> value, string ownerKey)
        {
            string token = Environment.GetEnvironmentVariable("AI_ENGINEER_API_TOKEN");
            if (String.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("AI_ENGINEER_API_TOKEN is not configured for AutoCAD.");
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (!String.IsNullOrEmpty(ownerKey))
                    request.Headers.Add("X-AI-Engineer-Drafting-Key", ownerKey);
                request.Content = new StringContent(
                    _serializer.Serialize(value), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string content = await response.Content.ReadAsStringAsync();
                    if (Encoding.UTF8.GetByteCount(content) > MaximumJsonBytes)
                        throw new InvalidOperationException("Local API response exceeded its byte limit.");
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Local API returned HTTP " +
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) +
                            ApiDetail(content) + ".");
                    Dictionary<string, object> document =
                        _serializer.DeserializeObject(content) as Dictionary<string, object>;
                    if (document == null)
                        throw new InvalidOperationException("Local API response is invalid.");
                    return document;
                }
            }
        }

        private string ApiDetail(string content)
        {
            try
            {
                Dictionary<string, object> document =
                    _serializer.DeserializeObject(content) as Dictionary<string, object>;
                object detail;
                if (document != null && document.TryGetValue("detail", out detail) && detail is string)
                    return ": " + SafeText((string)detail, 180);
            }
            catch { }
            return String.Empty;
        }

        private bool BeginOperation()
        {
            if (_busy) return false;
            _busy = true;
            _refresh.Enabled = _select.Enabled = _create.Enabled = _clear.Enabled = false;
            _reviewExecution.Enabled = _approveExecution.Enabled =
                _rejectExecution.Enabled = _execute.Enabled = false;
            SetStatus(IsArabic ? "جارٍ إنشاء المعاينة محليًا..." : "Creating preview locally...");
            return true;
        }

        private void EndOperation()
        {
            _busy = false;
            _refresh.Enabled = _select.Enabled = _clear.Enabled = true;
            RefreshContext();
        }

        private void ClearPreview()
        {
            if (_busy) return;
            _proposalValue = null;
            ResetRunState();
            _preview.Clear();
            ShowProposalLabel();
            RefreshContext();
            SetStatus(IsArabic ? "تم مسح المعاينة المحلية." : "Local preview cleared.");
        }

        private void ShowProposalLabel()
        {
            _proposal.Text = _proposalValue == null
                ? (IsArabic ? "لا يوجد مقترح محدد." : "No proposal selected.")
                : (IsArabic ? "المقترح: " : "Proposal: ") +
                    RequiredString(_proposalValue, "proposal_id");
        }

        private void ResetRunState()
        {
            _runId = null;
            _ownerKey = null;
            _sourcePath = null;
            _sourceSha256 = null;
            _executionRequestSha256 = null;
            _executionApproved = false;
            UpdateExecutionButtons();
        }

        private void UpdateExecutionButtons()
        {
            if (_busy)
            {
                _reviewExecution.Enabled = _approveExecution.Enabled =
                    _rejectExecution.Enabled = _execute.Enabled = false;
                return;
            }
            _reviewExecution.Enabled = !String.IsNullOrEmpty(_runId);
            bool pending = !String.IsNullOrEmpty(_executionRequestSha256) &&
                !_executionApproved;
            _approveExecution.Enabled = pending;
            _rejectExecution.Enabled = pending;
            _execute.Enabled = _executionApproved;
        }

        private void ShowError(System.Exception error)
        {
            string message = error is InvalidOperationException ? error.Message : error.GetType().Name;
            SetStatus((IsArabic ? "تعذر الطلب: " : "Request failed: ") + SafeText(message, 256));
        }

        private void SetStatus(string value) { _status.Text = value; }

        private static string LoadWorkspaceRoot()
        {
            string assembly = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string settingsPath = Path.GetFullPath(Path.Combine(assembly, "settings.json"));
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> settings = serializer.DeserializeObject(
                File.ReadAllText(settingsPath, Encoding.UTF8)) as Dictionary<string, object>;
            string root = RequiredString(settings, "workspace_root");
            if (!Path.IsPathRooted(root))
                throw new InvalidOperationException("workspace_root must be absolute.");
            return Path.GetFullPath(root);
        }

        private static void EnsureContained(string path, string root)
        {
            string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string canonicalPath = Path.GetFullPath(path);
            if (!canonicalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Drafting proposal must be inside the configured workspace root.");
        }

        private static Dictionary<string, object> RequiredObject(
            Dictionary<string, object> value, string name)
        {
            object raw;
            Dictionary<string, object> result;
            if (value == null || !value.TryGetValue(name, out raw) ||
                (result = raw as Dictionary<string, object>) == null)
                throw new InvalidOperationException("Local data is missing " + name + ".");
            return result;
        }

        private static string RequiredString(Dictionary<string, object> value, string name)
        {
            object raw;
            string result;
            if (value == null || !value.TryGetValue(name, out raw) ||
                String.IsNullOrEmpty(result = raw as string) || result.Length > 16384)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static bool RequiredBool(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw) || !(raw is bool))
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return (bool)raw;
        }

        private static bool HasPersistentDatabaseChanges(int databaseModified)
        {
            const int objectDatabase = 1;
            const int databaseVariable = 4;
            const int field = 32;
            return (databaseModified & (objectDatabase | databaseVariable | field)) != 0;
        }

        private static string CalculateSha256(string path)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > 268435456)
                throw new InvalidOperationException(
                    "Execution source is empty or exceeds its byte limit.");
            using (FileStream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (System.Security.Cryptography.SHA256 sha =
                System.Security.Cryptography.SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream))
                    .Replace("-", String.Empty).ToLowerInvariant();
            }
        }

        private static string SafeText(string value, int maximum)
        {
            if (String.IsNullOrEmpty(value)) return "error";
            StringBuilder result = new StringBuilder();
            foreach (char character in value)
            {
                if (result.Length >= maximum) break;
                result.Append(Char.IsControl(character) ? ' ' : character);
            }
            return result.ToString();
        }
    }

    internal sealed class QuantityTakeoffPaletteControl : UserControl
    {
        private const int MaximumJsonBytes = 1048576;
        private readonly ComboBox _language = new ComboBox();
        private readonly Label _context = new Label();
        private readonly NumericUpDown _vertical = new NumericUpDown();
        private readonly NumericUpDown _termination = new NumericUpDown();
        private readonly NumericUpDown _waste = new NumericUpDown();
        private readonly Button _refresh = new Button();
        private readonly Button _create = new Button();
        private readonly Button _export = new Button();
        private readonly Button _clear = new Button();
        private readonly TextBox _output = new TextBox();
        private readonly Label _status = new Label();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly HttpClient _client;
        private Dictionary<string, object> _lastRequest;
        private string _reportSha256;
        private bool _busy;

        public QuantityTakeoffPaletteControl()
        {
            _serializer.MaxJsonLength = MaximumJsonBytes;
            _client = CreateClient();
            BuildLayout();
            ApplyLanguage();
            RefreshContext();
        }

        private static HttpClient CreateClient()
        {
            int port = 8765;
            string configured = Environment.GetEnvironmentVariable("AI_ENGINEER_API_PORT");
            int parsed;
            if (!String.IsNullOrWhiteSpace(configured) &&
                Int32.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
                parsed >= 1024 && parsed <= 65535)
                port = parsed;
            HttpClientHandler handler = new HttpClientHandler();
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            client.Timeout = TimeSpan.FromSeconds(15);
            return client;
        }

        private void BuildLayout()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 7;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _language.DropDownStyle = ComboBoxStyle.DropDownList;
            _language.Items.Add("Arabic");
            _language.Items.Add("English");
            _language.SelectedIndex = 0;
            _language.SelectedIndexChanged += delegate { ApplyLanguage(); };
            _context.AutoSize = true;
            _context.MaximumSize = new Size(320, 0);

            TableLayoutPanel policy = new TableLayoutPanel();
            policy.AutoSize = true;
            policy.ColumnCount = 2;
            policy.RowCount = 3;
            ConfigureNumber(_vertical, 1000000, 3);
            ConfigureNumber(_termination, 1000000, 3);
            ConfigureNumber(_waste, 100, 2);
            policy.Controls.Add(new Label { Text = "Vertical allowance (m)", AutoSize = true }, 0, 0);
            policy.Controls.Add(_vertical, 1, 0);
            policy.Controls.Add(new Label { Text = "Termination / end (m)", AutoSize = true }, 0, 1);
            policy.Controls.Add(_termination, 1, 1);
            policy.Controls.Add(new Label { Text = "Waste (%)", AutoSize = true }, 0, 2);
            policy.Controls.Add(_waste, 1, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.WrapContents = true;
            buttons.MaximumSize = new Size(320, 0);
            _refresh.AutoSize = _create.AutoSize = _export.AutoSize = _clear.AutoSize = true;
            buttons.Controls.Add(_refresh);
            buttons.Controls.Add(_create);
            buttons.Controls.Add(_export);
            buttons.Controls.Add(_clear);

            _output.Dock = DockStyle.Fill;
            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Both;
            _output.WordWrap = false;
            _status.AutoSize = true;
            _status.MaximumSize = new Size(320, 0);
            layout.Controls.Add(_language, 0, 0);
            layout.Controls.Add(_context, 0, 1);
            layout.Controls.Add(policy, 0, 2);
            layout.Controls.Add(buttons, 0, 3);
            layout.Controls.Add(_output, 0, 4);
            layout.Controls.Add(_status, 0, 5);
            Controls.Add(layout);

            _refresh.Click += delegate { RefreshContext(); };
            _create.Click += async delegate { await CreateReport(); };
            _export.Click += async delegate { await CreateCsv(); };
            _clear.Click += delegate { Clear(); };
        }

        private static void ConfigureNumber(NumericUpDown control, decimal maximum, int decimals)
        {
            control.Minimum = 0;
            control.Maximum = maximum;
            control.DecimalPlaces = decimals;
            control.Width = 100;
        }

        private bool IsArabic { get { return _language.SelectedIndex == 0; } }

        private void ApplyLanguage()
        {
            RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
            _refresh.Text = IsArabic ? "\u062a\u062d\u062f\u064a\u062b \u0627\u0644\u0633\u064a\u0627\u0642" : "Refresh context";
            _create.Text = IsArabic ? "\u0625\u0646\u0634\u0627\u0621 \u0627\u0644\u062d\u0635\u0631" : "Create takeoff";
            _export.Text = IsArabic ? "\u0625\u0646\u0634\u0627\u0621 CSV \u0627\u0644\u0645\u064f\u0631\u0627\u062c\u0639" : "Create reviewed CSV";
            _clear.Text = IsArabic ? "\u0645\u0633\u062d" : "Clear";
            RefreshContext();
        }

        private void RefreshContext()
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null)
            {
                _context.Text = IsArabic
                    ? "\u0644\u0627 \u064a\u0648\u062c\u062f \u0633\u064a\u0627\u0642 AutoCAD \u0635\u0631\u064a\u062d."
                    : "No explicit AutoCAD context is available.";
                _create.Enabled = false;
            }
            else
            {
                _context.Text = (IsArabic ? "\u0627\u0644\u0633\u064a\u0627\u0642: " : "Context: ") +
                    evidence.SourceMode + " | " + evidence.EntityCount.ToString(CultureInfo.InvariantCulture) +
                    (IsArabic ? " \u0639\u0646\u0635\u0631" : " entities") + " | DBMOD=" +
                    evidence.DatabaseModified.ToString(CultureInfo.InvariantCulture);
                _create.Enabled = !_busy;
            }
            _export.Enabled = !_busy && !String.IsNullOrEmpty(_reportSha256);
        }

        private async Task CreateReport()
        {
            if (!BeginOperation()) return;
            try
            {
                Dictionary<string, object> request = BuildRequest();
                Dictionary<string, object> response = await Post("/v1/takeoff/reports", request);
                Dictionary<string, object> report = RequiredObject(response, "report");
                ValidateReport(report);
                _lastRequest = request;
                _reportSha256 = RequiredString(report, "report_sha256");
                ShowReport(report);
                SetStatus(IsArabic
                    ? "\u062a\u0645 \u0625\u0646\u0634\u0627\u0621 \u0627\u0644\u062d\u0635\u0631 \u0644\u0644\u0645\u0631\u0627\u062c\u0639\u0629\u061b \u0644\u0645 \u064a\u062a\u063a\u064a\u0631 \u0627\u0644\u0631\u0633\u0645."
                    : "Takeoff created for review; drawing unchanged.");
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private async Task CreateCsv()
        {
            if (!BeginOperation()) return;
            try
            {
                if (_lastRequest == null || String.IsNullOrEmpty(_reportSha256))
                    throw new InvalidOperationException("Create and inspect a takeoff report first.");
                DialogResult choice = MessageBox.Show(
                    (IsArabic
                        ? "\u0625\u0646\u0634\u0627\u0621 CSV \u0644\u0644\u062a\u0642\u0631\u064a\u0631 \u0627\u0644\u0645\u064f\u0631\u0627\u062c\u0639 \u0630\u064a \u0627\u0644\u0628\u0635\u0645\u0629:\n"
                        : "Create CSV for the reviewed report SHA-256:\n") + _reportSha256 +
                    (IsArabic ? "\n\u0644\u0646 \u064a\u062a\u0645 \u062a\u0639\u062f\u064a\u0644 \u0627\u0644\u0631\u0633\u0645 \u0623\u0648 \u0643\u062a\u0627\u0628\u0629 \u0645\u0644\u0641."
                        : "\nNo drawing mutation or file write will occur."),
                    "AI-Engineer", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (choice != DialogResult.Yes) return;
                Dictionary<string, object> request = CloneRequest(_lastRequest);
                request["reviewed_report_sha256"] = _reportSha256;
                Dictionary<string, object> response = await Post("/v1/takeoff/exports/csv", request);
                Dictionary<string, object> exported = RequiredObject(response, "export");
                if (!String.Equals(RequiredString(exported, "report_sha256"), _reportSha256,
                    StringComparison.Ordinal) || RequiredBool(exported, "drawing_mutation_performed") ||
                    RequiredBool(exported, "file_write_performed"))
                    throw new InvalidOperationException("Reviewed CSV returned unsafe or mismatched evidence.");
                string content = RequiredString(exported, "content");
                if (Encoding.UTF8.GetByteCount(content) > MaximumJsonBytes)
                    throw new InvalidOperationException("Reviewed CSV exceeded its byte limit.");
                _output.AppendText(Environment.NewLine + Environment.NewLine +
                    "CSV SHA-256: " + RequiredString(exported, "content_sha256") +
                    Environment.NewLine + "file_write_performed=false" + Environment.NewLine +
                    content.TrimStart('\ufeff'));
                SetStatus(IsArabic
                    ? "\u062a\u0645 \u0625\u0646\u0634\u0627\u0621 CSV \u0627\u0644\u0645\u0637\u0627\u0628\u0642\u061b \u0644\u0645 \u064a\u062a\u0645 \u0643\u062a\u0627\u0628\u0629 \u0645\u0644\u0641."
                    : "Matching CSV created; no file was written.");
            }
            catch (System.Exception error) { ShowError(error); }
            finally { EndOperation(); }
        }

        private Dictionary<string, object> BuildRequest()
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null)
                throw new InvalidOperationException("Export an explicit AutoCAD context first.");
            Dictionary<string, object> policy = new Dictionary<string, object>();
            policy["calculation_version"] = "m19.takeoff.v1";
            policy["vertical_allowance_per_route_meters"] = Decimal.ToDouble(_vertical.Value);
            policy["termination_allowance_per_end_meters"] = Decimal.ToDouble(_termination.Value);
            policy["waste_percent"] = Decimal.ToDouble(_waste.Value);
            policy["route_overrides"] = new object[0];
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["context_id"] = evidence.ContextId;
            request["context_evidence_sha256"] = evidence.Sha256;
            request["policy"] = policy;
            return request;
        }

        private static Dictionary<string, object> CloneRequest(Dictionary<string, object> source)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> pair in source) result[pair.Key] = pair.Value;
            return result;
        }

        private void ValidateReport(Dictionary<string, object> report)
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null ||
                !String.Equals(RequiredString(report, "context_id"), evidence.ContextId, StringComparison.Ordinal) ||
                !String.Equals(RequiredString(report, "context_evidence_sha256"), evidence.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Quantity report is not bound to the current context.");
            if (RequiredBool(report, "drawing_mutation_performed"))
                throw new InvalidOperationException("Quantity report returned an unsafe drawing state.");
            RequiredString(report, "report_sha256");
        }

        private void ShowReport(Dictionary<string, object> report)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Report SHA-256: " + RequiredString(report, "report_sha256"));
            text.AppendLine("Calculation: " + RequiredString(report, "calculation_version"));
            text.AppendLine("drawing_mutation_performed=false");
            text.AppendLine("is_complete=" + RequiredBool(report, "is_complete").ToString().ToLowerInvariant());
            text.AppendLine();
            foreach (Dictionary<string, object> item in RequiredObjects(report, "device_quantities"))
                text.AppendLine("DEVICE " + RequiredString(item, "symbol_id") + " = " +
                    RequiredInt(item, "quantity").ToString(CultureInfo.InvariantCulture) + " each");
            foreach (Dictionary<string, object> item in RequiredObjects(report, "route_quantities"))
                text.AppendLine("ROUTE " + RequiredString(item, "route_id") + " = " +
                    RequiredDouble(item, "final_total_meters").ToString("0.###", CultureInfo.InvariantCulture) + " m");
            int unknown = RequiredObjects(report, "unknown_object_ids", true).Count;
            if (unknown > 0)
                text.AppendLine("UNKNOWN = " + unknown.ToString(CultureInfo.InvariantCulture));
            _output.Text = text.ToString();
        }

        private async Task<Dictionary<string, object>> Post(
            string path, Dictionary<string, object> value)
        {
            string token = Environment.GetEnvironmentVariable("AI_ENGINEER_API_TOKEN");
            if (String.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("AI_ENGINEER_API_TOKEN is not configured for AutoCAD.");
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(
                    _serializer.Serialize(value), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string content = await response.Content.ReadAsStringAsync();
                    if (Encoding.UTF8.GetByteCount(content) > MaximumJsonBytes)
                        throw new InvalidOperationException("Local API response exceeded its byte limit.");
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Local API returned HTTP " +
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                    Dictionary<string, object> document =
                        _serializer.DeserializeObject(content) as Dictionary<string, object>;
                    if (document == null)
                        throw new InvalidOperationException("Local API response is invalid.");
                    return document;
                }
            }
        }

        private bool BeginOperation()
        {
            if (_busy) return false;
            _busy = true;
            _refresh.Enabled = _create.Enabled = _export.Enabled = _clear.Enabled = false;
            SetStatus(IsArabic ? "\u062c\u0627\u0631\u064d \u0627\u0644\u062d\u0633\u0627\u0628 \u0645\u062d\u0644\u064a\u064b\u0627..." : "Calculating locally...");
            return true;
        }

        private void EndOperation()
        {
            _busy = false;
            _refresh.Enabled = _clear.Enabled = true;
            RefreshContext();
        }

        private void Clear()
        {
            if (_busy) return;
            _lastRequest = null;
            _reportSha256 = null;
            _output.Clear();
            RefreshContext();
            SetStatus(IsArabic ? "\u062a\u0645 \u0627\u0644\u0645\u0633\u062d." : "Cleared.");
        }

        private void ShowError(System.Exception error)
        {
            string message = error is InvalidOperationException ? error.Message : error.GetType().Name;
            SetStatus((IsArabic ? "\u062a\u0639\u0630\u0631 \u0627\u0644\u0637\u0644\u0628: " : "Request failed: ") +
                SafeText(message, 256));
        }

        private void SetStatus(string value) { _status.Text = value; }

        private static Dictionary<string, object> RequiredObject(
            Dictionary<string, object> value, string name)
        {
            object raw;
            Dictionary<string, object> result;
            if (value == null || !value.TryGetValue(name, out raw) ||
                (result = raw as Dictionary<string, object>) == null)
                throw new InvalidOperationException("Local data is missing " + name + ".");
            return result;
        }

        private static string RequiredString(Dictionary<string, object> value, string name)
        {
            object raw;
            string result;
            if (value == null || !value.TryGetValue(name, out raw) ||
                String.IsNullOrEmpty(result = raw as string) || result.Length > MaximumJsonBytes)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static bool RequiredBool(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw) || !(raw is bool))
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return (bool)raw;
        }

        private static int RequiredInt(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw))
                throw new InvalidOperationException("Local data is missing " + name + ".");
            int result = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            if (result < 0 || result > 1000000)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static double RequiredDouble(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw))
                throw new InvalidOperationException("Local data is missing " + name + ".");
            double result = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (Double.IsNaN(result) || Double.IsInfinity(result) || result < 0 || result > 1000000000)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static List<Dictionary<string, object>> RequiredObjects(
            Dictionary<string, object> value, string name, bool identifiersOnly = false)
        {
            object raw;
            IEnumerable items;
            if (!value.TryGetValue(name, out raw) || (items = raw as IEnumerable) == null || raw is string)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (object item in items)
            {
                if (identifiersOnly)
                {
                    if (!(item is string))
                        throw new InvalidOperationException("Local data has invalid " + name + ".");
                    result.Add(new Dictionary<string, object>());
                }
                else
                {
                    Dictionary<string, object> document = item as Dictionary<string, object>;
                    if (document == null)
                        throw new InvalidOperationException("Local data has invalid " + name + ".");
                    result.Add(document);
                }
                if (result.Count > 8192)
                    throw new InvalidOperationException("Local data exceeded the item limit.");
            }
            return result;
        }

        private static string SafeText(string value, int maximum)
        {
            if (String.IsNullOrEmpty(value)) return "error";
            StringBuilder result = new StringBuilder();
            foreach (char character in value)
            {
                if (result.Length >= maximum) break;
                result.Append(Char.IsControl(character) ? ' ' : character);
            }
            return result.ToString();
        }
    }

    internal sealed class CompliancePaletteControl : UserControl
    {
        private const int MaximumJsonBytes = 1048576;
        private readonly Button _select = new Button();
        private readonly Button _evaluate = new Button();
        private readonly Button _clear = new Button();
        private readonly Label _file = new Label();
        private readonly Label _status = new Label();
        private readonly TextBox _output = new TextBox();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly HttpClient _client;
        private Dictionary<string, object> _request;
        private bool _busy;

        public CompliancePaletteControl()
        {
            _serializer.MaxJsonLength = MaximumJsonBytes;
            _client = CreateClient();
            BuildLayout();
            string preloaded = Environment.GetEnvironmentVariable(
                "AI_ENGINEER_COMPLIANCE_REQUEST");
            if (!String.IsNullOrWhiteSpace(preloaded))
            {
                try
                {
                    LoadRequest(preloaded);
                    Load += async delegate { await Evaluate(); };
                }
                catch (System.Exception error)
                {
                    _status.Text = "Preloaded review failed: " + error.Message;
                }
            }
        }

        private static HttpClient CreateClient()
        {
            int port = 8765;
            string configured = Environment.GetEnvironmentVariable("AI_ENGINEER_API_PORT");
            int parsed;
            if (!String.IsNullOrWhiteSpace(configured) &&
                Int32.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
                parsed >= 1024 && parsed <= 65535)
                port = parsed;
            HttpClientHandler handler = new HttpClientHandler();
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            client.Timeout = TimeSpan.FromSeconds(15);
            return client;
        }

        private void BuildLayout()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label warning = new Label();
            warning.AutoSize = true;
            warning.MaximumSize = new Size(320, 0);
            warning.ForeColor = Color.DarkOrange;
            warning.Text = "Read-only review. No provider authority, regulatory approval, or drawing mutation.";
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            _select.Text = "Select reviewed JSON";
            _evaluate.Text = "Evaluate locally";
            _clear.Text = "Clear";
            buttons.Controls.Add(_select);
            buttons.Controls.Add(_evaluate);
            buttons.Controls.Add(_clear);
            _file.AutoSize = true;
            _file.MaximumSize = new Size(320, 0);
            _file.Text = "No reviewed compliance request selected.";
            _output.Dock = DockStyle.Fill;
            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Both;
            _output.WordWrap = false;
            _status.AutoSize = true;
            _status.MaximumSize = new Size(320, 0);
            layout.Controls.Add(warning, 0, 0);
            layout.Controls.Add(buttons, 0, 1);
            layout.Controls.Add(_file, 0, 2);
            layout.Controls.Add(_output, 0, 3);
            layout.Controls.Add(_status, 0, 4);
            Controls.Add(layout);
            _select.Click += delegate { SelectRequest(); };
            _evaluate.Click += async delegate { await Evaluate(); };
            _clear.Click += delegate { if (!_busy) { _output.Clear(); _status.Text = "Cleared."; } };
            UpdateButtons();
        }

        private void SelectRequest()
        {
            if (_busy) return;
            using (System.Windows.Forms.OpenFileDialog dialog =
                new System.Windows.Forms.OpenFileDialog())
            {
                dialog.Title = "Select reviewed AI-Engineer compliance request";
                dialog.Filter = "JSON files (*.json)|*.json";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (dialog.ShowDialog() != DialogResult.OK) return;
                LoadRequest(dialog.FileName);
            }
        }

        private void LoadRequest(string path)
        {
            if (!Path.IsPathRooted(path))
                throw new InvalidOperationException("Reviewed JSON path must be absolute.");
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length < 2 || info.Length > MaximumJsonBytes)
                throw new InvalidOperationException("Reviewed JSON file is missing, empty, or exceeds 1 MiB.");
            string content = File.ReadAllText(info.FullName, Encoding.UTF8);
            Dictionary<string, object> parsed =
                _serializer.DeserializeObject(content) as Dictionary<string, object>;
            if (parsed == null)
                throw new InvalidOperationException("Reviewed JSON request is invalid.");
            _request = parsed;
            _file.Text = "Reviewed request: " + info.Name;
            _status.Text = "Request loaded locally; review then evaluate.";
            UpdateButtons();
        }

        private async Task Evaluate()
        {
            if (_busy || _request == null) return;
            _busy = true;
            UpdateButtons();
            _status.Text = "Evaluating exact reviewed evidence locally...";
            try
            {
                Dictionary<string, object> response = await Post("/v1/compliance/evaluate", _request);
                Dictionary<string, object> profile = RequiredObject(response, "profile");
                Dictionary<string, object> result = RequiredObject(response, "result");
                if (RequiredBool(result, "provider_retrieval_performed") ||
                    RequiredBool(result, "regulatory_approval_performed") ||
                    RequiredBool(result, "drawing_mutation_performed"))
                    throw new InvalidOperationException("Compliance response claimed an unsafe external action.");
                string profileHash = RequiredSha256(profile, "profile_sha256");
                if (!String.Equals(profileHash, RequiredSha256(result, "profile_sha256"), StringComparison.Ordinal))
                    throw new InvalidOperationException("Compliance result profile hash does not match.");
                StringBuilder text = new StringBuilder();
                text.AppendLine("Profile: " + RequiredString(profile, "profile_id"));
                text.AppendLine("Profile SHA-256: " + profileHash);
                text.AppendLine("Result SHA-256: " + RequiredSha256(result, "result_sha256"));
                text.AppendLine("Complete: " + RequiredBool(result, "complete").ToString().ToLowerInvariant());
                text.AppendLine("All requirements satisfied: " +
                    RequiredBool(result, "all_requirements_satisfied").ToString().ToLowerInvariant());
                text.AppendLine("provider_retrieval_performed=false");
                text.AppendLine("regulatory_approval_performed=false");
                text.AppendLine("drawing_mutation_performed=false");
                text.AppendLine();
                text.AppendLine("CITATIONS");
                foreach (Dictionary<string, object> clause in RequiredObjects(response, "clauses"))
                {
                    if (!String.Equals(profileHash, RequiredSha256(clause, "profile_sha256"), StringComparison.Ordinal))
                        throw new InvalidOperationException("Clause evidence profile hash does not match.");
                    text.AppendLine(RequiredString(clause, "authority_id") + " | " +
                        RequiredString(clause, "source_id") + " | edition " +
                        RequiredString(clause, "source_edition") + " | " +
                        RequiredString(clause, "clause_locator"));
                    text.AppendLine("  Source SHA-256: " + RequiredSha256(clause, "source_content_sha256"));
                    text.AppendLine("  Evidence SHA-256: " + RequiredSha256(clause, "evidence_sha256"));
                }
                text.AppendLine();
                text.AppendLine("FINDINGS (PASS/FAIL/UNKNOWN/CONFLICT)");
                foreach (Dictionary<string, object> finding in RequiredObjects(result, "findings"))
                    text.AppendLine(RequiredString(finding, "outcome").ToUpperInvariant() + " | " +
                        RequiredString(finding, "rule_id") + " | " +
                        RequiredString(finding, "fact_id") + " | " +
                        RequiredString(finding, "message"));
                _output.Text = text.ToString();
                _status.Text = "Reviewed locally; drawing unchanged.";
            }
            catch (System.Exception error)
            {
                _status.Text = "Evaluation failed: " + error.Message;
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }

        private async Task<Dictionary<string, object>> Post(string path, Dictionary<string, object> value)
        {
            string token = Environment.GetEnvironmentVariable("AI_ENGINEER_API_TOKEN");
            if (String.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("AI_ENGINEER_API_TOKEN is not configured for AutoCAD.");
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(_serializer.Serialize(value), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string content = await response.Content.ReadAsStringAsync();
                    if (Encoding.UTF8.GetByteCount(content) > MaximumJsonBytes)
                        throw new InvalidOperationException("Local API response exceeded 1 MiB.");
                    if (!response.IsSuccessStatusCode)
                    {
                        string detail = "";
                        try
                        {
                            Dictionary<string, object> error = _serializer.DeserializeObject(content) as Dictionary<string, object>;
                            object rawDetail;
                            if (error != null && error.TryGetValue("detail", out rawDetail) && rawDetail is string)
                                detail = ((string)rawDetail).Trim();
                        }
                        catch { detail = ""; }
                        if (detail.Length > 512) detail = detail.Substring(0, 512);
                        throw new InvalidOperationException("Local API returned HTTP " +
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) +
                            (detail.Length > 0 ? ": " + detail : "."));
                    }
                    Dictionary<string, object> document =
                        _serializer.DeserializeObject(content) as Dictionary<string, object>;
                    if (document == null) throw new InvalidOperationException("Local API response is invalid.");
                    return document;
                }
            }
        }

        private void UpdateButtons()
        {
            _select.Enabled = !_busy;
            _evaluate.Enabled = !_busy && _request != null;
            _clear.Enabled = !_busy;
        }

        private static Dictionary<string, object> RequiredObject(Dictionary<string, object> value, string name)
        {
            object raw;
            Dictionary<string, object> result;
            if (value == null || !value.TryGetValue(name, out raw) ||
                (result = raw as Dictionary<string, object>) == null)
                throw new InvalidOperationException("Local data is missing " + name + ".");
            return result;
        }

        private static List<Dictionary<string, object>> RequiredObjects(Dictionary<string, object> value, string name)
        {
            object raw;
            object[] items;
            if (!value.TryGetValue(name, out raw) || (items = raw as object[]) == null || items.Length > 512)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (object item in items)
            {
                Dictionary<string, object> record = item as Dictionary<string, object>;
                if (record == null) throw new InvalidOperationException("Local data has invalid " + name + ".");
                result.Add(record);
            }
            return result;
        }

        private static string RequiredString(Dictionary<string, object> value, string name)
        {
            object raw;
            string result;
            if (!value.TryGetValue(name, out raw) || String.IsNullOrWhiteSpace(result = raw as string) ||
                result.Length > 1024)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static string RequiredSha256(Dictionary<string, object> value, string name)
        {
            string result = RequiredString(value, name);
            if (result.Length != 64) throw new InvalidOperationException("Local data has invalid " + name + ".");
            for (int index = 0; index < result.Length; index++)
                if (!(result[index] >= '0' && result[index] <= '9') &&
                    !(result[index] >= 'a' && result[index] <= 'f'))
                    throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static bool RequiredBool(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw) || !(raw is bool))
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return (bool)raw;
        }
    }

    internal sealed class ManufacturerDiscoveryPaletteControl : UserControl
    {
        private const int MaximumJsonBytes = 1048576;
        private readonly Button _select = new Button();
        private readonly Button _discover = new Button();
        private readonly Button _verifyManual = new Button();
        private readonly Button _copy = new Button();
        private readonly Button _choose = new Button();
        private readonly ComboBox _candidates = new ComboBox();
        private readonly TextBox _rationale = new TextBox();
        private readonly Label _file = new Label();
        private readonly Label _status = new Label();
        private readonly TextBox _output = new TextBox();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly HttpClient _client;
        private Dictionary<string, object> _request;
        private Dictionary<string, object> _result;
        private Dictionary<string, object> _manualDraft;
        private bool _busy;

        public ManufacturerDiscoveryPaletteControl()
        {
            _serializer.MaxJsonLength = MaximumJsonBytes;
            _client = CreateClient();
            BuildLayout();
            string preload = Environment.GetEnvironmentVariable("AI_ENGINEER_MANUFACTURER_DISCOVERY_REQUEST");
            if (!String.IsNullOrWhiteSpace(preload))
            {
                try { LoadRequest(preload); }
                catch (System.Exception error) { _status.Text = "Preload failed: " + error.Message; }
            }
        }

        private static HttpClient CreateClient()
        {
            int port = 8765;
            string configured = Environment.GetEnvironmentVariable("AI_ENGINEER_API_PORT");
            int parsed;
            if (!String.IsNullOrWhiteSpace(configured) &&
                Int32.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
                parsed >= 1024 && parsed <= 65535) port = parsed;
            HttpClientHandler handler = new HttpClientHandler();
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            // Quality web search can consume the server's bounded 180-second window.
            client.Timeout = TimeSpan.FromSeconds(210);
            return client;
        }

        private void BuildLayout()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowCount = 6;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label warning = new Label();
            warning.AutoSize = true;
            warning.MaximumSize = new Size(320, 0);
            warning.ForeColor = Color.DarkOrange;
            warning.Text = "Choose free official-page verification or paid AI search. Results are candidates only: no selection, download, catalog, or drawing action.";
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Top;
            buttons.WrapContents = true;
            _select.Text = "Select approved request";
            _discover.Text = "Search official sources";
            _verifyManual.Text = "Verify free candidate file";
            _copy.Text = "Copy complete result";
            _choose.Text = "Choose for separate review";
            _select.AutoSize = true;
            _discover.AutoSize = true;
            _verifyManual.AutoSize = true;
            _copy.AutoSize = true;
            _choose.AutoSize = true;
            buttons.Controls.Add(_select);
            buttons.Controls.Add(_verifyManual);
            buttons.Controls.Add(_discover);
            buttons.Controls.Add(_copy);
            _file.AutoSize = true;
            _file.MaximumSize = new Size(320, 0);
            _file.Text = "No approved project requirement request selected.";
            _output.Dock = DockStyle.Fill;
            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Vertical;
            _output.WordWrap = true;
            _output.MinimumSize = new Size(0, 160);
            TableLayoutPanel choice = new TableLayoutPanel();
            choice.AutoSize = true;
            choice.Dock = DockStyle.Top;
            choice.ColumnCount = 1;
            choice.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            choice.RowCount = 3;
            choice.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            choice.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            choice.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _candidates.DropDownStyle = ComboBoxStyle.DropDownList;
            _candidates.Dock = DockStyle.Top;
            _rationale.Dock = DockStyle.Top;
            _rationale.MaxLength = 512;
            _rationale.Text = "Continue to separate datasheet and product approval review.";
            _choose.Anchor = AnchorStyles.Left;
            choice.Controls.Add(_candidates, 0, 0);
            choice.Controls.Add(_rationale, 0, 1);
            choice.Controls.Add(_choose, 0, 2);
            _status.AutoSize = true;
            _status.MaximumSize = new Size(320, 0);
            layout.Controls.Add(warning, 0, 0);
            layout.Controls.Add(buttons, 0, 1);
            layout.Controls.Add(_file, 0, 2);
            layout.Controls.Add(_output, 0, 3);
            layout.Controls.Add(choice, 0, 4);
            layout.Controls.Add(_status, 0, 5);
            Controls.Add(layout);
            _select.Click += delegate { SelectRequest(); };
            _verifyManual.Click += async delegate { await VerifyManual(); };
            _discover.Click += async delegate { await Discover(); };
            _copy.Click += delegate { CopyOutput(); };
            _choose.Click += async delegate { await Choose(); };
            UpdateButtons();
        }

        private void CopyOutput()
        {
            if (_busy || String.IsNullOrWhiteSpace(_output.Text)) return;
            try
            {
                Clipboard.SetText(_output.Text);
                _status.Text = "Complete displayed result copied to the clipboard.";
            }
            catch (System.Exception error)
            {
                _status.Text = "Copy failed: " + error.Message;
            }
        }

        private async Task VerifyManual()
        {
            if (_busy || _request == null) return;
            using (System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog())
            {
                dialog.Title = "Select free official candidate draft";
                dialog.Filter = "JSON files (*.json)|*.json";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (dialog.ShowDialog() != DialogResult.OK) return;
                FileInfo info = new FileInfo(Path.GetFullPath(dialog.FileName));
                if (!info.Exists || info.Length < 2 || info.Length > MaximumJsonBytes)
                {
                    _status.Text = "Candidate draft is missing, empty, or exceeds 1 MiB.";
                    return;
                }
                _manualDraft = _serializer.DeserializeObject(
                    File.ReadAllText(info.FullName, Encoding.UTF8)) as Dictionary<string, object>;
                if (_manualDraft == null)
                {
                    _status.Text = "Candidate draft JSON is invalid.";
                    return;
                }
            }
            await Discover();
        }

        private void SelectRequest()
        {
            if (_busy) return;
            using (System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog())
            {
                dialog.Title = "Select approved manufacturer discovery request";
                dialog.Filter = "JSON files (*.json)|*.json";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (dialog.ShowDialog() != DialogResult.OK) return;
                LoadRequest(dialog.FileName);
            }
        }

        private void LoadRequest(string path)
        {
            FileInfo info = new FileInfo(Path.GetFullPath(path));
            if (!info.Exists || info.Length < 2 || info.Length > MaximumJsonBytes)
                throw new InvalidOperationException("Approved request is missing, empty, or exceeds 1 MiB.");
            Dictionary<string, object> parsed = _serializer.DeserializeObject(
                File.ReadAllText(info.FullName, Encoding.UTF8)) as Dictionary<string, object>;
            if (parsed == null) throw new InvalidOperationException("Approved request is invalid.");
            _request = parsed;
            _result = null;
            _candidates.Items.Clear();
            _file.Text = "Approved request: " + info.Name;
            _status.Text = "Review requirements and approved domains, then search.";
            UpdateButtons();
        }

        private async Task Discover()
        {
            if (_busy || _request == null) return;
            bool manualVerification = _manualDraft != null;
            _busy = true;
            UpdateButtons();
            _status.Text = manualVerification ?
                "Verifying candidate evidence on approved official pages..." :
                "Searching approved official domains with the paid provider...";
            try
            {
                _result = null;
                _candidates.Items.Clear();
                Dictionary<string, object> payload = new Dictionary<string, object>(_request);
                string endpoint = "/v1/manufacturers/discover";
                if (manualVerification)
                {
                    payload["candidate_draft"] = _manualDraft;
                    endpoint = "/v1/manufacturers/discover/manual";
                }
                Dictionary<string, object> response = await Post(endpoint, payload);
                Dictionary<string, object> request = RequiredObject(response, "request");
                Dictionary<string, object> result = RequiredObject(response, "result");
                if (RequiredBool(result, "provider_egress_performed") != !manualVerification ||
                    RequiredBool(result, "automatic_selection_performed") ||
                    RequiredBool(result, "automatic_download_performed") ||
                    RequiredBool(result, "catalog_registration_performed") ||
                    RequiredBool(result, "drawing_mutation_performed"))
                    throw new InvalidOperationException("Discovery response claimed an unauthorized action.");
                string requestHash = RequiredSha256(request, "request_sha256");
                if (!String.Equals(requestHash, RequiredSha256(result, "request_sha256"), StringComparison.Ordinal))
                    throw new InvalidOperationException("Discovery request hash does not match result.");
                StringBuilder text = new StringBuilder();
                text.AppendLine("Category: " + RequiredString(request, "product_category"));
                text.AppendLine("Request SHA-256: " + requestHash);
                text.AppendLine("Result SHA-256: " + RequiredSha256(result, "result_sha256"));
                text.AppendLine("Complete evidence: " + RequiredBool(result, "complete").ToString().ToLowerInvariant());
                text.AppendLine("Search path: " + (manualVerification ?
                    "free candidate evidence verification" : "paid AI official search"));
                text.AppendLine("Evidence mode: " + RequiredString(result, "provider_name"));
                text.AppendLine("provider_egress_performed=" +
                    RequiredBool(result, "provider_egress_performed").ToString().ToLowerInvariant());
                text.AppendLine("automatic_selection_performed=false");
                text.AppendLine("automatic_download_performed=false");
                text.AppendLine("catalog_registration_performed=false");
                text.AppendLine("drawing_mutation_performed=false");
                List<Dictionary<string, object>> candidates = RequiredObjects(result, "candidates", 32);
                List<Dictionary<string, object>> comparisons = RequiredObjects(result, "comparisons", 32);
                if (candidates.Count != comparisons.Count)
                    throw new InvalidOperationException("Candidate comparison count does not match.");
                for (int index = 0; index < candidates.Count; index++)
                {
                    Dictionary<string, object> candidate = candidates[index];
                    Dictionary<string, object> comparison = comparisons[index];
                    text.AppendLine();
                    text.AppendLine("CANDIDATE " + (index + 1).ToString(CultureInfo.InvariantCulture));
                    text.AppendLine(RequiredString(candidate, "brand_name") + " | " +
                        RequiredString(candidate, "model") + " | Part number " +
                        RequiredString(candidate, "part_number"));
                    text.AppendLine(RequiredString(candidate, "product_url"));
                    _candidates.Items.Add(RequiredString(candidate, "candidate_id") + " | " +
                        RequiredString(candidate, "part_number"));
                    text.AppendLine("Mandatory satisfied: " +
                        RequiredBool(comparison, "mandatory_requirements_satisfied").ToString().ToLowerInvariant());
                    foreach (Dictionary<string, object> finding in RequiredObjects(comparison, "findings", 64))
                        text.AppendLine(RequiredString(finding, "outcome").ToUpperInvariant() + " | " +
                            RequiredString(finding, "requirement_id") + " | " +
                            RequiredString(finding, "message"));
                }
                _result = result;
                if (_candidates.Items.Count > 0) _candidates.SelectedIndex = 0;
                _output.Text = text.ToString();
                _status.Text = "Candidates displayed for engineer choice; no follow-on action performed.";
            }
            catch (System.Exception error)
            {
                _status.Text = "Discovery failed: " + error.Message;
            }
            finally { _manualDraft = null; _busy = false; UpdateButtons(); }
        }

        private async Task Choose()
        {
            if (_busy || _result == null || _candidates.SelectedItem == null) return;
            string rationale = (_rationale.Text ?? String.Empty).Trim();
            if (rationale.Length < 1) { _status.Text = "A rationale is required."; return; }
            string selected = _candidates.SelectedItem.ToString();
            int separator = selected.IndexOf(" | ", StringComparison.Ordinal);
            string candidateId = separator < 0 ? selected : selected.Substring(0, separator);
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["result"] = _result;
            payload["selection_id"] = "manufacturer-selection:" + Guid.NewGuid().ToString("N");
            payload["candidate_id"] = candidateId;
            payload["selected_by"] = "engineer:" + Environment.UserName.ToLowerInvariant();
            payload["selected_on"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            payload["rationale"] = rationale;
            _busy = true; UpdateButtons(); _status.Text = "Recording engineer choice...";
            try
            {
                Dictionary<string, object> response = await Post("/v1/manufacturers/discover/select", payload);
                Dictionary<string, object> selection = RequiredObject(response, "selection");
                if (RequiredBool(selection, "product_approval_performed") ||
                    RequiredBool(selection, "automatic_download_performed") ||
                    RequiredBool(selection, "catalog_registration_performed") ||
                    RequiredBool(selection, "drawing_mutation_performed"))
                    throw new InvalidOperationException("Selection response claimed an unauthorized action.");
                _output.AppendText(Environment.NewLine + "ENGINEER CHOICE FOR FOLLOW-ON REVIEW" + Environment.NewLine +
                    RequiredString(selection, "candidate_id") + " | Part number " +
                    RequiredString(selection, "part_number") + Environment.NewLine +
                    "Selection SHA-256: " + RequiredSha256(selection, "selection_sha256") + Environment.NewLine +
                    "product_approval_performed=false" + Environment.NewLine +
                    "automatic_download_performed=false" + Environment.NewLine +
                    "catalog_registration_performed=false" + Environment.NewLine +
                    "drawing_mutation_performed=false" + Environment.NewLine);
                _status.Text = "Choice recorded for separate review; product is not approved or downloaded.";
            }
            catch (System.Exception error) { _status.Text = "Selection failed: " + error.Message; }
            finally { _busy = false; UpdateButtons(); }
        }

        private async Task<Dictionary<string, object>> Post(string path, Dictionary<string, object> value)
        {
            string token = Environment.GetEnvironmentVariable("AI_ENGINEER_API_TOKEN");
            if (String.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("AI_ENGINEER_API_TOKEN is not configured for AutoCAD.");
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(_serializer.Serialize(value), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string content = await response.Content.ReadAsStringAsync();
                    if (Encoding.UTF8.GetByteCount(content) > MaximumJsonBytes)
                        throw new InvalidOperationException("Local API response exceeded 1 MiB.");
                    if (!response.IsSuccessStatusCode)
                    {
                        string detail = "";
                        try
                        {
                            Dictionary<string, object> errorDocument = _serializer.DeserializeObject(content) as Dictionary<string, object>;
                            object rawDetail;
                            if (errorDocument != null && errorDocument.TryGetValue("detail", out rawDetail) && rawDetail is string)
                                detail = ((string)rawDetail).Trim();
                        }
                        catch { detail = ""; }
                        if (detail.Length > 512) detail = detail.Substring(0, 512);
                        throw new InvalidOperationException("Local API returned HTTP " +
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) +
                            (detail.Length > 0 ? ": " + detail : "."));
                    }
                    Dictionary<string, object> document = _serializer.DeserializeObject(content) as Dictionary<string, object>;
                    if (document == null) throw new InvalidOperationException("Local API response is invalid.");
                    return document;
                }
            }
        }

        private void UpdateButtons()
        {
            _select.Enabled = !_busy;
            _discover.Enabled = !_busy && _request != null;
            _verifyManual.Enabled = !_busy && _request != null;
            _copy.Enabled = !_busy && !String.IsNullOrWhiteSpace(_output.Text);
            _choose.Enabled = !_busy && _result != null && _candidates.SelectedItem != null;
        }

        private static Dictionary<string, object> RequiredObject(Dictionary<string, object> value, string name)
        {
            object raw; Dictionary<string, object> result;
            if (!value.TryGetValue(name, out raw) || (result = raw as Dictionary<string, object>) == null)
                throw new InvalidOperationException("Local data is missing " + name + ".");
            return result;
        }

        private static List<Dictionary<string, object>> RequiredObjects(Dictionary<string, object> value, string name, int maximum)
        {
            object raw; object[] items;
            if (!value.TryGetValue(name, out raw) || (items = raw as object[]) == null || items.Length > maximum)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (object item in items)
            {
                Dictionary<string, object> record = item as Dictionary<string, object>;
                if (record == null) throw new InvalidOperationException("Local data has invalid " + name + ".");
                result.Add(record);
            }
            return result;
        }

        private static string RequiredString(Dictionary<string, object> value, string name)
        {
            object raw; string result;
            if (!value.TryGetValue(name, out raw) || String.IsNullOrWhiteSpace(result = raw as string) || result.Length > 2048)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static string RequiredSha256(Dictionary<string, object> value, string name)
        {
            string result = RequiredString(value, name);
            if (result.Length != 64) throw new InvalidOperationException("Local data has invalid " + name + ".");
            for (int index = 0; index < result.Length; index++)
                if (!(result[index] >= '0' && result[index] <= '9') && !(result[index] >= 'a' && result[index] <= 'f'))
                    throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static bool RequiredBool(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw) || !(raw is bool))
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return (bool)raw;
        }
    }

    internal sealed class ManufacturerReviewPaletteControl : UserControl
    {
        private const int MaximumJsonBytes = 1048576;
        private readonly Button _select = new Button();
        private readonly Button _review = new Button();
        private readonly Button _clear = new Button();
        private readonly Label _file = new Label();
        private readonly Label _status = new Label();
        private readonly TextBox _output = new TextBox();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly HttpClient _client;
        private Dictionary<string, object> _request;
        private bool _busy;

        public ManufacturerReviewPaletteControl()
        {
            _serializer.MaxJsonLength = MaximumJsonBytes;
            _client = CreateClient();
            BuildLayout();
            string preloaded = Environment.GetEnvironmentVariable(
                "AI_ENGINEER_MANUFACTURER_REVIEW_REQUEST");
            if (!String.IsNullOrWhiteSpace(preloaded))
            {
                try
                {
                    LoadRequest(preloaded);
                    Load += async delegate { await Review(); };
                }
                catch (System.Exception error)
                {
                    _status.Text = "Preloaded manufacturer review failed: " + error.Message;
                }
            }
        }

        private static HttpClient CreateClient()
        {
            int port = 8765;
            string configured = Environment.GetEnvironmentVariable("AI_ENGINEER_API_PORT");
            int parsed;
            if (!String.IsNullOrWhiteSpace(configured) &&
                Int32.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
                parsed >= 1024 && parsed <= 65535)
                port = parsed;
            HttpClientHandler handler = new HttpClientHandler();
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            client.Timeout = TimeSpan.FromSeconds(15);
            return client;
        }

        private void BuildLayout()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label warning = new Label();
            warning.AutoSize = true;
            warning.MaximumSize = new Size(320, 0);
            warning.ForeColor = Color.DarkOrange;
            warning.Text = "Read-only exact evidence review. No download, catalog change, or drawing mutation.";
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            _select.Text = "Select reviewed JSON";
            _review.Text = "Review locally";
            _clear.Text = "Clear";
            buttons.Controls.Add(_select);
            buttons.Controls.Add(_review);
            buttons.Controls.Add(_clear);
            _file.AutoSize = true;
            _file.MaximumSize = new Size(320, 0);
            _file.Text = "No reviewed manufacturer package selected.";
            _output.Dock = DockStyle.Fill;
            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Both;
            _output.WordWrap = false;
            _status.AutoSize = true;
            _status.MaximumSize = new Size(320, 0);
            layout.Controls.Add(warning, 0, 0);
            layout.Controls.Add(buttons, 0, 1);
            layout.Controls.Add(_file, 0, 2);
            layout.Controls.Add(_output, 0, 3);
            layout.Controls.Add(_status, 0, 4);
            Controls.Add(layout);
            _select.Click += delegate { SelectRequest(); };
            _review.Click += async delegate { await Review(); };
            _clear.Click += delegate { if (!_busy) { _output.Clear(); _status.Text = "Cleared."; } };
            UpdateButtons();
        }

        private void SelectRequest()
        {
            if (_busy) return;
            using (System.Windows.Forms.OpenFileDialog dialog =
                new System.Windows.Forms.OpenFileDialog())
            {
                dialog.Title = "Select reviewed AI-Engineer manufacturer package";
                dialog.Filter = "JSON files (*.json)|*.json";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (dialog.ShowDialog() != DialogResult.OK) return;
                LoadRequest(dialog.FileName);
            }
        }

        private void LoadRequest(string path)
        {
            if (!Path.IsPathRooted(path))
                throw new InvalidOperationException("Reviewed JSON path must be absolute.");
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length < 2 || info.Length > MaximumJsonBytes)
                throw new InvalidOperationException("Reviewed JSON file is missing, empty, or exceeds 1 MiB.");
            string content = File.ReadAllText(info.FullName, Encoding.UTF8);
            Dictionary<string, object> parsed =
                _serializer.DeserializeObject(content) as Dictionary<string, object>;
            if (parsed == null)
                throw new InvalidOperationException("Reviewed manufacturer package is invalid.");
            _request = parsed;
            _file.Text = "Reviewed package: " + info.Name;
            _status.Text = "Package loaded locally; review exact identities before evaluation.";
            UpdateButtons();
        }

        private async Task Review()
        {
            if (_busy || _request == null) return;
            _busy = true;
            UpdateButtons();
            _status.Text = "Reviewing exact manufacturer evidence locally...";
            try
            {
                Dictionary<string, object> response = await Post("/v1/manufacturers/review", _request);
                Dictionary<string, object> bundle = RequiredObject(response, "bundle");
                Dictionary<string, object> manufacturer = RequiredObject(bundle, "manufacturer");
                Dictionary<string, object> product = RequiredObject(bundle, "product");
                Dictionary<string, object> result = RequiredObject(response, "result");
                if (RequiredBool(result, "provider_egress_performed") ||
                    RequiredBool(result, "retrieval_performed_by_review") ||
                    RequiredBool(result, "catalog_registration_performed_by_review") ||
                    RequiredBool(result, "drawing_mutation_performed"))
                    throw new InvalidOperationException("Manufacturer response claimed an unsafe review action.");
                string bundleHash = RequiredSha256(bundle, "bundle_sha256");
                if (!String.Equals(bundleHash, RequiredSha256(result, "bundle_sha256"), StringComparison.Ordinal))
                    throw new InvalidOperationException("Manufacturer result bundle hash does not match.");
                StringBuilder text = new StringBuilder();
                text.AppendLine("Manufacturer: " + RequiredString(manufacturer, "legal_name") +
                    " | " + RequiredString(manufacturer, "brand_name"));
                text.AppendLine("Product: " + RequiredString(product, "family_name") +
                    " | " + RequiredString(product, "model") +
                    " | Part number " + RequiredString(product, "part_number"));
                text.AppendLine("Bundle SHA-256: " + bundleHash);
                text.AppendLine("Result SHA-256: " + RequiredSha256(result, "result_sha256"));
                text.AppendLine("Complete: " + RequiredBool(result, "complete").ToString().ToLowerInvariant());
                text.AppendLine("provider_egress_performed=false");
                text.AppendLine("retrieval_performed_by_review=false");
                text.AppendLine("catalog_registration_performed_by_review=false");
                text.AppendLine("drawing_mutation_performed=false");
                text.AppendLine();
                text.AppendLine("ARTIFACTS");
                foreach (Dictionary<string, object> artifact in RequiredObjects(bundle, "artifacts", 512))
                    text.AppendLine(RequiredString(artifact, "artifact_id") + " | " +
                        RequiredString(artifact, "kind") + " | " +
                        RequiredString(artifact, "media_type") + " | " +
                        RequiredSha256(artifact, "content_sha256") + " | " +
                        RequiredString(artifact, "locator"));
                AppendOptionalRecord(text, response, "quarantine", "QUARANTINE", "record_sha256");
                AppendOptionalRecord(text, response, "model_binding", "MODEL BINDING", "binding_sha256");
                AppendOptionalRecord(text, response, "symbol_candidate", "SYMBOL CANDIDATE", "candidate_sha256");
                AppendOptionalRecord(text, response, "catalog_registration", "CATALOG REGISTRATION", "registration_sha256");
                text.AppendLine();
                text.AppendLine("FINDINGS (PASS/UNKNOWN/CONFLICT)");
                foreach (Dictionary<string, object> finding in RequiredObjects(result, "findings", 64))
                    text.AppendLine(RequiredString(finding, "outcome").ToUpperInvariant() + " | " +
                        RequiredString(finding, "subject_id") + " | " +
                        RequiredString(finding, "message"));
                _output.Text = text.ToString();
                _status.Text = "Manufacturer evidence reviewed locally; drawing unchanged.";
            }
            catch (System.Exception error)
            {
                _status.Text = "Manufacturer review failed: " + error.Message;
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }

        private async Task<Dictionary<string, object>> Post(string path, Dictionary<string, object> value)
        {
            string token = Environment.GetEnvironmentVariable("AI_ENGINEER_API_TOKEN");
            if (String.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("AI_ENGINEER_API_TOKEN is not configured for AutoCAD.");
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(_serializer.Serialize(value), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string content = await response.Content.ReadAsStringAsync();
                    if (Encoding.UTF8.GetByteCount(content) > MaximumJsonBytes)
                        throw new InvalidOperationException("Local API response exceeded 1 MiB.");
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Local API returned HTTP " +
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                    Dictionary<string, object> document =
                        _serializer.DeserializeObject(content) as Dictionary<string, object>;
                    if (document == null)
                        throw new InvalidOperationException("Local API response is invalid.");
                    return document;
                }
            }
        }

        private void UpdateButtons()
        {
            _select.Enabled = !_busy;
            _review.Enabled = !_busy && _request != null;
            _clear.Enabled = !_busy;
        }

        private static void AppendOptionalRecord(
            StringBuilder text, Dictionary<string, object> response, string name,
            string heading, string hashName)
        {
            object raw;
            if (!response.TryGetValue(name, out raw) || raw == null) return;
            Dictionary<string, object> record = raw as Dictionary<string, object>;
            if (record == null)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            text.AppendLine();
            text.AppendLine(heading);
            text.AppendLine("SHA-256: " + RequiredSha256(record, hashName));
        }

        private static Dictionary<string, object> RequiredObject(
            Dictionary<string, object> value, string name)
        {
            object raw;
            Dictionary<string, object> result;
            if (value == null || !value.TryGetValue(name, out raw) ||
                (result = raw as Dictionary<string, object>) == null)
                throw new InvalidOperationException("Local data is missing " + name + ".");
            return result;
        }

        private static List<Dictionary<string, object>> RequiredObjects(
            Dictionary<string, object> value, string name, int maximum)
        {
            object raw;
            object[] items;
            if (!value.TryGetValue(name, out raw) || (items = raw as object[]) == null ||
                items.Length > maximum)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (object item in items)
            {
                Dictionary<string, object> record = item as Dictionary<string, object>;
                if (record == null)
                    throw new InvalidOperationException("Local data has invalid " + name + ".");
                result.Add(record);
            }
            return result;
        }

        private static string RequiredString(Dictionary<string, object> value, string name)
        {
            object raw;
            string result;
            if (!value.TryGetValue(name, out raw) || String.IsNullOrWhiteSpace(result = raw as string) ||
                result.Length > 2048)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static string RequiredSha256(Dictionary<string, object> value, string name)
        {
            string result = RequiredString(value, name);
            if (result.Length != 64)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            for (int index = 0; index < result.Length; index++)
                if (!(result[index] >= '0' && result[index] <= '9') &&
                    !(result[index] >= 'a' && result[index] <= 'f'))
                    throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static bool RequiredBool(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw) || !(raw is bool))
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return (bool)raw;
        }
    }

    internal sealed class PanelSldPaletteControl : UserControl
    {
        private const int MaximumJsonBytes = 1048576;
        private readonly ComboBox _language = new ComboBox();
        private readonly Label _context = new Label();
        private readonly TextBox _reviewer = new TextBox();
        private readonly TextBox _assumption = new TextBox();
        private readonly Button _refresh = new Button();
        private readonly Button _extract = new Button();
        private readonly Button _clear = new Button();
        private readonly TextBox _output = new TextBox();
        private readonly Label _status = new Label();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly HttpClient _client;
        private bool _busy;

        public PanelSldPaletteControl()
        {
            _serializer.MaxJsonLength = MaximumJsonBytes;
            _client = CreateClient();
            BuildLayout();
            ApplyLanguage();
            RefreshContext();
        }

        private static HttpClient CreateClient()
        {
            int port = 8765;
            string configured = Environment.GetEnvironmentVariable("AI_ENGINEER_API_PORT");
            int parsed;
            if (!String.IsNullOrWhiteSpace(configured) &&
                Int32.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
                parsed >= 1024 && parsed <= 65535)
                port = parsed;
            HttpClientHandler handler = new HttpClientHandler();
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            client.Timeout = TimeSpan.FromSeconds(15);
            return client;
        }

        private void BuildLayout()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 8;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _language.DropDownStyle = ComboBoxStyle.DropDownList;
            _language.Items.Add("Arabic");
            _language.Items.Add("English");
            _language.SelectedIndex = 0;
            _language.SelectedIndexChanged += delegate { ApplyLanguage(); };
            _context.AutoSize = true;
            _context.MaximumSize = new Size(320, 0);
            _reviewer.MaxLength = 128;
            _reviewer.Text = "engineer:reviewer";
            _assumption.MaxLength = 512;
            _assumption.Text = "Supported M21 marker profile reviewed.";

            TableLayoutPanel inputs = new TableLayoutPanel();
            inputs.AutoSize = true;
            inputs.ColumnCount = 2;
            inputs.RowCount = 2;
            inputs.Controls.Add(new Label { Text = "Reviewer", AutoSize = true }, 0, 0);
            inputs.Controls.Add(_reviewer, 1, 0);
            inputs.Controls.Add(new Label { Text = "Assumption", AutoSize = true }, 0, 1);
            inputs.Controls.Add(_assumption, 1, 1);
            _reviewer.Width = _assumption.Width = 210;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.WrapContents = true;
            buttons.MaximumSize = new Size(320, 0);
            _refresh.AutoSize = _extract.AutoSize = _clear.AutoSize = true;
            buttons.Controls.Add(_refresh);
            buttons.Controls.Add(_extract);
            buttons.Controls.Add(_clear);

            _output.Dock = DockStyle.Fill;
            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Both;
            _output.WordWrap = false;
            _status.AutoSize = true;
            _status.MaximumSize = new Size(320, 0);
            layout.Controls.Add(_language, 0, 0);
            layout.Controls.Add(_context, 0, 1);
            layout.Controls.Add(inputs, 0, 2);
            layout.Controls.Add(buttons, 0, 3);
            layout.Controls.Add(_output, 0, 5);
            layout.Controls.Add(_status, 0, 6);
            Controls.Add(layout);

            _refresh.Click += delegate { RefreshContext(); };
            _extract.Click += async delegate { await ExtractAndReview(); };
            _clear.Click += delegate { if (!_busy) { _output.Clear(); SetStatus("Cleared."); } };
        }

        private bool IsArabic { get { return _language.SelectedIndex == 0; } }

        private void ApplyLanguage()
        {
            RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
            _refresh.Text = IsArabic ? "\u062a\u062d\u062f\u064a\u062b \u0627\u0644\u0633\u064a\u0627\u0642" : "Refresh context";
            _extract.Text = IsArabic ? "\u0627\u0633\u062a\u062e\u0631\u0627\u062c \u0648\u0645\u0631\u0627\u062c\u0639\u0629" : "Extract and review";
            _clear.Text = IsArabic ? "\u0645\u0633\u062d" : "Clear";
            RefreshContext();
        }

        private void RefreshContext()
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null)
            {
                _context.Text = IsArabic
                    ? "\u0644\u0627 \u064a\u0648\u062c\u062f \u0633\u064a\u0627\u0642 AutoCAD \u0635\u0631\u064a\u062d."
                    : "No explicit AutoCAD context is available.";
                _extract.Enabled = false;
            }
            else
            {
                _context.Text = (IsArabic ? "\u0627\u0644\u0633\u064a\u0627\u0642: " : "Context: ") +
                    evidence.SourceMode + " | " + evidence.EntityCount.ToString(CultureInfo.InvariantCulture) +
                    (IsArabic ? " \u0639\u0646\u0635\u0631" : " entities") + " | DBMOD=" +
                    evidence.DatabaseModified.ToString(CultureInfo.InvariantCulture);
                _extract.Enabled = !_busy;
            }
            _refresh.Enabled = _clear.Enabled = !_busy;
        }

        private async Task ExtractAndReview()
        {
            if (_busy) return;
            _busy = true;
            RefreshContext();
            SetStatus(IsArabic ? "\u062c\u0627\u0631\u064d \u0627\u0644\u0627\u0633\u062a\u062e\u0631\u0627\u062c \u0645\u062d\u0644\u064a\u064b\u0627..." : "Extracting locally...");
            try
            {
                Dictionary<string, object> response = await Post(
                    "/v1/panels/extract-schedule", BuildRequest());
                Dictionary<string, object> extraction = RequiredObject(response, "extraction");
                ValidateExtraction(extraction);
                object rawSchedule;
                if (!response.TryGetValue("schedule", out rawSchedule))
                    throw new InvalidOperationException("Local data is missing schedule.");
                bool complete = RequiredBool(extraction, "complete");
                if (!complete)
                {
                    if (rawSchedule != null || HasNonNull(extraction, "semantic_model") ||
                        HasNonNull(extraction, "contract"))
                        throw new InvalidOperationException("Incomplete extraction returned partial semantic data.");
                    ShowFindings(extraction);
                    SetStatus(IsArabic
                        ? "\u0627\u0644\u0627\u0633\u062a\u062e\u0631\u0627\u062c \u063a\u064a\u0631 \u0645\u0643\u062a\u0645\u0644\u061b \u0631\u0627\u062c\u0639 UNKNOWN/CONFLICT."
                        : "Extraction incomplete; review UNKNOWN/CONFLICT findings.");
                    return;
                }
                Dictionary<string, object> schedule = rawSchedule as Dictionary<string, object>;
                if (schedule == null)
                    throw new InvalidOperationException("Complete extraction is missing its schedule.");
                Dictionary<string, object> contract = RequiredObject(extraction, "contract");
                ValidateSchedule(schedule, contract);
                ShowSchedule(extraction, schedule);
                SetStatus(IsArabic
                    ? "\u062a\u0645 \u0627\u0644\u0627\u0633\u062a\u062e\u0631\u0627\u062c \u0648\u0627\u0644\u0645\u0631\u0627\u062c\u0639\u0629\u061b \u0627\u0644\u0631\u0633\u0645 \u0644\u0645 \u064a\u062a\u063a\u064a\u0631."
                    : "Extraction reviewed; drawing unchanged.");
            }
            catch (System.Exception error)
            {
                string message = error is InvalidOperationException ? error.Message : error.GetType().Name;
                SetStatus((IsArabic ? "\u062a\u0639\u0630\u0631 \u0627\u0644\u0637\u0644\u0628: " : "Request failed: ") + SafeText(message, 256));
            }
            finally
            {
                _busy = false;
                RefreshContext();
            }
        }

        private Dictionary<string, object> BuildRequest()
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null)
                throw new InvalidOperationException("Export an explicit AutoCAD context first.");
            string reviewer = _reviewer.Text.Trim();
            string assumption = _assumption.Text.Trim();
            if (reviewer.Length < 1 || reviewer.Length > 128 ||
                assumption.Length < 1 || assumption.Length > 512)
                throw new InvalidOperationException("Reviewer and assumption are required and bounded.");
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["context_id"] = evidence.ContextId;
            request["context_evidence_sha256"] = evidence.Sha256;
            request["reviewed_by"] = reviewer;
            request["assumptions"] = new object[] { assumption };
            return request;
        }

        private static void ValidateExtraction(Dictionary<string, object> extraction)
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null ||
                !String.Equals(RequiredString(extraction, "context_id"), evidence.ContextId, StringComparison.Ordinal) ||
                !String.Equals(RequiredString(extraction, "context_evidence_sha256"), evidence.Sha256, StringComparison.Ordinal) ||
                !RequiredBool(extraction, "existing_schedule_extraction_performed") ||
                RequiredBool(extraction, "provider_egress_performed") ||
                RequiredBool(extraction, "drawing_mutation_performed"))
                throw new InvalidOperationException("Panel/SLD extraction returned unsafe or mismatched evidence.");
            RequiredString(extraction, "result_sha256");
            RequiredInt(extraction, "supported_marker_count");
        }

        private static void ValidateSchedule(
            Dictionary<string, object> schedule, Dictionary<string, object> contract)
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null ||
                !String.Equals(RequiredString(schedule, "context_id"), evidence.ContextId, StringComparison.Ordinal) ||
                !String.Equals(RequiredString(schedule, "context_evidence_sha256"), evidence.Sha256, StringComparison.Ordinal) ||
                !String.Equals(RequiredString(schedule, "source_contract_sha256"),
                    RequiredString(contract, "contract_sha256"), StringComparison.Ordinal) ||
                !RequiredBool(schedule, "complete") ||
                !RequiredBool(schedule, "schedule_generation_performed") ||
                RequiredBool(schedule, "existing_schedule_extraction_performed") ||
                RequiredBool(schedule, "drawing_mutation_performed"))
                throw new InvalidOperationException("Panel schedule returned unsafe or mismatched evidence.");
            RequiredString(schedule, "result_sha256");
        }

        private void ShowFindings(Dictionary<string, object> extraction)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Extraction SHA-256: " + RequiredString(extraction, "result_sha256"));
            text.AppendLine("complete=false");
            text.AppendLine("drawing_mutation_performed=false");
            text.AppendLine();
            foreach (Dictionary<string, object> finding in RequiredObjects(extraction, "findings"))
                text.AppendLine(RequiredString(finding, "outcome").ToUpperInvariant() + " | " +
                    RequiredString(finding, "subject_id") + " | " +
                    SafeText(RequiredString(finding, "message"), 512));
            _output.Text = text.ToString();
        }

        private void ShowSchedule(
            Dictionary<string, object> extraction, Dictionary<string, object> schedule)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Extraction SHA-256: " + RequiredString(extraction, "result_sha256"));
            text.AppendLine("Schedule SHA-256: " + RequiredString(schedule, "result_sha256"));
            text.AppendLine("Panel: " + RequiredString(schedule, "panel_id") + " | " +
                RequiredString(schedule, "panel_name"));
            text.AppendLine("Ways=" + RequiredInt(schedule, "outgoing_way_count").ToString(CultureInfo.InvariantCulture) +
                " | Load ways=" + RequiredInt(schedule, "load_way_count").ToString(CultureInfo.InvariantCulture) +
                " | Spares=" + RequiredInt(schedule, "spare_way_count").ToString(CultureInfo.InvariantCulture));
            text.AppendLine("existing_schedule_extraction_performed=true");
            text.AppendLine("drawing_mutation_performed=false");
            text.AppendLine();
            foreach (Dictionary<string, object> row in RequiredObjects(schedule, "rows"))
            {
                text.Append("WAY ").Append(RequiredInt(row, "way_number"))
                    .Append(" | ").Append(RequiredString(row, "circuit_id"))
                    .Append(" | ").Append(RequiredString(row, "circuit_name"))
                    .Append(" | ").Append(RequiredString(row, "kind"))
                    .Append(" | ").Append(RequiredString(row, "phase"))
                    .Append(" | ").Append(RequiredString(row, "protective_device_designation"))
                    .AppendLine();
            }
            _output.Text = text.ToString();
        }

        private async Task<Dictionary<string, object>> Post(
            string path, Dictionary<string, object> value)
        {
            string token = Environment.GetEnvironmentVariable("AI_ENGINEER_API_TOKEN");
            if (String.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("AI_ENGINEER_API_TOKEN is not configured for AutoCAD.");
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(_serializer.Serialize(value), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string content = await response.Content.ReadAsStringAsync();
                    if (Encoding.UTF8.GetByteCount(content) > MaximumJsonBytes)
                        throw new InvalidOperationException("Local API response exceeded its byte limit.");
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Local API returned HTTP " +
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                    Dictionary<string, object> document =
                        _serializer.DeserializeObject(content) as Dictionary<string, object>;
                    if (document == null)
                        throw new InvalidOperationException("Local API response is invalid.");
                    return document;
                }
            }
        }

        private static bool HasNonNull(Dictionary<string, object> value, string name)
        {
            object raw;
            return value.TryGetValue(name, out raw) && raw != null;
        }

        private static Dictionary<string, object> RequiredObject(Dictionary<string, object> value, string name)
        {
            object raw;
            Dictionary<string, object> result;
            if (value == null || !value.TryGetValue(name, out raw) ||
                (result = raw as Dictionary<string, object>) == null)
                throw new InvalidOperationException("Local data is missing " + name + ".");
            return result;
        }

        private static string RequiredString(Dictionary<string, object> value, string name)
        {
            object raw;
            string result;
            if (value == null || !value.TryGetValue(name, out raw) ||
                String.IsNullOrEmpty(result = raw as string) || result.Length > MaximumJsonBytes)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static bool RequiredBool(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw) || !(raw is bool))
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return (bool)raw;
        }

        private static int RequiredInt(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw))
                throw new InvalidOperationException("Local data is missing " + name + ".");
            int result = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            if (result < 0 || result > 8192)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static List<Dictionary<string, object>> RequiredObjects(
            Dictionary<string, object> value, string name)
        {
            object raw;
            IEnumerable items;
            if (!value.TryGetValue(name, out raw) ||
                (items = raw as IEnumerable) == null || raw is string)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (object item in items)
            {
                Dictionary<string, object> document = item as Dictionary<string, object>;
                if (document == null)
                    throw new InvalidOperationException("Local data has invalid " + name + ".");
                result.Add(document);
                if (result.Count > 8192)
                    throw new InvalidOperationException("Local data exceeded the item limit.");
            }
            return result;
        }

        private void SetStatus(string value) { _status.Text = value; }

        private static string SafeText(string value, int maximum)
        {
            if (String.IsNullOrEmpty(value)) return "error";
            StringBuilder result = new StringBuilder();
            foreach (char character in value)
            {
                if (result.Length >= maximum) break;
                result.Append(Char.IsControl(character) ? ' ' : character);
            }
            return result.ToString();
        }
    }

    internal sealed class LowVoltageCalculationPaletteControl : UserControl
    {
        private const int MaximumJsonBytes = 1048576;
        private readonly ComboBox _phase = new ComboBox();
        private readonly NumericUpDown _power = new NumericUpDown();
        private readonly NumericUpDown _demand = new NumericUpDown();
        private readonly NumericUpDown _voltage = new NumericUpDown();
        private readonly NumericUpDown _powerFactor = new NumericUpDown();
        private readonly NumericUpDown _efficiency = new NumericUpDown();
        private readonly NumericUpDown _resistance = new NumericUpDown();
        private readonly NumericUpDown _reactance = new NumericUpDown();
        private readonly TextBox _reviewedBy = new TextBox();
        private readonly TextBox _assumption = new TextBox();
        private readonly NumericUpDown _aggregatePower = new NumericUpDown();
        private readonly NumericUpDown _aggregateDemand = new NumericUpDown();
        private readonly NumericUpDown _diversity = new NumericUpDown();
        private readonly TextBox _phaseAllocations = new TextBox();
        private readonly NumericUpDown _ampacityRoute = new NumericUpDown();
        private readonly TextBox _cableDesignation = new TextBox();
        private readonly NumericUpDown _baseAmpacity = new NumericUpDown();
        private readonly NumericUpDown _ambientFactor = new NumericUpDown();
        private readonly NumericUpDown _groupingFactor = new NumericUpDown();
        private readonly TextBox _ampacityBasis = new TextBox();
        private readonly TextBox _ampacityAssumption = new TextBox();
        private readonly NumericUpDown _shortCircuitRoute = new NumericUpDown();
        private readonly ComboBox _faultArrangement = new ComboBox();
        private readonly NumericUpDown _faultVoltage = new NumericUpDown();
        private readonly NumericUpDown _voltageMultiplier = new NumericUpDown();
        private readonly NumericUpDown _sourceResistance = new NumericUpDown();
        private readonly NumericUpDown _sourceReactance = new NumericUpDown();
        private readonly NumericUpDown _pathResistance = new NumericUpDown();
        private readonly NumericUpDown _pathReactance = new NumericUpDown();
        private readonly TextBox _faultBasis = new TextBox();
        private readonly TextBox _faultAssumption = new TextBox();
        private readonly TextBox _deviceDesignation = new TextBox();
        private readonly NumericUpDown _deviceRating = new NumericUpDown();
        private readonly NumericUpDown _breakingCapacity = new NumericUpDown();
        private readonly NumericUpDown _ampacityMultiplier = new NumericUpDown();
        private readonly NumericUpDown _maximumVoltageDrop = new NumericUpDown();
        private readonly TextBox _candidateBasis = new TextBox();
        private readonly TextBox _candidateAssumption = new TextBox();
        private readonly NumericUpDown _peRoute = new NumericUpDown();
        private readonly ComboBox _peApplicability = new ComboBox();
        private readonly TextBox _peApplicabilityBasis = new TextBox();
        private readonly ComboBox _peDisconnectionBasis = new ComboBox();
        private readonly TextBox _peDisconnectionReference = new TextBox();
        private readonly NumericUpDown _peDisconnectionTime = new NumericUpDown();
        private readonly NumericUpDown _peConductorFactor = new NumericUpDown();
        private readonly TextBox _peRuleTitle = new TextBox();
        private readonly TextBox _peRuleVersion = new TextBox();
        private readonly TextBox _peRuleLocator = new TextBox();
        private readonly TextBox _peRuleSha256 = new TextBox();
        private readonly TextBox _peAssumption = new TextBox();
        private readonly Label _context = new Label();
        private readonly TextBox _result = new TextBox();
        private readonly Label _status = new Label();
        private readonly Button _refresh = new Button();
        private readonly Button _calculate = new Button();
        private readonly Button _aggregate = new Button();
        private readonly Button _derateAmpacity = new Button();
        private readonly Button _shortCircuit = new Button();
        private readonly Button _reviewCandidate = new Button();
        private readonly Button _sizePe = new Button();
        private readonly Button _clear = new Button();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly HttpClient _client;
        private bool _busy;

        public LowVoltageCalculationPaletteControl()
        {
            _serializer.MaxJsonLength = MaximumJsonBytes;
            _client = CreateClient();
            BuildLayout();
            RefreshContext();
        }

        private static HttpClient CreateClient()
        {
            int port = 8765;
            string configured = Environment.GetEnvironmentVariable("AI_ENGINEER_API_PORT");
            int parsed;
            if (!String.IsNullOrWhiteSpace(configured) &&
                Int32.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
                parsed >= 1024 && parsed <= 65535)
                port = parsed;
            HttpClientHandler handler = new HttpClientHandler();
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            client.Timeout = TimeSpan.FromSeconds(15);
            return client;
        }

        private void BuildLayout()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            Padding = new Padding(8);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _context.AutoSize = true;
            _context.MaximumSize = new Size(320, 0);

            TableLayoutPanel inputs = new TableLayoutPanel();
            inputs.AutoScroll = true;
            inputs.Dock = DockStyle.Fill;
            inputs.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            inputs.ColumnCount = 2;
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            AddInput(inputs, 0, "Phase system", _phase);
            _phase.DropDownStyle = ComboBoxStyle.DropDownList;
            _phase.Items.Add("single_phase");
            _phase.Items.Add("balanced_three_phase");
            _phase.SelectedIndex = 0;
            ConfigureNumber(_power, 1000000000, 2, 1000);
            ConfigureNumber(_demand, 1, 3, 1);
            ConfigureNumber(_voltage, 1000000, 2, 230);
            ConfigureNumber(_powerFactor, 1, 3, 0.9M);
            ConfigureNumber(_efficiency, 1, 3, 0.95M);
            ConfigureNumber(_resistance, 1000000, 4, 7.41M);
            ConfigureNumber(_reactance, 1000000, 4, 0.08M);
            AddInput(inputs, 1, "Load output power (W)", _power);
            AddInput(inputs, 2, "Demand factor", _demand);
            AddInput(inputs, 3, "Nominal voltage (V)", _voltage);
            AddInput(inputs, 4, "Power factor", _powerFactor);
            AddInput(inputs, 5, "Efficiency", _efficiency);
            AddInput(inputs, 6, "R ac (ohm/km)", _resistance);
            AddInput(inputs, 7, "X (ohm/km)", _reactance);
            _reviewedBy.Text = "engineer:operator";
            _reviewedBy.MaxLength = 128;
            _assumption.Text = "Balanced sinusoidal steady-state load with lagging power factor.";
            _assumption.MaxLength = 512;
            AddInput(inputs, 8, "Reviewed by", _reviewedBy);
            AddInput(inputs, 9, "Assumption", _assumption);
            ConfigureNumber(_aggregatePower, 1000000000, 2, 1000);
            ConfigureNumber(_aggregateDemand, 1, 3, 1);
            ConfigureNumber(_diversity, 1, 3, 1);
            _phaseAllocations.Text = "l1,l2,l3,three_phase";
            _phaseAllocations.MaxLength = 4096;
            AddInput(inputs, 10, "Power per load (W)", _aggregatePower);
            AddInput(inputs, 11, "Per-load demand", _aggregateDemand);
            AddInput(inputs, 12, "Group diversity", _diversity);
            AddInput(inputs, 13, "Load phases CSV", _phaseAllocations);
            ConfigureNumber(_ampacityRoute, 256, 0, 1);
            _ampacityRoute.Minimum = 1;
            _cableDesignation.Text = "Reviewed cable candidate";
            _cableDesignation.MaxLength = 256;
            ConfigureNumber(_baseAmpacity, 1000000000, 3, 20);
            ConfigureNumber(_ambientFactor, 1, 3, 0.94M);
            ConfigureNumber(_groupingFactor, 1, 3, 0.8M);
            _ampacityBasis.Text = "Engineer-reviewed project ampacity and factor schedule.";
            _ampacityBasis.MaxLength = 512;
            _ampacityAssumption.Text = "Factors apply to the identified route and cable basis.";
            _ampacityAssumption.MaxLength = 512;
            AddInput(inputs, 14, "Ampacity route number", _ampacityRoute);
            AddInput(inputs, 15, "Cable designation", _cableDesignation);
            AddInput(inputs, 16, "Base ampacity (A)", _baseAmpacity);
            AddInput(inputs, 17, "Ambient factor", _ambientFactor);
            AddInput(inputs, 18, "Grouping factor", _groupingFactor);
            AddInput(inputs, 19, "Ampacity/factor basis", _ampacityBasis);
            AddInput(inputs, 20, "Ampacity assumption", _ampacityAssumption);
            ConfigureNumber(_shortCircuitRoute, 256, 0, 1);
            _shortCircuitRoute.Minimum = 1;
            _faultArrangement.DropDownStyle = ComboBoxStyle.DropDownList;
            _faultArrangement.Items.Add("single_phase_loop");
            _faultArrangement.Items.Add("balanced_three_phase");
            _faultArrangement.SelectedIndex = 1;
            ConfigureNumber(_faultVoltage, 1000000000, 2, 400);
            ConfigureNumber(_voltageMultiplier, 10, 3, 1);
            ConfigureNumber(_sourceResistance, 1000000000, 6, 0.01M);
            ConfigureNumber(_sourceReactance, 1000000000, 6, 0.05M);
            ConfigureNumber(_pathResistance, 1000000000, 6, 0.02M);
            ConfigureNumber(_pathReactance, 1000000000, 6, 0.04M);
            _faultBasis.Text = "Engineer-reviewed source and path impedance schedule.";
            _faultBasis.MaxLength = 512;
            _faultAssumption.Text = "Bolted symmetrical fault at the identified route.";
            _faultAssumption.MaxLength = 512;
            AddInput(inputs, 21, "Fault route number", _shortCircuitRoute);
            AddInput(inputs, 22, "Fault arrangement", _faultArrangement);
            AddInput(inputs, 23, "Fault nominal voltage (V)", _faultVoltage);
            AddInput(inputs, 24, "Voltage multiplier", _voltageMultiplier);
            AddInput(inputs, 25, "Source R (ohm)", _sourceResistance);
            AddInput(inputs, 26, "Source X (ohm)", _sourceReactance);
            AddInput(inputs, 27, "Path R (ohm)", _pathResistance);
            AddInput(inputs, 28, "Path X (ohm)", _pathReactance);
            AddInput(inputs, 29, "Impedance basis", _faultBasis);
            AddInput(inputs, 30, "Fault assumption", _faultAssumption);
            _deviceDesignation.Text = "10 A / 6 kA candidate";
            _deviceDesignation.MaxLength = 256;
            ConfigureNumber(_deviceRating, 1000000000, 3, 10);
            ConfigureNumber(_breakingCapacity, 1000000000, 3, 6000);
            ConfigureNumber(_ampacityMultiplier, 10, 3, 1.25M);
            _ampacityMultiplier.Minimum = 1;
            ConfigureNumber(_maximumVoltageDrop, 100, 3, 5);
            _candidateBasis.Text = "Engineer-reviewed project candidate rules.";
            _candidateBasis.MaxLength = 512;
            _candidateAssumption.Text = "Candidate values are reviewed project inputs.";
            _candidateAssumption.MaxLength = 512;
            AddInput(inputs, 31, "Protective device", _deviceDesignation);
            AddInput(inputs, 32, "Device rating (A)", _deviceRating);
            AddInput(inputs, 33, "Breaking capacity (A)", _breakingCapacity);
            AddInput(inputs, 34, "Required Iz / Ib", _ampacityMultiplier);
            AddInput(inputs, 35, "Maximum voltage drop (%)", _maximumVoltageDrop);
            AddInput(inputs, 36, "Candidate rule basis", _candidateBasis);
            AddInput(inputs, 37, "Candidate assumption", _candidateAssumption);
            ConfigureNumber(_peRoute, 256, 0, 1);
            _peRoute.Minimum = 1;
            _peApplicability.DropDownStyle = ComboBoxStyle.DropDownList;
            _peApplicability.Items.Add("applicable");
            _peApplicability.Items.Add("not_applicable");
            _peApplicability.Items.Add("unknown");
            _peApplicability.SelectedIndex = 0;
            _peApplicabilityBasis.Text = "Reviewed project applicability decision.";
            _peApplicabilityBasis.MaxLength = 512;
            _peDisconnectionBasis.DropDownStyle = ComboBoxStyle.DropDownList;
            _peDisconnectionBasis.Items.Add("reviewed_protective_device_time");
            _peDisconnectionBasis.Items.Add("reviewed_upstream_clearing_time");
            _peDisconnectionBasis.Items.Add("unknown");
            _peDisconnectionBasis.SelectedIndex = 0;
            _peDisconnectionReference.Text = "Reviewed protective-device clearing time.";
            _peDisconnectionReference.MaxLength = 512;
            ConfigureNumber(_peDisconnectionTime, 1000000000, 6, 0.2M);
            ConfigureNumber(_peConductorFactor, 1000000000, 6, 115);
            _peRuleTitle.Text = "AI-Engineer M20 PE acceptance reviewed rule input";
            _peRuleTitle.MaxLength = 256;
            _peRuleVersion.Text = "project-reviewed-2026-08-11";
            _peRuleVersion.MaxLength = 128;
            _peRuleLocator.Text = "project://electrical/rules/pe-adiabatic-acceptance";
            _peRuleLocator.MaxLength = 512;
            _peRuleSha256.Text =
                "bccee1183336a9d06cc142d74820c337851643b6d77b33f9ff2be2f532fe3a20";
            _peRuleSha256.MaxLength = 64;
            _peAssumption.Text = "Fault duration and conductor factor apply to this route.";
            _peAssumption.MaxLength = 512;
            AddInput(inputs, 38, "PE route number", _peRoute);
            AddInput(inputs, 39, "PE rule applicability", _peApplicability);
            AddInput(inputs, 40, "Applicability basis", _peApplicabilityBasis);
            AddInput(inputs, 41, "Disconnection basis", _peDisconnectionBasis);
            AddInput(inputs, 42, "Disconnection reference", _peDisconnectionReference);
            AddInput(inputs, 43, "Disconnection time (s)", _peDisconnectionTime);
            AddInput(inputs, 44, "Conductor factor k (A*sqrt(s)/mm2)", _peConductorFactor);
            AddInput(inputs, 45, "PE rule source title", _peRuleTitle);
            AddInput(inputs, 46, "PE rule source version", _peRuleVersion);
            AddInput(inputs, 47, "PE rule source locator", _peRuleLocator);
            AddInput(inputs, 48, "PE rule source SHA-256", _peRuleSha256);
            AddInput(inputs, 49, "PE assumption", _peAssumption);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            _refresh.Text = "Refresh context";
            _calculate.Text = "Calculate";
            _aggregate.Text = "Aggregate loads";
            _derateAmpacity.Text = "Apply derating";
            _shortCircuit.Text = "Calculate short circuit";
            _reviewCandidate.Text = "Review candidate";
            _sizePe.Text = "Size PE conductor";
            _clear.Text = "Clear";
            _refresh.AutoSize = _calculate.AutoSize = _aggregate.AutoSize =
                _derateAmpacity.AutoSize = _shortCircuit.AutoSize =
                _reviewCandidate.AutoSize = _sizePe.AutoSize = _clear.AutoSize = true;
            buttons.Controls.Add(_refresh);
            buttons.Controls.Add(_calculate);
            buttons.Controls.Add(_aggregate);
            buttons.Controls.Add(_derateAmpacity);
            buttons.Controls.Add(_shortCircuit);
            buttons.Controls.Add(_reviewCandidate);
            buttons.Controls.Add(_sizePe);
            buttons.Controls.Add(_clear);
            _result.Dock = DockStyle.Fill;
            _result.Multiline = true;
            _result.ReadOnly = true;
            _result.ScrollBars = ScrollBars.Both;
            _result.WordWrap = false;
            _status.AutoSize = true;
            _status.MaximumSize = new Size(320, 0);
            layout.Controls.Add(_context, 0, 0);
            layout.Controls.Add(buttons, 0, 1);
            layout.Controls.Add(inputs, 0, 2);
            layout.Controls.Add(_result, 0, 3);
            layout.Controls.Add(_status, 0, 4);
            Controls.Add(layout);
            _refresh.Click += delegate { RefreshContext(); };
            _calculate.Click += async delegate { await Calculate(); };
            _aggregate.Click += async delegate { await AggregateLoads(); };
            _derateAmpacity.Click += async delegate { await CalculateAmpacity(); };
            _shortCircuit.Click += async delegate { await CalculateShortCircuit(); };
            _reviewCandidate.Click += async delegate { await ReviewCandidate(); };
            _sizePe.Click += async delegate { await SizeProtectiveConductor(); };
            _clear.Click += delegate { _result.Clear(); _status.Text = "Cleared."; };
        }

        private static void AddInput(
            TableLayoutPanel panel, int row, string label, Control control)
        {
            panel.RowCount = Math.Max(panel.RowCount, row + 1);
            panel.Controls.Add(new Label { Text = label, AutoSize = true }, 0, row);
            control.Width = 150;
            panel.Controls.Add(control, 1, row);
        }

        private static void ConfigureNumber(
            NumericUpDown control, decimal maximum, int decimals, decimal value)
        {
            control.Minimum = 0;
            control.Maximum = maximum;
            control.DecimalPlaces = decimals;
            control.Value = value;
        }

        private void RefreshContext()
        {
            ContextEvidence evidence = ContextEvidenceStore.Get();
            if (evidence == null)
            {
                _context.Text = "No explicit AutoCAD context is available.";
                _calculate.Enabled = false;
                _aggregate.Enabled = false;
                _derateAmpacity.Enabled = false;
                _shortCircuit.Enabled = false;
                _reviewCandidate.Enabled = false;
                _sizePe.Enabled = false;
            }
            else
            {
                _context.Text = "Context: " + evidence.SourceMode + " | " +
                    evidence.EntityCount.ToString(CultureInfo.InvariantCulture) +
                    " entities | DBMOD=" + evidence.DatabaseModified.ToString(CultureInfo.InvariantCulture);
                _calculate.Enabled = !_busy;
                _aggregate.Enabled = !_busy;
                _derateAmpacity.Enabled = !_busy;
                _shortCircuit.Enabled = !_busy;
                _reviewCandidate.Enabled = !_busy;
                _sizePe.Enabled = !_busy;
            }
        }

        private async Task Calculate()
        {
            if (_busy) return;
            _busy = true;
            _calculate.Enabled = _aggregate.Enabled = _derateAmpacity.Enabled =
                _shortCircuit.Enabled = _reviewCandidate.Enabled = _sizePe.Enabled =
                _refresh.Enabled = _clear.Enabled = false;
            _status.Text = "Re-importing context and calculating locally...";
            try
            {
                ContextEvidence evidence = ContextEvidenceStore.Get();
                if (evidence == null)
                    throw new InvalidOperationException("Export an explicit AutoCAD context first.");
                Dictionary<string, object> takeoffRequest = ContextRequest(evidence);
                Dictionary<string, object> takeoffResponse = await Post(
                    "/v1/takeoff/reports", takeoffRequest);
                Dictionary<string, object> report = RequiredObject(takeoffResponse, "report");
                if (!RequiredBool(report, "is_complete") ||
                    RequiredBool(report, "drawing_mutation_performed"))
                    throw new InvalidOperationException(
                        "Calculation requires a complete, read-only recognized context.");
                List<string> loadIds = DeviceIds(report);
                List<string> routeIds = RouteIds(report);
                if (loadIds.Count == 0 || routeIds.Count == 0)
                    throw new InvalidOperationException(
                        "Calculation requires recognized devices and routes.");
                Dictionary<string, object> calculation = new Dictionary<string, object>();
                calculation["calculation_id"] = "calculation:autocad_live";
                calculation["calculation_version"] = "m20.lv_current_voltage_drop.v1";
                calculation["phase_system"] = (string)_phase.SelectedItem;
                calculation["load_output_power_watts"] = Decimal.ToDouble(_power.Value);
                calculation["demand_factor"] = Decimal.ToDouble(_demand.Value);
                calculation["nominal_voltage_volts"] = Decimal.ToDouble(_voltage.Value);
                calculation["power_factor"] = Decimal.ToDouble(_powerFactor.Value);
                calculation["efficiency"] = Decimal.ToDouble(_efficiency.Value);
                calculation["conductor_ac_resistance_ohm_per_km"] =
                    Decimal.ToDouble(_resistance.Value);
                calculation["conductor_reactance_ohm_per_km"] =
                    Decimal.ToDouble(_reactance.Value);
                calculation["load_object_ids"] = loadIds.ToArray();
                calculation["route_ids"] = routeIds.ToArray();
                calculation["reviewed_by"] = RequiredInput(_reviewedBy.Text, "reviewer");
                calculation["assumptions"] = new object[] {
                    RequiredInput(_assumption.Text, "assumption") };
                Dictionary<string, object> request = ContextRequest(evidence);
                request.Remove("policy");
                request["calculation"] = calculation;
                Dictionary<string, object> response = await Post(
                    "/v1/calculations/low-voltage", request);
                Dictionary<string, object> result = RequiredObject(response, "result");
                ValidateResult(result, evidence);
                ShowResult(result, loadIds.Count, routeIds.Count);
                _status.Text = "Deterministic result created; no compliance claim or drawing mutation.";
            }
            catch (System.Exception error)
            {
                _status.Text = "Request failed: " + SafeText(
                    error is InvalidOperationException ? error.Message : error.GetType().Name, 256);
            }
            finally
            {
                _busy = false;
                _refresh.Enabled = _clear.Enabled = true;
                RefreshContext();
            }
        }

        private async Task AggregateLoads()
        {
            if (_busy) return;
            _busy = true;
            _calculate.Enabled = _aggregate.Enabled = _derateAmpacity.Enabled =
                _shortCircuit.Enabled = _reviewCandidate.Enabled = _sizePe.Enabled =
                _refresh.Enabled = _clear.Enabled = false;
            _status.Text = "Re-importing context and aggregating reviewed loads locally...";
            try
            {
                ContextEvidence evidence = ContextEvidenceStore.Get();
                if (evidence == null)
                    throw new InvalidOperationException("Export an explicit AutoCAD context first.");
                Dictionary<string, object> takeoffResponse = await Post(
                    "/v1/takeoff/reports", ContextRequest(evidence));
                Dictionary<string, object> report = RequiredObject(takeoffResponse, "report");
                if (!RequiredBool(report, "is_complete") ||
                    RequiredBool(report, "drawing_mutation_performed"))
                    throw new InvalidOperationException(
                        "Aggregation requires a complete, read-only recognized context.");
                List<string> loadIds = DeviceIds(report);
                if (loadIds.Count == 0)
                    throw new InvalidOperationException("Aggregation requires recognized devices.");
                string[] phases = RequiredInput(_phaseAllocations.Text, "phase allocations")
                    .Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (phases.Length != loadIds.Count)
                    throw new InvalidOperationException(
                        "Load phases CSV must contain exactly one phase per recognized device.");
                List<object> allocations = new List<object>();
                for (int index = 0; index < loadIds.Count; index++)
                {
                    string phase = phases[index].Trim().ToLowerInvariant();
                    if (phase != "l1" && phase != "l2" && phase != "l3" &&
                        phase != "three_phase")
                        throw new InvalidOperationException(
                            "Each load phase must be l1, l2, l3, or three_phase.");
                    Dictionary<string, object> allocation = new Dictionary<string, object>();
                    allocation["load_object_id"] = loadIds[index];
                    allocation["connected_output_power_watts"] =
                        Decimal.ToDouble(_aggregatePower.Value);
                    allocation["demand_factor"] = Decimal.ToDouble(_aggregateDemand.Value);
                    allocation["phase"] = phase;
                    allocations.Add(allocation);
                }
                Dictionary<string, object> group = new Dictionary<string, object>();
                group["group_id"] = "group:autocad_live";
                group["member_load_object_ids"] = loadIds.ToArray();
                group["diversity_factor"] = Decimal.ToDouble(_diversity.Value);
                Dictionary<string, object> ruleSet = new Dictionary<string, object>();
                ruleSet["rule_set_id"] = "ruleset:autocad_live";
                ruleSet["rule_set_version"] = "m20.load_aggregation.v1";
                ruleSet["allocations"] = allocations.ToArray();
                ruleSet["diversity_groups"] = new object[] { group };
                ruleSet["reviewed_by"] = RequiredInput(_reviewedBy.Text, "reviewer");
                ruleSet["assumptions"] = new object[] {
                    RequiredInput(_assumption.Text, "assumption") };
                Dictionary<string, object> request = ContextRequest(evidence);
                request.Remove("policy");
                request["rule_set"] = ruleSet;
                Dictionary<string, object> response = await Post(
                    "/v1/calculations/load-aggregation", request);
                Dictionary<string, object> result = RequiredObject(response, "result");
                ValidateResult(result, evidence);
                ShowAggregationResult(result, loadIds.Count);
                _status.Text = "Load aggregation created; no code limit or drawing mutation.";
            }
            catch (System.Exception error)
            {
                _status.Text = "Request failed: " + SafeText(
                    error is InvalidOperationException ? error.Message : error.GetType().Name, 256);
            }
            finally
            {
                _busy = false;
                _refresh.Enabled = _clear.Enabled = true;
                RefreshContext();
            }
        }

        private void ShowAggregationResult(Dictionary<string, object> result, int loadCount)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Result SHA-256: " + RequiredString(result, "result_sha256"));
            text.AppendLine("Rule set: m20.load_aggregation.v1");
            text.AppendLine("Load objects: " + loadCount.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("Connected output: " + Number(result, "connected_output_power_watts") + " W");
            text.AppendLine("Demand before diversity: " + Number(result, "demand_before_diversity_watts") + " W");
            text.AppendLine("Demand after diversity: " + Number(result, "demand_after_diversity_watts") + " W");
            foreach (Dictionary<string, object> phase in RequiredObjects(result, "phase_results"))
                text.AppendLine(RequiredString(phase, "phase").ToUpperInvariant() +
                    " demand: " + Number(phase, "demand_output_power_watts") + " W | deviation " +
                    Number(phase, "deviation_from_average_percent") + " %");
            text.AppendLine("Maximum phase spread: " + Number(result, "phase_maximum_spread_watts") + " W");
            text.AppendLine("Maximum phase deviation: " + Number(result, "phase_maximum_deviation_percent") + " %");
            text.AppendLine("compliance_evaluation_performed=false");
            text.AppendLine("drawing_mutation_performed=false");
            text.AppendLine();
            foreach (string formula in RequiredStrings(result, "formulas"))
                text.AppendLine("FORMULA: " + formula);
            foreach (string warning in RequiredStrings(result, "warnings"))
                text.AppendLine("WARNING: " + warning);
            _result.Text = text.ToString();
        }

        private async Task CalculateAmpacity()
        {
            if (_busy) return;
            _busy = true;
            _calculate.Enabled = _aggregate.Enabled = _derateAmpacity.Enabled =
                _shortCircuit.Enabled = _reviewCandidate.Enabled = _sizePe.Enabled =
                _refresh.Enabled = _clear.Enabled = false;
            _status.Text = "Re-importing context and applying reviewed derating locally...";
            try
            {
                ContextEvidence evidence = ContextEvidenceStore.Get();
                if (evidence == null)
                    throw new InvalidOperationException("Export an explicit AutoCAD context first.");
                Dictionary<string, object> takeoffResponse = await Post(
                    "/v1/takeoff/reports", ContextRequest(evidence));
                Dictionary<string, object> report = RequiredObject(takeoffResponse, "report");
                if (!RequiredBool(report, "is_complete") ||
                    RequiredBool(report, "drawing_mutation_performed"))
                    throw new InvalidOperationException(
                        "Ampacity derating requires a complete, read-only recognized context.");
                List<string> routeIds = RouteIds(report);
                int routeIndex = Decimal.ToInt32(_ampacityRoute.Value) - 1;
                if (routeIndex < 0 || routeIndex >= routeIds.Count)
                    throw new InvalidOperationException(
                        "Ampacity route number exceeds the recognized route count.");
                string basis = RequiredInput(_ampacityBasis.Text, "ampacity basis");
                Dictionary<string, object> ambient = new Dictionary<string, object>();
                ambient["factor_id"] = "factor:ambient";
                ambient["label"] = "Ambient temperature";
                ambient["factor"] = Decimal.ToDouble(_ambientFactor.Value);
                ambient["basis_reference"] = basis + " Ambient factor.";
                Dictionary<string, object> grouping = new Dictionary<string, object>();
                grouping["factor_id"] = "factor:grouping";
                grouping["label"] = "Circuit grouping";
                grouping["factor"] = Decimal.ToDouble(_groupingFactor.Value);
                grouping["basis_reference"] = basis + " Grouping factor.";
                Dictionary<string, object> calculation = new Dictionary<string, object>();
                calculation["calculation_id"] = "calculation:ampacity_autocad_live";
                calculation["calculation_version"] = "m20.cable_ampacity_derating.v1";
                calculation["source_route_id"] = routeIds[routeIndex];
                calculation["cable_designation"] = RequiredInput(
                    _cableDesignation.Text, "cable designation");
                calculation["base_ampacity_amperes"] = Decimal.ToDouble(_baseAmpacity.Value);
                calculation["base_ampacity_basis_reference"] = basis + " Base ampacity.";
                calculation["derating_factors"] = new object[] { ambient, grouping };
                calculation["reviewed_by"] = RequiredInput(_reviewedBy.Text, "reviewer");
                calculation["assumptions"] = new object[] {
                    RequiredInput(_ampacityAssumption.Text, "ampacity assumption") };
                Dictionary<string, object> request = ContextRequest(evidence);
                request.Remove("policy");
                request["calculation"] = calculation;
                Dictionary<string, object> response = await Post(
                    "/v1/calculations/cable-ampacity", request);
                Dictionary<string, object> result = RequiredObject(response, "result");
                ValidateResult(result, evidence);
                if (RequiredBool(result, "cable_selection_performed") ||
                    RequiredBool(result, "protective_device_selection_performed"))
                    throw new InvalidOperationException(
                        "Ampacity result returned an unsupported selection claim.");
                ShowAmpacityResult(result, routeIndex + 1);
                _status.Text = "Reviewed derating chain created; no selection or compliance claim.";
            }
            catch (System.Exception error)
            {
                _status.Text = "Request failed: " + SafeText(
                    error is InvalidOperationException ? error.Message : error.GetType().Name, 256);
            }
            finally
            {
                _busy = false;
                _refresh.Enabled = _clear.Enabled = true;
                RefreshContext();
            }
        }

        private void ShowAmpacityResult(Dictionary<string, object> result, int routeNumber)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Result SHA-256: " + RequiredString(result, "result_sha256"));
            text.AppendLine("Calculation: m20.cable_ampacity_derating.v1");
            text.AppendLine("Source route number: " + routeNumber.ToString(CultureInfo.InvariantCulture));
            Dictionary<string, object> input = RequiredObject(result, "input");
            text.AppendLine("Cable: " + RequiredString(input, "cable_designation"));
            text.AppendLine("Base ampacity: " + Number(input, "base_ampacity_amperes") + " A");
            text.AppendLine("Combined derating factor: " + Number(result, "combined_derating_factor"));
            text.AppendLine("Derated ampacity: " + Number(result, "derated_ampacity_amperes") + " A");
            foreach (Dictionary<string, object> stage in RequiredObjects(result, "stages"))
                text.AppendLine("Stage " + Number(stage, "sequence") + ": " +
                    RequiredString(stage, "label") + " | " + Number(stage, "input_ampacity_amperes") +
                    " A x " + Number(stage, "factor") + " = " +
                    Number(stage, "output_ampacity_amperes") + " A");
            text.AppendLine("cable_selection_performed=false");
            text.AppendLine("protective_device_selection_performed=false");
            text.AppendLine("compliance_evaluation_performed=false");
            text.AppendLine("drawing_mutation_performed=false");
            text.AppendLine();
            text.AppendLine("FORMULA: " + RequiredString(result, "formula"));
            foreach (string warning in RequiredStrings(result, "warnings"))
                text.AppendLine("WARNING: " + warning);
            _result.Text = text.ToString();
        }

        private async Task CalculateShortCircuit()
        {
            if (_busy) return;
            _busy = true;
            _calculate.Enabled = _aggregate.Enabled = _derateAmpacity.Enabled =
                _shortCircuit.Enabled = _reviewCandidate.Enabled = _sizePe.Enabled =
                _refresh.Enabled = _clear.Enabled = false;
            _status.Text = "Re-importing context and calculating bounded fault current locally...";
            try
            {
                ContextEvidence evidence = ContextEvidenceStore.Get();
                if (evidence == null)
                    throw new InvalidOperationException("Export an explicit AutoCAD context first.");
                Dictionary<string, object> takeoffResponse = await Post(
                    "/v1/takeoff/reports", ContextRequest(evidence));
                Dictionary<string, object> report = RequiredObject(takeoffResponse, "report");
                if (!RequiredBool(report, "is_complete") ||
                    RequiredBool(report, "drawing_mutation_performed"))
                    throw new InvalidOperationException(
                        "Short-circuit calculation requires a complete read-only context.");
                List<string> routeIds = RouteIds(report);
                int routeIndex = Decimal.ToInt32(_shortCircuitRoute.Value) - 1;
                if (routeIndex < 0 || routeIndex >= routeIds.Count)
                    throw new InvalidOperationException(
                        "Fault route number exceeds the recognized route count.");
                Dictionary<string, object> calculation = new Dictionary<string, object>();
                calculation["calculation_id"] = "calculation:short_circuit_autocad_live";
                calculation["calculation_version"] =
                    "m20.short_circuit_source_equivalent.v1";
                calculation["source_route_id"] = routeIds[routeIndex];
                calculation["fault_arrangement"] = (string)_faultArrangement.SelectedItem;
                calculation["nominal_voltage_volts"] = Decimal.ToDouble(_faultVoltage.Value);
                calculation["voltage_multiplier"] = Decimal.ToDouble(_voltageMultiplier.Value);
                calculation["source_resistance_ohms"] = Decimal.ToDouble(_sourceResistance.Value);
                calculation["source_reactance_ohms"] = Decimal.ToDouble(_sourceReactance.Value);
                calculation["path_resistance_ohms"] = Decimal.ToDouble(_pathResistance.Value);
                calculation["path_reactance_ohms"] = Decimal.ToDouble(_pathReactance.Value);
                calculation["impedance_basis_reference"] = RequiredInput(
                    _faultBasis.Text, "impedance basis");
                calculation["reviewed_by"] = RequiredInput(_reviewedBy.Text, "reviewer");
                calculation["assumptions"] = new object[] {
                    RequiredInput(_faultAssumption.Text, "fault assumption") };
                Dictionary<string, object> request = ContextRequest(evidence);
                request.Remove("policy");
                request["calculation"] = calculation;
                Dictionary<string, object> response = await Post(
                    "/v1/calculations/short-circuit", request);
                Dictionary<string, object> result = RequiredObject(response, "result");
                ValidateResult(result, evidence);
                if (RequiredBool(result, "protective_device_adequacy_evaluated") ||
                    RequiredBool(result, "peak_current_calculated") ||
                    RequiredBool(result, "arc_flash_calculated"))
                    throw new InvalidOperationException(
                        "Short-circuit result returned an unsupported engineering claim.");
                ShowShortCircuitResult(result, routeIndex + 1);
                _status.Text = "Bounded RMS fault current created; no equipment/compliance claim.";
            }
            catch (System.Exception error)
            {
                _status.Text = "Request failed: " + SafeText(
                    error is InvalidOperationException ? error.Message : error.GetType().Name, 256);
            }
            finally
            {
                _busy = false;
                _refresh.Enabled = _clear.Enabled = true;
                RefreshContext();
            }
        }

        private void ShowShortCircuitResult(Dictionary<string, object> result, int routeNumber)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Result SHA-256: " + RequiredString(result, "result_sha256"));
            text.AppendLine("Calculation: m20.short_circuit_source_equivalent.v1");
            text.AppendLine("Source route number: " + routeNumber.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("Total R: " + Number(result, "total_resistance_ohms") + " ohm");
            text.AppendLine("Total X: " + Number(result, "total_reactance_ohms") + " ohm");
            text.AppendLine("Total |Z|: " + Number(result, "total_impedance_magnitude_ohms") + " ohm");
            text.AppendLine("Symmetrical RMS current: " +
                Number(result, "symmetrical_short_circuit_current_amperes") + " A");
            text.AppendLine("Apparent fault power: " +
                Number(result, "apparent_fault_power_volt_amperes") + " VA");
            text.AppendLine("protective_device_adequacy_evaluated=false");
            text.AppendLine("peak_current_calculated=false");
            text.AppendLine("arc_flash_calculated=false");
            text.AppendLine("compliance_evaluation_performed=false");
            text.AppendLine("drawing_mutation_performed=false");
            text.AppendLine();
            foreach (string formula in RequiredStrings(result, "formulas"))
                text.AppendLine("FORMULA: " + formula);
            foreach (string warning in RequiredStrings(result, "warnings"))
                text.AppendLine("WARNING: " + warning);
            _result.Text = text.ToString();
        }

        private async Task ReviewCandidate()
        {
            if (_busy) return;
            _busy = true;
            _calculate.Enabled = _aggregate.Enabled = _derateAmpacity.Enabled =
                _shortCircuit.Enabled = _reviewCandidate.Enabled = _sizePe.Enabled =
                _refresh.Enabled = _clear.Enabled = false;
            _status.Text = "Re-importing context and recalculating the reviewed candidate...";
            try
            {
                ContextEvidence evidence = ContextEvidenceStore.Get();
                if (evidence == null)
                    throw new InvalidOperationException("Export an explicit AutoCAD context first.");
                Dictionary<string, object> takeoffResponse = await Post(
                    "/v1/takeoff/reports", ContextRequest(evidence));
                Dictionary<string, object> report = RequiredObject(takeoffResponse, "report");
                if (!RequiredBool(report, "is_complete") ||
                    RequiredBool(report, "drawing_mutation_performed"))
                    throw new InvalidOperationException(
                        "Candidate review requires a complete read-only recognized context.");
                List<string> loadIds = DeviceIds(report);
                List<string> routeIds = RouteIds(report);
                if (loadIds.Count == 0 || routeIds.Count == 0)
                    throw new InvalidOperationException(
                        "Candidate review requires recognized devices and routes.");
                int ampacityRouteIndex = Decimal.ToInt32(_ampacityRoute.Value) - 1;
                int faultRouteIndex = Decimal.ToInt32(_shortCircuitRoute.Value) - 1;
                if (ampacityRouteIndex < 0 || ampacityRouteIndex >= routeIds.Count ||
                    faultRouteIndex < 0 || faultRouteIndex >= routeIds.Count)
                    throw new InvalidOperationException(
                        "Candidate route number exceeds the recognized route count.");
                if (ampacityRouteIndex != faultRouteIndex)
                    throw new InvalidOperationException(
                        "Ampacity and fault route numbers must identify the same candidate route.");

                string reviewer = RequiredInput(_reviewedBy.Text, "reviewer");
                Dictionary<string, object> lowVoltage = new Dictionary<string, object>();
                lowVoltage["calculation_id"] = "calculation:candidate_lv_autocad_live";
                lowVoltage["calculation_version"] = "m20.lv_current_voltage_drop.v1";
                lowVoltage["phase_system"] = (string)_phase.SelectedItem;
                lowVoltage["load_output_power_watts"] = Decimal.ToDouble(_power.Value);
                lowVoltage["demand_factor"] = Decimal.ToDouble(_demand.Value);
                lowVoltage["nominal_voltage_volts"] = Decimal.ToDouble(_voltage.Value);
                lowVoltage["power_factor"] = Decimal.ToDouble(_powerFactor.Value);
                lowVoltage["efficiency"] = Decimal.ToDouble(_efficiency.Value);
                lowVoltage["conductor_ac_resistance_ohm_per_km"] =
                    Decimal.ToDouble(_resistance.Value);
                lowVoltage["conductor_reactance_ohm_per_km"] =
                    Decimal.ToDouble(_reactance.Value);
                lowVoltage["load_object_ids"] = loadIds.ToArray();
                lowVoltage["route_ids"] = routeIds.ToArray();
                lowVoltage["reviewed_by"] = reviewer;
                lowVoltage["assumptions"] = new object[] {
                    RequiredInput(_assumption.Text, "assumption") };

                string ampacityBasis = RequiredInput(_ampacityBasis.Text, "ampacity basis");
                Dictionary<string, object> ambient = new Dictionary<string, object>();
                ambient["factor_id"] = "factor:ambient";
                ambient["label"] = "Ambient temperature";
                ambient["factor"] = Decimal.ToDouble(_ambientFactor.Value);
                ambient["basis_reference"] = ampacityBasis + " Ambient factor.";
                Dictionary<string, object> grouping = new Dictionary<string, object>();
                grouping["factor_id"] = "factor:grouping";
                grouping["label"] = "Circuit grouping";
                grouping["factor"] = Decimal.ToDouble(_groupingFactor.Value);
                grouping["basis_reference"] = ampacityBasis + " Grouping factor.";
                Dictionary<string, object> ampacity = new Dictionary<string, object>();
                ampacity["calculation_id"] = "calculation:candidate_ampacity_autocad_live";
                ampacity["calculation_version"] = "m20.cable_ampacity_derating.v1";
                ampacity["source_route_id"] = routeIds[ampacityRouteIndex];
                ampacity["cable_designation"] = RequiredInput(
                    _cableDesignation.Text, "cable designation");
                ampacity["base_ampacity_amperes"] = Decimal.ToDouble(_baseAmpacity.Value);
                ampacity["base_ampacity_basis_reference"] = ampacityBasis + " Base ampacity.";
                ampacity["derating_factors"] = new object[] { ambient, grouping };
                ampacity["reviewed_by"] = reviewer;
                ampacity["assumptions"] = new object[] {
                    RequiredInput(_ampacityAssumption.Text, "ampacity assumption") };

                Dictionary<string, object> shortCircuit = new Dictionary<string, object>();
                shortCircuit["calculation_id"] = "calculation:candidate_fault_autocad_live";
                shortCircuit["calculation_version"] =
                    "m20.short_circuit_source_equivalent.v1";
                shortCircuit["source_route_id"] = routeIds[faultRouteIndex];
                shortCircuit["fault_arrangement"] = (string)_faultArrangement.SelectedItem;
                shortCircuit["nominal_voltage_volts"] = Decimal.ToDouble(_faultVoltage.Value);
                shortCircuit["voltage_multiplier"] = Decimal.ToDouble(_voltageMultiplier.Value);
                shortCircuit["source_resistance_ohms"] = Decimal.ToDouble(_sourceResistance.Value);
                shortCircuit["source_reactance_ohms"] = Decimal.ToDouble(_sourceReactance.Value);
                shortCircuit["path_resistance_ohms"] = Decimal.ToDouble(_pathResistance.Value);
                shortCircuit["path_reactance_ohms"] = Decimal.ToDouble(_pathReactance.Value);
                shortCircuit["impedance_basis_reference"] = RequiredInput(
                    _faultBasis.Text, "impedance basis");
                shortCircuit["reviewed_by"] = reviewer;
                shortCircuit["assumptions"] = new object[] {
                    RequiredInput(_faultAssumption.Text, "fault assumption") };

                Dictionary<string, object> ruleSet = new Dictionary<string, object>();
                ruleSet["rule_set_id"] = "ruleset:candidate_autocad_live";
                ruleSet["rule_set_version"] = "m20.candidate_adequacy.v1";
                ruleSet["candidate_id"] = "candidate:autocad_live";
                ruleSet["protective_device_designation"] = RequiredInput(
                    _deviceDesignation.Text, "protective device designation");
                ruleSet["protective_device_rated_current_amperes"] =
                    Decimal.ToDouble(_deviceRating.Value);
                ruleSet["protective_device_breaking_capacity_amperes"] =
                    Decimal.ToDouble(_breakingCapacity.Value);
                ruleSet["required_ampacity_multiplier"] =
                    Decimal.ToDouble(_ampacityMultiplier.Value);
                ruleSet["maximum_voltage_drop_percent"] =
                    Decimal.ToDouble(_maximumVoltageDrop.Value);
                ruleSet["basis_reference"] = RequiredInput(
                    _candidateBasis.Text, "candidate rule basis");
                ruleSet["reviewed_by"] = reviewer;
                ruleSet["assumptions"] = new object[] {
                    RequiredInput(_candidateAssumption.Text, "candidate assumption") };

                Dictionary<string, object> request = ContextRequest(evidence);
                request.Remove("policy");
                request["low_voltage"] = lowVoltage;
                request["ampacity"] = ampacity;
                request["short_circuit"] = shortCircuit;
                request["rule_set"] = ruleSet;
                Dictionary<string, object> response = await Post(
                    "/v1/calculations/candidate-adequacy", request);
                Dictionary<string, object> result = RequiredObject(response, "result");
                ValidateResult(result, evidence);
                if (RequiredBool(result, "automatic_selection_performed"))
                    throw new InvalidOperationException(
                        "Candidate result returned an unsupported automatic-selection claim.");
                ShowCandidateResult(result, ampacityRouteIndex + 1);
                _status.Text = "Candidate reviewed against explicit inputs; no selection or compliance claim.";
            }
            catch (System.Exception error)
            {
                _status.Text = "Request failed: " + SafeText(
                    error is InvalidOperationException ? error.Message : error.GetType().Name, 256);
            }
            finally
            {
                _busy = false;
                _refresh.Enabled = _clear.Enabled = true;
                RefreshContext();
            }
        }

        private void ShowCandidateResult(Dictionary<string, object> result, int routeNumber)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Result SHA-256: " + RequiredString(result, "result_sha256"));
            text.AppendLine("Rule set: m20.candidate_adequacy.v1");
            text.AppendLine("Candidate route number: " +
                routeNumber.ToString(CultureInfo.InvariantCulture));
            Dictionary<string, object> ruleSet = RequiredObject(result, "rule_set");
            text.AppendLine("Protective device: " +
                RequiredString(ruleSet, "protective_device_designation"));
            text.AppendLine("Candidate adequate under reviewed rules: " +
                RequiredBool(result, "candidate_adequate_under_reviewed_rules")
                    .ToString().ToLowerInvariant());
            foreach (Dictionary<string, object> finding in RequiredObjects(result, "findings"))
                text.AppendLine(RequiredString(finding, "outcome").ToUpperInvariant() + " | " +
                    RequiredString(finding, "expression") + " | actual " +
                    Number(finding, "actual_value") + " " + RequiredString(finding, "unit") +
                    " | required " + Number(finding, "required_value") + " " +
                    RequiredString(finding, "unit"));
            text.AppendLine();
            text.AppendLine("Low-voltage SHA-256: " +
                RequiredString(result, "low_voltage_result_sha256"));
            text.AppendLine("Ampacity SHA-256: " +
                RequiredString(result, "ampacity_result_sha256"));
            text.AppendLine("Short-circuit SHA-256: " +
                RequiredString(result, "short_circuit_result_sha256"));
            text.AppendLine("automatic_selection_performed=false");
            text.AppendLine("compliance_evaluation_performed=false");
            text.AppendLine("drawing_mutation_performed=false");
            text.AppendLine();
            foreach (string warning in RequiredStrings(result, "warnings"))
                text.AppendLine("WARNING: " + warning);
            _result.Text = text.ToString();
        }

        private async Task SizeProtectiveConductor()
        {
            if (_busy) return;
            _busy = true;
            _calculate.Enabled = _aggregate.Enabled = _derateAmpacity.Enabled =
                _shortCircuit.Enabled = _reviewCandidate.Enabled = _sizePe.Enabled =
                _refresh.Enabled = _clear.Enabled = false;
            _status.Text = "Re-importing context and applying the reviewed PE rule...";
            try
            {
                ContextEvidence evidence = ContextEvidenceStore.Get();
                if (evidence == null)
                    throw new InvalidOperationException("Export an explicit AutoCAD context first.");
                Dictionary<string, object> takeoffResponse = await Post(
                    "/v1/takeoff/reports", ContextRequest(evidence));
                Dictionary<string, object> report = RequiredObject(takeoffResponse, "report");
                if (!RequiredBool(report, "is_complete") ||
                    RequiredBool(report, "drawing_mutation_performed"))
                    throw new InvalidOperationException(
                        "PE sizing requires a complete read-only recognized context.");
                List<string> routeIds = RouteIds(report);
                int routeIndex = Decimal.ToInt32(_peRoute.Value) - 1;
                if (routeIndex < 0 || routeIndex >= routeIds.Count)
                    throw new InvalidOperationException(
                        "PE route number exceeds the recognized route count.");
                string applicability = (string)_peApplicability.SelectedItem;
                string disconnectionBasis = (string)_peDisconnectionBasis.SelectedItem;
                bool applicable = String.Equals(
                    applicability, "applicable", StringComparison.Ordinal);
                if (applicable && String.Equals(
                        disconnectionBasis, "unknown", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Applicable PE sizing requires a reviewed disconnection basis.");
                if (!applicable && !String.Equals(
                        disconnectionBasis, "unknown", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Unknown or not-applicable PE sizing requires unknown disconnection basis.");

                string reviewer = RequiredInput(_reviewedBy.Text, "reviewer");
                Dictionary<string, object> ruleInput = new Dictionary<string, object>();
                ruleInput["calculation_id"] = "calculation:pe_autocad_live";
                ruleInput["calculation_version"] =
                    "m20.protective_conductor_adiabatic.v1";
                ruleInput["source_route_id"] = routeIds[routeIndex];
                ruleInput["applicability"] = applicability;
                ruleInput["applicability_basis_reference"] = RequiredInput(
                    _peApplicabilityBasis.Text, "PE applicability basis");
                ruleInput["disconnection_basis"] = disconnectionBasis;
                ruleInput["disconnection_basis_reference"] = RequiredInput(
                    _peDisconnectionReference.Text, "PE disconnection reference");
                ruleInput["disconnection_time_seconds"] = applicable
                    ? (object)Decimal.ToDouble(_peDisconnectionTime.Value) : null;
                ruleInput["conductor_factor_amperes_sqrt_second_per_square_millimeter"] =
                    applicable ? (object)Decimal.ToDouble(_peConductorFactor.Value) : null;
                ruleInput["rule_source_id"] = "source:pe_rule_autocad_live";
                ruleInput["rule_source_title"] = RequiredInput(
                    _peRuleTitle.Text, "PE rule source title");
                ruleInput["rule_source_version"] = RequiredInput(
                    _peRuleVersion.Text, "PE rule source version");
                ruleInput["rule_source_locator"] = RequiredInput(
                    _peRuleLocator.Text, "PE rule source locator");
                ruleInput["rule_source_sha256"] = RequiredInput(
                    _peRuleSha256.Text, "PE rule source SHA-256");
                ruleInput["reviewed_by"] = reviewer;
                ruleInput["assumptions"] = new object[] {
                    RequiredInput(_peAssumption.Text, "PE assumption") };

                Dictionary<string, object> shortCircuit = null;
                if (applicable)
                {
                    int faultRouteIndex = Decimal.ToInt32(_shortCircuitRoute.Value) - 1;
                    if (faultRouteIndex != routeIndex)
                        throw new InvalidOperationException(
                            "PE and fault route numbers must identify the same route.");
                    shortCircuit = new Dictionary<string, object>();
                    shortCircuit["calculation_id"] = "calculation:pe_fault_autocad_live";
                    shortCircuit["calculation_version"] =
                        "m20.short_circuit_source_equivalent.v1";
                    shortCircuit["source_route_id"] = routeIds[routeIndex];
                    shortCircuit["fault_arrangement"] =
                        (string)_faultArrangement.SelectedItem;
                    shortCircuit["nominal_voltage_volts"] =
                        Decimal.ToDouble(_faultVoltage.Value);
                    shortCircuit["voltage_multiplier"] =
                        Decimal.ToDouble(_voltageMultiplier.Value);
                    shortCircuit["source_resistance_ohms"] =
                        Decimal.ToDouble(_sourceResistance.Value);
                    shortCircuit["source_reactance_ohms"] =
                        Decimal.ToDouble(_sourceReactance.Value);
                    shortCircuit["path_resistance_ohms"] =
                        Decimal.ToDouble(_pathResistance.Value);
                    shortCircuit["path_reactance_ohms"] =
                        Decimal.ToDouble(_pathReactance.Value);
                    shortCircuit["impedance_basis_reference"] = RequiredInput(
                        _faultBasis.Text, "impedance basis");
                    shortCircuit["reviewed_by"] = reviewer;
                    shortCircuit["assumptions"] = new object[] {
                        RequiredInput(_faultAssumption.Text, "fault assumption") };
                }

                Dictionary<string, object> request = ContextRequest(evidence);
                request.Remove("policy");
                request["rule_input"] = ruleInput;
                request["short_circuit"] = shortCircuit;
                Dictionary<string, object> response = await Post(
                    "/v1/calculations/protective-conductor", request);
                Dictionary<string, object> result = RequiredObject(response, "result");
                ValidateResult(result, evidence);
                if (RequiredBool(result, "conductor_selection_performed"))
                    throw new InvalidOperationException(
                        "PE result returned an unsupported conductor-selection claim.");
                ShowProtectiveConductorResult(result, routeIndex + 1);
                _status.Text = "Reviewed PE outcome created; no selection or compliance claim.";
            }
            catch (System.Exception error)
            {
                _status.Text = "Request failed: " + SafeText(
                    error is InvalidOperationException ? error.Message : error.GetType().Name, 256);
            }
            finally
            {
                _busy = false;
                _refresh.Enabled = _clear.Enabled = true;
                RefreshContext();
            }
        }

        private void ShowProtectiveConductorResult(
            Dictionary<string, object> result, int routeNumber)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Result SHA-256: " + RequiredString(result, "result_sha256"));
            text.AppendLine("Calculation: m20.protective_conductor_adiabatic.v1");
            text.AppendLine("Source route number: " +
                routeNumber.ToString(CultureInfo.InvariantCulture));
            string outcome = RequiredString(result, "outcome");
            text.AppendLine("Outcome: " + outcome.ToUpperInvariant());
            Dictionary<string, object> input = RequiredObject(result, "input");
            text.AppendLine("Rule source: " + RequiredString(input, "rule_source_title"));
            text.AppendLine("Rule source version: " +
                RequiredString(input, "rule_source_version"));
            text.AppendLine("Rule source SHA-256: " +
                RequiredString(input, "rule_source_sha256"));
            if (String.Equals(outcome, "calculated", StringComparison.Ordinal))
            {
                text.AppendLine("Fault current: " + Number(result, "fault_current_amperes") + " A");
                text.AppendLine("Disconnection time: " +
                    Number(input, "disconnection_time_seconds") + " s");
                text.AppendLine("Conductor factor k: " + Number(input,
                    "conductor_factor_amperes_sqrt_second_per_square_millimeter") +
                    " A*sqrt(s)/mm2");
                text.AppendLine("Required adiabatic cross-section: " +
                    Number(result, "required_cross_section_square_millimeters") + " mm2");
                text.AppendLine("Short-circuit SHA-256: " +
                    RequiredString(result, "short_circuit_result_sha256"));
                text.AppendLine("FORMULA: " + RequiredString(result, "formula"));
            }
            text.AppendLine("conductor_selection_performed=false");
            text.AppendLine("compliance_evaluation_performed=false");
            text.AppendLine("drawing_mutation_performed=false");
            text.AppendLine();
            foreach (string warning in RequiredStrings(result, "warnings"))
                text.AppendLine("WARNING: " + warning);
            _result.Text = text.ToString();
        }

        private static Dictionary<string, object> ContextRequest(ContextEvidence evidence)
        {
            Dictionary<string, object> policy = new Dictionary<string, object>();
            policy["calculation_version"] = "m19.takeoff.v1";
            policy["vertical_allowance_per_route_meters"] = 0.0;
            policy["termination_allowance_per_end_meters"] = 0.0;
            policy["waste_percent"] = 0.0;
            policy["route_overrides"] = new object[0];
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["context_id"] = evidence.ContextId;
            request["context_evidence_sha256"] = evidence.Sha256;
            request["policy"] = policy;
            return request;
        }

        private static List<string> DeviceIds(Dictionary<string, object> report)
        {
            List<string> result = new List<string>();
            foreach (Dictionary<string, object> quantity in RequiredObjects(
                report, "device_quantities"))
                result.AddRange(RequiredStrings(quantity, "device_ids"));
            return result;
        }

        private static List<string> RouteIds(Dictionary<string, object> report)
        {
            List<string> result = new List<string>();
            foreach (Dictionary<string, object> quantity in RequiredObjects(
                report, "route_quantities"))
                result.Add(RequiredString(quantity, "route_id"));
            return result;
        }

        private static void ValidateResult(
            Dictionary<string, object> result, ContextEvidence evidence)
        {
            if (!String.Equals(RequiredString(result, "context_id"), evidence.ContextId,
                    StringComparison.Ordinal) ||
                !String.Equals(RequiredString(result, "context_evidence_sha256"), evidence.Sha256,
                    StringComparison.Ordinal) ||
                RequiredBool(result, "drawing_mutation_performed") ||
                RequiredBool(result, "compliance_evaluation_performed"))
                throw new InvalidOperationException(
                    "Calculation result returned unsafe or mismatched evidence.");
            RequiredString(result, "result_sha256");
        }

        private void ShowResult(
            Dictionary<string, object> result, int deviceCount, int routeCount)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Result SHA-256: " + RequiredString(result, "result_sha256"));
            text.AppendLine("Calculation: m20.lv_current_voltage_drop.v1");
            text.AppendLine("Load objects: " + deviceCount.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("Routes: " + routeCount.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("Route length: " + Number(result, "route_length_meters") + " m");
            text.AppendLine("Demand output: " + Number(result, "demand_output_power_watts") + " W");
            text.AppendLine("Calculated current: " + Number(result, "calculated_current_amperes") + " A");
            text.AppendLine("Voltage drop: " + Number(result, "voltage_drop_volts") + " V");
            text.AppendLine("Voltage drop: " + Number(result, "voltage_drop_percent") + " %");
            text.AppendLine("compliance_evaluation_performed=false");
            text.AppendLine("drawing_mutation_performed=false");
            text.AppendLine();
            foreach (Dictionary<string, object> formula in RequiredObjects(result, "formulas"))
                text.AppendLine(RequiredString(formula, "formula_id") + ": " +
                    RequiredString(formula, "expression"));
            text.AppendLine();
            foreach (string warning in RequiredStrings(result, "warnings"))
                text.AppendLine("WARNING: " + warning);
            _result.Text = text.ToString();
        }

        private static string Number(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw))
                throw new InvalidOperationException("Local data is missing " + name + ".");
            double number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number) || number < 0)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return number.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private async Task<Dictionary<string, object>> Post(
            string path, Dictionary<string, object> value)
        {
            string token = Environment.GetEnvironmentVariable("AI_ENGINEER_API_TOKEN");
            if (String.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("AI_ENGINEER_API_TOKEN is not configured for AutoCAD.");
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(
                    _serializer.Serialize(value), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string content = await response.Content.ReadAsStringAsync();
                    if (Encoding.UTF8.GetByteCount(content) > MaximumJsonBytes)
                        throw new InvalidOperationException("Local API response exceeded its byte limit.");
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Local API returned HTTP " +
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                    Dictionary<string, object> document =
                        _serializer.DeserializeObject(content) as Dictionary<string, object>;
                    if (document == null)
                        throw new InvalidOperationException("Local API response is invalid.");
                    return document;
                }
            }
        }

        private static Dictionary<string, object> RequiredObject(
            Dictionary<string, object> value, string name)
        {
            object raw;
            Dictionary<string, object> result;
            if (value == null || !value.TryGetValue(name, out raw) ||
                (result = raw as Dictionary<string, object>) == null)
                throw new InvalidOperationException("Local data is missing " + name + ".");
            return result;
        }

        private static string RequiredString(Dictionary<string, object> value, string name)
        {
            object raw;
            string result;
            if (value == null || !value.TryGetValue(name, out raw) ||
                String.IsNullOrEmpty(result = raw as string) || result.Length > MaximumJsonBytes)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return result;
        }

        private static bool RequiredBool(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw) || !(raw is bool))
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            return (bool)raw;
        }

        private static List<Dictionary<string, object>> RequiredObjects(
            Dictionary<string, object> value, string name)
        {
            object raw;
            IEnumerable items;
            if (!value.TryGetValue(name, out raw) ||
                (items = raw as IEnumerable) == null || raw is string)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (object item in items)
            {
                Dictionary<string, object> document = item as Dictionary<string, object>;
                if (document == null)
                    throw new InvalidOperationException("Local data has invalid " + name + ".");
                result.Add(document);
                if (result.Count > 8192)
                    throw new InvalidOperationException("Local data exceeded its item limit.");
            }
            return result;
        }

        private static List<string> RequiredStrings(
            Dictionary<string, object> value, string name)
        {
            object raw;
            IEnumerable items;
            if (!value.TryGetValue(name, out raw) ||
                (items = raw as IEnumerable) == null || raw is string)
                throw new InvalidOperationException("Local data has invalid " + name + ".");
            List<string> result = new List<string>();
            foreach (object item in items)
            {
                string text = item as string;
                if (String.IsNullOrEmpty(text) || text.Length > 512)
                    throw new InvalidOperationException("Local data has invalid " + name + ".");
                result.Add(text);
                if (result.Count > 8192)
                    throw new InvalidOperationException("Local data exceeded its item limit.");
            }
            return result;
        }

        private static string RequiredInput(string value, string name)
        {
            if (String.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Enter a reviewed " + name + ".");
            return value.Trim();
        }

        private static string SafeText(string value, int maximum)
        {
            if (String.IsNullOrEmpty(value)) return "error";
            StringBuilder result = new StringBuilder();
            foreach (char character in value)
            {
                if (result.Length >= maximum) break;
                result.Append(Char.IsControl(character) ? ' ' : character);
            }
            return result.ToString();
        }
    }
}
