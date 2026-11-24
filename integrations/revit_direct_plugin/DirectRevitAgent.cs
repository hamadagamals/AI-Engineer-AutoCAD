using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AIEngineer.Revit.Direct
{
    internal sealed class RevitAgentReply
    {
        public string Text = "";
        public List<string> ExecutedTools = new List<string>();
        public bool HallucinationGuardTriggered;
    }

    internal sealed class DirectRevitAgentSession
    {
        private const int MaxToolIterations = 12;
        private static readonly HttpClient _http = CreateHttp();
        private readonly object _historyLock = new object();
        private readonly List<Dictionary<string, string>> _history = new List<Dictionary<string, string>>();
        private int _historyEpoch = 0;

        private static HttpClient CreateHttp()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
            try { ServicePointManager.Expect100Continue = false; } catch { }
            HttpClient c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(120);
            return c;
        }

        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _history.Clear();
                _historyEpoch++;
            }
        }

        private List<Dictionary<string, string>> SnapshotHistory(out int epoch)
        {
            lock (_historyLock)
            {
                epoch = _historyEpoch;
                List<Dictionary<string, string>> copy = new List<Dictionary<string, string>>(_history.Count);
                for (int i = 0; i < _history.Count; i++)
                    copy.Add(new Dictionary<string, string>(_history[i]));
                return copy;
            }
        }

        private void AppendHistoryIfSameEpoch(int expectedEpoch, string userMsg, string assistantMsg)
        {
            lock (_historyLock)
            {
                if (_historyEpoch != expectedEpoch) return;
                _history.Add(new Dictionary<string, string> { { "role", "user" }, { "text", userMsg } });
                _history.Add(new Dictionary<string, string> { { "role", "assistant" }, { "text", assistantMsg } });
                if (_history.Count > 24) _history.RemoveRange(0, _history.Count - 24);
            }
        }

        private static bool IsActionOrCalcTool(string toolName)
        {
            string n = (toolName ?? "").Trim().ToLowerInvariant();
            return n == "distribute_lighting" || n == "export_dialux_stf" || n == "place_electrical_family" ||
                   n == "place_room_outlets" || n == "create_electrical_circuits" || n == "modify_parameters" ||
                   n == "delete_elements" || n == "quantity_takeoff_mep" || n == "calculate_lv_electrical" ||
                   n == "export_to_excel" || n == "approve_or_rollback_preview" || n == "undo_last_action";
        }

        /// <summary>
        /// Anti-hallucination guard: detects if the model claims it performed Revit mutations,
        /// calculations, or file exports when zero action/calc/export tools succeeded in this turn
        /// (read-only inspect_model / query_elements do NOT bypass this guard).
        /// </summary>
        internal static bool DetectUngroundedClaim(string replyText, List<string> succeededTools)
        {
            if (string.IsNullOrWhiteSpace(replyText)) return false;
            if (succeededTools != null)
            {
                for (int i = 0; i < succeededTools.Count; i++)
                    if (IsActionOrCalcTool(succeededTools[i])) return false;
            }
            string low = replyText.ToLowerInvariant();
            string[] claimPatterns = new string[]
            {
                "تم توزيع", "تم تركيب", "تم إنشاء الدوائر", "تم تعديل", "تم حذف", "تم التصدير إلى إكسيل",
                "تم تصدير ملف dialux", "تم حفظ ملف", "تم الحساب بنجاح", "تم الاعتماد",
                "تم التصدير", "تم الرسم", "تم التنفيذ",
                "i have placed", "i have distributed", "i have created the circuits", "exported to excel",
                "exported to dialux", ".xlsx has been created", ".stf has been created",
                "i placed", "i distributed", "i created", "i exported", "i deleted"
            };
            for (int i = 0; i < claimPatterns.Length; i++)
            {
                if (low.Contains(claimPatterns[i])) return true;
            }
            return false;
        }

        private static bool SupportsTemperature(string model)
        {
            string m = (model ?? "").Trim().ToLowerInvariant();
            if (m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4") ||
                m.StartsWith("gpt-5") || m.Contains("codex"))
                return false;
            return true;
        }

        public async Task<RevitAgentReply> SendAsync(
            RevitDirectAiConfig cfg,
            string provider,
            string model,
            string userMessage,
            bool isArabic,
            Func<string, Dictionary<string, object>, Task<Dictionary<string, object>>> executeToolOnRevitThread,
            Action<string> onProgress)
        {
            if (cfg == null) cfg = RevitDirectAiConfig.Load();
            string normProv = (provider ?? RevitDirectAiConfig.ProviderGemini).Trim().ToLowerInvariant();
            string apiKey = cfg.GetApiKey(normProv);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(isArabic
                    ? "مفتاح API غير مضبوط لـ " + normProv + ". اضغط [🔑 API] لإدخاله."
                    : "API key is missing for " + normProv + ". Click [🔑 API] to configure it.");

            string useModel = string.IsNullOrWhiteSpace(model) ? cfg.GetModel(normProv) : model.Trim();
            string sysPrompt = BuildSystemPrompt(isArabic);
            List<Dictionary<string, object>> tools = BuildToolCatalog();
            int startEpoch;
            List<Dictionary<string, string>> hist = SnapshotHistory(out startEpoch);

            RevitAgentReply reply;
            if (normProv == RevitDirectAiConfig.ProviderClaude)
                reply = await RunClaudeLoopAsync(apiKey, useModel, sysPrompt, tools, hist, userMessage, isArabic, executeToolOnRevitThread, onProgress);
            else if (normProv == RevitDirectAiConfig.ProviderOpenAI)
            {
                bool useResponses = string.Equals(cfg.OpenAIApiMode, "responses", StringComparison.OrdinalIgnoreCase) ||
                                    useModel.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0;
                if (useResponses)
                    reply = await RunOpenAiResponsesLoopAsync(apiKey, cfg.OpenAIBaseUrl, useModel, sysPrompt, tools, hist, userMessage, isArabic, executeToolOnRevitThread, onProgress);
                else
                    reply = await RunOpenAiChatLoopAsync(apiKey, cfg.OpenAIBaseUrl, useModel, sysPrompt, tools, hist, userMessage, isArabic, executeToolOnRevitThread, onProgress);
            }
            else
                reply = await RunGeminiLoopAsync(apiKey, useModel, sysPrompt, tools, hist, userMessage, isArabic, executeToolOnRevitThread, onProgress);

            AppendHistoryIfSameEpoch(startEpoch, userMessage, reply.Text);
            return reply;
        }

        private async Task<RevitAgentReply> RunGeminiLoopAsync(
            string apiKey, string model, string sysPrompt, List<Dictionary<string, object>> tools,
            List<Dictionary<string, string>> hist, string userMessage, bool isArabic,
            Func<string, Dictionary<string, object>, Task<Dictionary<string, object>>> runTool,
            Action<string> onProgress)
        {
            List<object> contents = new List<object>();
            for (int i = 0; i < hist.Count; i++)
            {
                contents.Add(new Dictionary<string, object>
                {
                    { "role", hist[i]["role"] == "assistant" ? "model" : "user" },
                    { "parts", new object[] { new Dictionary<string, object> { { "text", hist[i]["text"] } } } }
                });
            }
            contents.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "parts", new object[] { new Dictionary<string, object> { { "text", userMessage } } } }
            });

            List<string> succeededTools = new List<string>();
            int guardRetries = 0;
            string url = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent";
            Dictionary<string, string> headers = new Dictionary<string, string> { { "x-goog-api-key", apiKey.Trim() } };

            for (int iter = 0; iter < MaxToolIterations; iter++)
            {
                Dictionary<string, object> body = new Dictionary<string, object>
                {
                    { "systemInstruction", new Dictionary<string, object> { { "parts", new object[] { new Dictionary<string, object> { { "text", sysPrompt } } } } } },
                    { "contents", contents },
                    { "tools", new object[] { new Dictionary<string, object> { { "functionDeclarations", tools } } } },
                    { "generationConfig", new Dictionary<string, object> { { "temperature", 0.1 } } }
                };

                Dictionary<string, object> doc = await PostJsonAsync(url, headers, body);
                object[] cands = GetArr(doc, "candidates");
                if (cands == null || cands.Length == 0)
                    return new RevitAgentReply { Text = isArabic ? "لم يُرجع Gemini استجابة." : "Gemini returned no candidates.", ExecutedTools = succeededTools };

                Dictionary<string, object> c0 = cands[0] as Dictionary<string, object>;
                Dictionary<string, object> modelContent = GetDict(c0, "content");
                object[] parts = GetArr(modelContent, "parts");
                if (parts == null)
                    return new RevitAgentReply { Text = "", ExecutedTools = succeededTools };

                List<Dictionary<string, object>> calls = new List<Dictionary<string, object>>();
                StringBuilder textSb = new StringBuilder();
                for (int p = 0; p < parts.Length; p++)
                {
                    Dictionary<string, object> pd = parts[p] as Dictionary<string, object>;
                    if (pd == null) continue;
                    Dictionary<string, object> fc = GetDict(pd, "functionCall");
                    if (fc != null) calls.Add(fc);
                    else if (pd.ContainsKey("text") && pd["text"] != null && !(pd.ContainsKey("thought") && pd["thought"] is bool && (bool)pd["thought"]))
                        textSb.Append(pd["text"].ToString());
                }

                if (calls.Count == 0)
                {
                    string replyText = textSb.ToString().Trim();
                    if (DetectUngroundedClaim(replyText, succeededTools) && guardRetries < 1 && iter + 1 < MaxToolIterations)
                    {
                        guardRetries++;
                        contents.Add(modelContent);
                        contents.Add(new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "parts", new object[] { new Dictionary<string, object> { { "text", "[SYSTEM ANTI-HALLUCINATION GUARD]: You claimed an action/calculation/export succeeded, but NO matching action tool returned ok=true! Call the exact Revit tool now OR honestly report what is needed." } } } }
                        });
                        continue;
                    }
                    return new RevitAgentReply { Text = replyText, ExecutedTools = succeededTools, HallucinationGuardTriggered = guardRetries > 0 };
                }

                contents.Add(modelContent);
                List<object> responseParts = new List<object>();
                for (int i = 0; i < calls.Count; i++)
                {
                    string tName = GetString(calls[i], "name");
                    Dictionary<string, object> tArgs = GetDict(calls[i], "args") ?? new Dictionary<string, object>();
                    if (onProgress != null) onProgress((isArabic ? "جارٍ تنفيذ أداة Revit: " : "Executing Revit tool: ") + tName + "...");
                    Dictionary<string, object> res = await runTool(tName, tArgs);
                    if (res == null) res = RevitToolExecutor.Err("Tool returned null response.");
                    if (res.ContainsKey("ok") && res["ok"] is bool && (bool)res["ok"])
                        succeededTools.Add(tName);
                    responseParts.Add(new Dictionary<string, object>
                    {
                        { "functionResponse", new Dictionary<string, object> { { "name", tName }, { "response", res } } }
                    });
                }
                contents.Add(new Dictionary<string, object> { { "role", "user" }, { "parts", responseParts } });
            }

            return new RevitAgentReply
            {
                Text = isArabic ? "وصل الموديل للحد الأقصى لجولات الأدوات." : "Reached maximum tool iterations.",
                ExecutedTools = succeededTools
            };
        }

        private async Task<RevitAgentReply> RunClaudeLoopAsync(
            string apiKey, string model, string sysPrompt, List<Dictionary<string, object>> tools,
            List<Dictionary<string, string>> hist, string userMessage, bool isArabic,
            Func<string, Dictionary<string, object>, Task<Dictionary<string, object>>> runTool,
            Action<string> onProgress)
        {
            List<object> claudeTools = new List<object>();
            for (int i = 0; i < tools.Count; i++)
            {
                claudeTools.Add(new Dictionary<string, object>
                {
                    { "name", tools[i]["name"] },
                    { "description", tools[i]["description"] },
                    { "input_schema", tools[i]["parameters"] }
                });
            }

            List<object> messages = new List<object>();
            for (int i = 0; i < hist.Count; i++)
                messages.Add(new Dictionary<string, object> { { "role", hist[i]["role"] }, { "content", hist[i]["text"] } });
            messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", userMessage } });

            List<string> succeededTools = new List<string>();
            int guardRetries = 0;
            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                { "x-api-key", apiKey.Trim() },
                { "anthropic-version", "2023-06-01" }
            };

            for (int iter = 0; iter < MaxToolIterations; iter++)
            {
                Dictionary<string, object> body = new Dictionary<string, object>
                {
                    { "model", model },
                    { "max_tokens", 4096 },
                    { "system", sysPrompt },
                    { "tools", claudeTools },
                    { "messages", messages }
                };
                Dictionary<string, object> doc = await PostJsonAsync("https://api.anthropic.com/v1/messages", headers, body);
                object[] blocks = GetArr(doc, "content");
                if (blocks == null) return new RevitAgentReply { Text = "", ExecutedTools = succeededTools };

                List<Dictionary<string, object>> toolUses = new List<Dictionary<string, object>>();
                StringBuilder sb = new StringBuilder();
                for (int b = 0; b < blocks.Length; b++)
                {
                    Dictionary<string, object> bd = blocks[b] as Dictionary<string, object>;
                    if (bd == null) continue;
                    string bType = GetString(bd, "type");
                    if (bType == "tool_use") toolUses.Add(bd);
                    else if (bType == "text" && bd.ContainsKey("text") && bd["text"] != null) sb.Append(bd["text"].ToString());
                }

                if (toolUses.Count == 0)
                {
                    string replyText = sb.ToString().Trim();
                    if (DetectUngroundedClaim(replyText, succeededTools) && guardRetries < 1 && iter + 1 < MaxToolIterations)
                    {
                        guardRetries++;
                        messages.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", blocks } });
                        messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "[SYSTEM ANTI-HALLUCINATION GUARD]: Do not claim actions without calling the tool. Call the Revit tool now or state clearly what is missing." } });
                        continue;
                    }
                    return new RevitAgentReply { Text = replyText, ExecutedTools = succeededTools, HallucinationGuardTriggered = guardRetries > 0 };
                }

                messages.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", blocks } });
                List<object> toolResults = new List<object>();
                for (int i = 0; i < toolUses.Count; i++)
                {
                    string id = GetString(toolUses[i], "id");
                    string tName = GetString(toolUses[i], "name");
                    Dictionary<string, object> tArgs = GetDict(toolUses[i], "input") ?? new Dictionary<string, object>();
                    if (onProgress != null) onProgress((isArabic ? "جارٍ تنفيذ أداة Revit: " : "Executing Revit tool: ") + tName + "...");
                    Dictionary<string, object> res = await runTool(tName, tArgs);
                    if (res == null) res = RevitToolExecutor.Err("Tool returned null response.");
                    if (res.ContainsKey("ok") && res["ok"] is bool && (bool)res["ok"]) succeededTools.Add(tName);
                    toolResults.Add(new Dictionary<string, object>
                    {
                        { "type", "tool_result" },
                        { "tool_use_id", id },
                        { "content", SimpleJson.Serialize(res) }
                    });
                }
                messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", toolResults } });
            }
            return new RevitAgentReply { Text = isArabic ? "وصل Claude للحد الأقصى للجولات." : "Claude reached maximum iterations.", ExecutedTools = succeededTools };
        }

        private async Task<RevitAgentReply> RunOpenAiResponsesLoopAsync(
            string apiKey, string baseUrl, string model, string sysPrompt, List<Dictionary<string, object>> tools,
            List<Dictionary<string, string>> hist, string userMessage, bool isArabic,
            Func<string, Dictionary<string, object>, Task<Dictionary<string, object>>> runTool,
            Action<string> onProgress)
        {
            string b = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim().TrimEnd('/');
            string url = b + "/responses";
            List<object> oaiTools = new List<object>();
            for (int i = 0; i < tools.Count; i++)
            {
                oaiTools.Add(new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "name", tools[i]["name"] },
                    { "description", tools[i]["description"] },
                    { "parameters", tools[i]["parameters"] }
                });
            }

            List<object> input = new List<object>();
            for (int i = 0; i < hist.Count; i++)
                input.Add(new Dictionary<string, object> { { "role", hist[i]["role"] }, { "content", hist[i]["text"] } });
            input.Add(new Dictionary<string, object> { { "role", "user" }, { "content", userMessage } });

            List<string> succeededTools = new List<string>();
            int guardRetries = 0;
            Dictionary<string, string> headers = new Dictionary<string, string> { { "Authorization", "Bearer " + apiKey.Trim() } };

            for (int iter = 0; iter < MaxToolIterations; iter++)
            {
                Dictionary<string, object> body = new Dictionary<string, object>
                {
                    { "model", model },
                    { "instructions", sysPrompt },
                    { "input", input },
                    { "tools", oaiTools }
                };
                if (SupportsTemperature(model)) body["temperature"] = 0.1;

                Dictionary<string, object> doc = await PostJsonAsync(url, headers, body);
                object[] output = GetArr(doc, "output");
                if (output == null) return new RevitAgentReply { Text = GetString(doc, "output_text"), ExecutedTools = succeededTools };

                List<Dictionary<string, object>> reasoningItems = new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> fnCalls = new List<Dictionary<string, object>>();
                StringBuilder textSb = new StringBuilder();
                for (int i = 0; i < output.Length; i++)
                {
                    Dictionary<string, object> item = output[i] as Dictionary<string, object>;
                    if (item == null) continue;
                    string itemType = GetString(item, "type");
                    if (itemType == "reasoning") reasoningItems.Add(item);
                    else if (itemType == "function_call") fnCalls.Add(item);
                    else if (itemType == "message")
                    {
                        object[] parts = GetArr(item, "content");
                        if (parts != null)
                        {
                            for (int p = 0; p < parts.Length; p++)
                            {
                                Dictionary<string, object> pd = parts[p] as Dictionary<string, object>;
                                if (pd != null && pd.ContainsKey("text") && pd["text"] != null) textSb.Append(pd["text"].ToString());
                            }
                        }
                    }
                }

                if (fnCalls.Count == 0)
                {
                    string replyText = textSb.Length > 0 ? textSb.ToString().Trim() : GetString(doc, "output_text").Trim();
                    if (DetectUngroundedClaim(replyText, succeededTools) && guardRetries < 1 && iter + 1 < MaxToolIterations)
                    {
                        guardRetries++;
                        input.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", replyText } });
                        input.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "[SYSTEM ANTI-HALLUCINATION GUARD]: Call the real Revit tool before claiming any action or calculation." } });
                        continue;
                    }
                    return new RevitAgentReply { Text = replyText, ExecutedTools = succeededTools, HallucinationGuardTriggered = guardRetries > 0 };
                }

                for (int i = 0; i < reasoningItems.Count; i++)
                    input.Add(reasoningItems[i]);

                for (int i = 0; i < fnCalls.Count; i++)
                {
                    input.Add(fnCalls[i]);
                    string callId = GetString(fnCalls[i], "call_id");
                    string tName = GetString(fnCalls[i], "name");
                    Dictionary<string, object> tArgs;
                    string parseErr;
                    if (!TryParseArgsJson(GetString(fnCalls[i], "arguments"), out tArgs, out parseErr))
                    {
                        input.Add(new Dictionary<string, object>
                        {
                            { "type", "function_call_output" },
                            { "call_id", callId },
                            { "output", SimpleJson.Serialize(RevitToolExecutor.Err("Invalid JSON arguments: " + parseErr)) }
                        });
                        continue;
                    }
                    if (onProgress != null) onProgress((isArabic ? "جارٍ تنفيذ أداة Revit: " : "Executing Revit tool: ") + tName + "...");
                    Dictionary<string, object> res = await runTool(tName, tArgs);
                    if (res == null) res = RevitToolExecutor.Err("Tool returned null response.");
                    if (res.ContainsKey("ok") && res["ok"] is bool && (bool)res["ok"]) succeededTools.Add(tName);
                    input.Add(new Dictionary<string, object>
                    {
                        { "type", "function_call_output" },
                        { "call_id", callId },
                        { "output", SimpleJson.Serialize(res) }
                    });
                }
            }
            return new RevitAgentReply { Text = isArabic ? "وصل OpenAI للحد الأقصى للجولات." : "OpenAI reached maximum iterations.", ExecutedTools = succeededTools };
        }

        private async Task<RevitAgentReply> RunOpenAiChatLoopAsync(
            string apiKey, string baseUrl, string model, string sysPrompt, List<Dictionary<string, object>> tools,
            List<Dictionary<string, string>> hist, string userMessage, bool isArabic,
            Func<string, Dictionary<string, object>, Task<Dictionary<string, object>>> runTool,
            Action<string> onProgress)
        {
            string b = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim().TrimEnd('/');
            string url = b + "/chat/completions";
            List<object> oaiTools = new List<object>();
            for (int i = 0; i < tools.Count; i++)
                oaiTools.Add(new Dictionary<string, object> { { "type", "function" }, { "function", tools[i] } });

            List<object> messages = new List<object>();
            messages.Add(new Dictionary<string, object> { { "role", "system" }, { "content", sysPrompt } });
            for (int i = 0; i < hist.Count; i++)
                messages.Add(new Dictionary<string, object> { { "role", hist[i]["role"] }, { "content", hist[i]["text"] } });
            messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", userMessage } });

            List<string> succeededTools = new List<string>();
            int guardRetries = 0;
            Dictionary<string, string> headers = new Dictionary<string, string> { { "Authorization", "Bearer " + apiKey.Trim() } };

            for (int iter = 0; iter < MaxToolIterations; iter++)
            {
                Dictionary<string, object> body = new Dictionary<string, object>
                {
                    { "model", model },
                    { "messages", messages },
                    { "tools", oaiTools }
                };
                if (SupportsTemperature(model)) body["temperature"] = 0.1;

                Dictionary<string, object> doc = await PostJsonAsync(url, headers, body);
                object[] choices = GetArr(doc, "choices");
                if (choices == null || choices.Length == 0) return new RevitAgentReply { Text = "", ExecutedTools = succeededTools };
                Dictionary<string, object> msg = GetDict(choices[0] as Dictionary<string, object>, "message");
                if (msg == null) return new RevitAgentReply { Text = "", ExecutedTools = succeededTools };

                object[] tCalls = GetArr(msg, "tool_calls");
                if (tCalls == null || tCalls.Length == 0)
                {
                    string replyText = GetString(msg, "content").Trim();
                    if (DetectUngroundedClaim(replyText, succeededTools) && guardRetries < 1 && iter + 1 < MaxToolIterations)
                    {
                        guardRetries++;
                        messages.Add(msg);
                        messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "[SYSTEM ANTI-HALLUCINATION GUARD]: Call the real Revit tool before claiming any action or calculation." } });
                        continue;
                    }
                    return new RevitAgentReply { Text = replyText, ExecutedTools = succeededTools, HallucinationGuardTriggered = guardRetries > 0 };
                }

                messages.Add(msg);
                for (int i = 0; i < tCalls.Length; i++)
                {
                    Dictionary<string, object> tc = tCalls[i] as Dictionary<string, object>;
                    if (tc == null) continue;
                    string id = GetString(tc, "id");
                    Dictionary<string, object> fn = GetDict(tc, "function");
                    string tName = GetString(fn, "name");
                    Dictionary<string, object> tArgs;
                    string parseErr;
                    if (!TryParseArgsJson(GetString(fn, "arguments"), out tArgs, out parseErr))
                    {
                        messages.Add(new Dictionary<string, object>
                        {
                            { "role", "tool" }, { "tool_call_id", id },
                            { "content", SimpleJson.Serialize(RevitToolExecutor.Err("Invalid JSON arguments: " + parseErr)) }
                        });
                        continue;
                    }
                    if (onProgress != null) onProgress((isArabic ? "جارٍ تنفيذ أداة Revit: " : "Executing Revit tool: ") + tName + "...");
                    Dictionary<string, object> res = await runTool(tName, tArgs);
                    if (res == null) res = RevitToolExecutor.Err("Tool returned null response.");
                    if (res.ContainsKey("ok") && res["ok"] is bool && (bool)res["ok"]) succeededTools.Add(tName);
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "tool" },
                        { "tool_call_id", id },
                        { "content", SimpleJson.Serialize(res) }
                    });
                }
            }
            return new RevitAgentReply { Text = isArabic ? "وصل OpenAI للحد الأقصى للجولات." : "OpenAI reached maximum iterations.", ExecutedTools = succeededTools };
        }

        private static bool TryParseArgsJson(string raw, out Dictionary<string, object> parsed, out string error)
        {
            parsed = new Dictionary<string, object>();
            error = null;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            try
            {
                Dictionary<string, object> d = SimpleJson.DeserializeObject(raw);
                if (d != null) parsed = d;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v)) ? (v as Dictionary<string, object>) : null;
        }

        private static object[] GetArr(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v)) ? (v as object[]) : null;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v) && v != null) ? v.ToString() : "";
        }

        private static async Task<Dictionary<string, object>> PostJsonAsync(string url, Dictionary<string, string> headers, Dictionary<string, object> body)
        {
            string payload = SimpleJson.Serialize(body);
            int[] delays = new int[] { 700, 1600 };
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    if (headers != null)
                        foreach (KeyValuePair<string, string> kv in headers)
                            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        int code = (int)resp.StatusCode;
                        if (!resp.IsSuccessStatusCode)
                        {
                            if ((code == 429 || code == 500 || code == 502 || code == 503 || code == 504) && attempt < 2)
                            {
                                await Task.Delay(delays[attempt]).ConfigureAwait(false);
                                continue;
                            }
                            string snippet = text.Length > 500 ? text.Substring(0, 500) : text;
                            throw new InvalidOperationException("API HTTP " + code + ": " + snippet);
                        }
                        Dictionary<string, object> parsed = SimpleJson.DeserializeObject(text);
                        if (parsed == null) throw new InvalidOperationException("API returned non-object JSON.");
                        return parsed;
                    }
                }
            }
            throw new InvalidOperationException("API retries exhausted.");
        }

        private static string BuildSystemPrompt(bool isArabic)
        {
            return
                "You are AI-Engineer, a native multi-version Autodesk Revit BIM/MEP Electrical Engineering Copilot with direct tool access.\n" +
                "STRICT ANTI-HALLUCINATION & ENGINEERING PRECISION RULES:\n" +
                "1. NEVER invent ElementIds, Family names, Room names, Level names, counts, coordinates, or engineering numbers. ALWAYS call `inspect_model` or `query_elements` FIRST to read the real Revit model.\n" +
                "2. NEVER do mental math for cable sizing, circuit breakers, voltage drop, PE, short-circuit, lighting Lux, or BOQ quantities. ALWAYS call the deterministic tools (`calculate_lv_electrical`, `distribute_lighting`, `quantity_takeoff_mep`).\n" +
                "3. NEVER claim you placed, modified, circuited, deleted, calculated, or exported anything unless the matching tool returned `ok: true` in this turn. If a tool returns `ok: false` (e.g., family not loaded or hosted), report the exact error and list the real `available_*` types returned.\n" +
                "4. All newly placed elements are highlighted in Red review override by default (`preview_red: true`). When the engineer says 'تمام' / 'approve', call `approve_or_rollback_preview(action: \"approve\")`. When they say 'تراجع' / 'undo', call `undo_last_action(steps: 1)`.\n" +
                (isArabic
                    ? "5. Respond concisely in clear Arabic with exact verified numbers, ElementIds, and file paths from tool results."
                    : "5. Respond concisely in clear English with exact verified numbers, ElementIds, and file paths from tool results.");
        }

        internal static List<Dictionary<string, object>> BuildToolCatalog()
        {
            List<Dictionary<string, object>> t = new List<Dictionary<string, object>>();
            t.Add(Tool("inspect_model", "Inspect the active Revit model (levels, native & linked rooms/spaces, loaded electrical families/types, panels, circuits, selection).", Obj(new Dictionary<string, object> {
                { "include_rooms", Prop("boolean", "Include sample of native & linked rooms/spaces (default true)") },
                { "include_panels", Prop("boolean", "Include electrical equipment panels list (default true)") }
            })));
            t.Add(Tool("query_elements", "Query real Revit elements by category, level, family/type substring, or mark.", Obj(new Dictionary<string, object> {
                { "category", Prop("string", "lighting_fixtures, electrical_fixtures, electrical_equipment, electrical_circuits, conduits, cable_trays, wires, rooms, spaces, walls, ceilings") },
                { "level_name", Prop("string", "Optional level name filter") },
                { "name_contains", Prop("string", "Optional family/type substring filter") },
                { "mark", Prop("string", "Optional Mark filter") },
                { "limit", Prop("integer", "Max elements to return (default 100)") }
            })));
            t.Add(Tool("select_and_zoom", "Select real ElementIds on screen in Revit and zoom to them.", Obj(new Dictionary<string, object> {
                { "element_ids", ArrProp("integer", "Array of real ElementIds from inspect_model or query_elements") },
                { "zoom", Prop("boolean", "Zoom view to selected elements (default true)") }
            }, new string[] { "element_ids" })));
            t.Add(Tool("distribute_lighting", "Deterministic room-by-room lighting calculation (ECP/EN12464/CIBSE) & symmetrical fixture placement in Revit (supports native & linked arch rooms) + optional Excel & DIALux .stf export.", Obj(new Dictionary<string, object> {
                { "level_name", Prop("string", "Optional level filter") },
                { "room_name", Prop("string", "Optional room/space filter") },
                { "standard", Prop("string", "ECP (default), EN12464, or CIBSE") },
                { "target_lux", Prop("number", "Optional explicit target Lux override") },
                { "fixture_lumens", Prop("number", "Luminaire flux in Lumens (default 3600)") },
                { "fixture_watts", Prop("number", "Luminaire power in Watts (default 36)") },
                { "mounting_height_m", Prop("number", "Mounting height above floor in meters") },
                { "family_type", Prop("string", "Loaded lighting family/type name substring") },
                { "ies_path", Prop("string", "Optional real manufacturer .ies/.ldt file path for DIALux STF") },
                { "place_fixtures", Prop("boolean", "Place fixtures in Revit model (default true)") },
                { "preview_red", Prop("boolean", "Highlight placed fixtures in Red for review (default true)") },
                { "export_excel", Prop("boolean", "Export lighting calculation schedule to Excel (.xlsx)") },
                { "export_stf", Prop("boolean", "Export rooms & fixtures to DIALux .stf") }
            })));
            t.Add(Tool("export_dialux_stf", "Export Revit rooms/spaces (native & linked) and existing lighting fixtures in meters to a DIALux .stf file.", Obj(new Dictionary<string, object> {
                { "level_name", Prop("string", "Optional level filter") },
                { "room_name", Prop("string", "Optional room filter") },
                { "stf_path", Prop("string", "Optional destination .stf path") },
                { "ies_path", Prop("string", "Optional real manufacturer .ies/.ldt photometry file path") },
                { "fixture_lumens", Prop("number", "Default luminaire lumens (3600)") },
                { "fixture_watts", Prop("number", "Default luminaire watts (36)") }
            })));
            t.Add(Tool("place_electrical_family", "Place any loaded electrical panel, outlet, switch, or fixture at exact coordinates in meters.", Obj(new Dictionary<string, object> {
                { "category", Prop("string", "electrical_equipment, electrical_fixtures, or lighting_fixtures") },
                { "family_type", Prop("string", "Loaded family/type name substring") },
                { "level_name", Prop("string", "Target level name") },
                { "x_m", Prop("number", "X coordinate in meters") },
                { "y_m", Prop("number", "Y coordinate in meters") },
                { "elevation_m", Prop("number", "Mounting height above level in meters") },
                { "rotation_deg", Prop("number", "Rotation angle in degrees") },
                { "mark", Prop("string", "Element Mark") },
                { "panel_name", Prop("string", "Panel Name (for distribution boards)") },
                { "preview_red", Prop("boolean", "Highlight in Red for review (default true)") }
            }, new string[] { "x_m", "y_m" })));
            t.Add(Tool("place_room_outlets", "Distribute electrical sockets/outlets along room perimeters at specified mounting height and spacing.", Obj(new Dictionary<string, object> {
                { "level_name", Prop("string", "Optional level filter") },
                { "room_name", Prop("string", "Optional room filter") },
                { "family_type", Prop("string", "Loaded socket/outlet family/type substring") },
                { "mounting_height_m", Prop("number", "Height above floor in meters (default 0.30)") },
                { "spacing_m", Prop("number", "Wall spacing in meters (default 3.5)") },
                { "max_per_room", Prop("integer", "Max outlets per room (default 6)") },
                { "preview_red", Prop("boolean", "Highlight in Red for review (default true)") }
            })));
            t.Add(Tool("create_electrical_circuits", "Group real electrical element_ids into Revit Power Circuits and assign them to an existing Distribution Panel.", Obj(new Dictionary<string, object> {
                { "element_ids", ArrProp("integer", "Array of real ElementIds") },
                { "panel_name", Prop("string", "Target panel name") },
                { "panel_id", Prop("integer", "Optional target panel ElementId") },
                { "max_devices_per_circuit", Prop("integer", "Max devices per circuit (default 10)") }
            }, new string[] { "element_ids" })));
            t.Add(Tool("modify_parameters", "Batch-update a writable parameter (Mark, Comments, Panel, elevation_m, etc.) on specified ElementIds with Undo support.", Obj(new Dictionary<string, object> {
                { "element_ids", ArrProp("integer", "Array of real ElementIds") },
                { "parameter_name", Prop("string", "Parameter name (e.g. Mark, Comments, Panel, elevation_m)") },
                { "value", Prop("string", "New value") }
            }, new string[] { "element_ids", "parameter_name", "value" })));
            t.Add(Tool("delete_elements", "Delete specified ElementIds safely inside a Revit Transaction.", Obj(new Dictionary<string, object> {
                { "element_ids", ArrProp("integer", "Array of real ElementIds") }
            }, new string[] { "element_ids" })));
            t.Add(Tool("quantity_takeoff_mep", "Run deterministic BIM MEP Quantity Takeoff (BOQ: device counts, conduit & cable tray lengths in meters + waste %, room areas) with optional Excel export.", Obj(new Dictionary<string, object> {
                { "waste_percent", Prop("number", "Waste allowance % (default 10)") },
                { "export_excel", Prop("boolean", "Export formatted RTL Excel (.xlsx)") },
                { "excel_path", Prop("string", "Optional destination .xlsx path") }
            })));
            t.Add(Tool("calculate_lv_electrical", "Deterministic IEC 60364 / ECP LV calculator: design current Ib, breaker In/Icu, derating Ca*Cg, Cu/Al XLPE/PVC cable sizing + parallel runs, AC voltage drop %, PE size, short-circuit Isc + adiabatic check. Supports single load, fixture_count x fixture_watt/fixture_va, batch circuits[], OR `from_model_panel` to pull real Revit panel circuits directly!", Obj(new Dictionary<string, object> {
                { "from_model_panel", Prop("string", "Optional Revit panel name (or '*' for all) to automatically calculate every PowerCircuit on that panel") },
                { "name", Prop("string", "Circuit name") },
                { "load_type", Prop("string", "power, lighting (defaults 1P 220V), socket (defaults 1P 220V), motor, or feeder") },
                { "phases", Prop("integer", "1 or 3 (defaults 1 for lighting/socket, 3 otherwise)") },
                { "voltage_v", Prop("number", "Nominal voltage (220/230 for 1P, 380/400 for 3P)") },
                { "load_kw", Prop("number", "Load in kW") },
                { "load_kva", Prop("number", "Load in kVA") },
                { "load_hp", Prop("number", "Motor HP") },
                { "load_amps", Prop("number", "Load current in Amps") },
                { "fixture_count", Prop("number", "Optional luminaire/fixture count (used with fixture_watt or fixture_va)") },
                { "fixture_watt", Prop("number", "Optional Watts per luminaire/fixture (e.g. 28, 40)") },
                { "fixture_va", Prop("number", "Optional VA per luminaire/fixture") },
                { "length_m", Prop("number", "Cable route length in meters") },
                { "material", Prop("string", "Cu (default) or Al") },
                { "insulation", Prop("string", "XLPE (default) or PVC") },
                { "install_method", Prop("string", "tray, conduit, buried, or free_air") },
                { "ambient_temp_c", Prop("number", "Ambient temp C (default 40)") },
                { "grouped_circuits", Prop("integer", "Grouped circuits count (default 1)") },
                { "max_vd_percent", Prop("number", "Max voltage drop % (default 3 lighting / 5 power)") },
                { "source_isc_ka", Prop("number", "Optional upstream fault current in kA") },
                { "transformer_kva", Prop("number", "Optional supply transformer kVA") },
                { "transformer_uk_percent", Prop("number", "Optional transformer uk% (default 6)") },
                { "circuits", ArrObjProp("Optional batch array of circuit objects", new Dictionary<string, object> {
                    { "name", Prop("string", "Circuit name") },
                    { "load_type", Prop("string", "lighting, socket, power, motor, or feeder") },
                    { "load_kw", Prop("number", "Load in kW") },
                    { "load_kva", Prop("number", "Load in kVA") },
                    { "load_hp", Prop("number", "Motor HP") },
                    { "load_amps", Prop("number", "Load current in Amps") },
                    { "fixture_count", Prop("number", "Luminaire/fixture count") },
                    { "fixture_watt", Prop("number", "Watts per fixture") },
                    { "fixture_va", Prop("number", "VA per fixture") },
                    { "phases", Prop("integer", "1 or 3") },
                    { "length_m", Prop("number", "Route length in meters") }
                }) },
                { "export_excel", Prop("boolean", "Export RTL Excel (.xlsx)") }
            })));
            t.Add(Tool("export_to_excel", "Export a Revit Schedule (by schedule_name) or full MEP BOQ to RTL Excel (.xlsx).", Obj(new Dictionary<string, object> {
                { "schedule_name", Prop("string", "Optional Revit ViewSchedule name") },
                { "excel_path", Prop("string", "Optional destination .xlsx path") }
            })));
            t.Add(Tool("approve_or_rollback_preview", "Approve Red review elements (clears Red overrides) OR rollback last preview.", Obj(new Dictionary<string, object> {
                { "action", Prop("string", "approve or rollback") }
            }, new string[] { "action" })));
            t.Add(Tool("undo_last_action", "Undo last AI-Engineer Revit action(s) or list_history.", Obj(new Dictionary<string, object> {
                { "action", Prop("string", "undo (default) or list_history") },
                { "steps", Prop("integer", "Number of steps to undo (default 1)") }
            })));
            return t;
        }

        private static Dictionary<string, object> Tool(string name, string desc, Dictionary<string, object> parameters)
        {
            return new Dictionary<string, object> { { "name", name }, { "description", desc }, { "parameters", parameters } };
        }

        private static Dictionary<string, object> Obj(Dictionary<string, object> props, string[] req = null)
        {
            Dictionary<string, object> d = new Dictionary<string, object> { { "type", "object" }, { "properties", props } };
            if (req != null && req.Length > 0) d["required"] = req;
            return d;
        }

        private static Dictionary<string, object> Prop(string type, string desc)
        {
            return new Dictionary<string, object> { { "type", type }, { "description", desc } };
        }

        private static Dictionary<string, object> ArrProp(string itemType, string desc)
        {
            return new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", desc },
                { "items", new Dictionary<string, object> { { "type", itemType } } }
            };
        }

        private static Dictionary<string, object> ArrObjProp(string desc, Dictionary<string, object> itemProps)
        {
            return new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", desc },
                { "items", Obj(itemProps) }
            };
        }
    }
}
