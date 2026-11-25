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
            public string LegendTag = "";
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
        private static readonly Dictionary<IntPtr, DateTime> s_legendLoadedMtimeByDb =
            new Dictionary<IntPtr, DateTime>();
        private static readonly Dictionary<IntPtr, Dictionary<string, Dictionary<string, object>>> s_dialuxRoomPrefsByDb =
            new Dictionary<IntPtr, Dictionary<string, Dictionary<string, object>>>();

        private static Dictionary<string, object> LegendSpecToDict(ProjectLegendSpecItem item)
        {
            Dictionary<string, object> d = new Dictionary<string, object>
            {
                { "block_name", item.BlockName ?? "" },
                { "legend_tag", item.LegendTag ?? "" },
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
                LegendTag = GetStr(d, "legend_tag") ?? "",
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
                LegendTag = s.LegendTag ?? "",
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
            EnsureDocDestroyedHook();
            if (db == null || db.UnmanagedObject == IntPtr.Zero) return;
            IntPtr dbPtr = db.UnmanagedObject;

            string primaryPath, mirrorPath, indexPath, docPath;
            ResolveProjectDbPaths(doc, db, out primaryPath, out mirrorPath, out indexPath, out docPath);
            bool hasPrim = !string.IsNullOrEmpty(primaryPath) && File.Exists(primaryPath);
            bool hasMir = !string.IsNullOrEmpty(mirrorPath) && File.Exists(mirrorPath);
            string readPath = null;
            if (hasPrim && hasMir)
            {
                try
                {
                    DateTime pt = File.GetLastWriteTimeUtc(primaryPath);
                    DateTime mt = File.GetLastWriteTimeUtc(mirrorPath);
                    readPath = (mt > pt) ? mirrorPath : primaryPath;
                }
                catch { readPath = primaryPath; }
            }
            else if (hasPrim) readPath = primaryPath;
            else if (hasMir) readPath = mirrorPath;

            DateTime diskMtime = DateTime.MinValue;
            if (!string.IsNullOrEmpty(readPath))
            {
                try { diskMtime = File.GetLastWriteTimeUtc(readPath); } catch { }
            }

            if (s_legendLoadedForDb.Contains(dbPtr))
            {
                if (string.IsNullOrEmpty(readPath)) return;
                DateTime prevMtime;
                if (s_legendLoadedMtimeByDb.TryGetValue(dbPtr, out prevMtime) && prevMtime == diskMtime)
                    return;
            }
            s_legendLoadedForDb.Add(dbPtr);
            if (!string.IsNullOrEmpty(readPath))
                s_legendLoadedMtimeByDb[dbPtr] = diskMtime;

            Dictionary<string, ProjectLegendSpecItem> cat;
            if (!s_legendCatalogByDb.TryGetValue(dbPtr, out cat))
            {
                cat = new Dictionary<string, ProjectLegendSpecItem>(StringComparer.OrdinalIgnoreCase);
                s_legendCatalogByDb[dbPtr] = cat;
            }

            try
            {
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

                // Import sibling-DWG circuits (e.g. Lighting.dwg + Power.dwg in same project folder)
                Dictionary<string, object> diskCircs = root.ContainsKey("circuits")
                    ? (root["circuits"] as Dictionary<string, object>)
                    : null;
                if (diskCircs != null)
                {
                    Dictionary<string, SavedCircuitRecord> bucket;
                    if (!s_savedCircuitsByDb.TryGetValue(dbPtr, out bucket))
                    {
                        bucket = new Dictionary<string, SavedCircuitRecord>(StringComparer.OrdinalIgnoreCase);
                        s_savedCircuitsByDb[dbPtr] = bucket;
                    }
                    foreach (KeyValuePair<string, object> kv in diskCircs)
                    {
                        SavedCircuitRecord rec = ParseSavedCircuitFromDict(kv.Key, kv.Value as Dictionary<string, object>);
                        if (rec == null) continue;
                        string normKey = NormalizeCircuitKey(rec.CircuitName);
                        if (string.IsNullOrEmpty(normKey)) continue;
                        SavedCircuitRecord existing;
                        if (!bucket.TryGetValue(normKey, out existing) || existing == null)
                        {
                            bucket[normKey] = rec;
                        }
                        else if (!IsRecordFromCurrentDrawing(doc, existing) && (rec.Approved || rec.UserLocked))
                        {
                            bucket[normKey] = rec;
                        }
                    }
                }

                Dictionary<string, object> dialuxRoomsRaw = root.ContainsKey("dialux_rooms")
                    ? (root["dialux_rooms"] as Dictionary<string, object>)
                    : null;
                if (dialuxRoomsRaw != null)
                {
                    Dictionary<string, Dictionary<string, object>> rMap;
                    if (!s_dialuxRoomPrefsByDb.TryGetValue(dbPtr, out rMap))
                    {
                        rMap = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                        s_dialuxRoomPrefsByDb[dbPtr] = rMap;
                    }
                    foreach (KeyValuePair<string, object> rkv in dialuxRoomsRaw)
                    {
                        Dictionary<string, object> rv = rkv.Value as Dictionary<string, object>;
                        if (rv == null || string.IsNullOrWhiteSpace(rkv.Key)) continue;
                        rMap[rkv.Key] = new Dictionary<string, object>(rv, StringComparer.OrdinalIgnoreCase);
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

                // Merge existing on-disk circuits from sibling DWGs before applying current-DWG records
                Dictionary<string, SavedCircuitRecord> mergedCircuits = new Dictionary<string, SavedCircuitRecord>(StringComparer.OrdinalIgnoreCase);
                JavaScriptSerializer readSer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
                string existingDiskPath = (!string.IsNullOrEmpty(primaryPath) && File.Exists(primaryPath))
                    ? primaryPath
                    : ((!string.IsNullOrEmpty(mirrorPath) && File.Exists(mirrorPath)) ? mirrorPath : null);
                if (!string.IsNullOrEmpty(existingDiskPath))
                {
                    try
                    {
                        Dictionary<string, object> diskRoot = readSer.DeserializeObject(File.ReadAllText(existingDiskPath, Encoding.UTF8)) as Dictionary<string, object>;
                        Dictionary<string, object> diskCircs = (diskRoot != null && diskRoot.ContainsKey("circuits"))
                            ? (diskRoot["circuits"] as Dictionary<string, object>)
                            : null;
                        if (diskCircs != null)
                        {
                            foreach (KeyValuePair<string, object> kv in diskCircs)
                            {
                                SavedCircuitRecord dRec = ParseSavedCircuitFromDict(kv.Key, kv.Value as Dictionary<string, object>);
                                if (dRec == null) continue;
                                string nk = NormalizeCircuitKey(dRec.CircuitName);
                                if (!string.IsNullOrEmpty(nk)) mergedCircuits[nk] = dRec;
                            }
                        }
                    }
                    catch { }
                }

                Dictionary<string, SavedCircuitRecord> circBucket;
                if (s_savedCircuitsByDb.TryGetValue(dbPtr, out circBucket) && circBucket != null)
                {
                    foreach (KeyValuePair<string, SavedCircuitRecord> kv in circBucket)
                    {
                        SavedCircuitRecord r = kv.Value;
                        if (r == null) continue;
                        string nk = NormalizeCircuitKey(r.CircuitName);
                        if (string.IsNullOrEmpty(nk)) nk = kv.Key;
                        SavedCircuitRecord diskRec;
                        if (mergedCircuits.TryGetValue(nk, out diskRec) && diskRec != null && !IsRecordFromCurrentDrawing(doc, diskRec))
                        {
                            if (!IsRecordFromCurrentDrawing(doc, r) || (!r.Approved && !r.UserLocked && (diskRec.Approved || diskRec.UserLocked)))
                                continue;
                        }
                        mergedCircuits[nk] = CloneSavedRecord(r);
                    }
                }

                List<KeyValuePair<string, SavedCircuitRecord>> sortedCircs = new List<KeyValuePair<string, SavedCircuitRecord>>(mergedCircuits);
                sortedCircs.Sort(delegate(KeyValuePair<string, SavedCircuitRecord> a, KeyValuePair<string, SavedCircuitRecord> b)
                {
                    return CompareCircuitKeysNatural(
                        a.Value != null ? a.Value.PanelName : "",
                        a.Value != null ? a.Value.CircuitName : a.Key,
                        b.Value != null ? b.Value.PanelName : "",
                        b.Value != null ? b.Value.CircuitName : b.Key);
                });

                Dictionary<string, object> circSerialized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int si = 0; si < sortedCircs.Count; si++)
                {
                    SavedCircuitRecord r = sortedCircs[si].Value;
                    if (r != null) circSerialized[sortedCircs[si].Key] = SavedCircuitToDict(r);
                }

                Dictionary<string, object> existingDiskDialuxRooms = null;
                if (!string.IsNullOrEmpty(existingDiskPath))
                {
                    try
                    {
                        Dictionary<string, object> dr2 = readSer.DeserializeObject(File.ReadAllText(existingDiskPath, Encoding.UTF8)) as Dictionary<string, object>;
                        if (dr2 != null && dr2.ContainsKey("dialux_rooms"))
                            existingDiskDialuxRooms = dr2["dialux_rooms"] as Dictionary<string, object>;
                    }
                    catch { }
                }
                Dictionary<string, object> dialuxRoomsSerialized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (existingDiskDialuxRooms != null)
                {
                    foreach (KeyValuePair<string, object> rkv in existingDiskDialuxRooms)
                    {
                        Dictionary<string, object> rv = rkv.Value as Dictionary<string, object>;
                        if (rv != null && !string.IsNullOrWhiteSpace(rkv.Key))
                            dialuxRoomsSerialized[rkv.Key] = new Dictionary<string, object>(rv, StringComparer.OrdinalIgnoreCase);
                    }
                }
                Dictionary<string, Dictionary<string, object>> memDialuxRooms;
                if (s_dialuxRoomPrefsByDb.TryGetValue(dbPtr, out memDialuxRooms) && memDialuxRooms != null)
                {
                    foreach (KeyValuePair<string, Dictionary<string, object>> mkv in memDialuxRooms)
                    {
                        if (mkv.Value != null && !string.IsNullOrWhiteSpace(mkv.Key))
                            dialuxRoomsSerialized[mkv.Key] = new Dictionary<string, object>(mkv.Value, StringComparer.OrdinalIgnoreCase);
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
                    { "circuits", circSerialized },
                    { "dialux_rooms", dialuxRoomsSerialized }
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

                string mtimeTarget = (!string.IsNullOrEmpty(primaryPath) && File.Exists(primaryPath))
                    ? primaryPath
                    : ((!string.IsNullOrEmpty(mirrorPath) && File.Exists(mirrorPath)) ? mirrorPath : null);
                if (!string.IsNullOrEmpty(mtimeTarget))
                {
                    try { s_legendLoadedMtimeByDb[dbPtr] = File.GetLastWriteTimeUtc(mtimeTarget); } catch { }
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
            ComputeCircuitLoadFromProjectDb(
                doc, countsByBlock, explicitFixtureWatt, explicitFixtureVa, fallbackFixtureVa, powerFactor, false,
                out totalKw, out totalKva, out loadAssumed, out loadSource,
                out rowLoadByBlock, out missingCatalogBlocks, out specBreakdown);
        }

        private static void ComputeCircuitLoadFromProjectDb(
            Document doc,
            Dictionary<string, int> countsByBlock,
            double? explicitFixtureWatt,
            double? explicitFixtureVa,
            double fallbackFixtureVa,
            double powerFactor,
            bool strictNoAssume,
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
            loadSource = strictNoAssume ? "pending_user_specs" : "default_40va_assumed";
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

                if (!missingCatalogBlocks.Contains(kv.Key)) missingCatalogBlocks.Add(kv.Key);
                if (strictNoAssume)
                {
                    rowLoadByBlock[kv.Key] = "غير مسجل بالليجند (" + kv.Value.ToString(CultureInfo.InvariantCulture) + "x ? W)";
                    specBreakdown[kv.Key] = new Dictionary<string, object>
                    {
                        { "qty", kv.Value },
                        { "unit_va", 0.0 },
                        { "total_va", 0.0 },
                        { "missing_from_legend", true },
                        { "source", "pending_user_specs" }
                    };
                }
                else
                {
                    loadAssumed = true;
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
            }

            if (missingCatalogBlocks.Count == 0 && matchedAny)
                loadSource = "project_legend_db";
            else if (strictNoAssume && matchedAny)
                loadSource = "partial_pending_user_specs";
            else if (strictNoAssume)
                loadSource = "pending_user_specs";
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

        private static bool IsCircuitTagOrFloorNoteNoise(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string t = Regex.Replace(text.Trim(), @"\s+", " ");
            string u = t.ToUpperInvariant();
            if (u.Contains("UP TO NEXT FLOOR") || u.Contains("DOWN TO NEXT FLOOR") ||
                u.Contains("CORRIDORS AND PUBLIC AREAS LIGHTING CIRCUITS") ||
                u.Contains("CONTROLLED FROM PUSHBUTTONS LOCATED AT PANEL"))
                return true;
            if (Regex.IsMatch(t, @"^(?:[A-Z]{1,5}[-_][A-Z0-9.]+(?:/\d{1,3})?)(?:\s+[A-Z]{1,5}[-_][A-Z0-9.]+(?:/\d{1,3})?)*$", RegexOptions.IgnoreCase))
                return true;
            return false;
        }

        private static bool HasEngineeringSpecSignal(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string u = text.ToUpperInvariant();
            if (IsAsAboveReferenceText(u)) return true;
            return Regex.IsMatch(u,
                @"\b(WATTS?|KVA|KW|LED|FIXTURE|LIGHTING|DOWNLIGHT|BOLLARD|FLOOD|SPOT|RECESSED|SURFACE|WALL|WEATHER\s*PROOF|LUMINAIRE|SWITCH|SOCKET|OUTLET|RECEPTACLE|STREET|COLUMN|COLUMON|EMERGENCY|BACKUP|BATTERY|POLYCARBONATE|POLYSTYRENE)\b" +
                @"|\b\d+(?:\.\d+)?\s*(?:W|VA|LM|K|V)\b|\bIP\s*\d{2}\b|\bIK\s*\d{2}\b" +
                @"|كشاف|لمبة|إنارة|انارة|وات|واط|طوارئ|بريزة|مفتاح");
        }

        private static bool IsLegendCodeTagToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.Length > 7 || t.IndexOf(' ') >= 0) return false;
            return Regex.IsMatch(t, @"^[A-Za-z][A-Za-z0-9,.\-/]{0,6}$");
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

            // Detect Legend region headers & explicit SYMBOL <-> DESCRIPTION pairs
            List<Point2d> legendHeaderPts = new List<Point2d>();
            List<LegendCandidateText> symHdrs = new List<LegendCandidateText>();
            List<LegendCandidateText> descHdrs = new List<LegendCandidateText>();
            for (int i = 0; i < texts.Count; i++)
            {
                string raw = Regex.Replace(texts[i].Text ?? "", @"[%%UuOo:\.\-]+", " ").Trim();
                string tu = Regex.Replace(raw.ToUpperInvariant(), @"\s+", " ").Trim();
                if (tu == "SYMBOL" || tu == "SYMBOLS" || tu == "الرمز" || tu == "رمز")
                {
                    symHdrs.Add(texts[i]);
                    legendHeaderPts.Add(new Point2d(texts[i].Cx, texts[i].Cy));
                }
                else if (tu == "DESCRIPTION" || tu == "DESCRIPTIONS" || tu == "الوصف" || tu == "الوصف الهندسي")
                {
                    descHdrs.Add(texts[i]);
                    legendHeaderPts.Add(new Point2d(texts[i].Cx, texts[i].Cy));
                }
                else if (tu.Contains("LEGEND") || tu.Contains("جدول الرموز") ||
                         tu.Contains("مفاتيح") || tu.Contains("LUMINAIRE SCHEDULE"))
                {
                    legendHeaderPts.Add(new Point2d(texts[i].Cx, texts[i].Cy));
                }
            }

            List<LegendHeaderPair> headerPairs = new List<LegendHeaderPair>();
            for (int si = 0; si < symHdrs.Count; si++)
            {
                LegendCandidateText sh = symHdrs[si];
                LegendCandidateText bestDh = null;
                double bestDx = double.MaxValue;
                for (int di = 0; di < descHdrs.Count; di++)
                {
                    LegendCandidateText dh = descHdrs[di];
                    if (Math.Abs(dh.Cy - sh.Cy) > Math.Max(medSpan * 3.0, Math.Max(sh.Height, dh.Height) * 4.0)) continue;
                    double dx = Math.Abs(dh.Cx - sh.Cx);
                    if (dx < medSpan * 40.0 && dx < bestDx)
                    {
                        bestDx = dx;
                        bestDh = dh;
                    }
                }
                if (bestDh != null)
                {
                    headerPairs.Add(new LegendHeaderPair
                    {
                        SymHdr = sh,
                        DescHdr = bestDh,
                        DescOnLeft = (bestDh.Cx < sh.Cx)
                    });
                }
            }

            // Cluster symbols into vertical X buckets, keeping same-X Legend columns unified
            symbols.Sort(delegate(LegendCandidateSymbol a, LegendCandidateSymbol b)
            {
                int cx = a.Cx.CompareTo(b.Cx);
                return cx != 0 ? cx : b.Cy.CompareTo(a.Cy);
            });

            List<List<LegendCandidateSymbol>> xBuckets = new List<List<LegendCandidateSymbol>>();
            for (int i = 0; i < symbols.Count; i++)
            {
                LegendCandidateSymbol s = symbols[i];
                int bestBi = -1;
                double bestDx = double.MaxValue;
                for (int ci = xBuckets.Count - 1; ci >= 0; ci--)
                {
                    List<LegendCandidateSymbol> bucket = xBuckets[ci];
                    double dx = Math.Abs(s.Cx - bucket[0].Cx);
                    if (dx <= colXTol && dx < bestDx)
                    {
                        bestDx = dx;
                        bestBi = ci;
                    }
                }
                if (bestBi >= 0) xBuckets[bestBi].Add(s);
                else xBuckets.Add(new List<LegendCandidateSymbol> { s });
            }

            List<LegendColumnRun> runs = new List<LegendColumnRun>();
            for (int bi = 0; bi < xBuckets.Count; bi++)
            {
                List<LegendCandidateSymbol> bucket = xBuckets[bi];
                bucket.Sort(delegate(LegendCandidateSymbol a, LegendCandidateSymbol b) { return b.Cy.CompareTo(a.Cy); });
                if (bucket.Count == 0) continue;

                // Estimate tight local vertical step from positive gaps <= 6*medSpan
                List<double> tightGaps = new List<double>();
                for (int ri = 1; ri < bucket.Count; ri++)
                {
                    double dg = bucket[ri - 1].Cy - bucket[ri].Cy;
                    if (dg > medSpan * 0.3 && dg <= medSpan * 6.0) tightGaps.Add(dg);
                }
                double localStep = medSpan * 2.2;
                if (tightGaps.Count > 0)
                {
                    tightGaps.Sort();
                    localStep = tightGaps[tightGaps.Count / 2];
                }
                double splitGap = Math.Max(medSpan * 8.0, localStep * 4.5);

                List<List<LegendCandidateSymbol>> rawRuns = new List<List<LegendCandidateSymbol>>();
                List<LegendCandidateSymbol> curRun = new List<LegendCandidateSymbol> { bucket[0] };
                for (int ri = 1; ri < bucket.Count; ri++)
                {
                    double dg = bucket[ri - 1].Cy - bucket[ri].Cy;
                    if (dg > splitGap)
                    {
                        rawRuns.Add(curRun);
                        curRun = new List<LegendCandidateSymbol>();
                    }
                    curRun.Add(bucket[ri]);
                }
                if (curRun.Count > 0) rawRuns.Add(curRun);

                for (int rIdx = 0; rIdx < rawRuns.Count; rIdx++)
                {
                    List<LegendCandidateSymbol> rawSymList = rawRuns[rIdx];
                    double sumCx = 0.0, runMinX = double.MaxValue, runMaxX = double.MinValue;
                    double runMinY = double.MaxValue, runMaxY = double.MinValue;
                    for (int ri = 0; ri < rawSymList.Count; ri++)
                    {
                        sumCx += rawSymList[ri].Cx;
                        if (rawSymList[ri].MinX < runMinX) runMinX = rawSymList[ri].MinX;
                        if (rawSymList[ri].MaxX > runMaxX) runMaxX = rawSymList[ri].MaxX;
                        if (rawSymList[ri].MinY < runMinY) runMinY = rawSymList[ri].MinY;
                        if (rawSymList[ri].MaxY > runMaxY) runMaxY = rawSymList[ri].MaxY;
                    }
                    runs.Add(new LegendColumnRun
                    {
                        RawSymbols = rawSymList,
                        Rows = new List<LegendCandidateSymbol>(),
                        DistinctNamesCount = 0,
                        AvgCx = sumCx / rawSymList.Count,
                        MinX = runMinX,
                        MaxX = runMaxX,
                        MinY = runMinY,
                        MaxY = runMaxY,
                        TypRowStep = medSpan * 2.2
                    });
                }
            }

            // Merge contiguous Y-overlapping runs sharing almost identical X-axis (e.g. D1/D3 at 228446.69 and APP/DD at 228446.78)
            runs.Sort(delegate(LegendColumnRun a, LegendColumnRun b) { return a.AvgCx.CompareTo(b.AvgCx); });
            double mergeSameColXTol = Math.Max(colXTol * 1.35, medSpan * 1.8);
            double mergeGapY = Math.Max(medSpan * 8.0, medSpan * 2.2 * 4.5);
            List<LegendColumnRun> mergedRuns = new List<LegendColumnRun>();
            for (int ri = 0; ri < runs.Count; ri++)
            {
                LegendColumnRun cur = runs[ri];
                LegendColumnRun target = null;
                for (int mi = mergedRuns.Count - 1; mi >= 0; mi--)
                {
                    LegendColumnRun prev = mergedRuns[mi];
                    if (Math.Abs(cur.AvgCx - prev.AvgCx) <= mergeSameColXTol &&
                        !(cur.MaxY < prev.MinY - mergeGapY || cur.MinY > prev.MaxY + mergeGapY))
                    {
                        target = prev;
                        break;
                    }
                }
                if (target != null)
                {
                    target.RawSymbols.AddRange(cur.RawSymbols);
                    target.MinX = Math.Min(target.MinX, cur.MinX);
                    target.MaxX = Math.Max(target.MaxX, cur.MaxX);
                    target.MinY = Math.Min(target.MinY, cur.MinY);
                    target.MaxY = Math.Max(target.MaxY, cur.MaxY);
                    double sCx = 0.0;
                    for (int k = 0; k < target.RawSymbols.Count; k++) sCx += target.RawSymbols[k].Cx;
                    target.AvgCx = sCx / target.RawSymbols.Count;
                }
                else
                {
                    mergedRuns.Add(cur);
                }
            }

            // Finalize deduplicated rows & spacing on each merged vertical column run
            List<LegendColumnRun> finalRuns = new List<LegendColumnRun>();
            for (int ri = 0; ri < mergedRuns.Count; ri++)
            {
                LegendColumnRun run = mergedRuns[ri];
                List<LegendCandidateSymbol> rawSymList = run.RawSymbols;
                rawSymList.Sort(delegate(LegendCandidateSymbol a, LegendCandidateSymbol b) { return b.Cy.CompareTo(a.Cy); });

                List<List<LegendCandidateSymbol>> sameYGroups = new List<List<LegendCandidateSymbol>>();
                for (int si = 0; si < rawSymList.Count; si++)
                {
                    LegendCandidateSymbol sym = rawSymList[si];
                    if (sameYGroups.Count > 0 &&
                        Math.Abs(sameYGroups[sameYGroups.Count - 1][0].Cy - sym.Cy) <= medSpan * 0.30)
                    {
                        sameYGroups[sameYGroups.Count - 1].Add(sym);
                    }
                    else
                    {
                        sameYGroups.Add(new List<LegendCandidateSymbol> { sym });
                    }
                }

                List<LegendCandidateSymbol> rows = new List<LegendCandidateSymbol>();
                HashSet<string> distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int gi = 0; gi < sameYGroups.Count; gi++)
                {
                    List<LegendCandidateSymbol> grp = sameYGroups[gi];
                    LegendCandidateSymbol chosen = grp[0];
                    if (grp.Count > 1)
                    {
                        LegendCandidateSymbol textMatched = null;
                        for (int ti = 0; ti < texts.Count && textMatched == null; ti++)
                        {
                            LegendCandidateText tx = texts[ti];
                            if (Math.Abs(tx.Cy - grp[0].Cy) > medSpan * 0.55) continue;
                            if (tx.Cx < grp[0].MinX - medSpan * 2.5 || tx.Cx > grp[0].MaxX + medSpan * 3.0) continue;
                            if (!IsLegendCodeTagToken(tx.Text)) continue;
                            for (int k = 0; k < grp.Count; k++)
                            {
                                if (string.Equals(grp[k].BlockName, tx.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                                {
                                    textMatched = grp[k];
                                    break;
                                }
                            }
                        }
                        if (textMatched != null)
                        {
                            chosen = textMatched;
                        }
                        else
                        {
                            for (int k = 0; k < grp.Count; k++)
                            {
                                if (!distinctNames.Contains(grp[k].BlockName))
                                {
                                    chosen = grp[k];
                                    break;
                                }
                            }
                        }
                    }
                    rows.Add(chosen);
                    distinctNames.Add(chosen.BlockName);
                }

                if (!fromSelectionOnly && (rows.Count < 2 || distinctNames.Count < 2))
                    continue;

                double sumCx = 0.0, rMinX = double.MaxValue, rMaxX = double.MinValue;
                double rMinY = double.MaxValue, rMaxY = double.MinValue;
                for (int si = 0; si < rows.Count; si++)
                {
                    sumCx += rows[si].Cx;
                    if (rows[si].MinX < rMinX) rMinX = rows[si].MinX;
                    if (rows[si].MaxX > rMaxX) rMaxX = rows[si].MaxX;
                    if (rows[si].MinY < rMinY) rMinY = rows[si].MinY;
                    if (rows[si].MaxY > rMaxY) rMaxY = rows[si].MaxY;
                }

                double typStep = medSpan * 2.2;
                if (rows.Count >= 2)
                {
                    List<double> gaps = new List<double>();
                    for (int si = 1; si < rows.Count; si++)
                    {
                        double dg = rows[si - 1].Cy - rows[si].Cy;
                        if (dg > medSpan * 0.3 && dg < medSpan * 15.0) gaps.Add(dg);
                    }
                    if (gaps.Count > 0)
                    {
                        gaps.Sort();
                        typStep = gaps[gaps.Count / 2];
                    }
                }

                run.Rows = rows;
                run.DistinctNamesCount = distinctNames.Count;
                run.AvgCx = sumCx / rows.Count;
                run.MinX = rMinX;
                run.MaxX = rMaxX;
                run.MinY = rMinY;
                run.MaxY = rMaxY;
                run.TypRowStep = typStep;
                finalRuns.Add(run);
            }
            runs = finalRuns;

            // Group side-by-side vertical runs overlapping in Y into unified LegendTableGroups
            runs.Sort(delegate(LegendColumnRun a, LegendColumnRun b) { return a.AvgCx.CompareTo(b.AvgCx); });
            List<LegendTableGroup> tableGroups = new List<LegendTableGroup>();
            bool[] runAssigned = new bool[runs.Count];
            for (int i = 0; i < runs.Count; i++)
            {
                if (runAssigned[i]) continue;
                LegendColumnRun seed = runs[i];
                LegendTableGroup grp = new LegendTableGroup();
                grp.Runs.Add(seed);
                runAssigned[i] = true;

                double gMinY = seed.MinY, gMaxY = seed.MaxY, gLastCx = seed.AvgCx, gTypStep = seed.TypRowStep;
                double yPad = Math.Max(medSpan * 6.0, gTypStep * 3.0);
                double maxColPitch = Math.Max(medSpan * 45.0, gTypStep * 25.0);

                for (int j = i + 1; j < runs.Count; j++)
                {
                    if (runAssigned[j]) continue;
                    LegendColumnRun cand = runs[j];
                    if (cand.AvgCx - gLastCx > maxColPitch) continue;
                    bool yOverlaps = !(cand.MaxY < gMinY - yPad || cand.MinY > gMaxY + yPad);
                    if (yOverlaps)
                    {
                        grp.Runs.Add(cand);
                        runAssigned[j] = true;
                        gMinY = Math.Min(gMinY, cand.MinY);
                        gMaxY = Math.Max(gMaxY, cand.MaxY);
                        gLastCx = cand.AvgCx;
                    }
                }
                grp.Runs.Sort(delegate(LegendColumnRun a, LegendColumnRun b) { return a.AvgCx.CompareTo(b.AvgCx); });

                double tMinX = double.MaxValue, tMaxX = double.MinValue, tMinY = double.MaxValue, tMaxY = double.MinValue, maxTyp = 0.0;
                for (int k = 0; k < grp.Runs.Count; k++)
                {
                    if (grp.Runs[k].MinX < tMinX) tMinX = grp.Runs[k].MinX;
                    if (grp.Runs[k].MaxX > tMaxX) tMaxX = grp.Runs[k].MaxX;
                    if (grp.Runs[k].MinY < tMinY) tMinY = grp.Runs[k].MinY;
                    if (grp.Runs[k].MaxY > tMaxY) tMaxY = grp.Runs[k].MaxY;
                    if (grp.Runs[k].TypRowStep > maxTyp) maxTyp = grp.Runs[k].TypRowStep;
                }
                grp.MinX = tMinX; grp.MaxX = tMaxX; grp.MinY = tMinY; grp.MaxY = tMaxY;
                grp.TypRowStep = Math.Max(medSpan * 1.5, maxTyp);

                // Check if any Legend header or SYMBOL/DESCRIPTION pair sits near this table group
                double hdrReachX = Math.Max((tMaxX - tMinX) + medSpan * 30.0, medSpan * 45.0);
                double hdrReachY = Math.Max((tMaxY - tMinY) * 0.65, medSpan * 18.0);
                for (int hi = 0; hi < legendHeaderPts.Count; hi++)
                {
                    Point2d hp = legendHeaderPts[hi];
                    if (hp.X >= tMinX - medSpan * 15.0 && hp.X <= tMaxX + hdrReachX &&
                        hp.Y >= tMinY - medSpan * 6.0 && hp.Y <= tMaxY + hdrReachY)
                    {
                        grp.NearHeader = true;
                        break;
                    }
                }
                for (int pi = 0; pi < headerPairs.Count; pi++)
                {
                    LegendHeaderPair hp = headerPairs[pi];
                    double hx = hp.SymHdr.Cx, hy = hp.SymHdr.Cy;
                    if (hx >= tMinX - medSpan * 15.0 && hx <= tMaxX + hdrReachX &&
                        hy >= tMinY - medSpan * 6.0 && hy <= tMaxY + hdrReachY)
                    {
                        grp.HeaderPair = hp;
                        grp.NearHeader = true;
                        break;
                    }
                }
                tableGroups.Add(grp);
            }

            // Decide unified reading direction per LegendTableGroup and extract bounded rows
            Dictionary<string, ProjectLegendSpecItem> bestByBlock = new Dictionary<string, ProjectLegendSpecItem>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectLegendSpecItem nt in extracted)
            {
                if (!string.IsNullOrEmpty(nt.BlockName)) bestByBlock[nt.BlockName] = nt;
            }

            List<LegendGroupExtraction> groupExtractions = new List<LegendGroupExtraction>();
            bool anyHeaderSpecTable = false;

            for (int gi = 0; gi < tableGroups.Count; gi++)
            {
                LegendTableGroup grp = tableGroups[gi];
                double typRowStep = grp.TypRowStep;
                double singleReachX = Math.Max(medSpan * 45.0, typRowStep * 25.0);
                double colReachX = singleReachX;
                if (grp.Runs.Count >= 2)
                {
                    double minPitch = double.MaxValue;
                    for (int ci = 1; ci < grp.Runs.Count; ci++)
                    {
                        double p = grp.Runs[ci].AvgCx - grp.Runs[ci - 1].AvgCx;
                        if (p > medSpan && p < minPitch) minPitch = p;
                    }
                    if (minPitch < double.MaxValue)
                        colReachX = Math.Max(minPitch * 1.15, Math.Max(medSpan * 15.0, typRowStep * 10.0));
                }

                // Determine reading direction: HeaderPair takes priority, otherwise bounded exterior-weighted voting
                bool descOnLeft = false;
                if (grp.HeaderPair != null)
                {
                    descOnLeft = grp.HeaderPair.DescOnLeft;
                }
                else
                {
                    double ltrScore = 0.0;
                    double rtlScore = 0.0;
                    for (int ci = 0; ci < grp.Runs.Count; ci++)
                    {
                        LegendColumnRun run = grp.Runs[ci];
                        double nextBoundaryX = (ci + 1 < grp.Runs.Count)
                            ? (grp.Runs[ci + 1].AvgCx - medSpan * 0.25)
                            : (run.AvgCx + colReachX);
                        double prevBoundaryX = (ci > 0)
                            ? (grp.Runs[ci - 1].AvgCx + medSpan * 0.25)
                            : (run.AvgCx - colReachX);
                        double bandY = Math.Max(medSpan * 1.1, run.TypRowStep * 0.55);

                        for (int ri = 0; ri < run.Rows.Count; ri++)
                        {
                            LegendCandidateSymbol sym = run.Rows[ri];
                            for (int ti = 0; ti < texts.Count; ti++)
                            {
                                LegendCandidateText tx = texts[ti];
                                if (Math.Abs(tx.Cy - sym.Cy) > bandY) continue;
                                if (tx.Text.Length < 4 || IsCircuitTagOrFloorNoteNoise(tx.Text)) continue;
                                double wt = HasEngineeringSpecSignal(tx.Text) ? 2.5 : 0.5;
                                if (tx.Cx > run.AvgCx + medSpan * 0.25 && tx.MinX >= run.AvgCx && tx.MinX < nextBoundaryX && tx.Cx <= run.AvgCx + colReachX)
                                {
                                    double extBonus = (ci == 0 && grp.Runs.Count > 1) ? 1.5 : (ci == grp.Runs.Count - 1 ? 2.0 : 1.0);
                                    ltrScore += wt * extBonus;
                                }
                                else if (tx.Cx < run.AvgCx - medSpan * 0.25 && tx.MaxX <= run.AvgCx && tx.MaxX > prevBoundaryX && tx.Cx >= run.AvgCx - colReachX)
                                {
                                    double extBonus = (ci == 0 ? 2.0 : 1.0);
                                    rtlScore += wt * extBonus;
                                }
                            }
                        }
                    }
                    descOnLeft = rtlScore > ltrScore;
                }

                List<ProjectLegendSpecItem> groupItems = new List<ProjectLegendSpecItem>();
                int specRowsCount = 0;
                int wattBearingCount = 0;

                for (int ci = 0; ci < grp.Runs.Count; ci++)
                {
                    LegendColumnRun run = grp.Runs[ci];
                    List<LegendCandidateSymbol> rows = run.Rows;
                    double nextBoundaryX = (ci + 1 < grp.Runs.Count)
                        ? (grp.Runs[ci + 1].AvgCx - medSpan * 0.25)
                        : (run.AvgCx + colReachX);
                    double prevBoundaryX = (ci > 0)
                        ? (grp.Runs[ci - 1].AvgCx + medSpan * 0.25)
                        : (run.AvgCx - colReachX);

                    // Detect main description column left/right anchor X so short Legend tags (e.g. D1, W3, F1) separate cleanly
                    List<double> longTextEdges = new List<double>();
                    for (int ri = 0; ri < rows.Count; ri++)
                    {
                        LegendCandidateSymbol sym = rows[ri];
                        double ubY = (ri > 0 && (rows[ri - 1].Cy - sym.Cy) <= run.TypRowStep * 2.2)
                            ? (rows[ri - 1].Cy + sym.Cy) * 0.5
                            : (sym.Cy + Math.Max(medSpan * 0.75, run.TypRowStep * 0.55));
                        double lbY = (ri + 1 < rows.Count && (sym.Cy - rows[ri + 1].Cy) <= run.TypRowStep * 2.2)
                            ? (sym.Cy + rows[ri + 1].Cy) * 0.5
                            : (sym.Cy - Math.Max(medSpan * 0.75, run.TypRowStep * 0.55));

                        for (int ti = 0; ti < texts.Count; ti++)
                        {
                            LegendCandidateText tx = texts[ti];
                            if (tx.Cy > ubY || tx.Cy < lbY) continue;
                            if (tx.Text.Length <= 7) continue;
                            if (descOnLeft)
                            {
                                if (tx.Cx < sym.Cx - medSpan * 0.10 && tx.MaxX > prevBoundaryX && tx.MaxX >= sym.Cx - colReachX)
                                    longTextEdges.Add(tx.MaxX);
                            }
                            else
                            {
                                if (tx.Cx > sym.Cx + medSpan * 0.10 && tx.MinX < nextBoundaryX && tx.MinX <= sym.Cx + colReachX)
                                    longTextEdges.Add(tx.MinX);
                            }
                        }
                    }
                    double? mainDescEdgeX = null;
                    if (longTextEdges.Count > 0)
                    {
                        longTextEdges.Sort();
                        mainDescEdgeX = longTextEdges[longTextEdges.Count / 2];
                    }

                    ProjectLegendSpecItem lastSpec = null;
                    double openEdgeBand = Math.Max(medSpan * 0.65, run.TypRowStep * 0.45);
                    for (int ri = 0; ri < rows.Count; ri++)
                    {
                        LegendCandidateSymbol sym = rows[ri];
                        double upperBoundY = (ri > 0 && (rows[ri - 1].Cy - sym.Cy) <= run.TypRowStep * 2.2)
                            ? (rows[ri - 1].Cy + sym.Cy) * 0.5
                            : (sym.Cy + openEdgeBand);
                        double lowerBoundY = (ri + 1 < rows.Count && (sym.Cy - rows[ri + 1].Cy) <= run.TypRowStep * 2.2)
                            ? (sym.Cy + rows[ri + 1].Cy) * 0.5
                            : (sym.Cy - openEdgeBand);

                        List<LegendCandidateText> descTexts = new List<LegendCandidateText>();
                        List<LegendCandidateText> tagTexts = new List<LegendCandidateText>();

                        for (int ti = 0; ti < texts.Count; ti++)
                        {
                            LegendCandidateText tx = texts[ti];
                            if (tx.Cy > upperBoundY || tx.Cy < lowerBoundY) continue;

                            if (descOnLeft)
                            {
                                if (tx.Cx > sym.Cx + medSpan * 0.15) continue;
                                if (ci > 0 && tx.MaxX <= prevBoundaryX) continue;
                                if (tx.MaxX < sym.MinX - colReachX) continue;
                            }
                            else
                            {
                                if (tx.Cx < sym.Cx - medSpan * 0.05) continue;
                                if (ci + 1 < grp.Runs.Count && tx.MinX >= nextBoundaryX) continue;
                                if (tx.MinX > sym.MaxX + colReachX) continue;
                                if (tx.Cx <= run.AvgCx + medSpan * 0.05 && tx.MinX < run.AvgCx) continue;
                            }

                            string tu = tx.Text.ToUpperInvariant();
                            if (tu == "SYMBOL" || tu == "SYMBOLS" || tu == "DESCRIPTION" || tu == "LEGEND" ||
                                tu == "الرمز" || tu == "الوصف" || tu.Contains("%%ULEGEND"))
                                continue;

                            bool isTag = IsLegendCodeTagToken(tx.Text) && mainDescEdgeX.HasValue &&
                                (descOnLeft ? (tx.MinX > mainDescEdgeX.Value + medSpan * 0.12)
                                            : (tx.MaxX <= mainDescEdgeX.Value + medSpan * 0.05));
                            if (isTag)
                                tagTexts.Add(tx);
                            else
                                descTexts.Add(tx);
                        }

                        if (descTexts.Count == 0 && tagTexts.Count == 0) continue;

                        descTexts.Sort(delegate(LegendCandidateText a, LegendCandidateText b)
                        {
                            if (Math.Abs(b.Cy - a.Cy) > Math.Min(a.Height, b.Height) * 0.45)
                                return b.Cy.CompareTo(a.Cy);
                            return a.MinX.CompareTo(b.MinX);
                        });
                        tagTexts.Sort(delegate(LegendCandidateText a, LegendCandidateText b) { return b.Cy.CompareTo(a.Cy); });

                        string rowLegendTag = "";
                        for (int tgi = 0; tgi < tagTexts.Count; tgi++)
                        {
                            string candTag = tagTexts[tgi].Text.Trim();
                            if (string.IsNullOrEmpty(rowLegendTag) ||
                                string.Equals(candTag, sym.BlockName, StringComparison.OrdinalIgnoreCase))
                            {
                                rowLegendTag = candTag;
                                if (string.Equals(candTag, sym.BlockName, StringComparison.OrdinalIgnoreCase)) break;
                            }
                        }

                        List<string> parts = new List<string>();
                        for (int pti = 0; pti < descTexts.Count; pti++)
                        {
                            string ptTxt = descTexts[pti].Text;
                            if (string.Equals(ptTxt, sym.BlockName, StringComparison.OrdinalIgnoreCase) && descTexts.Count > 1)
                                continue;
                            parts.Add(ptTxt);
                        }
                        string combinedDesc = Regex.Replace(string.Join(" ", parts.ToArray()), @"\s+", " ").Trim();
                        if (string.IsNullOrWhiteSpace(combinedDesc)) continue;
                        if (!fromSelectionOnly && IsCircuitTagOrFloorNoteNoise(combinedDesc)) continue;

                        ProjectLegendSpecItem parsed = ParseSpecFromLegendDescription(sym.BlockName, combinedDesc, srcTag);
                        parsed.LegendTag = rowLegendTag;

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
                            wattBearingCount++;
                            specRowsCount++;
                        }
                        else if (HasEngineeringSpecSignal(combinedDesc))
                        {
                            specRowsCount++;
                        }
                        groupItems.Add(parsed);
                    }
                }

                if (!fromSelectionOnly)
                {
                    if (groupItems.Count < 2) continue;
                    if (specRowsCount == 0 && !grp.NearHeader) continue;
                    if (!grp.NearHeader && wattBearingCount < 2) continue;
                }

                if (grp.NearHeader && specRowsCount >= 2)
                    anyHeaderSpecTable = true;

                groupExtractions.Add(new LegendGroupExtraction
                {
                    NearHeader = grp.NearHeader,
                    SpecRowsCount = specRowsCount,
                    WattBearingCount = wattBearingCount,
                    Items = groupItems
                });
            }

            for (int gi = 0; gi < groupExtractions.Count; gi++)
            {
                LegendGroupExtraction gx = groupExtractions[gi];
                if (!fromSelectionOnly && anyHeaderSpecTable && !gx.NearHeader && gx.WattBearingCount < 4)
                    continue;

                for (int ri = 0; ri < gx.Items.Count; ri++)
                {
                    ProjectLegendSpecItem item = gx.Items[ri];
                    ProjectLegendSpecItem prev;
                    if (!bestByBlock.TryGetValue(item.BlockName, out prev) ||
                        (!prev.Watt.HasValue && item.Watt.HasValue) ||
                        (item.Watt.HasValue == prev.Watt.HasValue && item.Description.Length > prev.Description.Length && (item.Watt.HasValue || item.Va.HasValue)))
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

        private sealed class LegendHeaderPair
        {
            public LegendCandidateText SymHdr;
            public LegendCandidateText DescHdr;
            public bool DescOnLeft;
        }

        private sealed class LegendColumnRun
        {
            public List<LegendCandidateSymbol> RawSymbols;
            public List<LegendCandidateSymbol> Rows;
            public int DistinctNamesCount;
            public double AvgCx;
            public double MinX;
            public double MaxX;
            public double MinY;
            public double MaxY;
            public double TypRowStep;
        }

        private sealed class LegendTableGroup
        {
            public List<LegendColumnRun> Runs = new List<LegendColumnRun>();
            public double MinX;
            public double MaxX;
            public double MinY;
            public double MaxY;
            public double TypRowStep;
            public bool NearHeader;
            public LegendHeaderPair HeaderPair;
        }

        private sealed class LegendGroupExtraction
        {
            public bool NearHeader;
            public int SpecRowsCount;
            public int WattBearingCount;
            public List<ProjectLegendSpecItem> Items;
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
                        if (!fromSel && found.Count > 0)
                        {
                            List<string> staleKeys = new List<string>();
                            foreach (KeyValuePair<string, ProjectLegendSpecItem> kv in cat)
                            {
                                if (kv.Value != null &&
                                    string.Equals(kv.Value.Source, "drawing_legend", StringComparison.OrdinalIgnoreCase))
                                {
                                    staleKeys.Add(kv.Key);
                                }
                            }
                            for (int si = 0; si < staleKeys.Count; si++) cat.Remove(staleKeys[si]);
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
                            string tagStr = GetStr(d, "legend_tag");
                            if (!string.IsNullOrWhiteSpace(tagStr)) cur.LegendTag = tagStr.Trim();
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

        private static Dictionary<string, Dictionary<string, object>> GetSavedDialuxRoomPrefs(Document doc)
        {
            Dictionary<string, Dictionary<string, object>> result =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            if (doc == null || doc.Database == null || doc.Database.UnmanagedObject == IntPtr.Zero)
                return result;

            IntPtr dbPtr = doc.Database.UnmanagedObject;
            lock (s_lastCoordLock)
            {
                EnsureDocDestroyedHook();
                EnsureProjectLegendDbLoadedLocked(doc, doc.Database);
                Dictionary<string, Dictionary<string, object>> stored;
                if (s_dialuxRoomPrefsByDb.TryGetValue(dbPtr, out stored) && stored != null)
                {
                    foreach (KeyValuePair<string, Dictionary<string, object>> kv in stored)
                    {
                        if (kv.Value != null)
                            result[kv.Key] = new Dictionary<string, object>(kv.Value, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            return result;
        }

        private static void SaveDialuxRoomPrefs(
            Document doc,
            Dictionary<string, Dictionary<string, object>> prefs)
        {
            if (doc == null || doc.Database == null || doc.Database.UnmanagedObject == IntPtr.Zero || prefs == null)
                return;

            IntPtr dbPtr = doc.Database.UnmanagedObject;
            lock (s_lastCoordLock)
            {
                EnsureDocDestroyedHook();
                EnsureProjectLegendDbLoadedLocked(doc, doc.Database);
                Dictionary<string, Dictionary<string, object>> stored;
                if (!s_dialuxRoomPrefsByDb.TryGetValue(dbPtr, out stored) || stored == null)
                {
                    stored = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                    s_dialuxRoomPrefsByDb[dbPtr] = stored;
                }
                foreach (KeyValuePair<string, Dictionary<string, object>> kv in prefs)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                    stored[kv.Key.Trim()] = new Dictionary<string, object>(kv.Value, StringComparer.OrdinalIgnoreCase);
                }
                SaveProjectDatabaseToDiskLocked(doc, doc.Database);
            }
        }
    }
}
