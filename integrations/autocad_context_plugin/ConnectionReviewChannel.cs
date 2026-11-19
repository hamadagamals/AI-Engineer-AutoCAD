using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using Forms = System.Windows.Forms;

namespace AIEngineer.AutoCAD
{
    internal static class ConnectionReviewChannel
    {
        private const string RequestVersion = "autocad.connection_pair_review_request.v1";
        private const string ResultVersion = "autocad.connection_pair_review_result.v1";
        private static Forms.Timer _timer;
        private static bool _busy;

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new Forms.Timer();
            _timer.Interval = 1200;
            _timer.Tick += Poll;
            _timer.Start();
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
            if (document == null || Convert.ToInt32(AcApplication.GetSystemVariable("CMDACTIVE"), CultureInfo.InvariantCulture) != 0)
                return;
            string workspace;
            try { workspace = LoadWorkspaceRoot(); }
            catch { return; }
            string root = Path.GetFullPath(Path.Combine(workspace, ".ai-engineer", "autocad", "review-channel"));
            string inbox = Path.Combine(root, "inbox");
            Directory.CreateDirectory(inbox);
            string[] candidates = Directory.GetFiles(inbox, "*.json", SearchOption.TopDirectoryOnly);
            if (candidates.Length == 0) return;
            Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
            _busy = true;
            try { Process(document, root, candidates[0]); }
            catch (System.Exception error)
            {
                document.Editor.WriteMessage("\nAI-Engineer review channel failed safely: " + SafeMessage(error) + "\n");
            }
            finally { _busy = false; }
        }

        private static void Process(Document document, string root, string requestPath)
        {
            string processing = Path.Combine(root, "processing");
            string processed = Path.Combine(root, "processed");
            string outbox = Path.Combine(root, "outbox");
            Directory.CreateDirectory(processing);
            Directory.CreateDirectory(processed);
            Directory.CreateDirectory(outbox);
            string claimed = Path.Combine(processing, Path.GetFileName(requestPath));
            if ((File.GetAttributes(requestPath) & FileAttributes.ReparsePoint) != 0) return;
            byte[] requestBytes = File.ReadAllBytes(requestPath);
            if (requestBytes.Length == 0 || requestBytes.Length > 16384) return;
            Dictionary<string, object> request = ParseRequest(requestBytes);
            string activeDocumentId = DocumentId(document);
            if (!String.Equals(RequiredString(request, "document_id"), activeDocumentId, StringComparison.Ordinal))
                return;
            File.Move(requestPath, claimed);
            string requestSha = Sha256(requestBytes);
            string requestId = RequiredString(request, "request_id");
            Dictionary<string, object> result;
            try
            {
                result = Review(document, request, requestSha);
            }
            catch (System.Exception error)
            {
                result = BaseResult(document, requestId, requestSha, "error");
                AddEmptyReviewDetails(result);
                result["error"] = SafeMessage(error);
                result["drawing_mutation_performed"] = false;
            }
            WriteAtomic(Path.Combine(outbox, requestId + ".json"), result);
            File.Move(claimed, Path.Combine(processed, Path.GetFileName(claimed)));
        }

