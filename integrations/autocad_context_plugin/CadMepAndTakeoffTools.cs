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
                "_AI_BLOCK_LEGEND", "_AI_COORD_TABLE", "_AI_EXCEL_TABLE", "_AI_PANEL_SCHEDULE", "_AI_BOQ_TABLE", "_AI_LIGHTING_TABLE", "_AI_CIRCUIT_SCHEDULE", "_AI_LV_CALC_TABLE", "_AI_SLD", "_AI_ELECTRICAL_QA"
            };
            List<Extents3d> blockers = new List<Extents3d>();
            for (int i = 0; i < tableLayers.Length; i++)
            {
                if (replaceExisting && string.Equals(tableLayers[i], safeLayer, StringComparison.OrdinalIgnoreCase)) continue;
                Extents3d? ext = CollectLayerExtents(ms, tr, tableLayers[i]);
                if (ext.HasValue && IsFiniteBounds(ext.Value)) blockers.Add(ext.Value);
            }

            double vShiftMargin = Math.Max(gap * 0.65, Math.Max(usedTextH * 6.0, totalTableH * 0.12));
            for (int pass = 0; pass < blockers.Count + 3; pass++)
            {
                bool shifted = false;
                double ax1 = topLeftX;
                double ay2 = topLeftY;
                double ax2 = topLeftX + totalTableW;
                double ay1 = topLeftY - totalTableH;
                for (int b = 0; b < blockers.Count; b++)
                {
                    Extents3d blk = blockers[b];
                    if (RectsOverlap(ax1 - gap * 0.25, ay1 - gap * 0.25, ax2 + gap * 0.25, ay2 + gap * 0.25,
                                     blk.MinPoint.X, blk.MinPoint.Y, blk.MaxPoint.X, blk.MaxPoint.Y))
                    {
                        topLeftY = blk.MinPoint.Y - vShiftMargin;
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
            public string EffectiveColorKey;
            public bool IsNonNeutralColor;
            public bool IsExplicitColor;
            public int ColorKey;
            public bool HasBulges;
            public Point2d StartXY;
            public Point2d EndXY;
            public Point2d StartOutDir;
            public Point2d EndOutDir;
            public bool HasStartDir;
            public bool HasEndDir;
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
            public bool IsTerminator;
        }

        private sealed class CircuitTerminatorInfo
        {
            public ObjectId BlockId;
            public string Handle;
            public string BlockName;
            public string Kind;
            public string Label;
            public string PanelName;
            public double X;
            public double Y;
        }

        private sealed class CircuitSampleProfile
        {
            public HashSet<string> CircuitLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<int> ExplicitColors = new HashSet<int>();
            public HashSet<string> ColorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SampleBlockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> TerminatorBlockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool PreferCurvedWires;
            public double MedianFixtureSpan;
            public double LearnedSnapTol;
            public int LearnedFixtureCount;
        }

        private static readonly Dictionary<IntPtr, CircuitSampleProfile> s_circuitProfileByDb = new Dictionary<IntPtr, CircuitSampleProfile>();

        private static double ResolveEffectiveUnitsPerMeter(
            Database db,
            double rawUnitsPerM,
            double drawingSpan,
            double medianFixtureSpan,
            out bool scaleInferred,
            out string scaleWarning)
        {
            scaleInferred = false;
            scaleWarning = "";
            double fallback = rawUnitsPerM > 1e-9 ? rawUnitsPerM : 1000.0;
            if (db == null || medianFixtureSpan <= 1e-4) return fallback;

            UnitsValue iu = db.Insunits;
            if (iu == UnitsValue.Undefined || iu == UnitsValue.Inches)
            {
                if (medianFixtureSpan >= 0.15 && medianFixtureSpan <= 2.5)
                {
                    scaleInferred = true;
                    scaleWarning = "تم اكتشاف أن المخطط مرسوم بوحدة المتر (1 وحدة = 1 متر) رغم ضبط INSUNITS على " +
                                   iu.ToString() + "؛ تم تصحيح المقياس وحساب الطول الفعلي بالمتر تلقائياً.";
                    return 1.0;
                }
                if (iu == UnitsValue.Undefined && medianFixtureSpan >= 15.0 && medianFixtureSpan <= 250.0)
                {
                    scaleInferred = true;
                    scaleWarning = "تم استنتاج وحدة الرسم (سنتيمتر) تلقائياً لأن INSUNITS غير محدد.";
                    return 100.0;
                }
                if (medianFixtureSpan >= 250.0 && medianFixtureSpan <= 2500.0)
                {
                    scaleInferred = true;
                    scaleWarning = "تم اكتشاف أن المخطط مرسوم بالمليمتر رغم ضبط INSUNITS على " +
                                   iu.ToString() + "؛ تم تصحيح المقياس تلقائياً.";
                    return 1000.0;
                }
            }
            return fallback;
        }

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
            return u == "DB" || u == "MDB" || u == "SMDB" || u == "EMDB" || u == "LP" || u == "PP" ||
                   u.StartsWith("DB-") || u.StartsWith("DB_") || u.StartsWith("LP-") || u.StartsWith("LP_") ||
                   u.StartsWith("MDB") || u.StartsWith("SMDB") || u.StartsWith("EMDB") ||
                   u.Contains("PANEL") || u.Contains("SWITCHBOARD") || u.Contains("XFMR");
        }

        private static bool IsCircuitTerminatorArrowBlock(string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return false;
            string u = blockName.Trim().ToUpperInvariant();
            return u == "ARROW" || u == "HOMERUN" || u == "HOME_RUN" || u == "HOME-RUN" ||
                   u == "END_ARROW" || u == "CIRC_ARROW" || u == "FEED_ARROW" || u == "POINTER" ||
                   u.StartsWith("ARROW") || u.EndsWith("_ARROW") || u.EndsWith("-ARROW") || u.Contains("HOMERUN");
        }

        private static bool IsArchitecturalLayerName(string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return false;
            string u = layerName.Trim().ToUpperInvariant();
            return u.Contains("WALL") || u.Contains("DOOR") || u.Contains("WIND") || u.Contains("GRID") ||
                   u.Contains("AXIS") || u.Contains("DIM") || u.Contains("FURN") || u.Contains("STRUCT") ||
                   u.Contains("COLUMN") || u.Contains("HATCH") || u == "DEFPOINTS" || u == "ASHADE";
        }

        private static Dictionary<string, SavedColorState> BuildLayerColorStateMap(Database db, Transaction tr)
        {
            Dictionary<string, SavedColorState> map = new Dictionary<string, SavedColorState>(StringComparer.OrdinalIgnoreCase);
            try
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    LayerTableRecord ltr = tr.GetObject(lid, OpenMode.ForRead) as LayerTableRecord;
                    if (ltr != null) map[ltr.Name] = CaptureColor(ltr.Color);
                }
            }
            catch { }
            return map;
        }

        private static string ResolveEntityEffectiveColorKey(
            Entity ent,
            Dictionary<string, SavedColorState> layerColorMap,
            out bool isNonNeutral)
        {
            isNonNeutral = false;
            if (ent == null) return "ACI:7";
            SavedColorState st = null;
            try
            {
                Autodesk.AutoCAD.Colors.Color c = ent.Color;
                if (c != null)
                {
                    if (c.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByColor)
                        st = CaptureColor(c);
                    else if (c.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci && c.ColorIndex != 0 && c.ColorIndex != 256)
                        st = CaptureColor(c);
                }
            }
            catch { }

            if (st == null && layerColorMap != null && !string.IsNullOrEmpty(ent.Layer))
                layerColorMap.TryGetValue(ent.Layer, out st);

            if (st == null) return "ACI:7";
            if (st.Method == Autodesk.AutoCAD.Colors.ColorMethod.ByColor)
            {
                isNonNeutral = !((st.R >= 245 && st.G >= 245 && st.B >= 245) || (st.R <= 10 && st.G <= 10 && st.B <= 10));
                return string.Format(CultureInfo.InvariantCulture, "RGB:{0},{1},{2}", st.R, st.G, st.B);
            }
            short idx = st.ColorIndex;
            if (idx == 0 || idx == 256 || idx == 7) return "ACI:7";
            isNonNeutral = true;
            return "ACI:" + idx.ToString(CultureInfo.InvariantCulture);
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
            if (medianFixtureSpan > 1e-3 && blockSpan > medianFixtureSpan * 10.0) return true;
            if (medianFixtureSpan <= 1e-3 && isPastedAnon && blockSpan > 4.0 * unitsPerM) return true;
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

        private static void PopulateOutwardTangents(CurveChainNode node)
        {
            node.HasStartDir = false;
            node.HasEndDir = false;
            if (node == null || node.FlatCurve == null) return;
            try
            {
                double dStart = node.FlatCurve.GetDistanceAtParameter(node.FlatCurve.StartParam);
                double dEnd = node.FlatCurve.GetDistanceAtParameter(node.FlatCurve.EndParam);
                double len = dEnd - dStart;
                if (len <= 1e-5) return;
                double eps = Math.Min(len * 0.2, Math.Max(1e-4, len * 0.05));
                Point3d psNear = node.FlatCurve.GetPointAtDist(dStart + eps);
                Vector2d vs = new Vector2d(node.StartXY.X - psNear.X, node.StartXY.Y - psNear.Y);
                if (vs.Length > 1e-6)
                {
                    vs = vs / vs.Length;
                    node.StartOutDir = new Point2d(vs.X, vs.Y);
                    node.HasStartDir = true;
                }
                Point3d peNear = node.FlatCurve.GetPointAtDist(dEnd - eps);
                Vector2d ve = new Vector2d(node.EndXY.X - peNear.X, node.EndXY.Y - peNear.Y);
                if (ve.Length > 1e-6)
                {
                    ve = ve / ve.Length;
                    node.EndOutDir = new Point2d(ve.X, ve.Y);
                    node.HasEndDir = true;
                }
            }
            catch { }
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

        private static bool IsCurveCandidateCompatible(
            CurveChainNode cur,
            CurveChainNode cand,
            bool strictColor,
            bool strictLayer,
            CircuitSampleProfile learnedProfile)
        {
            if (strictColor && !string.Equals(cur.EffectiveColorKey, cand.EffectiveColorKey, StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(cand.Layer, cur.Layer, StringComparison.OrdinalIgnoreCase))
                return true;

            if (strictLayer) return false;

            if (learnedProfile != null && learnedProfile.CircuitLayers.Contains(cand.Layer) &&
                (!strictColor || !cur.IsNonNeutralColor || learnedProfile.ColorKeys.Count == 0 || learnedProfile.ColorKeys.Contains(cand.EffectiveColorKey)))
                return true;

            if (cur.IsNonNeutralColor && cand.IsNonNeutralColor &&
                string.Equals(cur.EffectiveColorKey, cand.EffectiveColorKey, StringComparison.OrdinalIgnoreCase) &&
                !IsArchitecturalLayerName(cand.Layer))
                return true;

            return false;
        }

        private static bool IsCollinearBreakBridgeMatch(
            Point2d pt,
            Point2d outDir,
            bool hasOutDir,
            CurveChainNode cand,
            double maxBreakGap,
            double maxLateralTol)
        {
            Point2d dummy;
            return IsCollinearBreakBridgeMatch(pt, outDir, hasOutDir, cand, maxBreakGap, maxLateralTol, 0.80, out dummy);
        }

        private static bool IsCollinearBreakBridgeMatch(
            Point2d pt,
            Point2d outDir,
            bool hasOutDir,
            CurveChainNode cand,
            double maxBreakGap,
            double maxLateralTol,
            double minAlign,
            out Point2d matchedCandPt)
        {
            matchedCandPt = Point2d.Origin;
            if (!hasOutDir) return false;
            Point2d[] cEnds = new Point2d[] { cand.StartXY, cand.EndXY };
            Point2d[] cDirs = new Point2d[] { cand.StartOutDir, cand.EndOutDir };
            bool[] cHas = new bool[] { cand.HasStartDir, cand.HasEndDir };

            for (int k = 0; k < 2; k++)
            {
                Point2d cp = cEnds[k];
                double dx = cp.X - pt.X;
                double dy = cp.Y - pt.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-4 || dist > maxBreakGap) continue;

                double forward = dx * outDir.X + dy * outDir.Y;
                if (forward <= 0.0) continue;
                double lateral = Math.Abs(dx * (-outDir.Y) + dy * outDir.X);
                if (lateral > maxLateralTol) continue;

                if (cHas[k])
                {
                    double align = -(cDirs[k].X * outDir.X + cDirs[k].Y * outDir.Y);
                    if (align < minAlign) continue;
                }
                matchedCandPt = cp;
                return true;
            }
            return false;
        }

        private static CurveChainNode CreateCurveChainNode(
            ObjectId oid,
            Curve cv,
            Dictionary<string, SavedColorState> layerColorMap)
        {
            Point2d sp, ep;
            if (!TryGetCurveEndpointsXY(cv, out sp, out ep)) return null;
            Curve flat = FlattenCurveToXY(cv);
            if (flat == null) return null;
            Extents3d ex;
            try { ex = flat.GeometricExtents; }
            catch { try { ex = cv.GeometricExtents; } catch { flat.Dispose(); return null; } }

            bool nonNeutral;
            string effColor = ResolveEntityEffectiveColorKey(cv, layerColorMap, out nonNeutral);
            CurveChainNode node = new CurveChainNode
            {
                Id = oid,
                Handle = cv.Handle.ToString(),
                Layer = cv.Layer ?? "0",
                EffectiveColorKey = effColor,
                IsNonNeutralColor = nonNeutral,
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
            PopulateOutwardTangents(node);
            return node;
        }

        private static void CollectChainTraceCandidates(
            Database db,
            Transaction tr,
            Dictionary<string, bool> layerHiddenFlags,
            Dictionary<string, SavedColorState> layerColorMap,
            bool visibleOnly,
            bool bridgeThroughBlocks,
            double unitsPerM,
            double medianFixtureSpan,
            double drawingSpan,
            bool includeContainerBlocks,
            List<string> excludeBlocks,
            string blockFilter,
            bool strictColor,
            bool strictLayer,
            HashSet<string> targetLayers,
            HashSet<string> seedColorKeys,
            bool anyNeutralSeed,
            HashSet<ObjectId> visited,
            List<CurveChainNode> candidates,
            List<CurveChainNode> allNodeRefs,
            List<BridgeBlockCandidate> bridgeBlocks,
            List<CrossingObstacleSegment> obstacleSegments)
        {
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

            foreach (ObjectId mid in ms)
            {
                if (mid.IsErased) continue;
                Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                if (visibleOnly && !IsEntityVisible(ent, layerHiddenFlags)) continue;

                Curve cv = ent as Curve;
                if (cv != null)
                {
                    if (visited.Contains(mid)) continue;
                    if (obstacleSegments != null)
                    {
                        CrossingObstacleSegment obs = TryCreateObstacleSegment(mid, cv, layerColorMap);
                        if (obs != null) obstacleSegments.Add(obs);
                    }

                    string cLayer = ent.Layer ?? "0";
                    if (IsArchitecturalLayerName(cLayer) && !targetLayers.Contains(cLayer)) continue;

                    bool candNonNeutral;
                    string candColor = ResolveEntityEffectiveColorKey(ent, layerColorMap, out candNonNeutral);
                    bool layerOk = targetLayers.Contains(cLayer);
                    bool sameNonNeutralColorAcrossLayer = !strictLayer && !anyNeutralSeed && candNonNeutral && seedColorKeys.Contains(candColor);
                    if (!layerOk && !sameNonNeutralColorAcrossLayer) continue;
                    if (strictColor && seedColorKeys.Count > 0 && !seedColorKeys.Contains(candColor)) continue;

                    CurveChainNode cn = CreateCurveChainNode(mid, cv, layerColorMap);
                    if (cn == null) continue;
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
                        IsPanelRoot = IsPanelDistributionBlockName(bn),
                        IsTerminator = IsCircuitTerminatorArrowBlock(bn)
                    });
                }
            }
        }

        private sealed class CircuitChainScope
        {
            public bool UseComponentLayerFilter;
            public bool StrictColor;
            public bool StrictLayer;
            public bool AnyNeutralSeed;
            public HashSet<string> TargetLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SeedColorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public CircuitSampleProfile LearnedProfile;
        }

        private static bool IsNodeInChainScope(CurveChainNode cur, CurveChainNode cand, CircuitChainScope scope)
        {
            if (scope != null && scope.UseComponentLayerFilter)
            {
                if (IsArchitecturalLayerName(cand.Layer) && !scope.TargetLayers.Contains(cand.Layer))
                    return false;
                bool layerOk = scope.TargetLayers.Contains(cand.Layer);
                bool sameNonNeutralColorAcrossLayer = !scope.StrictLayer && !scope.AnyNeutralSeed && cand.IsNonNeutralColor && scope.SeedColorKeys.Contains(cand.EffectiveColorKey);
                if (!layerOk && !sameNonNeutralColorAcrossLayer)
                    return false;
                if (scope.StrictColor && scope.SeedColorKeys.Count > 0 && !scope.SeedColorKeys.Contains(cand.EffectiveColorKey))
                    return false;
            }
            return IsCurveCandidateCompatible(
                cur,
                cand,
                scope != null ? scope.StrictColor : true,
                scope != null ? scope.StrictLayer : false,
                scope != null ? scope.LearnedProfile : null);
        }

        private static bool IsPointAtTerminatorBlock(
            Point2d pt,
            SpatialGrid2D<BridgeBlockCandidate> terminatorGrid,
            double maxTermTol,
            double tightSnapTol,
            List<ObjectId> touchedTerminatorBlockIds,
            List<BridgeBlockCandidate> scratchTerms)
        {
            if (terminatorGrid == null || terminatorGrid.Count == 0) return false;
            double qPad = Math.Max(tightSnapTol, maxTermTol) + 1e-4;
            terminatorGrid.Query(pt.X - qPad, pt.Y - qPad, pt.X + qPad, pt.Y + qPad, scratchTerms);
            for (int bi = 0; bi < scratchTerms.Count; bi++)
            {
                BridgeBlockCandidate blk = scratchTerms[bi];
                if (!blk.IsTerminator && !blk.IsPanelRoot) continue;
                double tTol = Math.Max(tightSnapTol, blk.Span * 0.55);
                if (pt.X >= blk.Bounds.MinPoint.X - tTol && pt.X <= blk.Bounds.MaxPoint.X + tTol &&
                    pt.Y >= blk.Bounds.MinPoint.Y - tTol && pt.Y <= blk.Bounds.MaxPoint.Y + tTol)
                {
                    if (touchedTerminatorBlockIds != null && !touchedTerminatorBlockIds.Contains(blk.Id))
                        touchedTerminatorBlockIds.Add(blk.Id);
                    return true;
                }
            }
            return false;
        }

        private static void EnqueueConnectedCurvesAtEndpoints(
            CurveChainNode cur,
            SpatialGrid2D<CurveChainNode> curveGrid,
            SpatialGrid2D<BridgeBlockCandidate> terminatorGrid,
            SpatialGrid2D<CrossingObstacleSegment> obstacleGrid,
            double maxTermTol,
            List<ObjectId> touchedTerminatorBlockIds,
            HashSet<ObjectId> visited,
            HashSet<ObjectId> globalSkipIds,
            Queue<CurveChainNode> queue,
            List<ObjectId> pathCurveIds,
            List<string> pathHandles,
            double tightSnapTol,
            bool connectGaps,
            double breakGapMax,
            double breakLateralTol,
            double crossingGapMax,
            double crossingLateralTol,
            double obsTol,
            CircuitChainScope scope,
            int maxChainCurves,
            List<CurveChainNode> scratchTight,
            List<CurveChainNode> scratchBreak,
            List<BridgeBlockCandidate> scratchTerms,
            List<CrossingObstacleSegment> scratchObs)
        {
            Point2d[] ends = new Point2d[] { cur.StartXY, cur.EndXY };
            Point2d[] dirs = new Point2d[] { cur.StartOutDir, cur.EndOutDir };
            bool[] hasDirs = new bool[] { cur.HasStartDir, cur.HasEndDir };
            bool isLayerZero = string.Equals(cur.Layer, "0", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(cur.Layer, "Defpoints", StringComparison.OrdinalIgnoreCase);

            for (int ei = 0; ei < ends.Length; ei++)
            {
                Point2d pt = ends[ei];
                // Hard stop: if this wire tip lands at a Home-Run ARROW or Panel block, do NOT jump out of it
                if (IsPointAtTerminatorBlock(pt, terminatorGrid, maxTermTol, tightSnapTol, touchedTerminatorBlockIds, scratchTerms))
                    continue;

                List<CurveChainNode> hitsAtPt = new List<CurveChainNode>();
                curveGrid.Query(pt.X - tightSnapTol, pt.Y - tightSnapTol, pt.X + tightSnapTol, pt.Y + tightSnapTol, scratchTight);
                for (int ci = 0; ci < scratchTight.Count; ci++)
                {
                    CurveChainNode cand = scratchTight[ci];
                    if (visited.Contains(cand.Id) || (globalSkipIds != null && globalSkipIds.Contains(cand.Id))) continue;
                    if (!IsNodeInChainScope(cur, cand, scope)) continue;

                    if (pt.X < cand.MinX - tightSnapTol || pt.X > cand.MaxX + tightSnapTol ||
                        pt.Y < cand.MinY - tightSnapTol || pt.Y > cand.MaxY + tightSnapTol)
                        continue;

                    bool hit = (cand.StartXY.GetDistanceTo(pt) <= tightSnapTol || cand.EndXY.GetDistanceTo(pt) <= tightSnapTol);
                    if (!hit && PointToFlatCurveDist2D(cand.FlatCurve, pt) <= tightSnapTol) hit = true;
                    if (!hit)
                    {
                        if (PointToFlatCurveDist2D(cur.FlatCurve, cand.StartXY) <= tightSnapTol ||
                            PointToFlatCurveDist2D(cur.FlatCurve, cand.EndXY) <= tightSnapTol)
                            hit = true;
                    }
                    if (hit) hitsAtPt.Add(cand);
                }

                if (hitsAtPt.Count == 0 && connectGaps && hasDirs[ei])
                {
                    CurveChainNode bestCollinear = null;
                    double bestDist = double.MaxValue;
                    curveGrid.Query(pt.X - breakGapMax, pt.Y - breakGapMax, pt.X + breakGapMax, pt.Y + breakGapMax, scratchBreak);
                    for (int ci = 0; ci < scratchBreak.Count; ci++)
                    {
                        CurveChainNode cand = scratchBreak[ci];
                        if (visited.Contains(cand.Id) || (globalSkipIds != null && globalSkipIds.Contains(cand.Id))) continue;
                        if (!IsNodeInChainScope(cur, cand, scope)) continue;
                        if (pt.X < cand.MinX - breakGapMax || pt.X > cand.MaxX + breakGapMax ||
                            pt.Y < cand.MinY - breakGapMax || pt.Y > cand.MaxY + breakGapMax)
                            continue;
                        Point2d matchedEnd;
                        if (IsCollinearBreakBridgeMatch(pt, dirs[ei], true, cand, breakGapMax, breakLateralTol, 0.80, out matchedEnd))
                        {
                            double d = matchedEnd.GetDistanceTo(pt);
                            if (d < bestDist) { bestDist = d; bestCollinear = cand; }
                        }
                    }
                    if (bestCollinear != null) hitsAtPt.Add(bestCollinear);
                }

                // Pass 3: Verified crossing bridge ("كوبري تقاطع مؤكد") across a transverse/other-color wire obstacle
                if (hitsAtPt.Count == 0 && connectGaps && hasDirs[ei] && obstacleGrid != null && obstacleGrid.Count > 0 && crossingGapMax > breakGapMax)
                {
                    CurveChainNode bestCross = null;
                    double bestCrossDist = double.MaxValue;
                    curveGrid.Query(pt.X - crossingGapMax, pt.Y - crossingGapMax, pt.X + crossingGapMax, pt.Y + crossingGapMax, scratchBreak);
                    for (int ci = 0; ci < scratchBreak.Count; ci++)
                    {
                        CurveChainNode cand = scratchBreak[ci];
                        if (visited.Contains(cand.Id) || (globalSkipIds != null && globalSkipIds.Contains(cand.Id))) continue;
                        if (!IsNodeInChainScope(cur, cand, scope)) continue;
                        if (pt.X < cand.MinX - crossingGapMax || pt.X > cand.MaxX + crossingGapMax ||
                            pt.Y < cand.MinY - crossingGapMax || pt.Y > cand.MaxY + crossingGapMax)
                            continue;

                        Point2d candEndPt;
                        if (!IsCollinearBreakBridgeMatch(pt, dirs[ei], true, cand, crossingGapMax, crossingLateralTol, 0.65, out candEndPt))
                            continue;

                        if (!HasCrossingObstacleInGap(pt, candEndPt, cur.Id, cand.Id, cur.EffectiveColorKey, obstacleGrid, obsTol, scratchObs))
                            continue;

                        double d = candEndPt.GetDistanceTo(pt);
                        if (d < bestCrossDist)
                        {
                            bestCrossDist = d;
                            bestCross = cand;
                        }
                    }
                    if (bestCross != null) hitsAtPt.Add(bestCross);
                }

                int maxBranch = (isLayerZero && !cur.IsNonNeutralColor && !cur.HasBulges) ? 2 : 8;
                if (hitsAtPt.Count > 0 && hitsAtPt.Count <= maxBranch)
                {
                    for (int hi = 0; hi < hitsAtPt.Count && pathCurveIds.Count < maxChainCurves; hi++)
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
        }

        private static int ExpandConnectedPathCurvesCore(
            Queue<CurveChainNode> queue,
            HashSet<ObjectId> visited,
            HashSet<ObjectId> globalSkipIds,
            List<ObjectId> pathCurveIds,
            List<string> pathHandles,
            SpatialGrid2D<CurveChainNode> curveGrid,
            SpatialGrid2D<BridgeBlockCandidate> bridgeGrid,
            SpatialGrid2D<BridgeBlockCandidate> terminatorGrid,
            SpatialGrid2D<CrossingObstacleSegment> obstacleGrid,
            double maxBridgeTol,
            double maxTermTol,
            double snapTol,
            bool bridgeThroughBlocks,
            bool connectGaps,
            double unitsPerM,
            double medianFixtureSpan,
            CircuitChainScope scope,
            int maxCircuitBlocks,
            out List<string> bridgedBlockNames,
            out List<ObjectId> touchedTerminatorBlockIds,
            out bool circuitGuardTriggered)
        {
            bridgedBlockNames = new List<string>();
            touchedTerminatorBlockIds = new List<ObjectId>();
            circuitGuardTriggered = false;
            if (queue == null || queue.Count == 0 || curveGrid == null) return 0;

            double tightSnapTol = medianFixtureSpan > 1e-3
                ? Math.Max(1e-3, Math.Min(snapTol, medianFixtureSpan * 0.10))
                : Math.Max(1e-3, Math.Min(snapTol, 0.06 * unitsPerM));
            double breakGapMax = medianFixtureSpan > 1e-3
                ? Math.Max(tightSnapTol * 2.0, medianFixtureSpan * 1.15)
                : Math.Max(1e-3, 0.90 * unitsPerM);
            double breakLateralTol = medianFixtureSpan > 1e-3
                ? Math.Max(1e-3, medianFixtureSpan * 0.16)
                : Math.Max(1e-3, 0.10 * unitsPerM);
            double crossingGapMax = medianFixtureSpan > 1e-3
                ? Math.Max(breakGapMax * 1.8, Math.Min(3.0 * unitsPerM, medianFixtureSpan * 3.5))
                : Math.Max(1e-3, 2.5 * unitsPerM);
            double crossingLateralTol = medianFixtureSpan > 1e-3
                ? Math.Max(breakLateralTol * 2.0, medianFixtureSpan * 0.35)
                : Math.Max(1e-3, 0.25 * unitsPerM);
            double obsTol = medianFixtureSpan > 1e-3
                ? Math.Max(0.25 * unitsPerM, medianFixtureSpan * 0.45)
                : Math.Max(0.25 * unitsPerM, 0.25);

            HashSet<ObjectId> visitedBlocks = new HashSet<ObjectId>();
            int bridgedCount = 0;
            int maxChainCurves = (maxCircuitBlocks > 0) ? Math.Max(40, maxCircuitBlocks * 3) : 512;

            List<CurveChainNode> scratchTight = new List<CurveChainNode>(32);
            List<CurveChainNode> scratchBreak = new List<CurveChainNode>(32);
            List<BridgeBlockCandidate> scratchTerms = new List<BridgeBlockCandidate>(8);
            List<BridgeBlockCandidate> scratchBridges = new List<BridgeBlockCandidate>(16);
            List<CurveChainNode> scratchOut = new List<CurveChainNode>(16);
            List<CrossingObstacleSegment> scratchObs = new List<CrossingObstacleSegment>(16);

            while (queue.Count > 0 && pathCurveIds.Count < maxChainCurves && !circuitGuardTriggered)
            {
                CurveChainNode cur = queue.Dequeue();

                EnqueueConnectedCurvesAtEndpoints(
                    cur, curveGrid, terminatorGrid, obstacleGrid, maxTermTol, touchedTerminatorBlockIds, visited, globalSkipIds, queue, pathCurveIds, pathHandles,
                    tightSnapTol, connectGaps, breakGapMax, breakLateralTol, crossingGapMax, crossingLateralTol, obsTol,
                    scope, maxChainCurves, scratchTight, scratchBreak, scratchTerms, scratchObs);

                if (bridgeThroughBlocks && bridgeGrid != null && bridgeGrid.Count > 0)
                {
                    double qPad = Math.Max(tightSnapTol, maxBridgeTol) + 1e-4;
                    bridgeGrid.Query(cur.MinX - qPad, cur.MinY - qPad, cur.MaxX + qPad, cur.MaxY + qPad, scratchBridges);
                    for (int bi = 0; bi < scratchBridges.Count && pathCurveIds.Count < maxChainCurves; bi++)
                    {
                        BridgeBlockCandidate blk = scratchBridges[bi];
                        if (visitedBlocks.Contains(blk.Id)) continue;

                        double bTol = (blk.IsTerminator || blk.IsPanelRoot)
                            ? Math.Max(tightSnapTol, blk.Span * 0.45)
                            : Math.Max(tightSnapTol, blk.Span * (connectGaps ? 0.36 : 0.18));

                        if (!DoesSingleCurveTouchBridgeBlock(cur, blk, bTol, unitsPerM)) continue;
                        visitedBlocks.Add(blk.Id);

                        // Hard stop at Home-Run Arrow or Panel blocks: never bridge through into other circuits!
                        if (blk.IsTerminator || blk.IsPanelRoot)
                        {
                            if (!touchedTerminatorBlockIds.Contains(blk.Id))
                                touchedTerminatorBlockIds.Add(blk.Id);
                            continue;
                        }

                        bridgedCount++;
                        if (!bridgedBlockNames.Contains(blk.BlockName)) bridgedBlockNames.Add(blk.BlockName);
                        if (maxCircuitBlocks > 0 && bridgedCount >= maxCircuitBlocks)
                        {
                            circuitGuardTriggered = true;
                            break;
                        }

                        List<CurveChainNode> outgoing = new List<CurveChainNode>();
                        curveGrid.Query(
                            blk.Bounds.MinPoint.X - bTol, blk.Bounds.MinPoint.Y - bTol,
                            blk.Bounds.MaxPoint.X + bTol, blk.Bounds.MaxPoint.Y + bTol,
                            scratchOut);
                        for (int ci = 0; ci < scratchOut.Count; ci++)
                        {
                            CurveChainNode cand = scratchOut[ci];
                            if (visited.Contains(cand.Id) || (globalSkipIds != null && globalSkipIds.Contains(cand.Id))) continue;
                            if (!IsNodeInChainScope(cur, cand, scope)) continue;
                            if (DoesSingleCurveTouchBridgeBlock(cand, blk, bTol, unitsPerM))
                                outgoing.Add(cand);
                        }

                        if (outgoing.Count > 0 && outgoing.Count <= 6)
                        {
                            for (int oi = 0; oi < outgoing.Count && pathCurveIds.Count < maxChainCurves; oi++)
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
            bool strictColor,
            bool strictLayer,
            double unitsPerM,
            double medianFixtureSpan,
            double drawingSpan,
            bool includeContainerBlocks,
            List<string> excludeBlocks,
            string blockFilter,
            CircuitSampleProfile learnedProfile,
            int maxCircuitBlocks,
            HashSet<ObjectId> skipCurveIds,
            out List<string> bridgedBlockNames,
            out List<ObjectId> touchedTerminatorBlockIds,
            out bool circuitGuardTriggered)
        {
            bridgedBlockNames = new List<string>();
            touchedTerminatorBlockIds = new List<ObjectId>();
            circuitGuardTriggered = false;
            if (pathCurveIds == null || pathCurveIds.Count == 0) return 0;

            Dictionary<string, SavedColorState> layerColorMap = BuildLayerColorStateMap(db, tr);
            HashSet<string> targetLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seedColorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyNeutralSeed = false;
            Queue<CurveChainNode> queue = new Queue<CurveChainNode>();
            HashSet<ObjectId> visited = new HashSet<ObjectId>();
            if (skipCurveIds != null)
            {
                foreach (ObjectId sid in skipCurveIds) visited.Add(sid);
            }
            List<CurveChainNode> allNodeRefs = new List<CurveChainNode>();

            try
            {
                for (int i = 0; i < pathCurveIds.Count; i++)
                {
                    ObjectId oid = pathCurveIds[i];
                    Curve cv = tr.GetObject(oid, OpenMode.ForRead) as Curve;
                    CurveChainNode seedNode = CreateCurveChainNode(oid, cv, layerColorMap);
                    if (seedNode == null) continue;
                    targetLayers.Add(seedNode.Layer);
                    seedColorKeys.Add(seedNode.EffectiveColorKey);
                    if (!seedNode.IsNonNeutralColor) anyNeutralSeed = true;
                    visited.Add(oid);
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

                List<CurveChainNode> candidates = new List<CurveChainNode>();
                List<BridgeBlockCandidate> bridgeBlocks = new List<BridgeBlockCandidate>();
                List<CrossingObstacleSegment> obstacleSegments = new List<CrossingObstacleSegment>();
                CollectChainTraceCandidates(
                    db, tr, layerHiddenFlags, layerColorMap, visibleOnly, bridgeThroughBlocks,
                    unitsPerM, medianFixtureSpan, drawingSpan, includeContainerBlocks, excludeBlocks, blockFilter,
                    strictColor, strictLayer, targetLayers, seedColorKeys, anyNeutralSeed,
                    visited, candidates, allNodeRefs, bridgeBlocks, obstacleSegments);

                if (candidates.Count == 0 && bridgeBlocks.Count == 0) return 0;

                double estBreakMax = medianFixtureSpan > 1e-3
                    ? Math.Max(snapTol * 2.0, medianFixtureSpan * 1.15)
                    : Math.Max(1e-3, 0.90 * unitsPerM);
                double gridCell = medianFixtureSpan > 1e-3
                    ? Math.Max(0.25, Math.Max(estBreakMax * 1.5, medianFixtureSpan * 2.5))
                    : Math.Max(1.0, estBreakMax * 1.5);

                SpatialGrid2D<CurveChainNode> curveGrid = new SpatialGrid2D<CurveChainNode>(gridCell);
                for (int ci = 0; ci < candidates.Count; ci++)
                {
                    CurveChainNode c = candidates[ci];
                    curveGrid.Add(c.MinX, c.MinY, c.MaxX, c.MaxY, c);
                }

                SpatialGrid2D<CrossingObstacleSegment> obstacleGrid = new SpatialGrid2D<CrossingObstacleSegment>(gridCell);
                for (int oi = 0; oi < obstacleSegments.Count; oi++)
                {
                    CrossingObstacleSegment ob = obstacleSegments[oi];
                    obstacleGrid.Add(ob.MinX, ob.MinY, ob.MaxX, ob.MaxY, ob);
                }

                SpatialGrid2D<BridgeBlockCandidate> bridgeGrid = new SpatialGrid2D<BridgeBlockCandidate>(gridCell);
                SpatialGrid2D<BridgeBlockCandidate> terminatorGrid = new SpatialGrid2D<BridgeBlockCandidate>(gridCell);
                double maxBridgeTol = 0.0;
                double maxTermTol = 0.0;
                for (int bi = 0; bi < bridgeBlocks.Count; bi++)
                {
                    BridgeBlockCandidate blk = bridgeBlocks[bi];
                    bridgeGrid.Add(blk.Bounds.MinPoint.X, blk.Bounds.MinPoint.Y, blk.Bounds.MaxPoint.X, blk.Bounds.MaxPoint.Y, blk);
                    double estBTol = (blk.IsTerminator || blk.IsPanelRoot)
                        ? Math.Max(snapTol, blk.Span * 0.45)
                        : Math.Max(snapTol, blk.Span * (connectGaps ? 0.36 : 0.18));
                    if (estBTol > maxBridgeTol) maxBridgeTol = estBTol;
                    if (blk.IsTerminator || blk.IsPanelRoot)
                    {
                        terminatorGrid.Add(blk.Bounds.MinPoint.X, blk.Bounds.MinPoint.Y, blk.Bounds.MaxPoint.X, blk.Bounds.MaxPoint.Y, blk);
                        double tTol = Math.Max(snapTol, blk.Span * 0.55);
                        if (tTol > maxTermTol) maxTermTol = tTol;
                    }
                }

                CircuitChainScope scope = new CircuitChainScope
                {
                    UseComponentLayerFilter = false,
                    StrictColor = strictColor,
                    StrictLayer = strictLayer,
                    AnyNeutralSeed = anyNeutralSeed,
                    TargetLayers = targetLayers,
                    SeedColorKeys = seedColorKeys,
                    LearnedProfile = learnedProfile
                };

                return ExpandConnectedPathCurvesCore(
                    queue, visited, null, pathCurveIds, pathHandles,
                    curveGrid, bridgeGrid, terminatorGrid, obstacleGrid, maxBridgeTol, maxTermTol,
                    snapTol, bridgeThroughBlocks, connectGaps,
                    unitsPerM, medianFixtureSpan, scope, maxCircuitBlocks,
                    out bridgedBlockNames, out touchedTerminatorBlockIds, out circuitGuardTriggered);
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
                double seedTol = sbSpan > 1e-3 ? Math.Max(1e-3, sbSpan * 0.25) : Math.Max(1e-3, 0.05 * unitsPerM);

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
            Dictionary<string, SavedColorState> layerColorMap = BuildLayerColorStateMap(db, tr);
            CircuitSampleProfile prof = new CircuitSampleProfile();
            List<double> selBlkSpans = new List<double>();
            for (int pi = 0; pi < pathCurveIds.Count; pi++)
            {
                Curve cv = tr.GetObject(pathCurveIds[pi], OpenMode.ForRead) as Curve;
                if (cv == null) continue;
                prof.CircuitLayers.Add(cv.Layer ?? "0");
                if (cv.ColorIndex != 256 && cv.ColorIndex != 0) prof.ExplicitColors.Add(cv.ColorIndex);
                bool nn;
                prof.ColorKeys.Add(ResolveEntityEffectiveColorKey(cv, layerColorMap, out nn));
                if (HasCurveBulges(cv)) prof.PreferCurvedWires = true;
            }
            for (int bi = 0; bi < selectedBlockIds.Count; bi++)
            {
                BlockReference br = tr.GetObject(selectedBlockIds[bi], OpenMode.ForRead) as BlockReference;
                if (br == null) continue;
                string bn = ResolveBlockName(tr, br);
                if (IsPanelOrFrameBlockName(bn)) continue;
                if (IsCircuitTerminatorArrowBlock(bn) || IsPanelDistributionBlockName(bn))
                {
                    prof.TerminatorBlockNames.Add(bn);
                    continue;
                }
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
            prof.LearnedFixtureCount = selBlkSpans.Count;
            if (selBlkSpans.Count > 0)
            {
                selBlkSpans.Sort();
                prof.MedianFixtureSpan = selBlkSpans[selBlkSpans.Count / 2];
            }
            else prof.MedianFixtureSpan = medianFixtureSpan;
            prof.LearnedSnapTol = prof.MedianFixtureSpan > 1e-3
                ? Math.Max(1e-3, prof.MedianFixtureSpan * 0.12)
                : Math.Max(1e-3, 0.05 * unitsPerM);
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
            out int skippedAiLayerCount,
            List<ObjectId> pathCurveIds,
            List<string> pathHandles,
            List<ObjectId> selectedBlockIds)
        {
            skippedAiLayerCount = 0;
            drawingSpan = 0.0;
            medianFixtureSpan = 0.0;
            BlockTableRecord msPre = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            Dictionary<string, bool> preHidden = BuildLayerHiddenMap(db, tr);
            Extents3d? dwgEx = null;
            List<double> candidateBlockSpans = new List<double>();
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
                    if (!string.IsNullOrEmpty(mbn) && !IsAnonymousBlockName(mbn) && !IsSystemBlockName(mbn) &&
                        !IsPanelOrFrameBlockName(mbn) && !IsCircuitTerminatorArrowBlock(mbn) && !IsPanelDistributionBlockName(mbn))
                    {
                        double sp = Math.Max(mex.MaxPoint.X - mex.MinPoint.X, mex.MaxPoint.Y - mex.MinPoint.Y);
                        if (sp > 1e-4) candidateBlockSpans.Add(sp);
                    }
                }
            }
            if (dwgEx.HasValue)
                drawingSpan = Math.Max(dwgEx.Value.MaxPoint.X - dwgEx.Value.MinPoint.X, dwgEx.Value.MaxPoint.Y - dwgEx.Value.MinPoint.Y);
            List<double> smallSpans = new List<double>();
            for (int si = 0; si < candidateBlockSpans.Count; si++)
            {
                double sp = candidateBlockSpans[si];
                if (drawingSpan <= 1e-3 || sp < drawingSpan * 0.08)
                    smallSpans.Add(sp);
            }
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
                if (IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer))
                {
                    skippedAiLayerCount++;
                    continue;
                }
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

            // Persist sample profile ONLY when engineer explicitly asks to learn a sample circuit
            if (learnSample && pathCurveIds.Count >= 1)
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

        private static string ExtractBestCircuitTagFromText(string raw, out string panelPrefix)
        {
            panelPrefix = "";
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string clean = Regex.Replace(raw, @"\\P|\r|\n", " ");
            clean = Regex.Replace(clean, @"\\[A-Za-z][^;]*;|[{}]", "").Trim();
            if (clean.Length == 0) return "";

            Match m1 = Regex.Match(clean, @"\b(?:LP|DB|MDB|SMDB|EMDB|EMLP|ELP|LCP|PP|MCC)[-\s_]*[A-Z0-9]+(?:[./\\-][A-Z0-9]+)*\b", RegexOptions.IgnoreCase);
            if (m1.Success)
            {
                string tag = m1.Value.Trim();
                int slashIdx = tag.LastIndexOfAny(new char[] { '/', '\\' });
                if (slashIdx > 0) panelPrefix = tag.Substring(0, slashIdx).Trim();
                return tag;
            }

            Match m2 = Regex.Match(clean, @"\b[A-Z]{1,4}[-_.]?[0-9]+(?:[/\\.-][A-Z0-9]+)+\b", RegexOptions.IgnoreCase);
            if (m2.Success)
            {
                string tag = m2.Value.Trim();
                int slashIdx = tag.LastIndexOfAny(new char[] { '/', '\\' });
                if (slashIdx > 0) panelPrefix = tag.Substring(0, slashIdx).Trim();
                return tag;
            }
            return "";
        }

        private static void CollectNearbyCircuitTags(
            Transaction tr,
            BlockTableRecord ms,
            Dictionary<string, bool> layerHiddenFlags,
            bool visibleOnly,
            double searchRadius,
            List<CircuitTerminatorInfo> terminators,
            List<Point2d> openWireTips,
            out string circuitName,
            out string panelName,
            out string terminationType)
        {
            circuitName = "";
            panelName = "";
            terminationType = "open_end";

            if (terminators.Count > 0)
            {
                bool hasPanel = false;
                for (int i = 0; i < terminators.Count; i++)
                {
                    if (terminators[i].Kind == "panel_connection") hasPanel = true;
                }
                terminationType = hasPanel ? "panel_connection" : "home_run_arrow";
            }

            List<Point2d> anchors = new List<Point2d>();
            for (int i = 0; i < terminators.Count; i++)
                anchors.Add(new Point2d(terminators[i].X, terminators[i].Y));
            if (anchors.Count == 0 && openWireTips != null)
                anchors.AddRange(openWireTips);
            if (anchors.Count == 0) return;

            double bestDist = double.MaxValue;
            for (int ti = 0; ti < terminators.Count; ti++)
            {
                string lbl = terminators[ti].Label;
                if (!string.IsNullOrWhiteSpace(lbl))
                {
                    string pPref;
                    string tag = ExtractBestCircuitTagFromText(lbl, out pPref);
                    if (!string.IsNullOrEmpty(tag))
                    {
                        circuitName = tag;
                        if (!string.IsNullOrEmpty(pPref)) panelName = pPref;
                        else if (!string.IsNullOrEmpty(terminators[ti].PanelName)) panelName = terminators[ti].PanelName;
                        return;
                    }
                }
            }

            foreach (ObjectId mid in ms)
            {
                if (mid.IsErased) continue;
                Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                if (visibleOnly && !IsEntityVisible(ent, layerHiddenFlags)) continue;

                string txt = null;
                Point2d pos = Point2d.Origin;
                DBText dbt = ent as DBText;
                MText mt = ent as MText;
                MLeader ml = ent as MLeader;
                if (dbt != null) { txt = dbt.TextString; pos = new Point2d(dbt.Position.X, dbt.Position.Y); }
                else if (mt != null) { txt = mt.Text; pos = new Point2d(mt.Location.X, mt.Location.Y); }
                else if (ml != null && ml.ContentType == ContentType.MTextContent && ml.MText != null)
                {
                    txt = ml.MText.Text;
                    pos = new Point2d(ml.MText.Location.X, ml.MText.Location.Y);
                }
                if (string.IsNullOrWhiteSpace(txt)) continue;

                double minAnchorDist = double.MaxValue;
                for (int ai = 0; ai < anchors.Count; ai++)
                {
                    double d = anchors[ai].GetDistanceTo(pos);
                    if (d < minAnchorDist) minAnchorDist = d;
                }
                if (minAnchorDist > searchRadius) continue;

                string pPref;
                string tag = ExtractBestCircuitTagFromText(txt, out pPref);
                if (string.IsNullOrEmpty(tag)) continue;
                if (minAnchorDist < bestDist)
                {
                    bestDist = minAnchorDist;
                    circuitName = tag;
                    panelName = pPref;
                }
            }

            if (string.IsNullOrEmpty(panelName))
            {
                for (int ti = 0; ti < terminators.Count; ti++)
                {
                    if (!string.IsNullOrEmpty(terminators[ti].PanelName))
                    {
                        panelName = terminators[ti].PanelName;
                        break;
                    }
                }
            }
        }

        private static void MatchBlocksAlongCircuitPath(
            Transaction tr,
            BlockTableRecord ms,
            Dictionary<string, bool> layerHiddenFlags,
            List<PathCurveCandidate> pathCandidates,
            HashSet<ObjectId> pathIdSet,
            HashSet<ObjectId> touchedTerminatorBlockIds,
            bool visibleOnly,
            bool includeAnonymous,
            bool includeSystem,
            bool includeContainerBlocks,
            bool includeTerminatorBlocks,
            bool connectGaps,
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
            List<CircuitTerminatorInfo> terminators,
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

                bool isTerminatorArrow = IsCircuitTerminatorArrowBlock(bn);
                bool isPanelBlock = IsPanelDistributionBlockName(bn);

                Extents3d bex;
                try { bex = br.GeometricExtents; }
                catch { continue; }
                if (!IsFiniteBounds(bex)) continue;

                double bw = Math.Max(0.0, bex.MaxPoint.X - bex.MinPoint.X);
                double bh = Math.Max(0.0, bex.MaxPoint.Y - bex.MinPoint.Y);
                double blockSpan = Math.Max(bw, bh);

                if (isTerminatorArrow || isPanelBlock)
                {
                    double tTol = Math.Max(1e-3, blockSpan * 0.55);
                    int hpT; double hdT;
                    bool touched = (touchedTerminatorBlockIds != null && touchedTerminatorBlockIds.Contains(mid)) ||
                        DoesPathHitBlockXY(pathCandidates, br, bex, tTol, unitsPerM, out hpT, out hdT);
                    if (touched)
                    {
                        string attrText = "";
                        try
                        {
                            foreach (ObjectId attId in br.AttributeCollection)
                            {
                                AttributeReference ar = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                                if (ar != null && !string.IsNullOrWhiteSpace(ar.TextString))
                                    attrText += " " + ar.TextString.Trim();
                            }
                        }
                        catch { }
                        terminators.Add(new CircuitTerminatorInfo
                        {
                            BlockId = mid,
                            Handle = ent.Handle.ToString(),
                            BlockName = bn,
                            Kind = isPanelBlock ? "panel_connection" : "home_run_arrow",
                            Label = attrText.Trim(),
                            PanelName = isPanelBlock ? bn : "",
                            X = (bex.MinPoint.X + bex.MaxPoint.X) * 0.5,
                            Y = (bex.MinPoint.Y + bex.MaxPoint.Y) * 0.5
                        });
                    }
                    if (!includeTerminatorBlocks && isTerminatorArrow) continue;
                }

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

                if (IsLikelyArchitecturalContainerBlock(bn, blockSpan, refFixSpan, drawingSpan, unitsPerM, includeContainerBlocks, blockFilter))
                {
                    containerBlocksSkipped++;
                    continue;
                }

                double tol = explicitTolerance.HasValue
                    ? Math.Max(1e-5, explicitTolerance.Value)
                    : Math.Max(1e-3, blockSpan * (connectGaps ? 0.36 : 0.18));

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

        private sealed class CircuitTraceMeta
        {
            public string CircuitName = "";
            public string PanelName = "";
            public string TerminationType = "open_end";
            public string SeedWireColorKey = "";
            public string InsunitsName = "";
            public bool HasCustomTitle;
            public bool UnitScaleInferred;
            public double EffectiveUnitsPerM = 1000.0;
            public string UnitScaleWarning = "";
            public bool CircuitGuardTriggered;
            public int MaxCircuitBlocks = 35;
            public bool LearnedSampleThisCall;
            public bool FromSavedMemory;
            public bool UserLocked;
            public bool SavedToMemory;
            public bool MergedWithSaved;
            public bool SkipAutoMemorySave;
            public List<CircuitTerminatorInfo> Terminators = new List<CircuitTerminatorInfo>();
        }

        private sealed class SavedCircuitRecord
        {
            public string CircuitName = "";
            public string PanelName = "";
            public string TerminationType = "open_end";
            public string WireLayer = "";
            public string WireColorKey = "";
            public List<string> PathHandles = new List<string>();
            public List<string> MatchedBlockHandles = new List<string>();
            public List<string> TerminatorHandles = new List<string>();
            public double LengthMeters;
            public Dictionary<string, int> CountsByBlock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public bool UserLocked;
        }

        private static readonly Dictionary<IntPtr, Dictionary<string, SavedCircuitRecord>> s_savedCircuitsByDb =
            new Dictionary<IntPtr, Dictionary<string, SavedCircuitRecord>>();
        private static readonly Dictionary<IntPtr, string> s_lastActiveCircuitNameByDb =
            new Dictionary<IntPtr, string>();
        private static readonly HashSet<IntPtr> s_loadedSidecarForDb =
            new HashSet<IntPtr>();

        private static string NormalizeCircuitKey(string circuitName)
        {
            if (string.IsNullOrWhiteSpace(circuitName)) return "";
            return Regex.Replace(circuitName.Trim().ToUpperInvariant(), @"\s+", "");
        }

        private static string ResolveCircuitMemorySidecarPath(Document doc, Database db)
        {
            try
            {
                string docPath = (db != null && !string.IsNullOrWhiteSpace(db.Filename))
                    ? db.Filename
                    : (doc != null ? doc.Name : "");
                if (string.IsNullOrWhiteSpace(docPath) || !Path.IsPathRooted(docPath) || !File.Exists(docPath))
                    return null;
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localApp)) return null;
                string dir = Path.Combine(Path.Combine(localApp, "AI-Engineer"), "circuit_memory");
                byte[] hash;
                using (SHA256 sha = SHA256.Create())
                {
                    hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(docPath).ToLowerInvariant()));
                }
                StringBuilder sb = new StringBuilder(16);
                for (int i = 0; i < 8 && i < hash.Length; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return Path.Combine(dir, sb.ToString() + ".json");
            }
            catch { return null; }
        }

        private static void EnsureSidecarLoadedLocked(Document doc, Database db)
        {
            if (db == null || db.UnmanagedObject == IntPtr.Zero) return;
            IntPtr dbPtr = db.UnmanagedObject;
            if (s_loadedSidecarForDb.Contains(dbPtr)) return;
            s_loadedSidecarForDb.Add(dbPtr);

            try
            {
                string sidecarPath = ResolveCircuitMemorySidecarPath(doc, db);
                if (string.IsNullOrEmpty(sidecarPath) || !File.Exists(sidecarPath)) return;
                string raw = File.ReadAllText(sidecarPath, Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> root = ser.DeserializeObject(raw) as Dictionary<string, object>;
                if (root == null) return;
                Dictionary<string, object> recMap = root.ContainsKey("circuits") ? root["circuits"] as Dictionary<string, object> : null;
                if (recMap == null) return;

                Dictionary<string, SavedCircuitRecord> bucket;
                if (!s_savedCircuitsByDb.TryGetValue(dbPtr, out bucket))
                {
                    bucket = new Dictionary<string, SavedCircuitRecord>(StringComparer.OrdinalIgnoreCase);
                    s_savedCircuitsByDb[dbPtr] = bucket;
                }
                foreach (KeyValuePair<string, object> kv in recMap)
                {
                    Dictionary<string, object> rd = kv.Value as Dictionary<string, object>;
                    if (rd == null) continue;
                    string cName = GetStr(rd, "circuit_name") ?? kv.Key;
                    string normKey = NormalizeCircuitKey(cName);
                    if (string.IsNullOrEmpty(normKey)) continue;
                    SavedCircuitRecord rec = new SavedCircuitRecord
                    {
                        CircuitName = cName,
                        PanelName = GetStr(rd, "panel_name") ?? "",
                        TerminationType = GetStr(rd, "termination_type") ?? "open_end",
                        WireLayer = GetStr(rd, "wire_layer") ?? "",
                        WireColorKey = GetStr(rd, "wire_color_key") ?? "",
                        PathHandles = GetStringList(rd, "path_handles"),
                        MatchedBlockHandles = GetStringList(rd, "matched_block_handles"),
                        TerminatorHandles = GetStringList(rd, "terminator_handles"),
                        LengthMeters = GetDouble(rd, "length_m", 0.0),
                        UserLocked = GetBool(rd, "user_locked", false)
                    };
                    Dictionary<string, object> cntObj = rd.ContainsKey("counts_by_block") ? rd["counts_by_block"] as Dictionary<string, object> : null;
                    if (cntObj != null)
                    {
                        foreach (KeyValuePair<string, object> ckv in cntObj)
                            rec.CountsByBlock[ckv.Key] = Convert.ToInt32(ckv.Value, CultureInfo.InvariantCulture);
                    }
                    bucket[normKey] = rec;
                }
                string lastAct = GetStr(root, "last_active_circuit");
                if (!string.IsNullOrWhiteSpace(lastAct)) s_lastActiveCircuitNameByDb[dbPtr] = lastAct;
            }
            catch { }
        }

        private static void PersistSidecarLocked(Document doc, Database db)
        {
            try
            {
                if (db == null || db.UnmanagedObject == IntPtr.Zero) return;
                string sidecarPath = ResolveCircuitMemorySidecarPath(doc, db);
                if (string.IsNullOrEmpty(sidecarPath)) return;
                IntPtr dbPtr = db.UnmanagedObject;
                Dictionary<string, SavedCircuitRecord> bucket;
                if (!s_savedCircuitsByDb.TryGetValue(dbPtr, out bucket)) return;

                Dictionary<string, object> serializedMap = new Dictionary<string, object>();
                foreach (KeyValuePair<string, SavedCircuitRecord> kv in bucket)
                {
                    SavedCircuitRecord r = kv.Value;
                    serializedMap[kv.Key] = new Dictionary<string, object>
                    {
                        { "circuit_name", r.CircuitName },
                        { "panel_name", r.PanelName },
                        { "termination_type", r.TerminationType },
                        { "wire_layer", r.WireLayer },
                        { "wire_color_key", r.WireColorKey },
                        { "path_handles", r.PathHandles },
                        { "matched_block_handles", r.MatchedBlockHandles },
                        { "terminator_handles", r.TerminatorHandles },
                        { "length_m", r.LengthMeters },
                        { "counts_by_block", r.CountsByBlock },
                        { "user_locked", r.UserLocked }
                    };
                }
                string lastAct = "";
                s_lastActiveCircuitNameByDb.TryGetValue(dbPtr, out lastAct);
                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    { "last_active_circuit", lastAct ?? "" },
                    { "circuits", serializedMap }
                };
                string dir = Path.GetDirectoryName(sidecarPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(sidecarPath, new JavaScriptSerializer().Serialize(payload), Encoding.UTF8);
            }
            catch { }
        }

        private static SavedCircuitRecord CloneSavedRecord(SavedCircuitRecord src)
        {
            if (src == null) return null;
            return new SavedCircuitRecord
            {
                CircuitName = src.CircuitName ?? "",
                PanelName = src.PanelName ?? "",
                TerminationType = src.TerminationType ?? "open_end",
                WireLayer = src.WireLayer ?? "",
                WireColorKey = src.WireColorKey ?? "",
                PathHandles = new List<string>(src.PathHandles ?? new List<string>()),
                MatchedBlockHandles = new List<string>(src.MatchedBlockHandles ?? new List<string>()),
                TerminatorHandles = new List<string>(src.TerminatorHandles ?? new List<string>()),
                LengthMeters = src.LengthMeters,
                CountsByBlock = new Dictionary<string, int>(src.CountsByBlock ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase),
                UserLocked = src.UserLocked
            };
        }

        private static void SaveCircuitRecordToMemory(
            Document doc,
            SavedCircuitRecord rec,
            bool setLastActive,
            bool allowOverwriteLocked)
        {
            if (doc == null || doc.Database == null || rec == null) return;
            string normKey = NormalizeCircuitKey(rec.CircuitName);
            if (string.IsNullOrEmpty(normKey)) return;
            Database db = doc.Database;
            IntPtr dbPtr = db.UnmanagedObject;
            if (dbPtr == IntPtr.Zero) return;

            lock (s_lastCoordLock)
            {
                EnsureSidecarLoadedLocked(doc, db);
                Dictionary<string, SavedCircuitRecord> bucket;
                if (!s_savedCircuitsByDb.TryGetValue(dbPtr, out bucket))
                {
                    bucket = new Dictionary<string, SavedCircuitRecord>(StringComparer.OrdinalIgnoreCase);
                    s_savedCircuitsByDb[dbPtr] = bucket;
                }
                SavedCircuitRecord existing;
                if (bucket.TryGetValue(normKey, out existing) && existing != null && existing.UserLocked && !allowOverwriteLocked && !rec.UserLocked)
                {
                    if (setLastActive)
                    {
                        s_lastActiveCircuitNameByDb[dbPtr] = existing.CircuitName;
                        PersistSidecarLocked(doc, db);
                    }
                    return;
                }
                bucket[normKey] = CloneSavedRecord(rec);
                if (setLastActive) s_lastActiveCircuitNameByDb[dbPtr] = rec.CircuitName.Trim();
                PersistSidecarLocked(doc, db);
            }
        }

        private static bool TryGetSavedCircuitRecord(Document doc, string circuitName, out SavedCircuitRecord rec)
        {
            rec = null;
            if (doc == null || doc.Database == null || string.IsNullOrWhiteSpace(circuitName)) return false;
            Database db = doc.Database;
            IntPtr dbPtr = db.UnmanagedObject;
            if (dbPtr == IntPtr.Zero) return false;
            string normKey = NormalizeCircuitKey(circuitName);
            if (string.IsNullOrEmpty(normKey)) return false;

            lock (s_lastCoordLock)
            {
                EnsureSidecarLoadedLocked(doc, db);
                Dictionary<string, SavedCircuitRecord> bucket;
                if (!s_savedCircuitsByDb.TryGetValue(dbPtr, out bucket) || bucket == null) return false;
                SavedCircuitRecord hit;
                if (bucket.TryGetValue(normKey, out hit) && hit != null)
                {
                    rec = CloneSavedRecord(hit);
                    return true;
                }
                string alnumQuery = Regex.Replace(normKey, @"[^A-Z0-9]", "");
                foreach (KeyValuePair<string, SavedCircuitRecord> kv in bucket)
                {
                    if (Regex.Replace(kv.Key, @"[^A-Z0-9]", "") == alnumQuery && kv.Value != null)
                    {
                        rec = CloneSavedRecord(kv.Value);
                        return true;
                    }
                }
                return false;
            }
        }

        private static string GetLastActiveCircuitName(Document doc)
        {
            if (doc == null || doc.Database == null || doc.Database.UnmanagedObject == IntPtr.Zero) return "";
            lock (s_lastCoordLock)
            {
                EnsureSidecarLoadedLocked(doc, doc.Database);
                string nm;
                return s_lastActiveCircuitNameByDb.TryGetValue(doc.Database.UnmanagedObject, out nm) ? (nm ?? "") : "";
            }
        }

        private static List<SavedCircuitRecord> GetLockedCircuitRecordsCopy(Document doc)
        {
            List<SavedCircuitRecord> list = new List<SavedCircuitRecord>();
            if (doc == null || doc.Database == null || doc.Database.UnmanagedObject == IntPtr.Zero) return list;
            lock (s_lastCoordLock)
            {
                EnsureSidecarLoadedLocked(doc, doc.Database);
                Dictionary<string, SavedCircuitRecord> bucket;
                if (!s_savedCircuitsByDb.TryGetValue(doc.Database.UnmanagedObject, out bucket) || bucket == null) return list;
                foreach (SavedCircuitRecord r in bucket.Values)
                {
                    if (r != null && r.UserLocked) list.Add(CloneSavedRecord(r));
                }
            }
            return list;
        }

        private static List<ObjectId> FindCircuitSeedCurveByTagName(
            Database db,
            Transaction tr,
            string circuitName,
            Dictionary<string, bool> hiddenMap,
            bool visibleOnly,
            double refSpan,
            double unitsPerM)
        {
            List<ObjectId> seeds = new List<ObjectId>();
            string targetNorm = NormalizeCircuitKey(circuitName);
            if (string.IsNullOrEmpty(targetNorm)) return seeds;

            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            List<Point2d> tagAnchors = new List<Point2d>();
            List<Curve> wireCurves = new List<Curve>();

            foreach (ObjectId mid in ms)
            {
                if (mid.IsErased) continue;
                Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                if (visibleOnly && !IsEntityVisible(ent, hiddenMap)) continue;

                Curve cv = ent as Curve;
                if (cv != null)
                {
                    Point2d sp, ep;
                    if (TryGetCurveEndpointsXY(cv, out sp, out ep))
                        wireCurves.Add(cv);
                    continue;
                }

                string txt = null;
                Point2d pos = Point2d.Origin;
                DBText dbt = ent as DBText;
                MText mt = ent as MText;
                MLeader ml = ent as MLeader;
                BlockReference br = ent as BlockReference;
                if (dbt != null) { txt = dbt.TextString; pos = new Point2d(dbt.Position.X, dbt.Position.Y); }
                else if (mt != null) { txt = mt.Text; pos = new Point2d(mt.Location.X, mt.Location.Y); }
                else if (ml != null && ml.ContentType == ContentType.MTextContent && ml.MText != null)
                {
                    txt = ml.MText.Text;
                    pos = new Point2d(ml.MText.Location.X, ml.MText.Location.Y);
                }
                else if (br != null)
                {
                    string attr = ResolveCachedBlockTag(tr, br);
                    if (!string.IsNullOrWhiteSpace(attr))
                    {
                        txt = attr;
                        pos = new Point2d(br.Position.X, br.Position.Y);
                    }
                }
                if (string.IsNullOrWhiteSpace(txt)) continue;

                string pPref;
                string extracted = ExtractBestCircuitTagFromText(txt, out pPref);
                string cleanTxt = Regex.Replace(Regex.Replace(txt, @"\\[A-Za-z][^;]*;|[{}]", ""), @"\s+", "").ToUpperInvariant();
                if (string.Equals(NormalizeCircuitKey(extracted), targetNorm, StringComparison.OrdinalIgnoreCase) ||
                    cleanTxt.Contains(targetNorm))
                {
                    tagAnchors.Add(pos);
                }
            }

            if (tagAnchors.Count == 0 || wireCurves.Count == 0) return seeds;
            double maxSearchR = refSpan > 1e-3 ? Math.Max(refSpan * 8.0, 2.5 * unitsPerM) : Math.Max(5.0 * unitsPerM, 5.0);
            ObjectId bestCurve = ObjectId.Null;
            double bestScore = double.MaxValue;

            for (int ci = 0; ci < wireCurves.Count; ci++)
            {
                Curve cv = wireCurves[ci];
                if (IsArchitecturalLayerName(cv.Layer ?? "")) continue;
                Point2d sp, ep;
                if (!TryGetCurveEndpointsXY(cv, out sp, out ep)) continue;
                double dMin = double.MaxValue;
                for (int ai = 0; ai < tagAnchors.Count; ai++)
                {
                    double ds = tagAnchors[ai].GetDistanceTo(sp);
                    double de = tagAnchors[ai].GetDistanceTo(ep);
                    if (ds < dMin) dMin = ds;
                    if (de < dMin) dMin = de;
                    try
                    {
                        Point3d cp = cv.GetClosestPointTo(new Point3d(tagAnchors[ai].X, tagAnchors[ai].Y, 0.0), false);
                        double dc = tagAnchors[ai].GetDistanceTo(new Point2d(cp.X, cp.Y));
                        if (dc < dMin) dMin = dc;
                    }
                    catch { }
                }
                if (dMin > maxSearchR) continue;
                double priorityBias = (IsCandidateCircuitLayerName(cv.Layer ?? "") || (cv.ColorIndex != 256 && cv.ColorIndex != 0 && cv.ColorIndex != 7)) ? 0.5 : 1.0;
                double score = dMin * priorityBias;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestCurve = cv.ObjectId;
                }
            }
            if (!bestCurve.IsNull) seeds.Add(bestCurve);
            return seeds;
        }

        private sealed class TracedCircuitSummary
        {
            public string CircuitName = "";
            public string PanelName = "";
            public string TerminationType = "open_end";
            public string WireLayer = "";
            public string WireColorKey = "";
            public int PathCurvesCount;
            public double LengthUnits;
            public double LengthMeters;
            public int MatchedBlocksCount;
            public Dictionary<string, int> CountsByBlock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public string CountsSummary = "";
            public bool GuardTriggered;
            public double MinX;
            public double MinY;
            public string EndSummary = "";
            public List<ObjectId> MatchedBlockIds = new List<ObjectId>();
            public List<ObjectId> PathCurveIds = new List<ObjectId>();
            public List<string> MatchedHandles = new List<string>();
        }

        private sealed class DrawingCircuitScanResult
        {
            public List<TracedCircuitSummary> Circuits = new List<TracedCircuitSummary>();
            public DrawingBlockGeometryCache Cache;
            public double EffectiveUnitsPerM = 1000.0;
            public bool ScaleInferred;
            public string ScaleWarning = "";
            public double DrawingSpan;
            public double MedianFixtureSpan;
            public double RefSpan;
            public Extents3d? MainExt;
            public int TotalBlocks;
            public double TotalLengthM;
            public int GuardTriggeredCount;
            public string ErrorMessage = "";
        }

        private static List<Point2d> CollectOpenWireTips(Transaction tr, List<ObjectId> pathCurveIds, double tol)
        {
            List<Point2d> allPts = new List<Point2d>();
            for (int i = 0; i < pathCurveIds.Count; i++)
            {
                Curve cv = tr.GetObject(pathCurveIds[i], OpenMode.ForRead) as Curve;
                Point2d sp, ep;
                if (!TryGetCurveEndpointsXY(cv, out sp, out ep)) continue;
                allPts.Add(sp);
                allPts.Add(ep);
            }
            List<Point2d> tips = new List<Point2d>();
            for (int i = 0; i < allPts.Count; i++)
            {
                int deg = 0;
                for (int j = 0; j < allPts.Count; j++)
                {
                    if (i != j && allPts[i].GetDistanceTo(allPts[j]) <= tol) deg++;
                }
                if (deg == 0) tips.Add(allPts[i]);
            }
            return tips;
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
            CircuitTraceMeta traceMeta,
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

            double lenMeters = Math.Round(totalPathLenUnits / unitsPerM, 3);
            string circuitName = (traceMeta != null && !string.IsNullOrWhiteSpace(traceMeta.CircuitName)) ? traceMeta.CircuitName.Trim() : "";
            string panelName = (traceMeta != null && !string.IsNullOrWhiteSpace(traceMeta.PanelName)) ? traceMeta.PanelName.Trim() : "";
            string termType = (traceMeta != null && !string.IsNullOrWhiteSpace(traceMeta.TerminationType)) ? traceMeta.TerminationType : "open_end";

            string effectiveTitle = title;
            if (traceMeta == null || !traceMeta.HasCustomTitle)
            {
                if (!string.IsNullOrEmpty(circuitName))
                    effectiveTitle = "حصر بلوكات اللينيا: " + circuitName + " (الطول: " + lenMeters.ToString("0.##", CultureInfo.InvariantCulture) + " م)";
                else
                    effectiveTitle = "جدول حصر ومفاتيح بلوكات المسار (الطول: " + lenMeters.ToString("0.##", CultureInfo.InvariantCulture) + " م)";
            }
            string footerLabel = !string.IsNullOrEmpty(circuitName)
                ? ("إجمالي بلوكات اللينيا (" + circuitName + ")")
                : "إجمالي البلوكات على المسار المحدد";

            double? fixtureWattArg = GetNullableDouble(args, "fixture_watt");
            double? fixtureVaArg = GetNullableDouble(args, "fixture_va");
            bool includeElecCalc = GetBool(args, "include_electrical_calc", false) ||
                (fixtureWattArg.HasValue && fixtureWattArg.Value > 0) ||
                (fixtureVaArg.HasValue && fixtureVaArg.Value > 0);
            LvCircuitCalcResult lvRes = null;
            Dictionary<string, object> electricalCalcDict = null;
            bool loadAssumed = false;
            Dictionary<string, object> rowLoadByBlock = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string totalLoadLabel = "";
            string maxVdLabel = "";
            string rowVdLabel = "";
            bool vdPass = true;
            if (includeElecCalc && matchedItems.Count > 0)
            {
                Dictionary<string, object> lvItem = new Dictionary<string, object>
                {
                    { "name", !string.IsNullOrEmpty(circuitName) ? circuitName : "CIRCUIT" },
                    { "load_type", GetStr(args, "load_type") ?? "lighting" },
                    { "length_m", lenMeters },
                    { "fixture_count", (double)matchedItems.Count }
                };
                if (args != null && args.ContainsKey("phases"))
                    lvItem["phases"] = GetInt(args, "phases", 1);
                else if (string.Equals(GetStr(lvItem, "load_type"), "lighting", StringComparison.OrdinalIgnoreCase))
                    lvItem["phases"] = 1;
                if (args != null && args.ContainsKey("voltage_v"))
                    lvItem["voltage_v"] = GetDouble(args, "voltage_v", 220.0);
                if (args != null && args.ContainsKey("power_factor"))
                    lvItem["power_factor"] = GetDouble(args, "power_factor", 0.90);
                if (args != null && args.ContainsKey("max_vd_percent"))
                    lvItem["max_vd_percent"] = GetDouble(args, "max_vd_percent", 3.0);
                string matArg = GetStr(args, "material");
                if (!string.IsNullOrEmpty(matArg)) lvItem["material"] = matArg;
                string insArg = GetStr(args, "insulation");
                if (!string.IsNullOrEmpty(insArg)) lvItem["insulation"] = insArg;
                string methArg = GetStr(args, "install_method");
                if (!string.IsNullOrEmpty(methArg)) lvItem["install_method"] = methArg;
                double? ambArg = GetNullableDouble(args, "ambient_temp_c");
                if (ambArg.HasValue) lvItem["ambient_temp_c"] = ambArg.Value;

                if (fixtureWattArg.HasValue && fixtureWattArg.Value > 0)
                    lvItem["fixture_watt"] = fixtureWattArg.Value;
                else if (fixtureVaArg.HasValue && fixtureVaArg.Value > 0)
                    lvItem["fixture_va"] = fixtureVaArg.Value;
                else
                {
                    lvItem["fixture_va"] = 40.0;
                    loadAssumed = true;
                }

                Dictionary<string, object> lvDefaults = new Dictionary<string, object>();
                lvRes = ComputeSingleLvCircuit(lvItem, lvDefaults, 1);

                foreach (KeyValuePair<string, int> kv in countsByBlock)
                {
                    if (fixtureWattArg.HasValue && fixtureWattArg.Value > 0)
                    {
                        double w = kv.Value * fixtureWattArg.Value;
                        rowLoadByBlock[kv.Key] = Math.Round(w, 0).ToString(CultureInfo.InvariantCulture) + " W (" +
                            Math.Round(w / 1000.0, 3).ToString("0.###", CultureInfo.InvariantCulture) + " kW)";
                    }
                    else
                    {
                        double vaPerUnit = (fixtureVaArg.HasValue && fixtureVaArg.Value > 0) ? fixtureVaArg.Value : 40.0;
                        double va = kv.Value * vaPerUnit;
                        rowLoadByBlock[kv.Key] = Math.Round(va, 0).ToString(CultureInfo.InvariantCulture) + " VA (" +
                            Math.Round(va / 1000.0, 3).ToString("0.###", CultureInfo.InvariantCulture) + " kVA)";
                    }
                }

                totalLoadLabel = lvRes.LoadKw.ToString("0.###", CultureInfo.InvariantCulture) + " kW / " +
                    lvRes.LoadKva.ToString("0.###", CultureInfo.InvariantCulture) + " kVA";
                maxVdLabel = lvRes.VoltageDropPercent.ToString("0.##", CultureInfo.InvariantCulture) + "% (<=" +
                    lvRes.MaxAllowedVdPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%) | " +
                    lvRes.CableSizeMm2.ToString("0.#", CultureInfo.InvariantCulture) + "mm2 / " +
                    lvRes.BreakerRatingA.ToString("0.#", CultureInfo.InvariantCulture) + "A";
                rowVdLabel = lvRes.VoltageDropPercent.ToString("0.##", CultureInfo.InvariantCulture) + "% (" +
                    lvRes.CableSizeMm2.ToString("0.#", CultureInfo.InvariantCulture) + "mm2)";
                vdPass = lvRes.VoltageDropPercent <= lvRes.MaxAllowedVdPercent + 1e-6;

                electricalCalcDict = new Dictionary<string, object>
                {
                    { "circuit_name", lvRes.CircuitName },
                    { "load_type", lvRes.LoadType },
                    { "phases", lvRes.Phases },
                    { "voltage_v", lvRes.VoltageV },
                    { "power_factor", lvRes.PowerFactor },
                    { "length_m", lvRes.LengthM },
                    { "fixture_count", matchedItems.Count },
                    { "load_kw", lvRes.LoadKw },
                    { "load_kva", lvRes.LoadKva },
                    { "design_current_ib_a", lvRes.DesignCurrentA },
                    { "breaker_in_a", lvRes.BreakerRatingA },
                    { "cable_designation", lvRes.CableDesignation },
                    { "cable_size_mm2", lvRes.CableSizeMm2 },
                    { "voltage_drop_v", lvRes.VoltageDropV },
                    { "voltage_drop_percent", lvRes.VoltageDropPercent },
                    { "max_vd_percent", lvRes.MaxAllowedVdPercent },
                    { "vd_pass", vdPass },
                    { "load_assumed", loadAssumed }
                };
                if (loadAssumed)
                {
                    electricalCalcDict["load_assumption_warning"] =
                        "لم يتم تحديد قدرة الكشاف، لذا تم افتراض 40 VA لكل كشاف. أعد الحساب بتمرير fixture_watt الفعلي لتأكيد القيم الدقيقة.";
                }
            }

            Dictionary<string, object> legendResult = null;
            Extents3d? legendExt = null;
            if (drawLegend && matchedHandles.Count > 0)
            {
                Dictionary<string, object> legendArgs = new Dictionary<string, object>
                {
                    { "handles", matchedHandles.ToArray() },
                    { "title", effectiveTitle },
                    { "footer_label", footerLabel },
                    { "position", legendPosition },
                    { "visible_only", visibleOnly },
                    { "include_system_blocks", includeSystem },
                    { "include_anonymous", includeAnonymous },
                    { "replace_existing", GetBool(args, "replace_existing", true) },
                    { "export_excel", GetBool(args, "export_excel", false) },
                    { "zoom", false }
                };
                if (lvRes != null)
                {
                    legendArgs["include_electrical_columns"] = true;
                    legendArgs["row_load_by_block"] = rowLoadByBlock;
                    legendArgs["total_load_label"] = totalLoadLabel;
                    legendArgs["max_vd_label"] = maxVdLabel;
                    legendArgs["row_vd_label"] = rowVdLabel;
                    legendArgs["vd_pass"] = vdPass;
                }
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

            string startSummary = matchedItems.Count > 0
                ? (matchedItems[0].BlockName + " (#" + matchedItems[0].Handle + " @ " + Round(matchedItems[0].X) + "," + Round(matchedItems[0].Y) + ")")
                : (pathHandles.Count > 0 ? ("Wire #" + pathHandles[0]) : "");
            string endSummary = "";
            List<object> termOut = new List<object>();
            if (traceMeta != null && traceMeta.Terminators.Count > 0)
            {
                for (int ti = 0; ti < traceMeta.Terminators.Count; ti++)
                {
                    CircuitTerminatorInfo t = traceMeta.Terminators[ti];
                    termOut.Add(new Dictionary<string, object>
                    {
                        { "handle", t.Handle },
                        { "block_name", t.BlockName },
                        { "kind", t.Kind },
                        { "label", t.Label },
                        { "panel_name", t.PanelName },
                        { "x", Round(t.X) },
                        { "y", Round(t.Y) }
                    });
                }
                CircuitTerminatorInfo tFirst = traceMeta.Terminators[0];
                endSummary = !string.IsNullOrEmpty(circuitName)
                    ? (tFirst.BlockName + " -> " + circuitName)
                    : (tFirst.BlockName + " (#" + tFirst.Handle + ")");
            }
            else if (matchedItems.Count > 0)
            {
                PathBlockMatchItem lastM = matchedItems[matchedItems.Count - 1];
                endSummary = lastM.BlockName + " (#" + lastM.Handle + " @ " + Round(lastM.X) + "," + Round(lastM.Y) + ")";
                if (!string.IsNullOrEmpty(circuitName)) endSummary += " -> " + circuitName;
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "circuit_name", circuitName },
                { "panel_name", panelName },
                { "termination_type", termType },
                { "circuit_start", startSummary },
                { "circuit_end", endSummary },
                { "circuit_terminators", termOut },
                { "wire_color_key", traceMeta != null ? traceMeta.SeedWireColorKey : "" },
                { "insunits_name", traceMeta != null ? traceMeta.InsunitsName : "" },
                { "effective_units_per_m", Round(unitsPerM) },
                { "unit_scale_inferred", traceMeta != null && traceMeta.UnitScaleInferred },
                { "circuit_guard_triggered", traceMeta != null && traceMeta.CircuitGuardTriggered },
                { "path_curves_count", pathCurveIds.Count },
                { "initial_path_handles", initialPathHandles },
                { "chain_followed", pathCurveIds.Count > initialPathHandles.Count },
                { "bridged_blocks_count", bridgedBlockCount },
                { "bridged_block_names", bridgedBlockNames },
                { "container_blocks_skipped", containerBlocksSkipped },
                { "path_entity_types", new List<string>(pathEntityTypes) },
                { "path_handles", pathHandles },
                { "total_path_length_units", Round(totalPathLenUnits) },
                { "total_path_length_m", lenMeters },
                { "matched_blocks_count", matchedItems.Count },
                { "block_types_count", countsByBlock.Count },
                { "counts_by_block", countsByBlock },
                { "matched_handles", matchedHandles },
                { "matched_blocks", matchedDetails },
                { "selected_paths", selectPaths && pathCurveIds.Count > 0 },
                { "selected_blocks", selectBlocks && matchedIds.Count > 0 },
                { "selected_in_autocad", selectionTargets.Count > 0 },
                { "from_saved_memory", traceMeta != null && traceMeta.FromSavedMemory },
                { "user_locked", traceMeta != null && traceMeta.UserLocked },
                { "saved_to_memory", (traceMeta != null && traceMeta.SavedToMemory) || !string.IsNullOrEmpty(circuitName) },
                { "merged_with_saved", traceMeta != null && traceMeta.MergedWithSaved },
                { "legend_drawn", legendResult != null && legendResult.ContainsKey("ok") && legendResult["ok"] is bool && (bool)legendResult["ok"] },
                { "legend_title", effectiveTitle },
                { "switched_to_model", switchedToModel }
            };
            if (!string.IsNullOrEmpty(circuitName) && !(traceMeta != null && traceMeta.SkipAutoMemorySave))
            {
                List<string> termHandles = new List<string>();
                if (traceMeta != null && traceMeta.Terminators != null)
                {
                    for (int ti = 0; ti < traceMeta.Terminators.Count; ti++)
                    {
                        if (!string.IsNullOrWhiteSpace(traceMeta.Terminators[ti].Handle))
                            termHandles.Add(traceMeta.Terminators[ti].Handle);
                    }
                }
                string wireLyr = "";
                if (pathCurveIds.Count > 0)
                {
                    try
                    {
                        using (Transaction trLyr = doc.Database.TransactionManager.StartTransaction())
                        {
                            Curve c0 = trLyr.GetObject(pathCurveIds[0], OpenMode.ForRead) as Curve;
                            if (c0 != null) wireLyr = c0.Layer ?? "0";
                            trLyr.Commit();
                        }
                    }
                    catch { }
                }
                SavedCircuitRecord rec = new SavedCircuitRecord
                {
                    CircuitName = circuitName,
                    PanelName = panelName,
                    TerminationType = termType,
                    WireLayer = wireLyr,
                    WireColorKey = traceMeta != null ? traceMeta.SeedWireColorKey : "",
                    PathHandles = new List<string>(pathHandles),
                    MatchedBlockHandles = new List<string>(matchedHandles),
                    TerminatorHandles = termHandles,
                    LengthMeters = lenMeters,
                    CountsByBlock = new Dictionary<string, int>(countsByBlock, StringComparer.OrdinalIgnoreCase),
                    UserLocked = (traceMeta != null && traceMeta.UserLocked)
                };
                SaveCircuitRecordToMemory(doc, rec, true, traceMeta != null && (traceMeta.UserLocked || traceMeta.MergedWithSaved));
            }
            if (traceMeta != null && !string.IsNullOrEmpty(traceMeta.UnitScaleWarning))
                res["unit_scale_warning"] = traceMeta.UnitScaleWarning;
            if (traceMeta != null && traceMeta.LearnedSampleThisCall)
                res["learned_sample"] = true;
            if (traceMeta != null && traceMeta.CircuitGuardTriggered)
            {
                res["warning"] = "توقف التتبع التلقائي عند الحد الهندسي (" + traceMeta.MaxCircuitBlocks +
                    " كشافات في الدائرة الواحدة) لمنع تداخل أكثر من لينيا. تأكد من فصل الأسلاك أو ارفع max_circuit_blocks إذا كانت دائرة خاصة أكبر.";
            }
            if (learnedProfile != null && learnedProfile.CircuitLayers.Count > 0)
            {
                res["learned_circuit_layers"] = new List<string>(learnedProfile.CircuitLayers);
                if (learnedProfile.ColorKeys.Count > 0)
                    res["learned_wire_colors"] = new List<string>(learnedProfile.ColorKeys);
                if (learnedProfile.SampleBlockNames.Count > 0)
                    res["learned_fixture_blocks"] = new List<string>(learnedProfile.SampleBlockNames);
                if (learnedProfile.TerminatorBlockNames.Count > 0)
                    res["learned_terminator_blocks"] = new List<string>(learnedProfile.TerminatorBlockNames);
            }
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
                if (legendResult.ContainsKey("has_electrical_columns")) res["legend_has_electrical_columns"] = legendResult["has_electrical_columns"];
                if (legendResult.ContainsKey("excel_exported")) res["excel_exported"] = legendResult["excel_exported"];
                if (legendResult.ContainsKey("excel_path")) res["excel_path"] = legendResult["excel_path"];
            }
            if (electricalCalcDict != null)
            {
                res["electrical_calc"] = electricalCalcDict;
                if (loadAssumed && electricalCalcDict.ContainsKey("load_assumption_warning"))
                    res["load_assumption_warning"] = electricalCalcDict["load_assumption_warning"];
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
            bool scaleInferred,
            string scaleWarning,
            string insunitsName,
            double medianFixtureSpan,
            double drawingSpan,
            bool visibleOnly,
            bool includeAnonymous,
            bool includeSystem,
            bool includeContainerBlocks,
            bool includeTerminatorBlocks,
            bool selectBlocks,
            bool selectPaths,
            bool drawLegend,
            bool followConnected,
            bool bridgeThroughBlocks,
            bool connectGaps,
            bool strictColor,
            bool strictLayer,
            bool learnedSampleThisCall,
            int maxCircuitBlocks,
            bool zoom,
            string zoomMode,
            string title,
            bool hasCustomTitle,
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
            CircuitTraceMeta traceMeta = new CircuitTraceMeta
            {
                HasCustomTitle = hasCustomTitle,
                InsunitsName = insunitsName ?? "",
                UnitScaleInferred = scaleInferred,
                EffectiveUnitsPerM = unitsPerM,
                UnitScaleWarning = scaleWarning ?? "",
                MaxCircuitBlocks = maxCircuitBlocks,
                LearnedSampleThisCall = learnedSampleThisCall
            };

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
                        Dictionary<string, SavedColorState> layerColorMap = BuildLayerColorStateMap(db, tr);
                        if (pathCurveIds.Count > 0)
                        {
                            Curve seedC = tr.GetObject(pathCurveIds[0], OpenMode.ForRead) as Curve;
                            if (seedC != null)
                            {
                                bool seedNn;
                                traceMeta.SeedWireColorKey = ResolveEntityEffectiveColorKey(seedC, layerColorMap, out seedNn);
                            }
                        }

                        double refSpan = (learnedProfile != null && learnedProfile.MedianFixtureSpan > 1e-3)
                            ? learnedProfile.MedianFixtureSpan
                            : medianFixtureSpan;
                        double chainSnapTol = explicitTolerance.HasValue
                            ? Math.Max(1e-3, explicitTolerance.Value)
                            : (refSpan > 1e-3
                                ? Math.Max(1e-3, refSpan * 0.12)
                                : Math.Max(1e-3, 0.05 * unitsPerM));

                        HashSet<ObjectId> touchedTermSet = new HashSet<ObjectId>();
                        if (followConnected)
                        {
                            List<ObjectId> termIds;
                            bool guardTriggered;
                            bridgedBlockCount = ExpandConnectedPathCurves(
                                db, tr, pathCurveIds, pathHandles, layerHiddenFlags, visibleOnly, chainSnapTol,
                                bridgeThroughBlocks, connectGaps, strictColor, strictLayer,
                                unitsPerM, refSpan, drawingSpan,
                                includeContainerBlocks, excludeBlocks, blockFilter, learnedProfile,
                                maxCircuitBlocks, null,
                                out bridgedBlockNames, out termIds, out guardTriggered);
                            traceMeta.CircuitGuardTriggered = guardTriggered;
                            if (termIds != null)
                            {
                                for (int ti = 0; ti < termIds.Count; ti++) touchedTermSet.Add(termIds[ti]);
                            }
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
                            tr, ms, layerHiddenFlags, pathCandidates, pathIdSet, touchedTermSet,
                            visibleOnly, includeAnonymous, includeSystem, includeContainerBlocks, includeTerminatorBlocks, connectGaps,
                            layerFilter, blockFilter, excludeBlocks, explicitTolerance,
                            unitsPerM, medianFixtureSpan, drawingSpan, learnedProfile,
                            matchedItems, countsByBlock, traceMeta.Terminators, ref selectedAndPathExt, out containerBlocksSkipped);

                        double tightTipTol = refSpan > 1e-3 ? Math.Max(1e-3, refSpan * 0.12) : Math.Max(1e-3, 0.06 * unitsPerM);
                        List<Point2d> openTips = CollectOpenWireTips(tr, pathCurveIds, tightTipTol);
                        double tagSearchRadius = refSpan > 1e-3
                            ? (refSpan * 4.5)
                            : Math.Max(2.5 * unitsPerM, 2.5);
                        CollectNearbyCircuitTags(
                            tr, ms, layerHiddenFlags, visibleOnly, tagSearchRadius,
                            traceMeta.Terminators, openTips,
                            out traceMeta.CircuitName, out traceMeta.PanelName, out traceMeta.TerminationType);

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
                    countsByBlock, nestedBlocksAlongPath, learnedProfile, traceMeta, selectedAndPathExt,
                    selectPaths, selectBlocks, drawLegend, zoom, zoomMode, title, legendPosition,
                    visibleOnly, includeSystem, includeAnonymous, switchedToModel);
            }
        }

        private static Dictionary<string, object> BuildAndFinalizeExplicitCircuit(
            Document doc,
            Database db,
            Dictionary<string, object> args,
            List<ObjectId> wireIds,
            List<ObjectId> explicitBlockIds,
            bool matchBlocksOnWiresIfEmpty,
            string circuitNameOverride,
            string panelNameOverride,
            CircuitSampleProfile learnedProfile,
            double unitsPerM,
            bool scaleInferred,
            string scaleWarning,
            string insunitsName,
            double medianFixtureSpan,
            double drawingSpan,
            bool visibleOnly,
            bool includeAnonymous,
            bool includeSystem,
            bool includeContainerBlocks,
            bool includeTerminatorBlocks,
            bool connectGaps,
            bool selectBlocks,
            bool selectPaths,
            bool drawLegend,
            bool zoom,
            string zoomMode,
            string title,
            bool hasCustomTitle,
            string legendPosition,
            string layerFilter,
            string blockFilter,
            List<string> excludeBlocks,
            double? explicitTolerance,
            bool fromSavedMemory,
            bool userLocked,
            bool savedToMemory,
            bool mergedWithSaved)
        {
            List<ObjectId> dedupWireIds = new List<ObjectId>();
            HashSet<ObjectId> wireSet = new HashSet<ObjectId>();
            List<string> pathHandles = new List<string>();
            if (wireIds != null)
            {
                for (int i = 0; i < wireIds.Count; i++)
                {
                    ObjectId id = wireIds[i];
                    if (!id.IsNull && !id.IsErased && wireSet.Add(id))
                        dedupWireIds.Add(id);
                }
            }

            List<ObjectId> dedupBlkIds = new List<ObjectId>();
            HashSet<ObjectId> blkSet = new HashSet<ObjectId>();
            if (explicitBlockIds != null)
            {
                for (int i = 0; i < explicitBlockIds.Count; i++)
                {
                    ObjectId id = explicitBlockIds[i];
                    if (!id.IsNull && !id.IsErased && !wireSet.Contains(id) && blkSet.Add(id))
                        dedupBlkIds.Add(id);
                }
            }

            List<PathCurveCandidate> pathCandidates = new List<PathCurveCandidate>();
            List<PathBlockMatchItem> matchedItems = new List<PathBlockMatchItem>();
            Dictionary<string, int> countsByBlock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> nestedBlocksAlongPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> pathEntityTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double totalPathLenUnits = 0.0;
            Extents3d? selectedAndPathExt = null;
            bool switchedToModel = false;
            int containerBlocksSkipped = 0;

            CircuitTraceMeta traceMeta = new CircuitTraceMeta
            {
                CircuitName = circuitNameOverride ?? "",
                PanelName = panelNameOverride ?? "",
                HasCustomTitle = hasCustomTitle,
                InsunitsName = insunitsName ?? "",
                UnitScaleInferred = scaleInferred,
                EffectiveUnitsPerM = unitsPerM,
                UnitScaleWarning = scaleWarning ?? "",
                FromSavedMemory = fromSavedMemory,
                UserLocked = userLocked,
                SavedToMemory = savedToMemory,
                MergedWithSaved = mergedWithSaved
            };
            if (string.IsNullOrWhiteSpace(traceMeta.PanelName) && !string.IsNullOrWhiteSpace(traceMeta.CircuitName))
            {
                int sepIdx = traceMeta.CircuitName.LastIndexOfAny(new char[] { '/', '\\' });
                if (sepIdx > 0) traceMeta.PanelName = traceMeta.CircuitName.Substring(0, sepIdx).Trim();
            }

            if ((selectBlocks || selectPaths || zoom) && !db.TileMode)
            {
                try { LayoutManager.Current.CurrentLayout = "Model"; switchedToModel = true; } catch { }
            }

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                    Dictionary<string, SavedColorState> layerColorMap = BuildLayerColorStateMap(db, tr);

                    List<ObjectId> liveWireIds = new List<ObjectId>();
                    for (int wi = 0; wi < dedupWireIds.Count; wi++)
                    {
                        Curve cv = tr.GetObject(dedupWireIds[wi], OpenMode.ForRead) as Curve;
                        if (cv == null || cv.IsErased) continue;
                        liveWireIds.Add(dedupWireIds[wi]);
                        pathHandles.Add(cv.Handle.ToString());
                        if (string.IsNullOrEmpty(traceMeta.SeedWireColorKey))
                        {
                            bool nn;
                            traceMeta.SeedWireColorKey = ResolveEntityEffectiveColorKey(cv, layerColorMap, out nn);
                        }
                    }
                    dedupWireIds = liveWireIds;

                    CollectCircuitPathCandidates(tr, dedupWireIds, pathCandidates, pathEntityTypes, ref totalPathLenUnits, ref selectedAndPathExt);

                    double refSpan = (learnedProfile != null && learnedProfile.MedianFixtureSpan > 1e-3)
                        ? learnedProfile.MedianFixtureSpan
                        : medianFixtureSpan;

                    if (dedupBlkIds.Count > 0)
                    {
                        for (int bi = 0; bi < dedupBlkIds.Count; bi++)
                        {
                            ObjectId bid = dedupBlkIds[bi];
                            BlockReference br = tr.GetObject(bid, OpenMode.ForRead) as BlockReference;
                            if (br == null || br.IsErased) continue;
                            if (IsLegendOrHighlightLayer(br.Layer) || IsAiAnnotationLayer(br.Layer)) continue;
                            string bn = ResolveBlockName(tr, br);
                            if (string.IsNullOrEmpty(bn)) continue;
                            if (!includeAnonymous && IsAnonymousBlockName(bn)) continue;
                            if (!includeSystem && (IsSystemBlockName(bn) || string.Equals(br.Layer, "ASHADE", StringComparison.OrdinalIgnoreCase))) continue;
                            if (!IsBlockNameAllowedForTrace(bn, blockFilter, excludeBlocks)) continue;
                            if (!includeTerminatorBlocks && IsCircuitTerminatorArrowBlock(bn)) continue;

                            Extents3d bex;
                            try { bex = br.GeometricExtents; } catch { continue; }
                            if (!IsFiniteBounds(bex)) continue;

                            double hitDist = 0.0;
                            int hitIdx = bi;
                            if (pathCandidates.Count > 0)
                            {
                                double bSpan = Math.Max(bex.MaxPoint.X - bex.MinPoint.X, bex.MaxPoint.Y - bex.MinPoint.Y);
                                double tol = explicitTolerance.HasValue ? Math.Max(1e-5, explicitTolerance.Value) : Math.Max(1e-3, bSpan * 0.5);
                                int hp; double hd;
                                if (DoesPathHitBlockXY(pathCandidates, br, bex, tol, unitsPerM, out hp, out hd))
                                {
                                    hitIdx = hp;
                                    hitDist = hd;
                                }
                            }

                            matchedItems.Add(new PathBlockMatchItem
                            {
                                Id = bid,
                                Handle = br.Handle.ToString(),
                                BlockName = bn,
                                Layer = br.Layer ?? "0",
                                ColorIndex = br.ColorIndex,
                                X = (bex.MinPoint.X + bex.MaxPoint.X) * 0.5,
                                Y = (bex.MinPoint.Y + bex.MaxPoint.Y) * 0.5,
                                PathIndex = hitIdx,
                                DistanceAlongPath = hitDist,
                                Bounds = bex
                            });
                            countsByBlock[bn] = countsByBlock.ContainsKey(bn) ? countsByBlock[bn] + 1 : 1;
                            if (!selectedAndPathExt.HasValue) selectedAndPathExt = bex;
                            else { Extents3d c = selectedAndPathExt.Value; c.AddExtents(bex); selectedAndPathExt = c; }
                        }
                    }
                    else if (matchBlocksOnWiresIfEmpty && pathCandidates.Count > 0)
                    {
                        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                        MatchBlocksAlongCircuitPath(
                            tr, ms, layerHiddenFlags, pathCandidates, new HashSet<ObjectId>(dedupWireIds), new HashSet<ObjectId>(),
                            visibleOnly, includeAnonymous, includeSystem, includeContainerBlocks, includeTerminatorBlocks, connectGaps,
                            layerFilter, blockFilter, excludeBlocks, explicitTolerance,
                            unitsPerM, medianFixtureSpan, drawingSpan, learnedProfile,
                            matchedItems, countsByBlock, traceMeta.Terminators, ref selectedAndPathExt, out containerBlocksSkipped);
                    }

                    if (dedupWireIds.Count > 0)
                    {
                        BlockTableRecord msTag = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                        double tightTipTol = refSpan > 1e-3 ? Math.Max(1e-3, refSpan * 0.12) : Math.Max(1e-3, 0.06 * unitsPerM);
                        List<Point2d> openTips = CollectOpenWireTips(tr, dedupWireIds, tightTipTol);
                        double tagSearchRadius = refSpan > 1e-3 ? (refSpan * 4.5) : Math.Max(2.5 * unitsPerM, 2.5);
                        string foundCName, foundPName, foundTerm;
                        CollectNearbyCircuitTags(
                            tr, msTag, layerHiddenFlags, visibleOnly, tagSearchRadius,
                            traceMeta.Terminators, openTips,
                            out foundCName, out foundPName, out foundTerm);
                        if (string.IsNullOrWhiteSpace(traceMeta.CircuitName) && !string.IsNullOrWhiteSpace(foundCName))
                            traceMeta.CircuitName = foundCName;
                        if (string.IsNullOrWhiteSpace(traceMeta.PanelName) && !string.IsNullOrWhiteSpace(foundPName))
                            traceMeta.PanelName = foundPName;
                        if (!string.IsNullOrWhiteSpace(foundTerm) && foundTerm != "open_end")
                            traceMeta.TerminationType = foundTerm;
                    }

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

            List<string> initHandles = new List<string>(pathHandles);
            return FinalizeCircuitPathOutput(
                doc, args, dedupWireIds, pathHandles, initHandles,
                0, new List<string>(), containerBlocksSkipped,
                pathEntityTypes, totalPathLenUnits, unitsPerM, matchedItems,
                countsByBlock, nestedBlocksAlongPath, learnedProfile, traceMeta, selectedAndPathExt,
                selectPaths, selectBlocks, drawLegend, zoom, zoomMode, title, legendPosition,
                visibleOnly, includeSystem, includeAnonymous, switchedToModel);
        }

        private static Dictionary<string, object> FindBlocksAlongPath(Document doc, Dictionary<string, object> args)
        {
            if (args == null) args = new Dictionary<string, object>();
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
            bool drawLegend = GetBool(args, "draw_legend", true);
            string requestedCircuitName = (GetStr(args, "circuit_name") ?? "").Trim();
            bool mergeWithSaved = GetBool(args, "merge_with_saved", false);
            bool lockSelectionAsCircuit = GetBool(args, "lock_selection_as_circuit", false);
            bool hasCircuitIntent = !string.IsNullOrWhiteSpace(requestedCircuitName) || mergeWithSaved || lockSelectionAsCircuit;
            bool selectPaths = GetBool(args, "select_paths", hasCircuitIntent);
            bool usePreviousPath = GetBool(args, "use_previous_path", false);
            bool pickIfEmpty = GetBool(args, "pick_if_empty", true);
            bool followConnected = GetBool(args, "follow_connected", true);
            bool bridgeThroughBlocks = GetBool(args, "bridge_through_blocks", true);
            bool connectGaps = GetBool(args, "connect_gaps", true);
            bool strictColor = GetBool(args, "strict_color", true);
            bool strictLayer = GetBool(args, "strict_layer", false);
            bool learnSample = GetBool(args, "learn_sample", false);
            bool resetSample = GetBool(args, "reset_sample", false);
            int maxCircuitBlocks = GetInt(args, "max_circuit_blocks", 35);
            if (maxCircuitBlocks < 0) maxCircuitBlocks = 0;
            bool includeContainerBlocks = GetBool(args, "include_container_blocks", false);
            bool includeTerminatorBlocks = GetBool(args, "include_terminator_blocks", false);
            bool zoom = GetBool(args, "zoom", true);
            string zoomMode = (args.ContainsKey("zoom_mode") ? (GetStr(args, "zoom_mode") ?? "auto") : (hasCircuitIntent && !drawLegend ? "selected" : "auto")).Trim().ToLowerInvariant();
            string rawTitle = GetStr(args, "title");
            bool hasCustomTitle = !string.IsNullOrWhiteSpace(rawTitle);
            string title = hasCustomTitle ? rawTitle.Trim() : "جدول حصر ومفاتيح بلوكات المسار المحدد";
            string legendPosition = GetStr(args, "position") ?? "outside_right";

            if (!selectBlocks && !drawLegend && !args.ContainsKey("select_paths"))
                selectPaths = true;

            Dictionary<string, object> unitsInfo = GetUnitsInfo(db);
            double rawUnitsPerM = Convert.ToDouble(unitsInfo["units_per_m"], CultureInfo.InvariantCulture);
            if (rawUnitsPerM <= 1e-9) rawUnitsPerM = 1000.0;
            string insunitsName = unitsInfo.ContainsKey("name") && unitsInfo["name"] != null
                ? unitsInfo["name"].ToString()
                : "";

            List<string> candidateHandles = explicitHandles.Count > 0
                ? new List<string>(explicitHandles)
                : GetSelectedHandleStrings(doc);

            List<ObjectId> pathCurveIds = new List<ObjectId>();
            List<string> pathHandles = new List<string>();
            List<ObjectId> selectedBlockIds = new List<ObjectId>();
            CircuitSampleProfile learnedProfile = null;
            lock (s_lastCoordLock)
            {
                if (resetSample) s_circuitProfileByDb.Remove(db.UnmanagedObject);
                else s_circuitProfileByDb.TryGetValue(db.UnmanagedObject, out learnedProfile);
            }

            double drawingSpan = 0.0;
            double medianFixtureSpan = 0.0;
            int skippedAiLayerCount = 0;

            using (DocumentLock docLock = doc.LockDocument())
            {
                bool selectionHadOnlyBlocks = false;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ScanCircuitSeedEntities(
                        db, tr, candidateHandles, visibleOnly, rawUnitsPerM, usePreviousPath, excludeBlocks, blockFilter, learnSample,
                        ref learnedProfile, out drawingSpan, out medianFixtureSpan, out selectionHadOnlyBlocks, out skippedAiLayerCount,
                        pathCurveIds, pathHandles, selectedBlockIds);
                    tr.Commit();
                }

                bool scaleInferred;
                string scaleWarning;
                double effectiveUnitsPerM = ResolveEffectiveUnitsPerMeter(
                    db, rawUnitsPerM, drawingSpan, medianFixtureSpan, out scaleInferred, out scaleWarning);

                // Case 1: Lock user-selected non-AI wires & blocks as exact circuit definition (e.g. after deselecting unwanted blocks)
                if (lockSelectionAsCircuit)
                {
                    if (pathCurveIds.Count == 0 && selectedBlockIds.Count == 0)
                        return Err("لتثبيت مكونات اللينية وحذف ما ألغيته، حدد الأسلاك و/أو البلوكات المطلوب الإبقاء عليها في أوتوكاد أولاً.");
                    string targetName = !string.IsNullOrWhiteSpace(requestedCircuitName) ? requestedCircuitName : GetLastActiveCircuitName(doc);
                    return BuildAndFinalizeExplicitCircuit(
                        doc, db, args, pathCurveIds, selectedBlockIds, selectedBlockIds.Count == 0,
                        targetName, "", learnedProfile, effectiveUnitsPerM, scaleInferred, scaleWarning, insunitsName,
                        medianFixtureSpan, drawingSpan, visibleOnly, includeAnonymous, includeSystem,
                        includeContainerBlocks, includeTerminatorBlocks, connectGaps,
                        selectBlocks, selectPaths, drawLegend, zoom, zoomMode, title, hasCustomTitle, legendPosition,
                        layerFilter, blockFilter, excludeBlocks, explicitTolerance,
                        false, true, true, false);
                }

                // Case 2: Merge newly selected/traced branch onto saved circuit memory
                if (mergeWithSaved)
                {
                    string targetName = !string.IsNullOrWhiteSpace(requestedCircuitName) ? requestedCircuitName : GetLastActiveCircuitName(doc);
                    SavedCircuitRecord savedRec;
                    TryGetSavedCircuitRecord(doc, targetName, out savedRec);
                    if (savedRec == null && pathCurveIds.Count == 0 && selectedBlockIds.Count == 0)
                        return Err("لم يتم العثور على لينية محفوظة مسبقاً ولا عناصر محددة للإضافة.");

                    List<ObjectId> combinedWires = new List<ObjectId>();
                    List<ObjectId> combinedBlocks = new List<ObjectId>();
                    HashSet<ObjectId> seenW = new HashSet<ObjectId>();
                    HashSet<ObjectId> seenB = new HashSet<ObjectId>();

                    using (Transaction trMerge = db.TransactionManager.StartTransaction())
                    {
                        if (savedRec != null)
                        {
                            for (int i = 0; i < savedRec.PathHandles.Count; i++)
                            {
                                ObjectId oid = ResolveHandle(db, savedRec.PathHandles[i]);
                                if (!oid.IsNull && !oid.IsErased && seenW.Add(oid)) combinedWires.Add(oid);
                            }
                            for (int i = 0; i < savedRec.MatchedBlockHandles.Count; i++)
                            {
                                ObjectId oid = ResolveHandle(db, savedRec.MatchedBlockHandles[i]);
                                if (!oid.IsNull && !oid.IsErased && seenB.Add(oid)) combinedBlocks.Add(oid);
                            }
                        }

                        if (pathCurveIds.Count > 0 && followConnected && selectedBlockIds.Count == 0)
                        {
                            List<ObjectId> extraWires = new List<ObjectId>(pathCurveIds);
                            List<string> extraHandles = new List<string>(pathHandles);
                            Dictionary<string, bool> hiddenFlags = BuildLayerHiddenMap(db, trMerge);
                            double refSp = (learnedProfile != null && learnedProfile.MedianFixtureSpan > 1e-3) ? learnedProfile.MedianFixtureSpan : medianFixtureSpan;
                            double snapTolM = explicitTolerance.HasValue ? Math.Max(1e-3, explicitTolerance.Value) : (refSp > 1e-3 ? Math.Max(1e-3, refSp * 0.12) : Math.Max(1e-3, 0.05 * effectiveUnitsPerM));
                            List<string> bNames; List<ObjectId> tIds; bool gTrig;
                            ExpandConnectedPathCurves(
                                db, trMerge, extraWires, extraHandles, hiddenFlags, visibleOnly, snapTolM,
                                bridgeThroughBlocks, connectGaps, strictColor, strictLayer,
                                effectiveUnitsPerM, refSp, drawingSpan,
                                includeContainerBlocks, excludeBlocks, blockFilter, learnedProfile,
                                maxCircuitBlocks, null, out bNames, out tIds, out gTrig);
                            pathCurveIds = extraWires;
                        }

                        for (int i = 0; i < pathCurveIds.Count; i++)
                        {
                            if (!pathCurveIds[i].IsNull && !pathCurveIds[i].IsErased && seenW.Add(pathCurveIds[i]))
                                combinedWires.Add(pathCurveIds[i]);
                        }
                        for (int i = 0; i < selectedBlockIds.Count; i++)
                        {
                            if (!selectedBlockIds[i].IsNull && !selectedBlockIds[i].IsErased && seenB.Add(selectedBlockIds[i]))
                                combinedBlocks.Add(selectedBlockIds[i]);
                        }

                        if (pathCurveIds.Count > 0 && selectedBlockIds.Count == 0)
                        {
                            List<PathCurveCandidate> newCands = new List<PathCurveCandidate>();
                            HashSet<string> pTypes = new HashSet<string>();
                            double dummyLen = 0.0;
                            Extents3d? dummyExt = null;
                            try
                            {
                                CollectCircuitPathCandidates(trMerge, pathCurveIds, newCands, pTypes, ref dummyLen, ref dummyExt);
                                if (newCands.Count > 0)
                                {
                                    BlockTableRecord msM = (BlockTableRecord)trMerge.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                                    Dictionary<string, bool> hiddenM = BuildLayerHiddenMap(db, trMerge);
                                    List<PathBlockMatchItem> newMatched = new List<PathBlockMatchItem>();
                                    Dictionary<string, int> newCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                    List<CircuitTerminatorInfo> newTerms = new List<CircuitTerminatorInfo>();
                                    int cSkip;
                                    MatchBlocksAlongCircuitPath(
                                        trMerge, msM, hiddenM, newCands, new HashSet<ObjectId>(pathCurveIds), new HashSet<ObjectId>(),
                                        visibleOnly, includeAnonymous, includeSystem, includeContainerBlocks, includeTerminatorBlocks, connectGaps,
                                        layerFilter, blockFilter, excludeBlocks, explicitTolerance,
                                        effectiveUnitsPerM, medianFixtureSpan, drawingSpan, learnedProfile,
                                        newMatched, newCounts, newTerms, ref dummyExt, out cSkip);
                                    for (int mi = 0; mi < newMatched.Count; mi++)
                                    {
                                        if (seenB.Add(newMatched[mi].Id)) combinedBlocks.Add(newMatched[mi].Id);
                                    }
                                }
                            }
                            finally
                            {
                                for (int ci = 0; ci < newCands.Count; ci++) { try { if (newCands[ci].FlatCurve != null) newCands[ci].FlatCurve.Dispose(); } catch { } }
                            }
                        }
                        trMerge.Commit();
                    }

                    string resolvedPanel = savedRec != null ? savedRec.PanelName : "";
                    string resolvedCircName = savedRec != null && !string.IsNullOrWhiteSpace(savedRec.CircuitName) ? savedRec.CircuitName : targetName;
                    return BuildAndFinalizeExplicitCircuit(
                        doc, db, args, combinedWires, combinedBlocks, combinedBlocks.Count == 0,
                        resolvedCircName, resolvedPanel, learnedProfile, effectiveUnitsPerM, scaleInferred, scaleWarning, insunitsName,
                        medianFixtureSpan, drawingSpan, visibleOnly, includeAnonymous, includeSystem,
                        includeContainerBlocks, includeTerminatorBlocks, connectGaps,
                        selectBlocks, selectPaths, drawLegend, zoom, zoomMode, title, hasCustomTitle, legendPosition,
                        layerFilter, blockFilter, excludeBlocks, explicitTolerance,
                        false, true, true, true);
                }

                // Case 3: Recall or find circuit directly by circuit_name (ignoring accidentally selected AI table entities!)
                if (!string.IsNullOrWhiteSpace(requestedCircuitName))
                {
                    SavedCircuitRecord savedRec;
                    if (TryGetSavedCircuitRecord(doc, requestedCircuitName, out savedRec))
                    {
                        List<ObjectId> savedWires = new List<ObjectId>();
                        List<ObjectId> savedBlocks = new List<ObjectId>();
                        using (Transaction trRec = db.TransactionManager.StartTransaction())
                        {
                            for (int i = 0; i < savedRec.PathHandles.Count; i++)
                            {
                                ObjectId oid = ResolveHandle(db, savedRec.PathHandles[i]);
                                if (!oid.IsNull && !oid.IsErased) savedWires.Add(oid);
                            }
                            for (int i = 0; i < savedRec.MatchedBlockHandles.Count; i++)
                            {
                                ObjectId oid = ResolveHandle(db, savedRec.MatchedBlockHandles[i]);
                                if (!oid.IsNull && !oid.IsErased) savedBlocks.Add(oid);
                            }
                            trRec.Commit();
                        }
                        if (savedWires.Count > 0 || savedBlocks.Count > 0)
                        {
                            return BuildAndFinalizeExplicitCircuit(
                                doc, db, args, savedWires, savedBlocks, savedBlocks.Count == 0,
                                savedRec.CircuitName, savedRec.PanelName, learnedProfile, effectiveUnitsPerM, scaleInferred, scaleWarning, insunitsName,
                                medianFixtureSpan, drawingSpan, visibleOnly, includeAnonymous, includeSystem,
                                includeContainerBlocks, includeTerminatorBlocks, connectGaps,
                                selectBlocks, selectPaths, drawLegend, zoom, zoomMode, title, hasCustomTitle, legendPosition,
                                layerFilter, blockFilter, excludeBlocks, explicitTolerance,
                                true, savedRec.UserLocked, true, false);
                        }
                    }

                    // Not yet in memory: locate wire seed next to tag in ModelSpace
                    using (Transaction trFindTag = db.TransactionManager.StartTransaction())
                    {
                        Dictionary<string, bool> hiddenT = BuildLayerHiddenMap(db, trFindTag);
                        double refSp = (learnedProfile != null && learnedProfile.MedianFixtureSpan > 1e-3) ? learnedProfile.MedianFixtureSpan : medianFixtureSpan;
                        List<ObjectId> tagSeeds = FindCircuitSeedCurveByTagName(db, trFindTag, requestedCircuitName, hiddenT, visibleOnly, refSp, effectiveUnitsPerM);
                        for (int i = 0; i < tagSeeds.Count; i++)
                        {
                            if (!pathCurveIds.Contains(tagSeeds[i]))
                            {
                                Entity e = trFindTag.GetObject(tagSeeds[i], OpenMode.ForRead) as Entity;
                                if (e != null)
                                {
                                    pathCurveIds.Add(tagSeeds[i]);
                                    pathHandles.Add(e.Handle.ToString());
                                }
                            }
                        }
                        trFindTag.Commit();
                    }
                    if (pathCurveIds.Count == 0)
                        return Err("لم يتم العثور على اللينية '" + requestedCircuitName + "' في الذاكرة المحفوظة ولا بجانب نصوص المخطط. جرّب تشغيل trace_all_circuits أو حدد سلكاً منها.");
                }

                if (pathCurveIds.Count == 0 && skippedAiLayerCount > 0)
                {
                    return Err("العنصر المحدد في أوتوكاد ينتمي إلى جدول/طبقة التصدير (_AI_...) وليس إلى أسلاك المخطط. حدد سلكاً من مسار اللينية أو مرر اسم اللينية (مثل ELP-B3.2/1) في circuit_name.");
                }

                if (pathCurveIds.Count == 0 && pickIfEmpty)
                {
                    string pickErr = PromptSingleCurveSeedIfEmpty(doc, db, pathCurveIds, pathHandles);
                    if (!string.IsNullOrEmpty(pickErr)) return Err(pickErr);
                }

                if (pathCurveIds.Count == 0)
                {
                    List<string> prevCurves = GetValidCachedUserCurveHandles(db);
                    if (prevCurves.Count > 0)
                        return Err("لم يتم تحديد أي خط أو مسار (Line/Polyline) حالياً، لكن يوجد مسار سابق محفوظ (" + string.Join(",", prevCurves.ToArray()) + "). مرر use_previous_path=true أو حدد الخط في أوتوكاد.");
                    return Err("لم يتم تحديد أي خط أو مسار (Line/Polyline/Arc) في أوتوكاد. حدد الخط/الكابل أولاً ثم أعد المحاولة.");
                }

                // Strict sample lock: when teaching a full circuit sample, keep ONLY what user selected cleanly without expanding
                bool effectiveFollowConnected = followConnected;
                if (learnSample && (pathCurveIds.Count >= 2 || (pathCurveIds.Count >= 1 && selectedBlockIds.Count >= 2)))
                    effectiveFollowConnected = false;

                RecordUserSelectionSnapshot(doc, pathHandles);
                List<string> initialPathHandles = new List<string>(pathHandles);

                return ExecuteCircuitTracePass(
                    doc, db, args, pathCurveIds, pathHandles, initialPathHandles,
                    learnedProfile, effectiveUnitsPerM, scaleInferred, scaleWarning, insunitsName, medianFixtureSpan, drawingSpan,
                    visibleOnly, includeAnonymous, includeSystem, includeContainerBlocks, includeTerminatorBlocks,
                    selectBlocks, selectPaths, drawLegend, effectiveFollowConnected, bridgeThroughBlocks,
                    connectGaps, strictColor, strictLayer, learnSample, maxCircuitBlocks,
                    zoom, zoomMode, title, hasCustomTitle, legendPosition,
                    layerFilter, blockFilter, excludeBlocks, explicitTolerance);
            }
        }

        // ==================== Batch All-Circuits Partition & Schedule (`trace_all_circuits`) ====================

        private static bool IsCandidateCircuitLayerName(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName)) return false;
            if (IsLegendOrHighlightLayer(layerName) || IsAiAnnotationLayer(layerName) || IsArchitecturalLayerName(layerName))
                return false;
            string u = layerName.Trim().ToUpperInvariant();
            return u.Contains("LTG") || u.Contains("LIGHT") || u.Contains("WIRE") || u.Contains("CIRC") ||
                   u.Contains("ELEC") || u.Contains("POWER") || u.Contains("SOCK") || u.Contains("RECEPT") ||
                   u.Contains("HOME") || u.Contains("EMERG");
        }

        private static void ComputeDrawingFixtureMetrics(
            Database db,
            Transaction tr,
            bool visibleOnly,
            Dictionary<string, bool> hiddenMap,
            out double drawingSpan,
            out double medianFixtureSpan,
            out Extents3d? mainExt)
        {
            drawingSpan = 0.0;
            medianFixtureSpan = 0.0;
            mainExt = null;
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            List<double> rawSpans = new List<double>();
            foreach (ObjectId mid in ms)
            {
                if (mid.IsErased) continue;
                Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                if (me == null || me.IsErased || IsLegendOrHighlightLayer(me.Layer) || IsAiAnnotationLayer(me.Layer)) continue;
                if (visibleOnly && !IsEntityVisible(me, hiddenMap)) continue;
                Extents3d mex;
                try { mex = me.GeometricExtents; } catch { continue; }
                if (!IsFiniteBounds(mex)) continue;
                if (!mainExt.HasValue) mainExt = mex;
                else { Extents3d u = mainExt.Value; u.AddExtents(mex); mainExt = u; }

                BlockReference mbr = me as BlockReference;
                if (mbr != null)
                {
                    string bn = ResolveBlockName(tr, mbr);
                    if (!string.IsNullOrEmpty(bn) && !IsAnonymousBlockName(bn) && !IsSystemBlockName(bn) &&
                        !IsPanelOrFrameBlockName(bn) && !IsCircuitTerminatorArrowBlock(bn) && !IsPanelDistributionBlockName(bn))
                    {
                        double sp = Math.Max(mex.MaxPoint.X - mex.MinPoint.X, mex.MaxPoint.Y - mex.MinPoint.Y);
                        if (sp > 1e-4) rawSpans.Add(sp);
                    }
                }
            }
            if (mainExt.HasValue)
                drawingSpan = Math.Max(mainExt.Value.MaxPoint.X - mainExt.Value.MinPoint.X, mainExt.Value.MaxPoint.Y - mainExt.Value.MinPoint.Y);
            List<double> smallSpans = new List<double>();
            for (int i = 0; i < rawSpans.Count; i++)
            {
                if (drawingSpan <= 1e-3 || rawSpans[i] < drawingSpan * 0.08) smallSpans.Add(rawSpans[i]);
            }
            if (smallSpans.Count > 0)
            {
                smallSpans.Sort();
                medianFixtureSpan = smallSpans[smallSpans.Count / 2];
            }
        }

        private static List<ObjectId> CollectAllCircuitCandidateCurveIds(
            Database db,
            Transaction tr,
            Dictionary<string, bool> hiddenMap,
            Dictionary<string, SavedColorState> colorMap,
            bool visibleOnly,
            string layerFilter,
            string colorFilter,
            List<string> selectedHandles,
            CircuitSampleProfile learnedProfile)
        {
            HashSet<string> seedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selectedHandles != null && selectedHandles.Count > 0)
            {
                for (int i = 0; i < selectedHandles.Count; i++)
                {
                    ObjectId sid = ResolveHandle(db, selectedHandles[i]);
                    if (sid.IsNull || sid.IsErased) continue;
                    Curve scv = tr.GetObject(sid, OpenMode.ForRead) as Curve;
                    if (scv != null && !IsAiAnnotationLayer(scv.Layer)) seedLayers.Add(scv.Layer ?? "0");
                }
            }
            if (seedLayers.Count == 0 && learnedProfile != null && learnedProfile.CircuitLayers.Count > 0)
            {
                foreach (string lyr in learnedProfile.CircuitLayers)
                {
                    if (!string.IsNullOrWhiteSpace(lyr)) seedLayers.Add(lyr);
                }
            }

            List<ObjectId> explicitList = new List<ObjectId>();
            List<ObjectId> namedMepList = new List<ObjectId>();
            List<ObjectId> coloredZeroList = new List<ObjectId>();
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

            foreach (ObjectId mid in ms)
            {
                if (mid.IsErased) continue;
                Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                Curve cv = ent as Curve;
                if (cv == null || ent.IsErased || IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                if (visibleOnly && !IsEntityVisible(ent, hiddenMap)) continue;
                Point2d sp, ep;
                if (!TryGetCurveEndpointsXY(cv, out sp, out ep)) continue;

                string lyr = ent.Layer ?? "0";
                bool nn;
                string cKey = ResolveEntityEffectiveColorKey(ent, colorMap, out nn);
                if (!string.IsNullOrWhiteSpace(colorFilter) &&
                    !string.Equals(cKey, colorFilter.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    !MatchesPatternOrSubstring(cKey, colorFilter))
                    continue;

                if (!string.IsNullOrWhiteSpace(layerFilter))
                {
                    if (MatchesPatternOrSubstring(lyr, layerFilter)) explicitList.Add(mid);
                    continue;
                }
                if (seedLayers.Count > 0)
                {
                    if (seedLayers.Contains(lyr)) explicitList.Add(mid);
                    continue;
                }
                if (IsCandidateCircuitLayerName(lyr))
                    namedMepList.Add(mid);
                else if (nn && !IsArchitecturalLayerName(lyr))
                    coloredZeroList.Add(mid);
            }

            if (!string.IsNullOrWhiteSpace(layerFilter) || seedLayers.Count > 0) return explicitList;
            if (namedMepList.Count > 0) return namedMepList;
            return coloredZeroList;
        }

        private static bool IsBlockNameAllowedForTrace(
            string bn,
            string blockFilter,
            List<string> excludeBlocks)
        {
            if (string.IsNullOrEmpty(bn)) return false;
            if (!string.IsNullOrWhiteSpace(blockFilter) &&
                !string.Equals(bn, blockFilter.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !MatchesPatternOrSubstring(bn, blockFilter))
                return false;
            if (excludeBlocks != null)
            {
                for (int ei = 0; ei < excludeBlocks.Count; ei++)
                {
                    string exPat = excludeBlocks[ei];
                    if (string.IsNullOrWhiteSpace(exPat)) continue;
                    if (string.Equals(bn, exPat.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        MatchesPatternOrSubstring(bn, exPat))
                        return false;
                }
            }
            return true;
        }

        private static void MatchBlocksFromCacheAlongPath(
            DrawingBlockGeometryCache cache,
            List<PathCurveCandidate> pathCandidates,
            HashSet<ObjectId> pathIdSet,
            HashSet<ObjectId> touchedTerminatorBlockIds,
            bool includeContainerBlocks,
            bool includeTerminatorBlocks,
            bool connectGaps,
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
            List<CircuitTerminatorInfo> terminators,
            ref Extents3d? selectedAndPathExt,
            out int containerBlocksSkipped)
        {
            containerBlocksSkipped = 0;
            if (cache == null || pathCandidates == null || pathCandidates.Count == 0) return;

            double minX = pathCandidates[0].MinX;
            double minY = pathCandidates[0].MinY;
            double maxX = pathCandidates[0].MaxX;
            double maxY = pathCandidates[0].MaxY;
            for (int i = 1; i < pathCandidates.Count; i++)
            {
                if (pathCandidates[i].MinX < minX) minX = pathCandidates[i].MinX;
                if (pathCandidates[i].MinY < minY) minY = pathCandidates[i].MinY;
                if (pathCandidates[i].MaxX > maxX) maxX = pathCandidates[i].MaxX;
                if (pathCandidates[i].MaxY > maxY) maxY = pathCandidates[i].MaxY;
            }

            double refFixSpan = (learnedProfile != null && learnedProfile.MedianFixtureSpan > 1e-3)
                ? learnedProfile.MedianFixtureSpan
                : medianFixtureSpan;

            HashSet<ObjectId> matchedSet = new HashSet<ObjectId>();

            double termPad = Math.Max(1e-2, cache.MaxTerminatorSpan * 0.65);
            List<CachedBlockEntry> nearTerms = new List<CachedBlockEntry>();
            cache.TerminatorGrid.Query(minX - termPad, minY - termPad, maxX + termPad, maxY + termPad, nearTerms);
            for (int ti = 0; ti < nearTerms.Count; ti++)
            {
                CachedBlockEntry tb = nearTerms[ti];
                if (pathIdSet.Contains(tb.Id)) continue;
                if (!string.IsNullOrWhiteSpace(layerFilter) && !MatchesPatternOrSubstring(tb.Layer, layerFilter)) continue;

                double tTol = Math.Max(1e-3, tb.Span * 0.55);
                int hpT; double hdT;
                bool touched = (touchedTerminatorBlockIds != null && touchedTerminatorBlockIds.Contains(tb.Id)) ||
                    DoesPathHitBlockXY(pathCandidates, tb.Br, tb.Bounds, tTol, unitsPerM, out hpT, out hdT);
                if (!touched) continue;

                terminators.Add(new CircuitTerminatorInfo
                {
                    BlockId = tb.Id,
                    Handle = tb.Handle,
                    BlockName = tb.BlockName,
                    Kind = tb.IsPanelRoot ? "panel_connection" : "home_run_arrow",
                    Label = tb.AttributeText ?? "",
                    PanelName = tb.IsPanelRoot ? tb.BlockName : "",
                    X = tb.CenterX,
                    Y = tb.CenterY
                });

                if (tb.IsTerminator && !includeTerminatorBlocks) continue;
                if (!IsBlockNameAllowedForTrace(tb.BlockName, blockFilter, excludeBlocks)) continue;
                if (IsLikelyArchitecturalContainerBlock(tb.BlockName, tb.Span, refFixSpan, drawingSpan, unitsPerM, includeContainerBlocks, blockFilter))
                {
                    containerBlocksSkipped++;
                    continue;
                }

                double tolT = explicitTolerance.HasValue
                    ? Math.Max(1e-5, explicitTolerance.Value)
                    : Math.Max(1e-3, tb.Span * (connectGaps ? 0.36 : 0.18));
                int hitPathIdxT;
                double hitDistT;
                if (DoesPathHitBlockXY(pathCandidates, tb.Br, tb.Bounds, tolT, unitsPerM, out hitPathIdxT, out hitDistT) &&
                    matchedSet.Add(tb.Id))
                {
                    matchedItems.Add(new PathBlockMatchItem
                    {
                        Id = tb.Id,
                        Handle = tb.Handle,
                        BlockName = tb.BlockName,
                        Layer = tb.Layer,
                        ColorIndex = tb.ColorIndex,
                        X = tb.CenterX,
                        Y = tb.CenterY,
                        PathIndex = hitPathIdxT,
                        DistanceAlongPath = hitDistT,
                        Bounds = tb.Bounds
                    });
                    countsByBlock[tb.BlockName] = countsByBlock.ContainsKey(tb.BlockName) ? countsByBlock[tb.BlockName] + 1 : 1;
                    if (!selectedAndPathExt.HasValue) selectedAndPathExt = tb.Bounds;
                    else { Extents3d c = selectedAndPathExt.Value; c.AddExtents(tb.Bounds); selectedAndPathExt = c; }
                }
            }

            double fixPad = Math.Max(explicitTolerance.HasValue ? explicitTolerance.Value : 0.0, Math.Max(1e-2, cache.MaxFixtureSpan * 0.60));
            List<CachedBlockEntry> nearFix = new List<CachedBlockEntry>();
            cache.FixtureGrid.Query(minX - fixPad, minY - fixPad, maxX + fixPad, maxY + fixPad, nearFix);
            for (int fi = 0; fi < nearFix.Count; fi++)
            {
                CachedBlockEntry fb = nearFix[fi];
                if (pathIdSet.Contains(fb.Id) || matchedSet.Contains(fb.Id)) continue;
                if (!string.IsNullOrWhiteSpace(layerFilter) && !MatchesPatternOrSubstring(fb.Layer, layerFilter)) continue;
                if (!IsBlockNameAllowedForTrace(fb.BlockName, blockFilter, excludeBlocks)) continue;
                if (fb.IsContainer && !includeContainerBlocks)
                {
                    containerBlocksSkipped++;
                    continue;
                }
                if (IsLikelyArchitecturalContainerBlock(fb.BlockName, fb.Span, refFixSpan, drawingSpan, unitsPerM, includeContainerBlocks, blockFilter))
                {
                    containerBlocksSkipped++;
                    continue;
                }

                double tol = explicitTolerance.HasValue
                    ? Math.Max(1e-5, explicitTolerance.Value)
                    : Math.Max(1e-3, fb.Span * (connectGaps ? 0.36 : 0.18));

                int hitPathIdx;
                double hitDist;
                if (!DoesPathHitBlockXY(pathCandidates, fb.Br, fb.Bounds, tol, unitsPerM, out hitPathIdx, out hitDist))
                    continue;

                matchedSet.Add(fb.Id);
                matchedItems.Add(new PathBlockMatchItem
                {
                    Id = fb.Id,
                    Handle = fb.Handle,
                    BlockName = fb.BlockName,
                    Layer = fb.Layer,
                    ColorIndex = fb.ColorIndex,
                    X = fb.CenterX,
                    Y = fb.CenterY,
                    PathIndex = hitPathIdx,
                    DistanceAlongPath = hitDist,
                    Bounds = fb.Bounds
                });
                countsByBlock[fb.BlockName] = countsByBlock.ContainsKey(fb.BlockName) ? countsByBlock[fb.BlockName] + 1 : 1;

                if (!selectedAndPathExt.HasValue) selectedAndPathExt = fb.Bounds;
                else { Extents3d c = selectedAndPathExt.Value; c.AddExtents(fb.Bounds); selectedAndPathExt = c; }
            }
        }

        private static TracedCircuitSummary TraceSingleCircuitComponentCached(
            Database db,
            Transaction tr,
            DrawingBlockGeometryCache cache,
            Dictionary<ObjectId, CurveChainNode> curveNodesById,
            SpatialGrid2D<CurveChainNode> sharedCurveGrid,
            SpatialGrid2D<BridgeBlockCandidate> bridgeGrid,
            SpatialGrid2D<BridgeBlockCandidate> terminatorGrid,
            SpatialGrid2D<CrossingObstacleSegment> sharedObstacleGrid,
            double maxBridgeTol,
            double maxTermTol,
            Dictionary<string, SavedColorState> colorMap,
            CircuitSampleProfile learnedProfile,
            ObjectId seedId,
            HashSet<ObjectId> globalVisitedCurves,
            bool strictColor,
            bool strictLayer,
            bool connectGaps,
            bool bridgeThroughBlocks,
            int maxCircuitBlocks,
            double effectiveUnitsPerM,
            double refSpan,
            double drawingSpan)
        {
            Curve seedCv = tr.GetObject(seedId, OpenMode.ForRead) as Curve;
            if (seedCv == null || seedCv.IsErased) return null;

            CurveChainNode seedNode;
            if (!curveNodesById.TryGetValue(seedId, out seedNode) || seedNode == null)
                return null;

            List<ObjectId> compIds = new List<ObjectId> { seedId };
            List<string> compHandles = new List<string> { seedNode.Handle };
            double snapTol = refSpan > 1e-3 ? Math.Max(1e-3, refSpan * 0.12) : Math.Max(1e-3, 0.05 * effectiveUnitsPerM);

            Queue<CurveChainNode> queue = new Queue<CurveChainNode>();
            HashSet<ObjectId> compVisited = new HashSet<ObjectId>();
            compVisited.Add(seedId);
            queue.Enqueue(seedNode);

            CircuitChainScope scope = new CircuitChainScope
            {
                UseComponentLayerFilter = true,
                StrictColor = strictColor,
                StrictLayer = strictLayer,
                AnyNeutralSeed = !seedNode.IsNonNeutralColor,
                LearnedProfile = learnedProfile
            };
            scope.TargetLayers.Add(seedNode.Layer);
            scope.SeedColorKeys.Add(seedNode.EffectiveColorKey);
            if (learnedProfile != null)
            {
                foreach (string lLyr in learnedProfile.CircuitLayers)
                {
                    if (!string.IsNullOrWhiteSpace(lLyr)) scope.TargetLayers.Add(lLyr);
                }
            }

            List<string> bridgedNames;
            List<ObjectId> termIds;
            bool guardTriggered;
            ExpandConnectedPathCurvesCore(
                queue, compVisited, globalVisitedCurves, compIds, compHandles,
                sharedCurveGrid, bridgeGrid, terminatorGrid, sharedObstacleGrid, maxBridgeTol, maxTermTol,
                snapTol, bridgeThroughBlocks, connectGaps,
                effectiveUnitsPerM, refSpan, scope, maxCircuitBlocks,
                out bridgedNames, out termIds, out guardTriggered);

            for (int i = 0; i < compIds.Count; i++) globalVisitedCurves.Add(compIds[i]);

            List<PathCurveCandidate> pathCandidates = new List<PathCurveCandidate>();
            HashSet<string> pathTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double totalLenUnits = 0.0;
            Extents3d? compExt = null;
            List<PathBlockMatchItem> matchedItems = new List<PathBlockMatchItem>();
            Dictionary<string, int> countsByBlock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<CircuitTerminatorInfo> terminators = new List<CircuitTerminatorInfo>();
            int containerSkipped = 0;

            try
            {
                CollectCircuitPathCandidates(tr, compIds, pathCandidates, pathTypes, ref totalLenUnits, ref compExt);
                if (pathCandidates.Count == 0) return null;

                HashSet<ObjectId> termSet = termIds != null ? new HashSet<ObjectId>(termIds) : new HashSet<ObjectId>();
                MatchBlocksFromCacheAlongPath(
                    cache, pathCandidates, new HashSet<ObjectId>(compIds), termSet,
                    false, false, connectGaps,
                    "", "", new List<string>(), null,
                    effectiveUnitsPerM, refSpan, drawingSpan, learnedProfile,
                    matchedItems, countsByBlock, terminators, ref compExt, out containerSkipped);
            }
            finally
            {
                for (int i = 0; i < pathCandidates.Count; i++)
                {
                    try { if (pathCandidates[i].FlatCurve != null) pathCandidates[i].FlatCurve.Dispose(); } catch { }
                }
            }

            double tipTol = refSpan > 1e-3 ? Math.Max(1e-3, refSpan * 0.12) : Math.Max(1e-3, 0.06 * effectiveUnitsPerM);
            List<Point2d> openTips = CollectOpenWireTips(tr, compIds, tipTol);
            double tagRadius = refSpan > 1e-3 ? (refSpan * 4.5) : Math.Max(2.5 * effectiveUnitsPerM, 2.5);
            string circName, panelName, termType;
            cache.QueryNearbyCircuitTag(terminators, openTips, tagRadius, out circName, out panelName, out termType);

            bool nn;
            string wColor = ResolveEntityEffectiveColorKey(seedCv, colorMap, out nn);
            List<KeyValuePair<string, int>> pairs = new List<KeyValuePair<string, int>>(countsByBlock);
            pairs.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            List<string> parts = new List<string>();
            for (int pi = 0; pi < pairs.Count; pi++)
                parts.Add(pairs[pi].Key + " x" + pairs[pi].Value.ToString(CultureInfo.InvariantCulture));

            string endText = termType == "panel_connection" ? "لوحة" : (termType == "home_run_arrow" ? "سهم ARROW" : "نهاية مفتوحة");
            if (terminators.Count > 0 && !string.IsNullOrEmpty(terminators[0].BlockName))
                endText = terminators[0].BlockName + (!string.IsNullOrEmpty(circName) ? (" -> " + circName) : "");

            List<ObjectId> matchedIds = new List<ObjectId>(matchedItems.Count);
            List<string> matchedHandles = new List<string>(matchedItems.Count);
            for (int mi = 0; mi < matchedItems.Count; mi++)
            {
                matchedIds.Add(matchedItems[mi].Id);
                matchedHandles.Add(matchedItems[mi].Handle);
            }

            return new TracedCircuitSummary
            {
                CircuitName = circName ?? "",
                PanelName = panelName ?? "",
                TerminationType = termType ?? "open_end",
                WireLayer = seedCv.Layer ?? "0",
                WireColorKey = wColor,
                PathCurvesCount = compIds.Count,
                LengthUnits = totalLenUnits,
                LengthMeters = Math.Round(totalLenUnits / effectiveUnitsPerM, 2),
                MatchedBlocksCount = matchedItems.Count,
                CountsByBlock = countsByBlock,
                CountsSummary = parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "-",
                GuardTriggered = guardTriggered,
                MinX = compExt.HasValue ? compExt.Value.MinPoint.X : 0.0,
                MinY = compExt.HasValue ? compExt.Value.MinPoint.Y : 0.0,
                EndSummary = endText + (guardTriggered ? " [توقف حد الأمان]" : ""),
                MatchedBlockIds = matchedIds,
                PathCurveIds = compIds,
                MatchedHandles = matchedHandles
            };
        }

        private static TracedCircuitSummary HydrateSummaryFromSavedRecord(
            Database db,
            Transaction tr,
            SavedCircuitRecord lr,
            Dictionary<string, SavedColorState> colorMap,
            double effectiveUnitsPerM)
        {
            if (lr == null) return null;
            List<ObjectId> compIds = new List<ObjectId>();
            double totalLenUnits = 0.0;
            Extents3d? compExt = null;
            string wireLayer = lr.WireLayer ?? "0";
            string wireColor = lr.WireColorKey ?? "ACI:7";

            for (int i = 0; i < lr.PathHandles.Count; i++)
            {
                ObjectId oid = ResolveHandle(db, lr.PathHandles[i]);
                if (oid.IsNull || oid.IsErased) continue;
                Curve cv = tr.GetObject(oid, OpenMode.ForRead) as Curve;
                if (cv == null || cv.IsErased) continue;
                compIds.Add(oid);
                if (compIds.Count == 1)
                {
                    wireLayer = cv.Layer ?? "0";
                    bool nn;
                    wireColor = ResolveEntityEffectiveColorKey(cv, colorMap, out nn);
                }
                try { totalLenUnits += cv.GetDistanceAtParameter(cv.EndParam) - cv.GetDistanceAtParameter(cv.StartParam); } catch { }
                try
                {
                    Extents3d ex = cv.GeometricExtents;
                    if (IsFiniteBounds(ex))
                    {
                        if (!compExt.HasValue) compExt = ex;
                        else { Extents3d c = compExt.Value; c.AddExtents(ex); compExt = c; }
                    }
                }
                catch { }
            }

            List<ObjectId> matchedIds = new List<ObjectId>();
            List<string> matchedHandles = new List<string>();
            Dictionary<string, int> countsByBlock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lr.MatchedBlockHandles.Count; i++)
            {
                ObjectId oid = ResolveHandle(db, lr.MatchedBlockHandles[i]);
                if (oid.IsNull || oid.IsErased) continue;
                BlockReference br = tr.GetObject(oid, OpenMode.ForRead) as BlockReference;
                if (br == null || br.IsErased) continue;
                string bn = ResolveBlockName(tr, br);
                if (string.IsNullOrEmpty(bn)) continue;
                matchedIds.Add(oid);
                matchedHandles.Add(br.Handle.ToString());
                countsByBlock[bn] = countsByBlock.ContainsKey(bn) ? countsByBlock[bn] + 1 : 1;
                try
                {
                    Extents3d ex = br.GeometricExtents;
                    if (IsFiniteBounds(ex))
                    {
                        if (!compExt.HasValue) compExt = ex;
                        else { Extents3d c = compExt.Value; c.AddExtents(ex); compExt = c; }
                    }
                }
                catch { }
            }

            if (compIds.Count == 0 && matchedIds.Count == 0) return null;

            List<KeyValuePair<string, int>> pairs = new List<KeyValuePair<string, int>>(countsByBlock);
            pairs.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            List<string> parts = new List<string>();
            for (int pi = 0; pi < pairs.Count; pi++)
                parts.Add(pairs[pi].Key + " x" + pairs[pi].Value.ToString(CultureInfo.InvariantCulture));

            return new TracedCircuitSummary
            {
                CircuitName = lr.CircuitName,
                PanelName = lr.PanelName,
                TerminationType = string.IsNullOrWhiteSpace(lr.TerminationType) ? "panel_connection" : lr.TerminationType,
                WireLayer = wireLayer,
                WireColorKey = wireColor,
                PathCurvesCount = compIds.Count,
                LengthUnits = totalLenUnits,
                LengthMeters = Math.Round(totalLenUnits / effectiveUnitsPerM, 2),
                MatchedBlocksCount = matchedIds.Count,
                CountsByBlock = countsByBlock,
                CountsSummary = parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "-",
                GuardTriggered = false,
                MinX = compExt.HasValue ? compExt.Value.MinPoint.X : 0.0,
                MinY = compExt.HasValue ? compExt.Value.MinPoint.Y : 0.0,
                EndSummary = "معتمد ومقفل من المستخدم (" + matchedIds.Count.ToString(CultureInfo.InvariantCulture) + " وحدة)",
                MatchedBlockIds = matchedIds,
                PathCurveIds = compIds,
                MatchedHandles = matchedHandles
            };
        }

        private static DrawingCircuitScanResult ScanAllCircuitsInternal(
            Document doc,
            Database db,
            Transaction tr,
            Dictionary<string, object> args,
            bool honorSelectedHandles)
        {
            DrawingCircuitScanResult scan = new DrawingCircuitScanResult();
            string layerFilter = GetStr(args, "layer_filter") ?? GetStr(args, "layer");
            string colorFilter = GetStr(args, "color_filter") ?? GetStr(args, "color");
            bool visibleOnly = GetBool(args, "visible_only", true);
            bool strictColor = GetBool(args, "strict_color", true);
            bool strictLayer = GetBool(args, "strict_layer", false);
            bool connectGaps = GetBool(args, "connect_gaps", true);
            bool bridgeThroughBlocks = GetBool(args, "bridge_through_blocks", true);
            bool overwriteLocked = GetBool(args, "overwrite_locked", false);
            int maxCircuitBlocks = GetInt(args, "max_circuit_blocks", 35);
            if (maxCircuitBlocks < 0) maxCircuitBlocks = 0;
            int maxCircuits = GetInt(args, "max_circuits", 200);
            if (maxCircuits < 1) maxCircuits = 200;
            bool includeEmpty = GetBool(args, "include_empty", false);

            Dictionary<string, object> unitsInfo = GetUnitsInfo(db);
            double rawUnitsPerM = Convert.ToDouble(unitsInfo["units_per_m"], CultureInfo.InvariantCulture);
            if (rawUnitsPerM <= 1e-9) rawUnitsPerM = 1000.0;
            List<string> selectedHandles = honorSelectedHandles ? GetSelectedHandleStrings(doc) : new List<string>();
            CircuitSampleProfile learnedProfile = null;
            lock (s_lastCoordLock) { s_circuitProfileByDb.TryGetValue(db.UnmanagedObject, out learnedProfile); }

            Dictionary<string, bool> hiddenMap = BuildLayerHiddenMap(db, tr);
            Dictionary<string, SavedColorState> colorMap = BuildLayerColorStateMap(db, tr);
            double drawingSpan, medianFixtureSpan;
            Extents3d? mainExt;
            ComputeDrawingFixtureMetrics(db, tr, visibleOnly, hiddenMap, out drawingSpan, out medianFixtureSpan, out mainExt);

            double refSpan = (learnedProfile != null && learnedProfile.MedianFixtureSpan > 1e-3)
                ? learnedProfile.MedianFixtureSpan
                : medianFixtureSpan;
            bool scaleInferred;
            string scaleWarning;
            double effectiveUnitsPerM = ResolveEffectiveUnitsPerMeter(db, rawUnitsPerM, drawingSpan, refSpan, out scaleInferred, out scaleWarning);

            scan.EffectiveUnitsPerM = effectiveUnitsPerM;
            scan.ScaleInferred = scaleInferred;
            scan.ScaleWarning = scaleWarning;
            scan.DrawingSpan = drawingSpan;
            scan.MedianFixtureSpan = medianFixtureSpan;
            scan.RefSpan = refSpan;
            scan.MainExt = mainExt;

            scan.Cache = BuildDrawingBlockGeometryCache(
                db, tr, hiddenMap, visibleOnly, effectiveUnitsPerM, refSpan, drawingSpan,
                false, null, null);

            List<ObjectId> seedCurveIds = CollectAllCircuitCandidateCurveIds(
                db, tr, hiddenMap, colorMap, visibleOnly, layerFilter, colorFilter, selectedHandles, learnedProfile);
            if (seedCurveIds.Count == 0)
            {
                scan.ErrorMessage = "لم يتم العثور على طبقات أسلاك إنارة/قوى أو مسارات ملونة واضحة. حدد خط عينة واحد من اللينيا أو مرر layer_filter ثم أعد المحاولة.";
                return scan;
            }

            HashSet<string> seedLayerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < seedCurveIds.Count; i++)
            {
                Curve cv = tr.GetObject(seedCurveIds[i], OpenMode.ForRead) as Curve;
                if (cv != null && !cv.IsErased) seedLayerSet.Add(cv.Layer ?? "0");
            }
            if (learnedProfile != null)
            {
                foreach (string ll in learnedProfile.CircuitLayers)
                {
                    if (!string.IsNullOrWhiteSpace(ll)) seedLayerSet.Add(ll);
                }
            }

            double snapTol = refSpan > 1e-3 ? Math.Max(1e-3, refSpan * 0.12) : Math.Max(1e-3, 0.05 * effectiveUnitsPerM);
            double estBreakMax = refSpan > 1e-3 ? Math.Max(snapTol * 2.0, refSpan * 1.15) : Math.Max(1e-3, 0.90 * effectiveUnitsPerM);
            double gridCell = refSpan > 1e-3 ? Math.Max(0.25, Math.Max(estBreakMax * 1.5, refSpan * 2.5)) : Math.Max(1.0, estBreakMax * 1.5);

            List<CurveChainNode> allNodeRefs = new List<CurveChainNode>();
            Dictionary<ObjectId, CurveChainNode> curveNodesById = new Dictionary<ObjectId, CurveChainNode>();
            SpatialGrid2D<CurveChainNode> sharedCurveGrid = new SpatialGrid2D<CurveChainNode>(gridCell);
            SpatialGrid2D<CrossingObstacleSegment> sharedObstacleGrid = new SpatialGrid2D<CrossingObstacleSegment>(gridCell);
            SpatialGrid2D<BridgeBlockCandidate> bridgeGrid = new SpatialGrid2D<BridgeBlockCandidate>(gridCell);
            SpatialGrid2D<BridgeBlockCandidate> terminatorGrid = new SpatialGrid2D<BridgeBlockCandidate>(gridCell);
            double maxBridgeTol = 0.0;
            double maxTermTol = 0.0;

            try
            {
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                HashSet<ObjectId> seedIdSet = new HashSet<ObjectId>(seedCurveIds);

                foreach (ObjectId mid in ms)
                {
                    if (mid.IsErased) continue;
                    Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                    Curve cv = ent as Curve;
                    if (cv == null || ent.IsErased) continue;
                    if (IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                    if (visibleOnly && !IsEntityVisible(ent, hiddenMap)) continue;

                    CrossingObstacleSegment obs = TryCreateObstacleSegment(mid, cv, colorMap);
                    if (obs != null) sharedObstacleGrid.Add(obs.MinX, obs.MinY, obs.MaxX, obs.MaxY, obs);

                    string cLayer = ent.Layer ?? "0";
                    if (IsArchitecturalLayerName(cLayer) && !seedLayerSet.Contains(cLayer) && !seedIdSet.Contains(mid)) continue;

                    CurveChainNode cn = CreateCurveChainNode(mid, cv, colorMap);
                    if (cn == null) continue;
                    allNodeRefs.Add(cn);
                    curveNodesById[mid] = cn;
                    sharedCurveGrid.Add(cn.MinX, cn.MinY, cn.MaxX, cn.MaxY, cn);
                }

                if (bridgeThroughBlocks && scan.Cache != null)
                {
                    for (int fi = 0; fi < scan.Cache.FixtureBlocks.Count; fi++)
                    {
                        CachedBlockEntry fb = scan.Cache.FixtureBlocks[fi];
                        BridgeBlockCandidate bbc = new BridgeBlockCandidate
                        {
                            Id = fb.Id,
                            Handle = fb.Handle,
                            BlockName = fb.BlockName,
                            Br = fb.Br,
                            Bounds = fb.Bounds,
                            Span = fb.Span,
                            IsPanelRoot = fb.IsPanelRoot,
                            IsTerminator = fb.IsTerminator
                        };
                        bridgeGrid.Add(bbc.Bounds.MinPoint.X, bbc.Bounds.MinPoint.Y, bbc.Bounds.MaxPoint.X, bbc.Bounds.MaxPoint.Y, bbc);
                        double estBTol = Math.Max(snapTol, bbc.Span * (connectGaps ? 0.36 : 0.18));
                        if (estBTol > maxBridgeTol) maxBridgeTol = estBTol;
                    }
                    for (int ti = 0; ti < scan.Cache.TerminatorBlocks.Count; ti++)
                    {
                        CachedBlockEntry tb = scan.Cache.TerminatorBlocks[ti];
                        BridgeBlockCandidate bbc = new BridgeBlockCandidate
                        {
                            Id = tb.Id,
                            Handle = tb.Handle,
                            BlockName = tb.BlockName,
                            Br = tb.Br,
                            Bounds = tb.Bounds,
                            Span = tb.Span,
                            IsPanelRoot = tb.IsPanelRoot,
                            IsTerminator = tb.IsTerminator
                        };
                        bridgeGrid.Add(bbc.Bounds.MinPoint.X, bbc.Bounds.MinPoint.Y, bbc.Bounds.MaxPoint.X, bbc.Bounds.MaxPoint.Y, bbc);
                        terminatorGrid.Add(bbc.Bounds.MinPoint.X, bbc.Bounds.MinPoint.Y, bbc.Bounds.MaxPoint.X, bbc.Bounds.MaxPoint.Y, bbc);
                        double bTol = Math.Max(snapTol, bbc.Span * 0.45);
                        if (bTol > maxBridgeTol) maxBridgeTol = bTol;
                        double tTol = Math.Max(snapTol, bbc.Span * 0.55);
                        if (tTol > maxTermTol) maxTermTol = tTol;
                    }
                }

                HashSet<ObjectId> globalVisited = new HashSet<ObjectId>();
                if (!overwriteLocked)
                {
                    List<SavedCircuitRecord> lockedList = GetLockedCircuitRecordsCopy(doc);
                    for (int li = 0; li < lockedList.Count && scan.Circuits.Count < maxCircuits; li++)
                    {
                        SavedCircuitRecord lr = lockedList[li];
                        if (!string.IsNullOrWhiteSpace(layerFilter) && !MatchesPatternOrSubstring(lr.WireLayer, layerFilter)) continue;
                        if (!string.IsNullOrWhiteSpace(colorFilter) && !MatchesPatternOrSubstring(lr.WireColorKey, colorFilter)) continue;
                        TracedCircuitSummary hydrated = HydrateSummaryFromSavedRecord(db, tr, lr, colorMap, effectiveUnitsPerM);
                        if (hydrated == null || (!includeEmpty && hydrated.MatchedBlocksCount == 0)) continue;
                        for (int pi = 0; pi < hydrated.PathCurveIds.Count; pi++)
                            globalVisited.Add(hydrated.PathCurveIds[pi]);
                        scan.Circuits.Add(hydrated);
                        scan.TotalBlocks += hydrated.MatchedBlocksCount;
                        scan.TotalLengthM += hydrated.LengthMeters;
                    }
                }

                for (int si = 0; si < seedCurveIds.Count && scan.Circuits.Count < maxCircuits; si++)
                {
                    ObjectId seedId = seedCurveIds[si];
                    if (globalVisited.Contains(seedId)) continue;
                    TracedCircuitSummary summary = TraceSingleCircuitComponentCached(
                        db, tr, scan.Cache, curveNodesById, sharedCurveGrid, bridgeGrid, terminatorGrid, sharedObstacleGrid,
                        maxBridgeTol, maxTermTol, colorMap, learnedProfile,
                        seedId, globalVisited, strictColor, strictLayer, connectGaps, bridgeThroughBlocks,
                        maxCircuitBlocks, effectiveUnitsPerM, refSpan, drawingSpan);
                    if (summary == null) continue;
                    if (!includeEmpty && summary.MatchedBlocksCount == 0) continue;
                    scan.Circuits.Add(summary);
                    scan.TotalBlocks += summary.MatchedBlocksCount;
                    scan.TotalLengthM += summary.LengthMeters;
                    if (summary.GuardTriggered) scan.GuardTriggeredCount++;
                }

                scan.Circuits.Sort(delegate(TracedCircuitSummary a, TracedCircuitSummary b)
                {
                    bool aHas = !string.IsNullOrWhiteSpace(a.CircuitName);
                    bool bHas = !string.IsNullOrWhiteSpace(b.CircuitName);
                    if (aHas != bHas) return aHas ? -1 : 1;
                    int c = string.Compare(a.PanelName, b.PanelName, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                    c = string.Compare(a.CircuitName, b.CircuitName, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                    c = b.MinY.CompareTo(a.MinY);
                    return c != 0 ? c : a.MinX.CompareTo(b.MinX);
                });

                for (int ci = 0; ci < scan.Circuits.Count; ci++)
                {
                    TracedCircuitSummary c = scan.Circuits[ci];
                    if (string.IsNullOrWhiteSpace(c.CircuitName)) continue;
                    List<string> pHandles = new List<string>(c.PathCurveIds.Count);
                    for (int pi = 0; pi < c.PathCurveIds.Count; pi++)
                        pHandles.Add(c.PathCurveIds[pi].Handle.ToString());
                    SaveCircuitRecordToMemory(doc, new SavedCircuitRecord
                    {
                        CircuitName = c.CircuitName,
                        PanelName = c.PanelName,
                        TerminationType = c.TerminationType,
                        WireLayer = c.WireLayer,
                        WireColorKey = c.WireColorKey,
                        PathHandles = pHandles,
                        MatchedBlockHandles = new List<string>(c.MatchedHandles),
                        LengthMeters = c.LengthMeters,
                        CountsByBlock = new Dictionary<string, int>(c.CountsByBlock, StringComparer.OrdinalIgnoreCase),
                        UserLocked = false
                    }, false, overwriteLocked);
                }
            }
            finally
            {
                for (int i = 0; i < allNodeRefs.Count; i++)
                {
                    try { if (allNodeRefs[i].FlatCurve != null) allNodeRefs[i].FlatCurve.Dispose(); } catch { }
                }
            }

            return scan;
        }

        private static ExcelSheetData BuildCircuitScheduleSheet(
            List<TracedCircuitSummary> circuits,
            double fixtureVa,
            string title,
            string scaleWarning)
        {
            ExcelSheetData sheet = new ExcelSheetData();
            sheet.SheetName = "Circuit Schedule";
            sheet.Title = !string.IsNullOrWhiteSpace(title)
                ? title
                : ("جدول حصر وتفصيل دوائر/لينيات المخطط (" + circuits.Count.ToString(CultureInfo.InvariantCulture) + " لينية)");
            sheet.Headers = new List<string>
            {
                "م",
                "اسم اللينيا",
                "اللوحة",
                "الطبقة / اللون",
                "البلوكات",
                "تفصيل الأنواع",
                "طول السلك (م)",
                "الحمل التقديري (VA)",
                "النهاية / ملاحظات"
            };

            int totalBlocks = 0;
            double totalLenM = 0.0;
            double totalVa = 0.0;
            for (int i = 0; i < circuits.Count; i++)
            {
                TracedCircuitSummary c = circuits[i];
                double estVa = Math.Round(c.MatchedBlocksCount * Math.Max(0.0, fixtureVa), 1);
                totalBlocks += c.MatchedBlocksCount;
                totalLenM += c.LengthMeters;
                totalVa += estVa;
                sheet.Rows.Add(new List<string>
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(c.CircuitName) ? ("CIRC-" + (i + 1).ToString(CultureInfo.InvariantCulture)) : c.CircuitName,
                    string.IsNullOrWhiteSpace(c.PanelName) ? "-" : c.PanelName,
                    c.WireLayer + " (" + c.WireColorKey + ")",
                    c.MatchedBlocksCount.ToString(CultureInfo.InvariantCulture),
                    c.CountsSummary,
                    c.LengthMeters.ToString("0.##", CultureInfo.InvariantCulture),
                    estVa.ToString("0.#", CultureInfo.InvariantCulture),
                    c.EndSummary
                });
            }

            string footerNote = string.IsNullOrWhiteSpace(scaleWarning) ? "مكتمل" : "تم تصحيح المقياس تلقائياً";
            sheet.FooterRow = new List<string>
            {
                "الإجمالي (" + circuits.Count.ToString(CultureInfo.InvariantCulture) + ")",
                "",
                "",
                "",
                totalBlocks.ToString(CultureInfo.InvariantCulture),
                "",
                Math.Round(totalLenM, 2).ToString("0.##", CultureInfo.InvariantCulture) + " م",
                Math.Round(totalVa, 1).ToString("0.#", CultureInfo.InvariantCulture) + " VA",
                footerNote
            };
            return sheet;
        }

        private static Dictionary<string, object> TraceAllCircuits(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            double fixtureVa = GetDouble(args, "fixture_va", 40.0);
            bool drawTable = GetBool(args, "draw_table", true);
            string tablePos = GetStr(args, "table_position") ?? "outside_right";
            double? tableGap = GetNullableDouble(args, "table_gap");
            string rawTitle = GetStr(args, "title");
            bool replaceExisting = GetBool(args, "replace_existing", true);
            bool exportExcel = GetBool(args, "export_excel", false);
            bool zoom = GetBool(args, "zoom", true);

            DrawingCircuitScanResult scan;
            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    scan = ScanAllCircuitsInternal(doc, db, tr, args, true);
                    tr.Commit();
                }

                if (!string.IsNullOrEmpty(scan.ErrorMessage))
                    return Err(scan.ErrorMessage);
                if (scan.Circuits.Count == 0)
                    return Err("تم فحص الخطوط المرشحة ولم يتم رصد دوائر تحتوي على بلوكات إنارة/معدات مطابقة.");

                List<TracedCircuitSummary> circuits = scan.Circuits;
                ExcelSheetData sheet = BuildCircuitScheduleSheet(circuits, fixtureVa, rawTitle, scan.ScaleWarning);
                bool tableDrawn = false;
                int createdCount = 0;
                Extents3d? tableExt = null;

                if (drawTable)
                {
                    if (!db.TileMode) { try { db.TileMode = true; } catch { } }
                    List<ObjectId> createdIds = new List<ObjectId>();
                    List<ObjectId> erasedIds = new List<ObjectId>();
                    using (Transaction trTbl = db.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord msW = (BlockTableRecord)trTbl.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                        tableExt = RenderStyledScheduleTable(
                            doc, db, msW, trTbl, sheet, "_AI_CIRCUIT_SCHEDULE",
                            replaceExisting, tablePos, tableGap, null, scan.MainExt, createdIds, erasedIds);
                        db.UpdateExt(true);
                        trTbl.Commit();
                    }
                    tableDrawn = tableExt.HasValue;
                    createdCount = createdIds.Count;
                    if (createdIds.Count > 0 || erasedIds.Count > 0)
                    {
                        CadUndoEntry undo = new CadUndoEntry();
                        undo.ToolName = "trace_all_circuits";
                        undo.Summary = "Batch Circuit Schedule (" + circuits.Count.ToString(CultureInfo.InvariantCulture) + " circuits)";
                        undo.CreatedIds.AddRange(createdIds);
                        undo.ErasedIds.AddRange(erasedIds);
                        PushUndoEntry(doc, undo);
                    }
                    if (zoom && tableExt.HasValue && IsFiniteBounds(tableExt.Value))
                        ZoomToExtents3d(doc, tableExt.Value, 1.18);
                    try { doc.Editor.UpdateScreen(); } catch { }
                }

                List<object> circuitList = new List<object>();
                for (int i = 0; i < circuits.Count; i++)
                {
                    TracedCircuitSummary c = circuits[i];
                    circuitList.Add(new Dictionary<string, object>
                    {
                        { "index", i + 1 },
                        { "circuit_name", c.CircuitName },
                        { "panel_name", c.PanelName },
                        { "termination_type", c.TerminationType },
                        { "wire_layer", c.WireLayer },
                        { "wire_color_key", c.WireColorKey },
                        { "path_curves_count", c.PathCurvesCount },
                        { "total_path_length_m", c.LengthMeters },
                        { "matched_blocks_count", c.MatchedBlocksCount },
                        { "counts_by_block", c.CountsByBlock },
                        { "estimated_va", Math.Round(c.MatchedBlocksCount * Math.Max(0.0, fixtureVa), 1) },
                        { "guard_triggered", c.GuardTriggered }
                    });
                }

                Dictionary<string, object> res = new Dictionary<string, object>
                {
                    { "circuits_count", circuits.Count },
                    { "total_blocks_count", scan.TotalBlocks },
                    { "total_path_length_m", Math.Round(scan.TotalLengthM, 2) },
                    { "effective_units_per_m", Round(scan.EffectiveUnitsPerM) },
                    { "unit_scale_inferred", scan.ScaleInferred },
                    { "guard_triggered_count", scan.GuardTriggeredCount },
                    { "circuits", circuitList },
                    { "table_drawn", tableDrawn },
                    { "table_layer", "_AI_CIRCUIT_SCHEDULE" },
                    { "created_count", createdCount }
                };
                if (!string.IsNullOrEmpty(scan.ScaleWarning)) res["unit_scale_warning"] = scan.ScaleWarning;
                if (exportExcel)
                {
                    string xlTarget = GetStr(args, "excel_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "circuit_schedule", ".xlsx");
                    res["excel_exported"] = true;
                    res["excel_path"] = ExcelTableInterop.WriteSpreadsheet(xlTarget, sheet);
                }
                return Ok(res);
            }
        }
    }
}
