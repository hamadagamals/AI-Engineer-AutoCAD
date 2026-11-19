using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        private sealed class LoadScheduleRow
        {
            public int Index;
            public string Handle;
            public string CircuitTag;
            public string BlockName;
            public string Rating;
            public string CableSpec;
            public string LoadText;
            public double LoadKwNumeric;
            public double PathLengthUnits;
            public double PathLengthMeters;
            public int ConnectedSegments;
            public string Description;
            public double X;
            public double Y;
        }

        private sealed class PathSegmentItem
        {
            public ObjectId Id;
            public string Handle;
            public string Layer;
            public int ColorIndex;
            public Point2d Start;
            public Point2d End;
            public Extents3d Bounds;
            public double Length;
            public bool IsClosed;
        }

        private sealed class NearbyTextItem
        {
            public string Handle;
            public string Text;
            public double Cx;
            public double Cy;
            public Extents3d Bounds;
        }

        internal static Extents3d? RenderStyledScheduleTable(
            Document doc,
            Database db,
            BlockTableRecord ms,
            Transaction tr,
            ExcelSheetData sheet,
            string layerName,
            bool replaceExisting,
            string positionStr,
            double? customGap,
            double? reqTextH,
            Extents3d? mainDrawingExt,
            List<ObjectId> createdIds,
            List<ObjectId> erasedIds)
        {
            string safeLayer = EnsureLayerSafe(db, tr, layerName, 7, "Continuous");

            if (replaceExisting)
            {
                foreach (ObjectId id in ms)
                {
                    Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null || e.IsErased) continue;
                    if (string.Equals(e.Layer, safeLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        Entity ew = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                        ew.Erase(true);
                        erasedIds.Add(id);
                    }
                }
            }

            List<string> headers = sheet.Headers ?? new List<string>();
            List<List<string>> bodyRows = sheet.Rows ?? new List<List<string>>();
            List<string> footerRow = sheet.FooterRow;
            int colCount = headers.Count;
            for (int r = 0; r < bodyRows.Count; r++)
                if (bodyRows[r] != null && bodyRows[r].Count > colCount) colCount = bodyRows[r].Count;
            if (footerRow != null && footerRow.Count > colCount) colCount = footerRow.Count;
            if (colCount <= 0) return null;

            bool hasHeader = headers.Count > 0;
            bool hasTitle = !string.IsNullOrWhiteSpace(sheet.Title);
            bool hasFooter = footerRow != null && footerRow.Count > 0;
            bool isArabicRtl = ExcelTableInterop.IsSpreadsheetArabic(sheet);

            double dwgW = mainDrawingExt.HasValue ? Math.Max(1.0, mainDrawingExt.Value.MaxPoint.X - mainDrawingExt.Value.MinPoint.X) : 200.0;
            double dwgH = mainDrawingExt.HasValue ? Math.Max(1.0, mainDrawingExt.Value.MaxPoint.Y - mainDrawingExt.Value.MinPoint.Y) : 140.0;
            double dwgSpan = Math.Max(dwgW, dwgH);
            double unitsPerCm = Convert.ToDouble(GetUnitsInfo(db)["units_per_cm"], CultureInfo.InvariantCulture);

            double usedTextH;
            if (reqTextH.HasValue && reqTextH.Value > 1e-6)
            {
                usedTextH = reqTextH.Value;
            }
            else
            {
                double candidateH = dwgSpan > 1.0 ? Math.Max(0.45 * unitsPerCm, dwgSpan * 0.0115) : (2.5 * unitsPerCm);
                usedTextH = Math.Min(Math.Max(candidateH, 0.25 * unitsPerCm), Math.Max(5.0 * unitsPerCm, dwgH * 0.055));
            }

            double rowH = usedTextH * 2.55;
            double titleH = hasTitle ? usedTextH * 3.1 : 0.0;
            double headerH = hasHeader ? usedTextH * 2.75 : 0.0;
            double footerH = hasFooter ? usedTextH * 2.75 : 0.0;

            double[] colWidths = new double[colCount];
            for (int c = 0; c < colCount; c++)
            {
                double maxSpan = usedTextH * 4.5;
                if (hasHeader && c < headers.Count)
                    maxSpan = Math.Max(maxSpan, MeasureTextSpanInUnits(headers[c], usedTextH * 1.04));
                for (int r = 0; r < bodyRows.Count; r++)
                {
                    if (bodyRows[r] != null && c < bodyRows[r].Count)
                        maxSpan = Math.Max(maxSpan, MeasureTextSpanInUnits(bodyRows[r][c], usedTextH));
                }
                if (hasFooter && c < footerRow.Count)
                    maxSpan = Math.Max(maxSpan, MeasureTextSpanInUnits(footerRow[c], usedTextH * 1.02));
                colWidths[c] = Math.Min(usedTextH * 36.0, maxSpan + usedTextH * 2.2);
            }

            double totalTableW = 0;
            for (int c = 0; c < colCount; c++) totalTableW += colWidths[c];
            if (hasTitle)
            {
                double titleMinW = MeasureTextSpanInUnits(sheet.Title, usedTextH * 1.15) + usedTextH * 3.0;
                if (titleMinW > totalTableW && totalTableW > 1e-6)
                {
                    double sw = titleMinW / totalTableW;
                    totalTableW = titleMinW;
                    for (int c = 0; c < colCount; c++) colWidths[c] *= sw;
                }
            }
            double totalTableH = titleH + headerH + bodyRows.Count * rowH + footerH;
            double gap = (customGap.HasValue && customGap.Value > 0) ? customGap.Value : Math.Max(totalTableW * 0.06, dwgW * 0.06);

            double topLeftX = 0;
            double topLeftY = totalTableH;
            string posNorm = (positionStr ?? "outside_right").Trim().ToLowerInvariant();
            if (mainDrawingExt.HasValue)
            {
                Extents3d refExt = mainDrawingExt.Value;
                if (posNorm == "outside_left" || posNorm == "left")
                {
                    topLeftX = refExt.MinPoint.X - gap - totalTableW;
                    topLeftY = refExt.MaxPoint.Y;
                }
                else if (posNorm == "outside_top" || posNorm == "top")
                {
                    topLeftX = refExt.MinPoint.X;
                    topLeftY = refExt.MaxPoint.Y + gap + totalTableH;
                }
                else if (posNorm == "outside_bottom" || posNorm == "bottom")
                {
                    topLeftX = refExt.MinPoint.X;
                    topLeftY = refExt.MinPoint.Y - gap;
                }
                else
                {
                    topLeftX = refExt.MaxPoint.X + gap;
                    topLeftY = refExt.MaxPoint.Y;
                }
            }

            string[] tableLayers = new string[]
            {
                "_AI_BLOCK_LEGEND", "_AI_COORD_TABLE", "_AI_EXCEL_TABLE", "_AI_PANEL_SCHEDULE", "_AI_BOQ_TABLE"
            };
            List<Extents3d> blockers = new List<Extents3d>();
            for (int i = 0; i < tableLayers.Length; i++)
            {
                if (replaceExisting && string.Equals(tableLayers[i], safeLayer, StringComparison.OrdinalIgnoreCase)) continue;
                Extents3d? ext = CollectLayerExtents(ms, tr, tableLayers[i]);
                if (ext.HasValue && IsFiniteBounds(ext.Value)) blockers.Add(ext.Value);
            }

            for (int pass = 0; pass < blockers.Count + 2; pass++)
            {
                bool shifted = false;
                double ax1 = topLeftX;
                double ay2 = topLeftY;
                double ax2 = topLeftX + totalTableW;
                double ay1 = topLeftY - totalTableH;
                for (int b = 0; b < blockers.Count; b++)
                {
                    Extents3d blk = blockers[b];
                    if (RectsOverlap(ax1 - gap * 0.2, ay1 - gap * 0.2, ax2 + gap * 0.2, ay2 + gap * 0.2,
                                     blk.MinPoint.X, blk.MinPoint.Y, blk.MaxPoint.X, blk.MaxPoint.Y))
                    {
                        topLeftY = blk.MinPoint.Y - gap * 0.65;
                        shifted = true;
                        break;
                    }
                }
                if (!shifted) break;
            }

            ObjectId unicodeStyleId = EnsureUnicodeTextStyle(db, tr);
            double leftX = topLeftX;
            double rightX = topLeftX + totalTableW;
            double topY = topLeftY;
            double bottomY = topLeftY - totalTableH;

            Extents3d frameExt = new Extents3d(new Point3d(leftX, bottomY, 0.0), new Point3d(rightX, topY, 0.0));
            Polyline outerFrame = CreateFramePolyline(frameExt, safeLayer, 7);
            outerFrame.ConstantWidth = usedTextH * 0.06;
            ms.AppendEntity(outerFrame);
            tr.AddNewlyCreatedDBObject(outerFrame, true);
            createdIds.Add(outerFrame.ObjectId);

            double[] colLeftXs = new double[colCount];
            double[] colVisualWidths = new double[colCount];
            double curX = leftX;
            for (int vc = 0; vc < colCount; vc++)
            {
                int logicalC = isArabicRtl ? (colCount - 1 - vc) : vc;
                colLeftXs[vc] = curX;
                colVisualWidths[vc] = colWidths[logicalC];
                curX += colVisualWidths[vc];
            }

            double curY = topY;
            if (hasTitle)
            {
                double rowBottom = curY - titleH;
                MText tMt = CreateLegendCellMText(ms, tr, unicodeStyleId, sheet.Title,
                    (leftX + rightX) * 0.5, (curY + rowBottom) * 0.5, totalTableW, titleH,
                    usedTextH * 1.15, titleH, AttachmentPoint.MiddleCenter, 2, safeLayer);
                createdIds.Add(tMt.ObjectId);
                Line div = new Line(new Point3d(leftX, rowBottom, 0.0), new Point3d(rightX, rowBottom, 0.0));
                div.Layer = safeLayer;
                div.ColorIndex = 7;
                ms.AppendEntity(div);
                tr.AddNewlyCreatedDBObject(div, true);
                createdIds.Add(div.ObjectId);
                curY = rowBottom;
            }

            double gridTopY = curY;
            if (hasHeader)
            {
                double rowBottom = curY - headerH;
                for (int vc = 0; vc < colCount; vc++)
                {
                    int logicalC = isArabicRtl ? (colCount - 1 - vc) : vc;
                    string hVal = (logicalC < headers.Count && headers[logicalC] != null) ? headers[logicalC] : "";
                    MText hMt = CreateLegendCellMText(ms, tr, unicodeStyleId, hVal,
                        colLeftXs[vc] + colVisualWidths[vc] * 0.5, (curY + rowBottom) * 0.5,
                        colVisualWidths[vc], headerH, usedTextH * 1.02, headerH, AttachmentPoint.MiddleCenter, 4, safeLayer);
                    createdIds.Add(hMt.ObjectId);
                }
                Line div = new Line(new Point3d(leftX, rowBottom, 0.0), new Point3d(rightX, rowBottom, 0.0));
                div.Layer = safeLayer;
                div.ColorIndex = 7;
                ms.AppendEntity(div);
                tr.AddNewlyCreatedDBObject(div, true);
                createdIds.Add(div.ObjectId);
                curY = rowBottom;
            }

            for (int r = 0; r < bodyRows.Count; r++)
            {
                double rowBottom = curY - rowH;
                List<string> row = bodyRows[r];
                for (int vc = 0; vc < colCount; vc++)
                {
                    int logicalC = isArabicRtl ? (colCount - 1 - vc) : vc;
                    string cVal = (row != null && logicalC < row.Count && row[logicalC] != null) ? row[logicalC] : "";
                    MText cMt = CreateLegendCellMText(ms, tr, unicodeStyleId, cVal,
                        colLeftXs[vc] + colVisualWidths[vc] * 0.5, (curY + rowBottom) * 0.5,
                        colVisualWidths[vc], rowH, usedTextH, rowH, AttachmentPoint.MiddleCenter, 7, safeLayer);
                    createdIds.Add(cMt.ObjectId);
                }
                if (r < bodyRows.Count - 1 || hasFooter)
                {
                    Line rl = new Line(new Point3d(leftX, rowBottom, 0.0), new Point3d(rightX, rowBottom, 0.0));
                    rl.Layer = safeLayer;
                    rl.ColorIndex = (r == bodyRows.Count - 1 && hasFooter) ? (short)7 : (short)8;
                    ms.AppendEntity(rl);
                    tr.AddNewlyCreatedDBObject(rl, true);
                    createdIds.Add(rl.ObjectId);
                }
                curY = rowBottom;
            }

            if (hasFooter)
            {
                double rowBottom = curY - footerH;
                for (int vc = 0; vc < colCount; vc++)
                {
                    int logicalC = isArabicRtl ? (colCount - 1 - vc) : vc;
                    string fVal = (logicalC < footerRow.Count && footerRow[logicalC] != null) ? footerRow[logicalC] : "";
                    MText fMt = CreateLegendCellMText(ms, tr, unicodeStyleId, fVal,
                        colLeftXs[vc] + colVisualWidths[vc] * 0.5, (curY + rowBottom) * 0.5,
                        colVisualWidths[vc], footerH, usedTextH * 1.02, footerH, AttachmentPoint.MiddleCenter, 3, safeLayer);
                    createdIds.Add(fMt.ObjectId);
                }
            }

            for (int vc = 1; vc < colCount; vc++)
            {
                double vx = colLeftXs[vc];
                Line vl = new Line(new Point3d(vx, gridTopY, 0.0), new Point3d(vx, bottomY, 0.0));
                vl.Layer = safeLayer;
                vl.ColorIndex = 8;
                ms.AppendEntity(vl);
                tr.AddNewlyCreatedDBObject(vl, true);
                createdIds.Add(vl.ObjectId);
            }

            return frameExt;
        }

        private static string BucketKey2D(double x, double y, double cellSize)
        {
            long bx = (long)Math.Floor(x / cellSize);
            long by = (long)Math.Floor(y / cellSize);
            return bx.ToString(CultureInfo.InvariantCulture) + ":" + by.ToString(CultureInfo.InvariantCulture);
        }

        private static void AddPointToBucket(Dictionary<string, List<int>> buckets, double x, double y, int idx, double cellSize)
        {
            string key = BucketKey2D(x, y, cellSize);
            List<int> list;
            if (!buckets.TryGetValue(key, out list))
            {
                list = new List<int>();
                buckets[key] = list;
            }
            list.Add(idx);
        }

        private static void ClassifyElectricalTexts(
            List<string> texts,
            out string circuitTag,
            out string rating,
            out string cableSpec,
            out string loadText,
            out double loadKwNumeric,
            out string notes)
        {
            circuitTag = "";
            rating = "";
            cableSpec = "";
            loadText = "";
            loadKwNumeric = 0.0;
            List<string> extraNotes = new List<string>();

            Regex ampRx = new Regex(@"(?i)\b(\d+(?:\.\d+)?)\s*(AT|AF|AMPS?|A)\b");
            Regex kwRx = new Regex(@"(?i)\b(\d+(?:\.\d+)?)\s*(KW|KVA|VA|HP|W)\b");
            Regex cableRx = new Regex(@"(?i)\b(\d+\s*[xX×]\s*\d+(?:\.\d+)?(?:\s*\+\s*\d+(?:\.\d+)?[eE]?)?(?:\s*mm[2²]?)?)\b");
            Regex tagRx = new Regex(@"(?i)^([A-Z]{1,4}[-]?\d{1,4}(?:[-/][A-Z0-9]{1,4})?)$");

            for (int i = 0; i < texts.Count; i++)
            {
                string raw = (texts[i] ?? "").Trim();
                if (string.IsNullOrEmpty(raw)) continue;
                bool matchedAny = false;

                if (raw.IndexOf("SPARE", StringComparison.OrdinalIgnoreCase) >= 0 || raw.Contains("احتياطي"))
                {
                    if (string.IsNullOrEmpty(rating)) rating = "SPARE";
                    matchedAny = true;
                }

                Match am = ampRx.Match(raw);
                if (am.Success)
                {
                    string val = am.Groups[1].Value + " " + am.Groups[2].Value.ToUpperInvariant();
                    if (string.IsNullOrEmpty(rating) || rating == "SPARE") rating = val;
                    matchedAny = true;
                }

                Match km = kwRx.Match(raw);
                if (km.Success)
                {
                    string unit = km.Groups[2].Value.ToUpperInvariant();
                    double num;
                    if (double.TryParse(km.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                    {
                        double kw = num;
                        if (unit == "W") kw = num / 1000.0;
                        else if (unit == "VA") kw = num / 1000.0;
                        else if (unit == "HP") kw = num * 0.746;
                        if (kw > loadKwNumeric) loadKwNumeric = kw;
                        if (string.IsNullOrEmpty(loadText)) loadText = km.Groups[1].Value + " " + unit;
                    }
                    matchedAny = true;
                }

                Match cm = cableRx.Match(raw);
                if (cm.Success)
                {
                    if (string.IsNullOrEmpty(cableSpec)) cableSpec = cm.Groups[1].Value.Replace(" ", "");
                    matchedAny = true;
                }

                if (!matchedAny && tagRx.IsMatch(raw) && string.IsNullOrEmpty(circuitTag))
                {
                    circuitTag = raw;
                    matchedAny = true;
                }

                if (!matchedAny || raw.Length > 10)
                {
                    if (!extraNotes.Contains(raw)) extraNotes.Add(raw);
                }
            }

            notes = string.Join(" | ", extraNotes.ToArray());
            if (notes.Length > 80) notes = notes.Substring(0, 77) + "...";
        }

        private static bool SegmentTouchesExtents(PathSegmentItem seg, Extents3d ex, double tol)
        {
            double x1 = ex.MinPoint.X - tol;
            double y1 = ex.MinPoint.Y - tol;
            double x2 = ex.MaxPoint.X + tol;
            double y2 = ex.MaxPoint.Y + tol;
            bool sIn = seg.Start.X >= x1 && seg.Start.X <= x2 && seg.Start.Y >= y1 && seg.Start.Y <= y2;
            bool eIn = seg.End.X >= x1 && seg.End.X <= x2 && seg.End.Y >= y1 && seg.End.Y <= y2;
            return sIn || eIn;
        }

        private static Dictionary<string, object> ExtractLoadSchedule(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            string mode = (GetStr(args, "mode") ?? "auto").Trim().ToLowerInvariant();
            string blockFilter = GetStr(args, "block_name");
            string feederLayer = GetStr(args, "feeder_layer") ?? GetStr(args, "layer_filter");
            bool selectedOnly = GetBool(args, "selected_only", true);
            bool tracePaths = GetBool(args, "trace_paths", true);
            double? reqRadius = GetNullableDouble(args, "search_radius");
            string sortOrderRaw = GetStr(args, "sort_order") ?? "left_to_right";
            string sortOrder = NormalizeCoordSortOrder(sortOrderRaw);
            if (string.IsNullOrEmpty(sortOrder)) sortOrder = "left_to_right";
            int decimals = Math.Max(0, Math.Min(4, GetInt(args, "decimals", 2)));
            bool drawTable = GetBool(args, "draw_table", true);
            bool exportExcel = GetBool(args, "export_excel", false);
            string excelPath = GetStr(args, "excel_path");
            string title = GetStr(args, "title");
            string tablePos = GetStr(args, "table_position") ?? "outside_right";
            double? tableGap = GetNullableDouble(args, "table_gap");
            bool replaceExisting = GetBool(args, "replace_existing", true);
            bool zoom = GetBool(args, "zoom", true);

            double unitsPerM = Convert.ToDouble(GetUnitsInfo(db)["units_per_m"], CultureInfo.InvariantCulture);
            if (unitsPerM <= 1e-9) unitsPerM = 1000.0;
            double unitsPerCm = Convert.ToDouble(GetUnitsInfo(db)["units_per_cm"], CultureInfo.InvariantCulture);

            List<string> selHandles = GetSelectedHandleStrings(doc);
            HashSet<string> selSet = new HashSet<string>(selHandles, StringComparer.OrdinalIgnoreCase);

            List<LoadScheduleRow> rows = new List<LoadScheduleRow>();
            Dictionary<string, int> ratingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();
            Extents3d? tableExt = null;
            Extents3d? mainDrawingExt = null;
            double totalLengthM = 0;
            double totalKw = 0;
            string inferredSampleMode = mode;

            bool switchedToModel = false;
            try { if (!db.TileMode) { db.TileMode = true; switchedToModel = true; } } catch { }

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);

                    // Inspect selection sample first if present
                    List<string> sampleBlockNames = new List<string>();
                    List<string> samplePathLayers = new List<string>();
                    List<int> samplePathColors = new List<int>();
                    foreach (string sh in selHandles)
                    {
                        ObjectId oid = ResolveHandle(db, sh);
                        if (oid.IsNull || oid.IsErased) continue;
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        BlockReference sbr = ent as BlockReference;
                        if (sbr != null)
                        {
                            string bn = ResolveBlockName(tr, sbr);
                            if (!IsAnonymousBlockName(bn) && !IsSystemBlockName(bn) && !sampleBlockNames.Contains(bn))
                                sampleBlockNames.Add(bn);
                        }
                        else if (ent is Line || ent is Polyline || ent is Arc)
                        {
                            if (!string.IsNullOrEmpty(ent.Layer) && !samplePathLayers.Contains(ent.Layer))
                                samplePathLayers.Add(ent.Layer);
                            if (!samplePathColors.Contains(ent.ColorIndex))
                                samplePathColors.Add(ent.ColorIndex);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(blockFilter) && sampleBlockNames.Count == 1)
                        blockFilter = sampleBlockNames[0];

                    // Collect all texts, paths, and candidate blocks in a single clean scan
                    List<NearbyTextItem> allTexts = new List<NearbyTextItem>();
                    List<PathSegmentItem> allPaths = new List<PathSegmentItem>();
                    List<CoordTargetItem> targetBlocks = new List<CoordTargetItem>();
                    List<double> blockSizes = new List<double>();
                    List<double> textHeights = new List<double>();

                    foreach (ObjectId mid in ms)
                    {
                        if (mid.IsErased) continue;
                        Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        if (IsAiAnnotationLayer(ent.Layer)) continue;
                        if (!IsEntityVisible(ent, layerHiddenFlags)) continue;

                        Extents3d ex;
                        try { ex = ent.GeometricExtents; }
                        catch { continue; }
                        if (!IsFiniteBounds(ex)) continue;

                        if (!mainDrawingExt.HasValue) mainDrawingExt = ex;
                        else
                        {
                            Extents3d m = mainDrawingExt.Value;
                            m.AddExtents(ex);
                            mainDrawingExt = m;
                        }

                        if (ent is DBText || ent is MText)
                        {
                            string txt = ExtractEntityTextString(ent);
                            if (!string.IsNullOrWhiteSpace(txt))
                            {
                                allTexts.Add(new NearbyTextItem
                                {
                                    Handle = ent.Handle.ToString(),
                                    Text = txt,
                                    Cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5,
                                    Cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5,
                                    Bounds = ex
                                });
                                double th = (ent is MText) ? ((MText)ent).TextHeight : ((DBText)ent).Height;
                                if (th > 1e-6 && th < 1e6) textHeights.Add(th);
                            }
                            continue;
                        }

                        if (tracePaths && (ent is Line || ent is Polyline || ent is Arc))
                        {
                            bool layerOk = string.IsNullOrWhiteSpace(feederLayer) ||
                                           MatchesPatternOrSubstring(ent.Layer ?? "", feederLayer);
                            if (layerOk)
                            {
                                Curve cv = ent as Curve;
                                double len = 0;
                                try { len = cv.GetDistanceAtParameter(cv.EndParam) - cv.GetDistanceAtParameter(cv.StartParam); }
                                catch { len = ex.MinPoint.DistanceTo(ex.MaxPoint); }
                                if (len > 1e-6)
                                {
                                    Point3d sp = Point3d.Origin;
                                    Point3d ep = Point3d.Origin;
                                    try { sp = cv.StartPoint; ep = cv.EndPoint; } catch { }
                                    allPaths.Add(new PathSegmentItem
                                    {
                                        Id = mid,
                                        Handle = ent.Handle.ToString(),
                                        Layer = ent.Layer ?? "0",
                                        ColorIndex = ent.ColorIndex,
                                        Start = new Point2d(sp.X, sp.Y),
                                        End = new Point2d(ep.X, ep.Y),
                                        Bounds = ex,
                                        Length = len,
                                        IsClosed = cv.Closed
                                    });
                                }
                            }
                            continue;
                        }

                        BlockReference br = ent as BlockReference;
                        if (br != null)
                        {
                            string bn = ResolveBlockName(tr, br);
                            if (IsAnonymousBlockName(bn) || IsSystemBlockName(bn)) continue;
                            bool matchBlock = string.IsNullOrWhiteSpace(blockFilter)
                                ? (sampleBlockNames.Count == 0 || sampleBlockNames.Contains(bn))
                                : (string.Equals(bn, blockFilter, StringComparison.OrdinalIgnoreCase) || MatchesPatternOrSubstring(bn, blockFilter));
                            if (!matchBlock && selSet.Count > 0 && selectedOnly && sampleBlockNames.Count > 1 && !selSet.Contains(ent.Handle.ToString()))
                                continue;
                            if (!matchBlock) continue;

                            double cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                            double cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                            double bw = Math.Max(1e-6, ex.MaxPoint.X - ex.MinPoint.X);
                            double bh = Math.Max(1e-6, ex.MaxPoint.Y - ex.MinPoint.Y);
                            blockSizes.Add(Math.Max(bw, bh));
                            targetBlocks.Add(new CoordTargetItem
                            {
                                Id = mid,
                                Handle = ent.Handle.ToString(),
                                BlockName = bn,
                                CenterX = cx,
                                CenterY = cy,
                                ArrowX = cx,
                                ArrowY = cy,
                                Bounds = ex
                            });
                        }
                    }

                    textHeights.Sort();
                    double medTextH = textHeights.Count > 0 ? textHeights[textHeights.Count / 2] : (2.5 * unitsPerCm);
                    blockSizes.Sort();
                    double medBlk = blockSizes.Count > 0 ? blockSizes[blockSizes.Count / 2] : (medTextH * 3.0);
                    double searchRadius = (reqRadius.HasValue && reqRadius.Value > 1e-6)
                        ? reqRadius.Value
                        : Math.Max(medBlk * 3.8, medTextH * 6.0);
                    double connectTol = Math.Max(medBlk * 0.45, medTextH * 0.8);

                    // Build spatial endpoint index for fast connected feeder path tracing
                    double bucketSize = Math.Max(1e-3, connectTol * 2.0);
                    Dictionary<string, List<int>> endpointBuckets = new Dictionary<string, List<int>>();
                    for (int p = 0; p < allPaths.Count; p++)
                    {
                        AddPointToBucket(endpointBuckets, allPaths[p].Start.X, allPaths[p].Start.Y, p, bucketSize);
                        AddPointToBucket(endpointBuckets, allPaths[p].End.X, allPaths[p].End.Y, p, bucketSize);
                    }

                    // Case A: Block-anchored circuits/breakers (or blocks + traced paths)
                    if (targetBlocks.Count > 0 && mode != "paths")
                    {
                        inferredSampleMode = "blocks_with_paths";
                        double medW = medBlk;
                        double medH = medBlk;
                        SortCoordTargets(targetBlocks, sortOrder, medW, medH);

                        for (int i = 0; i < targetBlocks.Count; i++)
                        {
                            CoordTargetItem blk = targetBlocks[i];
                            List<string> gatheredTexts = new List<string>();

                            // 1. Read any Attributes inside the BlockReference itself!
                            BlockReference brEnt = tr.GetObject(blk.Id, OpenMode.ForRead) as BlockReference;
                            if (brEnt != null && brEnt.AttributeCollection != null)
                            {
                                foreach (ObjectId aid in brEnt.AttributeCollection)
                                {
                                    AttributeReference att = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                    if (att == null || att.IsErased) continue;
                                    string av = CleanCadTextValue(att.TextString);
                                    if (!string.IsNullOrWhiteSpace(av)) gatheredTexts.Add(av);
                                }
                            }

                            // 2. Collect nearby texts sorted by distance to block center
                            List<KeyValuePair<double, string>> nearList = new List<KeyValuePair<double, string>>();
                            for (int t = 0; t < allTexts.Count; t++)
                            {
                                double dx = allTexts[t].Cx - blk.CenterX;
                                double dy = allTexts[t].Cy - blk.CenterY;
                                double dist = Math.Sqrt(dx * dx + dy * dy);
                                if (dist <= searchRadius)
                                    nearList.Add(new KeyValuePair<double, string>(dist, allTexts[t].Text));
                            }
                            nearList.Sort(delegate(KeyValuePair<double, string> a, KeyValuePair<double, string> b) { return a.Key.CompareTo(b.Key); });
                            int takeNear = Math.Min(6, nearList.Count);
                            for (int k = 0; k < takeNear; k++)
                            {
                                if (!gatheredTexts.Contains(nearList[k].Value))
                                    gatheredTexts.Add(nearList[k].Value);
                            }

                            // 3. Trace connected feeder segments starting from block bounds (up to 40 segments per circuit)
                            double tracedLen = 0.0;
                            int segCount = 0;
                            if (tracePaths && allPaths.Count > 0)
                            {
                                HashSet<int> visitedSeg = new HashSet<int>();
                                Queue<int> q = new Queue<int>();
                                for (int p = 0; p < allPaths.Count; p++)
                                {
                                    if (SegmentTouchesExtents(allPaths[p], blk.Bounds, connectTol))
                                    {
                                        visitedSeg.Add(p);
                                        q.Enqueue(p);
                                    }
                                }
                                while (q.Count > 0 && segCount < 40)
                                {
                                    int idxP = q.Dequeue();
                                    PathSegmentItem seg = allPaths[idxP];
                                    tracedLen += seg.Length;
                                    segCount++;

                                    // Collect texts touching segment endpoints too
                                    for (int t = 0; t < allTexts.Count; t++)
                                    {
                                        double ds = Math.Sqrt(Math.Pow(allTexts[t].Cx - seg.Start.X, 2) + Math.Pow(allTexts[t].Cy - seg.Start.Y, 2));
                                        double de = Math.Sqrt(Math.Pow(allTexts[t].Cx - seg.End.X, 2) + Math.Pow(allTexts[t].Cy - seg.End.Y, 2));
                                        if (ds <= searchRadius * 0.65 || de <= searchRadius * 0.65)
                                        {
                                            if (!gatheredTexts.Contains(allTexts[t].Text))
                                                gatheredTexts.Add(allTexts[t].Text);
                                        }
                                    }

                                    // Follow connected segments matching same layer (prevents jumping across shared neutral busbars on unrelated layers)
                                    Point2d[] pts = new Point2d[] { seg.Start, seg.End };
                                    for (int epIdx = 0; epIdx < pts.Length; epIdx++)
                                    {
                                        long bx = (long)Math.Floor(pts[epIdx].X / bucketSize);
                                        long by = (long)Math.Floor(pts[epIdx].Y / bucketSize);
                                        for (long ox = -1; ox <= 1; ox++)
                                        {
                                            for (long oy = -1; oy <= 1; oy++)
                                            {
                                                string bKey = (bx + ox).ToString(CultureInfo.InvariantCulture) + ":" + (by + oy).ToString(CultureInfo.InvariantCulture);
                                                List<int> cands;
                                                if (!endpointBuckets.TryGetValue(bKey, out cands)) continue;
                                                for (int ci = 0; ci < cands.Count; ci++)
                                                {
                                                    int candIdx = cands[ci];
                                                    if (visitedSeg.Contains(candIdx)) continue;
                                                    PathSegmentItem cand = allPaths[candIdx];
                                                    if (!string.Equals(cand.Layer, seg.Layer, StringComparison.OrdinalIgnoreCase)) continue;
                                                    double d1 = Math.Sqrt(Math.Pow(pts[epIdx].X - cand.Start.X, 2) + Math.Pow(pts[epIdx].Y - cand.Start.Y, 2));
                                                    double d2 = Math.Sqrt(Math.Pow(pts[epIdx].X - cand.End.X, 2) + Math.Pow(pts[epIdx].Y - cand.End.Y, 2));
                                                    if (d1 <= connectTol || d2 <= connectTol)
                                                    {
                                                        visitedSeg.Add(candIdx);
                                                        q.Enqueue(candIdx);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            string cTag, rating, cableSpec, loadTxt, notes;
                            double loadKw;
                            ClassifyElectricalTexts(gatheredTexts, out cTag, out rating, out cableSpec, out loadTxt, out loadKw, out notes);
                            if (string.IsNullOrEmpty(cTag)) cTag = "#" + (i + 1).ToString(CultureInfo.InvariantCulture);

                            double lenM = tracedLen / unitsPerM;
                            totalLengthM += lenM;
                            totalKw += loadKw;

                            string ratingBucket = string.IsNullOrEmpty(rating) ? "Unlabeled" : rating;
                            int rc;
                            ratingCounts.TryGetValue(ratingBucket, out rc);
                            ratingCounts[ratingBucket] = rc + 1;

                            rows.Add(new LoadScheduleRow
                            {
                                Index = i + 1,
                                Handle = blk.Handle,
                                CircuitTag = cTag,
                                BlockName = blk.BlockName,
                                Rating = rating,
                                CableSpec = cableSpec,
                                LoadText = loadTxt,
                                LoadKwNumeric = loadKw,
                                PathLengthUnits = tracedLen,
                                PathLengthMeters = lenM,
                                ConnectedSegments = segCount,
                                Description = notes,
                                X = blk.CenterX,
                                Y = blk.CenterY
                            });
                        }
                    }
                    else
                    {
                        // Case B: Sample feeder paths without blocks (pure polyline/line feeder circuits!)
                        inferredSampleMode = "sample_feeder_paths";
                        List<PathSegmentItem> candidatePaths = new List<PathSegmentItem>();
                        for (int p = 0; p < allPaths.Count; p++)
                        {
                            PathSegmentItem sp = allPaths[p];
                            if (samplePathLayers.Count > 0 && !samplePathLayers.Contains(sp.Layer)) continue;
                            if (samplePathColors.Count > 0 && !samplePathColors.Contains(sp.ColorIndex)) continue;
                            candidatePaths.Add(sp);
                        }
                        candidatePaths.Sort(delegate(PathSegmentItem a, PathSegmentItem b)
                        {
                            int cy = b.Bounds.MaxPoint.Y.CompareTo(a.Bounds.MaxPoint.Y);
                            return cy != 0 ? cy : a.Bounds.MinPoint.X.CompareTo(b.Bounds.MinPoint.X);
                        });

                        int maxPathRows = Math.Min(200, candidatePaths.Count);
                        for (int i = 0; i < maxPathRows; i++)
                        {
                            PathSegmentItem seg = candidatePaths[i];
                            List<string> pathTexts = new List<string>();
                            for (int t = 0; t < allTexts.Count; t++)
                            {
                                double dStart = Math.Sqrt(Math.Pow(allTexts[t].Cx - seg.Start.X, 2) + Math.Pow(allTexts[t].Cy - seg.Start.Y, 2));
                                double dEnd = Math.Sqrt(Math.Pow(allTexts[t].Cx - seg.End.X, 2) + Math.Pow(allTexts[t].Cy - seg.End.Y, 2));
                                double dMid = Math.Sqrt(Math.Pow(allTexts[t].Cx - (seg.Start.X + seg.End.X) * 0.5, 2) + Math.Pow(allTexts[t].Cy - (seg.Start.Y + seg.End.Y) * 0.5, 2));
                                if (Math.Min(dMid, Math.Min(dStart, dEnd)) <= searchRadius)
                                    pathTexts.Add(allTexts[t].Text);
                            }

                            string cTag, rating, cableSpec, loadTxt, notes;
                            double loadKw;
                            ClassifyElectricalTexts(pathTexts, out cTag, out rating, out cableSpec, out loadTxt, out loadKw, out notes);
                            if (string.IsNullOrEmpty(cTag)) cTag = "P" + (i + 1).ToString(CultureInfo.InvariantCulture);

                            double lenM = seg.Length / unitsPerM;
                            totalLengthM += lenM;
                            totalKw += loadKw;
                            string rBucket = string.IsNullOrEmpty(rating) ? "Unlabeled" : rating;
                            int rc;
                            ratingCounts.TryGetValue(rBucket, out rc);
                            ratingCounts[rBucket] = rc + 1;

                            rows.Add(new LoadScheduleRow
                            {
                                Index = i + 1,
                                Handle = seg.Handle,
                                CircuitTag = cTag,
                                BlockName = seg.Layer,
                                Rating = rating,
                                CableSpec = cableSpec,
                                LoadText = loadTxt,
                                LoadKwNumeric = loadKw,
                                PathLengthUnits = seg.Length,
                                PathLengthMeters = lenM,
                                ConnectedSegments = 1,
                                Description = notes,
                                X = (seg.Start.X + seg.End.X) * 0.5,
                                Y = (seg.Start.Y + seg.End.Y) * 0.5
                            });
                        }
                    }

                    if (rows.Count == 0)
                    {
                        tr.Commit();
                        return Err("لم يتم العثور على بلوكات قواطع أو مسارات عينة كافية لاستخراج جدول الأحمال. حدد عينة بلوك أو مسار في AutoCAD أو مرّر block_name / feeder_layer.");
                    }

                    string numFmt = "0." + new string('0', decimals);
                    string sheetTitle = !string.IsNullOrWhiteSpace(title)
                        ? title
                        : "جدول الأحمال الكهربائية والمسارات (LOAD SCHEDULE)";
                    ExcelSheetData scheduleSheet = new ExcelSheetData
                    {
                        Title = sheetTitle,
                        SheetName = "Load_Schedule",
                        Headers = new List<string> { "م", "الدائرة", "النوع/البلوك", "الأمبير/التصنيف", "الكابل", "الحمل", "طول المسار (م)", "البيان / الوصف" },
                        FooterRow = new List<string>
                        {
                            "",
                            "الإجمالي (" + rows.Count.ToString(CultureInfo.InvariantCulture) + ")",
                            "", "", "",
                            totalKw > 0 ? (totalKw.ToString(numFmt, CultureInfo.InvariantCulture) + " kW") : "",
                            totalLengthM.ToString(numFmt, CultureInfo.InvariantCulture),
                            ""
                        }
                    };
                    for (int r = 0; r < rows.Count; r++)
                    {
                        LoadScheduleRow row = rows[r];
                        scheduleSheet.Rows.Add(new List<string>
                        {
                            row.Index.ToString(CultureInfo.InvariantCulture),
                            row.CircuitTag,
                            row.BlockName,
                            row.Rating,
                            row.CableSpec,
                            row.LoadText,
                            row.PathLengthMeters.ToString(numFmt, CultureInfo.InvariantCulture),
                            row.Description
                        });
                    }

                    lock (s_lastCoordLock)
                    {
                        s_lastPanelScheduleByDb[db.UnmanagedObject] = scheduleSheet;
                        s_lastGeneratedTableKindByDb[db.UnmanagedObject] = "panel_schedule";
                    }

                    if (drawTable)
                    {
                        tableExt = RenderStyledScheduleTable(
                            doc, db, ms, tr, scheduleSheet, "_AI_PANEL_SCHEDULE",
                            replaceExisting, tablePos, tableGap, null, mainDrawingExt,
                            createdIds, erasedIds);
                    }

                    db.UpdateExt(true);
                    tr.Commit();
                }

                if (createdIds.Count > 0 || erasedIds.Count > 0)
                {
                    CadUndoEntry undo = new CadUndoEntry();
                    undo.ToolName = "extract_load_schedule";
                    undo.Summary = "Extracted Load Schedule (" + rows.Count.ToString(CultureInfo.InvariantCulture) + " circuits)";
                    undo.ErasedIds.AddRange(erasedIds);
                    undo.CreatedIds.AddRange(createdIds);
                    PushUndoEntry(doc, undo);
                }

                if (zoom && tableExt.HasValue && IsFiniteBounds(tableExt.Value))
                    ZoomToExtents3d(doc, tableExt.Value, 1.18);
                else
                    doc.Editor.Regen();
                doc.Editor.UpdateScreen();
            }

            List<object> circuitOut = new List<object>();
            int limitOut = Math.Min(80, rows.Count);
            for (int i = 0; i < limitOut; i++)
            {
                LoadScheduleRow row = rows[i];
                circuitOut.Add(new Dictionary<string, object>
                {
                    { "index", row.Index },
                    { "handle", row.Handle },
                    { "circuit", row.CircuitTag },
                    { "block_name", row.BlockName },
                    { "rating", row.Rating },
                    { "cable", row.CableSpec },
                    { "load", row.LoadText },
                    { "path_length_m", Math.Round(row.PathLengthMeters, decimals) },
                    { "connected_segments", row.ConnectedSegments },
                    { "description", row.Description },
                    { "x", Round(row.X) },
                    { "y", Round(row.Y) }
                });
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "mode_used", inferredSampleMode },
                { "total_circuits", rows.Count },
                { "total_path_length_m", Math.Round(totalLengthM, decimals) },
                { "estimated_total_kw", Math.Round(totalKw, decimals) },
                { "rating_summary", ratingCounts },
                { "table_drawn", drawTable },
                { "table_layer", "_AI_PANEL_SCHEDULE" },
                { "created_count", createdIds.Count },
                { "erased_count", erasedIds.Count },
                { "switched_to_model", switchedToModel },
                { "circuits", circuitOut }
            };
            if (tableExt.HasValue)
            {
                res["table_bounds"] = new double[]
                {
                    Round(tableExt.Value.MinPoint.X), Round(tableExt.Value.MinPoint.Y),
                    Round(tableExt.Value.MaxPoint.X), Round(tableExt.Value.MaxPoint.Y)
                };
            }
            if (exportExcel)
            {
                ExcelSheetData xlSheet;
                lock (s_lastCoordLock) { s_lastPanelScheduleByDb.TryGetValue(db.UnmanagedObject, out xlSheet); }
                if (xlSheet != null)
                {
                    string xlTarget = excelPath ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "load_schedule", ".xlsx");
                    string savedPath = ExcelTableInterop.WriteSpreadsheet(xlTarget, xlSheet);
                    res["excel_exported"] = true;
                    res["excel_path"] = savedPath;
                }
            }
            return Ok(res);
        }

        private static Dictionary<string, object> QuantityTakeoff(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            List<string> layerFilters = ExtractStringListArg(args, "layers");
            string singleLayer = GetStr(args, "layer");
            if (!string.IsNullOrWhiteSpace(singleLayer) && !layerFilters.Contains(singleLayer))
                layerFilters.Add(singleLayer);

            bool selectedOnly = GetBool(args, "selected_only", false);
            double[] bbox = GetDoubleArray(args, "bbox");
            double wastePercent = Math.Max(0.0, Math.Min(100.0, GetDouble(args, "waste_percent", 0.0)));
            double verticalAllowM = Math.Max(0.0, GetDouble(args, "vertical_allowance_m", 0.0));
            double termAllowM = Math.Max(0.0, GetDouble(args, "termination_allowance_m", 0.0));
            bool labelAreas = GetBool(args, "label_areas", false);
            bool drawTable = GetBool(args, "draw_table", true);
            bool exportExcel = GetBool(args, "export_excel", false);
            string excelPath = GetStr(args, "excel_path");
            string title = GetStr(args, "title");
            string tablePos = GetStr(args, "table_position") ?? "outside_right";
            double? tableGap = GetNullableDouble(args, "table_gap");
            bool replaceExisting = GetBool(args, "replace_existing", true);
            bool zoom = GetBool(args, "zoom", true);
            int decimals = Math.Max(0, Math.Min(4, GetInt(args, "decimals", 2)));

            double unitsPerM = Convert.ToDouble(GetUnitsInfo(db)["units_per_m"], CultureInfo.InvariantCulture);
            if (unitsPerM <= 1e-9) unitsPerM = 1000.0;
            double unitsPerCm = Convert.ToDouble(GetUnitsInfo(db)["units_per_cm"], CultureInfo.InvariantCulture);
            double unitsPerM2 = unitsPerM * unitsPerM;

            HashSet<string> selSet = new HashSet<string>(GetSelectedHandleStrings(doc), StringComparer.OrdinalIgnoreCase);

            Dictionary<string, double> linearLenByLayer = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> linearCountByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> areaByLayer = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> areaCountByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> blockCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<Point3d, string>> areaLabelsToDraw = new List<KeyValuePair<Point3d, string>>();
            List<NearbyTextItem> textsForRoomMatch = new List<NearbyTextItem>();

            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();
            Extents3d? mainDrawingExt = null;
            Extents3d? tableExt = null;
            double grandTotalLinearWithWasteM = 0;
            double grandTotalAreaM2 = 0;
            int grandTotalBlocks = 0;

            bool switchedToModel = false;
            try { if (!db.TileMode) { db.TileMode = true; switchedToModel = true; } } catch { }

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);

                    if (labelAreas)
                    {
                        foreach (ObjectId tid in ms)
                        {
                            Entity te = tr.GetObject(tid, OpenMode.ForRead) as Entity;
                            if (te == null || te.IsErased || IsAiAnnotationLayer(te.Layer)) continue;
                            if (!(te is DBText) && !(te is MText)) continue;
                            try
                            {
                                Extents3d tex = te.GeometricExtents;
                                if (!IsFiniteBounds(tex)) continue;
                                textsForRoomMatch.Add(new NearbyTextItem
                                {
                                    Text = ExtractEntityTextString(te),
                                    Cx = (tex.MinPoint.X + tex.MaxPoint.X) * 0.5,
                                    Cy = (tex.MinPoint.Y + tex.MaxPoint.Y) * 0.5,
                                    Bounds = tex
                                });
                            }
                            catch { }
                        }
                    }

                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        if (IsAiAnnotationLayer(ent.Layer)) continue;
                        if (!IsEntityVisible(ent, layerHiddenFlags)) continue;

                        Extents3d ex;
                        try { ex = ent.GeometricExtents; }
                        catch { continue; }
                        if (!IsFiniteBounds(ex)) continue;

                        if (!mainDrawingExt.HasValue) mainDrawingExt = ex;
                        else
                        {
                            Extents3d m = mainDrawingExt.Value;
                            m.AddExtents(ex);
                            mainDrawingExt = m;
                        }

                        if (selectedOnly && selSet.Count > 0 && !selSet.Contains(ent.Handle.ToString())) continue;
                        if (bbox != null && bbox.Length >= 4)
                        {
                            double cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                            double cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                            if (cx < bbox[0] || cx > bbox[2] || cy < bbox[1] || cy > bbox[3]) continue;
                        }
                        if (layerFilters.Count > 0)
                        {
                            bool okLyr = false;
                            for (int li = 0; li < layerFilters.Count; li++)
                            {
                                if (MatchesPatternOrSubstring(ent.Layer ?? "", layerFilters[li])) { okLyr = true; break; }
                            }
                            if (!okLyr) continue;
                        }

                        string lyr = ent.Layer ?? "0";
                        BlockReference br = ent as BlockReference;
                        if (br != null)
                        {
                            string bn = ResolveBlockName(tr, br);
                            if (!IsAnonymousBlockName(bn) && !IsSystemBlockName(bn))
                            {
                                int bc;
                                blockCounts.TryGetValue(bn, out bc);
                                blockCounts[bn] = bc + 1;
                            }
                            continue;
                        }

                        Hatch ht = ent as Hatch;
                        if (ht != null)
                        {
                            try
                            {
                                double ar = ht.Area;
                                if (ar > 1e-6)
                                {
                                    double prev;
                                    areaByLayer.TryGetValue(lyr, out prev);
                                    areaByLayer[lyr] = prev + ar;
                                    int ac;
                                    areaCountByLayer.TryGetValue(lyr, out ac);
                                    areaCountByLayer[lyr] = ac + 1;
                                }
                            }
                            catch { }
                            continue;
                        }

                        Curve cv = ent as Curve;
                        if (cv != null)
                        {
                            double len = 0;
                            try { len = cv.GetDistanceAtParameter(cv.EndParam) - cv.GetDistanceAtParameter(cv.StartParam); }
                            catch { len = 0; }
                            if (len > 1e-6)
                            {
                                double prev;
                                linearLenByLayer.TryGetValue(lyr, out prev);
                                linearLenByLayer[lyr] = prev + len;
                                int lc;
                                linearCountByLayer.TryGetValue(lyr, out lc);
                                linearCountByLayer[lyr] = lc + 1;
                            }

                            if (cv.Closed || ent is Circle)
                            {
                                double ar = 0;
                                try { ar = cv.Area; }
                                catch { ar = 0; }
                                if (ar > 1e-6)
                                {
                                    double prev;
                                    areaByLayer.TryGetValue(lyr, out prev);
                                    areaByLayer[lyr] = prev + ar;
                                    int ac;
                                    areaCountByLayer.TryGetValue(lyr, out ac);
                                    areaCountByLayer[lyr] = ac + 1;

                                    if (labelAreas)
                                    {
                                        double cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                                        double cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                                        string rName = "";
                                        double bestD = double.MaxValue;
                                        for (int ti = 0; ti < textsForRoomMatch.Count; ti++)
                                        {
                                            NearbyTextItem nt = textsForRoomMatch[ti];
                                            if (nt.Cx >= ex.MinPoint.X && nt.Cx <= ex.MaxPoint.X && nt.Cy >= ex.MinPoint.Y && nt.Cy <= ex.MaxPoint.Y)
                                            {
                                                double d = Math.Sqrt(Math.Pow(nt.Cx - cx, 2) + Math.Pow(nt.Cy - cy, 2));
                                                if (d < bestD) { bestD = d; rName = nt.Text; }
                                            }
                                        }
                                        string lbl = string.IsNullOrEmpty(rName)
                                            ? ((ar / unitsPerM2).ToString("0.00", CultureInfo.InvariantCulture) + " m²")
                                            : (rName + "\\P" + (ar / unitsPerM2).ToString("0.00", CultureInfo.InvariantCulture) + " m²");
                                        areaLabelsToDraw.Add(new KeyValuePair<Point3d, string>(new Point3d(cx, cy, 0.0), lbl));
                                    }
                                }
                            }
                        }
                    }

                    if (labelAreas && areaLabelsToDraw.Count > 0)
                    {
                        string lblLayer = EnsureLayerSafe(db, tr, "_AI_AREA_LABELS", 3, "Continuous");
                        if (replaceExisting)
                        {
                            foreach (ObjectId oid in ms)
                            {
                                Entity oe = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                                if (oe != null && !oe.IsErased && string.Equals(oe.Layer, lblLayer, StringComparison.OrdinalIgnoreCase))
                                {
                                    oe.UpgradeOpen();
                                    oe.Erase(true);
                                    erasedIds.Add(oid);
                                }
                            }
                        }
                        ObjectId unicodeStyleId = EnsureUnicodeTextStyle(db, tr);
                        double lblH = 2.0 * unitsPerCm;
                        if (mainDrawingExt.HasValue)
                        {
                            double sp = Math.Max(mainDrawingExt.Value.MaxPoint.X - mainDrawingExt.Value.MinPoint.X,
                                                 mainDrawingExt.Value.MaxPoint.Y - mainDrawingExt.Value.MinPoint.Y);
                            lblH = Math.Max(0.4 * unitsPerCm, Math.Min(6.0 * unitsPerCm, sp * 0.01));
                        }
                        for (int i = 0; i < areaLabelsToDraw.Count; i++)
                        {
                            MText mt = new MText();
                            mt.SetDatabaseDefaults(db);
                            mt.Layer = lblLayer;
                            mt.ColorIndex = 3;
                            mt.TextStyleId = unicodeStyleId;
                            mt.TextHeight = lblH;
                            mt.Attachment = AttachmentPoint.MiddleCenter;
                            mt.Location = areaLabelsToDraw[i].Key;
                            mt.Contents = areaLabelsToDraw[i].Value;
                            ms.AppendEntity(mt);
                            tr.AddNewlyCreatedDBObject(mt, true);
                            createdIds.Add(mt.ObjectId);
                        }
                    }

                    string numFmt = "0." + new string('0', decimals);
                    ExcelSheetData boqSheet = new ExcelSheetData
                    {
                        Title = !string.IsNullOrWhiteSpace(title) ? title : "جدول حصر الكميات والأطوال والمساحات (BOQ TAKEOFF)",
                        SheetName = "BOQ_Takeoff",
                        Headers = new List<string> { "م", "الفئة / النوع", "البند / الطبقة", "العدد", "صافي القياس", "شامل الهالك (" + wastePercent.ToString("0.#", CultureInfo.InvariantCulture) + "%)", "الوحدة" }
                    };

                    int rowIdx = 1;

                    List<string> linLayers = new List<string>(linearLenByLayer.Keys);
                    linLayers.Sort(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < linLayers.Count; i++)
                    {
                        string lName = linLayers[i];
                        int cnt = linearCountByLayer[lName];
                        double netM = linearLenByLayer[lName] / unitsPerM;
                        double addM = cnt * (verticalAllowM + termAllowM);
                        double withWasteM = (netM + addM) * (1.0 + wastePercent / 100.0);
                        grandTotalLinearWithWasteM += withWasteM;
                        boqSheet.Rows.Add(new List<string>
                        {
                            (rowIdx++).ToString(CultureInfo.InvariantCulture),
                            "أطوال مسارات",
                            lName,
                            cnt.ToString(CultureInfo.InvariantCulture),
                            netM.ToString(numFmt, CultureInfo.InvariantCulture),
                            withWasteM.ToString(numFmt, CultureInfo.InvariantCulture),
                            "m"
                        });
                    }

                    List<string> arLayers = new List<string>(areaByLayer.Keys);
                    arLayers.Sort(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < arLayers.Count; i++)
                    {
                        string lName = arLayers[i];
                        int cnt = areaCountByLayer[lName];
                        double netM2 = areaByLayer[lName] / unitsPerM2;
                        double withWasteM2 = netM2 * (1.0 + wastePercent / 100.0);
                        grandTotalAreaM2 += withWasteM2;
                        boqSheet.Rows.Add(new List<string>
                        {
                            (rowIdx++).ToString(CultureInfo.InvariantCulture),
                            "مساحات",
                            lName,
                            cnt.ToString(CultureInfo.InvariantCulture),
                            netM2.ToString(numFmt, CultureInfo.InvariantCulture),
                            withWasteM2.ToString(numFmt, CultureInfo.InvariantCulture),
                            "m²"
                        });
                    }

                    List<string> blkNames = new List<string>(blockCounts.Keys);
                    blkNames.Sort(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < blkNames.Count; i++)
                    {
                        string bn = blkNames[i];
                        int cnt = blockCounts[bn];
                        double withWaste = Math.Ceiling(cnt * (1.0 + wastePercent / 100.0));
                        grandTotalBlocks += cnt;
                        boqSheet.Rows.Add(new List<string>
                        {
                            (rowIdx++).ToString(CultureInfo.InvariantCulture),
                            "بلوكات",
                            bn,
                            cnt.ToString(CultureInfo.InvariantCulture),
                            cnt.ToString(CultureInfo.InvariantCulture),
                            withWaste.ToString("0", CultureInfo.InvariantCulture),
                            "pcs"
                        });
                    }

                    if (boqSheet.Rows.Count == 0)
                    {
                        tr.Commit();
                        return Err("لم يتم العثور على مسارات أو مساحات أو بلوكات مطابقة للحصر.");
                    }

                    boqSheet.FooterRow = new List<string>
                    {
                        "", "الإجمالي",
                        "أطوال: " + grandTotalLinearWithWasteM.ToString(numFmt, CultureInfo.InvariantCulture) + " m",
                        "بلوكات: " + grandTotalBlocks.ToString(CultureInfo.InvariantCulture),
                        "",
                        "مساحات: " + grandTotalAreaM2.ToString(numFmt, CultureInfo.InvariantCulture) + " m²",
                        ""
                    };

                    lock (s_lastCoordLock)
                    {
                        s_lastBoqTableByDb[db.UnmanagedObject] = boqSheet;
                        s_lastGeneratedTableKindByDb[db.UnmanagedObject] = "boq";
                    }

                    if (drawTable)
                    {
                        tableExt = RenderStyledScheduleTable(
                            doc, db, ms, tr, boqSheet, "_AI_BOQ_TABLE",
                            replaceExisting, tablePos, tableGap, null, mainDrawingExt,
                            createdIds, erasedIds);
                    }

                    db.UpdateExt(true);
                    tr.Commit();
                }

                if (createdIds.Count > 0 || erasedIds.Count > 0)
                {
                    CadUndoEntry undo = new CadUndoEntry();
                    undo.ToolName = "quantity_takeoff";
                    undo.Summary = "Quantity Takeoff (BOQ)";
                    undo.ErasedIds.AddRange(erasedIds);
                    undo.CreatedIds.AddRange(createdIds);
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
                { "linear_layers_count", linearLenByLayer.Count },
                { "area_layers_count", areaByLayer.Count },
                { "block_types_count", blockCounts.Count },
                { "total_linear_with_waste_m", Math.Round(grandTotalLinearWithWasteM, decimals) },
                { "total_area_with_waste_m2", Math.Round(grandTotalAreaM2, decimals) },
                { "total_blocks", grandTotalBlocks },
                { "area_labels_stamped", areaLabelsToDraw.Count },
                { "table_drawn", drawTable },
                { "table_layer", "_AI_BOQ_TABLE" },
                { "created_count", createdIds.Count },
                { "erased_count", erasedIds.Count },
                { "switched_to_model", switchedToModel }
            };
            if (tableExt.HasValue)
            {
                res["table_bounds"] = new double[]
                {
                    Round(tableExt.Value.MinPoint.X), Round(tableExt.Value.MinPoint.Y),
                    Round(tableExt.Value.MaxPoint.X), Round(tableExt.Value.MaxPoint.Y)
                };
            }
            if (exportExcel)
            {
                ExcelSheetData xlSheet;
                lock (s_lastCoordLock) { s_lastBoqTableByDb.TryGetValue(db.UnmanagedObject, out xlSheet); }
                if (xlSheet != null)
                {
                    string xlTarget = excelPath ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "quantity_takeoff", ".xlsx");
                    string savedPath = ExcelTableInterop.WriteSpreadsheet(xlTarget, xlSheet);
                    res["excel_exported"] = true;
                    res["excel_path"] = savedPath;
                }
            }
            return Ok(res);
        }
    }
}