        private static Dictionary<string, object> Review(
            Document document, Dictionary<string, object> request, string requestSha)
        {
            int dbmodBefore = Dbmod();
            if ((dbmodBefore & ~(8 | 16)) != 0)
                throw new InvalidOperationException("Review channel requires clean or presentation-only DBMOD (8/16).");
            string expectedDrawingSha = RequiredString(request, "source_drawing_sha256");
            string actualDrawingSha = FileSha256(document.Name);
            if (!String.Equals(expectedDrawingSha, actualDrawingSha, StringComparison.Ordinal))
                throw new InvalidOperationException("Open drawing SHA-256 does not match the reviewed request.");
            string deviceHandle = RequiredString(request, "device_object_id");
            string routeHandle = RequiredString(request, "route_object_id");
            string endpoint = RequiredString(request, "endpoint");
            string terminalName = RequiredString(request, "terminal_name");
            ObjectId deviceId = ObjectIdFromHandle(document.Database, deviceHandle);
            ObjectId routeId = ObjectIdFromHandle(document.Database, routeHandle);
            string blockName, routeType;
            Point3d chosen, alternate;
            Extents3d bounds;
            Point3d devicePosition;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                BlockReference device = transaction.GetObject(deviceId, OpenMode.ForRead, false) as BlockReference;
                Entity route = transaction.GetObject(routeId, OpenMode.ForRead, false) as Entity;
                if (device == null || route == null)
                    throw new InvalidOperationException("Reviewed handles are not a block and route.");
                ObjectId definitionId = device.IsDynamicBlock ? device.DynamicBlockTableRecord : device.BlockTableRecord;
                BlockTableRecord definition = transaction.GetObject(definitionId, OpenMode.ForRead, false) as BlockTableRecord;
                blockName = definition == null ? "" : definition.Name;
                devicePosition = device.Position;
                Point3d start, end;
                Line line = route as Line;
                Autodesk.AutoCAD.DatabaseServices.Polyline polyline = route as Autodesk.AutoCAD.DatabaseServices.Polyline;
                if (line != null) { routeType = "LINE"; start = line.StartPoint; end = line.EndPoint; }
                else if (polyline != null && polyline.NumberOfVertices >= 2)
                {
                    routeType = "LWPOLYLINE";
                    start = polyline.GetPoint3dAt(0);
                    end = polyline.GetPoint3dAt(polyline.NumberOfVertices - 1);
                }
                else throw new InvalidOperationException("Reviewed route type is unsupported.");
                chosen = endpoint == "start" ? start : end;
                alternate = endpoint == "start" ? end : start;
                bounds = Merge(device.GeometricExtents, new Extents3d(chosen, chosen));
                transaction.Commit();
            }
            double markerSize = SetView(document.Editor, bounds);
            document.Editor.SetImpliedSelection(new ObjectId[] { deviceId });
            DrawEndpointMarker(document.Editor, chosen, devicePosition, markerSize);
            AcApplication.UpdateScreen();
            Forms.DialogResult approval;
            try
            {
                string message = "AI-Engineer read-only connection review\n\n" +
                    "Device: " + blockName + "\nHandle: " + deviceHandle + "\n\n" +
                    "Route: " + routeHandle + " / " + endpoint + "\nTerminal: " + terminalName +
                    "\n\nRED CROSS = exact reviewed route endpoint.\n" +
                    "The yellow line points from the device to that endpoint.\n\n" +
                    "Approve this exact connection pair?\n\n" +
                    "العلامة الحمراء هي نهاية الخط محل المراجعة.\n" +
                    "الخط الأصفر يصل البلوك بهذه النهاية.";
                approval = Forms.MessageBox.Show(message, "AI-Engineer Connection Review",
                    Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question, Forms.MessageBoxDefaultButton.Button2);
            }
            finally
            {
                document.Editor.SetImpliedSelection(new ObjectId[0]);
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            int dbmodAfter = Dbmod();
            if ((dbmodAfter & ~(8 | 16)) != 0)
                throw new InvalidOperationException("Connection review produced unsupported DBMOD bits.");
            Dictionary<string, object> result = BaseResult(
                document, RequiredString(request, "request_id"), requestSha,
                approval == Forms.DialogResult.Yes ? "approved" : "rejected");
            result["source_drawing_sha256"] = actualDrawingSha;
            result["device_object_id"] = deviceHandle;
            result["block_name"] = blockName;
            result["route_object_id"] = routeHandle;
            result["route_entity_type"] = routeType;
            result["endpoint"] = endpoint;
            result["world_position"] = Point(chosen);
            result["alternate_endpoint_distance"] = chosen.DistanceTo(alternate);
            result["terminal_name"] = terminalName;
            result["database_modified_before"] = dbmodBefore;
            result["database_modified_after"] = dbmodAfter;
            result["approval_method"] = "engineer_modal_endpoint_marker_v2";
            result["drawing_mutation_performed"] = false;
            result["error"] = null;
            return result;
        }

