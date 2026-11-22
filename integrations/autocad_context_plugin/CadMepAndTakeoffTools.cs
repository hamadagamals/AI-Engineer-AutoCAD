using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
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
                "_AI_BLOCK_LEGEND", "_AI_COORD_TABLE", "_AI_EXCEL_TABLE", "_AI_PANEL_SCHEDULE", "_AI_BOQ_TABLE", "_AI_LIGHTING_TABLE"
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
                        colVisualWidths[vc] * 0.90, headerH * 0.78, usedTextH * 1.02, headerH, AttachmentPoint.MiddleCenter, 4, safeLayer);
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
            if (selectedOnly && selSet.Count == 0)
            {
                return Err("لا توجد عناصر محددة في أوتوكاد حالياً لتنفيذ الحصر عليها (selected_only=true). حدد العناصر أولاً أو استخدم الأداة المناسبة.");
            }

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

        private sealed class PathCurveCandidate
        {
            public int PathIndex;
            public string Handle;
            public Curve FlatCurve;
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
            public double Length;
            public List<Point2d> Samples;
        }

        private sealed class PathBlockMatchItem
        {
            public ObjectId Id;
            public string Handle;
            public string BlockName;
            public string Layer;
            public int ColorIndex;
            public double X;
            public double Y;
            public int PathIndex;
            public double DistanceAlongPath;
            public Extents3d Bounds;
        }

        private static Curve FlattenCurveToXY(Curve srcCurve)
        {
            if (srcCurve == null) return null;
            try
            {
                Plane xy = new Plane(Point3d.Origin, Vector3d.ZAxis);
                Curve proj = srcCurve.GetOrthoProjectedCurve(xy) as Curve;
                if (proj != null) return proj;
            }
            catch { }

            try
            {
                Polyline poly = srcCurve as Polyline;
                if (poly != null && poly.NumberOfVertices >= 2)
                {
                    Polyline flatPoly = new Polyline(poly.NumberOfVertices);
                    bool useWcs = !poly.Normal.IsParallelTo(Vector3d.ZAxis, new Tolerance(1e-6, 1e-6));
                    for (int i = 0; i < poly.NumberOfVertices; i++)
                    {
                        Point2d pt;
                        if (useWcs)
                        {
                            Point3d wp = poly.GetPoint3dAt(i);
                            pt = new Point2d(wp.X, wp.Y);
                        }
                        else
                        {
                            pt = poly.GetPoint2dAt(i);
                        }
                        flatPoly.AddVertexAt(i, pt, useWcs ? 0.0 : poly.GetBulgeAt(i), 0.0, 0.0);
                    }
                    flatPoly.Closed = poly.Closed;
                    return flatPoly;
                }

                // Sample multi-point curves (Polyline2d, Polyline3d, Spline, Arc, Circle, Ellipse)
                // instead of collapsing them to a single straight line.
                try
                {
                    double spParam = srcCurve.StartParam;
                    double epParam = srcCurve.EndParam;
                    if (epParam > spParam + 1e-9)
                    {
                        int steps = (srcCurve is Line) ? 2 : 48;
                        Polyline sampled = new Polyline(steps + 1);
                        int added = 0;
                        Point2d prev = Point2d.Origin;
                        for (int i = 0; i <= steps; i++)
                        {
                            double par = spParam + (epParam - spParam) * (i / (double)steps);
                            Point3d p = srcCurve.GetPointAtParameter(par);
                            Point2d p2 = new Point2d(p.X, p.Y);
                            if (added == 0 || prev.GetDistanceTo(p2) > 1e-7)
                            {
                                sampled.AddVertexAt(added++, p2, 0.0, 0.0, 0.0);
                                prev = p2;
                            }
                        }
                        if (added >= 2)
                        {
                            sampled.Closed = srcCurve.Closed;
                            return sampled;
                        }
                        sampled.Dispose();
                    }
                }
                catch { }

                Point3d sp = srcCurve.StartPoint;
                Point3d ep = srcCurve.EndPoint;
                if (Math.Abs(sp.X - ep.X) > 1e-7 || Math.Abs(sp.Y - ep.Y) > 1e-7)
                {
                    return new Line(new Point3d(sp.X, sp.Y, 0.0), new Point3d(ep.X, ep.Y, 0.0));
                }
            }
            catch { }
            return null;
        }

        private static void AppendSegmentSamples(List<Point2d> pts, Point2d a, Point2d b, int maxExtra)
        {
            double dist = a.GetDistanceTo(b);
            if (dist <= 1e-6 || maxExtra <= 0) return;
            for (int k = 1; k <= maxExtra; k++)
            {
                double t = k / (double)(maxExtra + 1);
                pts.Add(new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }

        private static List<Point2d> SampleCurvePoints2D(Curve flatCurve, int minArcSamples)
        {
            List<Point2d> pts = new List<Point2d>();
            if (flatCurve == null) return pts;

            Polyline pl = flatCurve as Polyline;
            if (pl != null && pl.NumberOfVertices > 0)
            {
                int vCount = pl.NumberOfVertices;
                int segCount = pl.Closed ? vCount : (vCount - 1);
                for (int i = 0; i < vCount; i++)
                    pts.Add(pl.GetPoint2dAt(i));
                int perSegCap = Math.Max(2, Math.Min(12, 96 / Math.Max(1, segCount)));
                for (int s = 0; s < segCount; s++)
                {
                    double bulge = 0.0;
                    try { bulge = pl.GetBulgeAt(s); } catch { bulge = 0.0; }
                    if (Math.Abs(bulge) > 1e-5)
                    {
                        try
                        {
                            double pStart = s;
                            double pEnd = s + 1.0;
                            for (int k = 1; k <= 8; k++)
                            {
                                double par = pStart + (pEnd - pStart) * (k / 9.0);
                                Point3d ap = pl.GetPointAtParameter(par);
                                pts.Add(new Point2d(ap.X, ap.Y));
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        Point2d pa = pl.GetPoint2dAt(s);
                        Point2d pb = pl.GetPoint2dAt((s + 1) % vCount);
                        AppendSegmentSamples(pts, pa, pb, perSegCap);
                    }
                }
                return pts;
            }

            Line ln = flatCurve as Line;
            if (ln != null)
            {
                Point2d a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                Point2d b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                pts.Add(a);
                AppendSegmentSamples(pts, a, b, 15);
                pts.Add(b);
                return pts;
            }

            try
            {
                double sp = flatCurve.StartParam;
                double ep = flatCurve.EndParam;
                int steps = Math.Max(8, minArcSamples);
                for (int i = 0; i <= steps; i++)
                {
                    double par = sp + (ep - sp) * (i / (double)steps);
                    Point3d p = flatCurve.GetPointAtParameter(par);
                    pts.Add(new Point2d(p.X, p.Y));
                }
            }
            catch
            {
                try
                {
                    pts.Add(new Point2d(flatCurve.StartPoint.X, flatCurve.StartPoint.Y));
                    pts.Add(new Point2d(flatCurve.EndPoint.X, flatCurve.EndPoint.Y));
                }
                catch { }
            }
            return pts;
        }

        private static void CollectBlockGeometryForPathTest(
            Entity rootEnt,
            int depth,
            List<Curve> outFlatCurves,
            List<List<Point2d>> outClosedSolids,
            List<Extents3d> outFallbackExtents)
        {
            if (rootEnt == null || depth > 3) return;
            BlockReference br = rootEnt as BlockReference;
            if (br != null)
            {
                DBObjectCollection exploded = new DBObjectCollection();
                try
                {
                    br.Explode(exploded);
                    foreach (DBObject obj in exploded)
                    {
                        Entity child = obj as Entity;
                        if (child == null)
                        {
                            try { if (obj != null) obj.Dispose(); } catch { }
                            continue;
                        }
                        try
                        {
                            CollectBlockGeometryForPathTest(child, depth + 1, outFlatCurves, outClosedSolids, outFallbackExtents);
                        }
                        finally
                        {
                            try { child.Dispose(); } catch { }
                        }
                    }
                    return;
                }
                catch
                {
                    foreach (DBObject obj in exploded)
                    {
                        try { if (obj != null) obj.Dispose(); } catch { }
                    }
                }
                try
                {
                    Extents3d ex = br.GeometricExtents;
                    if (IsFiniteBounds(ex)) outFallbackExtents.Add(ex);
                }
                catch { }
                return;
            }

            Curve cv = rootEnt as Curve;
            if (cv != null)
            {
                Curve flat = FlattenCurveToXY(cv);
                if (flat != null)
                {
                    outFlatCurves.Add(flat);
                    return;
                }
                try
                {
                    Extents3d cex = cv.GeometricExtents;
                    if (IsFiniteBounds(cex)) outFallbackExtents.Add(cex);
                }
                catch { }
                return;
            }

            Solid s2 = rootEnt as Solid;
            if (s2 != null)
            {
                try
                {
                    List<Point2d> poly = new List<Point2d>();
                    for (short k = 0; k < 4; k++)
                    {
                        Point3d vp = s2.GetPointAt(k);
                        Point2d p2 = new Point2d(vp.X, vp.Y);
                        if (poly.Count == 0 || poly[poly.Count - 1].GetDistanceTo(p2) > 1e-6)
                            poly.Add(p2);
                    }
                    if (poly.Count >= 3)
                    {
                        outClosedSolids.Add(poly);
                        return;
                    }
                }
                catch { }
            }

            Hatch ht = rootEnt as Hatch;
            if (ht != null)
            {
                try
                {
                    int nLoops = ht.NumberOfLoops;
                    for (int li = 0; li < nLoops; li++)
                    {
                        HatchLoop loop = ht.GetLoopAt(li);
                        List<Point2d> poly = new List<Point2d>();
                        if (loop.IsPolyline && loop.Polyline != null)
                        {
                            for (int vi = 0; vi < loop.Polyline.Count; vi++)
                                poly.Add(loop.Polyline[vi].Vertex);
                        }
                        else if (loop.Curves != null)
                        {
                            foreach (Curve2d c2 in loop.Curves)
                            {
                                Point2d[] samples = c2.GetSamplePoints(10);
                                if (samples != null)
                                {
                                    for (int si = 0; si < samples.Length; si++)
                                        poly.Add(samples[si]);
                                }
                            }
                        }
                        if (poly.Count >= 3) outClosedSolids.Add(poly);
                    }
                    if (outClosedSolids.Count > 0) return;
                }
                catch { }
                try
                {
                    Extents3d hex = ht.GeometricExtents;
                    if (IsFiniteBounds(hex)) outFallbackExtents.Add(hex);
                }
                catch { }
                return;
            }

            if (!(rootEnt is DBText) && !(rootEnt is MText))
            {
                try
                {
                    Extents3d ex = rootEnt.GeometricExtents;
                    if (IsFiniteBounds(ex)) outFallbackExtents.Add(ex);
                }
                catch { }
            }
        }

        private static bool DoesCurveHitPointOrSegment2D(Curve flatPath, Point2d a, Point2d b, double tol, out Point2d hitPt)
        {
            hitPt = a;
            try
            {
                Point3d pa = flatPath.GetClosestPointTo(new Point3d(a.X, a.Y, 0.0), false);
                if (Math.Sqrt(Math.Pow(pa.X - a.X, 2) + Math.Pow(pa.Y - a.Y, 2)) <= tol)
                {
                    hitPt = a;
                    return true;
                }
                if (a.GetDistanceTo(b) > 1e-6)
                {
                    using (Line segLine = new Line(new Point3d(a.X, a.Y, 0.0), new Point3d(b.X, b.Y, 0.0)))
                    {
                        Point3dCollection pts = new Point3dCollection();
                        flatPath.IntersectWith(segLine, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                        if (pts.Count > 0)
                        {
                            hitPt = new Point2d(pts[0].X, pts[0].Y);
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool DoesPathHitBlockXY(
            List<PathCurveCandidate> paths,
            BlockReference br,
            Extents3d blockExt,
            double tol,
            double unitsPerM,
            out int matchedPathIndex,
            out double matchedDistAlongPath)
        {
            matchedPathIndex = 0;
            matchedDistAlongPath = 0.0;

            List<Curve> subCurves = new List<Curve>();
            List<List<Point2d>> closedPolys = new List<List<Point2d>>();
            List<Extents3d> fallbackExtents = new List<Extents3d>();

            double bw = Math.Max(0.0, blockExt.MaxPoint.X - blockExt.MinPoint.X);
            double bh = Math.Max(0.0, blockExt.MaxPoint.Y - blockExt.MinPoint.Y);
            double blockSpan = Math.Max(bw, bh);
            Point2d blkCenter = new Point2d((blockExt.MinPoint.X + blockExt.MaxPoint.X) * 0.5, (blockExt.MinPoint.Y + blockExt.MaxPoint.Y) * 0.5);
            Point2d blkPos = new Point2d(br.Position.X, br.Position.Y);
            bool hasValidPos =
                blockSpan <= 20.0 * unitsPerM &&
                blkPos.X >= blockExt.MinPoint.X - Math.Max(tol, blockSpan * 0.25) &&
                blkPos.X <= blockExt.MaxPoint.X + Math.Max(tol, blockSpan * 0.25) &&
                blkPos.Y >= blockExt.MinPoint.Y - Math.Max(tol, blockSpan * 0.25) &&
                blkPos.Y <= blockExt.MaxPoint.Y + Math.Max(tol, blockSpan * 0.25);
            double centerHitTol = (blockSpan > 0.0 && blockSpan <= 10.0 * unitsPerM)
                ? Math.Max(tol, Math.Min(blockSpan * 0.25, 0.25 * unitsPerM))
                : 0.0;

            try
            {
                CollectBlockGeometryForPathTest(br, 0, subCurves, closedPolys, fallbackExtents);

                for (int pi = 0; pi < paths.Count; pi++)
                {
                    PathCurveCandidate pc = paths[pi];
                    if (blockExt.MaxPoint.X + tol < pc.MinX || blockExt.MinPoint.X - tol > pc.MaxX ||
                        blockExt.MaxPoint.Y + tol < pc.MinY || blockExt.MinPoint.Y - tol > pc.MaxY)
                        continue;

                    Curve flatPath = pc.FlatCurve;
                    if (flatPath == null) continue;

                    // 0. Check block insertion point & fixture center (catches cable lines snapped to insertion point or passing straight through open symbols)
                    if (hasValidPos)
                    {
                        try
                        {
                            Point3d cpPos = flatPath.GetClosestPointTo(new Point3d(blkPos.X, blkPos.Y, 0.0), false);
                            double dPos = Math.Sqrt(Math.Pow(cpPos.X - blkPos.X, 2) + Math.Pow(cpPos.Y - blkPos.Y, 2));
                            if (dPos <= tol)
                            {
                                matchedPathIndex = pc.PathIndex;
                                try { matchedDistAlongPath = flatPath.GetDistAtPoint(cpPos); } catch { }
                                return true;
                            }
                        }
                        catch { }
                    }
                    if (centerHitTol > 0.0)
                    {
                        try
                        {
                            Point3d cpCtr = flatPath.GetClosestPointTo(new Point3d(blkCenter.X, blkCenter.Y, 0.0), false);
                            double dCtr = Math.Sqrt(Math.Pow(cpCtr.X - blkCenter.X, 2) + Math.Pow(cpCtr.Y - blkCenter.Y, 2));
                            if (dCtr <= centerHitTol)
                            {
                                matchedPathIndex = pc.PathIndex;
                                try { matchedDistAlongPath = flatPath.GetDistAtPoint(cpCtr); } catch { }
                                return true;
                            }
                        }
                        catch { }
                    }

                    // Collect path samples overlapping block bounds (+tol) for point-in-closed-symbol & near-point tests
                    List<Point2d> localPathSamples = new List<Point2d>();
                    for (int si = 0; si < pc.Samples.Count; si++)
                    {
                        Point2d sp = pc.Samples[si];
                        if (sp.X >= blockExt.MinPoint.X - tol && sp.X <= blockExt.MaxPoint.X + tol &&
                            sp.Y >= blockExt.MinPoint.Y - tol && sp.Y <= blockExt.MaxPoint.Y + tol)
                            localPathSamples.Add(sp);
                    }
                    // Always test closest point on path to block center as an extra local sample (catches straight segments passing straight over a closed fixture)
                    try
                    {
                        Point3d cpCenter = flatPath.GetClosestPointTo(new Point3d(blkCenter.X, blkCenter.Y, 0.0), false);
                        localPathSamples.Add(new Point2d(cpCenter.X, cpCenter.Y));
                    }
                    catch { }

                    // 1. Sub-curves intersection, proximity & closed-loop containment
                    for (int ci = 0; ci < subCurves.Count; ci++)
                    {
                        Curve sc = subCurves[ci];
                        if (sc == null) continue;

                        try
                        {
                            Point3dCollection intPts = new Point3dCollection();
                            flatPath.IntersectWith(sc, Intersect.OnBothOperands, intPts, IntPtr.Zero, IntPtr.Zero);
                            if (intPts.Count > 0)
                            {
                                matchedPathIndex = pc.PathIndex;
                                try
                                {
                                    Point3d cp = flatPath.GetClosestPointTo(new Point3d(intPts[0].X, intPts[0].Y, 0.0), false);
                                    matchedDistAlongPath = flatPath.GetDistAtPoint(cp);
                                }
                                catch { }
                                return true;
                            }
                        }
                        catch { }

                        List<Point2d> subPts = SampleCurvePoints2D(sc, 16);
                        for (int spi = 0; spi < subPts.Count; spi++)
                        {
                            Point2d spt = subPts[spi];
                            try
                            {
                                Point3d cp = flatPath.GetClosestPointTo(new Point3d(spt.X, spt.Y, 0.0), false);
                                double d = Math.Sqrt(Math.Pow(cp.X - spt.X, 2) + Math.Pow(cp.Y - spt.Y, 2));
                                if (d <= tol)
                                {
                                    matchedPathIndex = pc.PathIndex;
                                    try { matchedDistAlongPath = flatPath.GetDistAtPoint(cp); } catch { }
                                    return true;
                                }
                            }
                            catch { }
                        }

                        Circle scCircle = sc as Circle;
                        bool isClosedCurve = (scCircle != null) || sc.Closed;
                        if (isClosedCurve || localPathSamples.Count > 0)
                        {
                            for (int lpi = 0; lpi < localPathSamples.Count; lpi++)
                            {
                                Point2d lp = localPathSamples[lpi];
                                try
                                {
                                    Point3d cpSub = sc.GetClosestPointTo(new Point3d(lp.X, lp.Y, 0.0), false);
                                    double dSub = Math.Sqrt(Math.Pow(cpSub.X - lp.X, 2) + Math.Pow(cpSub.Y - lp.Y, 2));
                                    if (dSub <= tol)
                                    {
                                        matchedPathIndex = pc.PathIndex;
                                        try
                                        {
                                            Point3d cpPath = flatPath.GetClosestPointTo(new Point3d(lp.X, lp.Y, 0.0), false);
                                            matchedDistAlongPath = flatPath.GetDistAtPoint(cpPath);
                                        }
                                        catch { }
                                        return true;
                                    }
                                }
                                catch { }

                                if (scCircle != null)
                                {
                                    double dc = Math.Sqrt(Math.Pow(lp.X - scCircle.Center.X, 2) + Math.Pow(lp.Y - scCircle.Center.Y, 2));
                                    if (dc <= scCircle.Radius + tol)
                                    {
                                        matchedPathIndex = pc.PathIndex;
                                        try
                                        {
                                            Point3d cpPath = flatPath.GetClosestPointTo(new Point3d(lp.X, lp.Y, 0.0), false);
                                            matchedDistAlongPath = flatPath.GetDistAtPoint(cpPath);
                                        }
                                        catch { }
                                        return true;
                                    }
                                }
                                else if (sc.Closed && subPts.Count >= 3 && IsPointInPolygon2D(subPts, lp.X, lp.Y))
                                {
                                    matchedPathIndex = pc.PathIndex;
                                    try
                                    {
                                        Point3d cpPath = flatPath.GetClosestPointTo(new Point3d(lp.X, lp.Y, 0.0), false);
                                        matchedDistAlongPath = flatPath.GetDistAtPoint(cpPath);
                                    }
                                    catch { }
                                    return true;
                                }
                            }
                        }
                    }

                    // 2. Closed Solid / Hatch polygons
                    for (int pgi = 0; pgi < closedPolys.Count; pgi++)
                    {
                        List<Point2d> poly = closedPolys[pgi];
                        for (int vi = 0; vi < poly.Count; vi++)
                        {
                            Point2d va = poly[vi];
                            Point2d vb = poly[(vi + 1) % poly.Count];
                            Point2d hitPt;
                            if (DoesCurveHitPointOrSegment2D(flatPath, va, vb, tol, out hitPt))
                            {
                                matchedPathIndex = pc.PathIndex;
                                try
                                {
                                    Point3d cpPath = flatPath.GetClosestPointTo(new Point3d(hitPt.X, hitPt.Y, 0.0), false);
                                    matchedDistAlongPath = flatPath.GetDistAtPoint(cpPath);
                                }
                                catch { }
                                return true;
                            }
                        }
                        for (int lpi = 0; lpi < localPathSamples.Count; lpi++)
                        {
                            Point2d lp = localPathSamples[lpi];
                            if (IsPointInPolygon2D(poly, lp.X, lp.Y))
                            {
                                matchedPathIndex = pc.PathIndex;
                                try
                                {
                                    Point3d cpPath = flatPath.GetClosestPointTo(new Point3d(lp.X, lp.Y, 0.0), false);
                                    matchedDistAlongPath = flatPath.GetDistAtPoint(cpPath);
                                }
                                catch { }
                                return true;
                            }
                        }
                    }

                    // 3. Fallback extents (any sub-entities that failed vector projection, or empty vector geometry)
                    if (fallbackExtents.Count > 0 || (subCurves.Count == 0 && closedPolys.Count == 0))
                    {
                        if (fallbackExtents.Count == 0) fallbackExtents.Add(blockExt);
                        for (int fi = 0; fi < fallbackExtents.Count; fi++)
                        {
                            Extents3d fx = fallbackExtents[fi];
                            List<Point2d> boxPoly = new List<Point2d>
                            {
                                new Point2d(fx.MinPoint.X, fx.MinPoint.Y),
                                new Point2d(fx.MaxPoint.X, fx.MinPoint.Y),
                                new Point2d(fx.MaxPoint.X, fx.MaxPoint.Y),
                                new Point2d(fx.MinPoint.X, fx.MaxPoint.Y)
                            };
                            for (int vi = 0; vi < 4; vi++)
                            {
                                Point2d hitPt;
                                if (DoesCurveHitPointOrSegment2D(flatPath, boxPoly[vi], boxPoly[(vi + 1) % 4], tol, out hitPt))
                                {
                                    matchedPathIndex = pc.PathIndex;
                                    try
                                    {
                                        Point3d cpPath = flatPath.GetClosestPointTo(new Point3d(hitPt.X, hitPt.Y, 0.0), false);
                                        matchedDistAlongPath = flatPath.GetDistAtPoint(cpPath);
                                    }
                                    catch { }
                                    return true;
                                }
                            }
                            for (int lpi = 0; lpi < localPathSamples.Count; lpi++)
                            {
                                Point2d lp = localPathSamples[lpi];
                                if (lp.X >= fx.MinPoint.X - tol && lp.X <= fx.MaxPoint.X + tol &&
                                    lp.Y >= fx.MinPoint.Y - tol && lp.Y <= fx.MaxPoint.Y + tol)
                                {
                                    matchedPathIndex = pc.PathIndex;
                                    try
                                    {
                                        Point3d cpPath = flatPath.GetClosestPointTo(new Point3d(lp.X, lp.Y, 0.0), false);
                                        matchedDistAlongPath = flatPath.GetDistAtPoint(cpPath);
                                    }
                                    catch { }
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                for (int i = 0; i < subCurves.Count; i++)
                {
                    try { if (subCurves[i] != null) subCurves[i].Dispose(); } catch { }
                }
            }
            return false;
        }

        private sealed class CurveChainNode
        {
            public ObjectId Id;
            public string Handle;
            public string Layer;
            public bool IsExplicitColor;
            public int ColorKey;
            public bool HasBulges;
            public Point2d StartXY;
            public Point2d EndXY;
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
            public Curve FlatCurve;
        }

        private sealed class BridgeBlockCandidate
        {
            public ObjectId Id;
            public string Handle;
            public string BlockName;
            public BlockReference Br;
            public Extents3d Bounds;
            public double Span;
            public bool IsPanelRoot;
        }

        private sealed class CircuitSampleProfile
        {
            public HashSet<string> CircuitLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<int> ExplicitColors = new HashSet<int>();
            public HashSet<string> SampleBlockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool PreferCurvedWires;
            public double MedianFixtureSpan;
            public double LearnedSnapTol;
        }

        private static readonly Dictionary<IntPtr, CircuitSampleProfile> s_circuitProfileByDb = new Dictionary<IntPtr, CircuitSampleProfile>();

        private static bool IsPanelOrFrameBlockName(string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return false;
            string u = blockName.Trim().ToUpperInvariant();
            return u.Contains("FRAM") || u.Contains("BORDER") || u.Contains("TITLE") ||
                   u.Contains("SITE") || u.Contains("SHEET") || u.Contains("XREF") ||
                   u.Contains("FLOOR") || u.Contains("GRID");
        }

        private static bool IsPanelDistributionBlockName(string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return false;
            string u = blockName.Trim().ToUpperInvariant();
            return u == "DB" || u == "MDB" || u == "SMDB" || u.StartsWith("DB-") || u.StartsWith("DB_") ||
                   u.StartsWith("MDB") || u.StartsWith("SMDB") || u.Contains("PANEL") ||
                   u.Contains("SWITCHBOARD") || u.Contains("XFMR");
        }

        private static bool IsLikelyArchitecturalContainerBlock(
            string blockName,
            double blockSpan,
            double medianFixtureSpan,
            double drawingSpan,
            double unitsPerM,
            bool includeContainerBlocks,
            string explicitBlockFilter)
        {
            if (includeContainerBlocks) return false;
            if (!string.IsNullOrWhiteSpace(explicitBlockFilter) &&
                (string.Equals(blockName, explicitBlockFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 MatchesPatternOrSubstring(blockName, explicitBlockFilter)))
                return false;

            if (IsPanelOrFrameBlockName(blockName)) return true;

            bool isPastedAnon = !string.IsNullOrEmpty(blockName) &&
                                blockName.StartsWith("A$C", StringComparison.OrdinalIgnoreCase);
            if (drawingSpan > 1e-3 && blockSpan > drawingSpan * 0.28) return true;
            if (medianFixtureSpan > 1e-3 && blockSpan > medianFixtureSpan * 10.0 && (isPastedAnon || blockSpan > 3.0 * unitsPerM))
                return true;
            if (isPastedAnon && blockSpan > 4.0 * unitsPerM) return true;
            return false;
        }

        private static bool HasCurveBulges(Curve cv)
        {
            if (cv is Arc || cv is Spline) return true;
            Polyline pl = cv as Polyline;
            if (pl != null)
            {
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    try { if (Math.Abs(pl.GetBulgeAt(i)) > 1e-4) return true; } catch { }
                }
            }
            return false;
        }

        private static bool TryGetCurveEndpointsXY(Curve cv, out Point2d sp, out Point2d ep)
        {
            sp = Point2d.Origin;
            ep = Point2d.Origin;
            if (cv == null || cv.IsErased) return false;
            if (cv is Circle || cv is Ellipse || cv.Closed) return false;
            try
            {
                Point3d p0 = cv.StartPoint;
                Point3d p1 = cv.EndPoint;
                sp = new Point2d(p0.X, p0.Y);
                ep = new Point2d(p1.X, p1.Y);
                return sp.GetDistanceTo(ep) > 1e-6;
            }
            catch { return false; }
        }

        private static double PointToFlatCurveDist2D(Curve flatCurve, Point2d pt)
        {
            if (flatCurve == null) return double.MaxValue;
            try
            {
                Point3d cp = flatCurve.GetClosestPointTo(new Point3d(pt.X, pt.Y, 0.0), false);
                return Math.Sqrt(Math.Pow(cp.X - pt.X, 2) + Math.Pow(cp.Y - pt.Y, 2));
            }
            catch { return double.MaxValue; }
        }

        private static bool DoesSingleCurveTouchBridgeBlock(
            CurveChainNode node,
            BridgeBlockCandidate blk,
            double contactTol,
            double unitsPerM)
        {
            if (node == null || blk == null || node.FlatCurve == null) return false;
            if (node.MaxX + contactTol < blk.Bounds.MinPoint.X || node.MinX - contactTol > blk.Bounds.MaxPoint.X ||
                node.MaxY + contactTol < blk.Bounds.MinPoint.Y || node.MinY - contactTol > blk.Bounds.MaxPoint.Y)
                return false;

            // Fast endpoint/center check first; full geometry intersection via DoesPathHitBlockXY when inside expanded bbox
            List<PathCurveCandidate> onePath = new List<PathCurveCandidate>(1);
            onePath.Add(new PathCurveCandidate
            {
                PathIndex = 0,
                Handle = node.Handle,
                FlatCurve = node.FlatCurve,
                MinX = node.MinX,
                MinY = node.MinY,
                MaxX = node.MaxX,
                MaxY = node.MaxY,
                Length = 0.0,
                Samples = SampleCurvePoints2D(node.FlatCurve, 16)
            });
            int hp;
            double hd;
            return DoesPathHitBlockXY(onePath, blk.Br, blk.Bounds, contactTol, unitsPerM, out hp, out hd);
        }

        private static int ExpandConnectedPathCurves(
            Database db,
            Transaction tr,
            List<ObjectId> pathCurveIds,
            List<string> pathHandles,
            Dictionary<string, bool> layerHiddenFlags,
            bool visibleOnly,
            double snapTol,
            bool bridgeThroughBlocks,
            bool connectGaps,
            double unitsPerM,
            double medianFixtureSpan,
            double drawingSpan,
            bool includeContainerBlocks,
            List<string> excludeBlocks,
            string blockFilter,
            CircuitSampleProfile learnedProfile,
            out List<string> bridgedBlockNames)
        {
            bridgedBlockNames = new List<string>();
            if (pathCurveIds == null || pathCurveIds.Count == 0) return 0;

            HashSet<string> targetLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<CurveChainNode> queue = new Queue<CurveChainNode>();
            HashSet<ObjectId> visited = new HashSet<ObjectId>();
            List<CurveChainNode> allNodeRefs = new List<CurveChainNode>();

            try
            {
                for (int i = 0; i < pathCurveIds.Count; i++)
                {
                    ObjectId oid = pathCurveIds[i];
                    Curve cv = tr.GetObject(oid, OpenMode.ForRead) as Curve;
                    Point2d sp, ep;
                    if (!TryGetCurveEndpointsXY(cv, out sp, out ep)) continue;
                    Curve flat = FlattenCurveToXY(cv);
                    if (flat == null) continue;
                    Extents3d ex;
                    try { ex = flat.GeometricExtents; }
                    catch { try { ex = cv.GeometricExtents; } catch { flat.Dispose(); continue; } }

                    targetLayers.Add(cv.Layer ?? "0");
                    visited.Add(oid);
                    CurveChainNode seedNode = new CurveChainNode
                    {
                        Id = oid,
                        Handle = cv.Handle.ToString(),
                        Layer = cv.Layer ?? "0",
                        IsExplicitColor = (cv.ColorIndex != 256 && cv.ColorIndex != 0),
                        ColorKey = cv.ColorIndex,
                        HasBulges = HasCurveBulges(cv),
                        StartXY = sp,
                        EndXY = ep,
                        MinX = ex.MinPoint.X,
                        MinY = ex.MinPoint.Y,
                        MaxX = ex.MaxPoint.X,
                        MaxY = ex.MaxPoint.Y,
                        FlatCurve = flat
                    };
                    allNodeRefs.Add(seedNode);
                    queue.Enqueue(seedNode);
                }
                if (queue.Count == 0) return 0;

                if (learnedProfile != null)
                {
                    foreach (string lLyr in learnedProfile.CircuitLayers)
                    {
                        if (!string.IsNullOrWhiteSpace(lLyr)) targetLayers.Add(lLyr);
                    }
                }

                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                List<CurveChainNode> candidates = new List<CurveChainNode>();
                List<BridgeBlockCandidate> bridgeBlocks = new List<BridgeBlockCandidate>();

                foreach (ObjectId mid in ms)
                {
                    if (mid.IsErased) continue;
                    Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased) continue;
                    if (IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                    if (visibleOnly && !IsEntityVisible(ent, layerHiddenFlags)) continue;

                    if (ent is Curve)
                    {
                        if (visited.Contains(mid)) continue;
                        Curve cv = (Curve)ent;
                        if (!targetLayers.Contains(ent.Layer ?? "0")) continue;
                        Point2d sp, ep;
                        if (!TryGetCurveEndpointsXY(cv, out sp, out ep)) continue;
                        Curve flat = FlattenCurveToXY(cv);
                        if (flat == null) continue;
                        Extents3d cex;
                        try { cex = flat.GeometricExtents; }
                        catch { try { cex = cv.GeometricExtents; } catch { flat.Dispose(); continue; } }

                        CurveChainNode cn = new CurveChainNode
                        {
                            Id = mid,
                            Handle = ent.Handle.ToString(),
                            Layer = ent.Layer ?? "0",
                            IsExplicitColor = (ent.ColorIndex != 256 && ent.ColorIndex != 0),
                            ColorKey = ent.ColorIndex,
                            HasBulges = HasCurveBulges(cv),
                            StartXY = sp,
                            EndXY = ep,
                            MinX = cex.MinPoint.X,
                            MinY = cex.MinPoint.Y,
                            MaxX = cex.MaxPoint.X,
                            MaxY = cex.MaxPoint.Y,
                            FlatCurve = flat
                        };
                        candidates.Add(cn);
                        allNodeRefs.Add(cn);
                        continue;
                    }

                    if (bridgeThroughBlocks && ent is BlockReference)
                    {
                        BlockReference br = (BlockReference)ent;
                        string bn = ResolveBlockName(tr, br);
                        if (string.IsNullOrEmpty(bn) || IsAnonymousBlockName(bn) || IsSystemBlockName(bn)) continue;

                        bool isExcluded = false;
                        if (excludeBlocks != null)
                        {
                            for (int ei = 0; ei < excludeBlocks.Count; ei++)
                            {
                                string exPat = excludeBlocks[ei];
                                if (string.IsNullOrWhiteSpace(exPat)) continue;
                                if (string.Equals(bn, exPat.Trim(), StringComparison.OrdinalIgnoreCase) || MatchesPatternOrSubstring(bn, exPat))
                                { isExcluded = true; break; }
                            }
                        }
                        if (isExcluded) continue;

                        Extents3d bex;
                        try { bex = br.GeometricExtents; } catch { continue; }
                        if (!IsFiniteBounds(bex)) continue;
                        double bSpan = Math.Max(bex.MaxPoint.X - bex.MinPoint.X, bex.MaxPoint.Y - bex.MinPoint.Y);
                        if (IsLikelyArchitecturalContainerBlock(bn, bSpan, medianFixtureSpan, drawingSpan, unitsPerM, includeContainerBlocks, blockFilter))
                            continue;

                        bridgeBlocks.Add(new BridgeBlockCandidate
                        {
                            Id = mid,
                            Handle = ent.Handle.ToString(),
                            BlockName = bn,
                            Br = br,
                            Bounds = bex,
                            Span = bSpan,
                            IsPanelRoot = IsPanelDistributionBlockName(bn)
                        });
                    }
                }

                if (candidates.Count == 0) return 0;

                double effSnapTol = connectGaps
                    ? Math.Max(snapTol, Math.Max(0.25 * unitsPerM, medianFixtureSpan * 0.35))
                    : snapTol;

                HashSet<ObjectId> visitedBlocks = new HashSet<ObjectId>();
                int bridgedCount = 0;
                const int MaxChainCurves = 512;

                while (queue.Count > 0 && pathCurveIds.Count < MaxChainCurves)
                {
                    CurveChainNode cur = queue.Dequeue();
                    Point2d[] ends = new Point2d[] { cur.StartXY, cur.EndXY };
                    bool isLayerZero = string.Equals(cur.Layer, "0", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(cur.Layer, "Defpoints", StringComparison.OrdinalIgnoreCase);

                    // 1. Direct Curve-to-Curve connections (endpoint-to-endpoint + T-junctions onto curve body)
                    for (int ei = 0; ei < ends.Length; ei++)
                    {
                        Point2d pt = ends[ei];
                        List<CurveChainNode> hitsAtPt = new List<CurveChainNode>();
                        for (int ci = 0; ci < candidates.Count; ci++)
                        {
                            CurveChainNode cand = candidates[ci];
                            if (visited.Contains(cand.Id)) continue;
                            if (!string.Equals(cand.Layer, cur.Layer, StringComparison.OrdinalIgnoreCase) &&
                                !(learnedProfile != null && learnedProfile.CircuitLayers.Contains(cand.Layer)))
                                continue;
                            if (cur.IsExplicitColor && cand.IsExplicitColor && cur.ColorKey != cand.ColorKey) continue;

                            // Quick bbox filter
                            if (pt.X < cand.MinX - effSnapTol || pt.X > cand.MaxX + effSnapTol ||
                                pt.Y < cand.MinY - effSnapTol || pt.Y > cand.MaxY + effSnapTol)
                                continue;

                            bool hit = (cand.StartXY.GetDistanceTo(pt) <= effSnapTol || cand.EndXY.GetDistanceTo(pt) <= effSnapTol);
                            if (!hit && PointToFlatCurveDist2D(cand.FlatCurve, pt) <= effSnapTol) hit = true;
                            if (!hit)
                            {
                                if (PointToFlatCurveDist2D(cur.FlatCurve, cand.StartXY) <= effSnapTol ||
                                    PointToFlatCurveDist2D(cur.FlatCurve, cand.EndXY) <= effSnapTol)
                                    hit = true;
                            }
                            if (hit) hitsAtPt.Add(cand);
                        }

                        // Guard only against shared uncolored straight Layer-0 wall grids having huge fanouts
                        int maxBranch = (isLayerZero && !cur.IsExplicitColor && !cur.HasBulges) ? 2 : 8;
                        if (hitsAtPt.Count > 0 && hitsAtPt.Count <= maxBranch)
                        {
                            for (int hi = 0; hi < hitsAtPt.Count && pathCurveIds.Count < MaxChainCurves; hi++)
                            {
                                CurveChainNode nxt = hitsAtPt[hi];
                                if (visited.Add(nxt.Id))
                                {
                                    pathCurveIds.Add(nxt.Id);
                                    pathHandles.Add(nxt.Handle);
                                    queue.Enqueue(nxt);
                                }
                            }
                        }
                    }

                    // 2. Block-Bridging: hop across inline fixture/switch blocks ([Wire1] -> [Lamp F1] -> [Wire2] -> ...)
                    if (bridgeThroughBlocks && bridgeBlocks.Count > 0)
                    {
                        for (int bi = 0; bi < bridgeBlocks.Count && pathCurveIds.Count < MaxChainCurves; bi++)
                        {
                            BridgeBlockCandidate blk = bridgeBlocks[bi];
                            if (blk.IsPanelRoot || visitedBlocks.Contains(blk.Id)) continue;

                            double bTol = Math.Max(effSnapTol, Math.Min(blk.Span * 0.16, 0.25 * unitsPerM));
                            if (connectGaps) bTol = Math.Max(bTol, blk.Span * 0.28);

                            if (!DoesSingleCurveTouchBridgeBlock(cur, blk, bTol, unitsPerM)) continue;
                            visitedBlocks.Add(blk.Id);
                            bridgedCount++;
                            if (!bridgedBlockNames.Contains(blk.BlockName)) bridgedBlockNames.Add(blk.BlockName);

                            // Find ALL other curves on the circuit layer/color touching this fixture block
                            List<CurveChainNode> outgoing = new List<CurveChainNode>();
                            for (int ci = 0; ci < candidates.Count; ci++)
                            {
                                CurveChainNode cand = candidates[ci];
                                if (visited.Contains(cand.Id)) continue;
                                if (!string.Equals(cand.Layer, cur.Layer, StringComparison.OrdinalIgnoreCase) &&
                                    !(learnedProfile != null && learnedProfile.CircuitLayers.Contains(cand.Layer)))
                                    continue;
                                if (cur.IsExplicitColor && cand.IsExplicitColor && cur.ColorKey != cand.ColorKey) continue;
                                if (DoesSingleCurveTouchBridgeBlock(cand, blk, bTol, unitsPerM))
                                    outgoing.Add(cand);
                            }

                            // Guard against bridging through a junction where > 6 wires meet (usually shared room/panel boundary)
                            if (outgoing.Count > 0 && outgoing.Count <= 6)
                            {
                                for (int oi = 0; oi < outgoing.Count && pathCurveIds.Count < MaxChainCurves; oi++)
                                {
                                    CurveChainNode nxt = outgoing[oi];
                                    if (visited.Add(nxt.Id))
                                    {
                                        pathCurveIds.Add(nxt.Id);
                                        pathHandles.Add(nxt.Handle);
                                        queue.Enqueue(nxt);
                                    }
                                }
                            }
                        }
                    }
                }
                return bridgedCount;
            }
            finally
            {
                for (int i = 0; i < allNodeRefs.Count; i++)
                {
                    try { if (allNodeRefs[i].FlatCurve != null) allNodeRefs[i].FlatCurve.Dispose(); } catch { }
                }
            }
        }

        private static void ProbeNestedBlocksAlongPath(
            Transaction tr,
            BlockReference parentBr,
            string parentName,
            List<PathCurveCandidate> pathCandidates,
            double unitsPerM,
            int depth,
            Dictionary<string, int> nestedCounts)
        {
            if (parentBr == null || depth > 2) return;
            DBObjectCollection exploded = new DBObjectCollection();
            try
            {
                parentBr.Explode(exploded);
                foreach (DBObject obj in exploded)
                {
                    BlockReference childBr = obj as BlockReference;
                    if (childBr == null) continue;
                    try
                    {
                        string cbn = ResolveBlockName(tr, childBr);
                        if (string.IsNullOrEmpty(cbn) || IsAnonymousBlockName(cbn) || IsSystemBlockName(cbn)) continue;
                        Extents3d cex;
                        try { cex = childBr.GeometricExtents; } catch { continue; }
                        if (!IsFiniteBounds(cex)) continue;
                        double span = Math.Max(Math.Max(0.0, cex.MaxPoint.X - cex.MinPoint.X), Math.Max(0.0, cex.MaxPoint.Y - cex.MinPoint.Y));
                        double tol = Math.Max(1e-3, Math.Min(span * 0.12, 0.15 * unitsPerM));
                        int hp;
                        double hd;
                        if (span <= 20.0 * unitsPerM && DoesPathHitBlockXY(pathCandidates, childBr, cex, tol, unitsPerM, out hp, out hd))
                        {
                            string key = parentName + " -> " + cbn;
                            nestedCounts[key] = nestedCounts.ContainsKey(key) ? nestedCounts[key] + 1 : 1;
                        }
                        else if (span > 10.0 * unitsPerM)
                        {
                            ProbeNestedBlocksAlongPath(tr, childBr, parentName + " -> " + cbn, pathCandidates, unitsPerM, depth + 1, nestedCounts);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                foreach (DBObject obj in exploded)
                {
                    try { if (obj != null) obj.Dispose(); } catch { }
                }
            }
        }

        private static void SeedCurvesFromSelectedBlocks(
            Transaction tr,
            BlockTableRecord msPre,
            Dictionary<string, bool> preHidden,
            bool visibleOnly,
            double unitsPerM,
            CircuitSampleProfile learnedProfile,
            List<ObjectId> selectedBlockIds,
            List<ObjectId> pathCurveIds,
            List<string> pathHandles)
        {
            if (pathCurveIds.Count > 0 || selectedBlockIds.Count == 0) return;
            List<Curve> allModelCurves = new List<Curve>();
            foreach (ObjectId mid in msPre)
            {
                if (mid.IsErased) continue;
                Entity ce = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                Curve cv = ce as Curve;
                if (cv == null || ce.IsErased || IsAiAnnotationLayer(ce.Layer)) continue;
                if (visibleOnly && !IsEntityVisible(ce, preHidden)) continue;
                Point2d sp2, ep2;
                if (!TryGetCurveEndpointsXY(cv, out sp2, out ep2)) continue;
                allModelCurves.Add(cv);
            }

            for (int sbi = 0; sbi < selectedBlockIds.Count; sbi++)
            {
                BlockReference sbr = tr.GetObject(selectedBlockIds[sbi], OpenMode.ForRead) as BlockReference;
                if (sbr == null) continue;
                Extents3d sbex;
                try { sbex = sbr.GeometricExtents; } catch { continue; }
                if (!IsFiniteBounds(sbex)) continue;
                double sbSpan = Math.Max(sbex.MaxPoint.X - sbex.MinPoint.X, sbex.MaxPoint.Y - sbex.MinPoint.Y);
                double seedTol = Math.Max(0.05 * unitsPerM, sbSpan * 0.16);

                List<Curve> preferredHits = new List<Curve>();
                List<Curve> fallbackHits = new List<Curve>();
                for (int ci = 0; ci < allModelCurves.Count; ci++)
                {
                    Curve cv = allModelCurves[ci];
                    Extents3d cex;
                    try { cex = cv.GeometricExtents; } catch { continue; }
                    if (cex.MaxPoint.X + seedTol < sbex.MinPoint.X || cex.MinPoint.X - seedTol > sbex.MaxPoint.X ||
                        cex.MaxPoint.Y + seedTol < sbex.MinPoint.Y || cex.MinPoint.Y - seedTol > sbex.MaxPoint.Y)
                        continue;

                    Curve flat = FlattenCurveToXY(cv);
                    if (flat == null) continue;
                    try
                    {
                        List<PathCurveCandidate> singleList = new List<PathCurveCandidate>(1);
                        singleList.Add(new PathCurveCandidate
                        {
                            PathIndex = 0,
                            Handle = cv.Handle.ToString(),
                            FlatCurve = flat,
                            MinX = cex.MinPoint.X,
                            MinY = cex.MinPoint.Y,
                            MaxX = cex.MaxPoint.X,
                            MaxY = cex.MaxPoint.Y,
                            Length = 0.0,
                            Samples = SampleCurvePoints2D(flat, 16)
                        });
                        int hp; double hd;
                        if (!DoesPathHitBlockXY(singleList, sbr, sbex, seedTol, unitsPerM, out hp, out hd))
                            continue;

                        bool matchesLearned = (learnedProfile != null && learnedProfile.CircuitLayers.Contains(cv.Layer ?? "0"));
                        string upperLayer = (cv.Layer ?? "").ToUpperInvariant();
                        bool looksWire = HasCurveBulges(cv) ||
                            (cv.ColorIndex != 256 && cv.ColorIndex != 0) ||
                            upperLayer.Contains("LTG") || upperLayer.Contains("LIGHT") ||
                            upperLayer.Contains("WIRE") || upperLayer.Contains("CIRC") ||
                            upperLayer.Contains("ELEC");
                        if (matchesLearned || looksWire) preferredHits.Add(cv);
                        else fallbackHits.Add(cv);
                    }
                    finally
                    {
                        try { flat.Dispose(); } catch { }
                    }
                }

                List<Curve> chosenSeeds = preferredHits.Count > 0 ? preferredHits : fallbackHits;
                for (int k = 0; k < chosenSeeds.Count; k++)
                {
                    if (!pathCurveIds.Contains(chosenSeeds[k].ObjectId))
                    {
                        pathCurveIds.Add(chosenSeeds[k].ObjectId);
                        pathHandles.Add(chosenSeeds[k].Handle.ToString());
                    }
                }
            }
        }

        private static CircuitSampleProfile LearnCircuitSampleProfile(
            Database db,
            Transaction tr,
            List<ObjectId> pathCurveIds,
            List<ObjectId> selectedBlockIds,
            double unitsPerM,
            double medianFixtureSpan,
            double drawingSpan)
        {
            CircuitSampleProfile prof = new CircuitSampleProfile();
            List<double> selBlkSpans = new List<double>();
            for (int pi = 0; pi < pathCurveIds.Count; pi++)
            {
                Curve cv = tr.GetObject(pathCurveIds[pi], OpenMode.ForRead) as Curve;
                if (cv == null) continue;
                prof.CircuitLayers.Add(cv.Layer ?? "0");
                if (cv.ColorIndex != 256 && cv.ColorIndex != 0) prof.ExplicitColors.Add(cv.ColorIndex);
                if (HasCurveBulges(cv)) prof.PreferCurvedWires = true;
            }
            for (int bi = 0; bi < selectedBlockIds.Count; bi++)
            {
                BlockReference br = tr.GetObject(selectedBlockIds[bi], OpenMode.ForRead) as BlockReference;
                if (br == null) continue;
                string bn = ResolveBlockName(tr, br);
                if (IsPanelOrFrameBlockName(bn)) continue;
                try
                {
                    Extents3d bex = br.GeometricExtents;
                    double sp = Math.Max(bex.MaxPoint.X - bex.MinPoint.X, bex.MaxPoint.Y - bex.MinPoint.Y);
                    if (drawingSpan <= 1e-3 || sp < drawingSpan * 0.25)
                    {
                        prof.SampleBlockNames.Add(bn);
                        selBlkSpans.Add(sp);
                    }
                }
                catch { }
            }
            if (selBlkSpans.Count > 0)
            {
                selBlkSpans.Sort();
                prof.MedianFixtureSpan = selBlkSpans[selBlkSpans.Count / 2];
            }
            else prof.MedianFixtureSpan = medianFixtureSpan;
            prof.LearnedSnapTol = Math.Max(0.05 * unitsPerM, prof.MedianFixtureSpan * 0.12);
            if (prof.CircuitLayers.Count > 0)
            {
                lock (s_lastCoordLock) { s_circuitProfileByDb[db.UnmanagedObject] = prof; }
                return prof;
            }
            return null;
        }

        private static void ScanCircuitSeedEntities(
            Database db,
            Transaction tr,
            List<string> candidateHandles,
            bool visibleOnly,
            double unitsPerM,
            bool usePreviousPath,
            List<string> excludeBlocks,
            string blockFilter,
            bool learnSample,
            ref CircuitSampleProfile learnedProfile,
            out double drawingSpan,
            out double medianFixtureSpan,
            out bool selectionHadOnlyBlocks,
            List<ObjectId> pathCurveIds,
            List<string> pathHandles,
            List<ObjectId> selectedBlockIds)
        {
            drawingSpan = 0.0;
            medianFixtureSpan = 0.0;
            BlockTableRecord msPre = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            Dictionary<string, bool> preHidden = BuildLayerHiddenMap(db, tr);
            Extents3d? dwgEx = null;
            List<double> smallSpans = new List<double>();
            foreach (ObjectId mid in msPre)
            {
                if (mid.IsErased) continue;
                Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                if (me == null || me.IsErased || IsAiAnnotationLayer(me.Layer)) continue;
                if (visibleOnly && !IsEntityVisible(me, preHidden)) continue;
                Extents3d mex;
                try { mex = me.GeometricExtents; } catch { continue; }
                if (!IsFiniteBounds(mex)) continue;
                if (!dwgEx.HasValue) dwgEx = mex;
                else { Extents3d u = dwgEx.Value; u.AddExtents(mex); dwgEx = u; }

                BlockReference mbr = me as BlockReference;
                if (mbr != null)
                {
                    string mbn = ResolveBlockName(tr, mbr);
                    if (!string.IsNullOrEmpty(mbn) && !IsAnonymousBlockName(mbn) && !IsSystemBlockName(mbn) && !IsPanelOrFrameBlockName(mbn))
                    {
                        double sp = Math.Max(mex.MaxPoint.X - mex.MinPoint.X, mex.MaxPoint.Y - mex.MinPoint.Y);
                        if (sp > 1e-4 && sp <= 4.0 * unitsPerM) smallSpans.Add(sp);
                    }
                }
            }
            if (dwgEx.HasValue)
                drawingSpan = Math.Max(dwgEx.Value.MaxPoint.X - dwgEx.Value.MinPoint.X, dwgEx.Value.MaxPoint.Y - dwgEx.Value.MinPoint.Y);
            if (smallSpans.Count > 0)
            {
                smallSpans.Sort();
                medianFixtureSpan = smallSpans[smallSpans.Count / 2];
            }

            int selBlockCount = 0;
            int selTotalValid = 0;
            foreach (string h in candidateHandles)
            {
                ObjectId oid = ResolveHandle(db, h);
                if (oid.IsNull || oid.IsErased) continue;
                Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                selTotalValid++;
                if (ent is BlockReference) selBlockCount++;
                if (ent is BlockReference && !selectedBlockIds.Contains(oid)) selectedBlockIds.Add(oid);
                if (ent is Curve && !pathCurveIds.Contains(oid))
                {
                    pathCurveIds.Add(oid);
                    pathHandles.Add(ent.Handle.ToString());
                }
            }
            selectionHadOnlyBlocks = (selTotalValid > 0 && selBlockCount == selTotalValid);

            SeedCurvesFromSelectedBlocks(tr, msPre, preHidden, visibleOnly, unitsPerM, learnedProfile, selectedBlockIds, pathCurveIds, pathHandles);

            if (learnSample || (pathCurveIds.Count >= 1 && selectedBlockIds.Count >= 2))
            {
                CircuitSampleProfile learned = LearnCircuitSampleProfile(db, tr, pathCurveIds, selectedBlockIds, unitsPerM, medianFixtureSpan, drawingSpan);
                if (learned != null) learnedProfile = learned;
            }

            if (pathCurveIds.Count == 0 && (usePreviousPath || excludeBlocks.Count > 0 || !string.IsNullOrWhiteSpace(blockFilter)))
            {
                List<string> prevCurves = GetValidCachedUserCurveHandles(db);
                foreach (string h in prevCurves)
                {
                    ObjectId oid = ResolveHandle(db, h);
                    if (oid.IsNull || oid.IsErased) continue;
                    Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (ent is Curve && !pathCurveIds.Contains(oid))
                    {
                        pathCurveIds.Add(oid);
                        pathHandles.Add(ent.Handle.ToString());
                    }
                }
            }
        }

        private static void CollectCircuitPathCandidates(
            Transaction tr,
            List<ObjectId> pathCurveIds,
            List<PathCurveCandidate> pathCandidates,
            HashSet<string> pathEntityTypes,
            ref double totalPathLenUnits,
            ref Extents3d? selectedAndPathExt)
        {
            for (int i = 0; i < pathCurveIds.Count; i++)
            {
                Curve srcCurve = tr.GetObject(pathCurveIds[i], OpenMode.ForRead) as Curve;
                if (srcCurve == null || srcCurve.IsErased) continue;
                pathEntityTypes.Add(srcCurve.GetType().Name);
                Curve flat = FlattenCurveToXY(srcCurve);
                if (flat == null) continue;

                double cLen = 0.0;
                try { cLen = srcCurve.GetDistanceAtParameter(srcCurve.EndParam) - srcCurve.GetDistanceAtParameter(srcCurve.StartParam); }
                catch { cLen = 0.0; }
                totalPathLenUnits += cLen;

                Extents3d pex;
                try { pex = flat.GeometricExtents; }
                catch
                {
                    try { pex = srcCurve.GeometricExtents; }
                    catch { flat.Dispose(); continue; }
                }
                if (!IsFiniteBounds(pex)) { flat.Dispose(); continue; }

                if (!selectedAndPathExt.HasValue) selectedAndPathExt = pex;
                else { Extents3d c = selectedAndPathExt.Value; c.AddExtents(pex); selectedAndPathExt = c; }

                pathCandidates.Add(new PathCurveCandidate
                {
                    PathIndex = i,
                    Handle = srcCurve.Handle.ToString(),
                    FlatCurve = flat,
                    MinX = pex.MinPoint.X,
                    MinY = pex.MinPoint.Y,
                    MaxX = pex.MaxPoint.X,
                    MaxY = pex.MaxPoint.Y,
                    Length = cLen,
                    Samples = SampleCurvePoints2D(flat, 24)
                });
            }
        }

        private static void MatchBlocksAlongCircuitPath(
            Transaction tr,
            BlockTableRecord ms,
            Dictionary<string, bool> layerHiddenFlags,
            List<PathCurveCandidate> pathCandidates,
            HashSet<ObjectId> pathIdSet,
            bool visibleOnly,
            bool includeAnonymous,
            bool includeSystem,
            bool includeContainerBlocks,
            string layerFilter,
            string blockFilter,
            List<string> excludeBlocks,
            double? explicitTolerance,
            double unitsPerM,
            double medianFixtureSpan,
            double drawingSpan,
            CircuitSampleProfile learnedProfile,
            List<PathBlockMatchItem> matchedItems,
            Dictionary<string, int> countsByBlock,
            ref Extents3d? selectedAndPathExt,
            out int containerBlocksSkipped)
        {
            containerBlocksSkipped = 0;
            double refFixSpan = (learnedProfile != null && learnedProfile.MedianFixtureSpan > 1e-3)
                ? learnedProfile.MedianFixtureSpan
                : medianFixtureSpan;

            foreach (ObjectId mid in ms)
            {
                if (mid.IsErased || pathIdSet.Contains(mid)) continue;
                Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                BlockReference br = ent as BlockReference;
                if (br == null || ent.IsErased) continue;
                if (IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                if (visibleOnly && !IsEntityVisible(ent, layerHiddenFlags)) continue;
                if (!string.IsNullOrWhiteSpace(layerFilter) && !MatchesPatternOrSubstring(ent.Layer ?? "", layerFilter)) continue;

                string bn = ResolveBlockName(tr, br);
                if (string.IsNullOrEmpty(bn)) continue;
                if (!includeAnonymous && IsAnonymousBlockName(bn)) continue;
                if (!includeSystem && (IsSystemBlockName(bn) || string.Equals(ent.Layer, "ASHADE", StringComparison.OrdinalIgnoreCase))) continue;
                if (!string.IsNullOrWhiteSpace(blockFilter) &&
                    !string.Equals(bn, blockFilter.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    !MatchesPatternOrSubstring(bn, blockFilter))
                    continue;
                bool isExcludedBlock = false;
                for (int ei = 0; ei < excludeBlocks.Count; ei++)
                {
                    string exPat = excludeBlocks[ei];
                    if (string.IsNullOrWhiteSpace(exPat)) continue;
                    if (string.Equals(bn, exPat.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        MatchesPatternOrSubstring(bn, exPat))
                    {
                        isExcludedBlock = true;
                        break;
                    }
                }
                if (isExcludedBlock) continue;

                Extents3d bex;
                try { bex = br.GeometricExtents; }
                catch { continue; }
                if (!IsFiniteBounds(bex)) continue;

                double bw = Math.Max(0.0, bex.MaxPoint.X - bex.MinPoint.X);
                double bh = Math.Max(0.0, bex.MaxPoint.Y - bex.MinPoint.Y);
                double blockSpan = Math.Max(bw, bh);
                if (IsLikelyArchitecturalContainerBlock(bn, blockSpan, refFixSpan, drawingSpan, unitsPerM, includeContainerBlocks, blockFilter))
                {
                    containerBlocksSkipped++;
                    continue;
                }

                double tol = explicitTolerance.HasValue
                    ? Math.Max(1e-5, explicitTolerance.Value)
                    : Math.Max(1e-3, Math.Min(blockSpan * 0.16, 0.25 * unitsPerM));

                int hitPathIdx;
                double hitDist;
                if (!DoesPathHitBlockXY(pathCandidates, br, bex, tol, unitsPerM, out hitPathIdx, out hitDist))
                    continue;

                matchedItems.Add(new PathBlockMatchItem
                {
                    Id = mid,
                    Handle = ent.Handle.ToString(),
                    BlockName = bn,
                    Layer = ent.Layer ?? "0",
                    ColorIndex = ent.ColorIndex,
                    X = (bex.MinPoint.X + bex.MaxPoint.X) * 0.5,
                    Y = (bex.MinPoint.Y + bex.MaxPoint.Y) * 0.5,
                    PathIndex = hitPathIdx,
                    DistanceAlongPath = hitDist,
                    Bounds = bex
                });
                countsByBlock[bn] = countsByBlock.ContainsKey(bn) ? countsByBlock[bn] + 1 : 1;

                if (!selectedAndPathExt.HasValue) selectedAndPathExt = bex;
                else { Extents3d c = selectedAndPathExt.Value; c.AddExtents(bex); selectedAndPathExt = c; }
            }
        }

        private static Dictionary<string, object> FinalizeCircuitPathOutput(
            Document doc,
            Dictionary<string, object> args,
            List<ObjectId> pathCurveIds,
            List<string> pathHandles,
            List<string> initialPathHandles,
            int bridgedBlockCount,
            List<string> bridgedBlockNames,
            int containerBlocksSkipped,
            HashSet<string> pathEntityTypes,
            double totalPathLenUnits,
            double unitsPerM,
            List<PathBlockMatchItem> matchedItems,
            Dictionary<string, int> countsByBlock,
            Dictionary<string, int> nestedBlocksAlongPath,
            CircuitSampleProfile learnedProfile,
            Extents3d? selectedAndPathExt,
            bool selectPaths,
            bool selectBlocks,
            bool drawLegend,
            bool zoom,
            string zoomMode,
            string title,
            string legendPosition,
            bool visibleOnly,
            bool includeSystem,
            bool includeAnonymous,
            bool switchedToModel)
        {
            matchedItems.Sort(delegate(PathBlockMatchItem a, PathBlockMatchItem b)
            {
                int c = a.PathIndex.CompareTo(b.PathIndex);
                if (c != 0) return c;
                c = a.DistanceAlongPath.CompareTo(b.DistanceAlongPath);
                return c != 0 ? c : string.Compare(a.Handle, b.Handle, StringComparison.Ordinal);
            });

            List<string> matchedHandles = new List<string>(matchedItems.Count);
            List<ObjectId> matchedIds = new List<ObjectId>(matchedItems.Count);
            List<object> matchedDetails = new List<object>();
            for (int i = 0; i < matchedItems.Count; i++)
            {
                PathBlockMatchItem m = matchedItems[i];
                matchedHandles.Add(m.Handle);
                matchedIds.Add(m.Id);
                if (matchedDetails.Count < 200)
                {
                    matchedDetails.Add(new Dictionary<string, object>
                    {
                        { "order_along_path", i + 1 },
                        { "handle", m.Handle },
                        { "block_name", m.BlockName },
                        { "layer", m.Layer },
                        { "x", Round(m.X) },
                        { "y", Round(m.Y) },
                        { "distance_along_path", Round(m.DistanceAlongPath) }
                    });
                }
            }

            Dictionary<string, object> legendResult = null;
            Extents3d? legendExt = null;
            if (drawLegend && matchedHandles.Count > 0)
            {
                Dictionary<string, object> legendArgs = new Dictionary<string, object>
                {
                    { "handles", matchedHandles.ToArray() },
                    { "title", title },
                    { "footer_label", "إجمالي البلوكات على المسار المحدد" },
                    { "position", legendPosition },
                    { "visible_only", visibleOnly },
                    { "include_system_blocks", includeSystem },
                    { "include_anonymous", includeAnonymous },
                    { "replace_existing", GetBool(args, "replace_existing", true) },
                    { "export_excel", GetBool(args, "export_excel", false) },
                    { "zoom", false }
                };
                if (args.ContainsKey("descriptions")) legendArgs["descriptions"] = args["descriptions"];
                if (args.ContainsKey("clear_layers")) legendArgs["clear_layers"] = args["clear_layers"];
                if (args.ContainsKey("clear_handles")) legendArgs["clear_handles"] = args["clear_handles"];
                if (args.ContainsKey("layer")) legendArgs["layer"] = args["layer"];
                if (args.ContainsKey("gap")) legendArgs["gap"] = args["gap"];
                if (args.ContainsKey("excel_path")) legendArgs["excel_path"] = args["excel_path"];

                legendResult = DrawBlockLegend(doc, legendArgs);
                if (legendResult != null && legendResult.ContainsKey("legend_bounds"))
                {
                    double[] lb = GetDoubleArray(legendResult, "legend_bounds");
                    if (lb != null && lb.Length >= 4)
                        legendExt = new Extents3d(new Point3d(lb[0], lb[1], 0.0), new Point3d(lb[2], lb[3], 0.0));
                }
            }

            if (zoom)
            {
                string effectiveZoom = zoomMode;
                if (effectiveZoom == "auto")
                    effectiveZoom = (drawLegend && legendExt.HasValue) ? "legend" : "selected";

                if (effectiveZoom == "both" && selectedAndPathExt.HasValue && legendExt.HasValue)
                {
                    Extents3d both = selectedAndPathExt.Value;
                    both.AddExtents(legendExt.Value);
                    ZoomToExtents3d(doc, both, 1.12);
                }
                else if (effectiveZoom == "selected" && selectedAndPathExt.HasValue)
                    ZoomToExtents3d(doc, selectedAndPathExt.Value, 1.25);
                else if (legendExt.HasValue)
                    ZoomToExtents3d(doc, legendExt.Value, 1.18);
                else if (selectedAndPathExt.HasValue)
                    ZoomToExtents3d(doc, selectedAndPathExt.Value, 1.25);
            }

            try { Application.SetSystemVariable("PICKFIRST", 1); } catch { }
            List<ObjectId> selectionTargets = new List<ObjectId>();
            if (selectPaths && pathCurveIds.Count > 0)
                selectionTargets.AddRange(pathCurveIds);
            if (selectBlocks && matchedIds.Count > 0)
            {
                for (int si = 0; si < matchedIds.Count; si++)
                {
                    if (!selectionTargets.Contains(matchedIds[si])) selectionTargets.Add(matchedIds[si]);
                }
            }
            if (selectionTargets.Count > 0)
                try { doc.Editor.SetImpliedSelection(selectionTargets.ToArray()); } catch { }
            try { doc.Editor.UpdateScreen(); } catch { }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "path_curves_count", pathCurveIds.Count },
                { "initial_path_handles", initialPathHandles },
                { "chain_followed", pathCurveIds.Count > initialPathHandles.Count },
                { "bridged_blocks_count", bridgedBlockCount },
                { "bridged_block_names", bridgedBlockNames },
                { "container_blocks_skipped", containerBlocksSkipped },
                { "path_entity_types", new List<string>(pathEntityTypes) },
                { "path_handles", pathHandles },
                { "total_path_length_m", Math.Round(totalPathLenUnits / unitsPerM, 3) },
                { "matched_blocks_count", matchedItems.Count },
                { "block_types_count", countsByBlock.Count },
                { "counts_by_block", countsByBlock },
                { "matched_handles", matchedHandles },
                { "matched_blocks", matchedDetails },
                { "selected_paths", selectPaths && pathCurveIds.Count > 0 },
                { "selected_blocks", selectBlocks && matchedIds.Count > 0 },
                { "selected_in_autocad", selectionTargets.Count > 0 },
                { "legend_drawn", legendResult != null && legendResult.ContainsKey("ok") && legendResult["ok"] is bool && (bool)legendResult["ok"] },
                { "switched_to_model", switchedToModel }
            };
            if (learnedProfile != null && learnedProfile.CircuitLayers.Count > 0)
                res["learned_circuit_layers"] = new List<string>(learnedProfile.CircuitLayers);
            if (nestedBlocksAlongPath.Count > 0)
                res["nested_blocks_along_path"] = nestedBlocksAlongPath;
            if (selectedAndPathExt.HasValue)
            {
                res["selected_bounds"] = new double[]
                {
                    Round(selectedAndPathExt.Value.MinPoint.X), Round(selectedAndPathExt.Value.MinPoint.Y),
                    Round(selectedAndPathExt.Value.MaxPoint.X), Round(selectedAndPathExt.Value.MaxPoint.Y)
                };
            }
            if (legendResult != null)
            {
                if (legendResult.ContainsKey("legend_bounds")) res["legend_bounds"] = legendResult["legend_bounds"];
                if (legendResult.ContainsKey("rows")) res["legend_rows"] = legendResult["rows"];
                if (legendResult.ContainsKey("layer")) res["legend_layer"] = legendResult["layer"];
                if (legendResult.ContainsKey("excel_exported")) res["excel_exported"] = legendResult["excel_exported"];
                if (legendResult.ContainsKey("excel_path")) res["excel_path"] = legendResult["excel_path"];
            }
            if (matchedItems.Count == 0)
                res["note"] = "لم يتقاطع الخط المحدد مع أي بلوكات مطابقة في المخطط (تم الحفاظ على اختيار الخط الأصلي).";
            return Ok(res);
        }

        private static string PromptSingleCurveSeedIfEmpty(
            Document doc,
            Database db,
            List<ObjectId> pathCurveIds,
            List<string> pathHandles)
        {
            if (pathCurveIds.Count > 0) return null;
            int cmdActive = 0;
            try { cmdActive = Convert.ToInt32(Application.GetSystemVariable("CMDACTIVE"), CultureInfo.InvariantCulture); } catch { }
            if (cmdActive != 0)
                return "يوجد أمر نشط داخل أوتوكاد حالياً (CMDACTIVE=" + cmdActive + "). اضغط Esc أولاً ثم حدد الخط أو المسار المطلوب.";

            try
            {
                string proc = Path.GetFileNameWithoutExtension(System.Diagnostics.Process.GetCurrentProcess().ProcessName ?? "");
                if (string.Equals(proc, "acad", StringComparison.OrdinalIgnoreCase))
                {
                    Type utilsType = typeof(Application).Assembly.GetType("Autodesk.AutoCAD.Internal.Utils", false);
                    if (utilsType != null)
                    {
                        System.Reflection.MethodInfo focusM = utilsType.GetMethod("SetFocusToDwgView",
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                        if (focusM != null) focusM.Invoke(null, null);
                    }
                }
            }
            catch { }

            PromptEntityOptions peo = new PromptEntityOptions(
                "\n[AI-Engineer] اضغط ضغطة واحدة على الخط أو الكيبل (Line/Polyline/Arc): ");
            peo.SetRejectMessage("\nيجب الضغط على خط أو مسار (Line/Polyline/Arc/Spline/Circle).");
            peo.AddAllowedClass(typeof(Curve), false);
            PromptEntityResult per = doc.Editor.GetEntity(peo);
            if (per != null && per.Status == PromptStatus.OK && !per.ObjectId.IsNull && !per.ObjectId.IsErased)
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Entity ent = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent is Curve)
                    {
                        pathCurveIds.Add(per.ObjectId);
                        pathHandles.Add(ent.Handle.ToString());
                    }
                    tr.Commit();
                }
            }
            return null;
        }

        private static Dictionary<string, object> ExecuteCircuitTracePass(
            Document doc,
            Database db,
            Dictionary<string, object> args,
            List<ObjectId> pathCurveIds,
            List<string> pathHandles,
            List<string> initialPathHandles,
            CircuitSampleProfile learnedProfile,
            double unitsPerM,
            double medianFixtureSpan,
            double drawingSpan,
            bool visibleOnly,
            bool includeAnonymous,
            bool includeSystem,
            bool includeContainerBlocks,
            bool selectBlocks,
            bool selectPaths,
            bool drawLegend,
            bool followConnected,
            bool bridgeThroughBlocks,
            bool connectGaps,
            bool zoom,
            string zoomMode,
            string title,
            string legendPosition,
            string layerFilter,
            string blockFilter,
            List<string> excludeBlocks,
            double? explicitTolerance)
        {
            List<PathCurveCandidate> pathCandidates = new List<PathCurveCandidate>();
            List<PathBlockMatchItem> matchedItems = new List<PathBlockMatchItem>();
            Dictionary<string, int> countsByBlock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> nestedBlocksAlongPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> pathEntityTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double totalPathLenUnits = 0.0;
            Extents3d? selectedAndPathExt = null;
            bool switchedToModel = false;
            int bridgedBlockCount = 0;
            List<string> bridgedBlockNames = new List<string>();
            int containerBlocksSkipped = 0;

            using (DocumentLock docLock = doc.LockDocument())
            {
                if ((selectBlocks || selectPaths || zoom) && !db.TileMode)
                {
                    try { LayoutManager.Current.CurrentLayout = "Model"; switchedToModel = true; } catch { }
                }

                try
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                        if (followConnected)
                        {
                            double refSpan = (learnedProfile != null && learnedProfile.MedianFixtureSpan > 1e-3)
                                ? learnedProfile.MedianFixtureSpan
                                : medianFixtureSpan;
                            double chainSnapTol = explicitTolerance.HasValue
                                ? Math.Max(1e-3, explicitTolerance.Value)
                                : Math.Max(1e-3, Math.Max(0.05 * unitsPerM, refSpan * 0.12));
                            bridgedBlockCount = ExpandConnectedPathCurves(
                                db, tr, pathCurveIds, pathHandles, layerHiddenFlags, visibleOnly, chainSnapTol,
                                bridgeThroughBlocks, connectGaps, unitsPerM, refSpan, drawingSpan,
                                includeContainerBlocks, excludeBlocks, blockFilter, learnedProfile, out bridgedBlockNames);
                        }
                        HashSet<ObjectId> pathIdSet = new HashSet<ObjectId>(pathCurveIds);

                        CollectCircuitPathCandidates(tr, pathCurveIds, pathCandidates, pathEntityTypes, ref totalPathLenUnits, ref selectedAndPathExt);
                        if (pathCandidates.Count == 0)
                        {
                            tr.Commit();
                            return Err("تعذر قراءة الهندسة ثنائية الأبعاد للخط المحدد.");
                        }

                        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                            SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                        MatchBlocksAlongCircuitPath(
                            tr, ms, layerHiddenFlags, pathCandidates, pathIdSet,
                            visibleOnly, includeAnonymous, includeSystem, includeContainerBlocks,
                            layerFilter, blockFilter, excludeBlocks, explicitTolerance,
                            unitsPerM, medianFixtureSpan, drawingSpan, learnedProfile,
                            matchedItems, countsByBlock, ref selectedAndPathExt, out containerBlocksSkipped);

                        tr.Commit();
                    }
                }
                finally
                {
                    for (int i = 0; i < pathCandidates.Count; i++)
                    {
                        try { if (pathCandidates[i].FlatCurve != null) pathCandidates[i].FlatCurve.Dispose(); } catch { }
                    }
                }

                return FinalizeCircuitPathOutput(
                    doc, args, pathCurveIds, pathHandles, initialPathHandles,
                    bridgedBlockCount, bridgedBlockNames, containerBlocksSkipped,
                    pathEntityTypes, totalPathLenUnits, unitsPerM, matchedItems,
                    countsByBlock, nestedBlocksAlongPath, learnedProfile, selectedAndPathExt,
                    selectPaths, selectBlocks, drawLegend, zoom, zoomMode, title, legendPosition,
                    visibleOnly, includeSystem, includeAnonymous, switchedToModel);
            }
        }

        private static Dictionary<string, object> FindBlocksAlongPath(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            List<string> explicitHandles = GetStringList(args, "handles");
            double? explicitTolerance = GetNullableDouble(args, "tolerance");
            string blockFilter = GetStr(args, "block_name");
            List<string> excludeBlocks = new List<string>();
            excludeBlocks.AddRange(ExtractStringListArg(args, "exclude_blocks"));
            excludeBlocks.AddRange(ExtractStringListArg(args, "exclude_block_names"));
            excludeBlocks.AddRange(ExtractStringListArg(args, "exclude_block_name"));
            string layerFilter = GetStr(args, "layer_filter") ?? GetStr(args, "layer");
            bool visibleOnly = GetBool(args, "visible_only", true);
            bool includeAnonymous = GetBool(args, "include_anonymous", false);
            bool includeSystem = GetBool(args, "include_system", false);
            bool selectBlocks = GetBool(args, "select_blocks", true);
            bool selectPaths = GetBool(args, "select_paths", false);
            bool drawLegend = GetBool(args, "draw_legend", true);
            bool usePreviousPath = GetBool(args, "use_previous_path", false);
            bool pickIfEmpty = GetBool(args, "pick_if_empty", true);
            bool followConnected = GetBool(args, "follow_connected", true);
            bool bridgeThroughBlocks = GetBool(args, "bridge_through_blocks", true);
            bool connectGaps = GetBool(args, "connect_gaps", false);
            bool learnSample = GetBool(args, "learn_sample", false);
            bool includeContainerBlocks = GetBool(args, "include_container_blocks", false);
            bool zoom = GetBool(args, "zoom", true);
            string zoomMode = (GetStr(args, "zoom_mode") ?? "auto").Trim().ToLowerInvariant();
            string title = GetStr(args, "title");
            if (string.IsNullOrWhiteSpace(title)) title = "جدول حصر ومفاتيح بلوكات المسار المحدد";
            string legendPosition = GetStr(args, "position") ?? "outside_right";

            if (!selectBlocks && !drawLegend && !args.ContainsKey("select_paths"))
                selectPaths = true;

            double unitsPerM = Convert.ToDouble(GetUnitsInfo(db)["units_per_m"], CultureInfo.InvariantCulture);
            if (unitsPerM <= 1e-9) unitsPerM = 1000.0;

            List<string> candidateHandles = explicitHandles.Count > 0
                ? new List<string>(explicitHandles)
                : GetSelectedHandleStrings(doc);

            List<ObjectId> pathCurveIds = new List<ObjectId>();
            List<string> pathHandles = new List<string>();
            List<ObjectId> selectedBlockIds = new List<ObjectId>();
            CircuitSampleProfile learnedProfile = null;
            lock (s_lastCoordLock) { s_circuitProfileByDb.TryGetValue(db.UnmanagedObject, out learnedProfile); }

            double drawingSpan = 0.0;
            double medianFixtureSpan = 0.0;

            using (DocumentLock docLock = doc.LockDocument())
            {
                bool selectionHadOnlyBlocks = false;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ScanCircuitSeedEntities(
                        db, tr, candidateHandles, visibleOnly, unitsPerM, usePreviousPath, excludeBlocks, blockFilter, learnSample,
                        ref learnedProfile, out drawingSpan, out medianFixtureSpan, out selectionHadOnlyBlocks,
                        pathCurveIds, pathHandles, selectedBlockIds);
                    tr.Commit();
                }

                if (pathCurveIds.Count == 0 && pickIfEmpty)
                {
                    string pickErr = PromptSingleCurveSeedIfEmpty(doc, db, pathCurveIds, pathHandles);
                    if (!string.IsNullOrEmpty(pickErr)) return Err(pickErr);
                }
            }

            if (pathCurveIds.Count == 0)
            {
                List<string> prevCurves = GetValidCachedUserCurveHandles(db);
                if (prevCurves.Count > 0)
                    return Err("لم يتم تحديد أي خط أو مسار (Line/Polyline) حالياً، لكن يوجد مسار سابق محفوظ (" + string.Join(",", prevCurves.ToArray()) + "). مرر use_previous_path=true أو حدد الخط في أوتوكاد.");
                return Err("لم يتم تحديد أي خط أو مسار (Line/Polyline/Arc) في أوتوكاد. حدد الخط/الكابل أولاً ثم أعد المحاولة.");
            }

            RecordUserSelectionSnapshot(doc, pathHandles);
            List<string> initialPathHandles = new List<string>(pathHandles);

            return ExecuteCircuitTracePass(
                doc, db, args, pathCurveIds, pathHandles, initialPathHandles,
                learnedProfile, unitsPerM, medianFixtureSpan, drawingSpan,
                visibleOnly, includeAnonymous, includeSystem, includeContainerBlocks,
                selectBlocks, selectPaths, drawLegend, followConnected, bridgeThroughBlocks,
                connectGaps, zoom, zoomMode, title, legendPosition, layerFilter, blockFilter,
                excludeBlocks, explicitTolerance);
        }
    }
}
