using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        private static List<ObjectId> ResolveTargetEntitiesGeneral(
            Document doc,
            Database db,
            BlockTableRecord ms,
            Transaction tr,
            Dictionary<string, object> args)
        {
            List<string> explicitHandles = ExtractStringListArg(args, "handles");
            bool selectedOnly = GetBool(args, "selected_only", explicitHandles.Count == 0 && string.IsNullOrWhiteSpace(GetStr(args, "block_name")) && string.IsNullOrWhiteSpace(GetStr(args, "layer_filter")) && string.IsNullOrWhiteSpace(GetStr(args, "entity_type")));
            string blockFilter = GetStr(args, "block_name");
            string layerFilter = GetStr(args, "layer_filter");
            string typeFilter = GetStr(args, "entity_type");
            double[] bbox = GetDoubleArray(args, "bbox");

            HashSet<string> targetSet = new HashSet<string>(explicitHandles, StringComparer.OrdinalIgnoreCase);
            if (selectedOnly && explicitHandles.Count == 0)
            {
                List<string> sel = GetSelectedHandleStrings(doc);
                for (int i = 0; i < sel.Count; i++) targetSet.Add(sel[i]);
            }

            List<ObjectId> ids = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;

                if (explicitHandles.Count > 0 || selectedOnly)
                {
                    if (!targetSet.Contains(ent.Handle.ToString())) continue;
                }
                if (!string.IsNullOrWhiteSpace(layerFilter) && !MatchesPatternOrSubstring(ent.Layer ?? "", layerFilter)) continue;
                if (!string.IsNullOrWhiteSpace(typeFilter) && !MatchesPatternOrSubstring(ent.GetType().Name, typeFilter)) continue;
                if (!string.IsNullOrWhiteSpace(blockFilter))
                {
                    BlockReference br = ent as BlockReference;
                    if (br == null) continue;
                    string bn = ResolveBlockName(tr, br);
                    if (!string.Equals(bn, blockFilter, StringComparison.OrdinalIgnoreCase) && !MatchesPatternOrSubstring(bn, blockFilter)) continue;
                }
                if (bbox != null && bbox.Length >= 4)
                {
                    try
                    {
                        Extents3d ex = ent.GeometricExtents;
                        double cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                        double cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                        if (cx < bbox[0] || cx > bbox[2] || cy < bbox[1] || cy > bbox[3]) continue;
                    }
                    catch { continue; }
                }
                ids.Add(id);
            }
            return ids;
        }

        private static Polyline BuildScallopedRevisionCloud(double minX, double minY, double maxX, double maxY, string layerName, short colorIndex)
        {
            double w = Math.Max(1.0, maxX - minX);
            double h = Math.Max(1.0, maxY - minY);
            double perim = 2.0 * (w + h);
            double step = Math.Max(Math.Min(w, h) * 0.16, perim / 24.0);
            step = Math.Max(0.5, Math.Min(step, Math.Min(w, h) * 0.45));

            List<Point2d> pts = new List<Point2d>();
            int nx = Math.Max(2, (int)Math.Ceiling(w / step));
            int ny = Math.Max(2, (int)Math.Ceiling(h / step));

            // Walk counter-clockwise around rectangle so positive bulge creates outward arcs
            for (int i = 0; i < nx; i++) pts.Add(new Point2d(minX + (w * i) / nx, minY));
            for (int i = 0; i < ny; i++) pts.Add(new Point2d(maxX, minY + (h * i) / ny));
            for (int i = 0; i < nx; i++) pts.Add(new Point2d(maxX - (w * i) / nx, maxY));
            for (int i = 0; i < ny; i++) pts.Add(new Point2d(minX, maxY - (h * i) / ny));

            Polyline pl = new Polyline();
            pl.Layer = layerName;
            pl.ColorIndex = colorIndex;
            pl.Closed = true;
            for (int i = 0; i < pts.Count; i++)
                pl.AddVertexAt(i, pts[i], -0.45, 0.0, 0.0);
            return pl;
        }

        private static Dictionary<string, object> AdvancedGeometryOps(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            string op = (GetStr(args, "operation") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(op))
                return Err("'operation' is required ('align', 'distribute', 'array_rect', 'array_polar', 'offset', 'revision_cloud', 'explode').");

            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();
            Dictionary<ObjectId, ModifiedEntitySnapshot> snapById = new Dictionary<ObjectId, ModifiedEntitySnapshot>();
            bool zoom = GetBool(args, "zoom", true);
            Extents3d? resultExt = null;
            int processedCount = 0;

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    if (op == "revision_cloud")
                    {
                        double[] bbox = GetDoubleArray(args, "bbox");
                        double padding = Math.Max(0.0, GetDouble(args, "padding", 5.0));
                        string layer = EnsureLayerSafe(db, tr, GetStr(args, "layer") ?? "_AI_REV_CLOUD", (short)ClampColor(GetInt(args, "color_index", 1)), "Continuous");
                        short colIdx = (short)ClampColor(GetInt(args, "color_index", 1));

                        double minX = 0, minY = 0, maxX = 0, maxY = 0;
                        bool hasBox = false;
                        if (bbox != null && bbox.Length >= 4)
                        {
                            minX = Math.Min(bbox[0], bbox[2]) - padding;
                            minY = Math.Min(bbox[1], bbox[3]) - padding;
                            maxX = Math.Max(bbox[0], bbox[2]) + padding;
                            maxY = Math.Max(bbox[1], bbox[3]) + padding;
                            hasBox = true;
                        }
                        else
                        {
                            List<ObjectId> ids = ResolveTargetEntitiesGeneral(doc, db, ms, tr, args);
                            Extents3d? uExt = null;
                            for (int i = 0; i < ids.Count; i++)
                            {
                                Entity e = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                                if (e == null) continue;
                                try
                                {
                                    Extents3d ex = e.GeometricExtents;
                                    if (!IsFiniteBounds(ex)) continue;
                                    if (!uExt.HasValue) uExt = ex;
                                    else { Extents3d m = uExt.Value; m.AddExtents(ex); uExt = m; }
                                }
                                catch { }
                            }
                            if (uExt.HasValue)
                            {
                                double autoPad = Math.Max(padding, Math.Max(uExt.Value.MaxPoint.X - uExt.Value.MinPoint.X, uExt.Value.MaxPoint.Y - uExt.Value.MinPoint.Y) * 0.12);
                                minX = uExt.Value.MinPoint.X - autoPad;
                                minY = uExt.Value.MinPoint.Y - autoPad;
                                maxX = uExt.Value.MaxPoint.X + autoPad;
                                maxY = uExt.Value.MaxPoint.Y + autoPad;
                                hasBox = true;
                            }
                        }

                        if (!hasBox)
                        {
                            tr.Commit();
                            return Err("حدد عناصر أو مرّر bbox لإنشاء سحابة المراجعة (Revision Cloud).");
                        }

                        Polyline cloud = BuildScallopedRevisionCloud(minX, minY, maxX, maxY, layer, colIdx);
                        ms.AppendEntity(cloud);
                        tr.AddNewlyCreatedDBObject(cloud, true);
                        createdIds.Add(cloud.ObjectId);
                        processedCount = 1;
                        resultExt = new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));

                        string lbl = GetStr(args, "label");
                        if (!string.IsNullOrWhiteSpace(lbl))
                        {
                            ObjectId uStyle = EnsureUnicodeTextStyle(db, tr);
                            double th = Math.Max(1.0, (maxY - minY) * 0.08);
                            MText mt = new MText();
                            mt.SetDatabaseDefaults(db);
                            mt.Layer = layer;
                            mt.ColorIndex = colIdx;
                            mt.TextStyleId = uStyle;
                            mt.TextHeight = th;
                            mt.Attachment = AttachmentPoint.BottomCenter;
                            mt.Location = new Point3d((minX + maxX) * 0.5, maxY + th * 0.6, 0.0);
                            mt.Contents = EscapeMTextPlain(lbl);
                            ms.AppendEntity(mt);
                            tr.AddNewlyCreatedDBObject(mt, true);
                            createdIds.Add(mt.ObjectId);
                        }
                    }
                    else
                    {
                        List<ObjectId> ids = ResolveTargetEntitiesGeneral(doc, db, ms, tr, args);
                        if (ids.Count == 0)
                        {
                            tr.Commit();
                            return Err("لم يتم العثور على عناصر لتنفيذ العملية الهندسية عليها. حدد عناصر في AutoCAD أو مرّر handles / block_name / layer_filter.");
                        }

                        if (op == "align")
                        {
                            string alignment = (GetStr(args, "alignment") ?? "left").Trim().ToLowerInvariant();
                            List<KeyValuePair<Entity, Extents3d>> items = new List<KeyValuePair<Entity, Extents3d>>();
                            double refCoord = 0.0;
                            for (int i = 0; i < ids.Count; i++)
                            {
                                Entity e = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                                if (e == null) continue;
                                try
                                {
                                    Extents3d ex = e.GeometricExtents;
                                    if (!IsFiniteBounds(ex)) continue;
                                    items.Add(new KeyValuePair<Entity, Extents3d>(e, ex));
                                }
                                catch { }
                            }
                            if (items.Count < 2) { tr.Commit(); return Err("المحاذاة تتطلب عنصرين على الأقل."); }

                            if (alignment == "left")
                            {
                                refCoord = items[0].Value.MinPoint.X;
                                for (int i = 1; i < items.Count; i++) refCoord = Math.Min(refCoord, items[i].Value.MinPoint.X);
                            }
                            else if (alignment == "right")
                            {
                                refCoord = items[0].Value.MaxPoint.X;
                                for (int i = 1; i < items.Count; i++) refCoord = Math.Max(refCoord, items[i].Value.MaxPoint.X);
                            }
                            else if (alignment == "top")
                            {
                                refCoord = items[0].Value.MaxPoint.Y;
                                for (int i = 1; i < items.Count; i++) refCoord = Math.Max(refCoord, items[i].Value.MaxPoint.Y);
                            }
                            else if (alignment == "bottom")
                            {
                                refCoord = items[0].Value.MinPoint.Y;
                                for (int i = 1; i < items.Count; i++) refCoord = Math.Min(refCoord, items[i].Value.MinPoint.Y);
                            }
                            else if (alignment == "center_x")
                            {
                                double s = 0;
                                for (int i = 0; i < items.Count; i++) s += (items[i].Value.MinPoint.X + items[i].Value.MaxPoint.X) * 0.5;
                                refCoord = s / items.Count;
                            }
                            else if (alignment == "center_y")
                            {
                                double s = 0;
                                for (int i = 0; i < items.Count; i++) s += (items[i].Value.MinPoint.Y + items[i].Value.MaxPoint.Y) * 0.5;
                                refCoord = s / items.Count;
                            }

                            for (int i = 0; i < items.Count; i++)
                            {
                                Entity e = items[i].Key;
                                Extents3d ex = items[i].Value;
                                double dx = 0, dy = 0;
                                if (alignment == "left") dx = refCoord - ex.MinPoint.X;
                                else if (alignment == "right") dx = refCoord - ex.MaxPoint.X;
                                else if (alignment == "center_x") dx = refCoord - (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                                else if (alignment == "top") dy = refCoord - ex.MaxPoint.Y;
                                else if (alignment == "bottom") dy = refCoord - ex.MinPoint.Y;
                                else if (alignment == "center_y") dy = refCoord - (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;

                                if (Math.Abs(dx) > 1e-7 || Math.Abs(dy) > 1e-7)
                                {
                                    CaptureEntitySnapshot(e, snapById);
                                    Matrix3d mat = Matrix3d.Displacement(new Vector3d(dx, dy, 0.0));
                                    e.UpgradeOpen();
                                    e.TransformBy(mat);
                                    snapById[e.ObjectId].InverseTransforms.Add(mat.Inverse());
                                    e.RecordGraphicsModified(true);
                                    processedCount++;
                                }
                                try
                                {
                                    Extents3d nex = e.GeometricExtents;
                                    if (!resultExt.HasValue) resultExt = nex;
                                    else { Extents3d m = resultExt.Value; m.AddExtents(nex); resultExt = m; }
                                }
                                catch { }
                            }
                        }
                        else if (op == "distribute")
                        {
                            string axis = (GetStr(args, "axis") ?? "horizontal").Trim().ToLowerInvariant();
                            string distMode = (GetStr(args, "mode") ?? "equal_gaps").Trim().ToLowerInvariant();
                            bool horiz = (axis != "vertical" && axis != "y");

                            List<KeyValuePair<Entity, Extents3d>> items = new List<KeyValuePair<Entity, Extents3d>>();
                            for (int i = 0; i < ids.Count; i++)
                            {
                                Entity e = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                                if (e == null) continue;
                                try
                                {
                                    Extents3d ex = e.GeometricExtents;
                                    if (IsFiniteBounds(ex)) items.Add(new KeyValuePair<Entity, Extents3d>(e, ex));
                                }
                                catch { }
                            }
                            if (items.Count < 3) { tr.Commit(); return Err("التوزيع المتساوي يتطلب 3 عناصر على الأقل."); }

                            items.Sort(delegate(KeyValuePair<Entity, Extents3d> a, KeyValuePair<Entity, Extents3d> b)
                            {
                                double ca = horiz ? (a.Value.MinPoint.X + a.Value.MaxPoint.X) * 0.5 : (a.Value.MinPoint.Y + a.Value.MaxPoint.Y) * 0.5;
                                double cb = horiz ? (b.Value.MinPoint.X + b.Value.MaxPoint.X) * 0.5 : (b.Value.MinPoint.Y + b.Value.MaxPoint.Y) * 0.5;
                                return ca.CompareTo(cb);
                            });

                            if (distMode == "equal_centers")
                            {
                                double firstC = horiz ? (items[0].Value.MinPoint.X + items[0].Value.MaxPoint.X) * 0.5 : (items[0].Value.MinPoint.Y + items[0].Value.MaxPoint.Y) * 0.5;
                                double lastC = horiz ? (items[items.Count - 1].Value.MinPoint.X + items[items.Count - 1].Value.MaxPoint.X) * 0.5 : (items[items.Count - 1].Value.MinPoint.Y + items[items.Count - 1].Value.MaxPoint.Y) * 0.5;
                                double step = (lastC - firstC) / (items.Count - 1);
                                for (int i = 1; i < items.Count - 1; i++)
                                {
                                    double curC = horiz ? (items[i].Value.MinPoint.X + items[i].Value.MaxPoint.X) * 0.5 : (items[i].Value.MinPoint.Y + items[i].Value.MaxPoint.Y) * 0.5;
                                    double delta = (firstC + i * step) - curC;
                                    if (Math.Abs(delta) > 1e-7)
                                    {
                                        Entity e = items[i].Key;
                                        CaptureEntitySnapshot(e, snapById);
                                        Matrix3d mat = Matrix3d.Displacement(horiz ? new Vector3d(delta, 0, 0) : new Vector3d(0, delta, 0));
                                        e.UpgradeOpen();
                                        e.TransformBy(mat);
                                        snapById[e.ObjectId].InverseTransforms.Add(mat.Inverse());
                                        e.RecordGraphicsModified(true);
                                        processedCount++;
                                    }
                                }
                            }
                            else
                            {
                                double minStart = horiz ? items[0].Value.MinPoint.X : items[0].Value.MinPoint.Y;
                                double maxEnd = horiz ? items[items.Count - 1].Value.MaxPoint.X : items[items.Count - 1].Value.MaxPoint.Y;
                                double sumSpans = 0;
                                for (int i = 0; i < items.Count; i++)
                                    sumSpans += horiz ? (items[i].Value.MaxPoint.X - items[i].Value.MinPoint.X) : (items[i].Value.MaxPoint.Y - items[i].Value.MinPoint.Y);
                                double gap = ((maxEnd - minStart) - sumSpans) / (items.Count - 1);
                                double cursor = horiz ? items[0].Value.MaxPoint.X : items[0].Value.MaxPoint.Y;
                                for (int i = 1; i < items.Count - 1; i++)
                                {
                                    double span = horiz ? (items[i].Value.MaxPoint.X - items[i].Value.MinPoint.X) : (items[i].Value.MaxPoint.Y - items[i].Value.MinPoint.Y);
                                    double targetMin = cursor + gap;
                                    double curMin = horiz ? items[i].Value.MinPoint.X : items[i].Value.MinPoint.Y;
                                    double delta = targetMin - curMin;
                                    if (Math.Abs(delta) > 1e-7)
                                    {
                                        Entity e = items[i].Key;
                                        CaptureEntitySnapshot(e, snapById);
                                        Matrix3d mat = Matrix3d.Displacement(horiz ? new Vector3d(delta, 0, 0) : new Vector3d(0, delta, 0));
                                        e.UpgradeOpen();
                                        e.TransformBy(mat);
                                        snapById[e.ObjectId].InverseTransforms.Add(mat.Inverse());
                                        e.RecordGraphicsModified(true);
                                        processedCount++;
                                    }
                                    cursor = targetMin + span;
                                }
                            }
                        }
                        else if (op == "array_rect")
                        {
                            int rCount = Math.Max(1, Math.Min(50, GetInt(args, "rows", 1)));
                            int cCount = Math.Max(1, Math.Min(50, GetInt(args, "cols", 1)));
                            double rowSpacing = GetDouble(args, "row_spacing", 10.0);
                            double colSpacing = GetDouble(args, "col_spacing", 10.0);

                            for (int i = 0; i < ids.Count; i++)
                            {
                                Entity src = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                                if (src == null) continue;
                                for (int r = 0; r < rCount; r++)
                                {
                                    for (int c = 0; c < cCount; c++)
                                    {
                                        if (r == 0 && c == 0) continue;
                                        Entity clone = src.Clone() as Entity;
                                        if (clone == null) continue;
                                        clone.TransformBy(Matrix3d.Displacement(new Vector3d(c * colSpacing, r * rowSpacing, 0.0)));
                                        ms.AppendEntity(clone);
                                        tr.AddNewlyCreatedDBObject(clone, true);
                                        createdIds.Add(clone.ObjectId);
                                        processedCount++;
                                    }
                                }
                            }
                        }
                        else if (op == "array_polar")
                        {
                            int count = Math.Max(2, Math.Min(120, GetInt(args, "count", 4)));
                            double totalAngleDeg = GetDouble(args, "total_angle_deg", 360.0);
                            bool rotateItems = GetBool(args, "rotate_items", true);
                            double[] centerArr = GetDoubleArray(args, "center");
                            Point3d centerPt = Point3d.Origin;
                            if (centerArr != null && centerArr.Length >= 2)
                            {
                                centerPt = new Point3d(centerArr[0], centerArr[1], 0.0);
                            }
                            else
                            {
                                Extents3d? u = null;
                                for (int i = 0; i < ids.Count; i++)
                                {
                                    Entity e = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                                    try
                                    {
                                        Extents3d ex = e.GeometricExtents;
                                        if (!u.HasValue) u = ex; else { Extents3d m = u.Value; m.AddExtents(ex); u = m; }
                                    }
                                    catch { }
                                }
                                if (u.HasValue)
                                    centerPt = new Point3d((u.Value.MinPoint.X + u.Value.MaxPoint.X) * 0.5, (u.Value.MinPoint.Y + u.Value.MaxPoint.Y) * 0.5, 0.0);
                            }

                            double stepDeg = Math.Abs(Math.Abs(totalAngleDeg) - 360.0) < 1e-4 ? (totalAngleDeg / count) : (totalAngleDeg / (count - 1));
                            for (int i = 0; i < ids.Count; i++)
                            {
                                Entity src = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                                if (src == null) continue;
                                Extents3d sEx = src.GeometricExtents;
                                Point3d srcMid = new Point3d((sEx.MinPoint.X + sEx.MaxPoint.X) * 0.5, (sEx.MinPoint.Y + sEx.MaxPoint.Y) * 0.5, 0.0);
                                for (int k = 1; k < count; k++)
                                {
                                    double angRad = (stepDeg * k) * Math.PI / 180.0;
                                    Entity clone = src.Clone() as Entity;
                                    if (clone == null) continue;
                                    if (rotateItems)
                                    {
                                        clone.TransformBy(Matrix3d.Rotation(angRad, Vector3d.ZAxis, centerPt));
                                    }
                                    else
                                    {
                                        Point3d rotMid = srcMid.TransformBy(Matrix3d.Rotation(angRad, Vector3d.ZAxis, centerPt));
                                        clone.TransformBy(Matrix3d.Displacement(rotMid - srcMid));
                                    }
                                    ms.AppendEntity(clone);
                                    tr.AddNewlyCreatedDBObject(clone, true);
                                    createdIds.Add(clone.ObjectId);
                                    processedCount++;
                                }
                            }
                        }
                        else if (op == "offset")
                        {
                            double dist = Math.Abs(GetDouble(args, "distance", 1.0));
                            if (dist < 1e-6) { tr.Commit(); return Err("'distance' must be > 0 for offset."); }
                            string side = (GetStr(args, "side") ?? "positive").Trim().ToLowerInvariant();
                            string reqLyr = GetStr(args, "layer");
                            int? reqCol = GetNullableInt(args, "color_index");

                            double[] signs = (side == "both") ? new double[] { 1.0, -1.0 } : ((side == "negative" || side == "inward") ? new double[] { -1.0 } : new double[] { 1.0 });
                            for (int i = 0; i < ids.Count; i++)
                            {
                                Curve cv = tr.GetObject(ids[i], OpenMode.ForRead) as Curve;
                                if (cv == null) continue;
                                for (int s = 0; s < signs.Length; s++)
                                {
                                    try
                                    {
                                        DBObjectCollection offsets = cv.GetOffsetCurves(dist * signs[s]);
                                        if (offsets == null) continue;
                                        foreach (DBObject obj in offsets)
                                        {
                                            Entity oe = obj as Entity;
                                            if (oe == null) continue;
                                            oe.Layer = !string.IsNullOrWhiteSpace(reqLyr) ? EnsureLayerSafe(db, tr, reqLyr, null, "Continuous") : cv.Layer;
                                            if (reqCol.HasValue) oe.ColorIndex = ClampColor(reqCol.Value);
                                            ms.AppendEntity(oe);
                                            tr.AddNewlyCreatedDBObject(oe, true);
                                            createdIds.Add(oe.ObjectId);
                                            processedCount++;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        else if (op == "explode")
                        {
                            for (int i = 0; i < ids.Count; i++)
                            {
                                Entity src = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                                if (src == null || src.IsErased) continue;
                                DBObjectCollection parts = new DBObjectCollection();
                                try { src.Explode(parts); }
                                catch { continue; }
                                if (parts.Count == 0) continue;

                                foreach (DBObject p in parts)
                                {
                                    Entity pe = p as Entity;
                                    if (pe == null) continue;
                                    ms.AppendEntity(pe);
                                    tr.AddNewlyCreatedDBObject(pe, true);
                                    createdIds.Add(pe.ObjectId);
                                }
                                src.UpgradeOpen();
                                src.Erase(true);
                                erasedIds.Add(src.ObjectId);
                                processedCount++;
                            }
                        }
                        else
                        {
                            tr.Commit();
                            return Err("Unknown operation '" + op + "'.");
                        }
                    }

                    db.UpdateExt(true);
                    tr.Commit();
                }

                if (createdIds.Count > 0 || erasedIds.Count > 0 || snapById.Count > 0)
                {
                    CadUndoEntry undo = new CadUndoEntry();
                    undo.ToolName = "advanced_geometry_ops";
                    undo.Summary = op + " (" + processedCount.ToString(CultureInfo.InvariantCulture) + " items)";
                    undo.CreatedIds.AddRange(createdIds);
                    undo.ErasedIds.AddRange(erasedIds);
                    undo.ModifiedSnapshots.AddRange(snapById.Values);
                    PushUndoEntry(doc, undo);
                }

                if (zoom && resultExt.HasValue && IsFiniteBounds(resultExt.Value))
                    ZoomToExtents3d(doc, resultExt.Value, 1.25);
                else
                    doc.Editor.Regen();
                doc.Editor.UpdateScreen();
            }

            return Ok(new Dictionary<string, object>
            {
                { "operation", op },
                { "processed_count", processedCount },
                { "created_count", createdIds.Count },
                { "erased_count", erasedIds.Count },
                { "modified_count", snapById.Count },
                { "undo_history_steps", GetUndoHistoryCount(doc) }
            });
        }

        private static string QuantPt(double x, double y, double tol)
        {
            long ix = (long)Math.Round(x / tol);
            long iy = (long)Math.Round(y / tol);
            return ix.ToString(CultureInfo.InvariantCulture) + "," + iy.ToString(CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, object> DrawingQaCleanup(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            string action = (GetStr(args, "action") ?? "audit_duplicates").Trim().ToLowerInvariant();
            double tol = Math.Max(1e-5, GetDouble(args, "tolerance", 0.05));
            bool removeDuplicates = GetBool(args, "remove_duplicates", action == "overkill" || action == "full_cleanup");
            bool highlight = GetBool(args, "highlight", !removeDuplicates);
            bool ignoreLayer = GetBool(args, "ignore_layer", false);

            if (action == "check_env")
            {
                EnsureInteractiveFileDiaGuard();
                int fileDia = 1;
                int cmdDia = 1;
                int pickFirst = 1;
                try { fileDia = Convert.ToInt32(AcadApp.GetSystemVariable("FILEDIA"), CultureInfo.InvariantCulture); } catch { }
                try { cmdDia = Convert.ToInt32(AcadApp.GetSystemVariable("CMDDIA"), CultureInfo.InvariantCulture); } catch { }
                try { AcadApp.SetSystemVariable("PICKFIRST", 1); pickFirst = 1; } catch { }
                return Ok(new Dictionary<string, object>
                {
                    { "action", "check_env" },
                    { "FILEDIA", fileDia },
                    { "CMDDIA", cmdDia },
                    { "PICKFIRST", pickFirst }
                });
            }

            if (action == "compare_dwg")
            {
                string cmpPath = GetStr(args, "compare_file_path") ?? GetStr(args, "file_path");
                bool browse = GetBool(args, "browse", string.IsNullOrWhiteSpace(cmpPath));
                if (browse || string.IsNullOrWhiteSpace(cmpPath))
                {
                    using (OpenFileDialog dlg = new OpenFileDialog())
                    {
                        dlg.Title = "اختر ملف الأوتوكاد المرجعي للمقارنة (Select Reference DWG)";
                        dlg.Filter = "AutoCAD Drawing (*.dwg)|*.dwg";
                        if (dlg.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.FileName))
                            return Err("تم إلغاء اختيار ملف DWG للمقارنة.");
                        cmpPath = dlg.FileName;
                    }
                }
                if (!File.Exists(cmpPath))
                    return Err("ملف DWG المرجعي غير موجود: " + cmpPath);

                Dictionary<string, List<Point2d>> refBlocks = new Dictionary<string, List<Point2d>>(StringComparer.OrdinalIgnoreCase);
                using (Database refDb = new Database(false, true))
                {
                    refDb.ReadDwgFile(cmpPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    using (Transaction rtr = refDb.TransactionManager.StartTransaction())
                    {
                        BlockTable rbt = (BlockTable)rtr.GetObject(refDb.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord rms = (BlockTableRecord)rtr.GetObject(rbt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId rid in rms)
                        {
                            BlockReference rbr = rtr.GetObject(rid, OpenMode.ForRead) as BlockReference;
                            if (rbr == null || rbr.IsErased || IsAiAnnotationLayer(rbr.Layer)) continue;
                            string bn = ResolveBlockName(rtr, rbr);
                            if (IsAnonymousBlockName(bn) || IsSystemBlockName(bn)) continue;
                            try
                            {
                                Extents3d ex = rbr.GeometricExtents;
                                if (!IsFiniteBounds(ex)) continue;
                                Point2d pt = new Point2d((ex.MinPoint.X + ex.MaxPoint.X) * 0.5, (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5);
                                List<Point2d> lst;
                                if (!refBlocks.TryGetValue(bn, out lst)) { lst = new List<Point2d>(); refBlocks[bn] = lst; }
                                lst.Add(pt);
                            }
                            catch { }
                        }
                        rtr.Commit();
                    }
                }

                Dictionary<string, List<Point2d>> curBlocks = new Dictionary<string, List<Point2d>>(StringComparer.OrdinalIgnoreCase);
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null || br.IsErased || IsAiAnnotationLayer(br.Layer)) continue;
                        string bn = ResolveBlockName(tr, br);
                        if (IsAnonymousBlockName(bn) || IsSystemBlockName(bn)) continue;
                        try
                        {
                            Extents3d ex = br.GeometricExtents;
                            if (!IsFiniteBounds(ex)) continue;
                            Point2d pt = new Point2d((ex.MinPoint.X + ex.MaxPoint.X) * 0.5, (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5);
                            List<Point2d> lst;
                            if (!curBlocks.TryGetValue(bn, out lst)) { lst = new List<Point2d>(); curBlocks[bn] = lst; }
                            lst.Add(pt);
                        }
                        catch { }
                    }
                    tr.Commit();
                }

                HashSet<string> allNames = new HashSet<string>(refBlocks.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (string k in curBlocks.Keys) allNames.Add(k);

                List<KeyValuePair<string, Point2d>> added = new List<KeyValuePair<string, Point2d>>();
                List<KeyValuePair<string, Point2d>> removed = new List<KeyValuePair<string, Point2d>>();
                double matchTol = Math.Max(0.5, tol * 10.0);

                foreach (string bn in allNames)
                {
                    List<Point2d> rList = refBlocks.ContainsKey(bn) ? refBlocks[bn] : new List<Point2d>();
                    List<Point2d> cList = curBlocks.ContainsKey(bn) ? curBlocks[bn] : new List<Point2d>();
                    bool[] rMatched = new bool[rList.Count];
                    for (int ci = 0; ci < cList.Count; ci++)
                    {
                        int bestR = -1;
                        double bestD = double.MaxValue;
                        for (int ri = 0; ri < rList.Count; ri++)
                        {
                            if (rMatched[ri]) continue;
                            double d = cList[ci].GetDistanceTo(rList[ri]);
                            if (d <= matchTol && d < bestD) { bestD = d; bestR = ri; }
                        }
                        if (bestR >= 0) rMatched[bestR] = true;
                        else added.Add(new KeyValuePair<string, Point2d>(bn, cList[ci]));
                    }
                    for (int ri = 0; ri < rList.Count; ri++)
                    {
                        if (!rMatched[ri]) removed.Add(new KeyValuePair<string, Point2d>(bn, rList[ri]));
                    }
                }

                List<ObjectId> createdClouds = new List<ObjectId>();
                if (GetBool(args, "draw_clouds", true) && (added.Count > 0 || removed.Count > 0))
                {
                    using (DocumentLock docLock = doc.LockDocument())
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        string clLayer = EnsureLayerSafe(db, tr, "_AI_REV_CLOUD", 1, "Continuous");
                        double pad = Math.Max(5.0, Convert.ToDouble(GetUnitsInfo(db)["units_per_cm"], CultureInfo.InvariantCulture) * 5.0);
                        int maxClouds = 40;
                        for (int i = 0; i < Math.Min(maxClouds, added.Count); i++)
                        {
                            Point2d p = added[i].Value;
                            Polyline pl = BuildScallopedRevisionCloud(p.X - pad, p.Y - pad, p.X + pad, p.Y + pad, clLayer, 3);
                            ms.AppendEntity(pl);
                            tr.AddNewlyCreatedDBObject(pl, true);
                            createdClouds.Add(pl.ObjectId);
                        }
                        for (int i = 0; i < Math.Min(maxClouds, removed.Count); i++)
                        {
                            Point2d p = removed[i].Value;
                            Polyline pl = BuildScallopedRevisionCloud(p.X - pad, p.Y - pad, p.X + pad, p.Y + pad, clLayer, 1);
                            ms.AppendEntity(pl);
                            tr.AddNewlyCreatedDBObject(pl, true);
                            createdClouds.Add(pl.ObjectId);
                        }
                        tr.Commit();
                    }
                    if (createdClouds.Count > 0)
                    {
                        CadUndoEntry undo = new CadUndoEntry();
                        undo.ToolName = "drawing_qa_cleanup";
                        undo.Summary = "DWG Compare revision clouds";
                        undo.CreatedIds.AddRange(createdClouds);
                        PushUndoEntry(doc, undo);
                    }
                    doc.Editor.Regen();
                }

                Dictionary<string, object> cmpRes = new Dictionary<string, object>
                {
                    { "action", "compare_dwg" },
                    { "reference_file", cmpPath },
                    { "added_blocks_count", added.Count },
                    { "removed_blocks_count", removed.Count },
                    { "revision_clouds_drawn", createdClouds.Count }
                };

                if (GetBool(args, "export_excel", true))
                {
                    ExcelSheetData diffSheet = new ExcelSheetData
                    {
                        Title = "تقرير مقارنة اللوحات (DWG REVISION DIFF)",
                        SheetName = "Revision_Diff",
                        Headers = new List<string> { "الحالة", "اسم البلوك", "إحداثي X", "إحداثي Y" }
                    };
                    for (int i = 0; i < added.Count; i++)
                        diffSheet.Rows.Add(new List<string> { "مضاف (Added)", added[i].Key, Round(added[i].Value.X).ToString(CultureInfo.InvariantCulture), Round(added[i].Value.Y).ToString(CultureInfo.InvariantCulture) });
                    for (int i = 0; i < removed.Count; i++)
                        diffSheet.Rows.Add(new List<string> { "محذوف/منقول (Removed)", removed[i].Key, Round(removed[i].Value.X).ToString(CultureInfo.InvariantCulture), Round(removed[i].Value.Y).ToString(CultureInfo.InvariantCulture) });
                    if (diffSheet.Rows.Count == 0)
                        diffSheet.Rows.Add(new List<string> { "متطابق", "لا توجد فروقات في البلوكات", "", "" });
                    string saved = ExcelTableInterop.WriteSpreadsheet(ExcelTableInterop.ResolveDefaultExportPath(doc, "dwg_compare", ".xlsx"), diffSheet);
                    cmpRes["excel_exported"] = true;
                    cmpRes["excel_path"] = saved;
                }
                return Ok(cmpRes);
            }

            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();
            Dictionary<ObjectId, ModifiedEntitySnapshot> snapById = new Dictionary<ObjectId, ModifiedEntitySnapshot>();
            int dupBlocksFound = 0;
            int dupGeomFound = 0;
            int zeroLinesFound = 0;
            int purgedSymbols = 0;
            int standardizedCount = 0;

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    if (action == "audit_duplicates" || action == "full_cleanup")
                    {
                        Dictionary<string, ObjectId> seenBlockPos = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                        List<Extents3d> issueBoxes = new List<Extents3d>();

                        foreach (ObjectId id in ms)
                        {
                            if (id.IsErased) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;

                            Line ln = ent as Line;
                            if (ln != null && ln.Length <= 1e-5)
                            {
                                zeroLinesFound++;
                                if (removeDuplicates)
                                {
                                    ent.UpgradeOpen();
                                    ent.Erase(true);
                                    erasedIds.Add(id);
                                }
                                continue;
                            }

                            BlockReference br = ent as BlockReference;
                            if (br == null) continue;
                            string bn = ResolveBlockName(tr, br);
                            if (IsAnonymousBlockName(bn) || IsSystemBlockName(bn)) continue;

                            Extents3d ex;
                            try { ex = ent.GeometricExtents; }
                            catch { continue; }
                            if (!IsFiniteBounds(ex)) continue;

                            double cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                            double cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                            string bKey = bn + "|" + QuantPt(cx, cy, tol) + "|" + Math.Round(br.Rotation, 2).ToString(CultureInfo.InvariantCulture);

                            if (seenBlockPos.ContainsKey(bKey))
                            {
                                dupBlocksFound++;
                                issueBoxes.Add(ex);
                                if (removeDuplicates)
                                {
                                    ent.UpgradeOpen();
                                    ent.Erase(true);
                                    erasedIds.Add(id);
                                }
                            }
                            else
                            {
                                seenBlockPos[bKey] = id;
                            }
                        }

                        if (highlight && !removeDuplicates && issueBoxes.Count > 0)
                        {
                            string qaLayer = EnsureLayerSafe(db, tr, "_AI_QA_ISSUE", 1, "Continuous");
                            int cap = Math.Min(60, issueBoxes.Count);
                            for (int i = 0; i < cap; i++)
                            {
                                Extents3d ex = issueBoxes[i];
                                double pad = Math.Max(1.0, Math.Max(ex.MaxPoint.X - ex.MinPoint.X, ex.MaxPoint.Y - ex.MinPoint.Y) * 0.15);
                                Polyline frame = CreateFramePolyline(
                                    new Extents3d(new Point3d(ex.MinPoint.X - pad, ex.MinPoint.Y - pad, 0), new Point3d(ex.MaxPoint.X + pad, ex.MaxPoint.Y + pad, 0)),
                                    qaLayer, 1);
                                ms.AppendEntity(frame);
                                tr.AddNewlyCreatedDBObject(frame, true);
                                createdIds.Add(frame.ObjectId);
                            }
                        }
                    }

                    if (action == "overkill" || action == "full_cleanup")
                    {
                        HashSet<string> seenGeom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (ObjectId id in ms)
                        {
                            if (id.IsErased) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;
                            string lyrPrefix = ignoreLayer ? "" : ((ent.Layer ?? "0") + "|");
                            string sig = null;

                            Line ln = ent as Line;
                            if (ln != null)
                            {
                                string p1 = QuantPt(ln.StartPoint.X, ln.StartPoint.Y, tol);
                                string p2 = QuantPt(ln.EndPoint.X, ln.EndPoint.Y, tol);
                                sig = lyrPrefix + "L|" + (string.CompareOrdinal(p1, p2) <= 0 ? (p1 + "|" + p2) : (p2 + "|" + p1));
                            }
                            else if (ent is Circle)
                            {
                                Circle c = (Circle)ent;
                                sig = lyrPrefix + "C|" + QuantPt(c.Center.X, c.Center.Y, tol) + "|" + Math.Round(c.Radius / tol).ToString(CultureInfo.InvariantCulture);
                            }
                            else if (ent is Polyline)
                            {
                                Polyline pl = (Polyline)ent;
                                if (pl.NumberOfVertices >= 2)
                                {
                                    List<string> vKeys = new List<string>();
                                    for (int v = 0; v < pl.NumberOfVertices; v++)
                                    {
                                        Point2d vp = pl.GetPoint2dAt(v);
                                        vKeys.Add(QuantPt(vp.X, vp.Y, tol));
                                    }
                                    string fwd = string.Join(";", vKeys.ToArray());
                                    vKeys.Reverse();
                                    string rev = string.Join(";", vKeys.ToArray());
                                    sig = lyrPrefix + "P|" + (pl.Closed ? "1|" : "0|") + (string.CompareOrdinal(fwd, rev) <= 0 ? fwd : rev);
                                }
                            }

                            if (sig != null)
                            {
                                if (seenGeom.Contains(sig))
                                {
                                    dupGeomFound++;
                                    ent.UpgradeOpen();
                                    ent.Erase(true);
                                    erasedIds.Add(id);
                                }
                                else
                                {
                                    seenGeom.Add(sig);
                                }
                            }
                        }
                    }

                    if (action == "standardize_bylayer")
                    {
                        foreach (ObjectId id in ms)
                        {
                            if (id.IsErased) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;
                            if (ent.ColorIndex != 256 || !string.Equals(ent.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase))
                            {
                                CaptureEntitySnapshot(ent, snapById);
                                ent.UpgradeOpen();
                                ent.ColorIndex = 256;
                                try { ent.Linetype = "ByLayer"; } catch { }
                                ent.RecordGraphicsModified(true);
                                standardizedCount++;
                            }
                        }
                    }

                    if (action == "purge" || action == "full_cleanup")
                    {
                        ObjectId[] symTables = new ObjectId[] { db.BlockTableId, db.LayerTableId, db.TextStyleTableId, db.LinetypeTableId, db.DimStyleTableId };
                        for (int pass = 0; pass < 3; pass++)
                        {
                            int passPurged = 0;
                            for (int stIdx = 0; stIdx < symTables.Length; stIdx++)
                            {
                                SymbolTable st = tr.GetObject(symTables[stIdx], OpenMode.ForRead) as SymbolTable;
                                if (st == null) continue;
                                ObjectIdCollection cand = new ObjectIdCollection();
                                foreach (ObjectId sid in st) cand.Add(sid);
                                db.Purge(cand);
                                foreach (ObjectId pid in cand)
                                {
                                    try
                                    {
                                        SymbolTableRecord rec = tr.GetObject(pid, OpenMode.ForWrite) as SymbolTableRecord;
                                        if (rec != null && !rec.IsErased && !string.Equals(rec.Name, "0", StringComparison.OrdinalIgnoreCase))
                                        {
                                            rec.Erase(true);
                                            purgedSymbols++;
                                            passPurged++;
                                        }
                                    }
                                    catch { }
                                }
                            }
                            if (passPurged == 0) break;
                        }
                    }

                    db.UpdateExt(true);
                    tr.Commit();
                }

                if (createdIds.Count > 0 || erasedIds.Count > 0 || snapById.Count > 0)
                {
                    CadUndoEntry undo = new CadUndoEntry();
                    undo.ToolName = "drawing_qa_cleanup";
                    undo.Summary = action + " (erased=" + erasedIds.Count.ToString(CultureInfo.InvariantCulture) + ", purged=" + purgedSymbols.ToString(CultureInfo.InvariantCulture) + ")";
                    undo.CreatedIds.AddRange(createdIds);
                    undo.ErasedIds.AddRange(erasedIds);
                    undo.ModifiedSnapshots.AddRange(snapById.Values);
                    PushUndoEntry(doc, undo);
                }
                doc.Editor.Regen();
                doc.Editor.UpdateScreen();
            }

            return Ok(new Dictionary<string, object>
            {
                { "action", action },
                { "duplicate_blocks_found", dupBlocksFound },
                { "duplicate_geometry_removed", dupGeomFound },
                { "zero_length_lines_found", zeroLinesFound },
                { "purged_unused_symbols", purgedSymbols },
                { "standardized_bylayer_count", standardizedCount },
                { "highlight_markers_created", createdIds.Count },
                { "erased_count", erasedIds.Count },
                { "undo_history_steps", GetUndoHistoryCount(doc) }
            });
        }

        private static string PickMediaName(PlotSettingsValidator psv, PlotSettings ps, string paperKey)
        {
            string norm = (paperKey ?? "A3").Trim().ToUpperInvariant();
            StringCollection medias = psv.GetCanonicalMediaNameList(ps);
            List<string> candidates = new List<string>();
            if (norm == "A0") { candidates.Add("ISO_full_bleed_A0"); candidates.Add("ISO_A0"); candidates.Add("A0"); }
            else if (norm == "A1") { candidates.Add("ISO_full_bleed_A1"); candidates.Add("ISO_A1"); candidates.Add("A1"); }
            else if (norm == "A4") { candidates.Add("ISO_full_bleed_A4"); candidates.Add("ISO_A4"); candidates.Add("A4"); }
            else { candidates.Add("ISO_full_bleed_A3"); candidates.Add("ISO_A3"); candidates.Add("A3"); }

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                foreach (string m in medias)
                {
                    if (m != null && m.IndexOf(candidates[ci], StringComparison.OrdinalIgnoreCase) >= 0)
                        return m;
                }
            }
            return medias.Count > 0 ? medias[0] : "ISO_A3_(420.00_x_297.00_MM)";
        }

        private static Dictionary<string, object> LayoutAndPlotTools(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            string action = (GetStr(args, "action") ?? "list_layouts").Trim().ToLowerInvariant();

            if (action == "list_layouts")
            {
                List<object> layouts = new List<object>();
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBDictionary lDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry de in lDict)
                    {
                        Layout lay = tr.GetObject(de.Value, OpenMode.ForRead) as Layout;
                        if (lay == null) continue;
                        layouts.Add(new Dictionary<string, object>
                        {
                            { "name", lay.LayoutName },
                            { "tab_order", lay.TabOrder },
                            { "is_model", lay.ModelType },
                            { "plotter", lay.PlotConfigurationName },
                            { "paper_size", lay.CanonicalMediaName }
                        });
                    }
                    tr.Commit();
                }
                return Ok(new Dictionary<string, object> { { "action", "list_layouts" }, { "layouts", layouts } });
            }

            if (action == "create_layout")
            {
                string reqName = SanitizeSymbolName(GetStr(args, "layout_name") ?? "AI_Sheet_01");
                string paperSize = (GetStr(args, "paper_size") ?? "A3").Trim().ToUpperInvariant();
                bool landscape = GetBool(args, "landscape", true);
                double scaleDenom = Math.Max(0.0, GetDouble(args, "scale", 0.0));
                double[] viewBbox = GetDoubleArray(args, "view_bbox");
                string sheetTitle = GetStr(args, "title") ?? Path.GetFileNameWithoutExtension(doc.Name ?? "Drawing");

                string finalName = reqName;
                using (DocumentLock docLock = doc.LockDocument())
                {
                    LayoutManager lm = LayoutManager.Current;
                    int suffix = 1;
                    while (lm.LayoutExists(finalName))
                    {
                        finalName = reqName + "_" + (suffix++).ToString(CultureInfo.InvariantCulture);
                    }
                    ObjectId layId = lm.CreateLayout(finalName);
                    lm.CurrentLayout = finalName;

                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        Layout lay = (Layout)tr.GetObject(layId, OpenMode.ForWrite);
                        PlotSettingsValidator psv = PlotSettingsValidator.Current;
                        psv.SetPlotConfigurationName(lay, "DWG To PDF.pc3", null);
                        string media = PickMediaName(psv, lay, paperSize);
                        psv.SetCanonicalMediaName(lay, media);
                        psv.SetPlotPaperUnits(lay, PlotPaperUnit.Millimeters);
                        psv.SetPlotRotation(lay, landscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
                        psv.SetPlotType(lay, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);

                        double pw = 420.0, ph = 297.0;
                        if (paperSize == "A0") { pw = 1189.0; ph = 841.0; }
                        else if (paperSize == "A1") { pw = 841.0; ph = 594.0; }
                        else if (paperSize == "A4") { pw = 297.0; ph = 210.0; }
                        if (!landscape) { double tmp = pw; pw = ph; ph = tmp; }

                        BlockTableRecord psBtr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);
                        double margin = 12.0;
                        double tbH = 22.0;

                        // Outer Title Block border in PaperSpace
                        Polyline border = CreateFramePolyline(
                            new Extents3d(new Point3d(margin, margin, 0), new Point3d(pw - margin, ph - margin, 0)),
                            "0", 7);
                        psBtr.AppendEntity(border);
                        tr.AddNewlyCreatedDBObject(border, true);

                        Line tbLine = new Line(new Point3d(margin, margin + tbH, 0), new Point3d(pw - margin, margin + tbH, 0));
                        psBtr.AppendEntity(tbLine);
                        tr.AddNewlyCreatedDBObject(tbLine, true);

                        ObjectId uStyle = EnsureUnicodeTextStyle(db, tr);
                        MText tbTxt = CreateLegendCellMText(
                            psBtr, tr, uStyle, sheetTitle + " | Scale: " + (scaleDenom > 0 ? ("1:" + scaleDenom.ToString("0", CultureInfo.InvariantCulture)) : "Fit"),
                            pw * 0.5, margin + tbH * 0.5, pw - 2 * margin, tbH, 5.0, tbH, AttachmentPoint.MiddleCenter, 2, "0");

                        // Create Viewport fitting cleanly above Title Block
                        double vpW = pw - 2.0 * (margin + 3.0);
                        double vpH = ph - 2.0 * (margin + 3.0) - tbH;
                        Viewport vp = new Viewport();
                        vp.CenterPoint = new Point3d(pw * 0.5, margin + tbH + 3.0 + vpH * 0.5, 0.0);
                        vp.Width = vpW;
                        vp.Height = vpH;
                        psBtr.AppendEntity(vp);
                        tr.AddNewlyCreatedDBObject(vp, true);
                        vp.On = true;

                        // Target ModelSpace bounds or explicit view_bbox
                        Extents3d? targetExt = null;
                        if (viewBbox != null && viewBbox.Length >= 4)
                        {
                            targetExt = new Extents3d(new Point3d(viewBbox[0], viewBbox[1], 0), new Point3d(viewBbox[2], viewBbox[3], 0));
                        }
                        else
                        {
                            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                            foreach (ObjectId mid in ms)
                            {
                                Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                                if (me == null || me.IsErased) continue;
                                try
                                {
                                    Extents3d mex = me.GeometricExtents;
                                    if (!IsFiniteBounds(mex)) continue;
                                    if (!targetExt.HasValue) targetExt = mex;
                                    else { Extents3d u = targetExt.Value; u.AddExtents(mex); targetExt = u; }
                                }
                                catch { }
                            }
                        }

                        if (targetExt.HasValue)
                        {
                            double mw = Math.Max(1.0, targetExt.Value.MaxPoint.X - targetExt.Value.MinPoint.X);
                            double mh = Math.Max(1.0, targetExt.Value.MaxPoint.Y - targetExt.Value.MinPoint.Y);
                            vp.ViewDirection = Vector3d.ZAxis;
                            vp.ViewTarget = new Point3d((targetExt.Value.MinPoint.X + targetExt.Value.MaxPoint.X) * 0.5, (targetExt.Value.MinPoint.Y + targetExt.Value.MaxPoint.Y) * 0.5, 0.0);
                            vp.ViewCenter = Point2d.Origin;
                            if (scaleDenom > 0)
                            {
                                double unitsPerMm = Convert.ToDouble(GetUnitsInfo(db)["units_per_cm"], CultureInfo.InvariantCulture) / 10.0;
                                vp.CustomScale = 1.0 / (scaleDenom * Math.Max(1e-4, unitsPerMm));
                            }
                            else
                            {
                                vp.ViewHeight = Math.Max(mh * 1.12, (mw * 1.12) * (vpH / Math.Max(1.0, vpW)));
                            }
                        }
                        vp.Locked = true;
                        tr.Commit();
                    }
                    doc.Editor.Regen();
                }

                return Ok(new Dictionary<string, object>
                {
                    { "action", "create_layout" },
                    { "layout_name", finalName },
                    { "paper_size", paperSize },
                    { "landscape", landscape },
                    { "scale", scaleDenom > 0 ? ("1:" + scaleDenom.ToString("0", CultureInfo.InvariantCulture)) : "Fit" }
                });
            }

            if (action == "batch_plot_pdf")
            {
                if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                    return Err("يوجد أمر طباعة نشط حالياً في AutoCAD، انتظر انتهاءه ثم أعد المحاولة.");

                string outPdf = GetStr(args, "output_pdf") ?? GetStr(args, "file_path");
                if (string.IsNullOrWhiteSpace(outPdf))
                    outPdf = ExcelTableInterop.ResolveDefaultExportPath(doc, "sheets_plot", ".pdf");

                List<string> reqLayouts = ExtractStringListArg(args, "layouts");
                object oldBgPlot = null;
                try { oldBgPlot = AcadApp.GetSystemVariable("BACKGROUNDPLOT"); AcadApp.SetSystemVariable("BACKGROUNDPLOT", 0); } catch { }

                List<string> plottedLayouts = new List<string>();
                try
                {
                    using (DocumentLock docLock = doc.LockDocument())
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        DBDictionary lDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                        List<Layout> targets = new List<Layout>();
                        foreach (DBDictionaryEntry de in lDict)
                        {
                            Layout lay = tr.GetObject(de.Value, OpenMode.ForRead) as Layout;
                            if (lay == null || lay.ModelType) continue;
                            if (reqLayouts.Count > 0 && !reqLayouts.Contains(lay.LayoutName)) continue;
                            targets.Add(lay);
                        }
                        if (targets.Count == 0)
                        {
                            tr.Commit();
                            return Err("لا توجد PaperSpace Layouts للطباعة. أنشئ Layout أولاً عبر layout_and_plot_tools(action='create_layout').");
                        }

                        targets.Sort(delegate(Layout a, Layout b) { return a.TabOrder.CompareTo(b.TabOrder); });

                        using (PlotEngine pe = PlotFactory.CreatePublishEngine())
                        {
                            for (int i = 0; i < targets.Count; i++)
                            {
                                Layout lay = targets[i];
                                LayoutManager.Current.CurrentLayout = lay.LayoutName;
                                PlotInfo pi = new PlotInfo();
                                pi.Layout = lay.ObjectId;
                                PlotSettings ps = new PlotSettings(lay.ModelType);
                                ps.CopyFrom(lay);
                                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                                psv.SetPlotConfigurationName(ps, "DWG To PDF.pc3", PickMediaName(psv, ps, GetStr(args, "paper_size") ?? "A3"));
                                pi.OverrideSettings = ps;

                                PlotInfoValidator piv = new PlotInfoValidator();
                                piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                                piv.Validate(pi);

                                if (i == 0)
                                {
                                    pe.BeginPlot(null, null);
                                    pe.BeginDocument(pi, doc.Name, null, 1, true, outPdf);
                                }
                                PlotPageInfo ppi = new PlotPageInfo();
                                pe.BeginPage(ppi, pi, (i == targets.Count - 1), null);
                                pe.BeginGenerateGraphics(null);
                                pe.EndGenerateGraphics(null);
                                pe.EndPage(null);
                                plottedLayouts.Add(lay.LayoutName);
                                if (i == targets.Count - 1)
                                {
                                    pe.EndDocument(null);
                                    pe.EndPlot(null);
                                }
                            }
                        }
                        tr.Commit();
                    }
                }
                finally
                {
                    if (oldBgPlot != null) { try { AcadApp.SetSystemVariable("BACKGROUNDPLOT", oldBgPlot); } catch { } }
                }

                return Ok(new Dictionary<string, object>
                {
                    { "action", "batch_plot_pdf" },
                    { "output_pdf", outPdf },
                    { "plotted_layouts", plottedLayouts },
                    { "page_count", plottedLayouts.Count }
                });
            }

            return Err("Unknown layout_and_plot_tools action '" + action + "'. Use 'list_layouts', 'create_layout', or 'batch_plot_pdf'.");
        }

        private static string GetBlockLibraryDir(Dictionary<string, object> args)
        {
            string overrideDir = GetStr(args, "library_dir");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                Directory.CreateDirectory(overrideDir);
                return overrideDir;
            }
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIEngineer", "BlockLibrary");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static Dictionary<string, object> ManageBlockLibrary(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            string action = (GetStr(args, "action") ?? "list").Trim().ToLowerInvariant();
            string libDir = GetBlockLibraryDir(args);

            if (action == "list")
            {
                string[] dwgs = Directory.GetFiles(libDir, "*.dwg");
                JavaScriptSerializer js = new JavaScriptSerializer();
                List<object> entries = new List<object>();
                for (int i = 0; i < dwgs.Length; i++)
                {
                    string name = Path.GetFileNameWithoutExtension(dwgs[i]);
                    string metaPath = Path.ChangeExtension(dwgs[i], ".json");
                    Dictionary<string, object> item = new Dictionary<string, object>
                    {
                        { "block_name", name },
                        { "dwg_path", dwgs[i] },
                        { "size_bytes", new FileInfo(dwgs[i]).Length }
                    };
                    if (File.Exists(metaPath))
                    {
                        try
                        {
                            Dictionary<string, object> meta = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(metaPath));
                            if (meta != null)
                            {
                                if (meta.ContainsKey("description")) item["description"] = meta["description"];
                                if (meta.ContainsKey("category")) item["category"] = meta["category"];
                                if (meta.ContainsKey("units")) item["units"] = meta["units"];
                            }
                        }
                        catch { }
                    }
                    entries.Add(item);
                }
                return Ok(new Dictionary<string, object>
                {
                    { "action", "list" },
                    { "library_dir", libDir },
                    { "count", entries.Count },
                    { "blocks", entries }
                });
            }

            if (action == "save")
            {
                string reqBlock = GetStr(args, "block_name");
                string saveAs = GetStr(args, "save_as") ?? reqBlock;
                if (string.IsNullOrWhiteSpace(reqBlock))
                {
                    List<string> sel = GetSelectedHandleStrings(doc);
                    if (sel.Count > 0)
                    {
                        using (DocumentLock dl = doc.LockDocument())
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            ObjectId sid = ResolveHandle(db, sel[0]);
                            BlockReference sbr = (!sid.IsNull) ? (tr.GetObject(sid, OpenMode.ForRead) as BlockReference) : null;
                            if (sbr != null) reqBlock = ResolveBlockName(tr, sbr);
                            tr.Commit();
                        }
                    }
                }
                if (string.IsNullOrWhiteSpace(reqBlock))
                    return Err("حدد بلوك في الرسم أو مرّر block_name لحفظه في مكتبة البلوكات.");

                if (string.IsNullOrWhiteSpace(saveAs)) saveAs = reqBlock;
                string safeFileName = Regex.Replace(saveAs.Trim(), @"[<>:""/\\|?*]+", "_");
                string outDwg = Path.Combine(libDir, safeFileName + ".dwg");

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (!bt.Has(reqBlock))
                    {
                        tr.Commit();
                        return Err("البلوك '" + reqBlock + "' غير موجود في جدول البلوكات بالرسم.");
                    }
                    ObjectId defId = bt[reqBlock];
                    using (Database subDb = db.Wblock(defId))
                    {
                        subDb.SaveAs(outDwg, DwgVersion.Current);
                    }
                    tr.Commit();
                }

                Dictionary<string, object> meta = new Dictionary<string, object>
                {
                    { "block_name", safeFileName },
                    { "original_name", reqBlock },
                    { "description", GetStr(args, "description") ?? "" },
                    { "category", GetStr(args, "category") ?? "General" },
                    { "units", db.Insunits.ToString() },
                    { "saved_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
                };
                File.WriteAllText(Path.ChangeExtension(outDwg, ".json"), new JavaScriptSerializer().Serialize(meta));

                return Ok(new Dictionary<string, object>
                {
                    { "action", "save" },
                    { "block_name", safeFileName },
                    { "dwg_path", outDwg },
                    { "library_dir", libDir }
                });
            }

            if (action == "insert")
            {
                string reqBlock = GetStr(args, "block_name");
                if (string.IsNullOrWhiteSpace(reqBlock))
                    return Err("مرّر block_name المراد إدراجه من المكتبة.");

                string safeFileName = Regex.Replace(reqBlock.Trim(), @"[<>:""/\\|?*]+", "_");
                string dwgPath = Path.Combine(libDir, safeFileName + ".dwg");
                if (!File.Exists(dwgPath))
                    return Err("البلوك '" + reqBlock + "' غير موجود في مكتبة البلوكات (" + libDir + ").");

                double[] pt = GetDoubleArray(args, "insert_at") ?? new double[] { 0.0, 0.0 };
                double scale = Math.Max(1e-4, GetDouble(args, "scale", 1.0));
                double rotRad = GetDouble(args, "rotation_deg", 0.0) * Math.PI / 180.0;
                string targetLayer = GetStr(args, "layer") ?? "0";
                int? colorIdx = GetNullableInt(args, "color_index");
                bool zoom = GetBool(args, "zoom", true);

                List<ObjectId> createdIds = new List<ObjectId>();
                Extents3d? placedExt = null;

                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (Database srcDb = new Database(false, true))
                    {
                        srcDb.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                        ObjectId btrId = db.Insert(safeFileName, srcDb, true);

                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                            string safeLyr = EnsureLayerSafe(db, tr, targetLayer, null, "Continuous");

                            BlockReference br = new BlockReference(new Point3d(pt[0], pt.Length > 1 ? pt[1] : 0.0, 0.0), btrId);
                            br.Layer = safeLyr;
                            br.ScaleFactors = new Scale3d(scale, scale, scale);
                            br.Rotation = rotRad;
                            if (colorIdx.HasValue) br.ColorIndex = ClampColor(colorIdx.Value);
                            ms.AppendEntity(br);
                            tr.AddNewlyCreatedDBObject(br, true);
                            createdIds.Add(br.ObjectId);
                            try { placedExt = br.GeometricExtents; } catch { }

                            db.UpdateExt(true);
                            tr.Commit();
                        }
                    }

                    if (createdIds.Count > 0)
                    {
                        CadUndoEntry undo = new CadUndoEntry();
                        undo.ToolName = "manage_block_library";
                        undo.Summary = "Inserted library block '" + safeFileName + "'";
                        undo.CreatedIds.AddRange(createdIds);
                        PushUndoEntry(doc, undo);
                    }
                    if (zoom && placedExt.HasValue && IsFiniteBounds(placedExt.Value))
                        ZoomToExtents3d(doc, placedExt.Value, 2.5);
                    else
                        doc.Editor.Regen();
                    doc.Editor.UpdateScreen();
                }

                return Ok(new Dictionary<string, object>
                {
                    { "action", "insert" },
                    { "block_name", safeFileName },
                    { "created_count", createdIds.Count },
                    { "undo_history_steps", GetUndoHistoryCount(doc) }
                });
            }

            if (action == "delete")
            {
                string reqBlock = GetStr(args, "block_name");
                if (string.IsNullOrWhiteSpace(reqBlock)) return Err("مرّر block_name لحذفه من المكتبة.");
                string safeFileName = Regex.Replace(reqBlock.Trim(), @"[<>:""/\\|?*]+", "_");
                string dwgPath = Path.Combine(libDir, safeFileName + ".dwg");
                if (File.Exists(dwgPath)) File.Delete(dwgPath);
                string metaPath = Path.ChangeExtension(dwgPath, ".json");
                if (File.Exists(metaPath)) File.Delete(metaPath);
                return Ok(new Dictionary<string, object> { { "action", "delete" }, { "deleted", safeFileName } });
            }

            return Err("Unknown manage_block_library action '" + action + "'. Use 'save', 'list', 'insert', or 'delete'.");
        }
    }
}
