using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        // ==================== Phase 3: Persistent Project Legend Spec Database ====================

        private sealed class ProjectLegendSpecItem
        {
            public string BlockName = "";
            public string Description = "";
            public double? Watt;
            public double? Va;
            public double? VoltageV;
            public double? PowerFactor;
            public bool IsEmergency;
            public string InheritedFrom = "";
            public string Category = "lighting";
            public string Source = "drawing_legend";
            public string UpdatedUtc = "";
        }

        private static readonly Dictionary<IntPtr, Dictionary<string, ProjectLegendSpecItem>> s_legendCatalogByDb =
            new Dictionary<IntPtr, Dictionary<string, ProjectLegendSpecItem>>();
        private static readonly HashSet<IntPtr> s_legendLoadedForDb =
            new HashSet<IntPtr>();

        private static Dictionary<string, object> LegendSpecToDict(ProjectLegendSpecItem item)
        {
            Dictionary<string, object> d = new Dictionary<string, object>
            {
                { "block_name", item.BlockName ?? "" },
                { "description", item.Description ?? "" },
                { "watt", item.Watt.HasValue ? (object)Math.Round(item.Watt.Value, 2) : null },
                { "va", item.Va.HasValue ? (object)Math.Round(item.Va.Value, 2) : null },
                { "voltage_v", item.VoltageV.HasValue ? (object)Math.Round(item.VoltageV.Value, 1) : null },
                { "power_factor", item.PowerFactor.HasValue ? (object)Math.Round(item.PowerFactor.Value, 3) : null },
                { "is_emergency", item.IsEmergency },
                { "inherited_from", item.InheritedFrom ?? "" },
                { "category", item.Category ?? "lighting" },
                { "source", item.Source ?? "drawing_legend" },
                { "updated_utc", item.UpdatedUtc ?? "" }
            };
            return d;
        }

        private static ProjectLegendSpecItem LegendSpecFromDict(string keyHint, Dictionary<string, object> d)
        {
            if (d == null) return null;
            string bn = GetStr(d, "block_name") ?? keyHint ?? "";
            if (string.IsNullOrWhiteSpace(bn)) return null;
            return new ProjectLegendSpecItem
            {
                BlockName = bn.Trim(),
                Description = GetStr(d, "description") ?? "",
                Watt = GetNullableDouble(d, "watt"),
                Va = GetNullableDouble(d, "va"),
                VoltageV = GetNullableDouble(d, "voltage_v"),
                PowerFactor = GetNullableDouble(d, "power_factor"),
                IsEmergency = GetBool(d, "is_emergency", false),
                InheritedFrom = GetStr(d, "inherited_from") ?? "",
                Category = !string.IsNullOrWhiteSpace(GetStr(d, "category")) ? GetStr(d, "category").Trim().ToLowerInvariant() : "lighting",
                Source = !string.IsNullOrWhiteSpace(GetStr(d, "source")) ? GetStr(d, "source").Trim() : "drawing_legend",
                UpdatedUtc = GetStr(d, "updated_utc") ?? ""
            };
        }

        private static ProjectLegendSpecItem CloneLegendSpec(ProjectLegendSpecItem s)
        {
            if (s == null) return null;
            return new ProjectLegendSpecItem
            {
                BlockName = s.BlockName ?? "",
                Description = s.Description ?? "",
                Watt = s.Watt,
                Va = s.Va,
                VoltageV = s.VoltageV,
                PowerFactor = s.PowerFactor,
                IsEmergency = s.IsEmergency,
                InheritedFrom = s.InheritedFrom ?? "",
                Category = s.Category ?? "lighting",
                Source = s.Source ?? "drawing_legend",
                UpdatedUtc = s.UpdatedUtc ?? ""
            };
        }

        private static bool IsProjectTempOrProtectedDir(string dirPath)
        {
            if (string.IsNullOrWhiteSpace(dirPath)) return true;
            try
            {
                string full = Path.GetFullPath(dirPath).TrimEnd('\\', '/') + "\\";
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(pf) && full.StartsWith(pf.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(pf86) && full.StartsWith(pf86.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(win) && full.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                string tmp = Path.GetTempPath();
                if (!string.IsNullOrEmpty(tmp) && full.StartsWith(tmp.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }
            return false;
        }

        private static string ComputeShortPathHash(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes((text ?? "").ToLowerInvariant()));
                StringBuilder sb = new StringBuilder(8);
                for (int i = 0; i < 4 && i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static void ResolveProjectDbPaths(
            Document doc,
            Database db,
            out string primaryPath,
            out string portableDwgMirrorPath,
            out string indexPath,
            out string dwgPathOut)
        {
            primaryPath = null;
            portableDwgMirrorPath = null;
            indexPath = null;
            dwgPathOut = "";
            try
            {
                string docPath = (db != null && !string.IsNullOrWhiteSpace(db.Filename))
                    ? db.Filename
                    : (doc != null ? doc.Name : "");
                if (string.IsNullOrWhiteSpace(docPath) || !Path.IsPathRooted(docPath) || !File.Exists(docPath) ||
                    !string.Equals(Path.GetExtension(docPath), ".dwg", StringComparison.OrdinalIgnoreCase))
                    return;

                string fullDoc = Path.GetFullPath(docPath);
                dwgPathOut = fullDoc;
                string dwgDir = Path.GetDirectoryName(fullDoc) ?? "";
                string folderName = Path.GetFileName(dwgDir.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(folderName))
                    folderName = Path.GetFileNameWithoutExtension(fullDoc);
                string slug = Regex.Replace(folderName ?? "project", @"[^A-Za-z0-9_\-]+", "_").Trim('_');
                if (string.IsNullOrWhiteSpace(slug)) slug = "project";
                if (slug.Length > 36) slug = slug.Substring(0, 36);

                string keyBasis = !string.IsNullOrEmpty(dwgDir) && !IsProjectTempOrProtectedDir(dwgDir) ? dwgDir : fullDoc;
                string hash = ComputeShortPathHash(keyBasis);

                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localApp))
                {
                    string rootDbDir = Path.Combine(Path.Combine(localApp, "AI-Engineer"), "project_db");
                    string projDir = Path.Combine(rootDbDir, slug + "_" + hash);
                    primaryPath = Path.Combine(projDir, "project_database.json");
                    indexPath = Path.Combine(rootDbDir, "projects_index.json");
                }

                if (!string.IsNullOrEmpty(dwgDir) && !IsProjectTempOrProtectedDir(dwgDir))
                    portableDwgMirrorPath = Path.Combine(dwgDir, ".ai-engineer-project-db.json");
            }
            catch { }
        }

        private static void EnsureProjectLegendDbLoadedLocked(Document doc, Database db)
        {
            if (db == null || db.UnmanagedObject == IntPtr.Zero) return;
            IntPtr dbPtr = db.UnmanagedObject;
            if (s_legendLoadedForDb.Contains(dbPtr)) return;
            s_legendLoadedForDb.Add(dbPtr);

            Dictionary<string, ProjectLegendSpecItem> cat;
            if (!s_legendCatalogByDb.TryGetValue(dbPtr, out cat))
            {
                cat = new Dictionary<string, ProjectLegendSpecItem>(StringComparer.OrdinalIgnoreCase);
                s_legendCatalogByDb[dbPtr] = cat;
            }

            try
            {
                string primaryPath, mirrorPath, indexPath, docPath;
                ResolveProjectDbPaths(doc, db, out primaryPath, out mirrorPath, out indexPath, out docPath);
                string readPath = (!string.IsNullOrEmpty(primaryPath) && File.Exists(primaryPath))
                    ? primaryPath
                    : ((!string.IsNullOrEmpty(mirrorPath) && File.Exists(mirrorPath)) ? mirrorPath : null);
                if (string.IsNullOrEmpty(readPath)) return;

                string raw = File.ReadAllText(readPath, Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> root = ser.DeserializeObject(raw) as Dictionary<string, object>;
                if (root == null) return;

                Dictionary<string, object> legMap = root.ContainsKey("legend_catalog")
                    ? (root["legend_catalog"] as Dictionary<string, object>)
                    : null;
                if (legMap != null)
                {
                    foreach (KeyValuePair<string, object> kv in legMap)
                    {
                        Dictionary<string, object> ld = kv.Value as Dictionary<string, object>;
                        ProjectLegendSpecItem parsed = LegendSpecFromDict(kv.Key, ld);
                        if (parsed != null && !string.IsNullOrEmpty(parsed.BlockName))
                            cat[parsed.BlockName] = parsed;
                    }
                }
            }
            catch { }
        }

        private static void SaveProjectDatabaseToDiskLocked(Document doc, Database db)
        {
            try
            {
                if (db == null || db.UnmanagedObject == IntPtr.Zero) return;
                IntPtr dbPtr = db.UnmanagedObject;
                string primaryPath, mirrorPath, indexPath, docPath;
                ResolveProjectDbPaths(doc, db, out primaryPath, out mirrorPath, out indexPath, out docPath);
                if (string.IsNullOrEmpty(primaryPath) && string.IsNullOrEmpty(mirrorPath)) return;

                EnsureSidecarLoadedLocked(doc, db);
                EnsureProjectLegendDbLoadedLocked(doc, db);

                Dictionary<string, ProjectLegendSpecItem> cat;
                if (!s_legendCatalogByDb.TryGetValue(dbPtr, out cat))
                    cat = new Dictionary<string, ProjectLegendSpecItem>(StringComparer.OrdinalIgnoreCase);

                Dictionary<string, object> legSerialized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, ProjectLegendSpecItem> kv in cat)
                {
                    if (kv.Value != null) legSerialized[kv.Value.BlockName] = LegendSpecToDict(kv.Value);
                }

                Dictionary<string, object> circSerialized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, SavedCircuitRecord> circBucket;
                if (s_savedCircuitsByDb.TryGetValue(dbPtr, out circBucket) && circBucket != null)
                {
                    foreach (KeyValuePair<string, SavedCircuitRecord> kv in circBucket)
                    {
                        SavedCircuitRecord r = kv.Value;
                        if (r == null) continue;
                        circSerialized[kv.Key] = SavedCircuitToDict(r);
                    }
                }

                string dwgDir = !string.IsNullOrEmpty(docPath) ? (Path.GetDirectoryName(docPath) ?? "") : "";
                string utcNow = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    { "schema_version", 1 },
                    { "drawing_path", docPath },
                    { "drawing_name", !string.IsNullOrEmpty(docPath) ? Path.GetFileName(docPath) : "" },
                    { "project_dir", dwgDir },
                    { "updated_utc", utcNow },
                    { "legend_catalog", legSerialized },
                    { "circuits", circSerialized }
                };

                JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
                string json = ser.Serialize(payload);

                if (!string.IsNullOrEmpty(primaryPath))
                {
                    string dir = Path.GetDirectoryName(primaryPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(primaryPath, json, Encoding.UTF8);
                }
                if (!string.IsNullOrEmpty(mirrorPath))
                {
                    try { File.WriteAllText(mirrorPath, json, Encoding.UTF8); } catch { }
                }

                if (!string.IsNullOrEmpty(indexPath) && !string.IsNullOrEmpty(primaryPath))
                {
                    try
                    {
                        string idxDir = Path.GetDirectoryName(indexPath);
                        if (!string.IsNullOrEmpty(idxDir)) Directory.CreateDirectory(idxDir);
                        Dictionary<string, object> idxRoot = null;
                        if (File.Exists(indexPath))
                        {
                            try { idxRoot = ser.DeserializeObject(File.ReadAllText(indexPath, Encoding.UTF8)) as Dictionary<string, object>; }
                            catch { }
                        }
                        if (idxRoot == null) idxRoot = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        string idxKey = !string.IsNullOrEmpty(dwgDir) ? dwgDir : docPath;
                        idxRoot[idxKey] = new Dictionary<string, object>
                        {
                            { "project_db_path", primaryPath },
                            { "portable_db_path", mirrorPath ?? "" },
                            { "last_drawing", docPath },
                            { "legend_count", legSerialized.Count },
                            { "circuits_count", circSerialized.Count },
                            { "updated_utc", utcNow }
                        };
                        File.WriteAllText(indexPath, ser.Serialize(idxRoot), Encoding.UTF8);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool TryGetProjectLegendSpec(Document doc, string blockName, out ProjectLegendSpecItem spec)
        {
            spec = null;
            if (doc == null || doc.Database == null || string.IsNullOrWhiteSpace(blockName)) return false;
            Database db = doc.Database;
            IntPtr dbPtr = db.UnmanagedObject;
            if (dbPtr == IntPtr.Zero) return false;
            string key = blockName.Trim();

            lock (s_lastCoordLock)
            {
                EnsureProjectLegendDbLoadedLocked(doc, db);
                Dictionary<string, ProjectLegendSpecItem> cat;
                if (!s_legendCatalogByDb.TryGetValue(dbPtr, out cat) || cat == null) return false;
                ProjectLegendSpecItem hit;
                if (cat.TryGetValue(key, out hit) && hit != null)
                {
                    spec = CloneLegendSpec(hit);
                    return true;
                }
                string norm = Regex.Replace(key.ToUpperInvariant(), @"[\s_\-]+", "");
                foreach (KeyValuePair<string, ProjectLegendSpecItem> kv in cat)
                {
                    if (kv.Value == null) continue;
                    string kNorm = Regex.Replace(kv.Key.ToUpperInvariant(), @"[\s_\-]+", "");
                    if (kNorm == norm)
                    {
                        spec = CloneLegendSpec(kv.Value);
                        return true;
                    }
                }
                return false;
            }
        }

        private static void ComputeCircuitLoadFromProjectDb(
            Document doc,
            Dictionary<string, int> countsByBlock,
            double? explicitFixtureWatt,
            double? explicitFixtureVa,
            double fallbackFixtureVa,
            double powerFactor,
            out double totalKw,
            out double totalKva,
            out bool loadAssumed,
            out string loadSource,
            out Dictionary<string, object> rowLoadByBlock,
            out List<string> missingCatalogBlocks,
            out Dictionary<string, object> specBreakdown)
        {
            totalKw = 0.0;
            totalKva = 0.0;
            loadAssumed = false;
            loadSource = "default_40va_assumed";
            rowLoadByBlock = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            missingCatalogBlocks = new List<string>();
            specBreakdown = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            double pf = Math.Max(0.4, Math.Min(1.0, powerFactor));
            double safeFallbackVa = Math.Max(1.0, fallbackFixtureVa);
            if (countsByBlock == null || countsByBlock.Count == 0) return;

            if (explicitFixtureWatt.HasValue && explicitFixtureWatt.Value > 0)
            {
                loadSource = "explicit_watt";
                foreach (KeyValuePair<string, int> kv in countsByBlock)
                {
                    if (kv.Value <= 0 || kv.Key.StartsWith("__", StringComparison.Ordinal)) continue;
                    double w = kv.Value * explicitFixtureWatt.Value;
                    double kw = w / 1000.0;
                    double kva = kw / pf;
                    totalKw += kw;
                    totalKva += kva;
                    rowLoadByBlock[kv.Key] = Math.Round(w, 0).ToString(CultureInfo.InvariantCulture) + " W (" +
                        kv.Value.ToString(CultureInfo.InvariantCulture) + "x" +
                        Math.Round(explicitFixtureWatt.Value, 1).ToString("0.#", CultureInfo.InvariantCulture) + "W)";
                    specBreakdown[kv.Key] = new Dictionary<string, object>
                    {
                        { "qty", kv.Value },
                        { "unit_watt", Math.Round(explicitFixtureWatt.Value, 2) },
                        { "total_watt", Math.Round(w, 2) },
                        { "source", "explicit_watt" }
                    };
                }
                return;
            }

            if (explicitFixtureVa.HasValue && explicitFixtureVa.Value > 0)
            {
                loadSource = "explicit_va";
                foreach (KeyValuePair<string, int> kv in countsByBlock)
                {
                    if (kv.Value <= 0 || kv.Key.StartsWith("__", StringComparison.Ordinal)) continue;
                    double va = kv.Value * explicitFixtureVa.Value;
                    double kva = va / 1000.0;
                    double kw = kva * pf;
                    totalKva += kva;
                    totalKw += kw;
                    rowLoadByBlock[kv.Key] = Math.Round(va, 0).ToString(CultureInfo.InvariantCulture) + " VA (" +
                        kv.Value.ToString(CultureInfo.InvariantCulture) + "x" +
                        Math.Round(explicitFixtureVa.Value, 1).ToString("0.#", CultureInfo.InvariantCulture) + "VA)";
                    specBreakdown[kv.Key] = new Dictionary<string, object>
                    {
                        { "qty", kv.Value },
                        { "unit_va", Math.Round(explicitFixtureVa.Value, 2) },
                        { "total_va", Math.Round(va, 2) },
                        { "source", "explicit_va" }
                    };
                }
                return;
            }

            bool matchedAny = false;
            foreach (KeyValuePair<string, int> kv in countsByBlock)
            {
                if (kv.Value <= 0 || kv.Key.StartsWith("__", StringComparison.Ordinal)) continue;
                ProjectLegendSpecItem spec;
                if (TryGetProjectLegendSpec(doc, kv.Key, out spec) && spec != null)
                {
                    if (spec.Watt.HasValue && spec.Watt.Value > 0)
                    {
                        matchedAny = true;
                        double w = kv.Value * spec.Watt.Value;
                        double kw = w / 1000.0;
                        double blkPf = (spec.PowerFactor.HasValue && spec.PowerFactor.Value >= 0.4 && spec.PowerFactor.Value <= 1.0)
                            ? spec.PowerFactor.Value
                            : pf;
                        double kva = kw / blkPf;
                        totalKw += kw;
                        totalKva += kva;
                        rowLoadByBlock[kv.Key] = Math.Round(w, 0).ToString(CultureInfo.InvariantCulture) + " W (" +
                            kv.Value.ToString(CultureInfo.InvariantCulture) + "x" +
                            Math.Round(spec.Watt.Value, 1).ToString("0.#", CultureInfo.InvariantCulture) + "W)";
                        specBreakdown[kv.Key] = new Dictionary<string, object>
                        {
                            { "qty", kv.Value },
                            { "unit_watt", Math.Round(spec.Watt.Value, 2) },
                            { "total_watt", Math.Round(w, 2) },
                            { "source", spec.Source },
                            { "inherited_from", spec.InheritedFrom ?? "" },
                            { "description", spec.Description ?? "" }
                        };
                        continue;
                    }
                    if (spec.Va.HasValue && spec.Va.Value > 0)
                    {
                        matchedAny = true;
                        double va = kv.Value * spec.Va.Value;
                        double kva = va / 1000.0;
                        double blkPf = (spec.PowerFactor.HasValue && spec.PowerFactor.Value >= 0.4 && spec.PowerFactor.Value <= 1.0)
                            ? spec.PowerFactor.Value
                            : pf;
                        double kw = kva * blkPf;
                        totalKva += kva;
                        totalKw += kw;
                        rowLoadByBlock[kv.Key] = Math.Round(va, 0).ToString(CultureInfo.InvariantCulture) + " VA (" +
                            kv.Value.ToString(CultureInfo.InvariantCulture) + "x" +
                            Math.Round(spec.Va.Value, 1).ToString("0.#", CultureInfo.InvariantCulture) + "VA)";
                        specBreakdown[kv.Key] = new Dictionary<string, object>
                        {
                            { "qty", kv.Value },
                            { "unit_va", Math.Round(spec.Va.Value, 2) },
                            { "total_va", Math.Round(va, 2) },
                            { "source", spec.Source },
                            { "inherited_from", spec.InheritedFrom ?? "" },
                            { "description", spec.Description ?? "" }
                        };
                        continue;
                    }
                }

                loadAssumed = true;
                if (!missingCatalogBlocks.Contains(kv.Key)) missingCatalogBlocks.Add(kv.Key);
                double fbVa = kv.Value * safeFallbackVa;
                double fbKva = fbVa / 1000.0;
                double fbKw = fbKva * pf;
                totalKva += fbKva;
                totalKw += fbKw;
                rowLoadByBlock[kv.Key] = "~" + Math.Round(fbVa, 0).ToString(CultureInfo.InvariantCulture) + " VA (افتراضي)";
                specBreakdown[kv.Key] = new Dictionary<string, object>
                {
                    { "qty", kv.Value },
                    { "unit_va", Math.Round(safeFallbackVa, 2) },
                    { "total_va", Math.Round(fbVa, 2) },
                    { "source", "default_40va_assumed" }
                };
            }

            if (missingCatalogBlocks.Count == 0 && matchedAny)
                loadSource = "project_legend_db";
            else if (matchedAny)
                loadSource = "partial_legend_db";
            else
                loadSource = "default_40va_assumed";
        }

        // ==================== Deterministic Legend Text Parser ====================

        private static bool IsAsAboveReferenceText(string desc)
        {
            if (string.IsNullOrWhiteSpace(desc)) return false;
            return Regex.IsMatch(desc, @"\b(AS\s+ABOVE|SAME\s+AS\s+ABOVE|DITTO)\b|مثله|نفس\s+السابق|كما\s+سبق|بالأعلى",
                RegexOptions.IgnoreCase);
        }

        private static string InferCategoryFromLegendText(string blockName, string desc)
        {
            string combined = ((blockName ?? "") + " " + (desc ?? "")).ToUpperInvariant();
            if (combined.Contains("SOCKET") || combined.Contains("OUTLET") || combined.Contains("RECEPTACLE") ||
                combined.Contains("DUPLEX") || combined.Contains("بريزة") || combined.Contains("برايز") ||
                combined.Contains("أفياش") || combined.Contains("فيش"))
                return "socket";
            if (combined.Contains("MOTOR") || combined.Contains("CHILLER") || combined.Contains("AHU") ||
                combined.Contains("FCU") || combined.Contains("FAN") || combined.Contains("PUMP") ||
                combined.Contains("HVAC") || combined.Contains("مضخة") || combined.Contains("مروحة") ||
                combined.Contains("تكييف") || combined.Contains("شيلر"))
                return "hvac";
            if (combined.Contains("PANEL") || combined.Contains("SWITCHBOARD") || combined.Contains("SMDB") ||
                combined.Contains("MDB") || combined.Contains("EMDB") || combined.Contains("لوحة"))
                return "panel";
            return "lighting";
        }

        private static ProjectLegendSpecItem ParseSpecFromLegendDescription(string blockName, string description, string source)
        {
            string desc = (description ?? "").Trim();
            ProjectLegendSpecItem item = new ProjectLegendSpecItem
            {
                BlockName = (blockName ?? "").Trim(),
                Description = desc,
                Source = string.IsNullOrEmpty(source) ? "drawing_legend" : source,
                UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };

            item.IsEmergency = Regex.IsMatch(desc, @"\b(EMERGENCY|BACKUP|BATTERY|EXIT)\b|طوارئ|إخلاء|اخلاء", RegexOptions.IgnoreCase) ||
                (!string.IsNullOrEmpty(blockName) && Regex.IsMatch(blockName, @"([0-9]E$|^APPE$|E$)", RegexOptions.IgnoreCase) &&
                 desc.Length > 0 && desc.ToUpperInvariant().Contains("EMERGENCY"));

            // 1. Compound Wattage e.g. 2x18W, 4 x 18 WATT
            Match mComp = Regex.Match(desc, @"(\d+)\s*[xX×\*]\s*(\d+(?:\.\d+)?)\s*(?:WATTS?|W(?=\b|[\s,;./)]|$)|واط|وات)", RegexOptions.IgnoreCase);
            if (mComp.Success)
            {
                double cnt, unitW;
                if (double.TryParse(mComp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out cnt) &&
                    double.TryParse(mComp.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out unitW) &&
                    cnt > 0 && unitW > 0)
                {
                    item.Watt = Math.Round(cnt * unitW, 2);
                }
            }

            // 2. kW
            if (!item.Watt.HasValue)
            {
                Match mKw = Regex.Match(desc, @"(\d+(?:\.\d+)?)\s*(?:KW(?=\b|[\s,;./)]|$)|كيلو\s*وات|كيلوواط)", RegexOptions.IgnoreCase);
                if (mKw.Success)
                {
                    double kwVal;
                    if (double.TryParse(mKw.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out kwVal) && kwVal > 0)
                        item.Watt = Math.Round(kwVal * 1000.0, 2);
                }
            }

            // 3. Single Wattage e.g. "45 WATT", "22W", "36 وات"
            if (!item.Watt.HasValue)
            {
                Match mWatt = Regex.Match(desc, @"(?:^|[\s,;(/:])(\d+(?:\.\d+)?)\s*(?:WATTS?|W(?=\b|[\s,;./)]|$)|واط|وات)", RegexOptions.IgnoreCase);
                if (mWatt.Success)
                {
                    double wVal;
                    if (double.TryParse(mWatt.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out wVal) && wVal > 0)
                        item.Watt = Math.Round(wVal, 2);
                }
            }

            // 4. kVA / VA
            Match mKva = Regex.Match(desc, @"(?:^|[\s,;(/:])(\d+(?:\.\d+)?)\s*KVA(?=\b|[\s,;./)]|$)", RegexOptions.IgnoreCase);
            if (mKva.Success)
            {
                double kvaVal;
                if (double.TryParse(mKva.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out kvaVal) && kvaVal > 0)
                    item.Va = Math.Round(kvaVal * 1000.0, 2);
            }
            else
            {
                Match mVa = Regex.Match(desc, @"(?:^|[\s,;(/:])(\d+(?:\.\d+)?)\s*VA(?=\b|[\s,;./)]|$)", RegexOptions.IgnoreCase);
                if (mVa.Success)
                {
                    double vaVal;
                    if (double.TryParse(mVa.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out vaVal) && vaVal > 0)
                        item.Va = Math.Round(vaVal, 2);
                }
            }

            // 5. Voltage e.g. 220V / 230V / 380V
            Match mV = Regex.Match(desc, @"(?:^|[\s,;(/:])(220|230|240|380|400|415)\s*V(?=\b|[\s,;./)]|$)", RegexOptions.IgnoreCase);
            if (mV.Success)
            {
                double vVal;
                if (double.TryParse(mV.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out vVal))
                    item.VoltageV = vVal;
            }

            item.Category = InferCategoryFromLegendText(item.BlockName, desc);
            return item;
        }

        // ==================== Deterministic Drawing Legend Extractor ====================

        private sealed class LegendCandidateSymbol
        {
            public string BlockName;
            public double Cx;
            public double Cy;
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
            public double Span;
        }

        private sealed class LegendCandidateText
        {
            public string Text;
            public double Cx;
            public double Cy;
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
            public double Height;
        }

        private static string ExtractLegendEntityText(Entity ent)
        {
            if (ent == null || ent.IsErased) return "";
            if (ent is DBText) return (((DBText)ent).TextString ?? "").Trim();
            if (ent is MText)
            {
                MText mt = (MText)ent;
                string txt = !string.IsNullOrEmpty(mt.Text) ? mt.Text : (mt.Contents ?? "");
                txt = txt.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                return Regex.Replace(txt, @"\s+", " ").Trim();
            }
            return "";
        }

        private static bool IsLegendContainerBlockName(string bn)
        {
            if (string.IsNullOrWhiteSpace(bn)) return false;
            string u = bn.Trim().ToUpperInvariant();
            return u.Contains("LEGEND") || u.Contains("SYMBOL") || u.Contains("KEY_PLAN") || u.Contains("KEYPLAN");
        }

        private static void CollectLegendPrimitivesRecursive(
            Transaction tr,
            BlockTableRecord btr,
            Matrix3d xform,
            int depth,
            bool visibleOnly,
            Dictionary<string, bool> hiddenMap,
            List<LegendCandidateSymbol> symbols,
            List<LegendCandidateText> texts)
        {
            if (btr == null || depth > 3) return;
            foreach (ObjectId oid in btr)
            {
                if (oid.IsErased) continue;
                Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                if (depth == 0 && visibleOnly && !IsEntityVisible(ent, hiddenMap)) continue;

                if (ent is DBText || ent is MText)
                {
                    string txt = ExtractLegendEntityText(ent);
                    if (string.IsNullOrWhiteSpace(txt)) continue;
                    Extents3d ex;
                    try { ex = ent.GeometricExtents; } catch { continue; }
                    if (!IsFiniteBounds(ex)) continue;
                    Point3d pMin = ex.MinPoint.TransformBy(xform);
                    Point3d pMax = ex.MaxPoint.TransformBy(xform);
                    double minX = Math.Min(pMin.X, pMax.X);
                    double minY = Math.Min(pMin.Y, pMax.Y);
                    double maxX = Math.Max(pMin.X, pMax.X);
                    double maxY = Math.Max(pMin.Y, pMax.Y);
                    double h = Math.Max(1e-3, maxY - minY);
                    if (ent is DBText) h = Math.Max(h, ((DBText)ent).Height);
                    else if (ent is MText) h = Math.Max(h, ((MText)ent).TextHeight);
                    texts.Add(new LegendCandidateText
                    {
                        Text = txt,
                        Cx = (minX + maxX) * 0.5,
                        Cy = (minY + maxY) * 0.5,
                        MinX = minX,
                        MinY = minY,
                        MaxX = maxX,
                        MaxY = maxY,
                        Height = h
                    });
                    continue;
                }

                BlockReference br = ent as BlockReference;
                if (br == null) continue;
                string bn = ResolveBlockName(tr, br);
                if (string.IsNullOrEmpty(bn) || IsSystemBlockName(bn) || IsCircuitTerminatorArrowBlock(bn)) continue;

                if (!IsAnonymousBlockName(bn) && !IsLegendContainerBlockName(bn))
                {
                    Extents3d ex;
                    try { ex = br.GeometricExtents; } catch { continue; }
                    if (!IsFiniteBounds(ex)) continue;
                    Point3d pMin = ex.MinPoint.TransformBy(xform);
                    Point3d pMax = ex.MaxPoint.TransformBy(xform);
                    double minX = Math.Min(pMin.X, pMax.X);
                    double minY = Math.Min(pMin.Y, pMax.Y);
                    double maxX = Math.Max(pMin.X, pMax.X);
                    double maxY = Math.Max(pMin.Y, pMax.Y);
                    double sp = Math.Max(maxX - minX, maxY - minY);
                    if (sp > 1e-4)
                    {
                        symbols.Add(new LegendCandidateSymbol
                        {
                            BlockName = bn,
                            Cx = (minX + maxX) * 0.5,
                            Cy = (minY + maxY) * 0.5,
                            MinX = minX,
                            MinY = minY,
                            MaxX = maxX,
                            MaxY = maxY,
                            Span = sp
                        });
                    }
                }
                else if (IsLegendContainerBlockName(bn) || (depth > 0 && IsAnonymousBlockName(bn)))
                {
                    ObjectId defId = GetBlockDefinitionId(br);
                    if (!defId.IsNull)
                    {
                        BlockTableRecord subBtr = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                        if (subBtr != null)
                        {
                            CollectLegendPrimitivesRecursive(
                                tr, subBtr, xform * br.BlockTransform, depth + 1,
                                false, hiddenMap, symbols, texts);
                        }
                    }
                }
            }
        }

        private static List<ProjectLegendSpecItem> ExtractLegendFromEntities(
            Document doc,
            Database db,
            bool fromSelectionOnly,
            bool visibleOnly,
            out int nativeTablesScanned,
            out int candidateSymbolsCount,
            out int candidateTextsCount)
        {
            nativeTablesScanned = 0;
            candidateSymbolsCount = 0;
            candidateTextsCount = 0;
            List<ProjectLegendSpecItem> extracted = new List<ProjectLegendSpecItem>();
            string srcTag = fromSelectionOnly ? "selection_legend" : "drawing_legend";

            List<string> selHandles = fromSelectionOnly ? GetSelectedHandleStrings(doc) : new List<string>();
            if (fromSelectionOnly && selHandles.Count == 0) return extracted;

            List<LegendCandidateSymbol> symbols = new List<LegendCandidateSymbol>();
            List<LegendCandidateText> texts = new List<LegendCandidateText>();
            HashSet<string> allKnownDrawingBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Dictionary<string, bool> hiddenMap = BuildLayerHiddenMap(db, tr);
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    BlockTableRecord btr = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
                    if (btr != null && !btr.IsLayout && !IsAnonymousBlockName(btr.Name) && !IsSystemBlockName(btr.Name))
                        allKnownDrawingBlocks.Add(btr.Name);
                }

                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                List<Entity> scopeEnts = new List<Entity>();
                if (fromSelectionOnly)
                {
                    foreach (string sh in selHandles)
                    {
                        ObjectId oid = ResolveHandle(db, sh);
                        if (oid.IsNull || oid.IsErased) continue;
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent != null && !ent.IsErased && !IsLegendOrHighlightLayer(ent.Layer) && !IsAiAnnotationLayer(ent.Layer))
                            scopeEnts.Add(ent);
                    }
                }
                else
                {
                    foreach (ObjectId mid in ms)
                    {
                        if (mid.IsErased) continue;
                        Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased || IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                        if (visibleOnly && !IsEntityVisible(ent, hiddenMap)) continue;
                        scopeEnts.Add(ent);
                    }
                }

                // A. Check native AutoCAD Table entities first
                for (int i = 0; i < scopeEnts.Count; i++)
                {
                    Table tbl = scopeEnts[i] as Table;
                    if (tbl == null) continue;
                    nativeTablesScanned++;
                    int rows = tbl.Rows.Count;
                    int cols = tbl.Columns.Count;
                    ProjectLegendSpecItem lastTableSpec = null;
                    for (int r = 0; r < rows; r++)
                    {
                        string foundBlk = "";
                        List<string> cellTexts = new List<string>();
                        for (int c = 0; c < cols; c++)
                        {
                            try
                            {
                                ObjectId cellBlkId = tbl.Cells[r, c].BlockTableRecordId;
                                if (!cellBlkId.IsNull)
                                {
                                    BlockTableRecord cbtr = tr.GetObject(cellBlkId, OpenMode.ForRead) as BlockTableRecord;
                                    if (cbtr != null && !IsAnonymousBlockName(cbtr.Name) && !IsSystemBlockName(cbtr.Name))
                                        foundBlk = cbtr.Name;
                                }
                            }
                            catch { }
                            string ct = "";
                            try { ct = (tbl.Cells[r, c].TextString ?? "").Trim(); } catch { }
                            if (!string.IsNullOrWhiteSpace(ct)) cellTexts.Add(ct);
                        }
                        if (string.IsNullOrEmpty(foundBlk))
                        {
                            for (int cti = 0; cti < cellTexts.Count; cti++)
                            {
                                if (allKnownDrawingBlocks.Contains(cellTexts[cti]))
                                {
                                    foundBlk = cellTexts[cti];
                                    break;
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(foundBlk) && cellTexts.Count > 0)
                        {
                            List<string> descParts = new List<string>();
                            for (int cti = 0; cti < cellTexts.Count; cti++)
                            {
                                if (!string.Equals(cellTexts[cti], foundBlk, StringComparison.OrdinalIgnoreCase))
                                    descParts.Add(cellTexts[cti]);
                            }
                            string desc = string.Join(" ", descParts.ToArray()).Trim();
                            ProjectLegendSpecItem parsed = ParseSpecFromLegendDescription(foundBlk, desc, srcTag);
                            if (IsAsAboveReferenceText(desc) && lastTableSpec != null)
                            {
                                if (!parsed.Watt.HasValue && lastTableSpec.Watt.HasValue)
                                {
                                    parsed.Watt = lastTableSpec.Watt;
                                    parsed.InheritedFrom = lastTableSpec.BlockName;
                                }
                                if (!parsed.Va.HasValue && lastTableSpec.Va.HasValue)
                                {
                                    parsed.Va = lastTableSpec.Va;
                                    if (string.IsNullOrEmpty(parsed.InheritedFrom)) parsed.InheritedFrom = lastTableSpec.BlockName;
                                }
                                if (!parsed.VoltageV.HasValue && lastTableSpec.VoltageV.HasValue)
                                    parsed.VoltageV = lastTableSpec.VoltageV;
                                if (parsed.Category == "lighting" && !string.IsNullOrEmpty(lastTableSpec.Category))
                                    parsed.Category = lastTableSpec.Category;
                            }
                            if (parsed.Watt.HasValue || parsed.Va.HasValue) lastTableSpec = parsed;
                            extracted.Add(parsed);
                        }
                    }
                }

                // B. Collect symbols & texts (handling loose entities and nested LEGEND block containers)
                Dictionary<string, int> modelBlockFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < scopeEnts.Count; i++)
                {
                    Entity ent = scopeEnts[i];
                    if (ent is DBText || ent is MText)
                    {
                        string txt = ExtractLegendEntityText(ent);
                        if (string.IsNullOrWhiteSpace(txt)) continue;
                        Extents3d ex;
                        try { ex = ent.GeometricExtents; } catch { continue; }
                        if (!IsFiniteBounds(ex)) continue;
                        double h = Math.Max(1e-3, ex.MaxPoint.Y - ex.MinPoint.Y);
                        if (ent is DBText) h = Math.Max(h, ((DBText)ent).Height);
                        else if (ent is MText) h = Math.Max(h, ((MText)ent).TextHeight);
                        texts.Add(new LegendCandidateText
                        {
                            Text = txt,
                            Cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5,
                            Cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5,
                            MinX = ex.MinPoint.X,
                            MinY = ex.MinPoint.Y,
                            MaxX = ex.MaxPoint.X,
                            MaxY = ex.MaxPoint.Y,
                            Height = h
                        });
                    }
                    else if (ent is BlockReference)
                    {
                        BlockReference br = (BlockReference)ent;
                        string bn = ResolveBlockName(tr, br);
                        if (string.IsNullOrEmpty(bn) || IsSystemBlockName(bn) || IsCircuitTerminatorArrowBlock(bn)) continue;

                        if (IsLegendContainerBlockName(bn) || (fromSelectionOnly && IsAnonymousBlockName(bn)))
                        {
                            ObjectId defId = GetBlockDefinitionId(br);
                            if (!defId.IsNull)
                            {
                                BlockTableRecord subBtr = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                                if (subBtr != null)
                                    CollectLegendPrimitivesRecursive(tr, subBtr, br.BlockTransform, 1, false, hiddenMap, symbols, texts);
                            }
                        }
                        else if (!IsAnonymousBlockName(bn) && !IsPanelOrFrameBlockName(bn))
                        {
                            int curC = 0;
                            modelBlockFreq.TryGetValue(bn, out curC);
                            modelBlockFreq[bn] = curC + 1;

                            Extents3d ex;
                            try { ex = br.GeometricExtents; } catch { continue; }
                            if (!IsFiniteBounds(ex)) continue;
                            double sp = Math.Max(ex.MaxPoint.X - ex.MinPoint.X, ex.MaxPoint.Y - ex.MinPoint.Y);
                            if (sp <= 1e-4) continue;
                            symbols.Add(new LegendCandidateSymbol
                            {
                                BlockName = bn,
                                Cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5,
                                Cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5,
                                MinX = ex.MinPoint.X,
                                MinY = ex.MinPoint.Y,
                                MaxX = ex.MaxPoint.X,
                                MaxY = ex.MaxPoint.Y,
                                Span = sp
                            });
                        }
                    }
                }
                tr.Commit();
            }

            candidateSymbolsCount = symbols.Count;
            candidateTextsCount = texts.Count;
            if (symbols.Count == 0 || texts.Count == 0) return extracted;

            // Compute median symbol span to normalize column X tolerance & row-band Y matching
            List<double> spans = new List<double>();
            for (int i = 0; i < symbols.Count; i++) spans.Add(symbols[i].Span);
            spans.Sort();
            double medSpan = spans[spans.Count / 2];
            double colXTol = Math.Max(1e-3, medSpan * 1.4);

            // Detect Legend region anchor texts (used to prioritize legend area when scanning entire drawing)
            List<Point2d> legendHeaderPts = new List<Point2d>();
            for (int i = 0; i < texts.Count; i++)
            {
                string tu = texts[i].Text.ToUpperInvariant();
                if (tu.Contains("LEGEND") || tu.Contains("SYMBOL") || tu.Contains("جدول الرموز") ||
                    tu.Contains("مفاتيح") || tu.Contains("LUMINAIRE SCHEDULE"))
                    legendHeaderPts.Add(new Point2d(texts[i].Cx, texts[i].Cy));
            }

            // Cluster symbols into vertical Legend columns (aligned on Cx)
            symbols.Sort(delegate(LegendCandidateSymbol a, LegendCandidateSymbol b)
            {
                int cx = a.Cx.CompareTo(b.Cx);
                return cx != 0 ? cx : b.Cy.CompareTo(a.Cy);
            });

            List<List<LegendCandidateSymbol>> columns = new List<List<LegendCandidateSymbol>>();
            for (int i = 0; i < symbols.Count; i++)
            {
                LegendCandidateSymbol s = symbols[i];
                bool placed = false;
                for (int ci = columns.Count - 1; ci >= 0; ci--)
                {
                    List<LegendCandidateSymbol> col = columns[ci];
                    if (Math.Abs(s.Cx - col[0].Cx) <= colXTol)
                    {
                        col.Add(s);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    columns.Add(new List<LegendCandidateSymbol> { s });
                }
            }

            Dictionary<string, ProjectLegendSpecItem> bestByBlock = new Dictionary<string, ProjectLegendSpecItem>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectLegendSpecItem nt in extracted)
            {
                if (!string.IsNullOrEmpty(nt.BlockName)) bestByBlock[nt.BlockName] = nt;
            }

            for (int ci = 0; ci < columns.Count; ci++)
            {
                List<LegendCandidateSymbol> col = columns[ci];
                if (!fromSelectionOnly && col.Count < 2) continue;

                // Sort strictly top-to-bottom (Y descending), deduplicating overlapping duplicate symbols at same Y
                col.Sort(delegate(LegendCandidateSymbol a, LegendCandidateSymbol b) { return b.Cy.CompareTo(a.Cy); });
                List<LegendCandidateSymbol> rows = new List<LegendCandidateSymbol>();
                HashSet<string> colDistinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int ri = 0; ri < col.Count; ri++)
                {
                    if (rows.Count > 0 && Math.Abs(rows[rows.Count - 1].Cy - col[ri].Cy) <= medSpan * 0.25)
                        continue;
                    rows.Add(col[ri]);
                    colDistinctNames.Add(col[ri].BlockName);
                }
                if (!fromSelectionOnly && colDistinctNames.Count < 2) continue;

                // Check typical vertical row spacing in this column
                double typRowStep = medSpan * 2.2;
                if (rows.Count >= 2)
                {
                    List<double> gaps = new List<double>();
                    for (int ri = 1; ri < rows.Count; ri++)
                    {
                        double dg = rows[ri - 1].Cy - rows[ri].Cy;
                        if (dg > medSpan * 0.3 && dg < medSpan * 15.0) gaps.Add(dg);
                    }
                    if (gaps.Count > 0)
                    {
                        gaps.Sort();
                        typRowStep = gaps[gaps.Count / 2];
                    }
                }

                // Split sub-clusters if there's a huge empty vertical jump between unrelated floor groups
                double maxDescReachX = Math.Max(medSpan * 45.0, typRowStep * 25.0);
                double nextColMinX = (ci + 1 < columns.Count && columns[ci + 1].Count > 0)
                    ? columns[ci + 1][0].MinX - medSpan * 0.5
                    : double.MaxValue;

                // Score pairs to decide whether descriptions sit mostly right or left of symbols
                int rightHits = 0;
                int leftHits = 0;
                double quickBandY = Math.Max(medSpan * 1.1, typRowStep * 0.55);
                for (int ri = 0; ri < rows.Count; ri++)
                {
                    LegendCandidateSymbol sym = rows[ri];
                    for (int ti = 0; ti < texts.Count; ti++)
                    {
                        LegendCandidateText tx = texts[ti];
                        if (Math.Abs(tx.Cy - sym.Cy) > quickBandY) continue;
                        if (tx.Text.Length < 4) continue;
                        if (tx.MinX >= sym.MaxX - medSpan * 0.2 && tx.MinX <= sym.MaxX + maxDescReachX && tx.Cx < nextColMinX)
                            rightHits++;
                        else if (tx.MaxX <= sym.MinX + medSpan * 0.2 && tx.MaxX >= sym.MinX - maxDescReachX)
                            leftHits++;
                    }
                }
                bool descOnLeft = leftHits > rightHits;
                if (!fromSelectionOnly && rightHits == 0 && leftHits == 0) continue;

                // When scanning whole drawing without selection, verify this vertical group actually has paired spec texts
                int pairedSpecRows = 0;
                ProjectLegendSpecItem lastSpec = null;
                List<ProjectLegendSpecItem> colResults = new List<ProjectLegendSpecItem>();

                for (int ri = 0; ri < rows.Count; ri++)
                {
                    LegendCandidateSymbol sym = rows[ri];
                    double upperBoundY = (ri > 0 && (rows[ri - 1].Cy - sym.Cy) <= typRowStep * 2.2)
                        ? (rows[ri - 1].Cy + sym.Cy) * 0.5
                        : (sym.Cy + Math.Max(medSpan * 0.9, typRowStep * 0.65));
                    double lowerBoundY = (ri + 1 < rows.Count && (sym.Cy - rows[ri + 1].Cy) <= typRowStep * 2.2)
                        ? (sym.Cy + rows[ri + 1].Cy) * 0.5
                        : (sym.Cy - Math.Max(medSpan * 0.9, typRowStep * 0.65));

                    List<LegendCandidateText> rowTexts = new List<LegendCandidateText>();
                    for (int ti = 0; ti < texts.Count; ti++)
                    {
                        LegendCandidateText tx = texts[ti];
                        if (tx.Cy > upperBoundY || tx.Cy < lowerBoundY) continue;
                        if (descOnLeft)
                        {
                            if (tx.MaxX > sym.MinX + medSpan * 0.35 || tx.MaxX < sym.MinX - maxDescReachX) continue;
                        }
                        else
                        {
                            if (tx.MinX < sym.MaxX - medSpan * 0.35 || tx.MinX > sym.MaxX + maxDescReachX) continue;
                            if (nextColMinX < double.MaxValue && tx.MinX >= nextColMinX) continue;
                        }
                        // Ignore pure column header titles
                        string tu = tx.Text.ToUpperInvariant();
                        if (tu == "SYMBOL" || tu == "SYMBOLS" || tu == "DESCRIPTION" || tu == "LEGEND" || tu == "الرمز" || tu == "الوصف")
                            continue;
                        // Ignore exact repeated block-name label alone unless there's no other text
                        rowTexts.Add(tx);
                    }

                    rowTexts.Sort(delegate(LegendCandidateText a, LegendCandidateText b)
                    {
                        if (Math.Abs(b.Cy - a.Cy) > Math.Min(a.Height, b.Height) * 0.45)
                            return b.Cy.CompareTo(a.Cy);
                        return a.MinX.CompareTo(b.MinX);
                    });

                    List<string> parts = new List<string>();
                    for (int pti = 0; pti < rowTexts.Count; pti++)
                    {
                        string ptTxt = rowTexts[pti].Text;
                        if (string.Equals(ptTxt, sym.BlockName, StringComparison.OrdinalIgnoreCase) && rowTexts.Count > 1)
                            continue;
                        parts.Add(ptTxt);
                    }
                    string combinedDesc = Regex.Replace(string.Join(" ", parts.ToArray()), @"\s+", " ").Trim();
                    if (string.IsNullOrWhiteSpace(combinedDesc)) continue;

                    ProjectLegendSpecItem parsed = ParseSpecFromLegendDescription(sym.BlockName, combinedDesc, srcTag);
                    if (IsAsAboveReferenceText(combinedDesc) && lastSpec != null)
                    {
                        if (!parsed.Watt.HasValue && lastSpec.Watt.HasValue)
                        {
                            parsed.Watt = lastSpec.Watt;
                            parsed.InheritedFrom = lastSpec.BlockName;
                        }
                        if (!parsed.Va.HasValue && lastSpec.Va.HasValue)
                        {
                            parsed.Va = lastSpec.Va;
                            if (string.IsNullOrEmpty(parsed.InheritedFrom)) parsed.InheritedFrom = lastSpec.BlockName;
                        }
                        if (!parsed.VoltageV.HasValue && lastSpec.VoltageV.HasValue)
                            parsed.VoltageV = lastSpec.VoltageV;
                        if (parsed.Category == "lighting" && !string.IsNullOrEmpty(lastSpec.Category))
                            parsed.Category = lastSpec.Category;
                    }
                    if (parsed.Watt.HasValue || parsed.Va.HasValue)
                    {
                        lastSpec = parsed;
                        pairedSpecRows++;
                    }
                    colResults.Add(parsed);
                }

                // Require at least 2 spec-bearing or 2 description rows when scanning a whole drawing automatically
                if (!fromSelectionOnly && colResults.Count < 2) continue;
                if (!fromSelectionOnly && pairedSpecRows == 0 && legendHeaderPts.Count == 0) continue;

                for (int ri = 0; ri < colResults.Count; ri++)
                {
                    ProjectLegendSpecItem item = colResults[ri];
                    ProjectLegendSpecItem prev;
                    if (!bestByBlock.TryGetValue(item.BlockName, out prev) ||
                        (!prev.Watt.HasValue && item.Watt.HasValue) ||
                        (item.Description.Length > prev.Description.Length && (item.Watt.HasValue || item.Va.HasValue)))
                    {
                        bestByBlock[item.BlockName] = item;
                    }
                }
            }

            extracted = new List<ProjectLegendSpecItem>(bestByBlock.Values);
            extracted.Sort(delegate(ProjectLegendSpecItem a, ProjectLegendSpecItem b)
            {
                return string.Compare(a.BlockName, b.BlockName, StringComparison.OrdinalIgnoreCase);
            });
            return extracted;
        }

        private static Dictionary<string, object> ManageProjectLegendDb(Document doc, Dictionary<string, object> args)
        {
            if (doc == null || doc.Database == null)
                return Err("لا يوجد ملف رسم نشط في أوتوكاد.");
            if (args == null) args = new Dictionary<string, object>();

            Database db = doc.Database;
            IntPtr dbPtr = db.UnmanagedObject;
            string action = (GetStr(args, "action") ?? "get_catalog").Trim().ToLowerInvariant();
            bool overwriteVerified = GetBool(args, "overwrite_verified", false);
            bool visibleOnly = GetBool(args, "visible_only", true);

            List<string> warnings = new List<string>();
            int upsertedCount = 0;
            int skippedVerifiedCount = 0;
            int extractedRawCount = 0;
            int nativeTablesScanned = 0;
            int candSymbols = 0;
            int candTexts = 0;

            using (DocumentLock docLock = doc.LockDocument())
            {
                if (action == "clear_catalog")
                {
                    lock (s_lastCoordLock)
                    {
                        EnsureProjectLegendDbLoadedLocked(doc, db);
                        s_legendCatalogByDb[dbPtr] = new Dictionary<string, ProjectLegendSpecItem>(StringComparer.OrdinalIgnoreCase);
                        SaveProjectDatabaseToDiskLocked(doc, db);
                    }
                }
                else if (action == "extract_from_drawing" || action == "extract_from_selection")
                {
                    bool fromSel = (action == "extract_from_selection");
                    List<ProjectLegendSpecItem> found = ExtractLegendFromEntities(
                        doc, db, fromSel, visibleOnly,
                        out nativeTablesScanned, out candSymbols, out candTexts);
                    extractedRawCount = found.Count;

                    lock (s_lastCoordLock)
                    {
                        EnsureProjectLegendDbLoadedLocked(doc, db);
                        Dictionary<string, ProjectLegendSpecItem> cat;
                        if (!s_legendCatalogByDb.TryGetValue(dbPtr, out cat))
                        {
                            cat = new Dictionary<string, ProjectLegendSpecItem>(StringComparer.OrdinalIgnoreCase);
                            s_legendCatalogByDb[dbPtr] = cat;
                        }
                        for (int i = 0; i < found.Count; i++)
                        {
                            ProjectLegendSpecItem f = found[i];
                            ProjectLegendSpecItem existing;
                            if (cat.TryGetValue(f.BlockName, out existing) && existing != null &&
                                string.Equals(existing.Source, "user_verified", StringComparison.OrdinalIgnoreCase) &&
                                !overwriteVerified)
                            {
                                skippedVerifiedCount++;
                                continue;
                            }
                            cat[f.BlockName] = CloneLegendSpec(f);
                            upsertedCount++;
                        }
                        SaveProjectDatabaseToDiskLocked(doc, db);
                    }
                    if (found.Count == 0)
                    {
                        warnings.Add(fromSel
                            ? "لم يتم العثور على أزواج رموز ونصوص مواصفات داخل العناصر المحددة. حدد صفوف جدول الليجند (الرمز + الوصف) وأعد المحاولة أو أدخلها عبر set_item."
                            : "لم يتم اكتشاف جدول ليجند قياسي تلقائياً في المخطط. يمكنك تحديد جدول الليجند بالماوس واستدعاء extract_from_selection أو حفظ القيم مباشرة عبر set_item.");
                    }
                }
                else if (action == "set_item" || action == "set_items")
                {
                    List<Dictionary<string, object>> batch = new List<Dictionary<string, object>>();
                    if (action == "set_items")
                    {
                        foreach (object o in AsList(GetObj(args, "items")))
                        {
                            Dictionary<string, object> d = AsDict(o);
                            if (d != null) batch.Add(d);
                        }
                    }
                    else
                    {
                        batch.Add(args);
                    }
                    if (batch.Count == 0)
                        return Err("يرجى تمرير block_name أو مصفوفة items لحفظ مواصفات الكشافات.");

                    lock (s_lastCoordLock)
                    {
                        EnsureProjectLegendDbLoadedLocked(doc, db);
                        Dictionary<string, ProjectLegendSpecItem> cat;
                        if (!s_legendCatalogByDb.TryGetValue(dbPtr, out cat))
                        {
                            cat = new Dictionary<string, ProjectLegendSpecItem>(StringComparer.OrdinalIgnoreCase);
                            s_legendCatalogByDb[dbPtr] = cat;
                        }
                        for (int i = 0; i < batch.Count; i++)
                        {
                            Dictionary<string, object> d = batch[i];
                            string bn = (GetStr(d, "block_name") ?? GetStr(d, "name") ?? "").Trim();
                            if (string.IsNullOrEmpty(bn)) continue;
                            string desc = GetStr(d, "description") ?? "";
                            ProjectLegendSpecItem cur = ParseSpecFromLegendDescription(bn, desc, "user_verified");
                            double? wOpt = GetNullableDouble(d, "watt");
                            double? vaOpt = GetNullableDouble(d, "va");
                            double? vOpt = GetNullableDouble(d, "voltage_v");
                            double? pfOpt = GetNullableDouble(d, "power_factor");
                            if (wOpt.HasValue && wOpt.Value > 0) cur.Watt = Math.Round(wOpt.Value, 2);
                            if (vaOpt.HasValue && vaOpt.Value > 0) cur.Va = Math.Round(vaOpt.Value, 2);
                            if (vOpt.HasValue && vOpt.Value > 0) cur.VoltageV = Math.Round(vOpt.Value, 1);
                            if (pfOpt.HasValue && pfOpt.Value >= 0.4 && pfOpt.Value <= 1.0) cur.PowerFactor = Math.Round(pfOpt.Value, 3);
                            if (d.ContainsKey("is_emergency")) cur.IsEmergency = GetBool(d, "is_emergency", cur.IsEmergency);
                            string catStr = GetStr(d, "category");
                            if (!string.IsNullOrWhiteSpace(catStr)) cur.Category = catStr.Trim().ToLowerInvariant();
                            string srcStr = GetStr(d, "source");
                            if (!string.IsNullOrWhiteSpace(srcStr)) cur.Source = srcStr.Trim();
                            cat[bn] = cur;
                            upsertedCount++;
                        }
                        SaveProjectDatabaseToDiskLocked(doc, db);
                    }
                }
                else if (action == "import_excel")
                {
                    string xlIn = GetStr(args, "excel_path");
                    if (string.IsNullOrWhiteSpace(xlIn) || !File.Exists(xlIn))
                        xlIn = ExcelTableInterop.BrowseOpenSpreadsheetPath(null);
                    if (string.IsNullOrWhiteSpace(xlIn) || !File.Exists(xlIn))
                        return Err("لم يتم العثور على ملف الإكسيل المطلوب استيراده.");

                    ExcelSheetData sheet = ExcelTableInterop.ReadSpreadsheet(xlIn, GetStr(args, "sheet_name"), 500, 12);
                    if (sheet == null || sheet.Rows == null || sheet.Rows.Count == 0)
                        return Err("ملف الإكسيل فارغ أو لا يحتوي على صفوف صالحة.");

                    int idxBlock = 0, idxDesc = 1, idxWatt = -1, idxVa = -1, idxCat = -1;
                    for (int hi = 0; hi < sheet.Headers.Count; hi++)
                    {
                        string h = (sheet.Headers[hi] ?? "").Trim().ToLowerInvariant();
                        if (h.Contains("block") || h.Contains("البلوك") || h.Contains("رمز")) idxBlock = hi;
                        else if (h.Contains("desc") || h.Contains("وصف") || h.Contains("المواصفات")) idxDesc = hi;
                        else if (h.Contains("watt") || h.Contains("وات") || h.Contains("واط") || h == "w") idxWatt = hi;
                        else if (h.Contains("va")) idxVa = hi;
                        else if (h.Contains("cat") || h.Contains("فئة") || h.Contains("نوع")) idxCat = hi;
                    }

                    lock (s_lastCoordLock)
                    {
                        EnsureProjectLegendDbLoadedLocked(doc, db);
                        Dictionary<string, ProjectLegendSpecItem> cat;
                        if (!s_legendCatalogByDb.TryGetValue(dbPtr, out cat))
                        {
                            cat = new Dictionary<string, ProjectLegendSpecItem>(StringComparer.OrdinalIgnoreCase);
                            s_legendCatalogByDb[dbPtr] = cat;
                        }
                        for (int ri = 0; ri < sheet.Rows.Count; ri++)
                        {
                            List<string> row = sheet.Rows[ri];
                            if (row == null || idxBlock >= row.Count) continue;
                            string bn = (row[idxBlock] ?? "").Trim();
                            if (string.IsNullOrEmpty(bn) || bn.StartsWith("الإجمالي", StringComparison.Ordinal)) continue;
                            string desc = (idxDesc >= 0 && idxDesc < row.Count) ? (row[idxDesc] ?? "").Trim() : "";
                            ProjectLegendSpecItem cur = ParseSpecFromLegendDescription(bn, desc, "excel_import");
                            double parsedNum;
                            if (idxWatt >= 0 && idxWatt < row.Count && double.TryParse((row[idxWatt] ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsedNum) && parsedNum > 0)
                                cur.Watt = Math.Round(parsedNum, 2);
                            if (idxVa >= 0 && idxVa < row.Count && double.TryParse((row[idxVa] ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsedNum) && parsedNum > 0)
                                cur.Va = Math.Round(parsedNum, 2);
                            if (idxCat >= 0 && idxCat < row.Count && !string.IsNullOrWhiteSpace(row[idxCat]))
                                cur.Category = row[idxCat].Trim().ToLowerInvariant();
                            cat[bn] = cur;
                            upsertedCount++;
                        }
                        SaveProjectDatabaseToDiskLocked(doc, db);
                    }
                }
            }

            string catFilter = (GetStr(args, "category") ?? "").Trim().ToLowerInvariant();
            string blkFilter = (GetStr(args, "block_name") ?? "").Trim();

            List<ProjectLegendSpecItem> snapshot = new List<ProjectLegendSpecItem>();
            int verifiedCount = 0;
            int savedCircuitsCount = 0;
            string primaryPath, mirrorPath, indexPath, docPath;
            ResolveProjectDbPaths(doc, db, out primaryPath, out mirrorPath, out indexPath, out docPath);

            lock (s_lastCoordLock)
            {
                EnsureProjectLegendDbLoadedLocked(doc, db);
                EnsureSidecarLoadedLocked(doc, db);
                Dictionary<string, ProjectLegendSpecItem> cat;
                if (s_legendCatalogByDb.TryGetValue(dbPtr, out cat) && cat != null)
                {
                    foreach (ProjectLegendSpecItem item in cat.Values)
                    {
                        if (item == null) continue;
                        if (string.Equals(item.Source, "user_verified", StringComparison.OrdinalIgnoreCase)) verifiedCount++;
                        if (!string.IsNullOrEmpty(catFilter) && !string.Equals(item.Category, catFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrEmpty(blkFilter) && !string.Equals(item.BlockName, blkFilter, StringComparison.OrdinalIgnoreCase) &&
                            !MatchesPatternOrSubstring(item.BlockName, blkFilter))
                            continue;
                        snapshot.Add(CloneLegendSpec(item));
                    }
                }
                Dictionary<string, SavedCircuitRecord> cb;
                if (s_savedCircuitsByDb.TryGetValue(dbPtr, out cb) && cb != null)
                    savedCircuitsCount = cb.Count;
            }

            snapshot.Sort(delegate(ProjectLegendSpecItem a, ProjectLegendSpecItem b)
            {
                return string.Compare(a.BlockName, b.BlockName, StringComparison.OrdinalIgnoreCase);
            });

            List<string> missingWattBlocks = new List<string>();
            List<object> itemDicts = new List<object>();
            for (int i = 0; i < snapshot.Count; i++)
            {
                itemDicts.Add(LegendSpecToDict(snapshot[i]));
                if ((!snapshot[i].Watt.HasValue || snapshot[i].Watt.Value <= 0) &&
                    (!snapshot[i].Va.HasValue || snapshot[i].Va.Value <= 0))
                    missingWattBlocks.Add(snapshot[i].BlockName);
            }

            // Also report drawing fixture blocks that are not in the catalog yet when querying catalog
            if (string.IsNullOrEmpty(blkFilter) && string.IsNullOrEmpty(catFilter))
            {
                try
                {
                    HashSet<string> inCatalog = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < snapshot.Count; i++) inCatalog.Add(snapshot[i].BlockName);

                    using (Transaction trScan = db.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord ms = (BlockTableRecord)trScan.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                        Dictionary<string, int> dwgCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (ObjectId mid in ms)
                        {
                            if (mid.IsErased) continue;
                            Entity ent = trScan.GetObject(mid, OpenMode.ForRead) as Entity;
                            BlockReference br = ent as BlockReference;
                            if (br == null || IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                            string bn = ResolveBlockName(trScan, br);
                            if (string.IsNullOrEmpty(bn) || IsAnonymousBlockName(bn) || IsSystemBlockName(bn) ||
                                IsPanelOrFrameBlockName(bn) || IsCircuitTerminatorArrowBlock(bn) || IsPanelDistributionBlockName(bn)) continue;
                            int c = 0;
                            dwgCounts.TryGetValue(bn, out c);
                            dwgCounts[bn] = c + 1;
                        }
                        foreach (KeyValuePair<string, int> kv in dwgCounts)
                        {
                            if (kv.Value >= 2 && !inCatalog.Contains(kv.Key) && !missingWattBlocks.Contains(kv.Key))
                                missingWattBlocks.Add(kv.Key);
                        }
                        trScan.Commit();
                    }
                }
                catch { }
            }

            string excelOutPath = null;
            bool excelExported = false;
            if (action == "export_excel" || GetBool(args, "export_excel", false))
            {
                ExcelSheetData xl = new ExcelSheetData
                {
                    Title = "قاعدة بيانات مواصفات وقدرات بلوكات المشروع (Project Legend Database)",
                    SheetName = "Project_Legend_DB",
                    Headers = new List<string> { "م", "اسم البلوك", "الفئة", "القدرة (W)", "القدرة (VA)", "الجهد (V)", "طوارئ", "موروث من", "المصدر", "الوصف الهندسي" }
                };
                for (int i = 0; i < snapshot.Count; i++)
                {
                    ProjectLegendSpecItem s = snapshot[i];
                    xl.Rows.Add(new List<string>
                    {
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        s.BlockName,
                        s.Category,
                        s.Watt.HasValue ? s.Watt.Value.ToString("0.##", CultureInfo.InvariantCulture) : "",
                        s.Va.HasValue ? s.Va.Value.ToString("0.##", CultureInfo.InvariantCulture) : "",
                        s.VoltageV.HasValue ? s.VoltageV.Value.ToString("0.#", CultureInfo.InvariantCulture) : "",
                        s.IsEmergency ? "نعم (Emergency)" : "لا",
                        s.InheritedFrom ?? "",
                        s.Source ?? "",
                        s.Description ?? ""
                    });
                }
                xl.FooterRow = new List<string>
                {
                    "الإجمالي", snapshot.Count.ToString(CultureInfo.InvariantCulture) + " بلوك", "", "", "", "", "", "", "", ""
                };
                string reqXl = GetStr(args, "excel_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "project_legend_db", ".xlsx");
                excelOutPath = ExcelTableInterop.WriteSpreadsheet(reqXl, xl);
                excelExported = true;
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "action", action },
                { "items_count", itemDicts.Count },
                { "verified_count", verifiedCount },
                { "upserted_count", upsertedCount },
                { "extracted_raw_count", extractedRawCount },
                { "skipped_verified_count", skippedVerifiedCount },
                { "native_tables_scanned", nativeTablesScanned },
                { "candidate_symbols_scanned", candSymbols },
                { "candidate_texts_scanned", candTexts },
                { "missing_watt_blocks", missingWattBlocks },
                { "saved_circuits_count", savedCircuitsCount },
                { "project_db_path", primaryPath ?? "" },
                { "portable_db_path", mirrorPath ?? "" },
                { "projects_index_path", indexPath ?? "" },
                { "persisted_to_disk", !string.IsNullOrEmpty(primaryPath) || !string.IsNullOrEmpty(mirrorPath) },
                { "items", itemDicts }
            };
            if (excelExported)
            {
                res["excel_exported"] = true;
                res["excel_path"] = excelOutPath;
            }
            if (warnings.Count > 0) res["warnings"] = warnings;
            return Ok(res);
        }
    }
}
