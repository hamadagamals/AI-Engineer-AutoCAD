using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

#if !PANEL_MFG_STANDALONE
[assembly: CommandClass(typeof(AIEngineer.AutoCAD.DialuxWizardCommands))]
#endif

namespace AIEngineer.AutoCAD
{
#if !PANEL_MFG_STANDALONE
    public sealed class DialuxWizardCommands
    {
        [CommandMethod("AIENGDIALUX", CommandFlags.Session)]
        public void RunDialuxExportWizard()
        {
            Autodesk.AutoCAD.ApplicationServices.Document doc =
                Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Dictionary<string, object> res = CadToolExecutor.ExecuteTool(doc, "export_dialux_stf", new Dictionary<string, object>
            {
                { "interactive_dialog", true }
            });
            Editor ed = doc.Editor;
            if (ed != null && res != null)
            {
                object msgObj;
                if (res.TryGetValue("message", out msgObj) && msgObj != null)
                    ed.WriteMessage("\n[AI-Engineer DIALux] " + msgObj.ToString() + "\n");
                else if (res.TryGetValue("error", out msgObj) && msgObj != null)
                    ed.WriteMessage("\n[AI-Engineer DIALux] " + msgObj.ToString() + "\n");
            }
        }
    }
#endif

    internal static partial class CadToolExecutor
    {
        internal sealed class PhotometricProfile
        {
            public string SourceFile = "";
            public string Manufacturer = "Generic";
            public string CatalogName = "LED_PANEL_60x60";
            public double TotalLumens = 3600.0;
            public double Watts = 36.0;
            public double WidthM = 0.60;
            public double LengthM = 0.60;
            public double HeightM = 0.05;
            public double DownwardFraction = 0.92;
            public double[] VerticalAngles = null;
            public double[] HorizontalAngles = null;
            public double[,] CandelaMatrix = null; // [phiIdx, thetaIdx] in cd total
        }

        private sealed class LightingStandardRule
        {
            public string SpaceCategory;
            public string StandardCode;
            public double TargetLux;
            public double MinUniformity;
            public double MaxUgr;
            public double MaxLpdWm2;
        }

        private sealed class RoomPolygonInfo
        {
            public int Index;
            public string Id;
            public string Name;
            public string Handle;
            public List<Point2d> VerticesM = new List<Point2d>();
            public double AreaM2;
            public double MinXm;
            public double MinYm;
            public double MaxXm;
            public double MaxYm;
            public double CentroidXm;
            public double CentroidYm;
        }

