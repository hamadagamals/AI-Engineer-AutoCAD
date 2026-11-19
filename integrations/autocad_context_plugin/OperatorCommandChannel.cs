using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using Forms = System.Windows.Forms;

namespace AIEngineer.AutoCAD
{
    internal static class OperatorCommandChannel
    {
        private const string RequestVersion = "autocad.operator_command_request.v1";
        private const string ResultVersion = "autocad.operator_command_result.v1";
        private static Forms.Timer _timer;
        private static bool _busy;
        private static DateTime _lastStatusAt = DateTime.MinValue;
        private static string _cachedStatusPath, _cachedStatusSha;
        private static long _cachedStatusLength, _cachedStatusWriteTicks;

        public static void Start()
        {
            if (_timer == null)
            {
                _timer = new Forms.Timer();
                _timer.Interval = 500;
                _timer.Tick += Poll;
            }
            _timer.Start();
        }

        public static void PublishNow()
        {
            Start();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                throw new InvalidOperationException(
                    "Operator status pulse requires one active document.");
            string workspace = LoadWorkspaceRoot();
            string root = Path.GetFullPath(Path.Combine(
                workspace, ".ai-engineer", "autocad", "operator-channel"));
            PublishDocumentStatus(document, root, true);
        }

        public static bool TryPublishNow()
        {
            try
            {
                PublishNow();
                return true;
            }
            catch (System.Exception error)
            {
                RecordPulseFailure(error);
                return false;
            }
        }

        public static void RecordPulseFailure(System.Exception error)
        {
            try
            {
                string directory = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                string message = error.Message ?? String.Empty;
                if (message.Length > 256) message = message.Substring(0, 256);
                Dictionary<string, object> value = new Dictionary<string, object>
                {
                    {"schema_version", 1},
                    {"error_type", error.GetType().Name},
                    {"message", message},
                    {"drawing_mutation_performed", false}
                };
                WriteAtomicReplace(Path.Combine(
                    directory, "operator-pulse-error.json"), value);
            }
            catch { }
        }

        public static void Stop()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer.Tick -= Poll;
            _timer.Dispose();
            _timer = null;
        }

