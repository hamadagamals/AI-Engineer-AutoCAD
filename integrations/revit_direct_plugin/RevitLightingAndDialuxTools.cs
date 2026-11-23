using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;

namespace AIEngineer.Revit.Direct
{
    internal static partial class RevitToolExecutor
    {
        internal sealed class RevitRoomBounds
        {
            public long Id;
            public string Name;
            public string Number;
            public string LevelName;
            public bool FromLink;
            public string LinkName;
            public double AreaM2;
            public double HeightM;
            public double LevelElevationM;
            public double MinXm;
            public double MinYm;
            public double MaxXm;
            public double MaxYm;
            public List<double[]> PolygonM = new List<double[]>(); // list of [x_m, y_m]
        }

        internal static double ResolveTargetLux(string roomName, string standardPref, double? explicitLux, out string category)
        {
            if (explicitLux.HasValue && explicitLux.Value >= 20.0)
            {
                category = "Custom";
                return explicitLux.Value;
            }
            string std = (standardPref ?? "ECP").Trim().ToUpperInvariant();
            string rn = (roomName ?? "").Trim().ToLowerInvariant();
            if (rn.Contains("corridor") || rn.Contains("hall") || rn.Contains("passage") || rn.Contains("ممر") || rn.Contains("طرقة"))
            { category = "Corridor"; return 100.0; }
            if (rn.Contains("stair") || rn.Contains("سلم") || rn.Contains("درج"))
            { category = "Staircase"; return 150.0; }
            if (rn.Contains("wc") || rn.Contains("toilet") || rn.Contains("bath") || rn.Contains("حمام") || rn.Contains("دورة"))
            { category = "Restroom"; return 150.0; }
            if (rn.Contains("store") || rn.Contains("storage") || rn.Contains("مخزن") || rn.Contains("مستودع"))
            { category = "Storage"; return 150.0; }
            if (rn.Contains("bedroom") || rn.Contains("نوم"))
            { category = "Bedroom"; return 150.0; }
            if (rn.Contains("living") || rn.Contains("reception") || rn.Contains("معيشة") || rn.Contains("استقبال") || rn.Contains("صالة"))
            { category = "Living/Reception"; return 250.0; }
            if (rn.Contains("kitchen") || rn.Contains("pantry") || rn.Contains("مطبخ") || rn.Contains("بوفيه"))
            { category = "Kitchen"; return 300.0; }
            if (rn.Contains("class") || rn.Contains("lecture") || rn.Contains("فصل") || rn.Contains("قاعة"))
            { category = "Classroom"; return std == "EN12464" ? 500.0 : 350.0; }
            if (rn.Contains("lab") || rn.Contains("clinic") || rn.Contains("معمل") || rn.Contains("مختبر") || rn.Contains("عيادة"))
            { category = "Lab/Clinic"; return 500.0; }
            if (rn.Contains("elec") || rn.Contains("mech") || rn.Contains("panel") || rn.Contains("كهرباء") || rn.Contains("لوحات"))
            { category = "Electrical Room"; return 300.0; }
            category = "Office/General";
            return std == "EN12464" ? 500.0 : 400.0;
        }