        private static Dictionary<string, object> ParseRequest(byte[] raw)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> value = serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(raw));
            string[] expected = new string[] { "schema_version", "request_version", "request_id", "created_at", "expires_at", "document_id", "source_drawing_sha256", "device_object_id", "route_object_id", "endpoint", "terminal_name", "requested_action", "drawing_mutation_allowed" };
            if (value == null || value.Count != expected.Length) throw new InvalidOperationException("Review request schema is invalid.");
            foreach (string key in expected) if (!value.ContainsKey(key)) throw new InvalidOperationException("Review request schema is invalid.");
            if (Convert.ToInt32(value["schema_version"], CultureInfo.InvariantCulture) != 1 || RequiredString(value, "request_version") != RequestVersion || RequiredString(value, "requested_action") != "review_connection_pair" || Convert.ToBoolean(value["drawing_mutation_allowed"], CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Review request capability is unsupported.");
            if (!Hex(RequiredString(value, "request_id"), 32) || !Hex(RequiredString(value, "source_drawing_sha256"), 64) || !Hex(RequiredString(value, "device_object_id"), 0) || !Hex(RequiredString(value, "route_object_id"), 0))
                throw new InvalidOperationException("Review request identity is invalid.");
            string endpoint = RequiredString(value, "endpoint");
            if (endpoint != "start" && endpoint != "end") throw new InvalidOperationException("Review endpoint is invalid.");
            Guid documentId;
            if (!Guid.TryParseExact(RequiredString(value, "document_id"), "D", out documentId) || documentId == Guid.Empty)
                throw new InvalidOperationException("Review document identity is invalid.");
            string terminal = RequiredString(value, "terminal_name");
            if (terminal.Length > 64 || terminal.IndexOfAny(new char[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("Review terminal name is invalid.");
            DateTime created, expires;
            DateTimeStyles timestampStyle = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
            if (!DateTime.TryParse(RequiredString(value, "created_at"), CultureInfo.InvariantCulture, timestampStyle, out created) ||
                !DateTime.TryParse(RequiredString(value, "expires_at"), CultureInfo.InvariantCulture, timestampStyle, out expires) ||
                created >= expires || expires > created.AddMinutes(30) || created > DateTime.UtcNow.AddMinutes(1) || expires <= DateTime.UtcNow)
                throw new InvalidOperationException("Review request is expired or exceeds 30 minutes.");
            return value;
        }

        private static Dictionary<string, object> BaseResult(Document document, string requestId, string requestSha, string status)
        {
            return new Dictionary<string, object>
            {
                {"schema_version", 1}, {"result_version", ResultVersion},
                {"result_id", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)},
                {"request_id", requestId}, {"request_sha256", requestSha},
                {"captured_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)},
                {"document_id", DocumentId(document)}, {"drawing_name", Path.GetFileName(document.Name)},
                {"drawing_units", (int)document.Database.Insunits}, {"layout", LayoutManager.Current.CurrentLayout},
                {"status", status}
            };
        }

        private static void AddEmptyReviewDetails(Dictionary<string, object> result)
        {
            foreach (string key in new string[]
            {
                "source_drawing_sha256", "device_object_id", "block_name", "route_object_id",
                "route_entity_type", "endpoint", "world_position", "alternate_endpoint_distance",
                "terminal_name", "database_modified_before", "database_modified_after", "approval_method"
            }) result[key] = null;
        }

        private static double SetView(Editor editor, Extents3d bounds)
        {
            using (ViewTableRecord view = editor.GetCurrentView())
            {
                double width = Math.Max(bounds.MaxPoint.X - bounds.MinPoint.X, 250.0) * 1.60;
                double height = Math.Max(bounds.MaxPoint.Y - bounds.MinPoint.Y, 250.0) * 1.60;
                double ratio = view.Height <= 0 ? 1.0 : view.Width / view.Height;
                if (width / height < ratio) width = height * ratio; else height = width / ratio;
                view.CenterPoint = new Point2d((bounds.MinPoint.X + bounds.MaxPoint.X) / 2.0, (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2.0);
                view.Width = width; view.Height = height;
                editor.SetCurrentView(view);
                return Math.Max(12.0, Math.Min(Math.Min(width, height) * 0.055, 100.0));
            }
        }

        private static void DrawEndpointMarker(Editor editor, Point3d endpoint, Point3d device, double size)
        {
            Vector3d x = new Vector3d(size, 0.0, 0.0);
            Vector3d y = new Vector3d(0.0, size, 0.0);
            Vector3d diagonalA = new Vector3d(size * 0.72, size * 0.72, 0.0);
            Vector3d diagonalB = new Vector3d(size * 0.72, -size * 0.72, 0.0);
            editor.DrawVector(endpoint - x, endpoint + x, 1, true);
            editor.DrawVector(endpoint - y, endpoint + y, 1, true);
            editor.DrawVector(endpoint - diagonalA, endpoint + diagonalA, 1, true);
            editor.DrawVector(endpoint - diagonalB, endpoint + diagonalB, 1, true);
            editor.DrawVector(device, endpoint, 2, true);
        }

        private static Extents3d Merge(Extents3d left, Extents3d right)
        {
            Point3d minimum = new Point3d(Math.Min(left.MinPoint.X, right.MinPoint.X), Math.Min(left.MinPoint.Y, right.MinPoint.Y), Math.Min(left.MinPoint.Z, right.MinPoint.Z));
            Point3d maximum = new Point3d(Math.Max(left.MaxPoint.X, right.MaxPoint.X), Math.Max(left.MaxPoint.Y, right.MaxPoint.Y), Math.Max(left.MaxPoint.Z, right.MaxPoint.Z));
            return new Extents3d(minimum, maximum);
        }

        private static ObjectId ObjectIdFromHandle(Database database, string value)
        {
            long parsed;
            if (!Int64.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)) throw new InvalidOperationException("Reviewed handle is invalid.");
            return database.GetObjectId(false, new Handle(parsed), 0);
        }

        private static string LoadWorkspaceRoot()
        {
            string settingsPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "settings.json");
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> value = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsPath, Encoding.UTF8));
            return Path.GetFullPath(RequiredString(value, "workspace_root"));
        }

        private static string DocumentId(Document document)
        {
            Guid fingerprint;
            if (Guid.TryParse(document.Database.FingerprintGuid, out fingerprint) && fingerprint != Guid.Empty) return fingerprint.ToString("D").ToLowerInvariant();
            byte[] digest;
            using (SHA256 sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(document.Name).ToUpperInvariant()));
            byte[] identifier = new byte[16]; Array.Copy(digest, identifier, identifier.Length);
            return new Guid(identifier).ToString("D").ToLowerInvariant();
        }

        private static int Dbmod() { return Convert.ToInt32(AcApplication.GetSystemVariable("DBMOD"), CultureInfo.InvariantCulture); }
        private static double[] Point(Point3d point) { return new double[] { point.X, point.Y, point.Z }; }
        private static string RequiredString(Dictionary<string, object> value, string key) { object raw; if (!value.TryGetValue(key, out raw) || !(raw is string) || String.IsNullOrWhiteSpace((string)raw)) throw new InvalidOperationException("Review request text is missing."); return (string)raw; }
        private static bool Hex(string value, int exact) { if (String.IsNullOrEmpty(value) || (exact > 0 ? value.Length != exact : value.Length > 32)) return false; foreach (char item in value) if (!Uri.IsHexDigit(item)) return false; return true; }
        private static string SafeMessage(System.Exception error) { string value = error.GetType().Name + ": " + error.Message; return value.Length <= 512 ? value : value.Substring(0, 512); }
        private static string Sha256(byte[] value) { using (SHA256 sha = SHA256.Create()) { StringBuilder text = new StringBuilder(64); foreach (byte item in sha.ComputeHash(value)) text.Append(item.ToString("x2", CultureInfo.InvariantCulture)); return text.ToString(); } }
        private static string FileSha256(string path) { using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (SHA256 sha = SHA256.Create()) { StringBuilder text = new StringBuilder(64); foreach (byte item in sha.ComputeHash(stream)) text.Append(item.ToString("x2", CultureInfo.InvariantCulture)); return text.ToString(); } }
        private static void WriteAtomic(string path, Dictionary<string, object> value) { JavaScriptSerializer serializer = new JavaScriptSerializer(); byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(value)); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); } File.Move(temporary, path); }
    }
}