        private static void Poll(object sender, EventArgs arguments)
        {
            if (_busy) return;
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null || DbmodCommandActive() != 0) return;
            string workspace;
            try { workspace = LoadWorkspaceRoot(); }
            catch { return; }
            string root = Path.GetFullPath(Path.Combine(
                workspace, ".ai-engineer", "autocad", "operator-channel"));
            try { PublishDocumentStatus(document, root, false); }
            catch { return; }
            string inbox = Path.Combine(root, "inbox");
            Directory.CreateDirectory(inbox);
            string[] candidates = Directory.GetFiles(inbox, "*.json", SearchOption.TopDirectoryOnly);
            if (candidates.Length == 0) return;
            Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
            _busy = true;
            try { Process(document, root, candidates[0]); }
            catch (System.Exception error)
            {
                document.Editor.WriteMessage(
                    "\nAI-Engineer operator channel failed safely: " + SafeMessage(error) + "\n");
            }
            finally { _busy = false; }
        }

        private static void Process(Document document, string root, string requestPath)
        {
            if ((File.GetAttributes(requestPath) & FileAttributes.ReparsePoint) != 0) return;
            byte[] requestBytes = File.ReadAllBytes(requestPath);
            if (requestBytes.Length == 0 || requestBytes.Length > 8192) return;
            Dictionary<string, object> request = ParseRequest(requestBytes);
            if (!String.Equals(RequiredString(request, "document_id"), DocumentId(document),
                StringComparison.Ordinal)) return;
            string expectedSha = RequiredString(request, "source_drawing_sha256");
            if (!String.Equals(expectedSha, FileSha256(document.Name), StringComparison.Ordinal)) return;

            string processing = Path.Combine(root, "processing");
            string processed = Path.Combine(root, "processed");
            string outbox = Path.Combine(root, "outbox");
            Directory.CreateDirectory(processing);
            Directory.CreateDirectory(processed);
            Directory.CreateDirectory(outbox);
            string claimed = Path.Combine(processing, Path.GetFileName(requestPath));
            File.Move(requestPath, claimed);
            string requestId = RequiredString(request, "request_id");
            string requestSha = Sha256(requestBytes);
            Dictionary<string, object> result;
            try { result = Execute(document, root, request, requestSha); }
            catch (OperationCanceledException)
            {
                result = BaseResult(document, request, requestSha, "cancelled");
                AddEmptyContext(result);
                result["error"] = null;
                result["drawing_mutation_performed"] = false;
            }
            catch (System.Exception error)
            {
                result = BaseResult(document, request, requestSha, "error");
                AddEmptyContext(result);
                result["error"] = SafeMessage(error);
                result["drawing_mutation_performed"] = false;
            }
            WriteAtomic(Path.Combine(outbox, requestId + ".json"), result);
            RetainCancellation(root, requestId);
            File.Move(claimed, Path.Combine(processed, Path.GetFileName(claimed)));
        }

        private static Dictionary<string, object> Execute(
            Document document, string root, Dictionary<string, object> request, string requestSha)
        {
            int before = Dbmod();
            if ((before & ~(8 | 16)) != 0)
                throw new InvalidOperationException(
                    "Operator channel requires clean or presentation-only DBMOD (8/16).");
            string action = RequiredString(request, "requested_action");
            ContextEvidence evidence = null;
            if (action == "capture_drawing_context" || action == "capture_drawing_index" ||
                action == "capture_drawing_delta")
            {
                using (DocumentLock documentLock = document.LockDocument())
                    evidence = action == "capture_drawing_index"
                        ? ContextCommands.ExportActiveDrawingIndexForChannel(
                            document,
                            delegate { return IsCancellationRequested(
                                root, RequiredString(request, "request_id"), requestSha); })
                        : action == "capture_drawing_delta"
                        ? ContextCommands.ExportActiveDrawingDeltaForChannel(
                            document,
                            delegate { return IsCancellationRequested(
                                root, RequiredString(request, "request_id"), requestSha); })
                        : ContextCommands.ExportActiveDrawingForChannel(
                            document,
                            delegate { return IsCancellationRequested(
                                root, RequiredString(request, "request_id"), requestSha); });
                if (evidence == null)
                    throw new InvalidOperationException("Drawing read-only export did not complete.");
            }
            else if (action == "show_palette")
            {
                new PaletteCommands().ShowPalette();
            }
            else if (action != "get_document_status")
            {
                throw new InvalidOperationException("Operator command is not allowlisted.");
            }
            int after = Dbmod();
            if ((after & ~(8 | 16)) != 0)
                throw new InvalidOperationException("Operator command changed persistent DBMOD bits.");

            Dictionary<string, object> result = BaseResult(document, request, requestSha, "completed");
            result["database_modified_before"] = before;
            result["database_modified_after"] = after;
            result["context_id"] = evidence == null ? null : evidence.ContextId;
            result["context_sha256"] = evidence == null ? null : evidence.Sha256;
            result["entity_count"] = evidence == null ? (object)null : evidence.EntityCount;
            result["duration_milliseconds"] = evidence == null ? (object)null : evidence.DurationMilliseconds;
            result["drawing_mutation_performed"] = false;
            result["error"] = null;
            return result;
        }

        private static Dictionary<string, object> ParseRequest(byte[] raw)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> value = serializer.Deserialize<Dictionary<string, object>>(
                Encoding.UTF8.GetString(raw));
            string[] expected = new string[] {
                "schema_version", "request_version", "request_id", "created_at", "expires_at",
                "document_id", "source_drawing_sha256", "requested_action",
                "drawing_mutation_allowed"
            };
            if (value == null || value.Count != expected.Length)
                throw new InvalidOperationException("Operator request schema is invalid.");
            foreach (string key in expected)
                if (!value.ContainsKey(key))
                    throw new InvalidOperationException("Operator request schema is invalid.");
            string action = RequiredString(value, "requested_action");
            if (Convert.ToInt32(value["schema_version"], CultureInfo.InvariantCulture) != 1 ||
                RequiredString(value, "request_version") != RequestVersion ||
                (action != "capture_drawing_context" && action != "capture_drawing_index" &&
                 action != "capture_drawing_delta" &&
                 action != "get_document_status" && action != "show_palette") ||
                Convert.ToBoolean(value["drawing_mutation_allowed"], CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Operator request capability is unsupported.");
            if (!Hex(RequiredString(value, "request_id"), 32) ||
                !Hex(RequiredString(value, "source_drawing_sha256"), 64))
                throw new InvalidOperationException("Operator request identity is invalid.");
            Guid documentId;
            if (!Guid.TryParseExact(RequiredString(value, "document_id"), "D", out documentId) ||
                documentId == Guid.Empty)
                throw new InvalidOperationException("Operator document identity is invalid.");
            DateTime created, expires;
            DateTimeStyles style = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
            if (!DateTime.TryParse(RequiredString(value, "created_at"), CultureInfo.InvariantCulture,
                    style, out created) ||
                !DateTime.TryParse(RequiredString(value, "expires_at"), CultureInfo.InvariantCulture,
                    style, out expires) || created >= expires || expires > created.AddMinutes(10) ||
                created > DateTime.UtcNow.AddMinutes(1) || expires <= DateTime.UtcNow)
                throw new InvalidOperationException("Operator request is expired or exceeds 10 minutes.");
            return value;
        }

        private static Dictionary<string, object> BaseResult(
            Document document, Dictionary<string, object> request, string requestSha, string status)
        {
            return new Dictionary<string, object>
            {
                {"schema_version", 1}, {"result_version", ResultVersion},
                {"result_id", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)},
                {"request_id", RequiredString(request, "request_id")},
                {"request_sha256", requestSha},
                {"completed_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)},
                {"document_id", DocumentId(document)},
                {"drawing_name", Path.GetFileName(document.Name)},
                {"source_drawing_sha256", FileSha256(document.Name)},
                {"requested_action", RequiredString(request, "requested_action")},
                {"status", status}
            };
        }

        private static void AddEmptyContext(Dictionary<string, object> result)
        {
            foreach (string key in new string[] {
                "database_modified_before", "database_modified_after", "context_id",
                "context_sha256", "entity_count", "duration_milliseconds"
            }) result[key] = null;
        }

        private static bool IsCancellationRequested(
            string root, string requestId, string requestSha)
        {
            string path = Path.Combine(root, "cancel", requestId + ".json");
            if (!File.Exists(path)) return false;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Operator cancellation cannot be a link.");
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length == 0 || raw.Length > 2048)
                throw new InvalidOperationException("Operator cancellation size is invalid.");
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> value = serializer.Deserialize<Dictionary<string, object>>(
                Encoding.UTF8.GetString(raw));
            string[] expected = new string[] {
                "schema_version", "cancel_version", "request_id", "request_sha256",
                "requested_at"
            };
            if (value == null || value.Count != expected.Length)
                throw new InvalidOperationException("Operator cancellation schema is invalid.");
            foreach (string key in expected)
                if (!value.ContainsKey(key))
                    throw new InvalidOperationException("Operator cancellation schema is invalid.");
            if (Convert.ToInt32(value["schema_version"], CultureInfo.InvariantCulture) != 1 ||
                RequiredString(value, "cancel_version") != "autocad.operator_cancel_request.v1" ||
                RequiredString(value, "request_id") != requestId ||
                RequiredString(value, "request_sha256") != requestSha)
                throw new InvalidOperationException("Operator cancellation binding is invalid.");
            DateTime requestedAt;
            DateTimeStyles style = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
            if (!DateTime.TryParse(RequiredString(value, "requested_at"),
                    CultureInfo.InvariantCulture, style, out requestedAt) ||
                requestedAt > DateTime.UtcNow.AddMinutes(1) ||
                requestedAt < DateTime.UtcNow.AddMinutes(-10))
                throw new InvalidOperationException("Operator cancellation timestamp is invalid.");
            return true;
        }

        private static void RetainCancellation(string root, string requestId)
        {
            string source = Path.Combine(root, "cancel", requestId + ".json");
            if (!File.Exists(source)) return;
            string retained = Path.Combine(root, "processed-cancellations");
            Directory.CreateDirectory(retained);
            File.Move(source, Path.Combine(retained, requestId + ".json"));
        }

        private static string LoadWorkspaceRoot()
        {
            string settingsPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "settings.json");
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> value = serializer.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(settingsPath, Encoding.UTF8));
            return Path.GetFullPath(RequiredString(value, "workspace_root"));
        }

        private static void PublishDocumentStatus(
            Document document, string root, bool force)
        {
            if (!force && (DateTime.UtcNow - _lastStatusAt).TotalSeconds < 2) return;
            string drawingPath = Path.GetFullPath(document.Name);
            FileInfo drawing = new FileInfo(drawingPath);
            if (!drawing.Exists) return;
            if (!String.Equals(_cachedStatusPath, drawingPath, StringComparison.OrdinalIgnoreCase) ||
                _cachedStatusLength != drawing.Length ||
                _cachedStatusWriteTicks != drawing.LastWriteTimeUtc.Ticks)
            {
                _cachedStatusPath = drawingPath;
                _cachedStatusLength = drawing.Length;
                _cachedStatusWriteTicks = drawing.LastWriteTimeUtc.Ticks;
                _cachedStatusSha = FileSha256(drawingPath);
            }
            int databaseModified = Dbmod();
            Dictionary<string, object> status = new Dictionary<string, object>
            {
                {"schema_version", 1},
                {"status_version", "autocad.active_document_status.v1"},
                {"observed_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)},
                {"document_id", DocumentId(document)},
                {"drawing_name", Path.GetFileName(document.Name)},
                {"source_drawing_sha256", _cachedStatusSha},
                {"database_modified", databaseModified},
                {"read_only_channel_ready", (databaseModified & ~24) == 0},
                {"drawing_mutation_performed", false}
            };
            string directory = Path.Combine(root, "documents");
            Directory.CreateDirectory(directory);
            WriteAtomicReplace(
                Path.Combine(directory, DocumentId(document) + ".json"), status);
            _lastStatusAt = DateTime.UtcNow;
        }

        private static string DocumentId(Document document)
        {
            Guid fingerprint;
            if (Guid.TryParse(document.Database.FingerprintGuid, out fingerprint) &&
                fingerprint != Guid.Empty) return fingerprint.ToString("D").ToLowerInvariant();
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    Path.GetFullPath(document.Name).ToUpperInvariant()));
            byte[] identifier = new byte[16];
            Array.Copy(digest, identifier, identifier.Length);
            return new Guid(identifier).ToString("D").ToLowerInvariant();
        }

        private static int Dbmod()
        { return Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"), CultureInfo.InvariantCulture); }

        private static int DbmodCommandActive()
        { return Convert.ToInt32(AcApplication.GetSystemVariable("CMDACTIVE"), CultureInfo.InvariantCulture); }

        private static string RequiredString(Dictionary<string, object> value, string key)
        {
            object raw;
            if (!value.TryGetValue(key, out raw) || !(raw is string) ||
                String.IsNullOrWhiteSpace((string)raw))
                throw new InvalidOperationException("Operator request text is missing.");
            return (string)raw;
        }

        private static bool Hex(string value, int exact)
        {
            if (String.IsNullOrEmpty(value) || value.Length != exact) return false;
            foreach (char item in value) if (!Uri.IsHexDigit(item)) return false;
            return true;
        }

        private static string SafeMessage(System.Exception error)
        {
            string value = error.GetType().Name + ": " + error.Message;
            return value.Length <= 512 ? value : value.Substring(0, 512);
        }

        private static string Sha256(byte[] value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                StringBuilder text = new StringBuilder(64);
                foreach (byte item in sha.ComputeHash(value))
                    text.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static string FileSha256(string path)
        {
            using (FileStream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 sha = SHA256.Create())
            {
                StringBuilder text = new StringBuilder(64);
                foreach (byte item in sha.ComputeHash(stream))
                    text.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static void WriteAtomic(string path, Dictionary<string, object> value)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(value));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (FileStream stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            File.Move(temporary, path);
        }

        private static void WriteAtomicReplace(
            string path, Dictionary<string, object> value)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(value));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
