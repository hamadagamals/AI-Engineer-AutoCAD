using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        private const string MasterSldLayer = "_AI_MASTER_SLD";
        private const string FeederScheduleLayer = "_AI_FEEDER_SCHEDULE";

        private static readonly double[] StandardTransformerKvaRatings = new double[]
        {
            100.0, 160.0, 250.0, 315.0, 400.0, 500.0, 630.0, 800.0, 1000.0, 1250.0, 1600.0, 2000.0, 2500.0
        };

        private static readonly double[] StandardGeneratorKvaRatings = new double[]
        {
            30.0, 45.0, 60.0, 80.0, 100.0, 150.0, 200.0, 250.0, 300.0, 400.0, 500.0, 630.0, 800.0, 1000.0, 1250.0, 1600.0, 2000.0
        };

        private sealed class DistNode
        {
            public string Name = "";
            public string Kind = "db"; // rmu|transformer|generator|ats|mdb|smdb|db|feeder_pillar|landscape_pole|motor_load
            public string ParentName = "";
            public double FeederLengthM = 25.0;
            public string FeederLayer = "";
            public double ExplicitKw = 0.0;
            public double ExplicitKva = 0.0;
            public double ConnectedKw = 0.0;
            public double ConnectedKva = 0.0;
            public double DemandKw = 0.0;
            public double DemandKva = 0.0;
            public double EmergencyDemandKva = 0.0;
            public double DemandFactor = 1.0;
            public double PowerFactor = 0.85;
            public int Phases = 3;
            public double VoltageV = 380.0;
            public string InstallMethod = "tray";
            public double AmbientTempC = 40.0;
            public int GroupedCircuits = 1;
            public string Material = "CU";
            public string Insulation = "XLPE";
            public double MaxVdPercent = 2.5;
            public double? TransformerKvaOpt;
            public double TransformerRatedKva = 0.0;
            public double TransformerLoadingPct = 0.0;
            public double ImpedancePct = 5.0;
            public double HvVoltageKv = 11.0;
            public double MvFaultMva = 250.0;
            public string MvCableDesignation = "";
            public double GeneratorRatedKva = 0.0;
            public double AtsRatingA = 0.0;
            public double BreakerRatingA = 0.0;
            public string BreakerType = "MCCB";
            public double RecommendedIcuKa = 0.0;
            public double BusIscKa = 25.0;
            public string FeederCableDesignation = "";
            public int FeederParallelRuns = 1;
            public double FeederSizeMm2 = 0.0;
            public double FeederPeSizeMm2 = 0.0;
            public double FeederVdPercent = 0.0;
            public double CumulativeVdPercent = 0.0;
            public bool IsEmergency = false;
            public bool IsLandscape = false;
            public bool HasDrawingCoords = false;
            public double CenterX = 0.0;
            public double CenterY = 0.0;
            public bool LoadHydratedFromProjectDb = false;
            public bool LoadMissing = false;
            public string Status = "OK";
            public DistNode Parent = null;
            public List<DistNode> Children = new List<DistNode>();
        }

        private sealed class ProjectPanelLoadSummary
        {
            public string PanelName = "";
            public double ConnectedKw = 0.0;
            public double ConnectedKva = 0.0;
            public int CircuitCount = 0;
            public bool IsEmergency = false;
            public bool IsLandscape = false;
        }

        private static string NormalizeDistKind(string rawKind, string nodeName)
        {
            string k = (rawKind ?? "").Trim().ToLowerInvariant();
            if (k == "rmu" || k == "transformer" || k == "generator" || k == "ats" ||
                k == "mdb" || k == "smdb" || k == "db" || k == "feeder_pillar" ||
                k == "landscape_pole" || k == "motor_load")
                return k;
            if (k == "tx" || k == "trf" || k == "trafo" || k == "محول") return "transformer";
            if (k == "gen" || k == "dg" || k == "genset" || k == "مولد") return "generator";
            if (k == "fp" || k == "feeder" || k == "pillar" || k == "ldb" || k == "landscape") return "feeder_pillar";
            if (k == "pole" || k == "bollard") return "landscape_pole";
            if (k == "motor" || k == "chiller" || k == "pump" || k == "ahu") return "motor_load";

            string up = (nodeName ?? "").Trim().ToUpperInvariant();
            if (up.Contains("RMU") || up.Contains("RING MAIN")) return "rmu";
            if (up.StartsWith("TX") || up.StartsWith("TRF") || up.Contains("TRANS") || up.Contains("محول")) return "transformer";
            if (up.StartsWith("GEN") || up.StartsWith("DG") || up.Contains("مولد")) return "generator";
            if (up.Contains("ATS") || up.Contains("MTS")) return "ats";
            if (up.StartsWith("MDB") || up.StartsWith("MSB") || up.StartsWith("MDP") || up == "MAIN") return "mdb";
            if (up.Contains("SMDB") || up.Contains("EMDB")) return "smdb";
            if (up.Contains("POLE") || up.Contains("BOLLARD")) return "landscape_pole";
            if (up.Contains("FP-") || up.Contains("FP_") || up.StartsWith("LDB") || up.Contains("LANDSCAPE") || up.Contains("SITE") || up.Contains("FEEDER PILLAR"))
                return "feeder_pillar";
            if (up.Contains("PUMP") || up.Contains("CHILLER") || up.Contains("MOTOR") || up.Contains("AHU")) return "motor_load";
            return "db";
        }

        private static bool LooksLikeEmergencyName(string name)
        {
            string up = (name ?? "").Trim().ToUpperInvariant();
            return up.StartsWith("EM") || up.StartsWith("EDB") || up.Contains("EMERG") ||
                   up.Contains("-EM") || up.Contains("ESSEN") || up.Contains("FIRE") || up.Contains("ATS");
        }

        private static bool LooksLikeLandscapeName(string name, string layer)
        {
            string up = ((name ?? "") + " " + (layer ?? "")).Trim().ToUpperInvariant();
            return up.Contains("LAND") || up.Contains("POLE") || up.Contains("BOLLARD") ||
                   up.Contains("SITE") || up.Contains("GARDEN") || up.Contains("EXT") ||
                   up.Contains("FEEDER PILLAR") || up.StartsWith("LDB") || up.StartsWith("FP-");
        }

        private static double GetDefaultNodeDemandFactor(string kind)
        {
            if (kind == "mdb" || kind == "transformer") return 0.80;
            if (kind == "smdb") return 0.85;
            if (kind == "ats" || kind == "generator") return 0.90;
            return 1.0;
        }

        private static double GetDefaultFeederMaxVd(string kind, bool isLandscape)
        {
            if (kind == "mdb") return 2.0;
            if (kind == "smdb" || kind == "ats") return 2.5;
            if (isLandscape || kind == "feeder_pillar" || kind == "landscape_pole") return 3.0;
            return 2.5;
        }

        private static double SelectStandardRating(double requiredKva, double[] table)
        {
            for (int i = 0; i < table.Length; i++)
            {
                if (table[i] + 1e-6 >= requiredKva) return table[i];
            }
            return table[table.Length - 1];
        }

        private static string SizeMvCableDesignation(double hvKv, double transformerKva)
        {
            double mvCurrentA = transformerKva / (Math.Sqrt(3.0) * Math.Max(1.0, hvKv));
            string sz = "3x35";
            if (mvCurrentA > 220.0) sz = "3x150";
            else if (mvCurrentA > 165.0) sz = "3x95";
            else if (mvCurrentA > 125.0) sz = "3x70";
            else if (mvCurrentA > 95.0) sz = "3x50";
            return sz + " mm2 Cu XLPE/SWA " + hvKv.ToString("0.#", CultureInfo.InvariantCulture) + "kV";
        }

        private static Dictionary<string, ProjectPanelLoadSummary> AggregateMergedProjectPanelLoads(Document doc)
        {
            Dictionary<string, ProjectPanelLoadSummary> map =
                new Dictionary<string, ProjectPanelLoadSummary>(StringComparer.OrdinalIgnoreCase);
            List<SavedCircuitRecord> recs = GetMergedSavedCircuitRecords(doc, false);
            for (int i = 0; i < recs.Count; i++)
            {
                SavedCircuitRecord r = recs[i];
                if (r == null) continue;
                string pName = ExtractPanelNameFallback(r.CircuitName, r.PanelName);
                if (string.IsNullOrWhiteSpace(pName)) continue;

                double cKw = r.LoadKw;
                double cKva = r.LoadKva;
                if (cKw <= 1e-6 && cKva <= 1e-6 && r.CountsByBlock != null && r.CountsByBlock.Count > 0)
                {
                    bool assumed;
                    string src;
                    Dictionary<string, object> rb, sb;
                    List<string> miss;
                    ComputeCircuitLoadFromProjectDb(
                        doc, r.CountsByBlock, null, null, 40.0, 0.90, false,
                        out cKw, out cKva, out assumed, out src, out rb, out miss, out sb);
                }

                ProjectPanelLoadSummary sum;
                if (!map.TryGetValue(pName, out sum))
                {
                    sum = new ProjectPanelLoadSummary
                    {
                        PanelName = pName,
                        IsEmergency = LooksLikeEmergencyName(pName),
                        IsLandscape = LooksLikeLandscapeName(pName, r.WireLayer)
                    };
                    map[pName] = sum;
                }
                sum.ConnectedKw += Math.Max(0.0, cKw);
                sum.ConnectedKva += Math.Max(0.0, cKva);
                sum.CircuitCount++;
            }
            return map;
        }

        private static bool IsCandidateCableRoutingCurve(Curve cv, Dictionary<string, SavedColorState> layerColorMap)
        {
            if (cv == null || cv.IsErased) return false;
            string layer = cv.Layer ?? "";
            if (IsLegendOrHighlightLayer(layer) || IsAiAnnotationLayer(layer) || IsArchitecturalLayerName(layer))
                return false;
            string up = layer.ToUpperInvariant();
            if (up.Contains("CABLE") || up.Contains("FEED") || up.Contains("ROUT") || up.Contains("TRAY") ||
                up.Contains("TRENCH") || up.Contains("BUSDUCT") || up.Contains("CONDUIT") ||
                up.Contains("RISER") || up.Contains("SUBMAIN") || up.Contains("POWER") ||
                up.Contains("LAND") || up.Contains("SITE") || up.Contains("LIGHT") || up.Contains("WIRE"))
                return true;
            bool nonDefault;
            ResolveEntityEffectiveColorKey(cv, layerColorMap, out nonDefault);
            return nonDefault;
        }

        private static bool MatchesDistEquipmentTag(string text, out string equipName, out string kind)
        {
            equipName = "";
            kind = "";
            if (string.IsNullOrWhiteSpace(text)) return false;
            string clean = Regex.Replace(text.Trim(), @"\s+", " ");
            if (clean.Length > 40) return false;
            // Reject circuit slash notation like DB-1/1 — only equipment/panel tags
            if (Regex.IsMatch(clean, @"[/\\]\s*\d+\s*$")) return false;

            Match m = Regex.Match(
                clean,
                @"\b(RMU(?:[-_\s]?\w+)?|TX(?:[-_\s]?\w+)?|TRF(?:[-_\s]?\w+)?|TRANSFORMER(?:[-_\s]?\w+)?|GEN(?:[-_\s]?\w+)?|DG(?:[-_\s]?\w+)?|ATS(?:[-_\s]?\w+)?|EMDB(?:[-_\s]?\w+)?|SMDB(?:[-_\s]?\w+)?|MDB(?:[-_\s]?\w+)?|MSB(?:[-_\s]?\w+)?|EDB(?:[-_\s]?\w+)?|DB[-_]?\w+|LP[-_]?\w+|PP[-_]?\w+|LDB[-_]?\w+|FP[-_]?\w+|POLE[-_]?\w+)\b",
                RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            equipName = Regex.Replace(m.Groups[1].Value.Trim().ToUpperInvariant(), @"\s+", "-");
            kind = NormalizeDistKind("", equipName);
            return true;
        }

        private sealed class RoutingCurveSeg
        {
            public ObjectId Id;
            public Point2d P1;
            public Point2d P2;
            public double LengthUnits;
        }

        private static List<object> ExtractDictObjectList(Dictionary<string, object> args, string key)
        {
            List<object> res = new List<object>();
            if (args == null || string.IsNullOrEmpty(key) || !args.ContainsKey(key) || args[key] == null)
                return res;
            System.Collections.IEnumerable en = args[key] as System.Collections.IEnumerable;
            if (en != null && !(args[key] is string))
            {
                foreach (object item in en) res.Add(item);
            }
            return res;
        }

        private static List<DistNode> DiscoverRoutingNodesFromDrawing(
            Document doc,
            Database db,
            Transaction tr,
            bool useSelected,
            double riserAllowanceM,
            double defaultFeederLenM)
        {
            Dictionary<string, bool> hidden = BuildLayerHiddenMap(db, tr);
            double dwgSpan, medSpan;
            Extents3d? dummyExt;
            ComputeDrawingFixtureMetrics(db, tr, true, hidden, out dwgSpan, out medSpan, out dummyExt);
            double rawU = Convert.ToDouble(GetUnitsInfo(db)["units_per_m"], CultureInfo.InvariantCulture);
            bool scInf;
            string scWarn;
            double unitsPerM = ResolveEffectiveUnitsPerMeter(db, rawU, dwgSpan, medSpan, out scInf, out scWarn);
            if (unitsPerM <= 1e-6) unitsPerM = 1000.0;

            Dictionary<string, SavedColorState> layerColorMap = BuildLayerColorStateMap(db, tr);
            HashSet<ObjectId> selectedSet = new HashSet<ObjectId>();
            if (useSelected)
            {
                List<string> sh = GetSelectedHandleStrings(doc);
                for (int i = 0; i < sh.Count; i++)
                {
                    ObjectId sid = ResolveHandle(db, sh[i]);
                    if (!sid.IsNull && !sid.IsErased) selectedSet.Add(sid);
                }
            }

            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            Dictionary<string, DistNode> discovered = new Dictionary<string, DistNode>(StringComparer.OrdinalIgnoreCase);
            List<RoutingCurveSeg> curves = new List<RoutingCurveSeg>();

            foreach (ObjectId oid in ms)
            {
                if (oid.IsErased) continue;
                if (selectedSet.Count > 0 && !selectedSet.Contains(oid)) continue;
                Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased || !IsEntityVisible(ent, hidden)) continue;
                if (IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;

                Curve cv = ent as Curve;
                if (cv != null && IsCandidateCableRoutingCurve(cv, layerColorMap))
                {
                    try
                    {
                        double len = 0.0;
                        try { len = Math.Abs(cv.GetDistanceAtParameter(cv.EndParam) - cv.GetDistanceAtParameter(cv.StartParam)); }
                        catch { len = cv.StartPoint.DistanceTo(cv.EndPoint); }
                        if (len > 1e-4)
                        {
                            curves.Add(new RoutingCurveSeg
                            {
                                Id = oid,
                                P1 = new Point2d(cv.StartPoint.X, cv.StartPoint.Y),
                                P2 = new Point2d(cv.EndPoint.X, cv.EndPoint.Y),
                                LengthUnits = len
                            });
                        }
                    }
                    catch { }
                    continue;
                }

                string rawTag = null;
                Point2d pos = Point2d.Origin;
                bool hasPos = false;

                BlockReference br = ent as BlockReference;
                if (br != null)
                {
                    string bn = ResolveBlockName(tr, br);
                    string attr = ResolveCachedBlockTag(tr, br);
                    rawTag = !string.IsNullOrWhiteSpace(attr) ? attr : bn;
                    try
                    {
                        Extents3d bex = br.GeometricExtents;
                        if (IsFiniteBounds(bex))
                        {
                            pos = new Point2d((bex.MinPoint.X + bex.MaxPoint.X) * 0.5, (bex.MinPoint.Y + bex.MaxPoint.Y) * 0.5);
                            hasPos = true;
                        }
                    }
                    catch { pos = new Point2d(br.Position.X, br.Position.Y); hasPos = true; }
                }
                else if (ent is DBText)
                {
                    DBText tx = (DBText)ent;
                    rawTag = tx.TextString;
                    pos = new Point2d(tx.Position.X, tx.Position.Y);
                    hasPos = true;
                }
                else if (ent is MText)
                {
                    MText mt = (MText)ent;
                    rawTag = mt.Text;
                    pos = new Point2d(mt.Location.X, mt.Location.Y);
                    hasPos = true;
                }

                if (!hasPos || string.IsNullOrWhiteSpace(rawTag)) continue;
                string eqName, eqKind;
                if (!MatchesDistEquipmentTag(rawTag, out eqName, out eqKind)) continue;

                if (!discovered.ContainsKey(eqName))
                {
                    bool isEm = LooksLikeEmergencyName(eqName);
                    bool isLand = LooksLikeLandscapeName(eqName, ent.Layer);
                    discovered[eqName] = new DistNode
                    {
                        Name = eqName,
                        Kind = eqKind,
                        DemandFactor = GetDefaultNodeDemandFactor(eqKind),
                        InstallMethod = isLand ? "buried" : "tray",
                        AmbientTempC = isLand ? 35.0 : 40.0,
                        MaxVdPercent = GetDefaultFeederMaxVd(eqKind, isLand),
                        IsEmergency = isEm,
                        IsLandscape = isLand,
                        HasDrawingCoords = true,
                        CenterX = pos.X,
                        CenterY = pos.Y,
                        FeederLengthM = defaultFeederLenM
                    };
                }
            }

            List<DistNode> list = new List<DistNode>(discovered.Values);
            if (list.Count == 0) return list;

            // Assign default hierarchy & estimate routed cable lengths along connected cable graph (or Manhattan + riser fallback)
            DistNode rmuNode = null, txNode = null, genNode = null, atsNode = null, mdbNode = null;
            List<DistNode> smdbNodes = new List<DistNode>();
            for (int i = 0; i < list.Count; i++)
            {
                DistNode n = list[i];
                if (n.Kind == "rmu" && rmuNode == null) rmuNode = n;
                else if (n.Kind == "transformer" && txNode == null) txNode = n;
                else if (n.Kind == "generator" && genNode == null) genNode = n;
                else if (n.Kind == "ats" && atsNode == null) atsNode = n;
                else if (n.Kind == "mdb" && mdbNode == null) mdbNode = n;
                else if (n.Kind == "smdb") smdbNodes.Add(n);
            }

            for (int i = 0; i < list.Count; i++)
            {
                DistNode n = list[i];
                if (!string.IsNullOrWhiteSpace(n.ParentName)) continue;
                if (n.Kind == "rmu") continue;
                if (n.Kind == "transformer") { if (rmuNode != null) n.ParentName = rmuNode.Name; continue; }
                if (n.Kind == "generator") { if (atsNode != null) n.ParentName = atsNode.Name; continue; }
                if (n.Kind == "mdb") { if (txNode != null) n.ParentName = txNode.Name; continue; }
                if (n.Kind == "ats") { if (mdbNode != null) n.ParentName = mdbNode.Name; else if (txNode != null) n.ParentName = txNode.Name; continue; }
                if (n.Kind == "smdb")
                {
                    if (n.IsEmergency && atsNode != null) n.ParentName = atsNode.Name;
                    else if (mdbNode != null) n.ParentName = mdbNode.Name;
                    else if (txNode != null) n.ParentName = txNode.Name;
                    continue;
                }

                // Leaf panels / landscape feeders: route to ATS/EMDB if emergency, nearest SMDB if present, else MDB
                if (n.IsEmergency && atsNode != null)
                {
                    n.ParentName = atsNode.Name;
                }
                else if (smdbNodes.Count > 0)
                {
                    DistNode bestSmdb = null;
                    double bestD = double.MaxValue;
                    for (int si = 0; si < smdbNodes.Count; si++)
                    {
                        DistNode sm = smdbNodes[si];
                        if (n.IsEmergency != sm.IsEmergency && smdbNodes.Count > 1) continue;
                        double d = Math.Sqrt(Math.Pow(n.CenterX - sm.CenterX, 2) + Math.Pow(n.CenterY - sm.CenterY, 2));
                        if (d < bestD) { bestD = d; bestSmdb = sm; }
                    }
                    if (bestSmdb != null) n.ParentName = bestSmdb.Name;
                }
                if (string.IsNullOrWhiteSpace(n.ParentName))
                {
                    if (mdbNode != null) n.ParentName = mdbNode.Name;
                    else if (txNode != null) n.ParentName = txNode.Name;
                }
            }

            // Estimate length for each parent link via routed curve traversal or geometry
            double snapTol = Math.Max(0.8 * unitsPerM, medSpan > 1e-3 ? medSpan * 2.5 : 2.0 * unitsPerM);
            Dictionary<string, DistNode> byName = new Dictionary<string, DistNode>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++) byName[list[i].Name] = list[i];

            for (int i = 0; i < list.Count; i++)
            {
                DistNode n = list[i];
                if (string.IsNullOrWhiteSpace(n.ParentName) || !byName.ContainsKey(n.ParentName)) continue;
                DistNode p = byName[n.ParentName];
                if (!n.HasDrawingCoords || !p.HasDrawingCoords) continue;

                double tracedM = MeasureRoutedCableLengthMeters(curves, n.CenterX, n.CenterY, p.CenterX, p.CenterY, snapTol, unitsPerM);
                if (tracedM > 0.5)
                {
                    n.FeederLengthM = Math.Round(tracedM + Math.Max(0.0, riserAllowanceM), 2);
                }
                else
                {
                    double manhattanM = (Math.Abs(n.CenterX - p.CenterX) + Math.Abs(n.CenterY - p.CenterY)) / unitsPerM;
                    n.FeederLengthM = manhattanM > 0.5
                        ? Math.Round(manhattanM + Math.Max(0.0, riserAllowanceM), 2)
                        : defaultFeederLenM;
                }
            }

            return list;
        }

        private static double MeasureRoutedCableLengthMeters(
            List<RoutingCurveSeg> curves,
            double x1, double y1,
            double x2, double y2,
            double snapTol,
            double unitsPerM)
        {
            if (curves == null || curves.Count == 0 || unitsPerM <= 1e-6) return 0.0;
            Point2d a = new Point2d(x1, y1);
            Point2d b = new Point2d(x2, y2);
            int seedIdx = -1;
            double seedD = double.MaxValue;
            HashSet<int> targetIdx = new HashSet<int>();

            for (int i = 0; i < curves.Count; i++)
            {
                RoutingCurveSeg s = curves[i];
                double dA = Math.Min(s.P1.GetDistanceTo(a), s.P2.GetDistanceTo(a));
                if (dA <= snapTol * 2.5 && dA < seedD) { seedD = dA; seedIdx = i; }
                double dB = Math.Min(s.P1.GetDistanceTo(b), s.P2.GetDistanceTo(b));
                if (dB <= snapTol * 2.5) targetIdx.Add(i);
            }
            if (seedIdx < 0 || targetIdx.Count == 0) return 0.0;
            if (targetIdx.Contains(seedIdx)) return curves[seedIdx].LengthUnits / unitsPerM;

            Queue<int> q = new Queue<int>();
            Dictionary<int, int> prev = new Dictionary<int, int>();
            prev[seedIdx] = -1;
            q.Enqueue(seedIdx);
            int reached = -1;

            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                if (targetIdx.Contains(cur)) { reached = cur; break; }
                RoutingCurveSeg sc = curves[cur];
                for (int j = 0; j < curves.Count; j++)
                {
                    if (prev.ContainsKey(j)) continue;
                    RoutingCurveSeg sj = curves[j];
                    bool touch =
                        sc.P1.GetDistanceTo(sj.P1) <= snapTol || sc.P1.GetDistanceTo(sj.P2) <= snapTol ||
                        sc.P2.GetDistanceTo(sj.P1) <= snapTol || sc.P2.GetDistanceTo(sj.P2) <= snapTol;
                    if (touch)
                    {
                        prev[j] = cur;
                        q.Enqueue(j);
                    }
                }
            }
            if (reached < 0) return 0.0;

            double sumUnits = 0.0;
            int step = reached;
            int guard = 0;
            while (step >= 0 && guard++ < 500)
            {
                sumUnits += curves[step].LengthUnits;
                step = prev[step];
            }
            return sumUnits / unitsPerM;
        }

        private static List<DistNode> ParseExplicitDistNodes(List<object> rawNodes, double defaultLengthM)
        {
            List<DistNode> list = new List<DistNode>();
            if (rawNodes == null) return list;
            for (int i = 0; i < rawNodes.Count; i++)
            {
                Dictionary<string, object> d = rawNodes[i] as Dictionary<string, object>;
                if (d == null) continue;
                string name = (GetStr(d, "name") ?? GetStr(d, "panel_name") ?? GetStr(d, "id") ?? ("N-" + (i + 1).ToString(CultureInfo.InvariantCulture))).Trim();
                string kind = NormalizeDistKind(GetStr(d, "kind") ?? GetStr(d, "type"), name);
                bool isLand = GetBool(d, "is_landscape", LooksLikeLandscapeName(name, GetStr(d, "layer")));
                bool isEm = GetBool(d, "is_emergency", LooksLikeEmergencyName(name));
                string inst = GetStr(d, "install_method") ?? (isLand ? "buried" : "tray");
                DistNode n = new DistNode
                {
                    Name = name,
                    Kind = kind,
                    ParentName = (GetStr(d, "parent") ?? GetStr(d, "parent_name") ?? GetStr(d, "fed_from") ?? "").Trim(),
                    FeederLengthM = Math.Max(0.5, GetDouble(d, "feeder_length_m", GetDouble(d, "length_m", defaultLengthM))),
                    ExplicitKw = Math.Max(0.0, GetDouble(d, "load_kw", 0.0)),
                    ExplicitKva = Math.Max(0.0, GetDouble(d, "load_kva", 0.0)),
                    DemandFactor = Math.Max(0.1, Math.Min(1.5, GetDouble(d, "demand_factor", GetDefaultNodeDemandFactor(kind)))),
                    PowerFactor = Math.Max(0.4, Math.Min(1.0, GetDouble(d, "power_factor", 0.85))),
                    Phases = GetInt(d, "phases", kind == "landscape_pole" ? 1 : 3),
                    VoltageV = GetDouble(d, "voltage_v", kind == "landscape_pole" ? 220.0 : 380.0),
                    InstallMethod = inst,
                    AmbientTempC = GetDouble(d, "ambient_temp_c", isLand ? 35.0 : 40.0),
                    GroupedCircuits = Math.Max(1, GetInt(d, "grouped_circuits", 1)),
                    Material = (GetStr(d, "material") ?? "CU").Trim().ToUpperInvariant(),
                    Insulation = (GetStr(d, "insulation") ?? "XLPE").Trim().ToUpperInvariant(),
                    MaxVdPercent = Math.Max(0.5, GetDouble(d, "max_vd_percent", GetDefaultFeederMaxVd(kind, isLand))),
                    TransformerKvaOpt = GetNullableDouble(d, "transformer_kva"),
                    ImpedancePct = Math.Max(2.0, Math.Min(12.0, GetDouble(d, "impedance_pct", 5.0))),
                    HvVoltageKv = Math.Max(3.3, GetDouble(d, "hv_voltage_kv", 11.0)),
                    MvFaultMva = Math.Max(25.0, GetDouble(d, "mv_fault_mva", 250.0)),
                    IsEmergency = isEm,
                    IsLandscape = isLand
                };
                if (n.ExplicitKw > 0.0 && n.ExplicitKva <= 0.0) n.ExplicitKva = n.ExplicitKw / n.PowerFactor;
                else if (n.ExplicitKva > 0.0 && n.ExplicitKw <= 0.0) n.ExplicitKw = n.ExplicitKva * n.PowerFactor;
                list.Add(n);
            }
            return list;
        }

        private static List<DistNode> SynthesizeProjectDistTree(
            Dictionary<string, ProjectPanelLoadSummary> projectLoads,
            double defaultLengthM,
            bool includeRmu,
            bool includeGeneratorAts)
        {
            List<DistNode> list = new List<DistNode>();
            List<ProjectPanelLoadSummary> panels = new List<ProjectPanelLoadSummary>(projectLoads.Values);
            panels.Sort(delegate(ProjectPanelLoadSummary a, ProjectPanelLoadSummary b)
            {
                return CompareNaturalTokens(a.PanelName, b.PanelName);
            });

            bool anyEm = includeGeneratorAts;
            for (int i = 0; i < panels.Count; i++)
                if (panels[i].IsEmergency) { anyEm = true; break; }

            if (includeRmu)
                list.Add(new DistNode { Name = "RMU-1", Kind = "rmu", ParentName = "", DemandFactor = 1.0, HvVoltageKv = 11.0 });

            list.Add(new DistNode
            {
                Name = "TX-1", Kind = "transformer",
                ParentName = includeRmu ? "RMU-1" : "",
                FeederLengthM = 15.0, DemandFactor = 0.80, HvVoltageKv = 11.0, ImpedancePct = 5.0
            });
            list.Add(new DistNode
            {
                Name = "MDB-1", Kind = "mdb", ParentName = "TX-1",
                FeederLengthM = 12.0, DemandFactor = 0.80, MaxVdPercent = 2.0
            });

            if (anyEm)
            {
                list.Add(new DistNode
                {
                    Name = "ATS-1", Kind = "ats", ParentName = "MDB-1",
                    FeederLengthM = 10.0, DemandFactor = 0.90, MaxVdPercent = 2.0, IsEmergency = true
                });
                list.Add(new DistNode
                {
                    Name = "GEN-1", Kind = "generator", ParentName = "ATS-1",
                    FeederLengthM = 18.0, DemandFactor = 1.0, MaxVdPercent = 2.0, IsEmergency = true
                });
            }

            bool addSmdb = panels.Count > 6;
            if (addSmdb)
            {
                list.Add(new DistNode
                {
                    Name = "SMDB-1", Kind = "smdb", ParentName = "MDB-1",
                    FeederLengthM = 25.0, DemandFactor = 0.85, MaxVdPercent = 2.5
                });
            }

            for (int i = 0; i < panels.Count; i++)
            {
                ProjectPanelLoadSummary p = panels[i];
                string kind = p.IsLandscape ? "feeder_pillar" : "db";
                string parent = p.IsEmergency && anyEm ? "ATS-1" : (p.IsLandscape ? "MDB-1" : (addSmdb ? "SMDB-1" : "MDB-1"));
                list.Add(new DistNode
                {
                    Name = p.PanelName,
                    Kind = kind,
                    ParentName = parent,
                    FeederLengthM = defaultLengthM,
                    ExplicitKw = Math.Round(p.ConnectedKw, 3),
                    ExplicitKva = Math.Round(p.ConnectedKva, 3),
                    DemandFactor = 1.0,
                    InstallMethod = p.IsLandscape ? "buried" : "tray",
                    AmbientTempC = p.IsLandscape ? 35.0 : 40.0,
                    MaxVdPercent = GetDefaultFeederMaxVd(kind, p.IsLandscape),
                    IsEmergency = p.IsEmergency,
                    IsLandscape = p.IsLandscape,
                    LoadHydratedFromProjectDb = true
                });
            }
            return list;
        }

        private static void RollUpDistLoadsDfs(DistNode node, HashSet<DistNode> visiting)
        {
            if (node == null || !visiting.Add(node)) return;
            if (node.Kind == "generator")
            {
                // Generator sizes off ATS / emergency subtree rather than feeding MDB upstream
                visiting.Remove(node);
                return;
            }

            if (node.Children.Count == 0)
            {
                node.ConnectedKw = Math.Round(node.ExplicitKw, 3);
                node.ConnectedKva = Math.Round(node.ExplicitKva, 3);
                node.DemandKw = Math.Round(node.ConnectedKw * node.DemandFactor, 3);
                node.DemandKva = Math.Round(node.ConnectedKva * node.DemandFactor, 3);
                node.EmergencyDemandKva = node.IsEmergency ? node.DemandKva : 0.0;
            }
            else
            {
                double sumConnKw = 0.0, sumConnKva = 0.0, sumSubDemandKw = 0.0, sumSubDemandKva = 0.0, sumEmKva = 0.0;
                for (int i = 0; i < node.Children.Count; i++)
                {
                    DistNode ch = node.Children[i];
                    if (ch.Kind == "generator") continue;
                    RollUpDistLoadsDfs(ch, visiting);
                    sumConnKw += ch.ConnectedKw;
                    sumConnKva += ch.ConnectedKva;
                    sumSubDemandKw += ch.DemandKw;
                    sumSubDemandKva += ch.DemandKva;
                    sumEmKva += ch.EmergencyDemandKva;
                }
                if (sumConnKva <= 1e-6 && node.ExplicitKva > 0.0)
                {
                    node.ConnectedKw = Math.Round(node.ExplicitKw, 3);
                    node.ConnectedKva = Math.Round(node.ExplicitKva, 3);
                    node.DemandKw = Math.Round(node.ExplicitKw * node.DemandFactor, 3);
                    node.DemandKva = Math.Round(node.ExplicitKva * node.DemandFactor, 3);
                    node.EmergencyDemandKva = node.IsEmergency ? node.DemandKva : 0.0;
                }
                else
                {
                    node.ConnectedKw = Math.Round(sumConnKw, 3);
                    node.ConnectedKva = Math.Round(sumConnKva, 3);
                    node.DemandKw = Math.Round(sumSubDemandKw * node.DemandFactor, 3);
                    node.DemandKva = Math.Round(sumSubDemandKva * node.DemandFactor, 3);
                    node.EmergencyDemandKva = (node.Kind == "ats" || node.IsEmergency) ? node.DemandKva : Math.Round(sumEmKva, 3);
                }
            }
            visiting.Remove(node);
        }

        private static void SizeDistTreeDownstreamDfs(
            DistNode node,
            double maxTotalVdPercent,
            List<string> warnings,
            HashSet<DistNode> visited)
        {
            if (node == null || !visited.Add(node)) return;

            if (node.Kind == "transformer")
            {
                double reqTxKva = node.DemandKva > 0.1 ? (node.DemandKva / 0.80) : 100.0;
                double txRating = node.TransformerKvaOpt.HasValue && node.TransformerKvaOpt.Value > 0.0
                    ? node.TransformerKvaOpt.Value
                    : SelectStandardRating(reqTxKva, StandardTransformerKvaRatings);
                node.TransformerRatedKva = txRating;
                node.TransformerLoadingPct = txRating > 0.0 ? Math.Round((node.DemandKva / txRating) * 100.0, 1) : 0.0;
                node.MvCableDesignation = SizeMvCableDesignation(node.HvVoltageKv, txRating);

                double vBaseV = node.VoltageV > 10.0 ? node.VoltageV : 400.0;
                double iRatedLv = (txRating * 1000.0) / (Math.Sqrt(3.0) * vBaseV);
                double zTxPu = Math.Max(0.02, node.ImpedancePct / 100.0);
                double zGridPu = txRating / (Math.Max(25.0, node.MvFaultMva) * 1000.0);
                node.BusIscKa = Math.Round((iRatedLv / (zTxPu + zGridPu)) / 1000.0, 2);
                node.CumulativeVdPercent = 0.0;
                if (node.TransformerLoadingPct > 85.0)
                    warnings.Add("تحذير تحميل المحول " + node.Name + ": " + node.TransformerLoadingPct.ToString("0.#", CultureInfo.InvariantCulture) + "% يتجاوز 85%.");
            }
            else if (node.Parent != null && node.Kind != "rmu")
            {
                DistNode parent = node.Parent;
                double feedKva = (node.Kind == "generator")
                    ? Math.Max(1.0, parent.DemandKva)
                    : Math.Max(0.1, node.DemandKva);

                if (node.Kind == "generator")
                {
                    double genReq = Math.Max(15.0, feedKva * 1.15);
                    node.GeneratorRatedKva = SelectStandardRating(genReq, StandardGeneratorKvaRatings);
                    node.BusIscKa = Math.Round(Math.Min(15.0, Math.Max(2.0, (node.GeneratorRatedKva * 1000.0) / (Math.Sqrt(3.0) * 400.0 * 0.18 * 1000.0))), 2);
                }
                if (node.Kind == "ats")
                {
                    double iAts = (feedKva * 1000.0) / (Math.Sqrt(3.0) * Math.Max(220.0, node.VoltageV));
                    double stdAts = SelectStandardBreaker(iAts * 1.25);
                    node.AtsRatingA = Math.Max(63.0, stdAts);
                }

                Dictionary<string, object> calcIn = new Dictionary<string, object>
                {
                    { "name", node.Name },
                    { "load_type", node.Kind == "motor_load" ? "motor" : (node.IsLandscape ? "lighting" : "power") },
                    { "phases", node.Phases },
                    { "voltage_v", node.VoltageV },
                    { "load_kva", feedKva },
                    { "power_factor", node.PowerFactor },
                    { "length_m", node.FeederLengthM },
                    { "material", node.Material },
                    { "insulation", node.Insulation },
                    { "install_method", node.InstallMethod },
                    { "ambient_temp_c", node.AmbientTempC },
                    { "grouped_circuits", node.GroupedCircuits },
                    { "max_vd_percent", node.MaxVdPercent },
                    { "source_isc_ka", parent.BusIscKa > 0.1 ? parent.BusIscKa : 25.0 },
                    { "breaker_type", feedKva >= 400.0 ? "ACB" : (feedKva >= 75.0 ? "MCCB" : "MCB") }
                };

                LvCircuitCalcResult res = ComputeSingleLvCircuit(calcIn, calcIn, 1);
                node.BreakerRatingA = res.BreakerRatingA;
                node.BreakerType = res.BreakerType;
                node.RecommendedIcuKa = res.RecommendedIcuKa;
                node.FeederCableDesignation = res.CableDesignation;
                node.FeederParallelRuns = res.ParallelRuns;
                node.FeederSizeMm2 = res.CableSizeMm2;
                node.FeederPeSizeMm2 = res.PeSizeMm2;
                node.FeederVdPercent = res.VoltageDropPercent;
                if (node.Kind != "generator")
                    node.BusIscKa = res.LoadIscKa > 0.0 ? res.LoadIscKa : parent.BusIscKa;
                node.CumulativeVdPercent = node.Kind == "generator"
                    ? res.VoltageDropPercent
                    : Math.Round(parent.CumulativeVdPercent + res.VoltageDropPercent, 2);
                node.Status = res.Status;

                if (node.CumulativeVdPercent > maxTotalVdPercent)
                {
                    node.Status = "CHECK_VD";
                    warnings.Add(
                        node.Name + ": هبوط الجهد التراكمي من المحول = " +
                        node.CumulativeVdPercent.ToString("0.00", CultureInfo.InvariantCulture) + "% يتجاوز الحد التوجيهي " +
                        maxTotalVdPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%.");
                }
            }

            for (int i = 0; i < node.Children.Count; i++)
                SizeDistTreeDownstreamDfs(node.Children[i], maxTotalVdPercent, warnings, visited);
        }

        private static string FormatParentEdgeSummary(DistNode n)
        {
            if (n.Kind == "transformer") return n.MvCableDesignation;
            if (string.IsNullOrEmpty(n.FeederCableDesignation)) return "-";
            return n.FeederCableDesignation + " | " + n.FeederLengthM.ToString("0.#", CultureInfo.InvariantCulture) + "m | VD " +
                   n.FeederVdPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        private static Dictionary<string, object> DistNodeToDict(DistNode n)
        {
            return new Dictionary<string, object>
            {
                { "name", n.Name },
                { "kind", n.Kind },
                { "parent", n.ParentName },
                { "connected_kw", n.ConnectedKw },
                { "connected_kva", n.ConnectedKva },
                { "demand_factor", n.DemandFactor },
                { "demand_kw", n.DemandKw },
                { "demand_kva", n.DemandKva },
                { "feeder_length_m", n.FeederLengthM },
                { "install_method", n.InstallMethod },
                { "breaker_a", n.BreakerRatingA },
                { "breaker_type", n.BreakerType },
                { "icu_ka", n.RecommendedIcuKa },
                { "cable", n.Kind == "transformer" ? n.MvCableDesignation : n.FeederCableDesignation },
                { "vd_percent", n.FeederVdPercent },
                { "cumulative_vd_percent", n.CumulativeVdPercent },
                { "bus_isc_ka", n.BusIscKa },
                { "transformer_kva", n.TransformerRatedKva },
                { "transformer_loading_percent", n.TransformerLoadingPct },
                { "generator_kva", n.GeneratorRatedKva },
                { "ats_a", n.AtsRatingA },
                { "is_emergency", n.IsEmergency },
                { "is_landscape", n.IsLandscape },
                { "load_from_project_db", n.LoadHydratedFromProjectDb },
                { "status", n.Status }
            };
        }

        private static Extents3d? DrawMasterDistributionSld(
            Database db,
            BlockTableRecord ms,
            Transaction tr,
            List<DistNode> allNodes,
            List<DistNode> orderedBfNodes,
            bool replaceExisting,
            double refSpan,
            double drawingSpan,
            double effectiveUnitsPerM,
            Extents3d? mainExt,
            List<ObjectId> createdIds,
            List<ObjectId> erasedIds)
        {
            if (orderedBfNodes == null || orderedBfNodes.Count == 0) return null;
            string sldLayer = EnsureLayerSafe(db, tr, MasterSldLayer, 7, "Continuous");

            if (replaceExisting)
            {
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e != null && !e.IsErased && string.Equals(e.Layer, sldLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        Entity ew = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                        ew.Erase(true);
                        erasedIds.Add(id);
                    }
                }
            }

            double h = refSpan > 1e-3
                ? Math.Max(0.18, refSpan * 0.42)
                : (drawingSpan > 1.0
                    ? Math.Max(0.20 * effectiveUnitsPerM, Math.Min(1.2 * effectiveUnitsPerM, drawingSpan * 0.010))
                    : Math.Max(0.22, 0.25 * effectiveUnitsPerM));

            double originX = mainExt.HasValue ? mainExt.Value.MaxPoint.X + Math.Max(h * 18.0, (mainExt.Value.MaxPoint.X - mainExt.Value.MinPoint.X) * 0.08) : 0.0;
            double topY = mainExt.HasValue ? mainExt.Value.MaxPoint.Y : 0.0;
            ObjectId unicodeStyleId = EnsureUnicodeTextStyle(db, tr);

            // Compute tree depth for clean horizontal row bands
            Dictionary<DistNode, int> depthMap = new Dictionary<DistNode, int>();
            int maxDepth = 0;
            for (int i = 0; i < orderedBfNodes.Count; i++)
            {
                DistNode n = orderedBfNodes[i];
                int d = 0;
                DistNode cur = n.Parent;
                int g = 0;
                while (cur != null && g++ < 12) { d++; cur = cur.Parent; }
                depthMap[n] = d;
                if (d > maxDepth) maxDepth = d;
            }

            // Compute X positions bottom-up / per level
            List<List<DistNode>> byLevel = new List<List<DistNode>>();
            for (int lv = 0; lv <= maxDepth; lv++) byLevel.Add(new List<DistNode>());
            for (int i = 0; i < orderedBfNodes.Count; i++)
                byLevel[depthMap[orderedBfNodes[i]]].Add(orderedBfNodes[i]);

            int maxCols = 1;
            for (int lv = 0; lv <= maxDepth; lv++)
                if (byLevel[lv].Count > maxCols) maxCols = byLevel[lv].Count;

            double colSpacing = h * 18.0;
            double rowSpacing = h * 16.0;
            double totalW = Math.Max(h * 45.0, maxCols * colSpacing + h * 10.0);
            double totalH = Math.Max(h * 35.0, (maxDepth + 1) * rowSpacing + h * 10.0);

            Extents3d frameExt = new Extents3d(
                new Point3d(originX, topY - totalH, 0.0),
                new Point3d(originX + totalW, topY, 0.0));
            Polyline border = CreateFramePolyline(frameExt, sldLayer, 7);
            border.ConstantWidth = h * 0.10;
            ms.AppendEntity(border);
            tr.AddNewlyCreatedDBObject(border, true);
            createdIds.Add(border.ObjectId);

            AddSldText(ms, tr, unicodeStyleId, sldLayer, 2,
                "MASTER ELECTRICAL DISTRIBUTION SLD (RMU / TX / GEN / ATS / MDB / LANDSCAPE)",
                originX + totalW * 0.5, topY - h * 2.0, h * 1.15, totalW * 0.92,
                AttachmentPoint.TopCenter, 0.0, createdIds);

            Dictionary<DistNode, Point2d> posMap = new Dictionary<DistNode, Point2d>();
            for (int lv = 0; lv <= maxDepth; lv++)
            {
                List<DistNode> rowNodes = byLevel[lv];
                double y = topY - h * 7.5 - lv * rowSpacing;
                double rowW = Math.Max(1, rowNodes.Count) * colSpacing;
                double startX = originX + (totalW - rowW) * 0.5 + colSpacing * 0.5;
                for (int ci = 0; ci < rowNodes.Count; ci++)
                    posMap[rowNodes[ci]] = new Point2d(startX + ci * colSpacing, y);
            }

            // Draw feeder edges first
            for (int i = 0; i < orderedBfNodes.Count; i++)
            {
                DistNode n = orderedBfNodes[i];
                if (n.Parent == null || !posMap.ContainsKey(n) || !posMap.ContainsKey(n.Parent)) continue;
                Point2d cp = posMap[n];
                Point2d pp = posMap[n.Parent];
                short edgeColor = n.IsLandscape ? (short)3 : (n.IsEmergency ? (short)1 : (short)4);
                double midY = (pp.Y + cp.Y) * 0.5;
                AddSldLine(ms, tr, sldLayer, edgeColor, pp.X, pp.Y - h * 2.2, pp.X, midY, h * 0.06, createdIds);
                AddSldLine(ms, tr, sldLayer, edgeColor, pp.X, midY, cp.X, midY, h * 0.06, createdIds);
                AddSldLine(ms, tr, sldLayer, edgeColor, cp.X, midY, cp.X, cp.Y + h * 3.8, h * 0.06, createdIds);
                AddSldBreakerSymbol(ms, tr, sldLayer, 3, cp.X, cp.Y + h * 3.8, cp.Y + h * 1.6, h * 0.65, createdIds);

                string edgeText = n.Kind == "transformer"
                    ? n.MvCableDesignation
                    : ((n.BreakerRatingA > 0 ? (n.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " + n.BreakerType + " | ") : "") +
                       FormatParentEdgeSummary(n) + " (Cum " + n.CumulativeVdPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%)");
                AddSldText(ms, tr, unicodeStyleId, sldLayer, 7, edgeText,
                    cp.X + h * 0.9, (midY + cp.Y + h * 2.4) * 0.5, h * 0.52, colSpacing * 0.85,
                    AttachmentPoint.MiddleLeft, 0.0, createdIds);
            }

            // Draw node equipment symbols
            for (int i = 0; i < orderedBfNodes.Count; i++)
            {
                DistNode n = orderedBfNodes[i];
                Point2d p = posMap[n];
                if (n.Kind == "transformer")
                {
                    Circle cTop = new Circle(new Point3d(p.X, p.Y + h * 0.7, 0.0), Vector3d.ZAxis, h * 1.3);
                    cTop.Layer = sldLayer; cTop.ColorIndex = 2;
                    ms.AppendEntity(cTop); tr.AddNewlyCreatedDBObject(cTop, true); createdIds.Add(cTop.ObjectId);
                    Circle cBot = new Circle(new Point3d(p.X, p.Y - h * 0.7, 0.0), Vector3d.ZAxis, h * 1.3);
                    cBot.Layer = sldLayer; cBot.ColorIndex = 2;
                    ms.AppendEntity(cBot); tr.AddNewlyCreatedDBObject(cBot, true); createdIds.Add(cBot.ObjectId);
                    AddSldText(ms, tr, unicodeStyleId, sldLayer, 2,
                        n.Name + " " + n.TransformerRatedKva.ToString("0", CultureInfo.InvariantCulture) + "kVA (" +
                        n.TransformerLoadingPct.ToString("0.#", CultureInfo.InvariantCulture) + "%) | Isc=" +
                        n.BusIscKa.ToString("0.#", CultureInfo.InvariantCulture) + "kA",
                        p.X + h * 1.8, p.Y, h * 0.60, colSpacing * 0.85,
                        AttachmentPoint.MiddleLeft, 0.0, createdIds);
                }
                else
                {
                    short boxCol = n.IsLandscape ? (short)3 : (n.IsEmergency ? (short)1 : (short)5);
                    double bw = n.Kind == "mdb" ? h * 11.0 : h * 8.0;
                    Extents3d nb = new Extents3d(
                        new Point3d(p.X - bw * 0.5, p.Y - h * 1.6, 0.0),
                        new Point3d(p.X + bw * 0.5, p.Y + h * 1.6, 0.0));
                    Polyline nPoly = CreateFramePolyline(nb, sldLayer, boxCol);
                    nPoly.ConstantWidth = n.Kind == "mdb" ? h * 0.14 : h * 0.07;
                    ms.AppendEntity(nPoly); tr.AddNewlyCreatedDBObject(nPoly, true); createdIds.Add(nPoly.ObjectId);

                    string lbl = n.Name + "\n" +
                        n.DemandKva.ToString("0.##", CultureInfo.InvariantCulture) + " kVA | Isc=" +
                        n.BusIscKa.ToString("0.#", CultureInfo.InvariantCulture) + " kA";
                    if (n.Kind == "generator")
                        lbl = n.Name + "\nGEN " + n.GeneratorRatedKva.ToString("0", CultureInfo.InvariantCulture) + " kVA Standby";
                    else if (n.Kind == "ats")
                        lbl = n.Name + "\nATS " + n.AtsRatingA.ToString("0", CultureInfo.InvariantCulture) + "A 4P";
                    AddSldText(ms, tr, unicodeStyleId, sldLayer, 7, lbl,
                        p.X, p.Y, h * 0.56, bw * 0.94,
                        AttachmentPoint.MiddleCenter, 0.0, createdIds);
                }
            }

            return frameExt;
        }

        private static Dictionary<string, object> BuildDistributionNetworkAndSld(Document doc, Dictionary<string, object> args)
        {
            if (doc == null || doc.Database == null)
                return Err("لا يوجد مخطط مفتوح حالياً في أوتوكاد.");
            if (args == null) args = new Dictionary<string, object>();

            Database db = doc.Database;
            string sourceMode = (GetStr(args, "source") ?? "auto").Trim().ToLowerInvariant();
            bool useSelected = GetBool(args, "use_selected", false);
            bool strictLoads = GetBool(args, "strict_loads", true);
            bool allowMissingLoads = GetBool(args, "allow_missing_loads", false);
            bool drawInCad = GetBool(args, "draw_in_cad", true);
            bool replaceExisting = GetBool(args, "replace_existing", true);
            bool exportExcel = GetBool(args, "export_excel", false);
            bool zoom = GetBool(args, "zoom", true);
            double defaultLengthM = Math.Max(1.0, GetDouble(args, "default_feeder_length_m", 25.0));
            double riserAllowanceM = Math.Max(0.0, GetDouble(args, "riser_allowance_m", 4.0));
            double maxTotalVdPct = Math.Max(1.0, Math.Min(15.0, GetDouble(args, "max_total_vd_percent", 5.0)));
            bool includeRmu = GetBool(args, "include_rmu", true);
            bool includeGenAts = GetBool(args, "include_generator_ats", false);
            double? txOverrideKva = GetNullableDouble(args, "transformer_kva");
            string customTitle = GetStr(args, "title");

            List<object> rawExplicitNodes = ExtractDictObjectList(args, "nodes");
            List<DistNode> nodes = new List<DistNode>();

            if (rawExplicitNodes != null && rawExplicitNodes.Count > 0)
            {
                nodes = ParseExplicitDistNodes(rawExplicitNodes, defaultLengthM);
            }
            else if (sourceMode == "auto" || sourceMode == "drawing_routing" || sourceMode == "drawing")
            {
                using (DocumentLock dl = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    nodes = DiscoverRoutingNodesFromDrawing(doc, db, tr, useSelected, riserAllowanceM, defaultLengthM);
                    tr.Commit();
                }
            }

            Dictionary<string, ProjectPanelLoadSummary> projectLoads = AggregateMergedProjectPanelLoads(doc);
            if (nodes.Count == 0)
            {
                if (projectLoads.Count == 0)
                    return Err("لم يتم العثور على شبكة توزيع/كابلات في الرسم ولا لوحات محفوظة في قاعدة بيانات المشروع. مرر nodes[] أو نفّذ الجرد أولاً.");
                nodes = SynthesizeProjectDistTree(projectLoads, defaultLengthM, includeRmu, includeGenAts);
            }

            // Hydrate nodes with saved project panel loads when explicit load wasn't specified
            for (int i = 0; i < nodes.Count; i++)
            {
                DistNode n = nodes[i];
                if (n.Kind == "transformer" && txOverrideKva.HasValue && txOverrideKva.Value > 0.0)
                    n.TransformerKvaOpt = txOverrideKva.Value;

                if (n.ExplicitKva <= 1e-6 && n.ExplicitKw <= 1e-6)
                {
                    ProjectPanelLoadSummary pSum;
                    if (projectLoads.TryGetValue(n.Name, out pSum) && pSum.ConnectedKva > 1e-6)
                    {
                        n.ExplicitKw = Math.Round(pSum.ConnectedKw, 3);
                        n.ExplicitKva = Math.Round(pSum.ConnectedKva, 3);
                        n.LoadHydratedFromProjectDb = true;
                        if (pSum.IsEmergency) n.IsEmergency = true;
                        if (pSum.IsLandscape)
                        {
                            n.IsLandscape = true;
                            n.InstallMethod = "buried";
                        }
                    }
                }
            }

            // Build parent-child graph safely
            Dictionary<string, DistNode> byName = new Dictionary<string, DistNode>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!byName.ContainsKey(nodes[i].Name)) byName[nodes[i].Name] = nodes[i];
            }
            List<DistNode> roots = new List<DistNode>();
            for (int i = 0; i < nodes.Count; i++)
            {
                DistNode n = nodes[i];
                if (!string.IsNullOrWhiteSpace(n.ParentName) && byName.ContainsKey(n.ParentName) && byName[n.ParentName] != n)
                {
                    n.Parent = byName[n.ParentName];
                    n.Parent.Children.Add(n);
                }
                else
                {
                    roots.Add(n);
                }
            }

            // Strict Zero-Hallucination Check for leaf loads
            List<string> missingPanelLoads = new List<string>();
            for (int i = 0; i < nodes.Count; i++)
            {
                DistNode n = nodes[i];
                bool isLeafConsumer = n.Children.Count == 0 &&
                    (n.Kind == "db" || n.Kind == "feeder_pillar" || n.Kind == "landscape_pole" || n.Kind == "motor_load");
                if (isLeafConsumer && n.ExplicitKva <= 1e-6 && n.ExplicitKw <= 1e-6)
                {
                    n.LoadMissing = true;
                    missingPanelLoads.Add(n.Name);
                }
            }

            if (strictLoads && !allowMissingLoads && missingPanelLoads.Count > 0)
            {
                return Ok(new Dictionary<string, object>
                {
                    { "blocked_for_missing_loads", true },
                    { "needs_user_loads", true },
                    { "missing_panel_loads", missingPanelLoads },
                    { "nodes_detected", nodes.Count },
                    { "message", "منع الهلوسة: اللوحات التالية في شبكة التوزيع لا تملك أحمالاً مسجلة في قاعدة البيانات: [" +
                        string.Join("، ", missingPanelLoads.ToArray()) +
                        "]. من فضلك أدخل حمل كل لوحة (kW أو kVA) أو أكمل جرد لينياتها أولاً (أو مرر allow_missing_loads=true)." }
                });
            }

            // Roll up demand & size feeders/equipment
            HashSet<DistNode> rollVis = new HashSet<DistNode>();
            for (int i = 0; i < roots.Count; i++) RollUpDistLoadsDfs(roots[i], rollVis);

            List<string> warnings = new List<string>();
            HashSet<DistNode> sizeVis = new HashSet<DistNode>();
            for (int i = 0; i < roots.Count; i++) SizeDistTreeDownstreamDfs(roots[i], maxTotalVdPct, warnings, sizeVis);

            // Breadth-first order for drawing and schedules
            List<DistNode> bfOrder = new List<DistNode>();
            Queue<DistNode> bfQ = new Queue<DistNode>();
            HashSet<DistNode> bfSeen = new HashSet<DistNode>();
            for (int i = 0; i < roots.Count; i++)
            {
                if (bfSeen.Add(roots[i])) bfQ.Enqueue(roots[i]);
            }
            while (bfQ.Count > 0)
            {
                DistNode cur = bfQ.Dequeue();
                bfOrder.Add(cur);
                cur.Children.Sort(delegate(DistNode a, DistNode b) { return CompareNaturalTokens(a.Name, b.Name); });
                for (int ci = 0; ci < cur.Children.Count; ci++)
                {
                    if (bfSeen.Add(cur.Children[ci])) bfQ.Enqueue(cur.Children[ci]);
                }
            }

            double totalDemandKw = 0.0, totalDemandKva = 0.0;
            DistNode primaryTx = null, primaryGen = null, primaryAts = null;
            for (int i = 0; i < bfOrder.Count; i++)
            {
                DistNode n = bfOrder[i];
                if (n.Kind == "transformer" && primaryTx == null) primaryTx = n;
                if (n.Kind == "generator" && primaryGen == null) primaryGen = n;
                if (n.Kind == "ats" && primaryAts == null) primaryAts = n;
            }
            if (primaryTx != null) { totalDemandKw = primaryTx.DemandKw; totalDemandKva = primaryTx.DemandKva; }
            else if (roots.Count > 0)
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    if (roots[i].Kind == "generator") continue;
                    totalDemandKw += roots[i].DemandKw;
                    totalDemandKva += roots[i].DemandKva;
                }
            }

            // Prepare Feeder Schedule Sheet + Multi-Sheet Excel data
            ExcelSheetData feederSheet = new ExcelSheetData
            {
                Title = !string.IsNullOrWhiteSpace(customTitle) ? customTitle : "Master Feeder & Voltage Drop Schedule (IEC 60364 / ECP)",
                SheetName = "Feeder & VD Calc",
                Headers = new List<string>
                {
                    "From", "To", "Kind", "Conn kVA", "Demand kVA", "Len (m)", "Method",
                    "Breaker", "Icu (kA)", "Cable", "VD %", "Cum VD %", "Bus Isc (kA)", "Status"
                },
                Rows = new List<List<string>>()
            };

            ExcelSheetData landscapeSheet = new ExcelSheetData
            {
                Title = "Landscape & Outdoor Feeders Schedule",
                SheetName = "Landscape Feeders",
                Headers = new List<string>
                {
                    "From", "To", "Demand kVA", "Len (m)", "Burial Method", "Breaker", "Cable", "VD %", "Cum VD %", "Status"
                },
                Rows = new List<List<string>>()
            };

            for (int i = 0; i < bfOrder.Count; i++)
            {
                DistNode n = bfOrder[i];
                if (n.Parent == null) continue;
                feederSheet.Rows.Add(new List<string>
                {
                    n.Parent.Name,
                    n.Name,
                    n.Kind.ToUpperInvariant(),
                    n.ConnectedKva.ToString("0.##", CultureInfo.InvariantCulture),
                    n.DemandKva.ToString("0.##", CultureInfo.InvariantCulture),
                    n.FeederLengthM.ToString("0.#", CultureInfo.InvariantCulture),
                    n.InstallMethod,
                    n.Kind == "transformer" ? "RMU 630A" : (n.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " + n.BreakerType),
                    n.RecommendedIcuKa > 0.0 ? n.RecommendedIcuKa.ToString("0.#", CultureInfo.InvariantCulture) : "20",
                    n.Kind == "transformer" ? n.MvCableDesignation : n.FeederCableDesignation,
                    n.FeederVdPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%",
                    n.CumulativeVdPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%",
                    n.BusIscKa.ToString("0.##", CultureInfo.InvariantCulture),
                    n.Status
                });

                if (n.IsLandscape || n.Kind == "feeder_pillar" || n.Kind == "landscape_pole")
                {
                    landscapeSheet.Rows.Add(new List<string>
                    {
                        n.Parent.Name, n.Name,
                        n.DemandKva.ToString("0.##", CultureInfo.InvariantCulture),
                        n.FeederLengthM.ToString("0.#", CultureInfo.InvariantCulture),
                        n.InstallMethod,
                        n.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " + n.BreakerType,
                        n.FeederCableDesignation,
                        n.FeederVdPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%",
                        n.CumulativeVdPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%",
                        n.Status
                    });
                }
            }

            bool cadDrawn = false;
            int cadEntitiesCreated = 0;
            if (drawInCad)
            {
                double refSpan, drawingSpan, effectiveUnitsPerM;
                Extents3d? mainExt = null;
                using (DocumentLock dl = doc.LockDocument())
                using (Transaction trEx = db.TransactionManager.StartTransaction())
                {
                    Dictionary<string, bool> hMap = BuildLayerHiddenMap(db, trEx);
                    ComputeDrawingFixtureMetrics(db, trEx, true, hMap, out drawingSpan, out refSpan, out mainExt);
                    double rawU = Convert.ToDouble(GetUnitsInfo(db)["units_per_m"], CultureInfo.InvariantCulture);
                    bool scInf;
                    string scWarn;
                    effectiveUnitsPerM = ResolveEffectiveUnitsPerMeter(db, rawU, drawingSpan, refSpan, out scInf, out scWarn);
                    trEx.Commit();
                }

                if (!db.TileMode) { try { db.TileMode = true; } catch { } }
                List<ObjectId> createdIds = new List<ObjectId>();
                List<ObjectId> erasedIds = new List<ObjectId>();
                Extents3d? allDrawnExt = null;

                using (DocumentLock dl = doc.LockDocument())
                using (Transaction trDraw = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord msW = (BlockTableRecord)trDraw.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    Extents3d? sldExt = DrawMasterDistributionSld(
                        db, msW, trDraw, nodes, bfOrder, replaceExisting,
                        refSpan, drawingSpan, effectiveUnitsPerM, mainExt, createdIds, erasedIds);
                    if (sldExt.HasValue && IsFiniteBounds(sldExt.Value)) allDrawnExt = sldExt;

                    if (feederSheet.Rows.Count > 0)
                    {
                        Extents3d? schedAnchor = sldExt ?? mainExt;
                        Extents3d? schedExt = RenderStyledScheduleTable(
                            doc, db, msW, trDraw, feederSheet, FeederScheduleLayer,
                            replaceExisting, "outside_right", null, null, schedAnchor, createdIds, erasedIds);
                        if (schedExt.HasValue && IsFiniteBounds(schedExt.Value))
                        {
                            if (!allDrawnExt.HasValue) allDrawnExt = schedExt;
                            else { Extents3d curE = allDrawnExt.Value; curE.AddExtents(schedExt.Value); allDrawnExt = curE; }
                        }
                    }

                    db.UpdateExt(true);
                    trDraw.Commit();
                }

                cadEntitiesCreated = createdIds.Count;
                cadDrawn = createdIds.Count > 0;
                if (createdIds.Count > 0 || erasedIds.Count > 0)
                {
                    CadUndoEntry uEntry = new CadUndoEntry
                    {
                        ToolName = "build_distribution_network_and_sld",
                        Summary = "Master Distribution SLD & Feeder Schedule"
                    };
                    uEntry.CreatedIds.AddRange(createdIds);
                    uEntry.ErasedIds.AddRange(erasedIds);
                    PushUndoEntry(doc, uEntry);
                }
                if (zoom && allDrawnExt.HasValue && IsFiniteBounds(allDrawnExt.Value))
                    ZoomToExtents3d(doc, allDrawnExt.Value, 1.12);
            }

            string excelPath = "";
            if (exportExcel)
            {
                ExcelSheetData summarySheet = new ExcelSheetData
                {
                    Title = "Master Project Electrical Distribution Summary",
                    SheetName = "Master Summary",
                    Headers = new List<string> { "Item", "Value", "Engineering Notes" },
                    Rows = new List<List<string>>
                    {
                        new List<string> { "Total Demand Power (kW)", Math.Round(totalDemandKw, 2).ToString("0.##", CultureInfo.InvariantCulture), "After demand factors" },
                        new List<string> { "Total Demand Apparent (kVA)", Math.Round(totalDemandKva, 2).ToString("0.##", CultureInfo.InvariantCulture), "IEC 60364 / ECP" },
                        new List<string> { "Main Transformer", primaryTx != null ? (primaryTx.TransformerRatedKva.ToString("0", CultureInfo.InvariantCulture) + " kVA (" + primaryTx.TransformerLoadingPct.ToString("0.#", CultureInfo.InvariantCulture) + "%)") : "-", primaryTx != null ? primaryTx.MvCableDesignation : "-" },
                        new List<string> { "Standby Generator", primaryGen != null ? (primaryGen.GeneratorRatedKva.ToString("0", CultureInfo.InvariantCulture) + " kVA") : "None", primaryAts != null ? ("ATS " + primaryAts.AtsRatingA.ToString("0", CultureInfo.InvariantCulture) + "A 4P") : "-" },
                        new List<string> { "Distribution Nodes", bfOrder.Count.ToString(CultureInfo.InvariantCulture), "Landscape feeders: " + landscapeSheet.Rows.Count.ToString(CultureInfo.InvariantCulture) }
                    }
                };

                ExcelSheetData eqSheet = new ExcelSheetData
                {
                    Title = "Transformer, Generator & Switchgear Sizing",
                    SheetName = "Transformer & ATS",
                    Headers = new List<string> { "Equipment", "Kind", "Rated / Breaker", "Demand kVA", "Loading %", "Bus Isc (kA)", "MV / Standby Cable" },
                    Rows = new List<List<string>>()
                };
                for (int i = 0; i < bfOrder.Count; i++)
                {
                    DistNode n = bfOrder[i];
                    if (n.Kind != "rmu" && n.Kind != "transformer" && n.Kind != "generator" && n.Kind != "ats" && n.Kind != "mdb")
                        continue;
                    string rated = n.Kind == "transformer" ? (n.TransformerRatedKva.ToString("0", CultureInfo.InvariantCulture) + " kVA")
                        : (n.Kind == "generator" ? (n.GeneratorRatedKva.ToString("0", CultureInfo.InvariantCulture) + " kVA")
                        : (n.Kind == "ats" ? (n.AtsRatingA.ToString("0", CultureInfo.InvariantCulture) + "A 4P ATS")
                        : (n.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " + n.BreakerType)));
                    eqSheet.Rows.Add(new List<string>
                    {
                        n.Name, n.Kind.ToUpperInvariant(), rated,
                        n.DemandKva.ToString("0.##", CultureInfo.InvariantCulture),
                        n.Kind == "transformer" ? (n.TransformerLoadingPct.ToString("0.#", CultureInfo.InvariantCulture) + "%") : "-",
                        n.BusIscKa.ToString("0.##", CultureInfo.InvariantCulture),
                        n.Kind == "transformer" ? n.MvCableDesignation : n.FeederCableDesignation
                    });
                }

                List<ExcelSheetData> book = new List<ExcelSheetData> { summarySheet, feederSheet, eqSheet };
                if (landscapeSheet.Rows.Count > 0) book.Add(landscapeSheet);

                string reqXl = GetStr(args, "excel_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "master_distribution_sld", ".xlsx");
                excelPath = ExcelTableInterop.WriteWorkbook(reqXl, book);
            }

            List<object> nodeDicts = new List<object>(bfOrder.Count);
            for (int i = 0; i < bfOrder.Count; i++) nodeDicts.Add(DistNodeToDict(bfOrder[i]));

            Dictionary<string, object> outDict = new Dictionary<string, object>
            {
                { "nodes_count", bfOrder.Count },
                { "landscape_feeders_count", landscapeSheet.Rows.Count },
                { "total_demand_kw", Math.Round(totalDemandKw, 3) },
                { "total_demand_kva", Math.Round(totalDemandKva, 3) },
                { "transformer_kva", primaryTx != null ? primaryTx.TransformerRatedKva : 0.0 },
                { "transformer_loading_percent", primaryTx != null ? primaryTx.TransformerLoadingPct : 0.0 },
                { "generator_kva", primaryGen != null ? primaryGen.GeneratorRatedKva : 0.0 },
                { "ats_rating_a", primaryAts != null ? primaryAts.AtsRatingA : 0.0 },
                { "drawn_in_cad", cadDrawn },
                { "cad_entities_created", cadEntitiesCreated },
                { "nodes", nodeDicts },
                { "warnings", warnings }
            };
            if (!string.IsNullOrEmpty(excelPath))
            {
                outDict["excel_exported"] = true;
                outDict["excel_path"] = excelPath;
            }
            outDict["message"] = string.Format(
                CultureInfo.InvariantCulture,
                "تم حساب ورسم شبكة التوزيع الرئيسية (SLD + الكابلات + القواطع + VD% + تيار القصر Isc): {0} عقدة | الطلب الكلي {1} kVA{2}{3}.",
                bfOrder.Count,
                Math.Round(totalDemandKva, 2).ToString("0.##", CultureInfo.InvariantCulture),
                primaryTx != null ? (" | محول " + primaryTx.TransformerRatedKva.ToString("0", CultureInfo.InvariantCulture) + " kVA (" + primaryTx.TransformerLoadingPct.ToString("0.#", CultureInfo.InvariantCulture) + "%)") : "",
                primaryGen != null ? (" | مولد " + primaryGen.GeneratorRatedKva.ToString("0", CultureInfo.InvariantCulture) + " kVA + ATS") : "");
            return Ok(outDict);
        }
    }
}
