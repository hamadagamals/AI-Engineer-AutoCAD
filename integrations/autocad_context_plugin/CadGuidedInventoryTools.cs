using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

#if !PANEL_MFG_STANDALONE
[assembly: CommandClass(typeof(AIEngineer.AutoCAD.GuidedInventoryCommands))]
#endif

namespace AIEngineer.AutoCAD
{
#if !PANEL_MFG_STANDALONE
    public sealed class GuidedInventoryCommands
    {
        [CommandMethod("AIENGINVENTORY", CommandFlags.Session)]
        public void RunGuidedInventoryCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            PromptKeywordOptions discOpts = new PromptKeywordOptions(
                "\n[AI-Engineer] اختر التخصص للجرد التفاعلي [All/Lighting/Power]: ",
                "All Lighting Power");
            discOpts.Keywords.Default = "Lighting";
            discOpts.AllowNone = true;
            PromptResult discRes = ed.GetKeywords(discOpts);
            if (discRes.Status == PromptStatus.Cancel) return;
            string disc = string.IsNullOrWhiteSpace(discRes.StringResult)
                ? "lighting"
                : discRes.StringResult.Trim().ToLowerInvariant();

            Dictionary<string, object> startRes = CadToolExecutor.ExecuteTool(doc, "guided_circuit_inventory", new Dictionary<string, object>
            {
                { "action", "start" },
                { "discipline", disc },
                { "strict_legend_specs", true },
                { "zoom", true }
            });
            PrintGuidedCommandResult(ed, startRes);

            while (true)
            {
                Dictionary<string, object> st = CadToolExecutor.GetGuidedInventoryStatus(doc);
                bool active = st.ContainsKey("active") && st["active"] is bool && (bool)st["active"];
                if (!active) break;

                string curCirc = st.ContainsKey("circuit_name") && st["circuit_name"] != null ? st["circuit_name"].ToString() : "";
                string curPanel = st.ContainsKey("panel_name") && st["panel_name"] != null ? st["panel_name"].ToString() : "";
                int curIdx = st.ContainsKey("current_index") ? Convert.ToInt32(st["current_index"], CultureInfo.InvariantCulture) : 0;
                int tot = st.ContainsKey("total_count") ? Convert.ToInt32(st["total_count"], CultureInfo.InvariantCulture) : 0;

                PromptKeywordOptions stepOpts = new PromptKeywordOptions(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "\n[جرد {0}/{1}] {2} ({3}) — [Ok/Add/Remove/Refresh/Skip/Audit/Stop]: ",
                        curIdx + 1, tot, curCirc, curPanel),
                    "Ok Add Remove Refresh Skip Audit Stop");
                stepOpts.Keywords.Default = "Ok";
                stepOpts.AllowNone = true;
                PromptResult stepRes = ed.GetKeywords(stepOpts);
                if (stepRes.Status == PromptStatus.Cancel)
                {
                    CadToolExecutor.ExecuteTool(doc, "guided_circuit_inventory", new Dictionary<string, object> { { "action", "stop" } });
                    break;
                }

                string kw = string.IsNullOrWhiteSpace(stepRes.StringResult) ? "ok" : stepRes.StringResult.Trim().ToLowerInvariant();
                string action = "status";
                if (kw == "ok") action = "approve_next";
                else if (kw == "add") action = "add_selection";
                else if (kw == "remove") action = "remove_selection";
                else if (kw == "refresh") action = "refresh";
                else if (kw == "skip") action = "skip";
                else if (kw == "audit") action = "audit_remaining";
                else if (kw == "stop") action = "stop";

                Dictionary<string, object> stepOut = CadToolExecutor.ExecuteTool(doc, "guided_circuit_inventory", new Dictionary<string, object>
                {
                    { "action", action },
                    { "zoom", true }
                });
                PrintGuidedCommandResult(ed, stepOut);
                if (action == "stop") break;
            }
        }

        private static void PrintGuidedCommandResult(Editor ed, Dictionary<string, object> res)
        {
            if (ed == null || res == null) return;
            object msgObj;
            if (res.TryGetValue("message", out msgObj) && msgObj != null)
                ed.WriteMessage("\n[AI-Engineer] " + msgObj.ToString() + "\n");
            else if (res.TryGetValue("error", out msgObj) && msgObj != null)
                ed.WriteMessage("\n[AI-Engineer ERROR] " + msgObj.ToString() + "\n");
        }
    }