        internal static List<RevitRoomBounds> CollectAllRooms(Document doc, string levelFilter, string roomFilter, bool includeLinked)
        {
            List<RevitRoomBounds> results = new List<RevitRoomBounds>();
            if (doc == null) return results;

            CollectRoomsFromDoc(doc, Transform.Identity, false, "", levelFilter, roomFilter, results);

            if (includeLinked)
            {
                FilteredElementCollector links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance));
                foreach (Element el in links)
                {
                    RevitLinkInstance link = el as RevitLinkInstance;
                    if (link == null) continue;
                    Document linkDoc = null;
                    try { linkDoc = link.GetLinkDocument(); } catch { }
                    if (linkDoc == null) continue;
                    Transform trf = Transform.Identity;
                    try { trf = link.GetTotalTransform(); } catch { }
                    CollectRoomsFromDoc(linkDoc, trf, true, link.Name ?? linkDoc.Title, levelFilter, roomFilter, results);
                }
            }
            return results;
        }

        private static void CollectRoomsFromDoc(
            Document srcDoc, Transform trf, bool fromLink, string linkName,
            string levelFilter, string roomFilter, List<RevitRoomBounds> target)
        {
            SpatialElementBoundaryOptions bOpt = new SpatialElementBoundaryOptions();
            bOpt.SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish;

            // Collect both Architecture Rooms and MEP Spaces
            FilteredElementCollector col = new FilteredElementCollector(srcDoc).OfClass(typeof(SpatialElement));
            foreach (Element el in col)
            {
                SpatialElement sp = el as SpatialElement;
                if (sp == null || sp.Area <= 1e-4) continue;
                bool isRoom = sp is Room;
                bool isSpace = sp is Space;
                if (!isRoom && !isSpace) continue;

                Level lvl = srcDoc.GetElement(sp.LevelId) as Level;
                string lvlName = lvl != null ? lvl.Name : "";
                if (!string.IsNullOrWhiteSpace(levelFilter) &&
                    lvlName.IndexOf(levelFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string rName = sp.Name ?? "";
                string rNum = sp.Number ?? "";
                if (!string.IsNullOrWhiteSpace(roomFilter) &&
                    rName.IndexOf(roomFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    rNum.IndexOf(roomFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                RevitRoomBounds rb = new RevitRoomBounds();
                rb.Id = RevitCompat.GetIdValue(sp.Id);
                rb.Name = rName;
                rb.Number = rNum;
                rb.LevelName = lvlName;
                rb.FromLink = fromLink;
                rb.LinkName = linkName;
                rb.AreaM2 = Math.Round(RevitCompat.SqFeetToSqMeters(sp.Area), 2);
                rb.LevelElevationM = lvl != null ? Math.Round(RevitCompat.FeetToMeters(trf.OfPoint(new XYZ(0, 0, lvl.Elevation)).Z), 3) : 0.0;

                double unboundedH = RevitCompat.ReadParamDouble(sp, BuiltInParameter.ROOM_HEIGHT, 0.0);
                double hM = unboundedH > 1.0 ? RevitCompat.FeetToMeters(unboundedH) : 3.0;
                rb.HeightM = Math.Round(Math.Max(2.2, Math.Min(12.0, hM)), 2);

                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                try
                {
                    IList<IList<BoundarySegment>> loops = sp.GetBoundarySegments(bOpt);
                    if (loops != null && loops.Count > 0 && loops[0] != null)
                    {
                        foreach (BoundarySegment seg in loops[0])
                        {
                            Curve c = seg.GetCurve();
                            if (c == null) continue;
                            IList<XYZ> tess = null;
                            try { tess = c.Tessellate(); } catch { }
                            if (tess == null || tess.Count < 2)
                                tess = new List<XYZ> { c.GetEndPoint(0) };
                            int ptCount = tess.Count > 1 ? (tess.Count - 1) : 1;
                            for (int ti = 0; ti < ptCount; ti++)
                            {
                                XYZ p = trf.OfPoint(tess[ti]);
                                double xm = RevitCompat.FeetToMeters(p.X);
                                double ym = RevitCompat.FeetToMeters(p.Y);
                                if (rb.PolygonM.Count > 0)
                                {
                                    double[] prev = rb.PolygonM[rb.PolygonM.Count - 1];
                                    if (Math.Abs(prev[0] - xm) < 1e-3 && Math.Abs(prev[1] - ym) < 1e-3)
                                        continue;
                                }
                                rb.PolygonM.Add(new double[] { xm, ym });
                                if (xm < minX) minX = xm;
                                if (ym < minY) minY = ym;
                                if (xm > maxX) maxX = xm;
                                if (ym > maxY) maxY = ym;
                            }
                        }
                    }
                }
                catch { }

                if (rb.PolygonM.Count < 3)
                {
                    BoundingBoxXYZ bb = sp.get_BoundingBox(null);
                    if (bb == null) continue;
                    XYZ pMin = trf.OfPoint(bb.Min);
                    XYZ pMax = trf.OfPoint(bb.Max);
                    minX = RevitCompat.FeetToMeters(Math.Min(pMin.X, pMax.X));
                    maxX = RevitCompat.FeetToMeters(Math.Max(pMin.X, pMax.X));
                    minY = RevitCompat.FeetToMeters(Math.Min(pMin.Y, pMax.Y));
                    maxY = RevitCompat.FeetToMeters(Math.Max(pMin.Y, pMax.Y));
                    rb.PolygonM.Add(new double[] { minX, minY });
                    rb.PolygonM.Add(new double[] { maxX, minY });
                    rb.PolygonM.Add(new double[] { maxX, maxY });
                    rb.PolygonM.Add(new double[] { minX, maxY });
                }

                rb.MinXm = Math.Round(minX, 3);
                rb.MinYm = Math.Round(minY, 3);
                rb.MaxXm = Math.Round(maxX, 3);
                rb.MaxYm = Math.Round(maxY, 3);
                target.Add(rb);
            }
        }

        internal static void ComputeLightingGridForRoom(
            double areaM2, double widthM, double lengthM, double mountHeightM, double workplaneM,
            double targetLux, double fixtureLumens, double mf,
            out int cols, out int rows, out int count, out double achievedLux, out double utilFactor)
        {
            double hm = Math.Max(0.8, mountHeightM - workplaneM);
            double w = Math.Max(0.8, widthM);
            double l = Math.Max(0.8, lengthM);
            double kIndex = (w * l) / (hm * (w + l));
            // Deterministic Utilization Factor curve (standard LED direct 70/50/20 reflectances)
            utilFactor = Math.Max(0.38, Math.Min(0.82, 0.34 + 0.28 * Math.Log(kIndex + 1.0)));

            double rawN = (targetLux * Math.Max(0.5, areaM2)) / Math.Max(1.0, fixtureLumens * utilFactor * mf);
            int targetN = Math.Max(1, (int)Math.Ceiling(rawN));

            double aspect = w / l;
            cols = Math.Max(1, (int)Math.Round(Math.Sqrt(targetN * aspect)));
            rows = Math.Max(1, (int)Math.Ceiling((double)targetN / cols));
            // Also enforce max spacing-to-mounting-height ratio <= 1.5 for good uniformity U0
            int minCols = Math.Max(1, (int)Math.Ceiling(w / (1.5 * hm)));
            int minRows = Math.Max(1, (int)Math.Ceiling(l / (1.5 * hm)));
            if (cols < minCols) cols = minCols;
            if (rows < minRows) rows = minRows;

            count = cols * rows;
            achievedLux = Math.Round((count * fixtureLumens * utilFactor * mf) / Math.Max(0.5, areaM2), 1);
            utilFactor = Math.Round(utilFactor, 3);
        }

        internal static bool IsPointInPolygon2D(List<double[]> poly, double x, double y)
        {
            if (poly == null || poly.Count < 3) return true;
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i][0], yi = poly[i][1];
                double xj = poly[j][0], yj = poly[j][1];
                bool intersect = ((yi > y) != (yj > y)) &&
                                 (x < (xj - xi) * (y - yi) / ((yj - yi) == 0 ? 1e-12 : (yj - yi)) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static string SanitizeStfValue(string val, string fallback)
        {
            string s = (val ?? "").Replace("\r", " ").Replace("\n", " ").Replace("[", "(").Replace("]", ")").Replace("=", "-").Trim();
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        internal static string WriteDialuxStf(
            string targetPath, string projectName,
            List<RevitRoomBounds> rooms,
            List<List<double[]>> fixturesPerRoomM,
            string luminaireName, double lumens, double watts, double workplaneM, double mf,
            string iesPath = null)
        {
            string full = Path.GetFullPath(targetPath);
            if (!full.EndsWith(".stf", StringComparison.OrdinalIgnoreCase)) full += ".stf";
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            StringBuilder sb = new StringBuilder();
            sb.Append("[VERSION]\r\n");
            sb.Append("STFF=1.0.5\r\n");
            sb.Append("Progname=AI-Engineer Revit Direct\r\n");
            sb.Append("Progvers=1.0\r\n\r\n");

            sb.Append("[PROJECT]\r\n");
            sb.Append("Name=" + SanitizeStfValue(projectName, "Revit_Lighting") + "\r\n");
            sb.Append("Date=" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\r\n");
            sb.Append("Operator=AI-Engineer\r\n");
            sb.Append("NrRooms=" + rooms.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
            for (int i = 0; i < rooms.Count; i++)
                sb.Append("Room" + (i + 1) + "=ROOM.R" + (i + 1) + "\r\n");
            sb.Append("\r\n");

            sb.Append("[LUMINAIRE.L1]\r\n");
            sb.Append("Manufacturer=AI-Engineer\r\n");
            sb.Append("OrderNr=L1\r\n");
            sb.Append("Name=" + SanitizeStfValue(luminaireName, "LED_Panel_600x600") + "\r\n");
            if (!string.IsNullOrWhiteSpace(iesPath))
                sb.Append("Photometry=" + SanitizeStfValue(Path.GetFileName(iesPath.Trim()), "") + "\r\n");
            sb.Append("Box=0.600 0.600 0.050\r\n");
            sb.Append("Shape=1\r\n");
            sb.Append("Load=" + watts.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n");
            sb.Append("Flux=" + lumens.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n\r\n");

            for (int i = 0; i < rooms.Count; i++)
            {
                RevitRoomBounds r = rooms[i];
                List<double[]> fixList = (fixturesPerRoomM != null && i < fixturesPerRoomM.Count) ? fixturesPerRoomM[i] : new List<double[]>();
                sb.Append("[ROOM.R" + (i + 1) + "]\r\n");
                sb.Append("Name=" + SanitizeStfValue(r.Name, "Room_" + (i + 1)) + "\r\n");
                sb.Append("Height=" + r.HeightM.ToString("0.###", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("WorkingPlane=" + workplaneM.ToString("0.###", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("MF=" + mf.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n");
                sb.Append("R_Ceiling=0.70\r\n");
                sb.Append("R_Floor=0.20\r\n");
                sb.Append("NrPoints=" + r.PolygonM.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
                for (int p = 0; p < r.PolygonM.Count; p++)
                {
                    sb.Append("Point" + (p + 1) + "=" +
                        r.PolygonM[p][0].ToString("0.###", CultureInfo.InvariantCulture) + " " +
                        r.PolygonM[p][1].ToString("0.###", CultureInfo.InvariantCulture) + "\r\n");
                    sb.Append("R_Wall" + (p + 1) + "=0.50\r\n");
                }
                sb.Append("NrStruct=0\r\n");
                sb.Append("NrLums=" + fixList.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
                for (int f = 0; f < fixList.Count; f++)
                {
                    double[] pt = fixList[f];
                    double z = pt.Length >= 3 ? pt[2] : r.HeightM;
                    double rotDeg = pt.Length >= 4 ? pt[3] : 0.0;
                    sb.Append("Lum" + (f + 1) + "=LUMINAIRE.L1\r\n");
                    sb.Append("Lum" + (f + 1) + ".Pos=" +
                        pt[0].ToString("0.###", CultureInfo.InvariantCulture) + " " +
                        pt[1].ToString("0.###", CultureInfo.InvariantCulture) + " " +
                        z.ToString("0.###", CultureInfo.InvariantCulture) + "\r\n");
                    sb.Append("Lum" + (f + 1) + ".Rot=0 0 " + rotDeg.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n");
                }
                sb.Append("\r\n");
            }

            File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
            return full;
        }

        private static Dictionary<string, object> DistributeLighting(Document doc, View activeView, Dictionary<string, object> args)
        {
            string levelFilter = GetStr(args, "level_name");
            string roomFilter = GetStr(args, "room_name");
            string standard = (GetStr(args, "standard") ?? "ECP").Trim().ToUpperInvariant();
            double? explicitLux = GetNullableDouble(args, "target_lux");
            double lumens = Math.Max(200.0, GetDouble(args, "fixture_lumens", 3600.0));
            double watts = Math.Max(3.0, GetDouble(args, "fixture_watts", 36.0));
            double workplaneM = Math.Max(0.0, GetDouble(args, "workplane_m", 0.80));
            double? customMountM = GetNullableDouble(args, "mounting_height_m");
            double mf = Math.Max(0.4, Math.Min(1.0, GetDouble(args, "maintenance_factor", 0.80)));
            bool placeInModel = GetBool(args, "place_fixtures", true);
            bool previewRed = GetBool(args, "preview_red", true);
            bool exportExcel = GetBool(args, "export_excel", false);
            bool exportStf = GetBool(args, "export_stf", false);
            string familyTypeReq = GetStr(args, "family_type") ?? GetStr(args, "family_name");

            List<RevitRoomBounds> rooms = CollectAllRooms(doc, levelFilter, roomFilter, true);
            if (rooms.Count == 0)
                return Err("No Rooms or MEP Spaces found matching filters (checked active document and linked architectural models).");

            FamilySymbol chosenSymbol = null;
            if (placeInModel)
            {
                chosenSymbol = FindLoadedFamilySymbol(doc, BuiltInCategory.OST_LightingFixtures, familyTypeReq);
                if (chosenSymbol == null)
                {
                    List<string> avail = ListLoadedFamilyTypeNames(doc, BuiltInCategory.OST_LightingFixtures, 20);
                    Dictionary<string, object> err = Err("No matching Lighting Fixture FamilySymbol loaded in the Revit model" +
                        (string.IsNullOrEmpty(familyTypeReq) ? "." : (" for '" + familyTypeReq + "'.")) +
                        " Load a lighting fixture family (.rfa) or pass place_fixtures=false for calculation-only / DIALux STF.");
                    err["available_lighting_types"] = avail;
                    return err;
                }
            }

            List<object> roomReports = new List<object>();
            List<List<double[]>> fixturesPerRoomM = new List<List<double[]>>();
            List<ElementId> createdIds = new List<ElementId>();
            int totalFixtures = 0;
            double totalWatts = 0.0;

            RevitExcelSheet xl = new RevitExcelSheet();
            xl.Title = "جدول حسابات وتوزيع الإنارة - Revit (" + standard + ")";
            xl.SheetName = "Lighting_Design";
            xl.Headers = new List<string> { "الغرفة", "الدور", "المساحة (m2)", "التصنيف", "اللكس المطلوب", "الشبكة", "عدد الكشافات", "اللكس الفعلي", "القدرة (W)" };

            Dictionary<string, Level> levelsByName = IndexLevelsByName(doc);
            Level fallbackLevel = GetDefaultLevel(doc, activeView);
            string firstPlacementError = null;

            using (Transaction tr = new Transaction(doc, "AI-Engineer: Distribute Lighting"))
            {
                tr.Start();
                if (chosenSymbol != null && !chosenSymbol.IsActive)
                {
                    chosenSymbol.Activate();
                    doc.Regenerate();
                }

                for (int i = 0; i < rooms.Count; i++)
                {
                    RevitRoomBounds r = rooms[i];
                    string cat;
                    double targetLux = ResolveTargetLux(r.Name, standard, explicitLux, out cat);
                    double mountH = customMountM.HasValue ? customMountM.Value : Math.Max(2.2, r.HeightM - 0.15);
                    double wM = Math.Max(0.8, r.MaxXm - r.MinXm);
                    double lM = Math.Max(0.8, r.MaxYm - r.MinYm);

                    int cols, rows, count;
                    double achievedLux, uf;
                    ComputeLightingGridForRoom(r.AreaM2, wM, lM, mountH, workplaneM, targetLux, lumens, mf,
                        out cols, out rows, out count, out achievedLux, out uf);

                    Level targetLvl = ResolveLevelForRoomOrElevation(levelsByName, r.LevelName, r.LevelElevationM, fallbackLevel);
                    double stepX = wM / cols;
                    double stepY = lM / rows;
                    List<double[]> ptsM = new List<double[]>();
                    int placedInRoom = 0;

                    for (int row = 0; row < rows; row++)
                    {
                        for (int col = 0; col < cols; col++)
                        {
                            double xm = r.MinXm + (col + 0.5) * stepX;
                            double ym = r.MinYm + (row + 0.5) * stepY;
                            if (!IsPointInPolygon2D(r.PolygonM, xm, ym)) continue;
                            double zmAbs = (targetLvl != null ? RevitCompat.FeetToMeters(targetLvl.Elevation) : r.LevelElevationM) + mountH;
                            ptsM.Add(new double[] { xm, ym, mountH, 0.0 });

                            if (placeInModel && chosenSymbol != null && targetLvl != null)
                            {
                                try
                                {
                                    XYZ pt = new XYZ(RevitCompat.MetersToFeet(xm), RevitCompat.MetersToFeet(ym), RevitCompat.MetersToFeet(zmAbs));
                                    FamilyInstance fi = doc.Create.NewFamilyInstance(pt, chosenSymbol, targetLvl, StructuralType.NonStructural);
                                    if (fi != null)
                                    {
                                        Parameter offP = fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                                        if (offP != null && !offP.IsReadOnly) offP.Set(RevitCompat.MetersToFeet(mountH));
                                        Parameter markP = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                                        if (markP != null && !markP.IsReadOnly)
                                            markP.Set("L" + (i + 1).ToString(CultureInfo.InvariantCulture) + "-" + (placedInRoom + 1).ToString("D2", CultureInfo.InvariantCulture));
                                        createdIds.Add(fi.Id);
                                        placedInRoom++;
                                    }
                                }
                                catch (Exception pEx)
                                {
                                    if (firstPlacementError == null) firstPlacementError = pEx.Message;
                                }
                            }
                        }
                    }

                    fixturesPerRoomM.Add(ptsM);
                    int effCount = ptsM.Count > 0 ? ptsM.Count : count;
                    if (ptsM.Count > 0 && ptsM.Count != count)
                        achievedLux = Math.Round((ptsM.Count * lumens * uf * mf) / Math.Max(0.5, r.AreaM2), 1);
                    totalFixtures += effCount;
                    double roomW = effCount * watts;
                    totalWatts += roomW;

                    roomReports.Add(new Dictionary<string, object>
                    {
                        { "room", r.Name },
                        { "number", r.Number },
                        { "level", r.LevelName },
                        { "from_link", r.FromLink },
                        { "area_m2", r.AreaM2 },
                        { "category", cat },
                        { "target_lux", targetLux },
                        { "achieved_lux", achievedLux },
                        { "utilization_factor", uf },
                        { "grid", cols + "x" + rows },
                        { "calculated_count", effCount },
                        { "placed_count", placedInRoom },
                        { "watts", Math.Round(roomW, 1) }
                    });

                    xl.Rows.Add(new List<string>
                    {
                        r.Name, r.LevelName, r.AreaM2.ToString("0.##", CultureInfo.InvariantCulture),
                        cat, targetLux.ToString("0", CultureInfo.InvariantCulture),
                        cols + "x" + rows, effCount.ToString(CultureInfo.InvariantCulture),
                        achievedLux.ToString("0.0", CultureInfo.InvariantCulture),
                        Math.Round(roomW, 1).ToString("0.#", CultureInfo.InvariantCulture)
                    });
                }

                if (previewRed && activeView != null && createdIds.Count > 0)
                    ApplyRedPreviewOverrides(activeView, createdIds);

                tr.Commit();
            }

            if (createdIds.Count > 0)
                RecordUndo(doc, activeView, "distribute_lighting", "Distributed " + createdIds.Count + " lighting fixtures across " + rooms.Count + " rooms", createdIds, previewRed);

            if (placeInModel && createdIds.Count == 0)
            {
                Dictionary<string, object> failResp = Err(
                    "Calculation succeeded (" + totalFixtures + " fixtures across " + rooms.Count + " rooms), but 0 fixtures could be placed with family '" +
                    (chosenSymbol != null ? (chosenSymbol.FamilyName + " : " + chosenSymbol.Name) : "") +
                    "'. This family may be Ceiling/Face-hosted; pick a Level-based/WorkPlane lighting family or pass place_fixtures=false. Detail: " +
                    (firstPlacementError ?? "NewFamilyInstance returned null."));
                failResp["rooms"] = roomReports;
                failResp["available_lighting_types"] = ListLoadedFamilyTypeNames(doc, BuiltInCategory.OST_LightingFixtures, 25);
                return failResp;
            }

            xl.FooterRow = new List<string> { "الإجمالي (" + rooms.Count + ")", "", "", "", "", "", totalFixtures.ToString(CultureInfo.InvariantCulture), "", Math.Round(totalWatts, 1).ToString("0.#", CultureInfo.InvariantCulture) + " W" };

            Dictionary<string, object> resp = new Dictionary<string, object>
            {
                { "standard", standard },
                { "rooms_processed", rooms.Count },
                { "total_fixtures_calculated", totalFixtures },
                { "total_fixtures_placed", createdIds.Count },
                { "total_watts", Math.Round(totalWatts, 1) },
                { "family_used", chosenSymbol != null ? (chosenSymbol.FamilyName + " : " + chosenSymbol.Name) : "none (calculation only)" },
                { "preview_red_applied", previewRed && createdIds.Count > 0 },
                { "rooms", roomReports }
            };

            string docRef = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName;
            string reqXl = GetStr(args, "excel_path");
            string reqStf = GetStr(args, "stf_path");
            string iesPath = GetStr(args, "ies_path");
            if (exportExcel)
            {
                string xlPath = !string.IsNullOrWhiteSpace(reqXl) ? reqXl : RevitExcelExporter.ResolveDefaultPath(docRef, "revit_lighting_design", ".xlsx");
                resp["excel_exported"] = true;
                resp["excel_path"] = RevitExcelExporter.WriteXlsx(xlPath, xl);
            }
            if (exportStf)
            {
                string stfPath = !string.IsNullOrWhiteSpace(reqStf) ? reqStf : RevitExcelExporter.ResolveDefaultPath(docRef, "revit_dialux", ".stf");
                resp["stf_exported"] = true;
                resp["stf_path"] = WriteDialuxStf(stfPath, doc.Title, rooms, fixturesPerRoomM,
                    chosenSymbol != null ? chosenSymbol.Name : "LED_Panel", lumens, watts, workplaneM, mf, iesPath);
            }
            return Ok(resp);
        }

        private static Dictionary<string, object> ExportDialuxStf(Document doc, Dictionary<string, object> args)
        {
            string levelFilter = GetStr(args, "level_name");
            string roomFilter = GetStr(args, "room_name");
            string iesPath = GetStr(args, "ies_path");
            double lumens = Math.Max(200.0, GetDouble(args, "fixture_lumens", 3600.0));
            double watts = Math.Max(3.0, GetDouble(args, "fixture_watts", 36.0));
            double workplaneM = Math.Max(0.0, GetDouble(args, "workplane_m", 0.80));
            double mf = Math.Max(0.4, Math.Min(1.0, GetDouble(args, "maintenance_factor", 0.80)));

            List<RevitRoomBounds> rooms = CollectAllRooms(doc, levelFilter, roomFilter, true);
            if (rooms.Count == 0)
                return Err("No Rooms or MEP Spaces found to export to DIALux .stf.");

            // Collect existing lighting fixtures with X, Y, Z (meters) and Rotation (degrees)
            List<double[]> allFixM = new List<double[]>();
            FilteredElementCollector fCol = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType();
            foreach (Element el in fCol)
            {
                LocationPoint lp = el.Location as LocationPoint;
                if (lp == null || lp.Point == null) continue;
                double rotDeg = 0.0;
                try { rotDeg = lp.Rotation * (180.0 / Math.PI); } catch { }
                allFixM.Add(new double[]
                {
                    RevitCompat.FeetToMeters(lp.Point.X),
                    RevitCompat.FeetToMeters(lp.Point.Y),
                    RevitCompat.FeetToMeters(lp.Point.Z),
                    rotDeg
                });
            }

            List<List<double[]>> perRoom = new List<List<double[]>>();
            for (int i = 0; i < rooms.Count; i++) perRoom.Add(new List<double[]>());

            int matchedCount = 0;
            for (int f = 0; f < allFixM.Count; f++)
            {
                double[] pt = allFixM[f];
                for (int i = 0; i < rooms.Count; i++)
                {
                    RevitRoomBounds r = rooms[i];
                    // Multi-story Z-elevation guard + 2D bounding box + polygon containment
                    if (pt[2] < r.LevelElevationM - 0.5 || pt[2] > r.LevelElevationM + r.HeightM + 0.5) continue;
                    if (pt[0] < r.MinXm - 0.1 || pt[0] > r.MaxXm + 0.1 || pt[1] < r.MinYm - 0.1 || pt[1] > r.MaxYm + 0.1) continue;
                    if (!IsPointInPolygon2D(r.PolygonM, pt[0], pt[1])) continue;

                    double relZ = Math.Max(0.5, Math.Min(r.HeightM, pt[2] - r.LevelElevationM));
                    perRoom[i].Add(new double[] { pt[0], pt[1], relZ, pt[3] });
                    matchedCount++;
                    break; // Assign each fixture to exactly ONE room (no cross-floor/adjacent duplication)
                }
            }

            string docRef = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName;
            string reqOut = GetStr(args, "stf_path") ?? GetStr(args, "file_path");
            string outPath = !string.IsNullOrWhiteSpace(reqOut) ? reqOut : RevitExcelExporter.ResolveDefaultPath(docRef, "revit_dialux", ".stf");
            string written = WriteDialuxStf(outPath, doc.Title, rooms, perRoom, "Revit_Luminaire", lumens, watts, workplaneM, mf, iesPath);
            return Ok(new Dictionary<string, object>
            {
                { "stf_path", written },
                { "rooms_exported", rooms.Count },
                { "existing_fixtures_in_model", allFixM.Count },
                { "fixtures_matched_to_rooms", matchedCount }
            });
        }
    }
}
