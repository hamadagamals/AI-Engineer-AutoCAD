using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        private sealed class PhotometricProfile
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

        private static Dictionary<string, object> ExportDialuxStf(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            string stfPath = GetStr(args, "file_path") ?? GetStr(args, "stf_path");
            bool browse = GetBool(args, "browse", false);
            string blockFilter = GetStr(args, "block_name");
            string fixLayerFilter = GetStr(args, "fixture_layer");
            string iesPath = GetStr(args, "ies_path") ?? GetStr(args, "ldt_path");

            double roomH = Math.Max(1.8, GetDouble(args, "room_height_m", 3.0));
            double workplaneM = Math.Max(0.0, GetDouble(args, "workplane_m", 0.80));
            double mountH = Math.Max(workplaneM + 0.5, GetDouble(args, "mounting_height_m", roomH));
            double mf = Math.Max(0.4, Math.Min(1.0, GetDouble(args, "maintenance_factor", 0.80)));
            double reflCeil = GetDouble(args, "reflectance_ceiling", 0.70);
            double reflWall = GetDouble(args, "reflectance_walls", 0.50);
            double reflFloor = GetDouble(args, "reflectance_floor", 0.20);
            double lumens = GetDouble(args, "fixture_lumens", 3600.0);
            double watts = GetDouble(args, "fixture_watts", 36.0);
            PhotometricProfile phot = ParsePhotometricFile(iesPath, lumens, watts, string.IsNullOrWhiteSpace(blockFilter) ? "LED_Fixture" : blockFilter);

            double unitsPerM = Convert.ToDouble(GetUnitsInfo(db)["units_per_m"], CultureInfo.InvariantCulture);
            if (unitsPerM <= 1e-9) unitsPerM = 1000.0;

            List<RoomPolygonInfo> rooms = null;
            List<List<double[]>> fixturesPerRoomM = new List<List<double[]>>();
            int totalLumExported = 0;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);

                rooms = ExtractClosedRoomsFromModelSpace(doc, db, ms, tr, layerHiddenFlags, unitsPerM, args);
                if (rooms.Count == 0)
                {
                    tr.Commit();
                    return Err("لم يتم العثور على غرف مغلقة (Closed Polylines) لتصديرها إلى DIALux .stf.");
                }
                for (int r = 0; r < rooms.Count; r++) fixturesPerRoomM.Add(new List<double[]>());

                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;
                    BlockReference br = ent as BlockReference;
                    if (br == null) continue;

                    string bn = ResolveBlockName(tr, br);
                    if (IsAnonymousBlockName(bn) || IsSystemBlockName(bn)) continue;

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
                        // Auto-match typical lighting fixture block names or E-LTG layers
                        string lyrUp = (ent.Layer ?? "").ToUpperInvariant();
                        string bnUp = bn.ToUpperInvariant();
                        bool looksLighting = lyrUp.Contains("LTG") || lyrUp.Contains("LIGHT") || lyrUp.Contains("LUMIN") ||
                                             bnUp.Contains("LED") || bnUp.Contains("LIGHT") || bnUp.Contains("LAMP") ||
                                             bnUp.Contains("LUMIN") || bnUp.Contains("FIXT") || bnUp.Contains("DOWNLIGHT") ||
                                             bnUp.Contains("PANEL");
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
                            fixturesPerRoomM[r].Add(new double[] { xm, ym, mountH, rotDeg });
                            totalLumExported++;
                            break;
                        }
                    }
                }
                tr.Commit();
            }

            if (string.IsNullOrWhiteSpace(stfPath))
                stfPath = ExcelTableInterop.ResolveDefaultExportPath(doc, "dialux_project", ".stf");
            if (browse)
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

            string projName = Path.GetFileNameWithoutExtension(doc.Name ?? "AutoCAD_Lighting");
            string writtenStf = WriteDialuxStfFile(stfPath, projName, rooms, fixturesPerRoomM, phot, roomH, workplaneM, reflCeil, reflWall, reflFloor, mf);

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "stf_path", writtenStf },
                { "rooms_exported", rooms.Count },
                { "luminaires_exported", totalLumExported },
                { "units_converted_from", db.Insunits.ToString() },
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