        private static bool IsPointInPolygon2D(List<Point2d> poly, double x, double y)
        {
            if (poly == null || poly.Count < 3) return false;
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;
                bool intersect = ((yi > y) != (yj > y)) &&
                                 (x < (xj - xi) * (y - yi) / ((yj - yi) == 0 ? 1e-12 : (yj - yi)) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static LightingStandardRule ResolveLightingStandard(string roomName, string standardPref, double? explicitTargetLux)
        {
            string std = (standardPref ?? "ECP").Trim().ToUpperInvariant();
            if (std != "EN12464" && std != "CIBSE" && std != "ECP") std = "ECP";

            string rn = (roomName ?? "").Trim().ToLowerInvariant();
            LightingStandardRule rule = new LightingStandardRule
            {
                SpaceCategory = "General / Office",
                StandardCode = std,
                TargetLux = (std == "EN12464") ? 500.0 : 400.0,
                MinUniformity = 0.60,
                MaxUgr = 19.0,
                MaxLpdWm2 = 10.0
            };

            if (rn.Contains("corridor") || rn.Contains("hallway") || rn.Contains("passage") || rn.Contains("ممر") || rn.Contains("طرقة") || rn.Contains("كريدور"))
            {
                rule.SpaceCategory = "Corridor / Circulation";
                rule.TargetLux = 100.0; rule.MinUniformity = 0.40; rule.MaxUgr = 28.0; rule.MaxLpdWm2 = 4.5;
            }
            else if (rn.Contains("stair") || rn.Contains("سلم") || rn.Contains("درج"))
            {
                rule.SpaceCategory = "Staircase";
                rule.TargetLux = 150.0; rule.MinUniformity = 0.40; rule.MaxUgr = 25.0; rule.MaxLpdWm2 = 5.5;
            }
            else if (rn.Contains("parking") || rn.Contains("garage") || rn.Contains("جراج") || rn.Contains("موقف"))
            {
                rule.SpaceCategory = "Parking / Garage";
                rule.TargetLux = 75.0; rule.MinUniformity = 0.40; rule.MaxUgr = 25.0; rule.MaxLpdWm2 = 3.0;
            }
            else if (rn.Contains("bath") || rn.Contains("toilet") || rn.Contains("wc") || rn.Contains("restroom") || rn.Contains("حمام") || rn.Contains("دورة"))
            {
                rule.SpaceCategory = "Restroom / WC";
                rule.TargetLux = (std == "EN12464") ? 200.0 : 150.0; rule.MinUniformity = 0.40; rule.MaxUgr = 25.0; rule.MaxLpdWm2 = 6.0;
            }
            else if (rn.Contains("bed") || rn.Contains("نوم"))
            {
                rule.SpaceCategory = "Bedroom";
                rule.TargetLux = 150.0; rule.MinUniformity = 0.40; rule.MaxUgr = 22.0; rule.MaxLpdWm2 = 6.0;
            }
            else if (rn.Contains("living") || rn.Contains("lounge") || rn.Contains("صالة") || rn.Contains("معيشة") || rn.Contains("ريسيبشن"))
            {
                rule.SpaceCategory = "Living / Reception";
                rule.TargetLux = 200.0; rule.MinUniformity = 0.50; rule.MaxUgr = 22.0; rule.MaxLpdWm2 = 7.5;
            }
            else if (rn.Contains("lobby") || rn.Contains("reception") || rn.Contains("بهو") || rn.Contains("استقبال"))
            {
                rule.SpaceCategory = "Lobby / Entrance";
                rule.TargetLux = 200.0; rule.MinUniformity = 0.50; rule.MaxUgr = 22.0; rule.MaxLpdWm2 = 8.0;
            }
            else if (rn.Contains("kitchen") || rn.Contains("pantry") || rn.Contains("مطبخ") || rn.Contains("أوفيس"))
            {
                rule.SpaceCategory = "Kitchen / Pantry";
                rule.TargetLux = (std == "EN12464") ? 500.0 : 300.0; rule.MinUniformity = 0.60; rule.MaxUgr = 22.0; rule.MaxLpdWm2 = 9.0;
            }
            else if (rn.Contains("class") || rn.Contains("lecture") || rn.Contains("school") || rn.Contains("فصل") || rn.Contains("محاضرات"))
            {
                rule.SpaceCategory = "Classroom";
                rule.TargetLux = 300.0; rule.MinUniformity = 0.60; rule.MaxUgr = 19.0; rule.MaxLpdWm2 = 9.0;
            }
            else if (rn.Contains("meeting") || rn.Contains("conf") || rn.Contains("board") || rn.Contains("اجتماعات") || rn.Contains("مؤتمرات"))
            {
                rule.SpaceCategory = "Meeting Room";
                rule.TargetLux = 500.0; rule.MinUniformity = 0.60; rule.MaxUgr = 19.0; rule.MaxLpdWm2 = 10.5;
            }
            else if (rn.Contains("clinic") || rn.Contains("hospital") || rn.Contains("exam") || rn.Contains("عيادة") || rn.Contains("كشف") || rn.Contains("مستشفى"))
            {
                rule.SpaceCategory = "Medical / Clinic";
                rule.TargetLux = 500.0; rule.MinUniformity = 0.60; rule.MaxUgr = 19.0; rule.MaxLpdWm2 = 11.0;
            }
            else if (rn.Contains("lab") || rn.Contains("معمل") || rn.Contains("مختبر"))
            {
                rule.SpaceCategory = "Laboratory";
                rule.TargetLux = 500.0; rule.MinUniformity = 0.60; rule.MaxUgr = 19.0; rule.MaxLpdWm2 = 11.5;
            }
            else if (rn.Contains("warehouse") || rn.Contains("store") || rn.Contains("storage") || rn.Contains("مخزن") || rn.Contains("مستودع"))
            {
                rule.SpaceCategory = "Warehouse / Storage";
                rule.TargetLux = 150.0; rule.MinUniformity = 0.40; rule.MaxUgr = 25.0; rule.MaxLpdWm2 = 6.0;
            }
            else if (rn.Contains("workshop") || rn.Contains("factory") || rn.Contains("ورشة") || rn.Contains("مصنع"))
            {
                rule.SpaceCategory = "Workshop / Industrial";
                rule.TargetLux = 300.0; rule.MinUniformity = 0.60; rule.MaxUgr = 22.0; rule.MaxLpdWm2 = 9.5;
            }
            else if (rn.Contains("shop") || rn.Contains("retail") || rn.Contains("showroom") || rn.Contains("محل") || rn.Contains("معرض"))
            {
                rule.SpaceCategory = "Retail / Showroom";
                rule.TargetLux = 300.0; rule.MinUniformity = 0.50; rule.MaxUgr = 22.0; rule.MaxLpdWm2 = 12.0;
            }
            else if (rn.Contains("mdb") || rn.Contains("elec") || rn.Contains("panel") || rn.Contains("switchgear") || rn.Contains("كهرباء") || rn.Contains("لوحات"))
            {
                rule.SpaceCategory = "Electrical / Switchgear Room";
                rule.TargetLux = 200.0; rule.MinUniformity = 0.50; rule.MaxUgr = 25.0; rule.MaxLpdWm2 = 7.5;
            }

            if (explicitTargetLux.HasValue && explicitTargetLux.Value > 1.0)
                rule.TargetLux = explicitTargetLux.Value;

            return rule;
        }

        private static PhotometricProfile ParsePhotometricFile(string filePath, double fallbackLumens, double fallbackWatts, string fallbackName)
        {
            PhotometricProfile p = new PhotometricProfile
            {
                TotalLumens = fallbackLumens > 0 ? fallbackLumens : 3600.0,
                Watts = fallbackWatts > 0 ? fallbackWatts : 36.0,
                CatalogName = string.IsNullOrWhiteSpace(fallbackName) ? "LED_PANEL_60x60" : fallbackName
            };
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return p;

            p.SourceFile = filePath;
            string ext = Path.GetExtension(filePath).Trim().ToLowerInvariant();
            string[] rawLines = File.ReadAllLines(filePath);

            if (ext == ".ldt")
            {
                // EULUMDAT format
                if (rawLines.Length >= 25)
                {
                    p.Manufacturer = (rawLines[0] ?? "").Trim();
                    if (string.IsNullOrEmpty(p.Manufacturer)) p.Manufacturer = "EULUMDAT";
                    string lName = (rawLines.Length > 8 ? rawLines[8] : "").Trim();
                    if (!string.IsNullOrEmpty(lName)) p.CatalogName = lName;

                    int mc = 1, ng = 1;
                    int.TryParse((rawLines[3] ?? "1").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out mc);
                    int.TryParse((rawLines[5] ?? "1").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out ng);
                    double lenMm = 600, widMm = 600;
                    double.TryParse((rawLines[12] ?? "600").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out lenMm);
                    double.TryParse((rawLines[13] ?? "600").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out widMm);
                    if (lenMm > 10) p.LengthM = lenMm / 1000.0;
                    if (widMm > 10) p.WidthM = widMm / 1000.0;

                    double lm = 0, wt = 0;
                    if (rawLines.Length > 28) double.TryParse(rawLines[28].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out lm);
                    if (rawLines.Length > 29) double.TryParse(rawLines[29].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out wt);
                    if (lm > 10) p.TotalLumens = lm;
                    if (wt > 0.5) p.Watts = wt;

                    int tokIdx = 42;
                    List<string> numTokens = new List<string>();
                    for (int li = 42; li < rawLines.Length; li++)
                    {
                        string[] parts = (rawLines[li] ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        for (int pi = 0; pi < parts.Length; pi++) numTokens.Add(parts[pi]);
                    }
                    if (numTokens.Count >= mc + ng && tokIdx > 0)
                    {
                        double[] phis = new double[Math.Max(1, mc)];
                        int ti = 0;
                        for (int i = 0; i < mc && ti < numTokens.Count; i++, ti++)
                            double.TryParse(numTokens[ti], NumberStyles.Any, CultureInfo.InvariantCulture, out phis[i]);
                        double[] thetas = new double[Math.Max(1, ng)];
                        for (int i = 0; i < ng && ti < numTokens.Count; i++, ti++)
                            double.TryParse(numTokens[ti], NumberStyles.Any, CultureInfo.InvariantCulture, out thetas[i]);

                        double[,] cd = new double[mc, ng];
                        double scaleKlm = p.TotalLumens / 1000.0;
                        for (int phi = 0; phi < mc; phi++)
                        {
                            for (int th = 0; th < ng && ti < numTokens.Count; th++, ti++)
                            {
                                double val = 0;
                                double.TryParse(numTokens[ti], NumberStyles.Any, CultureInfo.InvariantCulture, out val);
                                cd[phi, th] = val * scaleKlm;
                            }
                        }
                        p.HorizontalAngles = phis;
                        p.VerticalAngles = thetas;
                        p.CandelaMatrix = cd;
                    }
                }
                return p;
            }

            // Standard LM-63 .ies format
            int tiltIdx = -1;
            for (int i = 0; i < rawLines.Length; i++)
            {
                string line = (rawLines[i] ?? "").Trim();
                if (line.StartsWith("[MANUFAC]", StringComparison.OrdinalIgnoreCase))
                    p.Manufacturer = line.Substring(9).Trim();
                else if (line.StartsWith("[LUMCAT]", StringComparison.OrdinalIgnoreCase))
                    p.CatalogName = line.Substring(8).Trim();
                else if (line.StartsWith("[LUMINAIRE]", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(p.CatalogName))
                    p.CatalogName = line.Substring(11).Trim();
                else if (line.StartsWith("TILT=", StringComparison.OrdinalIgnoreCase))
                {
                    tiltIdx = i;
                    break;
                }
            }
            if (tiltIdx < 0) return p;

            int dataStart = tiltIdx + 1;
            if (!rawLines[tiltIdx].Trim().Equals("TILT=NONE", StringComparison.OrdinalIgnoreCase))
            {
                // Skip 4 TILT block lines
                dataStart = tiltIdx + 5;
            }

            List<double> nums = new List<double>();
            for (int i = dataStart; i < rawLines.Length; i++)
            {
                string[] parts = Regex.Split((rawLines[i] ?? "").Trim(), @"[\s,]+");
                for (int k = 0; k < parts.Length; k++)
                {
                    double v;
                    if (!string.IsNullOrEmpty(parts[k]) && double.TryParse(parts[k], NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                        nums.Add(v);
                }
            }

            // Header has 13 numeric parameters before angles/candela values
            if (nums.Count >= 15)
            {
                int numLamps = Math.Max(1, (int)Math.Round(nums[0]));
                double lmPerLamp = nums[1];
                double mult = nums[2] > 0 ? nums[2] : 1.0;
                int nVert = Math.Max(1, (int)Math.Round(nums[3]));
                int nHoriz = Math.Max(1, (int)Math.Round(nums[4]));
                int unitType = (int)Math.Round(nums[6]); // 1=feet, 2=meters
                double wVal = nums[7];
                double lVal = nums[8];
                double hVal = nums[9];
                double ballast = nums[10] > 0 ? nums[10] : 1.0;
                double future = nums[11] > 0 ? nums[11] : 1.0;
                double watts = nums[12];

                if (lmPerLamp > 0) p.TotalLumens = numLamps * lmPerLamp * ballast * future;
                if (watts > 0) p.Watts = watts;
                double uScale = (unitType == 1) ? 0.3048 : 1.0;
                if (wVal > 0) p.WidthM = wVal * uScale;
                if (lVal > 0) p.LengthM = lVal * uScale;
                if (hVal > 0) p.HeightM = hVal * uScale;

                int expectedLen = 13 + nVert + nHoriz + (nVert * nHoriz);
                if (nums.Count >= expectedLen)
                {
                    int cursor = 13;
                    double[] vAng = new double[nVert];
                    for (int i = 0; i < nVert; i++) vAng[i] = nums[cursor++];
                    double[] hAng = new double[nHoriz];
                    for (int i = 0; i < nHoriz; i++) hAng[i] = nums[cursor++];

                    double[,] cd = new double[nHoriz, nVert];
                    double downFlux = 0.0;
                    double totFlux = 0.0;
                    for (int phi = 0; phi < nHoriz; phi++)
                    {
                        for (int th = 0; th < nVert; th++)
                        {
                            double val = nums[cursor++] * mult * ballast * future;
                            cd[phi, th] = val;
                            double thRad = vAng[th] * Math.PI / 180.0;
                            double dTh = (th + 1 < nVert ? (vAng[th + 1] - vAng[th]) : (th > 0 ? vAng[th] - vAng[th - 1] : 5.0)) * Math.PI / 180.0;
                            double dFlux = val * Math.Sin(thRad) * Math.Max(0.01, dTh);
                            totFlux += dFlux;
                            if (vAng[th] <= 90.0) downFlux += dFlux;
                        }
                    }
                    if (totFlux > 1e-6) p.DownwardFraction = Math.Max(0.1, Math.Min(1.0, downFlux / totFlux));
                    p.VerticalAngles = vAng;
                    p.HorizontalAngles = hAng;
                    p.CandelaMatrix = cd;
                }
            }
            return p;
        }

        private static double SampleCandela(PhotometricProfile p, double thetaDeg, double phiDeg)
        {
            if (p.CandelaMatrix == null || p.VerticalAngles == null || p.HorizontalAngles == null || p.VerticalAngles.Length == 0)
            {
                // Clean Lambertian-cosine default distribution normalized so downward integral matches TotalLumens: I_0 = Phi / pi
                if (thetaDeg >= 90.0) return 0.0;
                double thRad = thetaDeg * Math.PI / 180.0;
                return (p.TotalLumens / Math.PI) * Math.Cos(thRad);
            }

            double[] vAng = p.VerticalAngles;
            double[] hAng = p.HorizontalAngles;
            double phi = phiDeg % 360.0;
            if (phi < 0) phi += 360.0;
            if (hAng.Length > 1 && hAng[hAng.Length - 1] <= 90.5 && phi > 90.0)
            {
                // Quarter symmetry fold
                phi = phi % 180.0;
                if (phi > 90.0) phi = 180.0 - phi;
            }
            else if (hAng.Length > 1 && hAng[hAng.Length - 1] <= 180.5 && phi > 180.0)
            {
                phi = 360.0 - phi;
            }

            int v0 = 0, v1 = 0;
            double vt = 0;
            for (int i = 0; i < vAng.Length - 1; i++)
            {
                if (thetaDeg >= vAng[i] && thetaDeg <= vAng[i + 1])
                {
                    v0 = i; v1 = i + 1;
                    double span = vAng[v1] - vAng[v0];
                    vt = span > 1e-6 ? (thetaDeg - vAng[v0]) / span : 0.0;
                    break;
                }
                if (i == vAng.Length - 2 && thetaDeg > vAng[vAng.Length - 1])
                    v0 = v1 = vAng.Length - 1;
            }

            int h0 = 0, h1 = 0;
            double ht = 0;
            for (int i = 0; i < hAng.Length - 1; i++)
            {
                if (phi >= hAng[i] && phi <= hAng[i + 1])
                {
                    h0 = i; h1 = i + 1;
                    double span = hAng[h1] - hAng[h0];
                    ht = span > 1e-6 ? (phi - hAng[h0]) / span : 0.0;
                    break;
                }
                if (i == hAng.Length - 2 && phi > hAng[hAng.Length - 1])
                    h0 = h1 = hAng.Length - 1;
            }

            double c00 = p.CandelaMatrix[h0, v0];
            double c01 = p.CandelaMatrix[h0, v1];
            double c10 = p.CandelaMatrix[h1, v0];
            double c11 = p.CandelaMatrix[h1, v1];
            double c0 = c00 + (c01 - c00) * vt;
            double c1 = c10 + (c11 - c10) * vt;
            return Math.Max(0.0, c0 + (c1 - c0) * ht);
        }

        private static double EstimateUtilizationFactor(double roomIndexK, double reflCeil, double reflWall, double reflFloor, double downFraction)
        {
            double k = Math.Max(0.4, Math.Min(5.0, roomIndexK));
            double baseCu = 0.30 + 0.42 * (1.0 - Math.Exp(-0.85 * k));
            double cavityBonus = 0.08 * reflCeil + 0.07 * reflWall + 0.03 * reflFloor;
            double cu = (baseCu + cavityBonus) * Math.Max(0.45, Math.Min(1.0, downFraction));
            return Math.Max(0.28, Math.Min(0.84, cu));
        }

        private static ObjectId EnsureLuminaireBlockDef(Database db, Transaction tr, BlockTable bt, string blockName, double widthInUnits, double lengthInUnits)
        {
            if (bt.Has(blockName)) return bt[blockName];

            BlockTableRecord btr = new BlockTableRecord();
            btr.Name = blockName;
            double hw = Math.Max(1e-3, widthInUnits * 0.5);
            double hl = Math.Max(1e-3, lengthInUnits * 0.5);

            Polyline rect = new Polyline();
            rect.AddVertexAt(0, new Point2d(-hl, -hw), 0, 0, 0);
            rect.AddVertexAt(1, new Point2d(hl, -hw), 0, 0, 0);
            rect.AddVertexAt(2, new Point2d(hl, hw), 0, 0, 0);
            rect.AddVertexAt(3, new Point2d(-hl, hw), 0, 0, 0);
            rect.Closed = true;
            rect.ColorIndex = 0; // ByBlock
            btr.AppendEntity(rect);

            Line d1 = new Line(new Point3d(-hl, -hw, 0), new Point3d(hl, hw, 0));
            d1.ColorIndex = 0;
            Line d2 = new Line(new Point3d(-hl, hw, 0), new Point3d(hl, -hw, 0));
            d2.ColorIndex = 0;
            btr.AppendEntity(d1);
            btr.AppendEntity(d2);

            bt.UpgradeOpen();
            ObjectId defId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            return defId;
        }

        private static List<RoomPolygonInfo> ExtractClosedRoomsFromModelSpace(
            Document doc,
            Database db,
            BlockTableRecord ms,
            Transaction tr,
            Dictionary<string, bool> layerHiddenFlags,
            double unitsPerM,
            Dictionary<string, object> args)
        {
            string roomLayerFilter = GetStr(args, "room_layer") ?? GetStr(args, "layer_filter");
            bool selectedOnly = GetBool(args, "selected_only", false);
            List<string> explicitHandles = ExtractStringListArg(args, "handles");
            HashSet<string> targetSet = new HashSet<string>(explicitHandles, StringComparer.OrdinalIgnoreCase);
            if (selectedOnly && explicitHandles.Count == 0)
            {
                List<string> sel = GetSelectedHandleStrings(doc);
                for (int i = 0; i < sel.Count; i++) targetSet.Add(sel[i]);
            }

            List<NearbyTextItem> textsM = new List<NearbyTextItem>();
            List<RoomPolygonInfo> rooms = new List<RoomPolygonInfo>();

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;
                if (!IsEntityVisible(ent, layerHiddenFlags)) continue;

                if (ent is DBText || ent is MText)
                {
                    string txt = ExtractEntityTextString(ent);
                    if (string.IsNullOrWhiteSpace(txt)) continue;
                    try
                    {
                        Extents3d ex = ent.GeometricExtents;
                        if (!IsFiniteBounds(ex)) continue;
                        textsM.Add(new NearbyTextItem
                        {
                            Handle = ent.Handle.ToString(),
                            Text = txt,
                            Cx = ((ex.MinPoint.X + ex.MaxPoint.X) * 0.5) / unitsPerM,
                            Cy = ((ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5) / unitsPerM,
                            Bounds = ex
                        });
                    }
                    catch { }
                    continue;
                }

                Polyline pl = ent as Polyline;
                if (pl == null || !pl.Closed || pl.NumberOfVertices < 3) continue;
                if ((selectedOnly || explicitHandles.Count > 0) && !targetSet.Contains(ent.Handle.ToString())) continue;
                if (!string.IsNullOrWhiteSpace(roomLayerFilter) && !MatchesPatternOrSubstring(ent.Layer ?? "", roomLayerFilter)) continue;

                double rawArea = 0;
                try { rawArea = pl.Area; }
                catch { rawArea = 0; }
                double areaM2 = rawArea / (unitsPerM * unitsPerM);
                if (areaM2 < 0.8 || areaM2 > 25000.0) continue;

                RoomPolygonInfo rm = new RoomPolygonInfo();
                rm.Handle = ent.Handle.ToString();
                rm.AreaM2 = areaM2;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                double sumX = 0, sumY = 0;
                for (int v = 0; v < pl.NumberOfVertices; v++)
                {
                    Point2d p = pl.GetPoint2dAt(v);
                    double xm = p.X / unitsPerM;
                    double ym = p.Y / unitsPerM;
                    rm.VerticesM.Add(new Point2d(xm, ym));
                    if (xm < minX) minX = xm;
                    if (ym < minY) minY = ym;
                    if (xm > maxX) maxX = xm;
                    if (ym > maxY) maxY = ym;
                    sumX += xm;
                    sumY += ym;
                }
                rm.MinXm = minX; rm.MinYm = minY; rm.MaxXm = maxX; rm.MaxYm = maxY;
                rm.CentroidXm = sumX / pl.NumberOfVertices;
                rm.CentroidYm = sumY / pl.NumberOfVertices;
                rooms.Add(rm);
            }

            // Sort top-to-bottom / left-to-right cleanly and assign room names from inner text labels
            rooms.Sort(delegate(RoomPolygonInfo a, RoomPolygonInfo b)
            {
                int cy = b.CentroidYm.CompareTo(a.CentroidYm);
                return cy != 0 ? cy : a.CentroidXm.CompareTo(b.CentroidXm);
            });

            for (int r = 0; r < rooms.Count; r++)
            {
                RoomPolygonInfo rm = rooms[r];
                rm.Index = r + 1;
                rm.Id = "R" + rm.Index.ToString(CultureInfo.InvariantCulture);
                string bestLabel = "";
                double bestDist = double.MaxValue;
                for (int t = 0; t < textsM.Count; t++)
                {
                    NearbyTextItem nt = textsM[t];
                    if (IsPointInPolygon2D(rm.VerticesM, nt.Cx, nt.Cy))
                    {
                        double d = Math.Sqrt(Math.Pow(nt.Cx - rm.CentroidXm, 2) + Math.Pow(nt.Cy - rm.CentroidYm, 2));
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestLabel = nt.Text;
                        }
                    }
                }
                rm.Name = !string.IsNullOrWhiteSpace(bestLabel) ? bestLabel : ("Room_" + rm.Index.ToString(CultureInfo.InvariantCulture));
            }
            return rooms;
        }

        private static string SanitizeStfValue(string val, string fallback)
        {
            string s = (val ?? "").Replace("\r", " ").Replace("\n", " ").Replace("[", "(").Replace("]", ")").Replace("=", "-").Trim();
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        private static string WriteDialuxStfFile(
            string stfPath,
            string projectName,
            List<RoomPolygonInfo> rooms,
            List<List<double[]>> luminairesPerRoomM, // [xM, yM, zM, rotDeg]
            PhotometricProfile profile,
            double roomHeightM,
            double workplaneM,
            double reflCeil,
            double reflWalls,
            double reflFloor,
            double mf)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[VERSION]\r\n");
            sb.Append("STFF=1.0.5\r\n");
            sb.Append("Progname=AIEngineer.AutoCAD\r\n");
            sb.Append("Progvers=2.0\r\n\r\n");

            sb.Append("[PROJECT]\r\n");
            sb.Append("Name=" + SanitizeStfValue(projectName, "AutoCAD_Lighting_Project") + "\r\n");
            sb.Append("Date=" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\r\n");
            sb.Append("Operator=AI-Engineer\r\n");
            sb.Append("NrRooms=" + rooms.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
            for (int r = 0; r < rooms.Count; r++)
                sb.Append("Room" + (r + 1).ToString(CultureInfo.InvariantCulture) + "=ROOM.R" + (r + 1).ToString(CultureInfo.InvariantCulture) + "\r\n");
            sb.Append("\r\n");

            string lumSection = "LUMINAIRE.L1";
            sb.Append("[" + lumSection + "]\r\n");
            sb.Append("Manufacturer=" + SanitizeStfValue(profile.Manufacturer, "Generic") + "\r\n");
            sb.Append("OrderNr=" + SanitizeStfValue(profile.CatalogName, "LED-36W") + "\r\n");
            sb.Append("Name=" + SanitizeStfValue(profile.CatalogName, "LED_PANEL_60x60") + "\r\n");
            if (!string.IsNullOrWhiteSpace(profile.SourceFile))
                sb.Append("Photometry=" + SanitizeStfValue(Path.GetFileName(profile.SourceFile), "") + "\r\n");
            sb.Append("Box=" + profile.LengthM.ToString("0.000", CultureInfo.InvariantCulture) + " " +
                          profile.WidthM.ToString("0.000", CultureInfo.InvariantCulture) + " " +
                          profile.HeightM.ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
            sb.Append("Shape=1\r\n");
            sb.Append("Load=" + profile.Watts.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n");
            sb.Append("Flux=" + profile.TotalLumens.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n\r\n");

            for (int r = 0; r < rooms.Count; r++)
            {
                RoomPolygonInfo rm = rooms[r];
                List<double[]> lums = (luminairesPerRoomM != null && r < luminairesPerRoomM.Count) ? luminairesPerRoomM[r] : new List<double[]>();

                sb.Append("[ROOM.R" + (r + 1).ToString(CultureInfo.InvariantCulture) + "]\r\n");
                sb.Append("Name=" + SanitizeStfValue(rm.Name, "Room_" + (r + 1).ToString(CultureInfo.InvariantCulture)) + "\r\n");
                sb.Append("Height=" + roomHeightM.ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("WorkingPlane=" + workplaneM.ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("MF=" + mf.ToString("0.00", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("R_Ceiling=" + reflCeil.ToString("0.00", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("R_Floor=" + reflFloor.ToString("0.00", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("NrPoints=" + rm.VerticesM.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
                for (int v = 0; v < rm.VerticesM.Count; v++)
                {
                    sb.Append("Point" + (v + 1).ToString(CultureInfo.InvariantCulture) + "=" +
                              rm.VerticesM[v].X.ToString("0.000", CultureInfo.InvariantCulture) + " " +
                              rm.VerticesM[v].Y.ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                    sb.Append("R_Wall" + (v + 1).ToString(CultureInfo.InvariantCulture) + "=" +
                              reflWalls.ToString("0.00", CultureInfo.InvariantCulture) + "\r\n");
                }
                sb.Append("NrStruct=0\r\n");
                sb.Append("NrLums=" + lums.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
                for (int k = 0; k < lums.Count; k++)
                {
                    double[] lp = lums[k];
                    string pfx = "Lum" + (k + 1).ToString(CultureInfo.InvariantCulture);
                    sb.Append(pfx + "=" + lumSection + "\r\n");
                    sb.Append(pfx + ".Pos=" + lp[0].ToString("0.000", CultureInfo.InvariantCulture) + " " +
                              lp[1].ToString("0.000", CultureInfo.InvariantCulture) + " " +
                              lp[2].ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                    sb.Append(pfx + ".Rot=0.000 0.000 " + lp[3].ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                }
                sb.Append("\r\n");
            }

            string fullStf = Path.GetFullPath(stfPath);
            string dir = Path.GetDirectoryName(fullStf);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullStf, sb.ToString(), new UTF8Encoding(false));
            return fullStf;
        }

        private static Dictionary<string, object> LightingDesignEngine(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            string standard = (GetStr(args, "standard") ?? "ECP").Trim().ToUpperInvariant();
            string iesPath = GetStr(args, "ies_path") ?? GetStr(args, "ldt_path");
            if (GetBool(args, "browse_ies", false))
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "اختر ملف الفوتومتري للكشاف (Select IES / EULUMDAT Photometry)";
                    dlg.Filter = "Photometry Files (*.ies;*.ldt)|*.ies;*.ldt|All Files (*.*)|*.*";
                    if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.FileName))
                        iesPath = dlg.FileName;
                }
            }

            double reqLumens = GetDouble(args, "fixture_lumens", 3600.0);
            double reqWatts = GetDouble(args, "fixture_watts", 36.0);
            string fixtureName = GetStr(args, "block_name") ?? "LED_PANEL_60x60";
            PhotometricProfile phot = ParsePhotometricFile(iesPath, reqLumens, reqWatts, fixtureName);

            double? explicitLux = GetNullableDouble(args, "target_lux");
            double roomH = Math.Max(1.8, GetDouble(args, "room_height_m", 3.0));
            double workplaneM = Math.Max(0.0, Math.Min(roomH - 0.5, GetDouble(args, "workplane_m", 0.80)));
            double mountH = Math.Max(workplaneM + 0.5, GetDouble(args, "mounting_height_m", roomH));
            double mf = Math.Max(0.4, Math.Min(1.0, GetDouble(args, "maintenance_factor", 0.80)));
            double reflCeil = Math.Max(0.1, Math.Min(0.9, GetDouble(args, "reflectance_ceiling", 0.70)));
            double reflWall = Math.Max(0.1, Math.Min(0.85, GetDouble(args, "reflectance_walls", 0.50)));
            double reflFloor = Math.Max(0.05, Math.Min(0.5, GetDouble(args, "reflectance_floor", 0.20)));

            bool drawFixtures = GetBool(args, "draw_fixtures", true);
            bool balanceCircuits = GetBool(args, "balance_circuits", true);
            bool stampLabels = GetBool(args, "stamp_labels", true);
            bool drawTable = GetBool(args, "draw_table", true);
            bool exportExcel = GetBool(args, "export_excel", false);
            bool exportStf = GetBool(args, "export_stf", false);
            bool replaceExisting = GetBool(args, "replace_existing", true);
            bool zoom = GetBool(args, "zoom", true);
            string fixLayerReq = GetStr(args, "fixture_layer") ?? "E-LTG-FIXT";

            double unitsPerM = Convert.ToDouble(GetUnitsInfo(db)["units_per_m"], CultureInfo.InvariantCulture);
            if (unitsPerM <= 1e-9) unitsPerM = 1000.0;
            double unitsPerCm = Convert.ToDouble(GetUnitsInfo(db)["units_per_cm"], CultureInfo.InvariantCulture);

            List<RoomPolygonInfo> rooms = null;
            List<List<double[]>> fixturesPerRoomM = new List<List<double[]>>();
            List<Dictionary<string, object>> roomSummaries = new List<Dictionary<string, object>>();
            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();
            Extents3d? mainDrawingExt = null;
            Extents3d? tableExt = null;
            int totalFixturesPlaced = 0;
            double totalWattsAll = 0;

            bool switchedToModel = false;
            try { if (!db.TileMode) { db.TileMode = true; switchedToModel = true; } } catch { }

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);

                    rooms = ExtractClosedRoomsFromModelSpace(doc, db, ms, tr, layerHiddenFlags, unitsPerM, args);
                    if (rooms.Count == 0)
                    {
                        tr.Commit();
                        return Err("لم يتم العثور على غرف مغلقة (Closed Polylines >= 0.8 m²) في الرسم لحساب وتوزيع الإضاءة عليها.");
                    }

                    foreach (ObjectId id in ms)
                    {
                        Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (e == null || e.IsErased || IsAiAnnotationLayer(e.Layer)) continue;
                        try
                        {
                            Extents3d ex = e.GeometricExtents;
                            if (!IsFiniteBounds(ex)) continue;
                            if (!mainDrawingExt.HasValue) mainDrawingExt = ex;
                            else { Extents3d m = mainDrawingExt.Value; m.AddExtents(ex); mainDrawingExt = m; }
                        }
                        catch { }
                    }

                    string fixLayer = EnsureLayerSafe(db, tr, fixLayerReq, 2, "Continuous");
                    string wireLayer = EnsureLayerSafe(db, tr, "E-LTG-WIRE", 1, "Continuous");
                    string lblLayer = EnsureLayerSafe(db, tr, "_AI_LIGHTING_LABELS", 3, "Continuous");

                    if (replaceExisting && drawFixtures)
                    {
                        foreach (ObjectId oid in ms)
                        {
                            Entity oe = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                            if (oe == null || oe.IsErased) continue;
                            bool isOldFix = oe is BlockReference && string.Equals(oe.Layer, fixLayer, StringComparison.OrdinalIgnoreCase);
                            bool isOldLbl = string.Equals(oe.Layer, lblLayer, StringComparison.OrdinalIgnoreCase);
                            bool isOldWire = string.Equals(oe.Layer, wireLayer, StringComparison.OrdinalIgnoreCase);
                            if (isOldFix || isOldLbl || isOldWire)
                            {
                                oe.UpgradeOpen();
                                oe.Erase(true);
                                erasedIds.Add(oid);
                            }
                        }
                    }

                    ObjectId fixDefId = drawFixtures
                        ? EnsureLuminaireBlockDef(db, tr, bt, SanitizeSymbolName(fixtureName), phot.WidthM * unitsPerM, phot.LengthM * unitsPerM)
                        : ObjectId.Null;
                    ObjectId uStyle = EnsureUnicodeTextStyle(db, tr);

                    ExcelSheetData calcSheet = new ExcelSheetData
                    {
                        Title = "جدول حسابات وتوزيع الإضاءة (" + standard + ") - " + phot.CatalogName,
                        SheetName = "Lighting_Calculations",
                        Headers = new List<string> { "م", "الغرفة", "المساحة (m²)", "الكود المستهدف (lx)", "عدد الكشافات", "الشبكة", "متوسط الإضاءة (lx)", "التجانس U0", "القدرة (W)", "الحالة" }
                    };

                    double hm = Math.Max(0.5, mountH - workplaneM);
                    double lblH = Math.Max(0.35 * unitsPerCm, Math.Min(3.5 * unitsPerCm, (mainDrawingExt.HasValue ? Math.Max(mainDrawingExt.Value.MaxPoint.X - mainDrawingExt.Value.MinPoint.X, 100.0) : 100.0) * 0.008));

                    for (int r = 0; r < rooms.Count; r++)
                    {
                        RoomPolygonInfo rm = rooms[r];
                        LightingStandardRule rule = ResolveLightingStandard(rm.Name, standard, explicitLux);

                        double lenM = Math.Max(0.8, rm.MaxXm - rm.MinXm);
                        double widM = Math.Max(0.8, rm.MaxYm - rm.MinYm);
                        double kIndex = (lenM * widM) / (hm * (lenM + widM));
                        double cu = EstimateUtilizationFactor(kIndex, reflCeil, reflWall, reflFloor, phot.DownwardFraction);

                        // Step 1: Lumen Method required fixture count
                        int reqCount = Math.Max(1, (int)Math.Ceiling((rule.TargetLux * rm.AreaM2) / (phot.TotalLumens * cu * mf)));

                        // Step 2: Symmetric architectural grid aspect-matched to bounding box, filtering to interior points
                        double aspect = lenM / widM;
                        int nx = Math.Max(1, (int)Math.Round(Math.Sqrt(reqCount * aspect)));
                        int ny = Math.Max(1, (int)Math.Ceiling((double)reqCount / nx));

                        List<double[]> placedM = new List<double[]>();
                        // Try symmetric grid; if L-shaped room clips too many points, step density slightly up until inside count >= reqCount
                        for (int attempt = 0; attempt < 4; attempt++)
                        {
                            placedM.Clear();
                            double sx = lenM / nx;
                            double sy = widM / ny;
                            for (int iy = 0; iy < ny; iy++)
                            {
                                double py = rm.MinYm + sy * (iy + 0.5);
                                for (int ix = 0; ix < nx; ix++)
                                {
                                    double px = rm.MinXm + sx * (ix + 0.5);
                                    if (IsPointInPolygon2D(rm.VerticesM, px, py))
                                        placedM.Add(new double[] { px, py, mountH, 0.0 });
                                }
                            }
                            if (placedM.Count >= reqCount || placedM.Count == nx * ny) break;
                            nx++; ny++;
                        }
                        if (placedM.Count == 0)
                            placedM.Add(new double[] { rm.CentroidXm, rm.CentroidYm, mountH, 0.0 });

                        fixturesPerRoomM.Add(placedM);

                        // Step 3: Point-by-Point Illuminance Grid evaluation inside room polygon
                        int evalNx = Math.Max(5, Math.Min(14, (int)Math.Ceiling(lenM / 0.75)));
                        int evalNy = Math.Max(5, Math.Min(14, (int)Math.Ceiling(widM / 0.75)));
                        double sumE = 0.0, minE = double.MaxValue, maxE = 0.0;
                        int evalPts = 0;
                        double lumenIndirect = (placedM.Count * phot.TotalLumens * Math.Max(0.05, cu - 0.35 * phot.DownwardFraction) * mf) / Math.Max(1.0, rm.AreaM2);

                        for (int gy = 0; gy < evalNy; gy++)
                        {
                            double y = rm.MinYm + (widM > 0 ? ((rm.MaxYm - rm.MinYm) * (gy + 0.5) / evalNy) : 0);
                            for (int gx = 0; gx < evalNx; gx++)
                            {
                                double x = rm.MinXm + (rm.MaxXm - rm.MinXm) * (gx + 0.5) / evalNx;
                                if (!IsPointInPolygon2D(rm.VerticesM, x, y)) continue;
                                double eDirect = 0.0;
                                for (int fi = 0; fi < placedM.Count; fi++)
                                {
                                    double dx = x - placedM[fi][0];
                                    double dy = y - placedM[fi][1];
                                    double dSq = dx * dx + dy * dy + hm * hm;
                                    double d = Math.Sqrt(dSq);
                                    double cosTheta = hm / d;
                                    double thetaDeg = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosTheta))) * 180.0 / Math.PI;
                                    double phiDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                                    double cd = SampleCandela(phot, thetaDeg, phiDeg);
                                    eDirect += (cd * cosTheta / dSq) * mf;
                                }
                                double ePt = eDirect + lumenIndirect;
                                sumE += ePt;
                                if (ePt < minE) minE = ePt;
                                if (ePt > maxE) maxE = ePt;
                                evalPts++;
                            }
                        }

                        double avgLux = evalPts > 0 ? (sumE / evalPts) : ((placedM.Count * phot.TotalLumens * cu * mf) / rm.AreaM2);
                        if (evalPts == 0) minE = avgLux * 0.7;
                        double u0 = avgLux > 1e-6 ? Math.Min(1.0, minE / avgLux) : 0.0;
                        double roomWatts = placedM.Count * phot.Watts;
                        double lpd = roomWatts / Math.Max(0.5, rm.AreaM2);
                        bool passCode = avgLux >= rule.TargetLux * 0.90 && u0 >= rule.MinUniformity * 0.85;

                        totalFixturesPlaced += placedM.Count;
                        totalWattsAll += roomWatts;

                        // Step 4: Place real CAD fixture blocks + 3-phase balanced wiring + room summary label
                        if (drawFixtures)
                        {
                            List<Point2d>[] phasePts = new List<Point2d>[] { new List<Point2d>(), new List<Point2d>(), new List<Point2d>() };
                            short[] phaseColors = new short[] { 1, 2, 5 }; // L1=Red, L2=Yellow, L3=Blue
                            for (int fi = 0; fi < placedM.Count; fi++)
                            {
                                double fx = placedM[fi][0] * unitsPerM;
                                double fy = placedM[fi][1] * unitsPerM;
                                BlockReference br = new BlockReference(new Point3d(fx, fy, 0.0), fixDefId);
                                br.Layer = fixLayer;
                                int ph = fi % 3;
                                if (balanceCircuits) br.ColorIndex = phaseColors[ph];
                                ms.AppendEntity(br);
                                tr.AddNewlyCreatedDBObject(br, true);
                                createdIds.Add(br.ObjectId);
                                phasePts[ph].Add(new Point2d(fx, fy));
                            }

                            if (balanceCircuits)
                            {
                                for (int ph = 0; ph < 3; ph++)
                                {
                                    if (phasePts[ph].Count >= 2)
                                    {
                                        Polyline wPl = new Polyline();
                                        wPl.Layer = wireLayer;
                                        wPl.ColorIndex = phaseColors[ph];
                                        for (int wi = 0; wi < phasePts[ph].Count; wi++)
                                            wPl.AddVertexAt(wi, phasePts[ph][wi], 0.18, 0.0, 0.0);
                                        ms.AppendEntity(wPl);
                                        tr.AddNewlyCreatedDBObject(wPl, true);
                                        createdIds.Add(wPl.ObjectId);
                                    }
                                }
                            }
                        }

                        if (stampLabels)
                        {
                            string lblText = rm.Name + " (" + placedM.Count.ToString(CultureInfo.InvariantCulture) + " pcs)" +
                                             "\\PEavg: " + avgLux.ToString("0", CultureInfo.InvariantCulture) + " lx | U0: " + u0.ToString("0.00", CultureInfo.InvariantCulture);
                            MText mt = new MText();
                            mt.SetDatabaseDefaults(db);
                            mt.Layer = lblLayer;
                            mt.ColorIndex = passCode ? (short)3 : (short)2;
                            mt.TextStyleId = uStyle;
                            mt.TextHeight = lblH;
                            mt.Attachment = AttachmentPoint.MiddleCenter;
                            mt.Location = new Point3d(rm.CentroidXm * unitsPerM, rm.CentroidYm * unitsPerM, 0.0);
                            mt.Contents = lblText;
                            ms.AppendEntity(mt);
                            tr.AddNewlyCreatedDBObject(mt, true);
                            createdIds.Add(mt.ObjectId);
                        }

                        calcSheet.Rows.Add(new List<string>
                        {
                            rm.Index.ToString(CultureInfo.InvariantCulture),
                            rm.Name,
                            rm.AreaM2.ToString("0.00", CultureInfo.InvariantCulture),
                            rule.TargetLux.ToString("0", CultureInfo.InvariantCulture),
                            placedM.Count.ToString(CultureInfo.InvariantCulture),
                            nx.ToString(CultureInfo.InvariantCulture) + "x" + ny.ToString(CultureInfo.InvariantCulture),
                            avgLux.ToString("0", CultureInfo.InvariantCulture),
                            u0.ToString("0.00", CultureInfo.InvariantCulture),
                            roomWatts.ToString("0.#", CultureInfo.InvariantCulture),
                            passCode ? "PASS" : "REVIEW"
                        });

                        roomSummaries.Add(new Dictionary<string, object>
                        {
                            { "room_id", rm.Id },
                            { "room_name", rm.Name },
                            { "space_category", rule.SpaceCategory },
                            { "area_m2", Math.Round(rm.AreaM2, 2) },
                            { "target_lux", rule.TargetLux },
                            { "fixtures_count", placedM.Count },
                            { "grid", nx + "x" + ny },
                            { "avg_lux", Math.Round(avgLux, 1) },
                            { "min_lux", Math.Round(minE, 1) },
                            { "uniformity_u0", Math.Round(u0, 2) },
                            { "watts", Math.Round(roomWatts, 1) },
                            { "lpd_w_m2", Math.Round(lpd, 2) },
                            { "cu_estimated", Math.Round(cu, 2) },
                            { "status", passCode ? "PASS" : "REVIEW" }
                        });
                    }

                    calcSheet.FooterRow = new List<string>
                    {
                        "", "الإجمالي",
                        "", "",
                        totalFixturesPlaced.ToString(CultureInfo.InvariantCulture),
                        "", "", "",
                        totalWattsAll.ToString("0.#", CultureInfo.InvariantCulture) + " W",
                        ""
                    };

                    lock (s_lastCoordLock)
                    {
                        s_lastLightingTableByDb[db.UnmanagedObject] = calcSheet;
                        s_lastGeneratedTableKindByDb[db.UnmanagedObject] = "lighting";
                    }

                    if (drawTable)
                    {
                        tableExt = RenderStyledScheduleTable(
                            doc, db, ms, tr, calcSheet, "_AI_LIGHTING_TABLE",
                            replaceExisting, GetStr(args, "table_position") ?? "outside_right",
                            GetNullableDouble(args, "table_gap"), null, mainDrawingExt,
                            createdIds, erasedIds);
                    }

                    db.UpdateExt(true);
                    tr.Commit();
                }

                if (createdIds.Count > 0 || erasedIds.Count > 0)
                {
                    CadUndoEntry undo = new CadUndoEntry();
                    undo.ToolName = "lighting_design_engine";
                    undo.Summary = "Lighting design (" + totalFixturesPlaced.ToString(CultureInfo.InvariantCulture) + " fixtures across " + rooms.Count.ToString(CultureInfo.InvariantCulture) + " rooms)";
                    undo.CreatedIds.AddRange(createdIds);
                    undo.ErasedIds.AddRange(erasedIds);
                    PushUndoEntry(doc, undo);
                }

                if (zoom && tableExt.HasValue && IsFiniteBounds(tableExt.Value))
                    ZoomToExtents3d(doc, tableExt.Value, 1.18);
                else
                    doc.Editor.Regen();
                doc.Editor.UpdateScreen();
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "standard", standard },
                { "luminaire", phot.CatalogName },
                { "lumens_per_fixture", phot.TotalLumens },
                { "watts_per_fixture", phot.Watts },
                { "ies_loaded", !string.IsNullOrEmpty(phot.SourceFile) },
                { "rooms_processed", rooms.Count },
                { "total_fixtures", totalFixturesPlaced },
                { "total_watts", Math.Round(totalWattsAll, 1) },
                { "fixture_layer", fixLayerReq },
                { "created_count", createdIds.Count },
                { "erased_count", erasedIds.Count },
                { "switched_to_model", switchedToModel },
                { "rooms", roomSummaries }
            };

            if (exportExcel)
            {
                ExcelSheetData xlSheet;
                lock (s_lastCoordLock) { s_lastLightingTableByDb.TryGetValue(db.UnmanagedObject, out xlSheet); }
                if (xlSheet != null)
                {
                    string xlTarget = GetStr(args, "excel_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "lighting_calculations", ".xlsx");
                    res["excel_exported"] = true;
                    res["excel_path"] = ExcelTableInterop.WriteSpreadsheet(xlTarget, xlSheet);
                }
            }

            if (exportStf)
            {
                string stfOut = GetStr(args, "stf_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "dialux_project", ".stf");
                string projName = Path.GetFileNameWithoutExtension(doc.Name ?? "Lighting_Project");
                res["stf_exported"] = true;
                res["stf_path"] = WriteDialuxStfFile(stfOut, projName, rooms, fixturesPerRoomM, phot, roomH, workplaneM, reflCeil, reflWall, reflFloor, mf);
            }

            return Ok(res);
        }

        internal sealed class DialuxRoomReviewRow
        {
            public int Index;
            public string RoomName = "";
            public string Handle = "";
            public double AreaM2;
            public string SpaceCategory = "General / Office";
            public double TargetLux = 400.0;
            public double CeilingHeightM = 3.0;
            public double MountingHeightM = 3.0;
            public double WorkplaneM = 0.80;
            public double ReflCeiling = 0.70;
            public double ReflWalls = 0.50;
            public double ReflFloor = 0.20;
            public int FixtureCount;
            public bool HeightConfirmed;
            public string StatusNote = "";
        }

        internal sealed class DialuxLuminaireReviewRow
        {
            public string BlockName = "";
            public int Qty;
            public double Watt = 36.0;
            public double Lumens = 3600.0;
            public double LengthM = 0.60;
            public double WidthM = 0.60;
            public double HeightM = 0.05;
            public string MountingType = "Recessed";
            public string IesPath = "";
            public string Manufacturer = "Generic";
            public PhotometricProfile Photometry;
            public bool HasVerifiedLegendSpec;
            public bool MissingSpec { get { return !HasVerifiedLegendSpec; } }
            public string SpecStatus = "";
        }

        internal sealed class DialuxExportReviewForm : Form
        {
            private readonly DataGridView _roomsGrid = new DataGridView();
            private readonly DataGridView _lumGrid = new DataGridView();
            private readonly TextBox _txtPath = new TextBox();
            private readonly NumericUpDown _numMf = new NumericUpDown();
            private readonly NumericUpDown _numApplyHeight = new NumericUpDown();
            private readonly CheckBox _chkSaveDb = new CheckBox();
            private readonly Label _lblSummary = new Label();
            private readonly List<DialuxRoomReviewRow> _rooms;
            private readonly List<DialuxLuminaireReviewRow> _lums;

            internal DataGridView RoomsGrid { get { return _roomsGrid; } }
            internal DataGridView LuminairesGrid { get { return _lumGrid; } }
            internal string SelectedStfPath { get { return (_txtPath.Text ?? "").Trim(); } }
            internal double SelectedMaintenanceFactor { get { return (double)_numMf.Value; } }
            internal bool SaveToProjectDb { get { return _chkSaveDb.Checked; } }
            internal int MissingCount
            {
                get
                {
                    int c = 0;
                    for (int i = 0; i < _rooms.Count; i++) if (!_rooms[i].HeightConfirmed) c++;
                    for (int i = 0; i < _lums.Count; i++) if (_lums[i].MissingSpec) c++;
                    return c;
                }
            }

            public DialuxExportReviewForm(
                List<DialuxRoomReviewRow> rooms,
                List<DialuxLuminaireReviewRow> luminaires,
                string defaultStfPath,
                double defaultMf)
            {
                _rooms = rooms ?? new List<DialuxRoomReviewRow>();
                _lums = luminaires ?? new List<DialuxLuminaireReviewRow>();

                Text = "AI-Engineer Pro — DIALux Pre-Export Review & Missing Parameters";
                Width = 920;
                Height = 620;
                MinimumSize = new Size(720, 480);
                StartPosition = FormStartPosition.CenterParent;
                BackColor = Color.FromArgb(24, 28, 36);
                ForeColor = Color.FromArgb(230, 237, 243);
                try { Font = new System.Drawing.Font("Segoe UI", 9.5F, FontStyle.Regular); } catch { }

                Panel header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Color.FromArgb(17, 24, 39), Padding = new Padding(12, 8, 12, 8) };
                Panel accent = new Panel { Dock = DockStyle.Top, Height = 3, BackColor = Color.FromArgb(59, 130, 246) };
                Label title = new Label
                {
                    Text = "⚡ مراجعة بيانات DIALux والبنود الناقصة قبل التصدير (.stf)",
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    ForeColor = Color.White
                };
                try { title.Font = new System.Drawing.Font("Segoe UI", 11F, FontStyle.Bold); } catch { }
                _lblSummary.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "الغرف المكتشفة: {0} | أنواع الكشافات: {1} | بنود افتراضية/ناقصة تحتاج تأكيد أو تعديل: {2}",
                    _rooms.Count, _lums.Count, MissingCount);
                _lblSummary.Dock = DockStyle.Top;
                _lblSummary.AutoSize = true;
                _lblSummary.ForeColor = MissingCount > 0 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(74, 222, 128);
                header.Controls.Add(_lblSummary);
                header.Controls.Add(title);

                FlowLayoutPanel topBar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    BackColor = Color.FromArgb(30, 41, 59),
                    Padding = new Padding(10, 6, 10, 6),
                    WrapContents = true
                };
                _numMf.Minimum = 0.40M; _numMf.Maximum = 1.00M; _numMf.DecimalPlaces = 2; _numMf.Increment = 0.05M;
                _numMf.Value = (decimal)Math.Max(0.40, Math.Min(1.00, defaultMf));
                _numMf.Width = 65;

                _numApplyHeight.Minimum = 2.0M; _numApplyHeight.Maximum = 25.0M; _numApplyHeight.DecimalPlaces = 2; _numApplyHeight.Increment = 0.1M;
                _numApplyHeight.Value = _rooms.Count > 0 ? (decimal)Math.Max(2.0, Math.Min(25.0, _rooms[0].CeilingHeightM)) : 3.0M;
                _numApplyHeight.Width = 65;

                Button btnApplyAll = new Button
                {
                    Text = "تطبيق الارتفاع على كل الغرف",
                    AutoSize = true,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(37, 99, 235),
                    ForeColor = Color.White,
                    Margin = new Padding(6, 0, 12, 0)
                };
                btnApplyAll.Click += delegate
                {
                    double hVal = (double)_numApplyHeight.Value;
                    for (int r = 0; r < _roomsGrid.Rows.Count; r++)
                    {
                        _roomsGrid.Rows[r].Cells["CeilingH"].Value = hVal.ToString("0.00", CultureInfo.InvariantCulture);
                        _roomsGrid.Rows[r].Cells["MountH"].Value = hVal.ToString("0.00", CultureInfo.InvariantCulture);
                        _roomsGrid.Rows[r].Cells["Status"].Value = "مؤكد يدوياً";
                        _roomsGrid.Rows[r].Cells["CeilingH"].Style.BackColor = Color.FromArgb(20, 83, 45);
                    }
                };

                Label lblPath = new Label { Text = "ملف STF:", AutoSize = true, Margin = new Padding(4, 6, 4, 2) };
                _txtPath.Text = defaultStfPath ?? "";
                _txtPath.Width = 260;
                _txtPath.BackColor = Color.FromArgb(15, 23, 42);
                _txtPath.ForeColor = Color.White;
                Button btnBrowse = new Button
                {
                    Text = "...",
                    Width = 34,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(51, 65, 85),
                    ForeColor = Color.White
                };
                btnBrowse.Click += delegate
                {
                    using (SaveFileDialog sfd = new SaveFileDialog())
                    {
                        sfd.Filter = "DIALux Interface File (*.stf)|*.stf";
                        sfd.DefaultExt = "stf";
                        sfd.FileName = Path.GetFileName(_txtPath.Text);
                        if (sfd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(sfd.FileName))
                            _txtPath.Text = sfd.FileName;
                    }
                };

                topBar.Controls.Add(new Label { Text = "MF:", AutoSize = true, Margin = new Padding(2, 6, 2, 2) });
                topBar.Controls.Add(_numMf);
                topBar.Controls.Add(new Label { Text = "  ارتفاع السقف (م):", AutoSize = true, Margin = new Padding(6, 6, 2, 2) });
                topBar.Controls.Add(_numApplyHeight);
                topBar.Controls.Add(btnApplyAll);
                topBar.Controls.Add(lblPath);
                topBar.Controls.Add(_txtPath);
                topBar.Controls.Add(btnBrowse);

                TabControl tabs = new TabControl { Dock = DockStyle.Fill };
                TabPage tabRooms = new TabPage("الغرف والارتفاعات / Rooms & Heights");
                TabPage tabLums = new TabPage("الكشافات والليجند / Luminaires & IES");

                StyleGrid(_roomsGrid);
                _roomsGrid.Columns.Add("RoomName", "اسم الغرفة");
                _roomsGrid.Columns.Add("AreaM2", "المساحة m2");
                _roomsGrid.Columns.Add("SpaceType", "نوع الغرفة");
                _roomsGrid.Columns.Add("CeilingH", "ارتفاع السقف (م)");
                _roomsGrid.Columns.Add("MountH", "ارتفاع الكشاف (م)");
                _roomsGrid.Columns.Add("Workplane", "مستوى العمل (م)");
                _roomsGrid.Columns.Add("TargetLux", "Lux المطلوب");
                _roomsGrid.Columns.Add("FixCount", "الكشافات");
                _roomsGrid.Columns.Add("Status", "الحالة");
                _roomsGrid.Columns["AreaM2"].ReadOnly = true;
                _roomsGrid.Columns["FixCount"].ReadOnly = true;
                _roomsGrid.Columns["Status"].ReadOnly = true;

                for (int i = 0; i < _rooms.Count; i++)
                {
                    DialuxRoomReviewRow rr = _rooms[i];
                    int rowIdx = _roomsGrid.Rows.Add(
                        rr.RoomName,
                        rr.AreaM2.ToString("0.##", CultureInfo.InvariantCulture),
                        rr.SpaceCategory,
                        rr.CeilingHeightM.ToString("0.00", CultureInfo.InvariantCulture),
                        rr.MountingHeightM.ToString("0.00", CultureInfo.InvariantCulture),
                        rr.WorkplaneM.ToString("0.00", CultureInfo.InvariantCulture),
                        rr.TargetLux.ToString("0", CultureInfo.InvariantCulture),
                        rr.FixtureCount.ToString(CultureInfo.InvariantCulture),
                        rr.StatusNote);
                    if (!rr.HeightConfirmed)
                        _roomsGrid.Rows[rowIdx].Cells["CeilingH"].Style.BackColor = Color.FromArgb(120, 53, 15);
                }

                StyleGrid(_lumGrid);
                _lumGrid.Columns.Add("BlockName", "اسم البلوك");
                _lumGrid.Columns.Add("Qty", "الكمية");
                _lumGrid.Columns.Add("Watt", "القدرة (W)");
                _lumGrid.Columns.Add("Lumens", "اللومن (lm)");
                _lumGrid.Columns.Add("MountType", "التركيب");
                _lumGrid.Columns.Add("IesPath", "ملف .ies / .ldt");
                _lumGrid.Columns.Add("SpecStatus", "مصدر المواصفة");
                _lumGrid.Columns["BlockName"].ReadOnly = true;
                _lumGrid.Columns["Qty"].ReadOnly = true;
                _lumGrid.Columns["SpecStatus"].ReadOnly = true;

                for (int i = 0; i < _lums.Count; i++)
                {
                    DialuxLuminaireReviewRow lr = _lums[i];
                    int rowIdx = _lumGrid.Rows.Add(
                        lr.BlockName,
                        lr.Qty.ToString(CultureInfo.InvariantCulture),
                        lr.Watt.ToString("0.##", CultureInfo.InvariantCulture),
                        lr.Lumens.ToString("0", CultureInfo.InvariantCulture),
                        lr.MountingType,
                        lr.IesPath,
                        lr.SpecStatus);
                    if (lr.MissingSpec)
                        _lumGrid.Rows[rowIdx].Cells["Watt"].Style.BackColor = Color.FromArgb(127, 29, 29);
                }

                tabRooms.Controls.Add(_roomsGrid);
                tabLums.Controls.Add(_lumGrid);
                tabs.TabPages.Add(tabRooms);
                tabs.TabPages.Add(tabLums);

                Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.FromArgb(17, 24, 39), Padding = new Padding(12, 8, 12, 8) };
                _chkSaveDb.Text = "حفظ الارتفاعات والمواصفات في قاعدة بيانات المشروع";
                _chkSaveDb.Checked = true;
                _chkSaveDb.AutoSize = true;
                _chkSaveDb.Location = new Point(12, 14);

                Button btnOk = new Button
                {
                    Text = "✅ اعتماد وتصدير DIALux (.stf)",
                    DialogResult = DialogResult.OK,
                    AutoSize = true,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(22, 101, 52),
                    ForeColor = Color.White,
                    Anchor = AnchorStyles.Right | AnchorStyles.Top,
                    Location = new Point(660, 9)
                };
                btnOk.Click += delegate { ApplyGridsBackToModels(); Close(); };

                Button btnCancel = new Button
                {
                    Text = "إلغاء",
                    DialogResult = DialogResult.Cancel,
                    AutoSize = true,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(71, 85, 105),
                    ForeColor = Color.White,
                    Anchor = AnchorStyles.Right | AnchorStyles.Top,
                    Location = new Point(580, 9)
                };

                footer.Controls.Add(_chkSaveDb);
                footer.Controls.Add(btnCancel);
                footer.Controls.Add(btnOk);

                Controls.Add(tabs);
                Controls.Add(topBar);
                Controls.Add(header);
                Controls.Add(accent);
                Controls.Add(footer);
                AcceptButton = btnOk;
                CancelButton = btnCancel;
            }

            private static void StyleGrid(DataGridView g)
            {
                g.Dock = DockStyle.Fill;
                g.AllowUserToAddRows = false;
                g.AllowUserToDeleteRows = false;
                g.RowHeadersVisible = false;
                g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                g.BackgroundColor = Color.FromArgb(19, 23, 32);
                g.GridColor = Color.FromArgb(51, 65, 85);
                g.DefaultCellStyle.BackColor = Color.FromArgb(26, 32, 44);
                g.DefaultCellStyle.ForeColor = Color.FromArgb(230, 237, 243);
                g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(37, 99, 235);
                g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
                g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
                g.EnableHeadersVisualStyles = false;
            }

            internal void ApplyGridsBackToModels()
            {
                for (int i = 0; i < _rooms.Count && i < _roomsGrid.Rows.Count; i++)
                {
                    DataGridViewRow row = _roomsGrid.Rows[i];
                    _rooms[i].RoomName = Convert.ToString(row.Cells["RoomName"].Value, CultureInfo.InvariantCulture) ?? _rooms[i].RoomName;
                    _rooms[i].SpaceCategory = Convert.ToString(row.Cells["SpaceType"].Value, CultureInfo.InvariantCulture) ?? _rooms[i].SpaceCategory;
                    double v;
                    if (double.TryParse(Convert.ToString(row.Cells["CeilingH"].Value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v) && v >= 1.8)
                        _rooms[i].CeilingHeightM = v;
                    if (double.TryParse(Convert.ToString(row.Cells["MountH"].Value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v) && v >= 1.5)
                        _rooms[i].MountingHeightM = Math.Min(_rooms[i].CeilingHeightM, v);
                    if (double.TryParse(Convert.ToString(row.Cells["Workplane"].Value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v) && v >= 0.0)
                        _rooms[i].WorkplaneM = v;
                    if (double.TryParse(Convert.ToString(row.Cells["TargetLux"].Value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v) && v > 10.0)
                        _rooms[i].TargetLux = v;
                    _rooms[i].HeightConfirmed = true;
                }

                for (int i = 0; i < _lums.Count && i < _lumGrid.Rows.Count; i++)
                {
                    DataGridViewRow row = _lumGrid.Rows[i];
                    double v;
                    if (double.TryParse(Convert.ToString(row.Cells["Watt"].Value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v) && v > 0.0)
                        _lums[i].Watt = v;
                    if (double.TryParse(Convert.ToString(row.Cells["Lumens"].Value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out v) && v > 0.0)
                        _lums[i].Lumens = v;
                    _lums[i].MountingType = Convert.ToString(row.Cells["MountType"].Value, CultureInfo.InvariantCulture) ?? _lums[i].MountingType;
                    string ies = (Convert.ToString(row.Cells["IesPath"].Value, CultureInfo.InvariantCulture) ?? "").Trim();
                    _lums[i].IesPath = ies;
                    if (!string.IsNullOrEmpty(ies) && File.Exists(ies))
                        _lums[i].Photometry = ParsePhotometricFile(ies, _lums[i].Lumens, _lums[i].Watt, _lums[i].BlockName);
                    else if (_lums[i].Photometry != null)
                    {
                        _lums[i].Photometry.Watts = _lums[i].Watt;
                        _lums[i].Photometry.TotalLumens = _lums[i].Lumens;
                    }
                    _lums[i].HasVerifiedLegendSpec = _lums[i].Watt > 0.0;
                }
            }
        }

        private static string WriteDialuxStfFile(
            string stfPath,
            string projectName,
            List<RoomPolygonInfo> rooms,
            List<List<KeyValuePair<string, double[]>>> luminairesPerRoom,
            Dictionary<string, DialuxLuminaireReviewRow> lumDefs,
            List<DialuxRoomReviewRow> roomRows,
            double mf)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[VERSION]\r\n");
            sb.Append("STFF=1.0.5\r\n");
            sb.Append("Progname=AIEngineer.AutoCAD\r\n");
            sb.Append("Progvers=2.0\r\n\r\n");

            sb.Append("[PROJECT]\r\n");
            sb.Append("Name=" + SanitizeStfValue(projectName, "AutoCAD_Lighting_Project") + "\r\n");
            sb.Append("Date=" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\r\n");
            sb.Append("Operator=AI-Engineer\r\n");
            sb.Append("NrRooms=" + rooms.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
            for (int r = 0; r < rooms.Count; r++)
                sb.Append("Room" + (r + 1).ToString(CultureInfo.InvariantCulture) + "=ROOM.R" + (r + 1).ToString(CultureInfo.InvariantCulture) + "\r\n");
            sb.Append("\r\n");

            Dictionary<string, string> blockToSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<DialuxLuminaireReviewRow> orderedDefs = new List<DialuxLuminaireReviewRow>(lumDefs.Values);
            if (orderedDefs.Count == 0)
            {
                orderedDefs.Add(new DialuxLuminaireReviewRow { BlockName = "LED_Fixture", Watt = 36.0, Lumens = 3600.0 });
            }
            for (int li = 0; li < orderedDefs.Count; li++)
            {
                DialuxLuminaireReviewRow def = orderedDefs[li];
                string sec = "LUMINAIRE.L" + (li + 1).ToString(CultureInfo.InvariantCulture);
                blockToSection[def.BlockName] = sec;
                PhotometricProfile prof = def.Photometry;
                sb.Append("[" + sec + "]\r\n");
                sb.Append("Manufacturer=" + SanitizeStfValue(prof != null ? prof.Manufacturer : def.Manufacturer, "Generic") + "\r\n");
                sb.Append("OrderNr=" + SanitizeStfValue(def.BlockName, "LED-36W") + "\r\n");
                sb.Append("Name=" + SanitizeStfValue(def.BlockName, "LED_Fixture") + "\r\n");
                if (!string.IsNullOrWhiteSpace(def.IesPath))
                    sb.Append("Photometry=" + SanitizeStfValue(Path.GetFileName(def.IesPath), "") + "\r\n");
                double boxL = prof != null && prof.LengthM > 0 ? prof.LengthM : def.LengthM;
                double boxW = prof != null && prof.WidthM > 0 ? prof.WidthM : def.WidthM;
                double boxH = prof != null && prof.HeightM > 0 ? prof.HeightM : def.HeightM;
                sb.Append("Box=" + boxL.ToString("0.000", CultureInfo.InvariantCulture) + " " +
                              boxW.ToString("0.000", CultureInfo.InvariantCulture) + " " +
                              boxH.ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("Shape=1\r\n");
                sb.Append("Load=" + def.Watt.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("Flux=" + def.Lumens.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n\r\n");
            }

            for (int r = 0; r < rooms.Count; r++)
            {
                RoomPolygonInfo rm = rooms[r];
                DialuxRoomReviewRow rRow = (roomRows != null && r < roomRows.Count) ? roomRows[r] : new DialuxRoomReviewRow();
                List<KeyValuePair<string, double[]>> lums =
                    (luminairesPerRoom != null && r < luminairesPerRoom.Count)
                    ? luminairesPerRoom[r]
                    : new List<KeyValuePair<string, double[]>>();

                sb.Append("[ROOM.R" + (r + 1).ToString(CultureInfo.InvariantCulture) + "]\r\n");
                sb.Append("Name=" + SanitizeStfValue(!string.IsNullOrWhiteSpace(rRow.RoomName) ? rRow.RoomName : rm.Name, "Room_" + (r + 1).ToString(CultureInfo.InvariantCulture)) + "\r\n");
                sb.Append("Height=" + rRow.CeilingHeightM.ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("WorkingPlane=" + rRow.WorkplaneM.ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("MF=" + mf.ToString("0.00", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("R_Ceiling=" + rRow.ReflCeiling.ToString("0.00", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("R_Floor=" + rRow.ReflFloor.ToString("0.00", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("NrPoints=" + rm.VerticesM.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
                for (int v = 0; v < rm.VerticesM.Count; v++)
                {
                    sb.Append("Point" + (v + 1).ToString(CultureInfo.InvariantCulture) + "=" +
                              rm.VerticesM[v].X.ToString("0.000", CultureInfo.InvariantCulture) + " " +
                              rm.VerticesM[v].Y.ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                    sb.Append("R_Wall" + (v + 1).ToString(CultureInfo.InvariantCulture) + "=" +
                              rRow.ReflWalls.ToString("0.00", CultureInfo.InvariantCulture) + "\r\n");
                }
                sb.Append("NrStruct=0\r\n");
                sb.Append("NrLums=" + lums.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
                for (int k = 0; k < lums.Count; k++)
                {
                    KeyValuePair<string, double[]> lum = lums[k];
                    string sec;
                    if (!blockToSection.TryGetValue(lum.Key ?? "", out sec))
                        sec = "LUMINAIRE.L1";
                    double[] lp = lum.Value;
                    double zM = (lp != null && lp.Length > 2 && lp[2] > 0) ? lp[2] : rRow.MountingHeightM;
                    string pfx = "Lum" + (k + 1).ToString(CultureInfo.InvariantCulture);
                    sb.Append(pfx + "=" + sec + "\r\n");
                    sb.Append(pfx + ".Pos=" + lp[0].ToString("0.000", CultureInfo.InvariantCulture) + " " +
                              lp[1].ToString("0.000", CultureInfo.InvariantCulture) + " " +
                              zM.ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                    sb.Append(pfx + ".Rot=0.000 0.000 " + lp[3].ToString("0.000", CultureInfo.InvariantCulture) + "\r\n");
                }
                sb.Append("\r\n");
            }

            string fullStf = Path.GetFullPath(stfPath);
            string dir = Path.GetDirectoryName(fullStf);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullStf, sb.ToString(), new UTF8Encoding(false));
            return fullStf;
        }

        private static Dictionary<string, object> ExportDialuxStf(Document doc, Dictionary<string, object> args)
        {
            if (args == null) args = new Dictionary<string, object>();
            Database db = doc.Database;
            string stfPath = GetStr(args, "file_path") ?? GetStr(args, "stf_path");
            bool browse = GetBool(args, "browse", false);
            bool interactiveDialog = GetBool(args, "interactive_dialog", false);
            bool previewMissingOnly = GetBool(args, "preview_missing_only", false);
            bool saveToProjectDb = GetBool(args, "save_to_project_db", true);
            string blockFilter = GetStr(args, "block_name");
            string fixLayerFilter = GetStr(args, "fixture_layer");
            string iesPath = GetStr(args, "ies_path") ?? GetStr(args, "ldt_path");
            string stdPref = GetStr(args, "standard") ?? "ECP";

            double? explicitRoomH = GetNullableDouble(args, "room_height_m");
            double? explicitMountH = GetNullableDouble(args, "mounting_height_m");
            double? explicitWorkplaneM = GetNullableDouble(args, "workplane_m");
            double defRoomH = Math.Max(1.8, explicitRoomH ?? 3.0);
            double defWorkplaneM = Math.Max(0.0, explicitWorkplaneM ?? 0.80);
            double defMountH = Math.Max(defWorkplaneM + 0.5, explicitMountH ?? defRoomH);
            double mf = Math.Max(0.4, Math.Min(1.0, GetDouble(args, "maintenance_factor", 0.80)));
            double reflCeil = GetDouble(args, "reflectance_ceiling", 0.70);
            double reflWall = GetDouble(args, "reflectance_walls", 0.50);
            double reflFloor = GetDouble(args, "reflectance_floor", 0.20);
            double defLumens = GetDouble(args, "fixture_lumens", 3600.0);
            double defWatts = GetDouble(args, "fixture_watts", 36.0);

            Dictionary<string, Dictionary<string, object>> savedRoomPrefs = GetSavedDialuxRoomPrefs(doc);

            double unitsPerM = 1000.0;
            List<RoomPolygonInfo> rooms = null;
            List<List<KeyValuePair<string, double[]>>> fixturesPerRoom = new List<List<KeyValuePair<string, double[]>>>();
            Dictionary<string, DialuxLuminaireReviewRow> lumDefs =
                new Dictionary<string, DialuxLuminaireReviewRow>(StringComparer.OrdinalIgnoreCase);
            int totalLumExported = 0;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                double dwgSpan, medSpan;
                Extents3d? mExt;
                ComputeDrawingFixtureMetrics(db, tr, true, layerHiddenFlags, out dwgSpan, out medSpan, out mExt);
                double rawU = Convert.ToDouble(GetUnitsInfo(db)["units_per_m"], CultureInfo.InvariantCulture);
                bool scInf;
                string scWarn;
                unitsPerM = ResolveEffectiveUnitsPerMeter(db, rawU, dwgSpan, medSpan, out scInf, out scWarn);
                if (unitsPerM <= 1e-9) unitsPerM = 1000.0;

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                rooms = ExtractClosedRoomsFromModelSpace(doc, db, ms, tr, layerHiddenFlags, unitsPerM, args);
                if (rooms.Count == 0)
                {
                    tr.Commit();
                    return Err("لم يتم العثور على غرف مغلقة (Closed Polylines) لتصديرها إلى DIALux .stf.");
                }
                for (int r = 0; r < rooms.Count; r++) fixturesPerRoom.Add(new List<KeyValuePair<string, double[]>>());

                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;
                    BlockReference br = ent as BlockReference;
                    if (br == null) continue;

                    string bn = ResolveBlockName(tr, br);
                    if (IsAnonymousBlockName(bn) || IsSystemBlockName(bn)) continue;

                    ProjectLegendSpecItem legSpec;
                    bool inLegendDb = TryGetProjectLegendSpec(doc, bn, out legSpec);

                    if (!string.IsNullOrWhiteSpace(blockFilter))
                    {
                        if (!string.Equals(bn, blockFilter, StringComparison.OrdinalIgnoreCase) && !MatchesPatternOrSubstring(bn, blockFilter))
                            continue;
                    }
                    else if (!string.IsNullOrWhiteSpace(fixLayerFilter))
                    {
                        if (!MatchesPatternOrSubstring(ent.Layer ?? "", fixLayerFilter)) continue;
                    }
                    else
                    {
                        string lyrUp = (ent.Layer ?? "").ToUpperInvariant();
                        string bnUp = bn.ToUpperInvariant();
                        bool looksLighting = lyrUp.Contains("LTG") || lyrUp.Contains("LIGHT") || lyrUp.Contains("LUMIN") ||
                                             bnUp.Contains("LED") || bnUp.Contains("LIGHT") || bnUp.Contains("LAMP") ||
                                             bnUp.Contains("LUMIN") || bnUp.Contains("FIXT") || bnUp.Contains("DOWNLIGHT") ||
                                             bnUp.Contains("PANEL") ||
                                             (inLegendDb && string.Equals(legSpec.Category, "lighting", StringComparison.OrdinalIgnoreCase));
                        if (!looksLighting) continue;
                    }

                    Extents3d ex;
                    try { ex = br.GeometricExtents; }
                    catch { continue; }
                    if (!IsFiniteBounds(ex)) continue;
                    double xm = ((ex.MinPoint.X + ex.MaxPoint.X) * 0.5) / unitsPerM;
                    double ym = ((ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5) / unitsPerM;
                    double rotDeg = br.Rotation * 180.0 / Math.PI;

                    for (int r = 0; r < rooms.Count; r++)
                    {
                        if (IsPointInPolygon2D(rooms[r].VerticesM, xm, ym))
                        {
                            fixturesPerRoom[r].Add(new KeyValuePair<string, double[]>(bn, new double[] { xm, ym, defMountH, rotDeg }));
                            totalLumExported++;

                            DialuxLuminaireReviewRow lRow;
                            if (!lumDefs.TryGetValue(bn, out lRow))
                            {
                                double w = defWatts;
                                double lm = defLumens;
                                bool hasSpec = false;
                                if (inLegendDb && legSpec != null && legSpec.Watt.HasValue && legSpec.Watt.Value > 0.0)
                                {
                                    w = legSpec.Watt.Value;
                                    lm = Math.Round(w * 100.0, 0);
                                    hasSpec = true;
                                }
                                PhotometricProfile prof = ParsePhotometricFile(iesPath, lm, w, bn);
                                if (!string.IsNullOrWhiteSpace(iesPath) && prof != null)
                                {
                                    w = prof.Watts;
                                    lm = prof.TotalLumens;
                                    hasSpec = true;
                                }
                                lRow = new DialuxLuminaireReviewRow
                                {
                                    BlockName = bn,
                                    Qty = 0,
                                    Watt = w,
                                    Lumens = lm,
                                    LengthM = prof != null ? prof.LengthM : 0.60,
                                    WidthM = prof != null ? prof.WidthM : 0.60,
                                    HeightM = prof != null ? prof.HeightM : 0.05,
                                    IesPath = iesPath ?? "",
                                    Manufacturer = prof != null ? prof.Manufacturer : "Generic",
                                    Photometry = prof,
                                    HasVerifiedLegendSpec = hasSpec,
                                    SpecStatus = hasSpec ? "من جدول الليجند" : "ناقص في الليجند (عدّل W / IES)"
                                };
                                lumDefs[bn] = lRow;
                            }
                            lRow.Qty++;
                            break;
                        }
                    }
                }
                tr.Commit();
            }

            List<DialuxRoomReviewRow> roomRows = new List<DialuxRoomReviewRow>(rooms.Count);
            for (int r = 0; r < rooms.Count; r++)
            {
                RoomPolygonInfo rm = rooms[r];
                LightingStandardRule std = ResolveLightingStandard(rm.Name, stdPref, null);
                double rCeilH = defRoomH;
                double rMountH = defMountH;
                double rWorkH = defWorkplaneM;
                double rRefC = reflCeil, rRefW = reflWall, rRefF = reflFloor;
                bool confirmed = explicitRoomH.HasValue;

                Dictionary<string, object> savedP;
                if (savedRoomPrefs.TryGetValue(rm.Name, out savedP) && savedP != null)
                {
                    if (savedP.ContainsKey("ceiling_height_m")) { rCeilH = GetDouble(savedP, "ceiling_height_m", rCeilH); confirmed = true; }
                    if (savedP.ContainsKey("mounting_height_m")) rMountH = GetDouble(savedP, "mounting_height_m", rCeilH);
                    if (savedP.ContainsKey("workplane_m")) rWorkH = GetDouble(savedP, "workplane_m", rWorkH);
                    if (savedP.ContainsKey("refl_ceiling")) rRefC = GetDouble(savedP, "refl_ceiling", rRefC);
                    if (savedP.ContainsKey("refl_walls")) rRefW = GetDouble(savedP, "refl_walls", rRefW);
                    if (savedP.ContainsKey("refl_floor")) rRefF = GetDouble(savedP, "refl_floor", rRefF);
                }

                roomRows.Add(new DialuxRoomReviewRow
                {
                    Index = r + 1,
                    RoomName = rm.Name,
                    Handle = rm.Handle,
                    AreaM2 = Math.Round(rm.AreaM2, 2),
                    SpaceCategory = std.SpaceCategory,
                    TargetLux = std.TargetLux,
                    CeilingHeightM = rCeilH,
                    MountingHeightM = Math.Min(rCeilH, rMountH),
                    WorkplaneM = rWorkH,
                    ReflCeiling = rRefC,
                    ReflWalls = rRefW,
                    ReflFloor = rRefF,
                    FixtureCount = fixturesPerRoom[r].Count,
                    HeightConfirmed = confirmed,
                    StatusNote = confirmed ? "محفوظ / مؤكد" : "افتراضي - يحتاج مراجعة الارتفاع"
                });
            }

            // Apply programmatic overrides (room_overrides[] & luminaire_overrides[])
            List<object> roomOverrides = ExtractDictObjectList(args, "room_overrides");
            for (int oi = 0; oi < roomOverrides.Count; oi++)
            {
                Dictionary<string, object> ov = roomOverrides[oi] as Dictionary<string, object>;
                if (ov == null) continue;
                string targetRm = (GetStr(ov, "room_name") ?? GetStr(ov, "name") ?? "").Trim();
                for (int ri = 0; ri < roomRows.Count; ri++)
                {
                    if (!string.Equals(roomRows[ri].RoomName, targetRm, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(roomRows[ri].Handle, targetRm, StringComparison.OrdinalIgnoreCase))
                        continue;
                    double? ch = GetNullableDouble(ov, "ceiling_height_m");
                    double? mh = GetNullableDouble(ov, "mounting_height_m");
                    double? wp = GetNullableDouble(ov, "workplane_m");
                    double? lx = GetNullableDouble(ov, "target_lux");
                    string sp = GetStr(ov, "space_category");
                    if (ch.HasValue && ch.Value >= 1.8) roomRows[ri].CeilingHeightM = ch.Value;
                    if (mh.HasValue && mh.Value >= 1.5) roomRows[ri].MountingHeightM = Math.Min(roomRows[ri].CeilingHeightM, mh.Value);
                    else if (ch.HasValue) roomRows[ri].MountingHeightM = ch.Value;
                    if (wp.HasValue && wp.Value >= 0.0) roomRows[ri].WorkplaneM = wp.Value;
                    if (lx.HasValue && lx.Value > 10.0) roomRows[ri].TargetLux = lx.Value;
                    if (!string.IsNullOrWhiteSpace(sp)) roomRows[ri].SpaceCategory = sp.Trim();
                    roomRows[ri].HeightConfirmed = true;
                    roomRows[ri].StatusNote = "مؤكد بالتعديل";
                }
            }

            List<object> lumOverrides = ExtractDictObjectList(args, "luminaire_overrides");
            for (int oi = 0; oi < lumOverrides.Count; oi++)
            {
                Dictionary<string, object> ov = lumOverrides[oi] as Dictionary<string, object>;
                if (ov == null) continue;
                string bn = (GetStr(ov, "block_name") ?? GetStr(ov, "name") ?? "").Trim();
                DialuxLuminaireReviewRow lRow;
                if (!lumDefs.TryGetValue(bn, out lRow)) continue;
                double? wt = GetNullableDouble(ov, "watt");
                double? lm = GetNullableDouble(ov, "lumens");
                string mt = GetStr(ov, "mounting_type");
                string ies = GetStr(ov, "ies_path") ?? GetStr(ov, "ldt_path");
                if (wt.HasValue && wt.Value > 0.0) lRow.Watt = wt.Value;
                if (lm.HasValue && lm.Value > 0.0) lRow.Lumens = lm.Value;
                if (!string.IsNullOrWhiteSpace(mt)) lRow.MountingType = mt.Trim();
                if (!string.IsNullOrWhiteSpace(ies)) lRow.IesPath = ies.Trim();
                if (!string.IsNullOrEmpty(lRow.IesPath) && File.Exists(lRow.IesPath))
                    lRow.Photometry = ParsePhotometricFile(lRow.IesPath, lRow.Lumens, lRow.Watt, lRow.BlockName);
                lRow.HasVerifiedLegendSpec = lRow.Watt > 0.0;
                lRow.SpecStatus = "مؤكد يدوياً";
            }

            if (string.IsNullOrWhiteSpace(stfPath))
                stfPath = ExcelTableInterop.ResolveDefaultExportPath(doc, "dialux_project", ".stf");

            if (interactiveDialog && !previewMissingOnly)
            {
                List<DialuxLuminaireReviewRow> lumList = new List<DialuxLuminaireReviewRow>(lumDefs.Values);
                using (DialuxExportReviewForm dlg = new DialuxExportReviewForm(roomRows, lumList, stfPath, mf))
                {
                    if (dlg.ShowDialog() != DialogResult.OK)
                        return Err("تم إلغاء تصدير DIALux من نافذة المراجعة.");
                    stfPath = dlg.SelectedStfPath;
                    mf = dlg.SelectedMaintenanceFactor;
                    saveToProjectDb = dlg.SaveToProjectDb;
                }
            }

            List<string> missingRooms = new List<string>();
            List<object> roomSummaryDicts = new List<object>(roomRows.Count);
            for (int r = 0; r < roomRows.Count; r++)
            {
                DialuxRoomReviewRow rr = roomRows[r];
                if (!rr.HeightConfirmed) missingRooms.Add(rr.RoomName);
                roomSummaryDicts.Add(new Dictionary<string, object>
                {
                    { "room_name", rr.RoomName },
                    { "area_m2", rr.AreaM2 },
                    { "space_category", rr.SpaceCategory },
                    { "ceiling_height_m", rr.CeilingHeightM },
                    { "mounting_height_m", rr.MountingHeightM },
                    { "workplane_m", rr.WorkplaneM },
                    { "target_lux", rr.TargetLux },
                    { "fixture_count", rr.FixtureCount },
                    { "height_confirmed", rr.HeightConfirmed },
                    { "status", rr.StatusNote }
                });
            }

            List<string> missingLumBlocks = new List<string>();
            List<object> lumSummaryDicts = new List<object>(lumDefs.Count);
            foreach (DialuxLuminaireReviewRow lr in lumDefs.Values)
            {
                if (lr.MissingSpec) missingLumBlocks.Add(lr.BlockName);
                lumSummaryDicts.Add(new Dictionary<string, object>
                {
                    { "block_name", lr.BlockName },
                    { "qty", lr.Qty },
                    { "watt", lr.Watt },
                    { "lumens", lr.Lumens },
                    { "mounting_type", lr.MountingType },
                    { "ies_path", lr.IesPath },
                    { "has_verified_legend_spec", lr.HasVerifiedLegendSpec },
                    { "status", lr.SpecStatus }
                });
            }

            Dictionary<string, object> missingSummary = new Dictionary<string, object>
            {
                { "rooms_with_default_height", missingRooms },
                { "luminaires_missing_legend_spec", missingLumBlocks },
                { "missing_count", missingRooms.Count + missingLumBlocks.Count }
            };

            if (previewMissingOnly)
            {
                return Ok(new Dictionary<string, object>
                {
                    { "preview_only", true },
                    { "rooms_count", roomRows.Count },
                    { "luminaires_count", totalLumExported },
                    { "missing_count", missingRooms.Count + missingLumBlocks.Count },
                    { "missing_parameters_summary", missingSummary },
                    { "rooms_summary", roomSummaryDicts },
                    { "luminaires_summary", lumSummaryDicts },
                    { "suggested_defaults", new Dictionary<string, object> {
                        { "ceiling_height_m", defRoomH }, { "mounting_height_m", defMountH },
                        { "workplane_m", defWorkplaneM }, { "maintenance_factor", mf },
                        { "reflectances", new[] { reflCeil, reflWall, reflFloor } }
                    } },
                    { "message", string.Format(
                        CultureInfo.InvariantCulture,
                        "معاينة DIALux: {0} غرفة و{1} كشاف ({2} نوع). ارتفاعات غرف افتراضية: [{3}] | كشافات بدون مواصفة Legend مؤكدة: [{4}]. عدّل القيم عبر room_overrides/luminaire_overrides أو النافذة التفاعلية ثم صدّر.",
                        roomRows.Count, totalLumExported, lumDefs.Count,
                        missingRooms.Count > 0 ? string.Join("، ", missingRooms.ToArray()) : "لا يوجد",
                        missingLumBlocks.Count > 0 ? string.Join("، ", missingLumBlocks.ToArray()) : "لا يوجد") }
                });
            }

            if (browse && !interactiveDialog)
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Title = "حفظ ملف DIALux STF";
                    dlg.Filter = "DIALux Interface File (*.stf)|*.stf";
                    dlg.DefaultExt = "stf";
                    dlg.FileName = Path.GetFileName(stfPath);
                    if (dlg.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.FileName))
                        return Err("تم إلغاء حفظ ملف .stf.");
                    stfPath = dlg.FileName;
                }
            }

            // Persist confirmed room heights & luminaire Watt/VA specs back to project DB
            if (saveToProjectDb)
            {
                Dictionary<string, Dictionary<string, object>> toSaveRooms =
                    new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                for (int r = 0; r < roomRows.Count; r++)
                {
                    DialuxRoomReviewRow rr = roomRows[r];
                    if (!rr.HeightConfirmed) continue;
                    toSaveRooms[rr.RoomName] = new Dictionary<string, object>
                    {
                        { "ceiling_height_m", rr.CeilingHeightM },
                        { "mounting_height_m", rr.MountingHeightM },
                        { "workplane_m", rr.WorkplaneM },
                        { "space_category", rr.SpaceCategory },
                        { "refl_ceiling", rr.ReflCeiling },
                        { "refl_walls", rr.ReflWalls },
                        { "refl_floor", rr.ReflFloor }
                    };
                }
                if (toSaveRooms.Count > 0) SaveDialuxRoomPrefs(doc, toSaveRooms);

                List<object> verifiedLumItems = new List<object>();
                foreach (DialuxLuminaireReviewRow lr in lumDefs.Values)
                {
                    if (!lr.HasVerifiedLegendSpec || lr.Watt <= 0.0) continue;
                    verifiedLumItems.Add(new Dictionary<string, object>
                    {
                        { "block_name", lr.BlockName },
                        { "watt", lr.Watt },
                        { "va", Math.Round(lr.Watt / 0.90, 2) },
                        { "power_factor", 0.90 },
                        { "category", "lighting" },
                        { "source", "dialux_review" }
                    });
                }
                if (verifiedLumItems.Count > 0)
                {
                    ExecuteTool(doc, "manage_project_legend_db", new Dictionary<string, object>
                    {
                        { "action", "set_items" },
                        { "items", verifiedLumItems }
                    });
                }
            }

            // Sync each room's fixture mounting height into export coordinates
            for (int r = 0; r < rooms.Count && r < roomRows.Count; r++)
            {
                double mh = roomRows[r].MountingHeightM;
                List<KeyValuePair<string, double[]>> rLums = fixturesPerRoom[r];
                for (int k = 0; k < rLums.Count; k++) rLums[k].Value[2] = mh;
            }

            string projName = Path.GetFileNameWithoutExtension(doc.Name ?? "AutoCAD_Lighting");
            string writtenStf = WriteDialuxStfFile(stfPath, projName, rooms, fixturesPerRoom, lumDefs, roomRows, mf);

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "stf_path", writtenStf },
                { "rooms_exported", rooms.Count },
                { "luminaires_exported", totalLumExported },
                { "luminaire_types_exported", lumDefs.Count },
                { "units_converted_from", db.Insunits.ToString() },
                { "effective_units_per_m", unitsPerM },
                { "missing_parameters_summary", missingSummary },
                { "rooms_summary", roomSummaryDicts },
                { "luminaires_summary", lumSummaryDicts },
                { "how_to_import_in_dialux", "In DIALux evo / DIALux 4: File -> Import -> STF file (.stf), then click Calculate to generate official DIALux PDF reports." }
            };

            if (GetBool(args, "export_clean_dwg", false))
            {
                string dwgCopy = Path.ChangeExtension(writtenStf, ".dwg");
                using (DocumentLock dl = doc.LockDocument())
                using (Database outDb = db.Wblock())
                {
                    outDb.SaveAs(dwgCopy, DwgVersion.Current);
                }
                result["dwg_background_path"] = dwgCopy;
            }
            return Ok(result);
        }
    }
}
