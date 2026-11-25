using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

#if !PANEL_MFG_STANDALONE
[assembly: CommandClass(typeof(AIEngineer.AutoCAD.DrawingExecutionCommands))]
#endif

namespace AIEngineer.AutoCAD
{
    public sealed class DrawingExecutionCommands
    {
        public const string ReviewLayerName = "_AI_PREVIEW_REVIEW";
        private static ObjectId _lastInsertedBlockRefId = ObjectId.Null;

        private static string ResolveRepoRoot()
        {
            string envRoot = Environment.GetEnvironmentVariable("AI_ENGINEER_ROOT");
            if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
                return Path.GetFullPath(envRoot.Trim());

            try
            {
                string asmDir = Path.GetDirectoryName(typeof(DrawingExecutionCommands).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    // Check settings.json written by build.ps1 alongside the DLL
                    string settingsPath = Path.Combine(asmDir, "settings.json");
                    if (File.Exists(settingsPath))
                    {
                        try
                        {
                            System.Web.Script.Serialization.JavaScriptSerializer ser =
                                new System.Web.Script.Serialization.JavaScriptSerializer();
                            Dictionary<string, object> cfg = ser.DeserializeObject(File.ReadAllText(settingsPath, Encoding.UTF8)) as Dictionary<string, object>;
                            if (cfg != null && cfg.ContainsKey("workspace_root") && cfg["workspace_root"] != null)
                            {
                                string ws = cfg["workspace_root"].ToString().Trim();
                                if (!string.IsNullOrEmpty(ws) && Directory.Exists(ws))
                                {
                                    if (File.Exists(Path.Combine(Path.Combine(ws, "integrations"), "cad_geometry_engine.py")))
                                        return Path.GetFullPath(ws);
                                    DirectoryInfo wsParent = Directory.GetParent(ws);
                                    if (wsParent != null && File.Exists(Path.Combine(Path.Combine(wsParent.FullName, "integrations"), "cad_geometry_engine.py")))
                                        return wsParent.FullName;
                                }
                            }
                        }
                        catch { }
                    }

                    DirectoryInfo cur = new DirectoryInfo(asmDir);
                    for (int depth = 0; cur != null && depth < 8; depth++)
                    {
                        if (File.Exists(Path.Combine(Path.Combine(cur.FullName, "integrations"), "cad_geometry_engine.py")))
                            return cur.FullName;
                        cur = cur.Parent;
                    }
                }
            }
            catch { }

            return Environment.CurrentDirectory;
        }

        private static string ResolvePythonExecutable(string repoRoot)
        {
            string envPy = Environment.GetEnvironmentVariable("AI_ENGINEER_PYTHON");
            if (!string.IsNullOrWhiteSpace(envPy) && File.Exists(envPy.Trim()))
                return envPy.Trim();

            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                string[] venvCandidates = new string[]
                {
                    Path.Combine(Path.Combine(Path.Combine(repoRoot, ".venv"), "Scripts"), "python.exe"),
                    Path.Combine(Path.Combine(Path.Combine(repoRoot, "venv"), "Scripts"), "python.exe")
                };
                for (int i = 0; i < venvCandidates.Length; i++)
                {
                    if (File.Exists(venvCandidates[i])) return venvCandidates[i];
                }
            }

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] dirs = pathEnv.Split(new char[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < dirs.Length; i++)
            {
                try
                {
                    string candidate = Path.Combine(dirs[i].Trim(), "python.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return "";
        }

        internal static string ResolveOutputDirectory(Document doc, string subFolder)
        {
            return ResolveOutputDirectory(doc, ResolveRepoRoot(), subFolder);
        }

        private static string ResolveOutputDirectory(Document doc, string repoRoot, string subFolder)
        {
            if (doc != null && !string.IsNullOrWhiteSpace(doc.Name))
            {
                try
                {
                    if (Path.IsPathRooted(doc.Name))
                    {
                        string docDir = Path.GetDirectoryName(doc.Name);
                        if (!string.IsNullOrEmpty(docDir) && Directory.Exists(docDir))
                            return Path.Combine(docDir, subFolder);
                    }
                }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(repoRoot) && Directory.Exists(repoRoot))
                return Path.Combine(Path.Combine(repoRoot, "workspace"), subFolder);

            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(localApp, "AI-Engineer"), subFolder);
        }

        private static void RunPythonScriptWithTimeout(string pyExe, string arguments, string workingDir, int timeoutMs)
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(pyExe, arguments);
            if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
                psi.WorkingDirectory = workingDir;
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                if (p == null) return;
                System.Threading.Tasks.Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("Python script timed out after " + timeoutMs.ToString(CultureInfo.InvariantCulture) + " ms.");
                }
                string errText = errTask.Wait(2000) ? errTask.Result : "";
                outTask.Wait(1000);
                if (p.ExitCode != 0)
                    throw new InvalidOperationException("Python script exited with code " + p.ExitCode.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(errText) ? "" : (": " + errText.Trim())));
            }
        }