#endif

    internal static partial class CadToolExecutor
    {
        private const string GuidedHighlightLayer = "_AI_GUIDED_INVENTORY";
        private const string CircuitTagRegApp = "AIENG_CIRCUIT_TAG";

        private static string ReadCircuitXDataTag(Entity ent)
        {
            if (ent == null || ent.IsErased) return "";
            try
            {
                ResultBuffer rb = ent.GetXDataForApplication(CircuitTagRegApp);
                if (rb == null) return "";
                foreach (TypedValue tv in rb)
                {
                    if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString && tv.Value != null)
                        return tv.Value.ToString();
                }
            }
            catch { }
            return "";
        }

        private static bool TryParseCircuitXDataTag(string payload, out string kind, out string circuitName, out string panelName, out string discipline)
        {
            kind = "";
            circuitName = "";
            panelName = "";
            discipline = "";
            if (string.IsNullOrWhiteSpace(payload)) return false;
            string[] parts = payload.Split('|');
            if (parts.Length < 2) return false;
            kind = (parts[0] ?? "").Trim().ToUpperInvariant();
            circuitName = (parts[1] ?? "").Trim();
            if (parts.Length >= 3) panelName = (parts[2] ?? "").Trim();
            if (parts.Length >= 4) discipline = (parts[3] ?? "").Trim().ToLowerInvariant();
            return !string.IsNullOrWhiteSpace(circuitName);
        }

        private static void StampCircuitXData(
            Database db,
            Transaction tr,
            IEnumerable<string> wireHandles,
            IEnumerable<string> blockHandles,
            IEnumerable<string> excludedHandles,
            string circuitName,
            string panelName,
            string discipline)
        {
            if (db == null || tr == null || string.IsNullOrWhiteSpace(circuitName)) return;
            EnsureRegApp(db, tr, CircuitTagRegApp);
            string cSafe = circuitName.Replace("|", "/").Trim();
            string pSafe = (panelName ?? "").Replace("|", "/").Trim();
            string dSafe = (discipline ?? "general").Replace("|", "/").Trim().ToLowerInvariant();
            string targetNorm = NormalizeCircuitKey(cSafe);

            if (wireHandles != null)
            {
                string payload = "W|" + cSafe + "|" + pSafe + "|" + dSafe;
                foreach (string wh in wireHandles)
                {
                    ObjectId oid = ResolveHandle(db, wh);
                    if (oid.IsNull || oid.IsErased) continue;
                    try
                    {
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        ent.UpgradeOpen();
                        ent.XData = new ResultBuffer(
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, CircuitTagRegApp),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, payload));
                    }
                    catch { }
                }
            }

            if (blockHandles != null)
            {
                string payload = "B|" + cSafe + "|" + pSafe + "|" + dSafe;
                foreach (string bh in blockHandles)
                {
                    ObjectId oid = ResolveHandle(db, bh);
                    if (oid.IsNull || oid.IsErased) continue;
                    try
                    {
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        ent.UpgradeOpen();
                        ent.XData = new ResultBuffer(
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, CircuitTagRegApp),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, payload));
                    }
                    catch { }
                }
            }

            if (excludedHandles != null)
            {
                foreach (string eh in excludedHandles)
                {
                    ObjectId oid = ResolveHandle(db, eh);
                    if (oid.IsNull || oid.IsErased) continue;
                    try
                    {
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        string curTag = ReadCircuitXDataTag(ent);
                        string tk, tc, tp, td;
                        if (TryParseCircuitXDataTag(curTag, out tk, out tc, out tp, out td) &&
                            string.Equals(NormalizeCircuitKey(tc), targetNorm, StringComparison.OrdinalIgnoreCase))
                        {
                            ent.UpgradeOpen();
                            ent.XData = new ResultBuffer(
                                new TypedValue((int)DxfCode.ExtendedDataRegAppName, CircuitTagRegApp));
                        }
                    }
                    catch { }
                }
            }
        }

        private static void RecoverCircuitHandlesFromXData(
            Database db,
            Transaction tr,
            string circuitName,
            HashSet<string> excludedHandles,
            out List<string> wireHandles,
            out List<string> blockHandles)
        {
            wireHandles = new List<string>();
            blockHandles = new List<string>();
            if (db == null || tr == null || string.IsNullOrWhiteSpace(circuitName)) return;
            string targetNorm = NormalizeCircuitKey(circuitName);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId oid in ms)
            {
                if (oid.IsErased) continue;
                Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                string payload = ReadCircuitXDataTag(ent);
                if (string.IsNullOrEmpty(payload)) continue;
                string kind, cTag, pTag, dTag;
                if (!TryParseCircuitXDataTag(payload, out kind, out cTag, out pTag, out dTag)) continue;
                if (!string.Equals(NormalizeCircuitKey(cTag), targetNorm, StringComparison.OrdinalIgnoreCase)) continue;
                string h = ent.Handle.ToString();
                if (excludedHandles != null && excludedHandles.Contains(h)) continue;
                if (!seen.Add(h)) continue;
                if (ent is Curve || kind == "W") wireHandles.Add(h);
                else if (ent is BlockReference || kind == "B") blockHandles.Add(h);
            }
        }

        internal sealed class GuidedInventoryItem
        {
            public string CircuitName = "";
            public string PanelName = "";
            public string Discipline = "general";
            public string WireLayer = "";
            public string WireColorKey = "";
            public string TerminationType = "open_end";
            public string Status = "pending"; // pending | approved | skipped
            public List<string> WireHandles = new List<string>();
            public List<string> ExplicitBlockHandles = new List<string>();
            public List<string> MatchedBlockHandles = new List<string>();
            public HashSet<string> ExcludedHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> CountsByBlock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public double LengthMeters;
            public double LoadKw;
            public double LoadKva;
            public string LoadSource = "";
            public bool NeedsUserSpecs;
            public List<Dictionary<string, object>> MissingSpecBlocks = new List<Dictionary<string, object>>();
            public bool GuardTriggered;
            public int MaxCircuitBlocks = 35;
            public List<string> PanelWays = new List<string>();
            public int PanelWaysCount;
            public string CableDesignation = "";
            public int CableCoresCount;
            public int TotalConductorsCount;
            public string ConduitSpec = "";
            public string InstallNote = "";
            public double? AnnotatedVdPercent;
            public string RawCalloutText = "";
        }

        internal sealed class GuidedInventorySession
        {
            public string SessionId = "";
            public string DisciplineMode = "all";
            public string PanelFilter = "";
            public bool StrictLegendSpecs = true;
            public bool AutoDrawBoard = true;
            public bool Active;
            public int CurrentIndex;
            public List<GuidedInventoryItem> Queue = new List<GuidedInventoryItem>();
            public double EffectiveUnitsPerM = 1000.0;
            public double MedianFixtureSpan;
            public double DrawingSpan;
            public DateTime UpdatedUtc = DateTime.UtcNow;
        }

        private static readonly Dictionary<IntPtr, GuidedInventorySession> s_guidedInventoryByDb =
            new Dictionary<IntPtr, GuidedInventorySession>();

        internal static Dictionary<string, object> GetGuidedInventoryStatus(Document doc)
        {
            Dictionary<string, object> st = new Dictionary<string, object>
            {
                { "active", false },
                { "current_index", 0 },
                { "total_count", 0 },
                { "approved_count", 0 },
                { "skipped_count", 0 },
                { "remaining_count", 0 },
                { "panel_name", "" },
                { "circuit_name", "" },
                { "discipline", "" },
                { "needs_user_specs", false },
                { "missing_spec_count", 0 }
            };
            if (doc == null || doc.Database == null || doc.Database.UnmanagedObject == IntPtr.Zero) return st;

            lock (s_lastCoordLock)
            {
                GuidedInventorySession session;
                if (!s_guidedInventoryByDb.TryGetValue(doc.Database.UnmanagedObject, out session) || session == null)
                    return st;

                int approved = 0;
                int skipped = 0;
                int remaining = 0;
                for (int i = 0; i < session.Queue.Count; i++)
                {
                    string s = session.Queue[i].Status ?? "pending";
                    if (s == "approved") approved++;
                    else if (s == "skipped") skipped++;
                    else remaining++;
                }

                st["active"] = session.Active;
                st["current_index"] = session.CurrentIndex;
                st["total_count"] = session.Queue.Count;
                st["approved_count"] = approved;
                st["skipped_count"] = skipped;
                st["remaining_count"] = remaining;
                st["discipline_mode"] = session.DisciplineMode ?? "all";

                if (session.Active && session.CurrentIndex >= 0 && session.CurrentIndex < session.Queue.Count)
                {
                    GuidedInventoryItem item = session.Queue[session.CurrentIndex];
                    st["panel_name"] = item.PanelName ?? "";
                    st["circuit_name"] = item.CircuitName ?? "";
                    st["discipline"] = item.Discipline ?? "general";
                    st["needs_user_specs"] = item.NeedsUserSpecs;
                    st["missing_spec_count"] = item.MissingSpecBlocks != null ? item.MissingSpecBlocks.Count : 0;
                }
            }
            return st;
        }

        internal static Dictionary<string, List<int>> ComputeMissingCircuitNumberGaps(
            List<SavedCircuitRecord> records,
            out Dictionary<string, List<int>> presentNumbersByPanel,
            out Dictionary<string, List<string>> presentNamesByPanel,
            out Dictionary<string, Dictionary<string, int>> disciplineCountsByPanel)
        {
            Dictionary<string, List<int>> missingByPanel = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            presentNumbersByPanel = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            presentNamesByPanel = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            disciplineCountsByPanel = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, SortedSet<int>> numSets = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
            if (records == null) return missingByPanel;

            for (int i = 0; i < records.Count; i++)
            {
                SavedCircuitRecord r = records[i];
                if (r == null) continue;
                string pn = ExtractPanelNameFallback(r.CircuitName, r.PanelName);
                if (!numSets.ContainsKey(pn))
                {
                    numSets[pn] = new SortedSet<int>();
                    presentNamesByPanel[pn] = new List<string>();
                    disciplineCountsByPanel[pn] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }
                if (!string.IsNullOrWhiteSpace(r.CircuitName) && !presentNamesByPanel[pn].Contains(r.CircuitName))
                    presentNamesByPanel[pn].Add(r.CircuitName);

                string disc = !string.IsNullOrWhiteSpace(r.Discipline) ? r.Discipline.Trim().ToLowerInvariant() : "general";
                Dictionary<string, int> dc = disciplineCountsByPanel[pn];
                dc[disc] = dc.ContainsKey(disc) ? dc[disc] + 1 : 1;

                int cNum;
                if (TryExtractCircuitNumberInt(r.CircuitName, out cNum) && cNum > 0 && cNum <= 120)
                    numSets[pn].Add(cNum);
            }

            List<string> orderedPanels = new List<string>(numSets.Keys);
            orderedPanels.Sort(CompareNaturalTokens);
            for (int pi = 0; pi < orderedPanels.Count; pi++)
            {
                string pn = orderedPanels[pi];
                SortedSet<int> set = numSets[pn];
                List<int> present = new List<int>(set);
                presentNumbersByPanel[pn] = present;
                List<int> missing = new List<int>();
                if (present.Count > 0)
                {
                    int maxNum = present[present.Count - 1];
                    for (int n = 1; n <= maxNum; n++)
                    {
                        if (!set.Contains(n)) missing.Add(n);
                    }
                }
                missingByPanel[pn] = missing;
            }
            return missingByPanel;
        }

        private static string FormatCountsSummary(Dictionary<string, int> counts)
        {
            if (counts == null || counts.Count == 0) return "-";
            List<KeyValuePair<string, int>> pairs = new List<KeyValuePair<string, int>>();
            foreach (KeyValuePair<string, int> kv in counts)
            {
                if (!kv.Key.StartsWith("__", StringComparison.Ordinal) && kv.Value > 0)
                    pairs.Add(kv);
            }
            if (pairs.Count == 0) return "-";
            pairs.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : CompareNaturalTokens(a.Key, b.Key);
            });
            List<string> parts = new List<string>(pairs.Count);
            for (int i = 0; i < pairs.Count; i++)
                parts.Add(pairs[i].Key + " x" + pairs[i].Value.ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", parts.ToArray());
        }

        private static int CountTotalMatchedBlocks(Dictionary<string, int> counts)
        {
            if (counts == null) return 0;
            int total = 0;
            foreach (KeyValuePair<string, int> kv in counts)
            {
                if (!kv.Key.StartsWith("__", StringComparison.Ordinal) && kv.Value > 0)
                    total += kv.Value;
            }
            return total;
        }

        private static void RecomputeGuidedItemSpecs(
            Document doc,
            GuidedInventoryItem item,
            bool strictLegendSpecs)
        {
            if (item == null) return;
            double kw, kva;
            bool loadAssumed;
            string loadSource;
            Dictionary<string, object> rowLoad, specBreakdown;
            List<string> missingNames;
            ComputeCircuitLoadFromProjectDb(
                doc, item.CountsByBlock, null, null, 40.0, 0.90, strictLegendSpecs,
                out kw, out kva, out loadAssumed, out loadSource,
                out rowLoad, out missingNames, out specBreakdown);

            item.LoadKw = Math.Round(kw, 3);
            item.LoadKva = Math.Round(kva, 3);
            item.LoadSource = loadSource ?? "";
            item.NeedsUserSpecs = missingNames != null && missingNames.Count > 0;
            item.MissingSpecBlocks = new List<Dictionary<string, object>>();
            if (missingNames != null)
            {
                for (int i = 0; i < missingNames.Count; i++)
                {
                    string bn = missingNames[i];
                    int cnt = item.CountsByBlock.ContainsKey(bn) ? item.CountsByBlock[bn] : 0;
                    item.MissingSpecBlocks.Add(new Dictionary<string, object>
                    {
                        { "block_name", bn },
                        { "count", cnt }
                    });
                }
            }
        }

        private static Dictionary<string, object> GuidedItemToDict(GuidedInventoryItem item, int queueIndex, int totalCount)
        {
            if (item == null) return new Dictionary<string, object>();
            Dictionary<string, object> d = new Dictionary<string, object>
            {
                { "queue_index", queueIndex + 1 },
                { "total_count", totalCount },
                { "circuit_name", item.CircuitName ?? "" },
                { "panel_name", item.PanelName ?? "" },
                { "discipline", item.Discipline ?? "general" },
                { "status", item.Status ?? "pending" },
                { "wire_layer", item.WireLayer ?? "" },
                { "wire_color_key", item.WireColorKey ?? "" },
                { "wire_handles", new List<string>(item.WireHandles) },
                { "path_curves_count", item.WireHandles.Count },
                { "matched_block_handles", new List<string>(item.MatchedBlockHandles) },
                { "matched_blocks_count", CountTotalMatchedBlocks(item.CountsByBlock) },
                { "counts_by_block", new Dictionary<string, int>(item.CountsByBlock, StringComparer.OrdinalIgnoreCase) },
                { "counts_summary", FormatCountsSummary(item.CountsByBlock) },
                { "excluded_handles_count", item.ExcludedHandles.Count },
                { "length_m", Math.Round(item.LengthMeters, 2) },
                { "load_kw", Math.Round(item.LoadKw, 3) },
                { "load_kva", Math.Round(item.LoadKva, 3) },
                { "load_source", item.LoadSource ?? "" },
                { "needs_user_specs", item.NeedsUserSpecs },
                { "missing_spec_blocks", item.MissingSpecBlocks ?? new List<Dictionary<string, object>>() },
                { "guard_triggered", item.GuardTriggered }
            };
            if (item.PanelWays != null && item.PanelWays.Count > 0)
            {
                d["panel_ways"] = new List<string>(item.PanelWays);
                d["panel_ways_count"] = item.PanelWaysCount;
            }
            if (!string.IsNullOrEmpty(item.CableDesignation)) d["cable_spec"] = item.CableDesignation;
            if (item.CableCoresCount > 0) d["cable_cores_count"] = item.CableCoresCount;
            if (item.TotalConductorsCount > 0) d["total_conductors_count"] = item.TotalConductorsCount;
            if (!string.IsNullOrEmpty(item.ConduitSpec)) d["conduit_spec"] = item.ConduitSpec;
            if (!string.IsNullOrEmpty(item.InstallNote)) d["install_note"] = item.InstallNote;
            if (item.AnnotatedVdPercent.HasValue) d["annotated_vd_percent"] = Math.Round(item.AnnotatedVdPercent.Value, 3);
            if (!string.IsNullOrEmpty(item.RawCalloutText)) d["raw_callout_text"] = item.RawCalloutText;
            return d;
        }

        private static void ClearGuidedHighlightLayer(Database db, Transaction tr)
        {
            if (db == null || tr == null) return;
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId oid in ms)
            {
                if (oid.IsErased) continue;
                Entity e = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                if (e == null || e.IsErased) continue;
                if (!string.Equals(e.Layer ?? "", GuidedHighlightLayer, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    e.UpgradeOpen();
                    e.Erase(true);
                }
                catch { }
            }
        }

        private static Extents3d? ReevaluateGuidedItemFromGeometry(
            Document doc,
            GuidedInventorySession session,
            GuidedInventoryItem item,
            bool expandForward)
        {
            if (doc == null || doc.Database == null || item == null) return null;
            Database db = doc.Database;
            double unitsPerM = session != null && session.EffectiveUnitsPerM > 1e-6 ? session.EffectiveUnitsPerM : 1000.0;
            double refSpan = session != null ? session.MedianFixtureSpan : 0.0;
            double drawingSpan = session != null ? session.DrawingSpan : 0.0;

            CircuitSampleProfile learnedProfile = null;
            lock (s_lastCoordLock)
            {
                s_circuitProfileByDb.TryGetValue(db.UnmanagedObject, out learnedProfile);
            }
            if (learnedProfile != null && learnedProfile.MedianFixtureSpan > 1e-3)
                refSpan = learnedProfile.MedianFixtureSpan;

            List<ObjectId> wireIds = new List<ObjectId>();
            List<string> wireHandles = new List<string>();
            HashSet<ObjectId> wireSet = new HashSet<ObjectId>();
            List<ObjectId> explicitBlkIds = new List<ObjectId>();
            HashSet<ObjectId> explicitBlkSet = new HashSet<ObjectId>();
            HashSet<ObjectId> excludedIds = new HashSet<ObjectId>();

            List<PathCurveCandidate> pathCandidates = new List<PathCurveCandidate>();
            List<PathBlockMatchItem> matchedItems = new List<PathBlockMatchItem>();
            Dictionary<string, int> countsByBlock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> pathEntityTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double totalPathLenUnits = 0.0;
            Extents3d? combinedExt = null;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                    Dictionary<string, SavedColorState> layerColorMap = BuildLayerColorStateMap(db, tr);

                    foreach (string eh in item.ExcludedHandles)
                    {
                        ObjectId eid = ResolveHandle(db, eh);
                        if (!eid.IsNull && !eid.IsErased) excludedIds.Add(eid);
                    }

                    for (int i = 0; i < item.WireHandles.Count; i++)
                    {
                        string wh = item.WireHandles[i];
                        if (string.IsNullOrWhiteSpace(wh) || item.ExcludedHandles.Contains(wh)) continue;
                        ObjectId oid = ResolveHandle(db, wh);
                        if (oid.IsNull || oid.IsErased || excludedIds.Contains(oid) || !wireSet.Add(oid)) continue;
                        Curve cv = tr.GetObject(oid, OpenMode.ForRead) as Curve;
                        if (cv == null || cv.IsErased) continue;
                        wireIds.Add(oid);
                        wireHandles.Add(cv.Handle.ToString());
                        if (string.IsNullOrWhiteSpace(item.WireLayer))
                            item.WireLayer = cv.Layer ?? "0";
                        if (string.IsNullOrWhiteSpace(item.WireColorKey))
                        {
                            bool nn;
                            item.WireColorKey = ResolveEntityEffectiveColorKey(cv, layerColorMap, out nn);
                        }
                    }

                    for (int i = 0; i < item.ExplicitBlockHandles.Count; i++)
                    {
                        string bh = item.ExplicitBlockHandles[i];
                        if (string.IsNullOrWhiteSpace(bh) || item.ExcludedHandles.Contains(bh)) continue;
                        ObjectId oid = ResolveHandle(db, bh);
                        if (oid.IsNull || oid.IsErased || excludedIds.Contains(oid) || !explicitBlkSet.Add(oid)) continue;
                        BlockReference br = tr.GetObject(oid, OpenMode.ForRead) as BlockReference;
                        if (br == null || br.IsErased) continue;
                        explicitBlkIds.Add(oid);
                    }

                    HashSet<ObjectId> touchedTerms = new HashSet<ObjectId>();
                    if (expandForward && wireIds.Count > 0)
                    {
                        double chainSnapTol = refSpan > 1e-3
                            ? Math.Max(1e-3, refSpan * 0.12)
                            : Math.Max(1e-3, 0.05 * unitsPerM);
                        List<string> bridgedNames;
                        List<ObjectId> termIds;
                        bool guardTriggered;
                        ExpandConnectedPathCurves(
                            db, tr, wireIds, wireHandles, layerHiddenFlags, true, chainSnapTol,
                            true, true, true, false,
                            unitsPerM, refSpan, drawingSpan,
                            false, new List<string>(), null, learnedProfile,
                            Math.Max(35, item.MaxCircuitBlocks), excludedIds,
                            out bridgedNames, out termIds, out guardTriggered);
                        item.GuardTriggered = guardTriggered;
                        if (termIds != null)
                        {
                            for (int ti = 0; ti < termIds.Count; ti++) touchedTerms.Add(termIds[ti]);
                        }
                    }

                    // Rebuild sanitized WireHandles excluding excludedIds
                    List<ObjectId> finalWireIds = new List<ObjectId>();
                    List<string> finalWireHandles = new List<string>();
                    HashSet<ObjectId> finalWireSet = new HashSet<ObjectId>();
                    for (int i = 0; i < wireIds.Count; i++)
                    {
                        ObjectId oid = wireIds[i];
                        if (oid.IsNull || oid.IsErased || excludedIds.Contains(oid) || !finalWireSet.Add(oid)) continue;
                        Curve cv = tr.GetObject(oid, OpenMode.ForRead) as Curve;
                        if (cv == null || cv.IsErased) continue;
                        finalWireIds.Add(oid);
                        finalWireHandles.Add(cv.Handle.ToString());
                    }
                    wireIds = finalWireIds;
                    item.WireHandles = finalWireHandles;

                    if (wireIds.Count > 0)
                    {
                        CollectCircuitPathCandidates(tr, wireIds, pathCandidates, pathEntityTypes, ref totalPathLenUnits, ref combinedExt);
                    }

                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    List<CircuitTerminatorInfo> terminators = new List<CircuitTerminatorInfo>();
                    if (pathCandidates.Count > 0)
                    {
                        int skippedContainers;
                        MatchBlocksAlongCircuitPath(
                            tr, ms, layerHiddenFlags, pathCandidates, finalWireSet, touchedTerms,
                            true, false, false, false, false, true,
                            null, null, new List<string>(), null,
                            unitsPerM, refSpan, drawingSpan, learnedProfile,
                            matchedItems, countsByBlock, terminators, ref combinedExt, out skippedContainers);
                    }

                    // Filter out excluded blocks and union explicit manual blocks
                    List<PathBlockMatchItem> filteredItems = new List<PathBlockMatchItem>();
                    HashSet<ObjectId> seenMatchedIds = new HashSet<ObjectId>();
                    Dictionary<string, int> filteredCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    for (int i = 0; i < matchedItems.Count; i++)
                    {
                        PathBlockMatchItem m = matchedItems[i];
                        if (excludedIds.Contains(m.Id) || item.ExcludedHandles.Contains(m.Handle)) continue;
                        if (!seenMatchedIds.Add(m.Id)) continue;
                        filteredItems.Add(m);
                        filteredCounts[m.BlockName] = filteredCounts.ContainsKey(m.BlockName) ? filteredCounts[m.BlockName] + 1 : 1;
                    }

                    List<string> liveExplicitHandles = new List<string>();
                    for (int i = 0; i < explicitBlkIds.Count; i++)
                    {
                        ObjectId bid = explicitBlkIds[i];
                        BlockReference br = tr.GetObject(bid, OpenMode.ForRead) as BlockReference;
                        if (br == null || br.IsErased) continue;
                        string bh = br.Handle.ToString();
                        if (item.ExcludedHandles.Contains(bh) || excludedIds.Contains(bid)) continue;
                        liveExplicitHandles.Add(bh);
                        if (!seenMatchedIds.Add(bid)) continue;
                        string bn = ResolveBlockName(tr, br);
                        if (string.IsNullOrEmpty(bn) || IsAnonymousBlockName(bn)) continue;
                        Extents3d bex;
                        try { bex = br.GeometricExtents; } catch { continue; }
                        if (!IsFiniteBounds(bex)) continue;
                        filteredItems.Add(new PathBlockMatchItem
                        {
                            Id = bid,
                            Handle = bh,
                            BlockName = bn,
                            Layer = br.Layer ?? "0",
                            ColorIndex = br.ColorIndex,
                            X = (bex.MinPoint.X + bex.MaxPoint.X) * 0.5,
                            Y = (bex.MinPoint.Y + bex.MaxPoint.Y) * 0.5,
                            Bounds = bex
                        });
                        filteredCounts[bn] = filteredCounts.ContainsKey(bn) ? filteredCounts[bn] + 1 : 1;
                        if (!combinedExt.HasValue) combinedExt = bex;
                        else { Extents3d cur = combinedExt.Value; cur.AddExtents(bex); combinedExt = cur; }
                    }
                    item.ExplicitBlockHandles = liveExplicitHandles;

                    item.MatchedBlockHandles = new List<string>(filteredItems.Count);
                    for (int i = 0; i < filteredItems.Count; i++)
                        item.MatchedBlockHandles.Add(filteredItems[i].Handle);
                    item.CountsByBlock = filteredCounts;
                    item.LengthMeters = Math.Round(totalPathLenUnits / unitsPerM, 3);

                    if (wireIds.Count > 0 && string.IsNullOrWhiteSpace(item.CircuitName))
                    {
                        double tightTipTol = refSpan > 1e-3 ? Math.Max(1e-3, refSpan * 0.12) : Math.Max(1e-3, 0.06 * unitsPerM);
                        List<Point2d> openTips = CollectOpenWireTips(tr, wireIds, tightTipTol);
                        double tagSearchR = refSpan > 1e-3 ? (refSpan * 4.5) : Math.Max(2.5 * unitsPerM, 2.5);
                        string foundC, foundP, foundT;
                        ParsedCircuitCallout foundCallout;
                        CollectNearbyCircuitTags(tr, ms, layerHiddenFlags, true, tagSearchR, terminators, openTips, out foundC, out foundP, out foundT, out foundCallout);
                        if (!string.IsNullOrWhiteSpace(foundC)) item.CircuitName = foundC;
                        if (!string.IsNullOrWhiteSpace(foundP)) item.PanelName = foundP;
                        if (!string.IsNullOrWhiteSpace(foundT)) item.TerminationType = foundT;
                        if (foundCallout != null)
                        {
                            if (foundCallout.PanelWays.Count > 0 && (item.PanelWays == null || item.PanelWays.Count == 0))
                            {
                                item.PanelWays = new List<string>(foundCallout.PanelWays);
                                item.PanelWaysCount = foundCallout.PanelWaysCount;
                            }
                            if (!string.IsNullOrEmpty(foundCallout.CableSpec) && string.IsNullOrEmpty(item.CableDesignation))
                            {
                                item.CableDesignation = foundCallout.CableSpec;
                                item.CableCoresCount = foundCallout.CableCoresCount;
                                item.TotalConductorsCount = foundCallout.TotalConductorsCount;
                            }
                            if (!string.IsNullOrEmpty(foundCallout.ConduitSpec) && string.IsNullOrEmpty(item.ConduitSpec))
                                item.ConduitSpec = foundCallout.ConduitSpec;
                            if (!string.IsNullOrEmpty(foundCallout.InstallNote) && string.IsNullOrEmpty(item.InstallNote))
                                item.InstallNote = foundCallout.InstallNote;
                            if (foundCallout.AnnotatedVdPercent.HasValue && !item.AnnotatedVdPercent.HasValue)
                                item.AnnotatedVdPercent = foundCallout.AnnotatedVdPercent;
                            if (!string.IsNullOrEmpty(foundCallout.RawText) && string.IsNullOrEmpty(item.RawCalloutText))
                                item.RawCalloutText = foundCallout.RawText;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(item.CircuitName))
                        item.PanelName = ExtractPanelNameFallback(item.CircuitName, item.PanelName);
                    else if (string.IsNullOrWhiteSpace(item.PanelName))
                        item.PanelName = "LP-MAIN";

                    tr.Commit();
                }
                finally
                {
                    for (int i = 0; i < pathCandidates.Count; i++)
                    {
                        try { if (pathCandidates[i].FlatCurve != null) pathCandidates[i].FlatCurve.Dispose(); } catch { }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(item.Discipline) || item.Discipline == "general")
                item.Discipline = InferCircuitDiscipline(doc, item.CircuitName, item.WireLayer, item.CountsByBlock);

            RecomputeGuidedItemSpecs(doc, item, session != null ? session.StrictLegendSpecs : true);
            return combinedExt;
        }

        private static void FocusAndHighlightGuidedItem(
            Document doc,
            GuidedInventorySession session,
            GuidedInventoryItem item,
            int index,
            int totalCount,
            bool zoom)
        {
            if (doc == null || doc.Database == null || item == null) return;
            Database db = doc.Database;
            double unitsPerM = session != null && session.EffectiveUnitsPerM > 1e-6 ? session.EffectiveUnitsPerM : 1000.0;
            double refSpan = session != null ? session.MedianFixtureSpan : 0.0;
            List<ObjectId> selectIds = new List<ObjectId>();
            Extents3d? totalExt = null;
            bool switchedToModel = false;

            using (DocumentLock docLock = doc.LockDocument())
            {
                if (!db.TileMode)
                {
                    try { LayoutManager.Current.CurrentLayout = "Model"; switchedToModel = true; } catch { }
                }
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ClearGuidedHighlightLayer(db, tr);
                    string layer = EnsureLayerSafe(db, tr, GuidedHighlightLayer, 4, "Continuous");
                    ObjectId unicodeStyleId = EnsureUnicodeTextStyle(db, tr);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                    for (int i = 0; i < item.WireHandles.Count; i++)
                    {
                        ObjectId oid = ResolveHandle(db, item.WireHandles[i]);
                        if (oid.IsNull || oid.IsErased) continue;
                        Entity e = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (e == null || e.IsErased) continue;
                        selectIds.Add(oid);
                        try
                        {
                            Extents3d ex = e.GeometricExtents;
                            if (IsFiniteBounds(ex))
                            {
                                if (!totalExt.HasValue) totalExt = ex;
                                else { Extents3d c = totalExt.Value; c.AddExtents(ex); totalExt = c; }
                            }
                        }
                        catch { }
                    }

                    int markersDrawn = 0;
                    for (int i = 0; i < item.MatchedBlockHandles.Count; i++)
                    {
                        ObjectId oid = ResolveHandle(db, item.MatchedBlockHandles[i]);
                        if (oid.IsNull || oid.IsErased) continue;
                        Entity e = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (e == null || e.IsErased) continue;
                        if (!selectIds.Contains(oid)) selectIds.Add(oid);
                        try
                        {
                            Extents3d ex = e.GeometricExtents;
                            if (IsFiniteBounds(ex))
                            {
                                if (!totalExt.HasValue) totalExt = ex;
                                else { Extents3d c = totalExt.Value; c.AddExtents(ex); totalExt = c; }
                                if (markersDrawn < 60)
                                {
                                    double cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                                    double cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                                    double rad = Math.Max(ex.MaxPoint.X - ex.MinPoint.X, ex.MaxPoint.Y - ex.MinPoint.Y) * 0.65;
                                    if (rad <= 1e-4) rad = Math.Max(0.15 * unitsPerM, 0.15);
                                    Circle cMark = new Circle(new Point3d(cx, cy, 0.0), Vector3d.ZAxis, rad);
                                    cMark.Layer = layer;
                                    cMark.ColorIndex = 4; // Cyan highlight ring
                                    ms.AppendEntity(cMark);
                                    tr.AddNewlyCreatedDBObject(cMark, true);
                                    markersDrawn++;
                                }
                            }
                        }
                        catch { }
                    }

                    if (totalExt.HasValue && IsFiniteBounds(totalExt.Value))
                    {
                        double pad = refSpan > 1e-3 ? Math.Max(refSpan * 0.9, 0.25 * unitsPerM) : Math.Max(0.4 * unitsPerM, 0.4);
                        Extents3d frameExt = new Extents3d(
                            new Point3d(totalExt.Value.MinPoint.X - pad, totalExt.Value.MinPoint.Y - pad, 0.0),
                            new Point3d(totalExt.Value.MaxPoint.X + pad, totalExt.Value.MaxPoint.Y + pad, 0.0));
                        Polyline box = CreateFramePolyline(frameExt, layer, 4);
                        box.ConstantWidth = pad * 0.08;
                        ms.AppendEntity(box);
                        tr.AddNewlyCreatedDBObject(box, true);

                        double span = Math.Max(frameExt.MaxPoint.X - frameExt.MinPoint.X, frameExt.MaxPoint.Y - frameExt.MinPoint.Y);
                        double txtH = Math.Max(pad * 0.55, span * 0.025);
                        string badgeText = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} ({1}/{2}) | {3} blk | {4}m",
                            item.CircuitName, index + 1, totalCount,
                            CountTotalMatchedBlocks(item.CountsByBlock),
                            Math.Round(item.LengthMeters, 1).ToString("0.#", CultureInfo.InvariantCulture));
                        MText badge = new MText();
                        badge.Layer = layer;
                        badge.ColorIndex = 2; // Yellow label
                        badge.TextStyleId = unicodeStyleId;
                        badge.TextHeight = txtH;
                        badge.Attachment = AttachmentPoint.BottomLeft;
                        badge.Location = new Point3d(frameExt.MinPoint.X, frameExt.MaxPoint.Y + txtH * 0.35, 0.0);
                        badge.Contents = badgeText;
                        ms.AppendEntity(badge);
                        tr.AddNewlyCreatedDBObject(badge, true);
                        totalExt = frameExt;
                    }

                    tr.Commit();
                }
            }

            try { Application.SetSystemVariable("PICKFIRST", 1); } catch { }
            if (selectIds.Count > 0)
            {
                try { doc.Editor.SetImpliedSelection(selectIds.ToArray()); } catch { }
            }
            if (zoom && totalExt.HasValue && IsFiniteBounds(totalExt.Value))
            {
                ZoomToExtents3d(doc, totalExt.Value, 1.25);
            }
            if (switchedToModel || selectIds.Count > 0 || zoom)
            {
                try { doc.Editor.UpdateScreen(); } catch { }
            }
        }

        private static void RedrawPanelStackedBoardOrdered(Document doc, string panelName)
        {
            if (doc == null || doc.Database == null || string.IsNullOrWhiteSpace(panelName)) return;
            Database db = doc.Database;
            string panelKey = ExtractPanelNameFallback(panelName, panelName).Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(panelKey)) return;

            List<SavedCircuitRecord> panelRecords = new List<SavedCircuitRecord>();
            List<SavedCircuitRecord> allMerged = GetMergedSavedCircuitRecords(doc, true);
            for (int i = 0; i < allMerged.Count; i++)
            {
                SavedCircuitRecord r = allMerged[i];
                if (r == null) continue;
                string rPanel = ExtractPanelNameFallback(r.CircuitName, r.PanelName).Trim().ToUpperInvariant();
                if (!string.Equals(rPanel, panelKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsRecordFromCurrentDrawing(doc, r)) continue;
                if (r.MatchedBlockHandles == null || r.MatchedBlockHandles.Count == 0) continue;
                panelRecords.Add(r);
            }

            if (panelRecords.Count == 0) return;
            panelRecords.Sort(delegate(SavedCircuitRecord a, SavedCircuitRecord b)
            {
                return CompareCircuitKeysNatural(a.PanelName, a.CircuitName, b.PanelName, b.CircuitName);
            });

            // Erase only existing stacked circuit tables belonging to this panel before redrawing in numeric order
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId oid in ms)
                {
                    if (oid.IsErased) continue;
                    Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased) continue;
                    if (!string.Equals(ent.Layer ?? "", "_AI_BLOCK_LEGEND", StringComparison.OrdinalIgnoreCase)) continue;
                    string tag = ReadCircTableTag(ent);
                    if (!string.IsNullOrEmpty(tag) &&
                        tag.StartsWith(panelKey + "|", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            ent.UpgradeOpen();
                            ent.Erase(true);
                        }
                        catch { }
                    }
                }
                tr.Commit();
            }

            for (int i = 0; i < panelRecords.Count; i++)
            {
                SavedCircuitRecord r = panelRecords[i];
                string discBadge = r.Discipline == "power" ? "باور" : (r.Discipline == "lighting" ? "إنارة" : "عام");
                string title = string.Format(
                    CultureInfo.InvariantCulture,
                    "حصر بلوكات اللينيا: {0} [{1}] (الطول: {2} م)",
                    r.CircuitName, discBadge, Math.Round(r.LengthMeters, 2).ToString("0.##", CultureInfo.InvariantCulture));

                Dictionary<string, object> lArgs = new Dictionary<string, object>
                {
                    { "handles", r.MatchedBlockHandles.ToArray() },
                    { "title", title },
                    { "footer_label", "إجمالي بلوكات اللينيا (" + r.CircuitName + ")" },
                    { "position", "outside_right" },
                    { "visible_only", true },
                    { "replace_existing", false },
                    { "stack_by_panel", true },
                    { "replace_all_legends", false },
                    { "panel_table_key", panelKey },
                    { "circuit_table_key", r.CircuitName ?? "" },
                    { "export_excel", false },
                    { "zoom", false }
                };

                double kw, kva;
                bool assumed;
                string src;
                Dictionary<string, object> rowLoad, brk;
                List<string> miss;
                ComputeCircuitLoadFromProjectDb(
                    doc, r.CountsByBlock, null, null, 40.0, 0.90, true,
                    out kw, out kva, out assumed, out src, out rowLoad, out miss, out brk);
                if (miss.Count == 0 && kw > 1e-6)
                {
                    LvCircuitCalcResult lv = ComputeSingleLvCircuit(new Dictionary<string, object>
                    {
                        { "name", r.CircuitName },
                        { "load_type", r.Discipline == "power" ? "power" : "lighting" },
                        { "length_m", Math.Max(1.0, r.LengthMeters) },
                        { "load_kw", kw },
                        { "phases", 1 }
                    }, new Dictionary<string, object>(), i + 1);
                    lArgs["include_electrical_columns"] = true;
                    lArgs["row_load_by_block"] = rowLoad;
                    lArgs["total_load_label"] = lv.LoadKw.ToString("0.###", CultureInfo.InvariantCulture) + " kW / " +
                        lv.LoadKva.ToString("0.###", CultureInfo.InvariantCulture) + " kVA";
                    lArgs["max_vd_label"] = lv.VoltageDropPercent.ToString("0.##", CultureInfo.InvariantCulture) + "% | " +
                        lv.CableSizeMm2.ToString("0.#", CultureInfo.InvariantCulture) + "mm2 / " +
                        lv.BreakerRatingA.ToString("0.#", CultureInfo.InvariantCulture) + "A";
                    lArgs["row_vd_label"] = lv.VoltageDropPercent.ToString("0.##", CultureInfo.InvariantCulture) + "% (" +
                        lv.CableSizeMm2.ToString("0.#", CultureInfo.InvariantCulture) + "mm2)";
                    lArgs["vd_pass"] = lv.VoltageDropPercent <= lv.MaxAllowedVdPercent + 1e-6;
                }

                DrawBlockLegend(doc, lArgs);
            }
        }

        private static Dictionary<string, object> BuildCoverageAndGapAudit(
            Document doc,
            bool highlightOrphans,
            bool zoomToOrphans)
        {
            Database db = doc.Database;
            List<string> orphanHandles = new List<string>();
            List<ObjectId> orphanIds = new List<ObjectId>();
            Dictionary<string, int> orphanCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int totalFixtures = 0;
            int coveredFixtures = 0;
            Extents3d? orphanExt = null;

            HashSet<string> coveredHandleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<SavedCircuitRecord> allMerged = GetMergedSavedCircuitRecords(doc, false);
            for (int i = 0; i < allMerged.Count; i++)
            {
                SavedCircuitRecord r = allMerged[i];
                if (r == null || (!r.Approved && !r.UserLocked)) continue;
                if (!IsRecordFromCurrentDrawing(doc, r)) continue;
                if (r.MatchedBlockHandles != null)
                {
                    for (int hi = 0; hi < r.MatchedBlockHandles.Count; hi++)
                        coveredHandleSet.Add(r.MatchedBlockHandles[hi]);
                }
            }

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Dictionary<string, bool> hiddenFlags = BuildLayerHiddenMap(db, tr);
                double dwgSpan, medSpan;
                Extents3d? mainExt;
                ComputeDrawingFixtureMetrics(db, tr, true, hiddenFlags, out dwgSpan, out medSpan, out mainExt);
                Dictionary<string, object> uInfo = GetUnitsInfo(db);
                double rawU = Convert.ToDouble(uInfo["units_per_m"], CultureInfo.InvariantCulture);
                bool scInf;
                string scWarn;
                double effUnitsPerM = ResolveEffectiveUnitsPerMeter(db, rawU, dwgSpan, medSpan, out scInf, out scWarn);
                DrawingBlockGeometryCache cache = BuildDrawingBlockGeometryCache(
                    db, tr, hiddenFlags, true, effUnitsPerM, medSpan, dwgSpan, false, null, null);
                if (cache != null && cache.FixtureBlocks != null)
                {
                    for (int fi = 0; fi < cache.FixtureBlocks.Count; fi++)
                    {
                        CachedBlockEntry fb = cache.FixtureBlocks[fi];
                        if (fb.IsPanelRoot || fb.IsTerminator) continue;
                        totalFixtures++;
                        if (coveredHandleSet.Contains(fb.Handle))
                        {
                            coveredFixtures++;
                        }
                        else
                        {
                            orphanHandles.Add(fb.Handle);
                            orphanIds.Add(fb.Id);
                            orphanCounts[fb.BlockName] = orphanCounts.ContainsKey(fb.BlockName) ? orphanCounts[fb.BlockName] + 1 : 1;
                            if (!orphanExt.HasValue) orphanExt = fb.Bounds;
                            else { Extents3d cur = orphanExt.Value; cur.AddExtents(fb.Bounds); orphanExt = cur; }
                        }
                    }
                }

                if (highlightOrphans)
                {
                    ClearGuidedHighlightLayer(db, tr);
                    string layer = EnsureLayerSafe(db, tr, GuidedHighlightLayer, 1, "Continuous");
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    int cap = Math.Min(100, orphanIds.Count);
                    for (int i = 0; i < cap; i++)
                    {
                        Entity ent = tr.GetObject(orphanIds[i], OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        try
                        {
                            Extents3d ex = ent.GeometricExtents;
                            if (!IsFiniteBounds(ex)) continue;
                            double cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                            double cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                            double r = Math.Max(0.2, Math.Max(ex.MaxPoint.X - ex.MinPoint.X, ex.MaxPoint.Y - ex.MinPoint.Y) * 0.70);
                            Circle c = new Circle(new Point3d(cx, cy, 0.0), Vector3d.ZAxis, r);
                            c.Layer = layer;
                            c.ColorIndex = 1; // Red orphan ring
                            ms.AppendEntity(c);
                            tr.AddNewlyCreatedDBObject(c, true);
                        }
                        catch { }
                    }
                }

                tr.Commit();
            }

            if (highlightOrphans && orphanIds.Count > 0)
            {
                try { Application.SetSystemVariable("PICKFIRST", 1); } catch { }
                try { doc.Editor.SetImpliedSelection(orphanIds.ToArray()); } catch { }
                if (zoomToOrphans && orphanExt.HasValue && IsFiniteBounds(orphanExt.Value))
                    ZoomToExtents3d(doc, orphanExt.Value, 1.25);
                try { doc.Editor.UpdateScreen(); } catch { }
            }

            Dictionary<string, List<int>> presentNums;
            Dictionary<string, List<string>> presentNames;
            Dictionary<string, Dictionary<string, int>> discCounts;
            Dictionary<string, List<int>> missingGaps = ComputeMissingCircuitNumberGaps(
                allMerged, out presentNums, out presentNames, out discCounts);

            List<object> panelGapSummaries = new List<object>();
            List<string> gapMsgs = new List<string>();
            List<string> orderedPanels = new List<string>(missingGaps.Keys);
            orderedPanels.Sort(CompareNaturalTokens);
            for (int pi = 0; pi < orderedPanels.Count; pi++)
            {
                string pn = orderedPanels[pi];
                List<int> gaps = missingGaps[pn];
                List<int> pres = presentNums.ContainsKey(pn) ? presentNums[pn] : new List<int>();
                panelGapSummaries.Add(new Dictionary<string, object>
                {
                    { "panel_name", pn },
                    { "present_circuits", presentNames.ContainsKey(pn) ? presentNames[pn] : new List<string>() },
                    { "present_numbers", pres },
                    { "missing_circuit_numbers", gaps },
                    { "disciplines", discCounts.ContainsKey(pn) ? discCounts[pn] : new Dictionary<string, int>() }
                });
                if (gaps.Count > 0)
                {
                    List<string> gStrs = new List<string>(gaps.Count);
                    for (int gi = 0; gi < gaps.Count; gi++) gStrs.Add(gaps[gi].ToString(CultureInfo.InvariantCulture));
                    gapMsgs.Add(pn + " نواقص: [" + string.Join(", ", gStrs.ToArray()) + "] (قد تكون باور أو احتياطي)");
                }
            }

            double coveragePct = totalFixtures > 0
                ? Math.Round((coveredFixtures * 100.0) / totalFixtures, 1)
                : 100.0;
            return new Dictionary<string, object>
            {
                { "total_fixtures_in_drawing", totalFixtures },
                { "covered_fixtures_count", coveredFixtures },
                { "orphan_fixtures_count", orphanHandles.Count },
                { "coverage_percent", coveragePct },
                { "orphan_counts_by_block", orphanCounts },
                { "orphan_handles", orphanHandles.Take(200).ToList() },
                { "panels_audit", panelGapSummaries },
                { "sequence_gap_notes", gapMsgs }
            };
        }

        private static string BuildMissingSpecsArabicQuestion(List<Dictionary<string, object>> missingList)
        {
            if (missingList == null || missingList.Count == 0) return "";
            List<string> items = new List<string>(missingList.Count);
            for (int i = 0; i < missingList.Count; i++)
            {
                string bn = GetStr(missingList[i], "block_name");
                int cnt = GetInt(missingList[i], "count", 0);
                items.Add(cnt > 0 ? (bn + " (الكمية " + cnt.ToString(CultureInfo.InvariantCulture) + ")") : bn);
            }
            return "تنبيه — قدرات بلوكات غير مسجلة في الـ Legend بدون أي افتراض: [" +
                string.Join("، ", items.ToArray()) +
                "]. من فضلك أدخل قدرة كل بلوك بالوات (Watt) أو VA، أو حدد جدول الـ Legend لاستخراجه وحفظه في قاعدة بيانات المشروع قبل الاعتماد النهائي (أو مرر allow_pending_specs=true للاعتماد المؤقت).";
        }

        private static int FindNextPendingIndex(GuidedInventorySession session, int startFrom)
        {
            if (session == null || session.Queue.Count == 0) return -1;
            for (int i = Math.Max(0, startFrom); i < session.Queue.Count; i++)
            {
                if (session.Queue[i].Status == "pending") return i;
            }
            for (int i = 0; i < Math.Min(startFrom, session.Queue.Count); i++)
            {
                if (session.Queue[i].Status == "pending") return i;
            }
            return -1;
        }

        private static Dictionary<string, object> BuildGuidedStepResponse(
            GuidedInventorySession session,
            string message,
            Dictionary<string, object> extra)
        {
            int approved = 0, skipped = 0, remaining = 0;
            for (int i = 0; i < session.Queue.Count; i++)
            {
                string s = session.Queue[i].Status ?? "pending";
                if (s == "approved") approved++;
                else if (s == "skipped") skipped++;
                else remaining++;
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "session_id", session.SessionId },
                { "active", session.Active },
                { "discipline_mode", session.DisciplineMode },
                { "strict_legend_specs", session.StrictLegendSpecs },
                { "current_index", session.CurrentIndex },
                { "total_count", session.Queue.Count },
                { "approved_count", approved },
                { "skipped_count", skipped },
                { "remaining_count", remaining },
                { "awaiting_engineer_action", session.Active && session.CurrentIndex >= 0 && session.CurrentIndex < session.Queue.Count },
                { "message", message ?? "" }
            };

            if (session.CurrentIndex >= 0 && session.CurrentIndex < session.Queue.Count)
            {
                GuidedInventoryItem cur = session.Queue[session.CurrentIndex];
                res["current_circuit"] = GuidedItemToDict(cur, session.CurrentIndex, session.Queue.Count);
                res["needs_user_specs"] = cur.NeedsUserSpecs;
                res["missing_spec_blocks"] = cur.MissingSpecBlocks ?? new List<Dictionary<string, object>>();
            }
            else
            {
                res["needs_user_specs"] = false;
                res["missing_spec_blocks"] = new List<Dictionary<string, object>>();
            }

            if (extra != null)
            {
                foreach (KeyValuePair<string, object> kv in extra)
                    res[kv.Key] = kv.Value;
            }
            return Ok(res);
        }

        private static Dictionary<string, object> GuidedCircuitInventory(Document doc, Dictionary<string, object> args)
        {
            if (doc == null || doc.Database == null)
                return Err("لا يوجد مخطط مفتوح حالياً في أوتوكاد.");
            if (args == null) args = new Dictionary<string, object>();

            Database db = doc.Database;
            IntPtr dbPtr = db.UnmanagedObject;
            EnsureDocDestroyedHook();

            string rawAction = (GetStr(args, "action") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(rawAction))
            {
                lock (s_lastCoordLock)
                {
                    GuidedInventorySession existing;
                    rawAction = (s_guidedInventoryByDb.TryGetValue(dbPtr, out existing) && existing != null && existing.Active)
                        ? "status"
                        : "start";
                }
            }
            if (rawAction == "ok" || rawAction == "approve" || rawAction == "confirm" || rawAction == "next")
                rawAction = "approve_next";
            else if (rawAction == "add") rawAction = "add_selection";
            else if (rawAction == "remove") rawAction = "remove_selection";
            else if (rawAction == "inspect" || rawAction == "inspect_selected") rawAction = "inspect_selection";
            else if (rawAction == "audit") rawAction = "audit_remaining";
            else if (rawAction == "export") rawAction = "export_merged_panels";
            else if (rawAction == "edit" || rawAction == "edit_circuit" || rawAction == "open" || rawAction == "show")
                rawAction = "recall";

            bool zoom = GetBool(args, "zoom", true);

            // 1. Standalone actions that don't require an active inventory queue
            if (rawAction == "inspect_selection")
            {
                return ExecuteInspectRandomSelection(doc, args, zoom);
            }
            if (rawAction == "recall")
            {
                return ExecuteRecallSavedCircuitForEdit(doc, args, zoom);
            }
            if (rawAction == "audit_remaining")
            {
                Dictionary<string, object> audit = BuildCoverageAndGapAudit(doc, GetBool(args, "highlight", true), zoom);
                audit["message"] = string.Format(
                    CultureInfo.InvariantCulture,
                    "فحص التغطية: تم تغطية {0}/{1} وحدة ({2}%) | متبقي بدون جرد: {3} وحدة.",
                    audit["covered_fixtures_count"],
                    audit["total_fixtures_in_drawing"],
                    audit["coverage_percent"],
                    audit["orphan_fixtures_count"]);
                return Ok(audit);
            }
            if (rawAction == "export_merged_panels")
            {
                Dictionary<string, object> panelArgs = new Dictionary<string, object>(args, StringComparer.OrdinalIgnoreCase);
                panelArgs["source"] = "saved_memory";
                if (!panelArgs.ContainsKey("approved_only")) panelArgs["approved_only"] = true;
                if (!panelArgs.ContainsKey("strict_legend_specs")) panelArgs["strict_legend_specs"] = true;
                if (!panelArgs.ContainsKey("export_excel")) panelArgs["export_excel"] = true;
                return GeneratePanelScheduleAndSld(doc, panelArgs);
            }

            // 2. Start / rebuild queue
            if (rawAction == "start")
            {
                string discMode = (GetStr(args, "discipline") ?? "all").Trim().ToLowerInvariant();
                if (discMode != "lighting" && discMode != "power") discMode = "all";
                string panelFilter = (GetStr(args, "panel_filter") ?? GetStr(args, "panel_name") ?? "").Trim();
                bool strictSpecs = GetBool(args, "strict_legend_specs", true);
                bool includeAlreadyApproved = GetBool(args, "include_already_approved", false);
                bool autoDrawBoard = GetBool(args, "auto_draw_board", true);

                DrawingCircuitScanResult scan;
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Dictionary<string, object> scanArgs = new Dictionary<string, object>(args, StringComparer.OrdinalIgnoreCase);
                    scanArgs["overwrite_locked"] = false;
                    scan = ScanAllCircuitsInternal(doc, db, tr, scanArgs, false);
                    tr.Commit();
                }

                if (!string.IsNullOrEmpty(scan.ErrorMessage)) return Err(scan.ErrorMessage);
                if (scan.Circuits.Count == 0)
                    return Err("لم يتم العثور على لينيات أو مسارات في الرسم الحالي. تأكد من فتح المخطط الصحيح أو استخدم inspect_selection على لينيا محددة يدوياً.");

                lock (s_lastCoordLock)
                {
                    EnsureProjectLegendDbLoadedLocked(doc, db);
                }

                List<GuidedInventoryItem> queue = new List<GuidedInventoryItem>();
                Dictionary<string, int> overallMissingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int fallbackIdx = 0;

                for (int ci = 0; ci < scan.Circuits.Count; ci++)
                {
                    TracedCircuitSummary c = scan.Circuits[ci];
                    string disc = !string.IsNullOrWhiteSpace(c.Discipline)
                        ? c.Discipline.Trim().ToLowerInvariant()
                        : InferCircuitDiscipline(doc, c.CircuitName, c.WireLayer, c.CountsByBlock);

                    if (discMode == "lighting" && disc == "power") continue;
                    if (discMode == "power" && disc == "lighting") continue;

                    string cName = !string.IsNullOrWhiteSpace(c.CircuitName) ? c.CircuitName.Trim() : "";
                    if (string.IsNullOrWhiteSpace(cName))
                    {
                        fallbackIdx++;
                        cName = "CIRC-" + fallbackIdx.ToString(CultureInfo.InvariantCulture);
                    }
                    string pName = ExtractPanelNameFallback(cName, c.PanelName);
                    if (!string.IsNullOrWhiteSpace(panelFilter) &&
                        !string.Equals(panelFilter, "ALL", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(pName, panelFilter, StringComparison.OrdinalIgnoreCase) &&
                        !MatchesPatternOrSubstring(pName, panelFilter))
                        continue;

                    SavedCircuitRecord existingRec;
                    bool alreadyApproved = false;
                    if (TryGetSavedCircuitRecord(doc, cName, out existingRec) && existingRec != null &&
                        (existingRec.Approved || existingRec.UserLocked))
                    {
                        alreadyApproved = true;
                        if (!includeAlreadyApproved) continue;
                    }

                    List<string> wHandles = new List<string>(c.PathCurveIds.Count);
                    for (int pi = 0; pi < c.PathCurveIds.Count; pi++)
                        wHandles.Add(c.PathCurveIds[pi].Handle.ToString());

                    GuidedInventoryItem item = new GuidedInventoryItem
                    {
                        CircuitName = cName,
                        PanelName = pName,
                        Discipline = disc,
                        WireLayer = c.WireLayer ?? "",
                        WireColorKey = c.WireColorKey ?? "",
                        TerminationType = c.TerminationType ?? "open_end",
                        Status = alreadyApproved ? "approved" : "pending",
                        WireHandles = wHandles,
                        MatchedBlockHandles = new List<string>(c.MatchedHandles),
                        CountsByBlock = new Dictionary<string, int>(c.CountsByBlock, StringComparer.OrdinalIgnoreCase),
                        LengthMeters = c.LengthMeters,
                        GuardTriggered = c.GuardTriggered,
                        PanelWays = c.PanelWays != null && c.PanelWays.Count > 0
                            ? new List<string>(c.PanelWays)
                            : (existingRec != null && existingRec.PanelWays != null ? new List<string>(existingRec.PanelWays) : new List<string>()),
                        PanelWaysCount = c.PanelWaysCount > 0 ? c.PanelWaysCount : (existingRec != null ? existingRec.PanelWaysCount : 0),
                        CableDesignation = !string.IsNullOrEmpty(c.CableDesignation) ? c.CableDesignation : (existingRec != null ? (existingRec.CableDesignation ?? "") : ""),
                        CableCoresCount = c.CableCoresCount > 0 ? c.CableCoresCount : (existingRec != null ? existingRec.CableCoresCount : 0),
                        TotalConductorsCount = c.TotalConductorsCount > 0 ? c.TotalConductorsCount : (existingRec != null ? existingRec.TotalConductorsCount : 0),
                        ConduitSpec = !string.IsNullOrEmpty(c.ConduitSpec) ? c.ConduitSpec : (existingRec != null ? (existingRec.ConduitSpec ?? "") : ""),
                        InstallNote = !string.IsNullOrEmpty(c.InstallNote) ? c.InstallNote : (existingRec != null ? (existingRec.InstallNote ?? "") : ""),
                        AnnotatedVdPercent = c.AnnotatedVdPercent.HasValue ? c.AnnotatedVdPercent : (existingRec != null ? existingRec.AnnotatedVdPercent : null),
                        RawCalloutText = existingRec != null ? (existingRec.RawCalloutText ?? "") : ""
                    };
                    if (existingRec != null && existingRec.ExcludedHandles != null)
                    {
                        for (int ei = 0; ei < existingRec.ExcludedHandles.Count; ei++)
                            item.ExcludedHandles.Add(existingRec.ExcludedHandles[ei]);
                    }
                    RecomputeGuidedItemSpecs(doc, item, strictSpecs);
                    if (item.MissingSpecBlocks != null)
                    {
                        for (int mi = 0; mi < item.MissingSpecBlocks.Count; mi++)
                        {
                            string mb = GetStr(item.MissingSpecBlocks[mi], "block_name");
                            int mc = GetInt(item.MissingSpecBlocks[mi], "count", 0);
                            if (!string.IsNullOrWhiteSpace(mb))
                                overallMissingCounts[mb] = overallMissingCounts.ContainsKey(mb) ? overallMissingCounts[mb] + mc : mc;
                        }
                    }
                    queue.Add(item);
                }

                if (queue.Count == 0)
                {
                    string reason = discMode != "all"
                        ? ("لا توجد لينيات متبقية غير معتمدة للتخصص '" + discMode + "'.")
                        : "جميع اللينيات المكتشفة معتمدة مسبقاً أو لا تطابق الفلتر.";
                    return Ok(new Dictionary<string, object>
                    {
                        { "active", false },
                        { "total_count", 0 },
                        { "completed", true },
                        { "message", reason + " يمكنك تشغيل audit_remaining أو export_merged_panels." }
                    });
                }

                bool mixDisciplines = GetBool(args, "mix_disciplines", false);
                queue.Sort(delegate(GuidedInventoryItem a, GuidedInventoryItem b)
                {
                    if (discMode == "all" && !mixDisciplines)
                    {
                        int ra = a.Discipline == "lighting" ? 0 : (a.Discipline == "power" ? 2 : 1);
                        int rb = b.Discipline == "lighting" ? 0 : (b.Discipline == "power" ? 2 : 1);
                        if (ra != rb) return ra.CompareTo(rb);
                    }
                    return CompareCircuitKeysNatural(a.PanelName, a.CircuitName, b.PanelName, b.CircuitName);
                });

                GuidedInventorySession session = new GuidedInventorySession
                {
                    SessionId = "inv_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    DisciplineMode = discMode,
                    PanelFilter = panelFilter,
                    StrictLegendSpecs = strictSpecs,
                    AutoDrawBoard = autoDrawBoard,
                    Active = true,
                    CurrentIndex = 0,
                    Queue = queue,
                    EffectiveUnitsPerM = scan.EffectiveUnitsPerM,
                    MedianFixtureSpan = scan.MedianFixtureSpan,
                    DrawingSpan = scan.DrawingSpan,
                    UpdatedUtc = DateTime.UtcNow
                };

                int firstPending = FindNextPendingIndex(session, 0);
                session.CurrentIndex = firstPending >= 0 ? firstPending : 0;

                lock (s_lastCoordLock)
                {
                    s_guidedInventoryByDb[dbPtr] = session;
                }

                GuidedInventoryItem firstItem = session.Queue[session.CurrentIndex];
                FocusAndHighlightGuidedItem(doc, session, firstItem, session.CurrentIndex, session.Queue.Count, zoom);

                List<object> overallMissing = new List<object>();
                foreach (KeyValuePair<string, int> mkv in overallMissingCounts)
                {
                    overallMissing.Add(new Dictionary<string, object>
                    {
                        { "block_name", mkv.Key },
                        { "total_count", mkv.Value }
                    });
                }

                List<string> queueOrderPreview = new List<string>();
                for (int qi = 0; qi < session.Queue.Count && qi < 40; qi++)
                    queueOrderPreview.Add(session.Queue[qi].CircuitName + " (" + session.Queue[qi].Discipline + ")");

                string msg = string.Format(
                    CultureInfo.InvariantCulture,
                    "بدأ الجرد التفاعلي ({0} لينيا). اللينيا الحالية [{1}/{2}]: {3} (لوحة {4} | {5} | {6} بلوك | {7} م). راجع التحديد ثم أضف/شيل أو أرسل 'اوكي' للاعتماد والانتقال للتالية.",
                    session.Queue.Count,
                    session.CurrentIndex + 1,
                    session.Queue.Count,
                    firstItem.CircuitName,
                    firstItem.PanelName,
                    firstItem.Discipline,
                    CountTotalMatchedBlocks(firstItem.CountsByBlock),
                    Math.Round(firstItem.LengthMeters, 2).ToString("0.##", CultureInfo.InvariantCulture));
                if (firstItem.NeedsUserSpecs)
                    msg += "\n" + BuildMissingSpecsArabicQuestion(firstItem.MissingSpecBlocks);

                return BuildGuidedStepResponse(session, msg, new Dictionary<string, object>
                {
                    { "queue_preview", queueOrderPreview },
                    { "missing_legend_blocks_overall", overallMissing },
                    { "legend_ready", overallMissing.Count == 0 }
                });
            }

            // 3. All remaining actions require an existing session
            GuidedInventorySession curSession;
            lock (s_lastCoordLock)
            {
                s_guidedInventoryByDb.TryGetValue(dbPtr, out curSession);
            }
            if (curSession == null || curSession.Queue.Count == 0)
                return Err("لا توجد جلسة جرد نشطة حالياً. ابدأ بـ action: 'start' أو اكتب 'ابدأ الجرد'.");

            if (rawAction == "status")
            {
                if (curSession.Active && curSession.CurrentIndex >= 0 && curSession.CurrentIndex < curSession.Queue.Count)
                {
                    GuidedInventoryItem item = curSession.Queue[curSession.CurrentIndex];
                    RecomputeGuidedItemSpecs(doc, item, curSession.StrictLegendSpecs);
                    string sMsg = string.Format(
                        CultureInfo.InvariantCulture,
                        "اللينيا الحالية [{0}/{1}]: {2} (لوحة {3}) — {4} بلوك | {5} م.",
                        curSession.CurrentIndex + 1, curSession.Queue.Count,
                        item.CircuitName, item.PanelName,
                        CountTotalMatchedBlocks(item.CountsByBlock),
                        Math.Round(item.LengthMeters, 2).ToString("0.##", CultureInfo.InvariantCulture));
                    return BuildGuidedStepResponse(curSession, sMsg, null);
                }
                return BuildGuidedStepResponse(curSession, "جلسة الجرد متوقفة أو مكتملة.", null);
            }

            if (rawAction == "stop")
            {
                curSession.Active = false;
                curSession.UpdatedUtc = DateTime.UtcNow;
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ClearGuidedHighlightLayer(db, tr);
                    tr.Commit();
                }
                try { doc.Editor.UpdateScreen(); } catch { }
                return BuildGuidedStepResponse(curSession, "تم إيقاف الجرد التفاعلي مع الاحتفاظ بجميع اللينيات المعتمدة محفوظة في قاعدة بيانات المشروع.", null);
            }

            if (rawAction == "jump")
            {
                int targetIdx = -1;
                string reqCirc = (GetStr(args, "circuit_name") ?? "").Trim();
                int reqIdx = GetInt(args, "index", 0);
                if (!string.IsNullOrWhiteSpace(reqCirc))
                {
                    string nk = NormalizeCircuitKey(reqCirc);
                    for (int i = 0; i < curSession.Queue.Count; i++)
                    {
                        if (string.Equals(NormalizeCircuitKey(curSession.Queue[i].CircuitName), nk, StringComparison.OrdinalIgnoreCase))
                        {
                            targetIdx = i;
                            break;
                        }
                    }
                }
                else if (reqIdx >= 1 && reqIdx <= curSession.Queue.Count)
                {
                    targetIdx = reqIdx - 1;
                }
                if (targetIdx < 0)
                    return Err("تعذر العثور على اللينيا المطلوبة في قائمة الجرد.");

                curSession.Active = true;
                curSession.CurrentIndex = targetIdx;
                GuidedInventoryItem targetItem = curSession.Queue[targetIdx];
                RecomputeGuidedItemSpecs(doc, targetItem, curSession.StrictLegendSpecs);
                FocusAndHighlightGuidedItem(doc, curSession, targetItem, targetIdx, curSession.Queue.Count, zoom);
                return BuildGuidedStepResponse(
                    curSession,
                    string.Format(CultureInfo.InvariantCulture, "تم الانتقال إلى اللينيا [{0}/{1}]: {2}.", targetIdx + 1, curSession.Queue.Count, targetItem.CircuitName),
                    null);
            }

            if (!curSession.Active || curSession.CurrentIndex < 0 || curSession.CurrentIndex >= curSession.Queue.Count)
                return Err("جلسة الجرد منتهية أو متوقفة. استخدم start أو jump للعودة.");

            GuidedInventoryItem curItem = curSession.Queue[curSession.CurrentIndex];

            if (rawAction == "rename")
            {
                string newCirc = GetStr(args, "circuit_name");
                string newPanel = GetStr(args, "panel_name");
                string newDisc = GetStr(args, "discipline");
                string selCalloutTxt = "";
                if (string.IsNullOrWhiteSpace(newCirc))
                {
                    List<string> explicitHandles = GetStringList(args, "handles");
                    List<string> pickedHandles = explicitHandles.Count > 0 ? explicitHandles : GetSelectedHandleStrings(doc);
                    if (pickedHandles.Count > 0)
                    {
                        using (DocumentLock dl = doc.LockDocument())
                        using (Transaction trTxt = db.TransactionManager.StartTransaction())
                        {
                            List<Point2d> dummyTips;
                            selCalloutTxt = ExtractSelectedCalloutText(trTxt, db, pickedHandles, out dummyTips);
                            trTxt.Commit();
                        }
                    }
                }
                string calloutSource = !string.IsNullOrWhiteSpace(selCalloutTxt) ? selCalloutTxt : (newCirc ?? "");
                if (!string.IsNullOrWhiteSpace(calloutSource))
                {
                    ParsedCircuitCallout parsed = ParseCircuitCalloutText(calloutSource);
                    if (!string.IsNullOrWhiteSpace(parsed.CircuitName))
                        curItem.CircuitName = parsed.CircuitName;
                    else if (!string.IsNullOrWhiteSpace(newCirc))
                        curItem.CircuitName = newCirc.Trim();

                    if (!string.IsNullOrWhiteSpace(parsed.PanelName))
                        curItem.PanelName = parsed.PanelName;
                    if (parsed.PanelWays != null && parsed.PanelWays.Count > 0)
                    {
                        curItem.PanelWays = new List<string>(parsed.PanelWays);
                        curItem.PanelWaysCount = parsed.PanelWaysCount;
                    }
                    if (!string.IsNullOrWhiteSpace(parsed.CableSpec))
                    {
                        curItem.CableDesignation = parsed.CableSpec;
                        curItem.CableCoresCount = parsed.CableCoresCount;
                        curItem.TotalConductorsCount = parsed.TotalConductorsCount;
                    }
                    if (!string.IsNullOrWhiteSpace(parsed.ConduitSpec))
                        curItem.ConduitSpec = parsed.ConduitSpec;
                    if (!string.IsNullOrWhiteSpace(parsed.InstallNote))
                        curItem.InstallNote = parsed.InstallNote;
                    if (parsed.AnnotatedVdPercent.HasValue)
                        curItem.AnnotatedVdPercent = parsed.AnnotatedVdPercent;
                    if (!string.IsNullOrWhiteSpace(parsed.RawText))
                        curItem.RawCalloutText = parsed.RawText;
                }
                if (!string.IsNullOrWhiteSpace(newPanel)) curItem.PanelName = newPanel.Trim();
                else curItem.PanelName = ExtractPanelNameFallback(curItem.CircuitName, curItem.PanelName);
                if (!string.IsNullOrWhiteSpace(newDisc))
                {
                    string dl = newDisc.Trim().ToLowerInvariant();
                    if (dl == "lighting" || dl == "power" || dl == "general") curItem.Discipline = dl;
                }
                curSession.UpdatedUtc = DateTime.UtcNow;
                FocusAndHighlightGuidedItem(doc, curSession, curItem, curSession.CurrentIndex, curSession.Queue.Count, false);
                return BuildGuidedStepResponse(curSession, "تم تحديث بيانات اللينيا إلى: " + curItem.CircuitName + " (" + curItem.PanelName + " | " + curItem.Discipline + ").", null);
            }

            if (rawAction == "add_selection" || rawAction == "remove_selection" || rawAction == "refresh")
            {
                List<string> explicitHandles = GetStringList(args, "handles");
                List<string> pickedHandles = explicitHandles.Count > 0 ? explicitHandles : GetSelectedHandleStrings(doc);

                if (rawAction == "add_selection")
                {
                    if (pickedHandles.Count == 0)
                        return Err("حدد الخطوط أو البلوكات المراد إضافتها للينيا داخل أوتوكاد أولاً (أو مرر handles)، ثم أعد المحاولة.");

                    int addedWires = 0;
                    int addedBlocks = 0;
                    using (DocumentLock docLock = doc.LockDocument())
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        for (int i = 0; i < pickedHandles.Count; i++)
                        {
                            string h = pickedHandles[i];
                            ObjectId oid = ResolveHandle(db, h);
                            if (oid.IsNull || oid.IsErased) continue;
                            Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased || IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                            string realH = ent.Handle.ToString();
                            curItem.ExcludedHandles.Remove(realH);
                            if (ent is Curve)
                            {
                                if (!curItem.WireHandles.Contains(realH))
                                {
                                    curItem.WireHandles.Add(realH);
                                    addedWires++;
                                }
                            }
                            else if (ent is BlockReference)
                            {
                                if (!curItem.ExplicitBlockHandles.Contains(realH))
                                {
                                    curItem.ExplicitBlockHandles.Add(realH);
                                    addedBlocks++;
                                }
                            }
                        }
                        tr.Commit();
                    }

                    ReevaluateGuidedItemFromGeometry(doc, curSession, curItem, true);
                    FocusAndHighlightGuidedItem(doc, curSession, curItem, curSession.CurrentIndex, curSession.Queue.Count, zoom);
                    string addMsg = string.Format(
                        CultureInfo.InvariantCulture,
                        "تمت إضافة {0} خط و{1} بلوك ثم استكمال المسار تلقائياً للينيا {2}: إجمالي {3} بلوك ({4}) بطول {5} م.",
                        addedWires, addedBlocks, curItem.CircuitName,
                        CountTotalMatchedBlocks(curItem.CountsByBlock),
                        FormatCountsSummary(curItem.CountsByBlock),
                        Math.Round(curItem.LengthMeters, 2).ToString("0.##", CultureInfo.InvariantCulture));
                    if (curItem.NeedsUserSpecs) addMsg += "\n" + BuildMissingSpecsArabicQuestion(curItem.MissingSpecBlocks);
                    return BuildGuidedStepResponse(curSession, addMsg, new Dictionary<string, object> { { "added_wires", addedWires }, { "added_blocks", addedBlocks } });
                }

                if (rawAction == "remove_selection")
                {
                    if (pickedHandles.Count == 0)
                        return Err("اضغط Esc ثم حدد العناصر الغلط فقط داخل أوتوكاد واضغط 'شيل' (Remove).");

                    HashSet<string> curUnion = new HashSet<string>(curItem.WireHandles, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < curItem.MatchedBlockHandles.Count; i++) curUnion.Add(curItem.MatchedBlockHandles[i]);
                    HashSet<string> pickedSet = new HashSet<string>(pickedHandles, StringComparer.OrdinalIgnoreCase);
                    if (curUnion.Count > 1 && pickedSet.SetEquals(curUnion))
                        return Err("المحدد حالياً هو كامل اللينيا دون تعديل. اضغط Esc أولاً ثم حدد العناصر الغلط فقط واضغط 'شيل'.");

                    int removedCount = 0;
                    for (int i = 0; i < pickedHandles.Count; i++)
                    {
                        string h = pickedHandles[i];
                        bool hit = false;
                        if (curItem.WireHandles.RemoveAll(delegate(string s) { return string.Equals(s, h, StringComparison.OrdinalIgnoreCase); }) > 0) hit = true;
                        if (curItem.ExplicitBlockHandles.RemoveAll(delegate(string s) { return string.Equals(s, h, StringComparison.OrdinalIgnoreCase); }) > 0) hit = true;
                        if (curItem.MatchedBlockHandles.RemoveAll(delegate(string s) { return string.Equals(s, h, StringComparison.OrdinalIgnoreCase); }) > 0) hit = true;
                        if (curItem.ExcludedHandles.Add(h)) hit = true;
                        if (hit) removedCount++;
                    }

                    ReevaluateGuidedItemFromGeometry(doc, curSession, curItem, false);
                    FocusAndHighlightGuidedItem(doc, curSession, curItem, curSession.CurrentIndex, curSession.Queue.Count, zoom);
                    string remMsg = string.Format(
                        CultureInfo.InvariantCulture,
                        "تم استبعاد {0} عنصر من اللينيا {1} وحفظ الاستبعاد بحيث لا يرجع مع التحديث: المتبقي {2} بلوك ({3}) بطول {4} م.",
                        removedCount, curItem.CircuitName,
                        CountTotalMatchedBlocks(curItem.CountsByBlock),
                        FormatCountsSummary(curItem.CountsByBlock),
                        Math.Round(curItem.LengthMeters, 2).ToString("0.##", CultureInfo.InvariantCulture));
                    return BuildGuidedStepResponse(curSession, remMsg, new Dictionary<string, object> { { "removed_count", removedCount } });
                }

                // rawAction == "refresh"
                ReevaluateGuidedItemFromGeometry(doc, curSession, curItem, true);
                FocusAndHighlightGuidedItem(doc, curSession, curItem, curSession.CurrentIndex, curSession.Queue.Count, zoom);
                string refMsg = string.Format(
                    CultureInfo.InvariantCulture,
                    "تم عمل ريفريش وإعادة مسح اللينيا {0} (مع احترام العناصر المستبعدة): {1} بلوك ({2}) | طول {3} م.",
                    curItem.CircuitName,
                    CountTotalMatchedBlocks(curItem.CountsByBlock),
                    FormatCountsSummary(curItem.CountsByBlock),
                    Math.Round(curItem.LengthMeters, 2).ToString("0.##", CultureInfo.InvariantCulture));
                if (curItem.NeedsUserSpecs) refMsg += "\n" + BuildMissingSpecsArabicQuestion(curItem.MissingSpecBlocks);
                return BuildGuidedStepResponse(curSession, refMsg, null);
            }

            if (rawAction == "skip")
            {
                curItem.Status = "skipped";
                curSession.UpdatedUtc = DateTime.UtcNow;
                int nextIdx = FindNextPendingIndex(curSession, curSession.CurrentIndex + 1);
                if (nextIdx >= 0)
                {
                    curSession.CurrentIndex = nextIdx;
                    GuidedInventoryItem nxt = curSession.Queue[nextIdx];
                    FocusAndHighlightGuidedItem(doc, curSession, nxt, nextIdx, curSession.Queue.Count, zoom);
                    return BuildGuidedStepResponse(
                        curSession,
                        string.Format(CultureInfo.InvariantCulture, "تم تخطي {0} والانتقال إلى [{1}/{2}]: {3}.", curItem.CircuitName, nextIdx + 1, curSession.Queue.Count, nxt.CircuitName),
                        null);
                }
                curSession.Active = false;
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ClearGuidedHighlightLayer(db, tr);
                    tr.Commit();
                }
                return BuildGuidedStepResponse(curSession, "انتهت قائمة الجرد (تم اعتماد أو تخطي جميع اللينيات).", new Dictionary<string, object> { { "completed", true } });
            }

            if (rawAction == "approve_next")
            {
                RecomputeGuidedItemSpecs(doc, curItem, curSession.StrictLegendSpecs);
                bool allowPending = GetBool(args, "allow_pending_specs", false);
                if (curSession.StrictLegendSpecs && curItem.NeedsUserSpecs && !allowPending)
                {
                    string blockMsg = BuildMissingSpecsArabicQuestion(curItem.MissingSpecBlocks);
                    return BuildGuidedStepResponse(curSession, blockMsg, new Dictionary<string, object>
                    {
                        { "approved", false },
                        { "blocked_for_missing_specs", true },
                        { "completed", false }
                    });
                }

                curItem.Status = "approved";
                curSession.UpdatedUtc = DateTime.UtcNow;

                SavedCircuitRecord rec = new SavedCircuitRecord
                {
                    CircuitName = curItem.CircuitName,
                    PanelName = curItem.PanelName,
                    Discipline = curItem.Discipline,
                    SourceDwgPath = NormalizeDwgIdentity(doc),
                    Approved = true,
                    TerminationType = curItem.TerminationType,
                    WireLayer = curItem.WireLayer,
                    WireColorKey = curItem.WireColorKey,
                    PathHandles = new List<string>(curItem.WireHandles),
                    MatchedBlockHandles = new List<string>(curItem.MatchedBlockHandles),
                    MatchedBlocksCount = CountTotalMatchedBlocks(curItem.CountsByBlock),
                    ExcludedHandles = new List<string>(curItem.ExcludedHandles),
                    LengthMeters = curItem.LengthMeters,
                    CountsByBlock = new Dictionary<string, int>(curItem.CountsByBlock, StringComparer.OrdinalIgnoreCase),
                    UserLocked = true,
                    LoadKw = curItem.LoadKw,
                    LoadKva = curItem.LoadKva,
                    CableDesignation = curItem.CableDesignation ?? "",
                    VoltageDropPercent = curItem.AnnotatedVdPercent ?? 0.0,
                    LoadSource = curItem.LoadSource,
                    PanelWays = curItem.PanelWays != null ? new List<string>(curItem.PanelWays) : new List<string>(),
                    PanelWaysCount = curItem.PanelWaysCount,
                    CableCoresCount = curItem.CableCoresCount,
                    TotalConductorsCount = curItem.TotalConductorsCount,
                    ConduitSpec = curItem.ConduitSpec ?? "",
                    InstallNote = curItem.InstallNote ?? "",
                    AnnotatedVdPercent = curItem.AnnotatedVdPercent,
                    RawCalloutText = curItem.RawCalloutText ?? ""
                };
                using (DocumentLock dl = doc.LockDocument())
                using (Transaction xtr = db.TransactionManager.StartTransaction())
                {
                    StampCircuitXData(
                        db, xtr,
                        curItem.WireHandles, curItem.MatchedBlockHandles, curItem.ExcludedHandles,
                        curItem.CircuitName, curItem.PanelName, curItem.Discipline);
                    xtr.Commit();
                }
                SaveCircuitRecordToMemory(doc, rec, true, true);

                if (curSession.AutoDrawBoard)
                {
                    RedrawPanelStackedBoardOrdered(doc, curItem.PanelName);
                }

                int nextIdx = FindNextPendingIndex(curSession, curSession.CurrentIndex + 1);
                if (nextIdx >= 0)
                {
                    curSession.CurrentIndex = nextIdx;
                    GuidedInventoryItem nxt = curSession.Queue[nextIdx];
                    RecomputeGuidedItemSpecs(doc, nxt, curSession.StrictLegendSpecs);
                    FocusAndHighlightGuidedItem(doc, curSession, nxt, nextIdx, curSession.Queue.Count, zoom);
                    string okMsg = string.Format(
                        CultureInfo.InvariantCulture,
                        "✅ تم اعتماد وحفظ {0} ({1}). التالي [{2}/{3}]: {4} (لوحة {5} | {6} بلوك | {7} م).",
                        curItem.CircuitName, curItem.PanelName,
                        nextIdx + 1, curSession.Queue.Count,
                        nxt.CircuitName, nxt.PanelName,
                        CountTotalMatchedBlocks(nxt.CountsByBlock),
                        Math.Round(nxt.LengthMeters, 2).ToString("0.##", CultureInfo.InvariantCulture));
                    if (nxt.NeedsUserSpecs) okMsg += "\n" + BuildMissingSpecsArabicQuestion(nxt.MissingSpecBlocks);

                    return BuildGuidedStepResponse(curSession, okMsg, new Dictionary<string, object>
                    {
                        { "approved", true },
                        { "approved_circuit", curItem.CircuitName },
                        { "completed", false }
                    });
                }
                else
                {
                    curSession.Active = false;
                    using (DocumentLock docLock = doc.LockDocument())
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        ClearGuidedHighlightLayer(db, tr);
                        tr.Commit();
                    }
                    Dictionary<string, object> audit = BuildCoverageAndGapAudit(doc, false, false);
                    string doneMsg = string.Format(
                        CultureInfo.InvariantCulture,
                        "🎉 تم اعتماد آخر لينيا ({0}) واكتمل الجرد التفاعلي للمخطط! تغطية الكشافات: {1}% ({2}/{3}).",
                        curItem.CircuitName,
                        audit["coverage_percent"], audit["covered_fixtures_count"], audit["total_fixtures_in_drawing"]);
                    return BuildGuidedStepResponse(curSession, doneMsg, new Dictionary<string, object>
                    {
                        { "approved", true },
                        { "approved_circuit", curItem.CircuitName },
                        { "completed", true },
                        { "coverage_audit", audit }
                    });
                }
            }

            return Err("الأمر غير معروف داخل الجرد التفاعلي: " + rawAction);
        }

        private static Dictionary<string, object> ExecuteInspectRandomSelection(
            Document doc,
            Dictionary<string, object> args,
            bool zoom)
        {
            Database db = doc.Database;
            List<string> explicitHandles = GetStringList(args, "handles");
            List<string> pickedHandles = explicitHandles.Count > 0 ? explicitHandles : GetSelectedHandleStrings(doc);
            if (pickedHandles.Count == 0)
                return Err("حدد اللينيا أو الخطوط/البلوكات في أوتوكاد أولاً (أو مرر handles) لفحص بياناتها.");

            bool followConnected = GetBool(args, "follow_connected", true);
            bool strictLegendSpecs = GetBool(args, "strict_legend_specs", true);
            bool saveToPanel = GetBool(args, "save_to_panel", false);
            bool allowPending = GetBool(args, "allow_pending_specs", false);
            string overrideCirc = GetStr(args, "circuit_name");
            string overridePanel = GetStr(args, "panel_name");
            string overrideDisc = GetStr(args, "discipline");

            double unitsPerM = 1000.0;
            double dwgSpan = 0.0;
            double medSpan = 0.0;
            GuidedInventoryItem tempItem = new GuidedInventoryItem();

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Dictionary<string, bool> hiddenFlags = BuildLayerHiddenMap(db, tr);
                Extents3d? mainExt;
                ComputeDrawingFixtureMetrics(db, tr, true, hiddenFlags, out dwgSpan, out medSpan, out mainExt);
                Dictionary<string, object> uInfo = GetUnitsInfo(db);
                double rawU = Convert.ToDouble(uInfo["units_per_m"], CultureInfo.InvariantCulture);
                bool scInf;
                string scWarn;
                unitsPerM = ResolveEffectiveUnitsPerMeter(db, rawU, dwgSpan, medSpan, out scInf, out scWarn);

                for (int i = 0; i < pickedHandles.Count; i++)
                {
                    ObjectId oid = ResolveHandle(db, pickedHandles[i]);
                    if (oid.IsNull || oid.IsErased) continue;
                    Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased || IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                    string hStr = ent.Handle.ToString();
                    if (ent is Curve) tempItem.WireHandles.Add(hStr);
                    else if (ent is BlockReference) tempItem.ExplicitBlockHandles.Add(hStr);
                }
                tr.Commit();
            }

            if (tempItem.WireHandles.Count == 0 && tempItem.ExplicitBlockHandles.Count == 0)
                return Err("العناصر المحددة لا تحتوي على خطوط أو بلوكات صالحة (تأكد من تحديد اللينيا وليس جداول الذكاء الصناعي).");

            GuidedInventorySession scratchSession = new GuidedInventorySession
            {
                StrictLegendSpecs = strictLegendSpecs,
                EffectiveUnitsPerM = unitsPerM,
                MedianFixtureSpan = medSpan,
                DrawingSpan = dwgSpan
            };

            if (!string.IsNullOrWhiteSpace(overrideCirc)) tempItem.CircuitName = overrideCirc.Trim();
            if (!string.IsNullOrWhiteSpace(overridePanel)) tempItem.PanelName = overridePanel.Trim();
            if (!string.IsNullOrWhiteSpace(overrideDisc)) tempItem.Discipline = overrideDisc.Trim().ToLowerInvariant();

            ReevaluateGuidedItemFromGeometry(doc, scratchSession, tempItem, followConnected);
            if (string.IsNullOrWhiteSpace(tempItem.CircuitName)) tempItem.CircuitName = "INSPECTED";

            FocusAndHighlightGuidedItem(doc, scratchSession, tempItem, 0, 1, zoom);

            bool saved = false;
            bool blockedSave = false;
            if (saveToPanel)
            {
                if (strictLegendSpecs && tempItem.NeedsUserSpecs && !allowPending)
                {
                    blockedSave = true;
                }
                else
                {
                    SavedCircuitRecord rec = new SavedCircuitRecord
                    {
                        CircuitName = tempItem.CircuitName,
                        PanelName = tempItem.PanelName,
                        Discipline = tempItem.Discipline,
                        SourceDwgPath = NormalizeDwgIdentity(doc),
                        Approved = true,
                        TerminationType = tempItem.TerminationType,
                        WireLayer = tempItem.WireLayer,
                        WireColorKey = tempItem.WireColorKey,
                        PathHandles = new List<string>(tempItem.WireHandles),
                        MatchedBlockHandles = new List<string>(tempItem.MatchedBlockHandles),
                        MatchedBlocksCount = CountTotalMatchedBlocks(tempItem.CountsByBlock),
                        LengthMeters = tempItem.LengthMeters,
                        CountsByBlock = new Dictionary<string, int>(tempItem.CountsByBlock, StringComparer.OrdinalIgnoreCase),
                        UserLocked = true,
                        LoadKw = tempItem.LoadKw,
                        LoadKva = tempItem.LoadKva,
                        CableDesignation = tempItem.CableDesignation ?? "",
                        VoltageDropPercent = tempItem.AnnotatedVdPercent ?? 0.0,
                        LoadSource = tempItem.LoadSource,
                        PanelWays = tempItem.PanelWays != null ? new List<string>(tempItem.PanelWays) : new List<string>(),
                        PanelWaysCount = tempItem.PanelWaysCount,
                        CableCoresCount = tempItem.CableCoresCount,
                        TotalConductorsCount = tempItem.TotalConductorsCount,
                        ConduitSpec = tempItem.ConduitSpec ?? "",
                        InstallNote = tempItem.InstallNote ?? "",
                        AnnotatedVdPercent = tempItem.AnnotatedVdPercent,
                        RawCalloutText = tempItem.RawCalloutText ?? ""
                    };
                    using (DocumentLock dl = doc.LockDocument())
                    using (Transaction xtr = db.TransactionManager.StartTransaction())
                    {
                        StampCircuitXData(
                            db, xtr,
                            tempItem.WireHandles, tempItem.MatchedBlockHandles, tempItem.ExcludedHandles,
                            tempItem.CircuitName, tempItem.PanelName, tempItem.Discipline);
                        xtr.Commit();
                    }
                    SaveCircuitRecordToMemory(doc, rec, true, true);
                    RedrawPanelStackedBoardOrdered(doc, tempItem.PanelName);
                    saved = true;
                }
            }

            Dictionary<string, object> res = GuidedItemToDict(tempItem, 0, 1);
            res["saved_to_panel"] = saved;
            res["blocked_for_missing_specs"] = blockedSave;
            string msg = string.Format(
                CultureInfo.InvariantCulture,
                "بيانات اللينيا المختارة ({0} | لوحة {1} | {2}): {3} بلوك ({4}) | طول السلك {5} م | الحمل {6} kW / {7} kVA.",
                tempItem.CircuitName, tempItem.PanelName, tempItem.Discipline,
                CountTotalMatchedBlocks(tempItem.CountsByBlock),
                FormatCountsSummary(tempItem.CountsByBlock),
                Math.Round(tempItem.LengthMeters, 2).ToString("0.##", CultureInfo.InvariantCulture),
                Math.Round(tempItem.LoadKw, 3).ToString("0.###", CultureInfo.InvariantCulture),
                Math.Round(tempItem.LoadKva, 3).ToString("0.###", CultureInfo.InvariantCulture));
            if (tempItem.NeedsUserSpecs) msg += "\n" + BuildMissingSpecsArabicQuestion(tempItem.MissingSpecBlocks);
            res["message"] = msg;
            return Ok(res);
        }

        private static Dictionary<string, object> ExecuteRecallSavedCircuitForEdit(
            Document doc,
            Dictionary<string, object> args,
            bool zoom)
        {
            Database db = doc.Database;
            IntPtr dbPtr = db.UnmanagedObject;
            string reqName = (GetStr(args, "circuit_name") ?? GetStr(args, "circuit") ?? GetStr(args, "name") ?? "").Trim();
            string reqPanel = (GetStr(args, "panel_name") ?? "").Trim();
            bool strictSpecs = GetBool(args, "strict_legend_specs", true);
            bool autoDrawBoard = GetBool(args, "auto_draw_board", true);

            // Try extracting circuit name from selected XData-tagged entity if omitted
            if (string.IsNullOrWhiteSpace(reqName))
            {
                List<string> selHandles = GetSelectedHandleStrings(doc);
                if (selHandles.Count > 0)
                {
                    using (DocumentLock dl = doc.LockDocument())
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        for (int i = 0; i < selHandles.Count; i++)
                        {
                            ObjectId oid = ResolveHandle(db, selHandles[i]);
                            if (oid.IsNull || oid.IsErased) continue;
                            Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                            string tk, tc, tp, td;
                            if (TryParseCircuitXDataTag(ReadCircuitXDataTag(ent), out tk, out tc, out tp, out td))
                            {
                                reqName = tc;
                                if (string.IsNullOrWhiteSpace(reqPanel)) reqPanel = tp;
                                break;
                            }
                        }
                        tr.Commit();
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(reqName))
                reqName = GetLastActiveCircuitName(doc);
            if (string.IsNullOrWhiteSpace(reqName))
                return Err("حدد اسم اللينيا المراد استدعاؤها (مثلاً circuit_name: 'DB-1/1') أو اختر جزءاً منها في الرسم.");

            SavedCircuitRecord rec;
            TryGetSavedCircuitRecord(doc, reqName, out rec);
            if (rec == null)
            {
                string targetNorm = NormalizeCircuitKey(reqName);
                List<SavedCircuitRecord> all = GetMergedSavedCircuitRecords(doc, false);
                for (int i = 0; i < all.Count; i++)
                {
                    SavedCircuitRecord cand = all[i];
                    if (cand == null || !IsRecordFromCurrentDrawing(doc, cand)) continue;
                    if (string.Equals(NormalizeCircuitKey(cand.CircuitName), targetNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        rec = cand;
                        break;
                    }
                }
            }

            List<string> liveWires = new List<string>();
            List<string> liveBlocks = new List<string>();
            HashSet<string> excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rec != null && rec.ExcludedHandles != null)
            {
                for (int i = 0; i < rec.ExcludedHandles.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(rec.ExcludedHandles[i]))
                        excludedSet.Add(rec.ExcludedHandles[i].Trim());
                }
            }

            bool recoveredViaXData = false;
            using (DocumentLock dl = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (rec != null && rec.PathHandles != null)
                {
                    for (int i = 0; i < rec.PathHandles.Count; i++)
                    {
                        ObjectId oid = ResolveHandle(db, rec.PathHandles[i]);
                        if (oid.IsNull || oid.IsErased) continue;
                        Curve cv = tr.GetObject(oid, OpenMode.ForRead) as Curve;
                        if (cv == null || cv.IsErased) continue;
                        string h = cv.Handle.ToString();
                        if (!excludedSet.Contains(h) && !liveWires.Contains(h)) liveWires.Add(h);
                    }
                }
                if (rec != null && rec.MatchedBlockHandles != null)
                {
                    for (int i = 0; i < rec.MatchedBlockHandles.Count; i++)
                    {
                        ObjectId oid = ResolveHandle(db, rec.MatchedBlockHandles[i]);
                        if (oid.IsNull || oid.IsErased) continue;
                        BlockReference br = tr.GetObject(oid, OpenMode.ForRead) as BlockReference;
                        if (br == null || br.IsErased) continue;
                        string h = br.Handle.ToString();
                        if (!excludedSet.Contains(h) && !liveBlocks.Contains(h)) liveBlocks.Add(h);
                    }
                }

                if (liveWires.Count == 0 && liveBlocks.Count == 0)
                {
                    List<string> xdWires, xdBlocks;
                    RecoverCircuitHandlesFromXData(db, tr, rec != null ? rec.CircuitName : reqName, excludedSet, out xdWires, out xdBlocks);
                    if (xdWires.Count > 0 || xdBlocks.Count > 0)
                    {
                        liveWires = xdWires;
                        liveBlocks = xdBlocks;
                        recoveredViaXData = true;
                    }
                }
                tr.Commit();
            }

            if (rec == null && liveWires.Count == 0 && liveBlocks.Count == 0)
                return Err("لم يتم العثور على اللينيا '" + reqName + "' لا في قاعدة بيانات المشروع ولا بوسم XData في الرسم الحالي.");

            string finalCirc = rec != null && !string.IsNullOrWhiteSpace(rec.CircuitName) ? rec.CircuitName : reqName;
            string finalPanel = !string.IsNullOrWhiteSpace(reqPanel)
                ? reqPanel
                : ExtractPanelNameFallback(finalCirc, rec != null ? rec.PanelName : "");
            string finalDisc = rec != null && !string.IsNullOrWhiteSpace(rec.Discipline)
                ? rec.Discipline
                : InferCircuitDiscipline(doc, finalCirc, rec != null ? rec.WireLayer : "", rec != null ? rec.CountsByBlock : null);

            double unitsPerM, medSpan, dwgSpan;
            using (DocumentLock dl = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Dictionary<string, bool> hMap = BuildLayerHiddenMap(db, tr);
                Extents3d? mExt;
                ComputeDrawingFixtureMetrics(db, tr, true, hMap, out dwgSpan, out medSpan, out mExt);
                double rawU = Convert.ToDouble(GetUnitsInfo(db)["units_per_m"], CultureInfo.InvariantCulture);
                bool scInf;
                string scWarn;
                unitsPerM = ResolveEffectiveUnitsPerMeter(db, rawU, dwgSpan, medSpan, out scInf, out scWarn);
                tr.Commit();
            }

            GuidedInventorySession curSession;
            lock (s_lastCoordLock)
            {
                s_guidedInventoryByDb.TryGetValue(dbPtr, out curSession);
                if (curSession == null)
                {
                    curSession = new GuidedInventorySession
                    {
                        SessionId = "inv_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        DisciplineMode = "all",
                        StrictLegendSpecs = strictSpecs,
                        AutoDrawBoard = autoDrawBoard,
                        EffectiveUnitsPerM = unitsPerM,
                        MedianFixtureSpan = medSpan,
                        DrawingSpan = dwgSpan
                    };
                    s_guidedInventoryByDb[dbPtr] = curSession;
                }
            }

            GuidedInventoryItem item = new GuidedInventoryItem
            {
                CircuitName = finalCirc,
                PanelName = finalPanel,
                Discipline = finalDisc,
                WireLayer = rec != null ? (rec.WireLayer ?? "") : "",
                WireColorKey = rec != null ? (rec.WireColorKey ?? "") : "",
                TerminationType = rec != null ? (rec.TerminationType ?? "open_end") : "open_end",
                Status = "pending",
                WireHandles = liveWires,
                ExplicitBlockHandles = new List<string>(liveBlocks),
                LengthMeters = rec != null ? rec.LengthMeters : 0.0
            };
            foreach (string eh in excludedSet) item.ExcludedHandles.Add(eh);

            ReevaluateGuidedItemFromGeometry(doc, curSession, item, liveWires.Count > 0 && recoveredViaXData);
            RecomputeGuidedItemSpecs(doc, item, curSession.StrictLegendSpecs);

            int existingIdx = -1;
            string normFinal = NormalizeCircuitKey(finalCirc);
            for (int i = 0; i < curSession.Queue.Count; i++)
            {
                if (string.Equals(NormalizeCircuitKey(curSession.Queue[i].CircuitName), normFinal, StringComparison.OrdinalIgnoreCase))
                {
                    existingIdx = i;
                    break;
                }
            }
            if (existingIdx >= 0) curSession.Queue[existingIdx] = item;
            else
            {
                curSession.Queue.Add(item);
                existingIdx = curSession.Queue.Count - 1;
            }
            curSession.Active = true;
            curSession.CurrentIndex = existingIdx;
            curSession.UpdatedUtc = DateTime.UtcNow;

            FocusAndHighlightGuidedItem(doc, curSession, item, curSession.CurrentIndex, curSession.Queue.Count, zoom);

            string recallMsg = string.Format(
                CultureInfo.InvariantCulture,
                "تم استدعاء اللينيا {0} (لوحة {1} | {2} بلوك | {3} م){4}. يمكنك التعديل بـ add_selection / remove_selection ثم 'اوكي' لحفظ التعديل.",
                item.CircuitName, item.PanelName,
                CountTotalMatchedBlocks(item.CountsByBlock),
                Math.Round(item.LengthMeters, 2).ToString("0.##", CultureInfo.InvariantCulture),
                recoveredViaXData ? " [تمت الاستعادة عبر وسم XData بعد النسخ]" : "");
            if (item.NeedsUserSpecs) recallMsg += "\n" + BuildMissingSpecsArabicQuestion(item.MissingSpecBlocks);

            return BuildGuidedStepResponse(curSession, recallMsg, new Dictionary<string, object>
            {
                { "recalled", true },
                { "recovered_via_xdata", recoveredViaXData }
            });
        }
    }
}