#if !PANEL_MFG_STANDALONE
        [CommandMethod("AIENGDRAWHOUSE", CommandFlags.Session)]
        [CommandMethod("AIENGDRAW150", CommandFlags.Session)]
        public void DrawHouseCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            string error;
            if (DrawHouseConcept(doc, out error))
            {
                doc.Editor.WriteMessage("\n[AI-Engineer] تم رسم مقترح الشقة 150م2 بنجاح بلون المراجعة الأحمر (_AI_PREVIEW_REVIEW).\n");
                doc.Editor.WriteMessage("[AI-Engineer] للاعتماد والتثبيت: اكتب AIENGAPPROVE أو اكتب «تمام» في الشات.\n");
                doc.Editor.WriteMessage("[AI-Engineer] للتراجع والحذف: اكتب AIENGROLLBACK أو اكتب «تراجع» في الشات.\n");
            }
            else
            {
                doc.Editor.WriteMessage("\n[AI-Engineer] تعذر رسم المقترح: " + error + "\n");
            }
        }

        [CommandMethod("AIENGAPPROVE", CommandFlags.Session)]
        [CommandMethod("AIENGCOMMIT", CommandFlags.Session)]
        public void ApproveCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            ApproveReview(doc);
            doc.Editor.WriteMessage("\n[AI-Engineer] تم اعتماد وتثبيت الرسم في الطبقات الرسمية بنجاح! 🏛️\n");
        }

        [CommandMethod("AIENGROLLBACK", CommandFlags.Session)]
        [CommandMethod("AIENGREVERT", CommandFlags.Session)]
        public void RollbackCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            RollbackReview(doc);
            doc.Editor.WriteMessage("\n[AI-Engineer] تم التراجع عن الرسم المقترح وحذف خطوط المعاينة بنجاح.\n");
        }

        [CommandMethod("AIENGDRAWELECTRICAL", CommandFlags.Session)]
        [CommandMethod("AIENGDRAWELEC", CommandFlags.Session)]
        public void DrawElectricalCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            string error;
            if (DrawElectricalDistribution(doc, out error))
            {
                doc.Editor.WriteMessage("\n[AI-Engineer] تم توزيع شبكة الإنارة والأفياش ولوحة التوزيع بنجاح!\n");
            }
            else
            {
                doc.Editor.WriteMessage("\n[AI-Engineer] تعذر رسم التوزيع الكهربائي: " + error + "\n");
            }
        }
#endif

        public static bool DrawElectricalDistribution(Document doc, out string errorMessage)
        {
            errorMessage = null;
            if (doc == null) { errorMessage = "لا يوجد مستند أوتوكاد نشط."; return false; }
            try
            {
                string repoRoot = ResolveRepoRoot();
                string outDir = ResolveOutputDirectory(doc, repoRoot, "apartment_150sqm");
                Directory.CreateDirectory(outDir);
                string dxfPath = Path.Combine(outDir, "house-150sqm-electrical-plan.dxf");
                if (!File.Exists(dxfPath))
                {
                    string pyExe = ResolvePythonExecutable(repoRoot);
                    string script = Path.Combine(Path.Combine(repoRoot, "integrations"), "build_house_150sqm_electrical.py");
                    if (!string.IsNullOrEmpty(pyExe) && File.Exists(pyExe) && File.Exists(script))
                    {
                        RunPythonScriptWithTimeout(
                            pyExe,
                            "\"" + script + "\" --output-dir \"" + outDir + "\"",
                            repoRoot,
                            10000);
                    }
                }

                if (!File.Exists(dxfPath))
                {
                    errorMessage = "ملف DXF الكهربائي غير موجود في المسار: " + dxfPath;
                    return false;
                }

                return InsertDxfDirectly(doc, dxfPath, out errorMessage);
            }
            catch (System.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool ExecuteNaturalLanguageDrawing(
            Document doc,
            string userPrompt,
            out string description,
            out bool isClarification,
            out string errorMessage)
        {
            description = null;
            isClarification = false;
            errorMessage = null;
            if (doc == null) { errorMessage = "لا يوجد مستند أوتوكاد نشط."; return false; }
            string promptFile = null;
            try
            {
                string repoRoot = ResolveRepoRoot();
                string pyExe = ResolvePythonExecutable(repoRoot);
                string script = Path.Combine(Path.Combine(repoRoot, "integrations"), "cad_geometry_engine.py");
                if (string.IsNullOrEmpty(pyExe) || !File.Exists(pyExe) || !File.Exists(script))
                {
                    errorMessage = "محرك بايثون الهندسي غير موجود. تأكد من تعيين AI_ENGINEER_ROOT أو تثبيت .venv.";
                    return false;
                }

                string outDir = ResolveOutputDirectory(doc, repoRoot, "apartment_150sqm");
                Directory.CreateDirectory(outDir);

                promptFile = Path.Combine(Path.GetTempPath(), "ai_eng_prompt_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(promptFile, userPrompt ?? "", Encoding.UTF8);

                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = pyExe;
                psi.WorkingDirectory = repoRoot;
                psi.Arguments = string.Format(
                    "\"{0}\" --prompt-file \"{1}\" --output-dir \"{2}\"",
                    script, promptFile, outDir);
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
                psi.StandardErrorEncoding = System.Text.Encoding.UTF8;

                string outputJson;
                string errorOutput;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    System.Threading.Tasks.Task<string> errTask = p.StandardError.ReadToEndAsync();
                    outputJson = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); } catch { }
                    }
                    errorOutput = errTask.Wait(2000) ? errTask.Result : "";
                }

                if (string.IsNullOrWhiteSpace(outputJson))
                {
                    errorMessage = !string.IsNullOrWhiteSpace(errorOutput)
                        ? ("خطأ في محرك الرسم: " + errorOutput)
                        : "لم يتم الحصول على مخرجات من محرك الرسم.";
                    return false;
                }

                System.Web.Script.Serialization.JavaScriptSerializer serializer =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                System.Collections.Generic.Dictionary<string, object> res =
                    serializer.DeserializeObject(outputJson) as System.Collections.Generic.Dictionary<string, object>;

                if (res == null)
                {
                    errorMessage = "استجابة محرك الرسم غير صالحة.";
                    return false;
                }

                string status = res.ContainsKey("status") ? res["status"].ToString() : "";
                if (status == "ask_user")
                {
                    isClarification = true;
                    description = res.ContainsKey("question") ? res["question"].ToString() : "يرجى توضيح البيانات الهندسية المطلوبة.";
                    return true;
                }

                if (status == "error")
                {
                    errorMessage = res.ContainsKey("message") ? res["message"].ToString() : "حدث خطأ أثناء معالجة الرسم.";
                    return false;
                }

                if (!res.ContainsKey("dxf"))
                {
                    errorMessage = "لم يتم توليد ملف DXF للرسم المطلوب.";
                    return false;
                }

                string dxfPath = res["dxf"].ToString();
                description = res.ContainsKey("description") ? res["description"].ToString() : "تم رسم العنصر المطلوب بنجاح.";

                return InsertDxfDirectly(doc, dxfPath, out errorMessage);
            }
            catch (System.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(promptFile))
                {
                    try { if (File.Exists(promptFile)) File.Delete(promptFile); } catch { }
                }
            }
        }

        public static bool ExecuteNaturalLanguageDrawing(
            Document doc,
            string userPrompt,
            out string description,
            out string errorMessage)
        {
            bool isClarification;
            return ExecuteNaturalLanguageDrawing(doc, userPrompt, out description, out isClarification, out errorMessage);
        }

        public static string InspectActiveDrawing(Document doc)
        {
            if (doc == null) return "active_file=none;status=NO_ACTIVE_DOCUMENT";
            try
            {
                Database db = doc.Database;
                string fileName = Path.GetFileName(doc.Name ?? "Drawing.dwg");
                int totalEntities = 0;
                Dictionary<string, int> layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, int> typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, int> blockCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                List<string> textSamples = new List<string>();
                HashSet<string> seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Extents3d? totalExtents = null;

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                    foreach (ObjectId id in modelSpace)
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        totalEntities++;

                        string layerName = ent.Layer ?? "0";
                        layerCounts[layerName] = layerCounts.ContainsKey(layerName) ? layerCounts[layerName] + 1 : 1;

                        string typeName = ent.GetType().Name;
                        typeCounts[typeName] = typeCounts.ContainsKey(typeName) ? typeCounts[typeName] + 1 : 1;

                        BlockReference br = ent as BlockReference;
                        if (br != null)
                        {
                            string blkName = br.Name;
                            try
                            {
                                if (br.IsDynamicBlock && !br.DynamicBlockTableRecord.IsNull)
                                {
                                    BlockTableRecord dynRec = tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                                    if (dynRec != null) blkName = dynRec.Name;
                                }
                            }
                            catch { }
                            blockCounts[blkName] = blockCounts.ContainsKey(blkName) ? blockCounts[blkName] + 1 : 1;
                            foreach (ObjectId attId in br.AttributeCollection)
                            {
                                try
                                {
                                    AttributeReference att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                                    if (att != null && !string.IsNullOrWhiteSpace(att.TextString) && textSamples.Count < 25)
                                    {
                                        string cleaned = att.TextString.Replace("\r", " ").Replace("\n", " ").Trim();
                                        if (cleaned.Length > 0 && seenTexts.Add(cleaned))
                                            textSamples.Add(cleaned.Length > 60 ? cleaned.Substring(0, 60) : cleaned);
                                    }
                                }
                                catch { }
                            }
                        }
                        else if (ent is DBText)
                        {
                            string s = ((DBText)ent).TextString ?? "";
                            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
                            if (s.Length > 0 && seenTexts.Add(s) && textSamples.Count < 25)
                                textSamples.Add(s.Length > 60 ? s.Substring(0, 60) : s);
                        }
                        else if (ent is MText)
                        {
                            string s = ((MText)ent).Text ?? "";
                            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
                            if (s.Length > 0 && seenTexts.Add(s) && textSamples.Count < 25)
                                textSamples.Add(s.Length > 60 ? s.Substring(0, 60) : s);
                        }

                        try
                        {
                            Extents3d ext = ent.GeometricExtents;
                            if (!totalExtents.HasValue) totalExtents = ext;
                            else
                            {
                                Extents3d cur = totalExtents.Value;
                                cur.AddExtents(ext);
                                totalExtents = cur;
                            }
                        }
                        catch { }
                    }
                    tr.Commit();
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("active_file=").Append(fileName);
                sb.Append(";units=").Append(db.Insunits.ToString());
                sb.Append(";total_entities=").Append(totalEntities.ToString(CultureInfo.InvariantCulture));
                if (totalEntities == 0)
                {
                    sb.Append(";status=EMPTY_DRAWING");
                    return sb.ToString();
                }

                if (totalExtents.HasValue)
                {
                    double w = Math.Round(totalExtents.Value.MaxPoint.X - totalExtents.Value.MinPoint.X, 1);
                    double h = Math.Round(totalExtents.Value.MaxPoint.Y - totalExtents.Value.MinPoint.Y, 1);
                    sb.Append(";extents=").Append(w.ToString(CultureInfo.InvariantCulture))
                      .Append("x").Append(h.ToString(CultureInfo.InvariantCulture));
                }

                if (typeCounts.Count > 0)
                {
                    sb.Append(";types=");
                    int i = 0;
                    foreach (KeyValuePair<string, int> kv in typeCounts)
                    {
                        if (i++ >= 12) break;
                        if (i > 1) sb.Append(",");
                        sb.Append(kv.Key).Append("(").Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(")");
                    }
                }

                if (layerCounts.Count > 0)
                {
                    sb.Append(";layers=");
                    int i = 0;
                    foreach (KeyValuePair<string, int> kv in layerCounts)
                    {
                        if (i++ >= 15) break;
                        if (i > 1) sb.Append(",");
                        sb.Append(kv.Key).Append("(").Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(")");
                    }
                }

                if (blockCounts.Count > 0)
                {
                    sb.Append(";blocks=");
                    int i = 0;
                    foreach (KeyValuePair<string, int> kv in blockCounts)
                    {
                        if (i++ >= 15) break;
                        if (i > 1) sb.Append(",");
                        sb.Append(kv.Key).Append("(").Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(")");
                    }
                }

                if (textSamples.Count > 0)
                {
                    sb.Append(";texts=[");
                    for (int i = 0; i < textSamples.Count; i++)
                    {
                        if (i > 0) sb.Append(" | ");
                        sb.Append(textSamples[i]);
                    }
                    sb.Append("]");
                }

                string result = sb.ToString();
                return result.Length > 3500 ? result.Substring(0, 3500) : result;
            }
            catch (System.Exception ex)
            {
                return "active_file=unknown;inspect_error=" + ex.Message;
            }
        }

        public static bool DuplicateActiveDrawing(
            Document doc,
            string userPrompt,
            out string description,
            out bool isClarification,
            out string errorMessage)
        {
            description = null;
            isClarification = false;
            errorMessage = null;
            if (doc == null) { errorMessage = "لا يوجد مستند أوتوكاد نشط."; return false; }

            try
            {
                Database db = doc.Database;
                string lower = (userPrompt ?? "").Trim().ToLowerInvariant();

                // Check if user wants a separate DWG file saved on disk
                bool wantsFileCopy = lower.Contains("ملف") || lower.Contains("dwg") || lower.Contains("حفظ") || lower.Contains("على الهارد");
                if (wantsFileCopy)
                {
                    string outDir = ResolveOutputDirectory(doc, ResolveRepoRoot(), "AI-Engineer-Copies");
                    Directory.CreateDirectory(outDir);
                    string baseName = Path.GetFileNameWithoutExtension(doc.Name ?? "drawing");
                    if (string.IsNullOrWhiteSpace(baseName)) baseName = "drawing";
                    string copyPath = Path.Combine(outDir, baseName + "_copy_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dwg");

                    using (DocumentLock docLock = doc.LockDocument())
                    {
                        ObjectIdCollection allIds = new ObjectIdCollection();
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                            foreach (ObjectId id in ms) allIds.Add(id);
                            tr.Commit();
                        }
                        if (allIds.Count == 0)
                        {
                            errorMessage = "المخطط المفتوح فارغ حالياً ولا يحتوي على عناصر لحفظ نسخة منه.";
                            return false;
                        }
                        using (Database copyDb = db.Wblock(allIds, Point3d.Origin))
                        {
                            copyDb.SaveAs(copyPath, DwgVersion.Current);
                        }
                    }

                    description = "تم حفظ نسخة مستقلة كاملة من المخطط المفتوح كملف DWG جديد في المسار:\n" + copyPath;
                    isClarification = true; // Saved on disk directly without preview layer mutation
                    return true;
                }

                // Clone ModelSpace geometry side-by-side inside the drawing onto _AI_PREVIEW_REVIEW
                int copiedCount = 0;
                double shiftX = 0;
                double shiftY = 0;

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(ReviewLayerName))
                    {
                        lt.UpgradeOpen();
                        LayerTableRecord ltr = new LayerTableRecord();
                        ltr.Name = ReviewLayerName;
                        ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
                        lt.Add(ltr);
                        tr.AddNewlyCreatedDBObject(ltr, true);
                    }

                    BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                    ObjectIdCollection ids = new ObjectIdCollection();
                    Extents3d? totalExtents = null;

                    foreach (ObjectId id in modelSpace)
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null || ent is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                        ids.Add(id);
                        try
                        {
                            Extents3d ext = ent.GeometricExtents;
                            if (!totalExtents.HasValue) totalExtents = ext;
                            else
                            {
                                Extents3d cur = totalExtents.Value;
                                cur.AddExtents(ext);
                                totalExtents = cur;
                            }
                        }
                        catch { }
                    }

                    if (ids.Count == 0)
                    {
                        errorMessage = "الرسم المفتوح حالياً فارغ ولا يحتوي على عناصر لنسخها. يمكنك طلب رسم مخطط أو أشكال أولاً.";
                        return false;
                    }

                    double width = totalExtents.HasValue ? Math.Max(100.0, totalExtents.Value.MaxPoint.X - totalExtents.Value.MinPoint.X) : 1000.0;
                    double height = totalExtents.HasValue ? Math.Max(100.0, totalExtents.Value.MaxPoint.Y - totalExtents.Value.MinPoint.Y) : 1000.0;
                    double gap = Math.Max(Math.Max(width, height) * 0.25, 250.0);

                    if (lower.Contains("يسار") || lower.Contains("شمال"))
                        shiftX = -(width + gap);
                    else if (lower.Contains("فوق") || lower.Contains("اعلى") || lower.Contains("أعلى"))
                        shiftY = height + gap;
                    else if (lower.Contains("تحت") || lower.Contains("اسفل") || lower.Contains("أسفل"))
                        shiftY = -(height + gap);
                    else
                        shiftX = width + gap;

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                    BlockTableRecord copyBlk = new BlockTableRecord();
                    copyBlk.Name = "AI_COPY_PREVIEW_" + DateTime.Now.Ticks.ToString();
                    ObjectId copyBlkId = bt.Add(copyBlk);
                    tr.AddNewlyCreatedDBObject(copyBlk, true);

                    IdMapping idMap = new IdMapping();
                    db.DeepCloneObjects(ids, copyBlkId, idMap, false);

                    BlockReference br = new BlockReference(new Point3d(shiftX, shiftY, 0.0), copyBlkId);
                    br.Layer = ReviewLayerName;
                    br.ColorIndex = 1;
                    _lastInsertedBlockRefId = modelSpace.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);

                    copiedCount = ids.Count;
                    tr.Commit();
                }

                ZoomExtents(doc);
                description = string.Format(
                    "تم عمل نسخة كاملة من المخطط المفتوح ({0} عنصر) ووضعها بجانبه بإزاحة ({1:0.#}, {2:0.#}) داخل طبقة المراجعة الأحمر (_AI_PREVIEW_REVIEW).\n" +
                    "💡 إذا كنت ترغب في حفظ نسخة كملف DWG منفصل على الهارد، فقط اكتب «احفظ نسخة ملف».",
                    copiedCount, shiftX, shiftY);
                return true;
            }
            catch (System.Exception ex)
            {
                errorMessage = "تعذر نسخ المخطط: " + ex.Message;
                return false;
            }
        }

        public static bool DrawHouseConcept(Document doc, out string errorMessage)
        {
            errorMessage = null;
            if (doc == null) { errorMessage = "لا يوجد مستند أوتوكاد نشط."; return false; }
            try
            {
                string repoRoot = ResolveRepoRoot();
                string outDir = ResolveOutputDirectory(doc, repoRoot, "apartment_150sqm");
                Directory.CreateDirectory(outDir);
                string dxfPath = Path.Combine(outDir, "house-150sqm-egyptian-code-concept-review.dxf");
                if (!File.Exists(dxfPath))
                {
                    string pyExe = ResolvePythonExecutable(repoRoot);
                    string script = Path.Combine(Path.Combine(repoRoot, "integrations"), "build_house_150sqm_preview.py");
                    if (!string.IsNullOrEmpty(pyExe) && File.Exists(pyExe) && File.Exists(script))
                    {
                        RunPythonScriptWithTimeout(
                            pyExe,
                            "\"" + script + "\" --output-dir \"" + outDir + "\"",
                            repoRoot,
                            10000);
                    }
                }

                if (!File.Exists(dxfPath))
                {
                    errorMessage = "ملف DXF غير موجود في المسار: " + dxfPath;
                    return false;
                }

                return InsertDxfDirectly(doc, dxfPath, out errorMessage);
            }
            catch (System.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool InsertDxfDirectly(Document doc, string dxfPath, out string errorMessage)
        {
            errorMessage = null;
            if (doc == null) { errorMessage = "لا يوجد مستند نشط"; return false; }
            if (!File.Exists(dxfPath)) { errorMessage = "ملف DXF غير موجود: " + dxfPath; return false; }

            try
            {
                Database db = doc.Database;
                using (DocumentLock docLock = doc.LockDocument())
                {
                    // 1. Ensure Layer _AI_PREVIEW_REVIEW exists with Color 1 (Red)
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (!lt.Has(ReviewLayerName))
                        {
                            lt.UpgradeOpen();
                            LayerTableRecord ltr = new LayerTableRecord();
                            ltr.Name = ReviewLayerName;
                            ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, 1); // Red
                            lt.Add(ltr);
                            tr.AddNewlyCreatedDBObject(ltr, true);
                        }
                        tr.Commit();
                    }

                    // 2. Read DXF into a side database and sanitize linetypes/layers so Insert never throws eInvalidLayer
                    using (Database sideDb = new Database(false, true))
                    {
                        sideDb.DxfIn(dxfPath, null);
                        using (Transaction sideTr = sideDb.TransactionManager.StartTransaction())
                        {
                            LayerTable sideLt = (LayerTable)sideTr.GetObject(sideDb.LayerTableId, OpenMode.ForRead);
                            foreach (ObjectId lid in sideLt)
                            {
                                LayerTableRecord ltr = sideTr.GetObject(lid, OpenMode.ForWrite) as LayerTableRecord;
                                if (ltr == null) continue;
                                if (ltr.LinetypeObjectId.IsNull || ltr.LinetypeObjectId.IsErased)
                                {
                                    ltr.LinetypeObjectId = sideDb.ContinuousLinetype;
                                }
                            }
                            sideTr.Commit();
                        }

                        // 3. Insert sideDb into doc.Database as a block
                        string blockName = "AI_PREVIEW_" + DateTime.Now.Ticks.ToString();
                        ObjectId blockId = db.Insert(blockName, sideDb, true);

                        // 4. Create BlockReference in ModelSpace (re-ensure review layer exists)
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            LayerTable ltCheck = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                            if (!ltCheck.Has(ReviewLayerName))
                            {
                                ltCheck.UpgradeOpen();
                                LayerTableRecord ltr = new LayerTableRecord();
                                ltr.Name = ReviewLayerName;
                                ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
                                ltCheck.Add(ltr);
                                tr.AddNewlyCreatedDBObject(ltr, true);
                            }

                            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                            BlockReference br = new BlockReference(Point3d.Origin, blockId);
                            br.Layer = ReviewLayerName;
                            br.ColorIndex = 1; // Red

                            _lastInsertedBlockRefId = modelSpace.AppendEntity(br);
                            tr.AddNewlyCreatedDBObject(br, true);

                            tr.Commit();
                        }
                    }
                }

                ZoomExtents(doc);
                return true;
            }
            catch (System.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static void ApproveReview(Document doc)
        {
            if (doc == null) return;
            try
            {
                Database db = doc.Database;
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                    if (_lastInsertedBlockRefId != ObjectId.Null && !_lastInsertedBlockRefId.IsErased)
                    {
                        BlockReference br = tr.GetObject(_lastInsertedBlockRefId, OpenMode.ForWrite) as BlockReference;
                        if (br != null)
                        {
                            DBObjectCollection exploded = new DBObjectCollection();
                            br.Explode(exploded);
                            foreach (DBObject obj in exploded)
                            {
                                Entity ent = obj as Entity;
                                if (ent != null)
                                {
                                    modelSpace.AppendEntity(ent);
                                    tr.AddNewlyCreatedDBObject(ent, true);
                                }
                            }
                            br.Erase(true);
                            _lastInsertedBlockRefId = ObjectId.Null;
                        }
                    }
                    else
                    {
                        foreach (ObjectId id in modelSpace)
                        {
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent != null && ent.Layer == ReviewLayerName)
                            {
                                ent.UpgradeOpen();
                                ent.ColorIndex = 256; // ByLayer
                                ent.Layer = "0";
                            }
                        }
                    }
                    tr.Commit();
                }
                doc.Editor.Regen();
                doc.Editor.UpdateScreen();
            }
            catch { }
        }

        public static bool HasReviewEntities(Document doc)
        {
            if (doc == null) return false;
            try
            {
                Database db = doc.Database;
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (_lastInsertedBlockRefId != ObjectId.Null && !_lastInsertedBlockRefId.IsErased)
                    {
                        BlockReference br = tr.GetObject(_lastInsertedBlockRefId, OpenMode.ForRead) as BlockReference;
                        if (br != null) { tr.Commit(); return true; }
                    }
                    BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    foreach (ObjectId id in modelSpace)
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent != null && ent.Layer == ReviewLayerName)
                        {
                            tr.Commit();
                            return true;
                        }
                    }
                    tr.Commit();
                }
            }
            catch { }
            return false;
        }

        public static void RollbackReview(Document doc)
        {
            if (doc == null) return;
            try
            {
                Database db = doc.Database;
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                    if (_lastInsertedBlockRefId != ObjectId.Null && !_lastInsertedBlockRefId.IsErased)
                    {
                        BlockReference br = tr.GetObject(_lastInsertedBlockRefId, OpenMode.ForWrite) as BlockReference;
                        if (br != null)
                        {
                            br.Erase(true);
                            _lastInsertedBlockRefId = ObjectId.Null;
                        }
                    }

                    foreach (ObjectId id in modelSpace)
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent != null && ent.Layer == ReviewLayerName)
                        {
                            ent.UpgradeOpen();
                            ent.Erase(true);
                        }
                    }
                    tr.Commit();
                }
                doc.Editor.Regen();
                doc.Editor.UpdateScreen();
            }
            catch { }
        }

        public static void ZoomExtents(Document doc)
        {
            if (doc == null) return;
            try
            {
                Database db = doc.Database;
                Extents3d? modelExtents = null;
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    db.UpdateExt(true);
                    BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    foreach (ObjectId id in modelSpace)
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        try
                        {
                            Extents3d ext = ent.GeometricExtents;
                            if (!modelExtents.HasValue) modelExtents = ext;
                            else
                            {
                                Extents3d cur = modelExtents.Value;
                                cur.AddExtents(ext);
                                modelExtents = cur;
                            }
                        }
                        catch { }
                    }
                    tr.Commit();
                }

                if (modelExtents.HasValue)
                {
                    Extents3d ext = modelExtents.Value;
                    using (ViewTableRecord view = doc.Editor.GetCurrentView())
                    {
                        Matrix3d matWCS2DCS = Matrix3d.PlaneToWorld(view.ViewDirection);
                        matWCS2DCS = Matrix3d.Displacement(view.Target - Point3d.Origin) * matWCS2DCS;
                        matWCS2DCS = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * matWCS2DCS;
                        matWCS2DCS = matWCS2DCS.Inverse();

                        ext.TransformBy(matWCS2DCS);
                        double w = Math.Max(10.0, (ext.MaxPoint.X - ext.MinPoint.X) * 1.15);
                        double h = Math.Max(10.0, (ext.MaxPoint.Y - ext.MinPoint.Y) * 1.15);
                        double aspect = view.Width / Math.Max(1e-6, view.Height);
                        if (w / h > aspect)
                            h = w / aspect;
                        else
                            w = h * aspect;

                        view.Width = w;
                        view.Height = h;
                        view.CenterPoint = new Point2d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0);
                        doc.Editor.SetCurrentView(view);
                    }
                    doc.Editor.Regen();
                    doc.Editor.UpdateScreen();
                    return;
                }
            }
            catch { }

            try
            {
                dynamic acadApp = Application.AcadApplication;
                acadApp.ZoomExtents();
            }
            catch
            {
                try
                {
                    if (doc.Editor.IsQuiescent)
                        doc.SendStringToExecute("._ZOOM _E\n", true, false, false);
                }
                catch { }
            }
        }
    }
}
