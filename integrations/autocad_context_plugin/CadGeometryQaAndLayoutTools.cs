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
using Autodesk.AutoCAD.EditorInput;
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

        private sealed class ArrowShaftCandidate
        {
            public ObjectId PrimaryId;
            public List<ObjectId> ExtraErasedIds = new List<ObjectId>();
            public string Handle;
            public string Layer;
            public Autodesk.AutoCAD.Colors.Color SourceColor;
            public int ColorKey;
            public List<Point3d> Pts;
            public double StartSegW0;
            public double StartSegW1;
            public double EndSegW0;
            public double EndSegW1;
            public bool IsOnCalloutLayer;
            public bool HasTaperedWidth;
            public bool IsRawLineSegment;
            public double TotalLength;
        }

        private sealed class ArrowHeadShape
        {
            public ObjectId PrimaryId;
            public List<ObjectId> ComponentIds = new List<ObjectId>();
            public string Kind;
            public string Layer;
            public Point2d Center;
            public List<Point2d> Vertices = new List<Point2d>();
            public Point2d? PreferredApex;
            public double Span;
        }

        private sealed class ArrowTextCandidate
        {
            public ObjectId Id;
            public string Text;
            public double Height;
            public Extents3d Bounds;
            public double Cx;
            public double Cy;
        }

        private sealed class ArrowBlockCandidate
        {
            public ObjectId Id;
            public string BlockName;
            public double Cx;
            public double Cy;
            public Extents3d Bounds;
        }

        private static double DistPointToBounds2D(double x, double y, Extents3d ex)
        {
            double dx = 0.0;
            if (x < ex.MinPoint.X) dx = ex.MinPoint.X - x;
            else if (x > ex.MaxPoint.X) dx = x - ex.MaxPoint.X;
            double dy = 0.0;
            if (y < ex.MinPoint.Y) dy = ex.MinPoint.Y - y;
            else if (y > ex.MaxPoint.Y) dy = y - ex.MaxPoint.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool IsTableOrTemporaryLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName)) return false;
            return string.Equals(layerName, "_AI_BLOCK_LEGEND", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_COORD_TABLE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_EXCEL_TABLE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_PANEL_SCHEDULE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_BOQ_TABLE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_LIGHTING_TABLE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_HIGHLIGHT", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, DrawingExecutionCommands.ReviewLayerName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDedicatedCalloutLayerName(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName)) return false;
            string up = layerName.Trim().ToUpperInvariant();
            return up == "_AI_COORD_CALLOUTS" || up == "_AI_COORDS" ||
                   up.Contains("CALLOUT") || up.Contains("LEADER") || up.Contains("ARROW") || up.Contains("COORD");
        }

        private static bool IsHorizontalSegment2D(Point3d a, Point3d b)
        {
            double dx = Math.Abs(b.X - a.X);
            double dy = Math.Abs(b.Y - a.Y);
            double len = Math.Sqrt(dx * dx + dy * dy);
            return len > 1e-6 && dx > 1e-6 && dy <= Math.Max(1e-5, dx * 0.08);
        }

        private static int ComputeColorKey(Entity ent)
        {
            if (ent == null || ent.Color == null) return 256;
            if (ent.Color.ColorMethod == ColorMethod.ByLayer) return 256;
            if (ent.Color.ColorMethod == ColorMethod.ByBlock) return 0;
            if (ent.Color.ColorMethod == ColorMethod.ByColor)
                return (1 << 24) | (ent.Color.Red << 16) | (ent.Color.Green << 8) | ent.Color.Blue;
            return ent.ColorIndex;
        }

        private static string FormatNumberMatchingDecimals(double val, string origToken, int defaultDecimals)
        {
            int dec = defaultDecimals;
            if (!string.IsNullOrEmpty(origToken))
            {
                int dot = origToken.IndexOf('.');
                if (dot >= 0) dec = Math.Max(0, Math.Min(6, origToken.Length - dot - 1));
            }
            return val.ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        private static string UpdateCoordinateValuesInText(string rawText, double tipX, double tipY, int defaultDecimals, out bool updated)
        {
            updated = false;
            if (string.IsNullOrWhiteSpace(rawText)) return rawText;

            bool replacedX = false;
            bool replacedY = false;

            string step1 = Regex.Replace(
                rawText,
                @"(?i)(^|\\P|[^A-Za-z0-9_\\])((?:X|E)\s*(?:[:=]\s*|\s+))(-?\d+(?:\.\d+)?)",
                delegate(Match m)
                {
                    replacedX = true;
                    string fmt = FormatNumberMatchingDecimals(tipX, m.Groups[3].Value, defaultDecimals);
                    return m.Groups[1].Value + m.Groups[2].Value + fmt;
                });

            string step2 = Regex.Replace(
                step1,
                @"(?i)(^|\\P|[^A-Za-z0-9_\\])((?:Y|N)\s*(?:[:=]\s*|\s+))(-?\d+(?:\.\d+)?)",
                delegate(Match m)
                {
                    replacedY = true;
                    string fmt = FormatNumberMatchingDecimals(tipY, m.Groups[3].Value, defaultDecimals);
                    return m.Groups[1].Value + m.Groups[2].Value + fmt;
                });

            if (replacedX || replacedY)
            {
                updated = true;
                return step2;
            }

            // Fallback: plain coordinate pair like "(123.45, 678.90)" or "123.45\P678.90"
            bool pairReplaced = false;
            string step3 = Regex.Replace(
                rawText,
                @"(-?\d+\.\d+)(\s*(?:,|\\P|/|;|\r?\n)\s*)(-?\d+\.\d+)",
                delegate(Match m)
                {
                    pairReplaced = true;
                    string fx = FormatNumberMatchingDecimals(tipX, m.Groups[1].Value, defaultDecimals);
                    string fy = FormatNumberMatchingDecimals(tipY, m.Groups[3].Value, defaultDecimals);
                    return fx + m.Groups[2].Value + fy;
                },
                RegexOptions.None);

            updated = pairReplaced;
            return updated ? step3 : rawText;
        }

        private static Dictionary<string, object> ConvertArrowsToLeaders(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            List<string> explicitHandles = GetStringList(args, "handles");
            bool hasExplicitMatchSimilar = args.ContainsKey("match_similar");
            bool matchSimilar = GetBool(args, "match_similar", false);
            bool selectedOnly = GetBool(args, "selected_only", false);
            string layerFilter = GetStr(args, "layer_filter") ?? GetStr(args, "layer");
            string targetLayerOverride = GetStr(args, "target_layer");
            double? explicitArrowSize = GetNullableDouble(args, "arrow_size");
            int? overrideColor = GetNullableInt(args, "color_index");
            bool keepSource = GetBool(args, "keep_source", false);
            bool clearHighlight = GetBool(args, "clear_highlight", true);
            bool updateCoordinates = GetBool(args, "update_coordinates", true);
            int coordDecimals = Math.Max(0, Math.Min(6, GetInt(args, "decimals", 2)));
            bool zoom = GetBool(args, "zoom", true);

            List<string> liveSel = GetSelectedHandleStrings(doc);
            if (!hasExplicitMatchSimilar && explicitHandles.Count == 0 && !selectedOnly && liveSel.Count > 0 && liveSel.Count <= 25)
            {
                // Engineer selected a small sample in AutoCAD and asked to convert all arrows like it
                matchSimilar = true;
            }
            if (matchSimilar && explicitHandles.Count == 0 && liveSel.Count == 0)
            {
                using (DocumentLock pickLock = doc.LockDocument())
                {
                    int cmdActive = 0;
                    try { cmdActive = Convert.ToInt32(AcadApp.GetSystemVariable("CMDACTIVE"), CultureInfo.InvariantCulture); } catch { }
                    if (cmdActive == 0)
                    {
                        TrySetFocusToDwgView(doc);
                        PromptSelectionOptions pso = new PromptSelectionOptions();
                        pso.MessageForAdding = "\n[AI-Engineer] اختر عينة سهم واحد (الخط الأفقي + الخط المائل + رأس السهم) واضغط Enter: ";
                        PromptSelectionResult psr = doc.Editor.GetSelection(pso);
                        if (psr != null && psr.Status == PromptStatus.OK && psr.Value != null)
                        {
                            using (Transaction ptr = db.TransactionManager.StartTransaction())
                            {
                                foreach (SelectedObject so in psr.Value)
                                {
                                    if (so == null || so.ObjectId.IsNull || so.ObjectId.IsErased) continue;
                                    Entity pe = ptr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                                    if (pe != null) liveSel.Add(pe.Handle.ToString());
                                }
                                ptr.Commit();
                            }
                        }
                    }
                }
                if (liveSel.Count == 0)
                    return Err("لم يتم تحديد عينة السهم في أوتوكاد. اختر أجزاء سهم واحد (الخط الأفقي + الخط المائل + رأس السهم) وأعد المحاولة.");
            }

            HashSet<string> targetSet = new HashSet<string>(explicitHandles, StringComparer.OrdinalIgnoreCase);
            if ((selectedOnly || matchSimilar) && targetSet.Count == 0)
            {
                for (int i = 0; i < liveSel.Count; i++) targetSet.Add(liveSel[i]);
                if (targetSet.Count == 0)
                    return Err("لا توجد أسهم محددة في أوتوكاد حالياً. حدد السهم/العينة أولاً ثم أعد المحاولة.");
            }
            bool restrictToExplicitSet = (targetSet.Count > 0) && !matchSimilar;

            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();
            List<string> createdHandles = new List<string>();
            List<object> convertedDetails = new List<object>();
            Dictionary<ObjectId, ModifiedEntitySnapshot> modifiedSnapshots = new Dictionary<ObjectId, ModifiedEntitySnapshot>();
            int alreadyLeaderCount = 0;
            int highlightErasedCount = 0;
            int coordsUpdatedCount = 0;
            Extents3d? convertedExt = null;
            Extents3d? drawingExt = null;
            bool switchedToModel = false;

            using (DocumentLock docLock = doc.LockDocument())
            {
                if (zoom && !db.TileMode)
                {
                    try { LayoutManager.Current.CurrentLayout = "Model"; switchedToModel = true; } catch { }
                }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);

                    List<ArrowHeadShape> headShapes = new List<ArrowHeadShape>();
                    List<ArrowTextCandidate> texts = new List<ArrowTextCandidate>();
                    List<ArrowBlockCandidate> blocks = new List<ArrowBlockCandidate>();
                    List<ArrowShaftCandidate> rawShafts = new List<ArrowShaftCandidate>();
                    List<ObjectId> highlightIds = new List<ObjectId>();
                    HashSet<ObjectId> initialSampleIds = new HashSet<ObjectId>();

                    foreach (ObjectId mid in ms)
                    {
                        if (mid.IsErased) continue;
                        Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;

                        if (string.Equals(ent.Layer, "_AI_HIGHLIGHT", StringComparison.OrdinalIgnoreCase))
                        {
                            highlightIds.Add(mid);
                            continue;
                        }
                        if (IsTableOrTemporaryLayer(ent.Layer)) continue;

                        if (targetSet.Contains(ent.Handle.ToString()))
                            initialSampleIds.Add(mid);

                        try
                        {
                            Extents3d dex = ent.GeometricExtents;
                            if (IsFiniteBounds(dex))
                            {
                                if (!drawingExt.HasValue) drawingExt = dex;
                                else { Extents3d c = drawingExt.Value; c.AddExtents(dex); drawingExt = c; }
                            }
                        }
                        catch { }

                        if (!IsEntityVisible(ent, layerHiddenFlags)) continue;

                        if (ent is Leader || ent is MLeader)
                        {
                            alreadyLeaderCount++;
                            continue;
                        }

                        if (ent is DBText || ent is MText)
                        {
                            string txt = ExtractEntityTextString(ent);
                            if (!string.IsNullOrWhiteSpace(txt))
                            {
                                try
                                {
                                    Extents3d tex = ent.GeometricExtents;
                                    if (IsFiniteBounds(tex))
                                    {
                                        double th = (ent is MText) ? ((MText)ent).TextHeight : ((DBText)ent).Height;
                                        texts.Add(new ArrowTextCandidate
                                        {
                                            Id = mid,
                                            Text = txt.Trim(),
                                            Height = Math.Max(1e-4, th),
                                            Bounds = tex,
                                            Cx = (tex.MinPoint.X + tex.MaxPoint.X) * 0.5,
                                            Cy = (tex.MinPoint.Y + tex.MaxPoint.Y) * 0.5
                                        });
                                    }
                                }
                                catch { }
                            }
                            continue;
                        }

                        BlockReference br = ent as BlockReference;
                        if (br != null)
                        {
                            string bn = ResolveBlockName(tr, br);
                            if (!IsAnonymousBlockName(bn) && !IsSystemBlockName(bn))
                            {
                                try
                                {
                                    Extents3d bex = br.GeometricExtents;
                                    if (IsFiniteBounds(bex))
                                    {
                                        blocks.Add(new ArrowBlockCandidate
                                        {
                                            Id = mid,
                                            BlockName = bn,
                                            Cx = (bex.MinPoint.X + bex.MaxPoint.X) * 0.5,
                                            Cy = (bex.MinPoint.Y + bex.MaxPoint.Y) * 0.5,
                                            Bounds = bex
                                        });
                                    }
                                }
                                catch { }
                            }
                            continue;
                        }

                        Solid s2 = ent as Solid;
                        if (s2 != null)
                        {
                            try
                            {
                                List<Point2d> vpts = new List<Point2d>();
                                double sx = 0, sy = 0;
                                for (short k = 0; k < 4; k++)
                                {
                                    Point3d p = s2.GetPointAt(k);
                                    Point2d p2 = new Point2d(p.X, p.Y);
                                    bool dup = false;
                                    for (int vi = 0; vi < vpts.Count; vi++)
                                        if (vpts[vi].GetDistanceTo(p2) < 1e-6) { dup = true; break; }
                                    if (!dup) { vpts.Add(p2); sx += p2.X; sy += p2.Y; }
                                }
                                if (vpts.Count >= 3)
                                {
                                    Extents3d sex = s2.GeometricExtents;
                                    double sSpan = IsFiniteBounds(sex)
                                        ? Math.Max(sex.MaxPoint.X - sex.MinPoint.X, sex.MaxPoint.Y - sex.MinPoint.Y)
                                        : 0.0;
                                    headShapes.Add(new ArrowHeadShape
                                    {
                                        PrimaryId = mid,
                                        ComponentIds = new List<ObjectId> { mid },
                                        Kind = "solid",
                                        Layer = ent.Layer ?? "0",
                                        Center = new Point2d(sx / vpts.Count, sy / vpts.Count),
                                        Vertices = vpts,
                                        Span = sSpan
                                    });
                                }
                            }
                            catch { }
                            continue;
                        }

                        Hatch ht = ent as Hatch;
                        if (ht != null)
                        {
                            try
                            {
                                Extents3d hex = ht.GeometricExtents;
                                if (IsFiniteBounds(hex))
                                {
                                    double hSpan = Math.Max(hex.MaxPoint.X - hex.MinPoint.X, hex.MaxPoint.Y - hex.MinPoint.Y);
                                    if (hSpan > 1e-6 && ht.NumberOfLoops >= 1)
                                    {
                                        HatchLoop loop = ht.GetLoopAt(0);
                                        List<Point2d> vpts = new List<Point2d>();
                                        if (loop.IsPolyline && loop.Polyline != null)
                                        {
                                            for (int vi = 0; vi < loop.Polyline.Count; vi++)
                                                vpts.Add(loop.Polyline[vi].Vertex);
                                        }
                                        else if (loop.Curves != null)
                                        {
                                            foreach (Curve2d c2 in loop.Curves)
                                                vpts.Add(c2.StartPoint);
                                        }
                                        if (vpts.Count >= 3 && vpts.Count <= 6)
                                        {
                                            double sx = 0, sy = 0;
                                            for (int vi = 0; vi < vpts.Count; vi++) { sx += vpts[vi].X; sy += vpts[vi].Y; }
                                            headShapes.Add(new ArrowHeadShape
                                            {
                                                PrimaryId = mid,
                                                ComponentIds = new List<ObjectId> { mid },
                                                Kind = "hatch",
                                                Layer = ent.Layer ?? "0",
                                                Center = new Point2d(sx / vpts.Count, sy / vpts.Count),
                                                Vertices = vpts,
                                                Span = hSpan
                                            });
                                        }
                                    }
                                }
                            }
                            catch { }
                            continue;
                        }

                        if (ent is Polyline)
                        {
                            Polyline pl = (Polyline)ent;
                            if (pl.Closed && (pl.NumberOfVertices == 3 || pl.NumberOfVertices == 4))
                            {
                                try
                                {
                                    Extents3d pex = pl.GeometricExtents;
                                    double pSpan = IsFiniteBounds(pex)
                                        ? Math.Max(pex.MaxPoint.X - pex.MinPoint.X, pex.MaxPoint.Y - pex.MinPoint.Y)
                                        : 0.0;
                                    if (pl.NumberOfVertices == 3 && pSpan > 1e-6)
                                    {
                                        List<Point2d> vpts = new List<Point2d>();
                                        double sx = 0, sy = 0;
                                        for (int vi = 0; vi < 3; vi++)
                                        {
                                            Point2d vp = pl.GetPoint2dAt(vi);
                                            vpts.Add(vp);
                                            sx += vp.X;
                                            sy += vp.Y;
                                        }
                                        headShapes.Add(new ArrowHeadShape
                                        {
                                            PrimaryId = mid,
                                            ComponentIds = new List<ObjectId> { mid },
                                            Kind = "poly_triangle",
                                            Layer = ent.Layer ?? "0",
                                            Center = new Point2d(sx / 3.0, sy / 3.0),
                                            Vertices = vpts,
                                            Span = pSpan
                                        });
                                        if (restrictToExplicitSet && !targetSet.Contains(ent.Handle.ToString()))
                                            continue;
                                        if (!restrictToExplicitSet) continue;
                                    }
                                }
                                catch { }
                            }
                        }

                        if (!(ent is Polyline) && !(ent is Polyline2d) && !(ent is Line))
                            continue;

                        string hStr = ent.Handle.ToString();
                        if (restrictToExplicitSet && !targetSet.Contains(hStr)) continue;
                        if (!string.IsNullOrWhiteSpace(layerFilter) && !MatchesPatternOrSubstring(ent.Layer ?? "", layerFilter)) continue;

                        List<Point3d> pts = new List<Point3d>();
                        double sw0 = 0, sw1 = 0, ew0 = 0, ew1 = 0;
                        bool isClosed = false;
                        bool hasTaper = false;

                        if (ent is Line)
                        {
                            Line ln = (Line)ent;
                            pts.Add(new Point3d(ln.StartPoint.X, ln.StartPoint.Y, 0.0));
                            pts.Add(new Point3d(ln.EndPoint.X, ln.EndPoint.Y, 0.0));
                        }
                        else if (ent is Polyline)
                        {
                            Polyline pl = (Polyline)ent;
                            isClosed = pl.Closed;
                            int n = pl.NumberOfVertices;
                            for (int vi = 0; vi < n; vi++)
                            {
                                Point3d vp = pl.GetPoint3dAt(vi);
                                Point3d p2 = new Point3d(vp.X, vp.Y, 0.0);
                                if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p2) > 1e-6)
                                    pts.Add(p2);
                            }
                            if (n >= 2)
                            {
                                try
                                {
                                    sw0 = pl.GetStartWidthAt(0);
                                    sw1 = pl.GetEndWidthAt(0);
                                    ew0 = pl.GetStartWidthAt(n - 2);
                                    ew1 = pl.GetEndWidthAt(n - 2);
                                    for (int si = 0; si < n - 1; si++)
                                    {
                                        double wA = pl.GetStartWidthAt(si);
                                        double wB = pl.GetEndWidthAt(si);
                                        if (Math.Abs(wA - wB) > 1e-4 && Math.Max(wA, wB) > 1e-4 &&
                                            Math.Min(wA, wB) <= Math.Max(wA, wB) * 0.35)
                                        {
                                            hasTaper = true;
                                            break;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        else if (ent is Polyline2d)
                        {
                            Polyline2d p2d = (Polyline2d)ent;
                            isClosed = p2d.Closed;
                            List<Vertex2d> vList = new List<Vertex2d>();
                            foreach (ObjectId vid in p2d)
                            {
                                Vertex2d vx = tr.GetObject(vid, OpenMode.ForRead) as Vertex2d;
                                if (vx == null) continue;
                                Point3d p2 = new Point3d(vx.Position.X, vx.Position.Y, 0.0);
                                if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p2) > 1e-6)
                                {
                                    pts.Add(p2);
                                    vList.Add(vx);
                                }
                            }
                            if (vList.Count >= 2)
                            {
                                sw0 = vList[0].StartWidth;
                                sw1 = vList[0].EndWidth;
                                ew0 = vList[vList.Count - 2].StartWidth;
                                ew1 = vList[vList.Count - 2].EndWidth;
                                for (int si = 0; si < vList.Count - 1; si++)
                                {
                                    double wA = vList[si].StartWidth;
                                    double wB = vList[si].EndWidth;
                                    if (Math.Abs(wA - wB) > 1e-4 && Math.Max(wA, wB) > 1e-4 &&
                                        Math.Min(wA, wB) <= Math.Max(wA, wB) * 0.35)
                                    {
                                        hasTaper = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (pts.Count < 2) continue;
                        if (isClosed && !restrictToExplicitSet) continue;

                        double totalLen = 0.0;
                        for (int vi = 1; vi < pts.Count; vi++) totalLen += pts[vi - 1].DistanceTo(pts[vi]);
                        if (totalLen <= 1e-6) continue;

                        rawShafts.Add(new ArrowShaftCandidate
                        {
                            PrimaryId = mid,
                            Handle = hStr,
                            Layer = ent.Layer ?? "0",
                            SourceColor = ent.Color,
                            ColorKey = ComputeColorKey(ent),
                            Pts = pts,
                            StartSegW0 = sw0,
                            StartSegW1 = sw1,
                            EndSegW0 = ew0,
                            EndSegW1 = ew1,
                            IsOnCalloutLayer = IsDedicatedCalloutLayerName(ent.Layer),
                            HasTaperedWidth = hasTaper,
                            IsRawLineSegment = (pts.Count == 2),
                            TotalLength = totalLen
                        });
                    }

                    double dwgSpan = drawingExt.HasValue
                        ? Math.Max(1.0, Math.Max(drawingExt.Value.MaxPoint.X - drawingExt.Value.MinPoint.X,
                                                 drawingExt.Value.MaxPoint.Y - drawingExt.Value.MinPoint.Y))
                        : 1000.0;

                    // 1b. If sample was selected, learn sample layers/colors/head-sizes and absorb touching head/shelf parts
                    HashSet<string> sampleLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    HashSet<int> sampleColors = new HashSet<int>();
                    HashSet<string> sampleHeadKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    double sampleHeadSpanSum = 0.0;
                    int sampleHeadSpanCount = 0;
                    bool sampleHasShelfOrMulti = false;
                    bool sampleHasTaper = false;

                    if (matchSimilar && initialSampleIds.Count > 0)
                    {
                        for (int i = 0; i < rawShafts.Count; i++)
                        {
                            if (!initialSampleIds.Contains(rawShafts[i].PrimaryId)) continue;
                            sampleLayers.Add(rawShafts[i].Layer);
                            sampleColors.Add(rawShafts[i].ColorKey);
                            if (rawShafts[i].HasTaperedWidth) sampleHasTaper = true;
                            if (rawShafts[i].Pts.Count >= 3 || IsHorizontalSegment2D(rawShafts[i].Pts[0], rawShafts[i].Pts[rawShafts[i].Pts.Count - 1]))
                                sampleHasShelfOrMulti = true;
                        }
                        for (int i = 0; i < headShapes.Count; i++)
                        {
                            if (!initialSampleIds.Contains(headShapes[i].PrimaryId)) continue;
                            sampleLayers.Add(headShapes[i].Layer);
                            sampleHeadKinds.Add(headShapes[i].Kind);
                            sampleHeadSpanSum += headShapes[i].Span;
                            sampleHeadSpanCount++;
                        }
                    }

                    // 2. Detect 2-Line Chevron & 3-Line Triangle arrowheads among short 2-pt segments BEFORE shaft merging
                    double maxHeadSegLen = (sampleHeadSpanCount > 0)
                        ? Math.Max(dwgSpan * 0.025, (sampleHeadSpanSum / sampleHeadSpanCount) * 3.5)
                        : Math.Max(1.0, dwgSpan * 0.035);
                    HashSet<int> consumedAsHeadSeg = new HashSet<int>();

                    for (int i = 0; i < rawShafts.Count; i++)
                    {
                        if (consumedAsHeadSeg.Contains(i)) continue;
                        ArrowShaftCandidate s1 = rawShafts[i];
                        if (!s1.IsRawLineSegment || s1.TotalLength > maxHeadSegLen) continue;
                        if (matchSimilar && sampleLayers.Count > 0 && !sampleLayers.Contains(s1.Layer)) continue;

                        double snap1 = Math.Max(1e-4, s1.TotalLength * 0.06);
                        Point3d a1 = s1.Pts[0], b1 = s1.Pts[1];

                        for (int j = i + 1; j < rawShafts.Count; j++)
                        {
                            if (consumedAsHeadSeg.Contains(j)) continue;
                            ArrowShaftCandidate s2 = rawShafts[j];
                            if (!s2.IsRawLineSegment || s2.TotalLength > maxHeadSegLen) continue;
                            if (!string.Equals(s1.Layer, s2.Layer, StringComparison.OrdinalIgnoreCase)) continue;
                            double lenRatio = s1.TotalLength / Math.Max(1e-9, s2.TotalLength);
                            if (lenRatio < 0.45 || lenRatio > 2.25) continue;

                            Point3d a2 = s2.Pts[0], b2 = s2.Pts[1];
                            double jt = Math.Max(snap1, s2.TotalLength * 0.06);
                            Point3d apex = Point3d.Origin, open1 = Point3d.Origin, open2 = Point3d.Origin;
                            bool shareApex = false;
                            if (a1.DistanceTo(a2) <= jt) { apex = a1; open1 = b1; open2 = b2; shareApex = true; }
                            else if (a1.DistanceTo(b2) <= jt) { apex = a1; open1 = b1; open2 = a2; shareApex = true; }
                            else if (b1.DistanceTo(a2) <= jt) { apex = b1; open1 = a1; open2 = b2; shareApex = true; }
                            else if (b1.DistanceTo(b2) <= jt) { apex = b1; open1 = a1; open2 = a2; shareApex = true; }
                            if (!shareApex) continue;

                            Vector3d v1 = (open1 - apex);
                            Vector3d v2 = (open2 - apex);
                            if (v1.Length <= 1e-6 || v2.Length <= 1e-6) continue;
                            double cosA = Math.Max(-1.0, Math.Min(1.0, v1.GetNormal().DotProduct(v2.GetNormal())));
                            double angDeg = Math.Acos(cosA) * (180.0 / Math.PI);
                            if (angDeg < 8.0 || angDeg > 105.0) continue;

                            // Check if a 3rd short segment closes the base triangle open1 -> open2
                            int thirdIdx = -1;
                            for (int k = 0; k < rawShafts.Count; k++)
                            {
                                if (k == i || k == j || consumedAsHeadSeg.Contains(k)) continue;
                                ArrowShaftCandidate s3 = rawShafts[k];
                                if (!s3.IsRawLineSegment || s3.TotalLength > maxHeadSegLen * 1.4) continue;
                                if (!string.Equals(s1.Layer, s3.Layer, StringComparison.OrdinalIgnoreCase)) continue;
                                double tTol = Math.Max(jt, s3.TotalLength * 0.08);
                                if ((s3.Pts[0].DistanceTo(open1) <= tTol && s3.Pts[1].DistanceTo(open2) <= tTol) ||
                                    (s3.Pts[1].DistanceTo(open1) <= tTol && s3.Pts[0].DistanceTo(open2) <= tTol))
                                {
                                    thirdIdx = k;
                                    break;
                                }
                            }

                            if (thirdIdx >= 0)
                            {
                                consumedAsHeadSeg.Add(i);
                                consumedAsHeadSeg.Add(j);
                                consumedAsHeadSeg.Add(thirdIdx);
                                List<ObjectId> cids = new List<ObjectId> { s1.PrimaryId, s2.PrimaryId, rawShafts[thirdIdx].PrimaryId };
                                double hSpan = Math.Max(apex.DistanceTo(open1), Math.Max(apex.DistanceTo(open2), open1.DistanceTo(open2)));
                                Point2d ctr = new Point2d((apex.X + open1.X + open2.X) / 3.0, (apex.Y + open1.Y + open2.Y) / 3.0);
                                ArrowHeadShape hsTri = new ArrowHeadShape
                                {
                                    PrimaryId = s1.PrimaryId,
                                    ComponentIds = cids,
                                    Kind = "line_triangle",
                                    Layer = s1.Layer,
                                    Center = ctr,
                                    Vertices = new List<Point2d> { new Point2d(apex.X, apex.Y), new Point2d(open1.X, open1.Y), new Point2d(open2.X, open2.Y) },
                                    PreferredApex = new Point2d(apex.X, apex.Y),
                                    Span = hSpan
                                };
                                headShapes.Add(hsTri);
                                if (initialSampleIds.Contains(s1.PrimaryId) || initialSampleIds.Contains(s2.PrimaryId) || initialSampleIds.Contains(rawShafts[thirdIdx].PrimaryId))
                                {
                                    sampleHeadKinds.Add("line_triangle");
                                    sampleHeadSpanSum += hSpan;
                                    sampleHeadSpanCount++;
                                }
                                break;
                            }

                            // Otherwise check if a longer shaft meets apex OR midpoint of open1..open2 along bisector
                            Point3d baseMid = new Point3d((open1.X + open2.X) * 0.5, (open1.Y + open2.Y) * 0.5, 0.0);
                            bool touchesShaft = false;
                            for (int m = 0; m < rawShafts.Count; m++)
                            {
                                if (m == i || m == j || consumedAsHeadSeg.Contains(m)) continue;
                                ArrowShaftCandidate shM = rawShafts[m];
                                if (shM.TotalLength <= Math.Max(s1.TotalLength, s2.TotalLength) * 1.1) continue;
                                Point3d m0 = shM.Pts[0];
                                Point3d mLast = shM.Pts[shM.Pts.Count - 1];
                                double mtol = Math.Max(jt, Math.Max(s1.TotalLength, s2.TotalLength) * 0.25);
                                if (m0.DistanceTo(apex) <= mtol || mLast.DistanceTo(apex) <= mtol ||
                                    m0.DistanceTo(baseMid) <= mtol || mLast.DistanceTo(baseMid) <= mtol)
                                {
                                    touchesShaft = true;
                                    break;
                                }
                            }
                            if (touchesShaft)
                            {
                                consumedAsHeadSeg.Add(i);
                                consumedAsHeadSeg.Add(j);
                                List<ObjectId> cids = new List<ObjectId> { s1.PrimaryId, s2.PrimaryId };
                                double hSpan = Math.Max(apex.DistanceTo(open1), apex.DistanceTo(open2));
                                Point2d ctr = new Point2d((apex.X + open1.X + open2.X) / 3.0, (apex.Y + open1.Y + open2.Y) / 3.0);
                                ArrowHeadShape hsChev = new ArrowHeadShape
                                {
                                    PrimaryId = s1.PrimaryId,
                                    ComponentIds = cids,
                                    Kind = "line_chevron",
                                    Layer = s1.Layer,
                                    Center = ctr,
                                    Vertices = new List<Point2d> { new Point2d(apex.X, apex.Y), new Point2d(open1.X, open1.Y), new Point2d(open2.X, open2.Y) },
                                    PreferredApex = new Point2d(apex.X, apex.Y),
                                    Span = hSpan
                                };
                                headShapes.Add(hsChev);
                                if (initialSampleIds.Contains(s1.PrimaryId) || initialSampleIds.Contains(s2.PrimaryId))
                                {
                                    sampleHeadKinds.Add("line_chevron");
                                    sampleHeadSpanSum += hSpan;
                                    sampleHeadSpanCount++;
                                }
                                break;
                            }
                        }
                    }

                    // Filter rawShafts removing segments already classified as arrowhead lines
                    List<ArrowShaftCandidate> shafts = new List<ArrowShaftCandidate>();
                    for (int i = 0; i < rawShafts.Count; i++)
                    {
                        if (!consumedAsHeadSeg.Contains(i))
                            shafts.Add(rawShafts[i]);
                    }

                    double avgSampleHeadSpan = sampleHeadSpanCount > 0 ? (sampleHeadSpanSum / sampleHeadSpanCount) : 0.0;
                    double headAttachTol = avgSampleHeadSpan > 1e-5
                        ? Math.Max(1e-3, avgSampleHeadSpan * 1.65)
                        : Math.Max(1e-3, dwgSpan * 0.008);

                    // 3. Merge Pointer Shaft + Horizontal Shelf Shaft (endpoint join OR interior T-junction on horizontal line)
                    HashSet<int> mergedShelfIndices = new HashSet<int>();
                    for (int i = 0; i < shafts.Count; i++)
                    {
                        if (mergedShelfIndices.Contains(i)) continue;
                        ArrowShaftCandidate a = shafts[i];
                        if (a.Pts.Count != 2) continue;
                        if (IsHorizontalSegment2D(a.Pts[0], a.Pts[1])) continue;
                        if (matchSimilar && sampleLayers.Count > 0 && !sampleLayers.Contains(a.Layer)) continue;

                        Point3d aStart = a.Pts[0], aEnd = a.Pts[1];
                        int bestShelfIdx = -1;
                        double bestJoinDist = double.MaxValue;
                        List<Point3d> bestMergedPts = null;

                        for (int j = 0; j < shafts.Count; j++)
                        {
                            if (i == j || mergedShelfIndices.Contains(j)) continue;
                            ArrowShaftCandidate b = shafts[j];
                            if (b.Pts.Count != 2) continue;
                            if (!string.Equals(a.Layer, b.Layer, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!IsHorizontalSegment2D(b.Pts[0], b.Pts[1])) continue;

                            Point3d bStart = b.Pts[0], bEnd = b.Pts[1];
                            double linkTol = Math.Max(1e-3, Math.Min(aStart.DistanceTo(aEnd), bStart.DistanceTo(bEnd)) * 0.09);

                            // Check endpoint joins first, then interior T-junction on horizontal shelf b
                            Point2d bs2 = new Point2d(bStart.X, bStart.Y);
                            Point2d be2 = new Point2d(bEnd.X, bEnd.Y);
                            double dAStartToB = PointToSegmentDist2D(new Point2d(aStart.X, aStart.Y), bs2, be2);
                            double dAEndToB = PointToSegmentDist2D(new Point2d(aEnd.X, aEnd.Y), bs2, be2);

                            double minD = Math.Min(dAStartToB, dAEndToB);
                            if (minD > linkTol || minD >= bestJoinDist) continue;

                            bool endMeetsShelf = (dAEndToB <= dAStartToB);
                            Point3d tipCand = endMeetsShelf ? aStart : aEnd;
                            Point3d joinCand = endMeetsShelf ? aEnd : aStart;

                            // Project joinCand onto segment bStart..bEnd for exact elbow
                            Vector2d bVec = be2 - bs2;
                            double bLen2 = bVec.X * bVec.X + bVec.Y * bVec.Y;
                            double tProj = bLen2 > 1e-12
                                ? Math.Max(0.0, Math.Min(1.0, ((joinCand.X - bs2.X) * bVec.X + (joinCand.Y - bs2.Y) * bVec.Y) / bLen2))
                                : 0.0;
                            Point3d elbowCand = new Point3d(bs2.X + tProj * bVec.X, bs2.Y + tProj * bVec.Y, 0.0);

                            // Pick far shelf end (if T-junction near center, pick whichever side sits under text or longer half)
                            Point3d shelfEndCand = (elbowCand.DistanceTo(bEnd) >= elbowCand.DistanceTo(bStart)) ? bEnd : bStart;
                            for (int ti = 0; ti < texts.Count; ti++)
                            {
                                ArrowTextCandidate tx = texts[ti];
                                double dShelfMid = DistPointToBounds2D((bStart.X + bEnd.X) * 0.5, bStart.Y, tx.Bounds);
                                if (dShelfMid <= tx.Height * 2.5)
                                {
                                    shelfEndCand = (tx.Cx >= elbowCand.X)
                                        ? ((bEnd.X >= bStart.X) ? bEnd : bStart)
                                        : ((bStart.X <= bEnd.X) ? bStart : bEnd);
                                    break;
                                }
                            }

                            bestJoinDist = minD;
                            bestShelfIdx = j;
                            bestMergedPts = new List<Point3d> { tipCand, elbowCand, shelfEndCand };
                        }

                        if (bestShelfIdx >= 0 && bestMergedPts != null)
                        {
                            a.Pts = bestMergedPts;
                            a.ExtraErasedIds.Add(shafts[bestShelfIdx].PrimaryId);
                            mergedShelfIndices.Add(bestShelfIdx);
                            sampleHasShelfOrMulti = sampleHasShelfOrMulti ||
                                initialSampleIds.Contains(a.PrimaryId) ||
                                initialSampleIds.Contains(shafts[bestShelfIdx].PrimaryId);
                        }
                    }

                    // 4. Qualify & Convert Matching Assemblies
                    bool explicitScope = restrictToExplicitSet || !string.IsNullOrWhiteSpace(layerFilter);
                    HashSet<ObjectId> consumedHeadPrimaryIds = new HashSet<ObjectId>();
                    HashSet<ObjectId> updatedTextIds = new HashSet<ObjectId>();

                    for (int i = 0; i < shafts.Count; i++)
                    {
                        if (mergedShelfIndices.Contains(i)) continue;
                        ArrowShaftCandidate sh = shafts[i];

                        if (matchSimilar)
                        {
                            if (sampleLayers.Count > 0 && !sampleLayers.Contains(sh.Layer)) continue;
                            if (sampleColors.Count > 0 && !sampleColors.Contains(sh.ColorKey)) continue;
                        }

                        List<Point3d> pts = sh.Pts;
                        Point3d pStart = pts[0];
                        Point3d pEnd = pts[pts.Count - 1];

                        // Find attached arrowhead at Start or End
                        ArrowHeadShape startHead = null, endHead = null;
                        double bestStartHd = double.MaxValue, bestEndHd = double.MaxValue;
                        for (int hi = 0; hi < headShapes.Count; hi++)
                        {
                            ArrowHeadShape hs = headShapes[hi];
                            if (consumedHeadPrimaryIds.Contains(hs.PrimaryId)) continue;
                            if (matchSimilar && avgSampleHeadSpan > 1e-5)
                            {
                                if (hs.Span < avgSampleHeadSpan * 0.25 || hs.Span > avgSampleHeadSpan * 4.0) continue;
                            }
                            double ht = Math.Max(headAttachTol, hs.Span * 1.6);
                            double ds = Math.Sqrt(Math.Pow(pStart.X - hs.Center.X, 2) + Math.Pow(pStart.Y - hs.Center.Y, 2));
                            double de = Math.Sqrt(Math.Pow(pEnd.X - hs.Center.X, 2) + Math.Pow(pEnd.Y - hs.Center.Y, 2));
                            for (int vi = 0; vi < hs.Vertices.Count; vi++)
                            {
                                ds = Math.Min(ds, Math.Sqrt(Math.Pow(pStart.X - hs.Vertices[vi].X, 2) + Math.Pow(pStart.Y - hs.Vertices[vi].Y, 2)));
                                de = Math.Min(de, Math.Sqrt(Math.Pow(pEnd.X - hs.Vertices[vi].X, 2) + Math.Pow(pEnd.Y - hs.Vertices[vi].Y, 2)));
                            }
                            if (hs.Vertices.Count >= 3)
                            {
                                for (int vi = 0; vi < hs.Vertices.Count; vi++)
                                {
                                    Point2d va = hs.Vertices[vi];
                                    Point2d vb = hs.Vertices[(vi + 1) % hs.Vertices.Count];
                                    ds = Math.Min(ds, PointToSegmentDist2D(new Point2d(pStart.X, pStart.Y), va, vb));
                                    de = Math.Min(de, PointToSegmentDist2D(new Point2d(pEnd.X, pEnd.Y), va, vb));
                                }
                            }
                            if (ds <= ht && ds < bestStartHd) { bestStartHd = ds; startHead = hs; }
                            if (de <= ht && de < bestEndHd) { bestEndHd = de; endHead = hs; }
                        }

                        // Find nearest text label near Start vs End/Shelf
                        ArrowTextCandidate bestStartTxt = null, bestEndTxt = null;
                        double dStartTxt = double.MaxValue, dEndTxt = double.MaxValue;
                        for (int ti = 0; ti < texts.Count; ti++)
                        {
                            ArrowTextCandidate tx = texts[ti];
                            double ds = DistPointToBounds2D(pStart.X, pStart.Y, tx.Bounds);
                            double de = DistPointToBounds2D(pEnd.X, pEnd.Y, tx.Bounds);
                            if (pts.Count >= 3 && IsHorizontalSegment2D(pts[pts.Count - 2], pts[pts.Count - 1]))
                            {
                                double dMid = DistPointToBounds2D((pts[pts.Count - 2].X + pEnd.X) * 0.5, (pts[pts.Count - 2].Y + pEnd.Y) * 0.5, tx.Bounds);
                                de = Math.Min(de, dMid);
                            }
                            if (pts.Count >= 3 && IsHorizontalSegment2D(pts[0], pts[1]))
                            {
                                double dMid = DistPointToBounds2D((pts[0].X + pts[1].X) * 0.5, (pts[0].Y + pts[1].Y) * 0.5, tx.Bounds);
                                ds = Math.Min(ds, dMid);
                            }
                            if (ds < dStartTxt) { dStartTxt = ds; bestStartTxt = tx; }
                            if (de < dEndTxt) { dEndTxt = de; bestEndTxt = tx; }
                        }

                        bool endNearText = bestEndTxt != null && dEndTxt <= Math.Max(bestEndTxt.Height * 3.2, dwgSpan * 0.015);
                        bool startNearText = bestStartTxt != null && dStartTxt <= Math.Max(bestStartTxt.Height * 3.2, dwgSpan * 0.015);
                        bool hasHorizEndShelf = pts.Count >= 3 && IsHorizontalSegment2D(pts[pts.Count - 2], pts[pts.Count - 1]);
                        bool hasHorizStartShelf = pts.Count >= 3 && IsHorizontalSegment2D(pts[0], pts[1]);

                        if (matchSimilar)
                        {
                            if (sampleHeadKinds.Count > 0 && startHead == null && endHead == null && !sh.HasTaperedWidth)
                                continue;
                            if (sampleHasShelfOrMulti && pts.Count < 3 && startHead == null && endHead == null)
                                continue;
                            if (sampleHasTaper && !sh.HasTaperedWidth && startHead == null && endHead == null)
                                continue;
                        }
                        else if (!explicitScope)
                        {
                            bool looksLikeCalloutOrArrow =
                                sh.IsOnCalloutLayer ||
                                sh.HasTaperedWidth ||
                                startHead != null ||
                                endHead != null ||
                                sh.ExtraErasedIds.Count > 0 ||
                                (pts.Count <= 5 && ((hasHorizEndShelf && endNearText && !startNearText) ||
                                                    (hasHorizStartShelf && startNearText && !endNearText)));
                            if (!looksLikeCalloutOrArrow) continue;
                        }

                        // Score Start vs End as pointed arrowhead TIP
                        int startTipScore = 0;
                        int endTipScore = 0;
                        double detectedHeadLen = 0.0;

                        if (sh.HasTaperedWidth)
                        {
                            if (sh.StartSegW0 < sh.StartSegW1 * 0.35 && sh.StartSegW1 > 1e-4)
                            {
                                startTipScore += 12;
                                detectedHeadLen = Math.Max(detectedHeadLen, pts[0].DistanceTo(pts[1]));
                            }
                            if (sh.EndSegW1 < sh.EndSegW0 * 0.35 && sh.EndSegW0 > 1e-4)
                            {
                                endTipScore += 12;
                                detectedHeadLen = Math.Max(detectedHeadLen, pts[pts.Count - 2].DistanceTo(pts[pts.Count - 1]));
                            }
                        }

                        if (startHead != null) { startTipScore += 14; detectedHeadLen = Math.Max(detectedHeadLen, startHead.Span); }
                        if (endHead != null) { endTipScore += 14; detectedHeadLen = Math.Max(detectedHeadLen, endHead.Span); }

                        if (endNearText && !startNearText) startTipScore += 8;
                        else if (startNearText && !endNearText) endTipScore += 8;

                        if (hasHorizEndShelf && !hasHorizStartShelf) startTipScore += 5;
                        else if (hasHorizStartShelf && !hasHorizEndShelf) endTipScore += 5;

                        if (blocks.Count > 0 && startTipScore == endTipScore)
                        {
                            double bDistStart = double.MaxValue, bDistEnd = double.MaxValue;
                            for (int bi = 0; bi < blocks.Count; bi++)
                            {
                                bDistStart = Math.Min(bDistStart, DistPointToBounds2D(pStart.X, pStart.Y, blocks[bi].Bounds));
                                bDistEnd = Math.Min(bDistEnd, DistPointToBounds2D(pEnd.X, pEnd.Y, blocks[bi].Bounds));
                            }
                            if (bDistStart < bDistEnd * 0.7) startTipScore += 3;
                            else if (bDistEnd < bDistStart * 0.7) endTipScore += 3;
                        }

                        bool reversePts = (endTipScore > startTipScore);
                        List<Point3d> orderedPts = new List<Point3d>(pts);
                        ArrowHeadShape usedHead = reversePts ? endHead : startHead;
                        ArrowTextCandidate pairedText = reversePts ? bestStartTxt : bestEndTxt;
                        double pairedTextDist = reversePts ? dStartTxt : dEndTxt;

                        if (reversePts) orderedPts.Reverse();

                        // Drop redundant collinear arrowhead-cone vertex in tapered Polylines
                        if (sh.HasTaperedWidth && orderedPts.Count >= 3)
                        {
                            Vector3d v0 = orderedPts[1] - orderedPts[0];
                            Vector3d v1 = orderedPts[2] - orderedPts[1];
                            if (v0.Length > 1e-6 && v1.Length > 1e-6)
                            {
                                double cosAng = v0.GetNormal().DotProduct(v1.GetNormal());
                                double headWidthMax = reversePts
                                    ? Math.Max(sh.EndSegW0, sh.EndSegW1)
                                    : Math.Max(sh.StartSegW0, sh.StartSegW1);
                                if (cosAng > 0.995 && v0.Length <= Math.Max(1e-4, headWidthMax * 4.5))
                                {
                                    detectedHeadLen = Math.Max(detectedHeadLen, v0.Length);
                                    orderedPts.RemoveAt(1);
                                }
                            }
                        }

                        // Snap Vertex 0 onto the exact sharp forward apex of the attached arrowhead shape!
                        if (usedHead != null && orderedPts.Count >= 2)
                        {
                            if (usedHead.PreferredApex.HasValue)
                            {
                                orderedPts[0] = new Point3d(usedHead.PreferredApex.Value.X, usedHead.PreferredApex.Value.Y, 0.0);
                            }
                            else if (usedHead.Vertices.Count >= 3)
                            {
                                Point3d elbowRef = orderedPts[1];
                                Vector2d outDir = new Vector2d(orderedPts[0].X - elbowRef.X, orderedPts[0].Y - elbowRef.Y);
                                if (outDir.Length > 1e-7)
                                {
                                    outDir = outDir.GetNormal();
                                    double bestProj = double.MinValue;
                                    Point2d bestV = usedHead.Vertices[0];
                                    for (int vi = 0; vi < usedHead.Vertices.Count; vi++)
                                    {
                                        Point2d hv = usedHead.Vertices[vi];
                                        double proj = (hv.X - elbowRef.X) * outDir.X + (hv.Y - elbowRef.Y) * outDir.Y;
                                        if (proj > bestProj) { bestProj = proj; bestV = hv; }
                                    }
                                    orderedPts[0] = new Point3d(bestV.X, bestV.Y, 0.0);
                                }
                            }
                            detectedHeadLen = Math.Max(detectedHeadLen, usedHead.Span);
                        }

                        string safeLyr = !string.IsNullOrWhiteSpace(targetLayerOverride)
                            ? EnsureLayerSafe(db, tr, targetLayerOverride.Trim(), overrideColor.HasValue ? (short?)ClampColor(overrideColor.Value) : null, null)
                            : sh.Layer;

                        double firstLegLen = orderedPts.Count >= 2 ? orderedPts[0].DistanceTo(orderedPts[1]) : 1.0;
                        bool hasClosePairedText = pairedText != null && pairedTextDist <= Math.Max(pairedText.Height * 3.5, dwgSpan * 0.02);
                        double textHRef = hasClosePairedText
                            ? pairedText.Height
                            : Math.Max(1e-4, firstLegLen * 0.09);

                        double dimasz = explicitArrowSize.HasValue
                            ? Math.Max(1e-4, explicitArrowSize.Value)
                            : (detectedHeadLen > 1e-4
                                ? Math.Min(firstLegLen * 0.48, Math.Max(detectedHeadLen, 1e-4))
                                : Math.Max(textHRef * 1.15, Math.Min(firstLegLen * 0.42, Math.Max(textHRef * 1.55, firstLegLen * 0.16))));
                        if (dimasz <= 1e-4) dimasz = Math.Max(1e-4, firstLegLen * 0.2);

                        Leader ldr = new Leader();
                        ldr.SetDatabaseDefaults(db);
                        ldr.Layer = safeLyr;
                        if (overrideColor.HasValue)
                        {
                            short aci = (short)ClampColor(overrideColor.Value);
                            ldr.ColorIndex = aci;
                            ldr.Dimclrd = Color.FromColorIndex(ColorMethod.ByAci, aci);
                        }
                        else if (sh.SourceColor != null)
                        {
                            ldr.Color = sh.SourceColor;
                            try { ldr.Dimclrd = sh.SourceColor; } catch { }
                        }
                        ldr.DimensionStyle = db.Dimstyle;
                        try { ldr.Dimldrblk = ObjectId.Null; } catch { }
                        ldr.HasArrowHead = true;
                        ldr.IsSplined = false;
                        ldr.Dimasz = dimasz;

                        int appendedCount = 0;
                        Point3d prevAppended = Point3d.Origin;
                        for (int vi = 0; vi < orderedPts.Count; vi++)
                        {
                            Point3d vpt = new Point3d(orderedPts[vi].X, orderedPts[vi].Y, 0.0);
                            if (appendedCount == 0 || vpt.DistanceTo(prevAppended) > 1e-6)
                            {
                                ldr.AppendVertex(vpt);
                                prevAppended = vpt;
                                appendedCount++;
                            }
                        }
                        if (appendedCount < 2)
                        {
                            ldr.Dispose();
                            continue;
                        }

                        ObjectId ldrId = ms.AppendEntity(ldr);
                        tr.AddNewlyCreatedDBObject(ldr, true);

                        // Update X/Y coordinates inside paired text to exact Vertex 0 arrowhead tip coordinates!
                        string finalPairedText = hasClosePairedText ? pairedText.Text : "";
                        bool thisTextUpdated = false;
                        if (updateCoordinates && hasClosePairedText && !updatedTextIds.Contains(pairedText.Id))
                        {
                            try
                            {
                                Entity txEnt = tr.GetObject(pairedText.Id, OpenMode.ForWrite) as Entity;
                                if (txEnt is MText)
                                {
                                    MText mt = (MText)txEnt;
                                    bool changed;
                                    string updatedContents = UpdateCoordinateValuesInText(mt.Contents, orderedPts[0].X, orderedPts[0].Y, coordDecimals, out changed);
                                    if (changed && !string.Equals(updatedContents, mt.Contents, StringComparison.Ordinal))
                                    {
                                        CaptureEntitySnapshot(mt, modifiedSnapshots);
                                        mt.Contents = updatedContents;
                                        updatedTextIds.Add(pairedText.Id);
                                        coordsUpdatedCount++;
                                        thisTextUpdated = true;
                                        finalPairedText = ExtractEntityTextString(mt).Trim();
                                    }
                                }
                                else if (txEnt is DBText)
                                {
                                    DBText dt = (DBText)txEnt;
                                    bool changed;
                                    string updatedStr = UpdateCoordinateValuesInText(dt.TextString, orderedPts[0].X, orderedPts[0].Y, coordDecimals, out changed);
                                    if (changed && !string.Equals(updatedStr, dt.TextString, StringComparison.Ordinal))
                                    {
                                        CaptureEntitySnapshot(dt, modifiedSnapshots);
                                        dt.TextString = updatedStr;
                                        updatedTextIds.Add(pairedText.Id);
                                        coordsUpdatedCount++;
                                        thisTextUpdated = true;
                                        finalPairedText = updatedStr;
                                    }
                                }
                            }
                            catch { }
                        }

                        // Attach AIENG_COORD_LDR XData if paired with numbered coordinate callout near a BlockReference
                        if (hasClosePairedText && blocks.Count > 0)
                        {
                            int parsedIdx;
                            string headerTok;
                            if (ParseLeadingCalloutIndex(pairedText.Text, out parsedIdx, out headerTok) && parsedIdx > 0)
                            {
                                ArrowBlockCandidate nearBlk = null;
                                double bestBd = double.MaxValue;
                                Point3d tipPt = orderedPts[0];
                                for (int bi = 0; bi < blocks.Count; bi++)
                                {
                                    double bd = DistPointToBounds2D(tipPt.X, tipPt.Y, blocks[bi].Bounds);
                                    if (bd < bestBd) { bestBd = bd; nearBlk = blocks[bi]; }
                                }
                                if (nearBlk != null)
                                {
                                    double blkSpan = Math.Max(nearBlk.Bounds.MaxPoint.X - nearBlk.Bounds.MinPoint.X,
                                                              nearBlk.Bounds.MaxPoint.Y - nearBlk.Bounds.MinPoint.Y);
                                    if (bestBd <= Math.Max(dimasz * 2.5, blkSpan * 0.65))
                                    {
                                        AttachCoordLeaderXData(db, tr, ldr, new CoordTargetItem
                                        {
                                            Index = parsedIdx,
                                            BlockIndex = parsedIdx,
                                            SubPointIndex = 0,
                                            SubPointLabel = string.IsNullOrEmpty(headerTok) ? ("#" + parsedIdx.ToString(CultureInfo.InvariantCulture)) : headerTok,
                                            BlockName = nearBlk.BlockName,
                                            Handle = tr.GetObject(nearBlk.Id, OpenMode.ForRead).Handle.ToString()
                                        });
                                    }
                                }
                            }
                        }

                        createdIds.Add(ldrId);
                        createdHandles.Add(ldr.Handle.ToString());

                        Extents3d lEx = new Extents3d(
                            new Point3d(Math.Min(orderedPts[0].X, orderedPts[orderedPts.Count - 1].X),
                                        Math.Min(orderedPts[0].Y, orderedPts[orderedPts.Count - 1].Y), 0.0),
                            new Point3d(Math.Max(orderedPts[0].X, orderedPts[orderedPts.Count - 1].X),
                                        Math.Max(orderedPts[0].Y, orderedPts[orderedPts.Count - 1].Y), 0.0));
                        if (!convertedExt.HasValue) convertedExt = lEx;
                        else { Extents3d c = convertedExt.Value; c.AddExtents(lEx); convertedExt = c; }

                        if (convertedDetails.Count < 80)
                        {
                            convertedDetails.Add(new Dictionary<string, object>
                            {
                                { "source_handle", sh.Handle },
                                { "leader_handle", ldr.Handle.ToString() },
                                { "layer", safeLyr },
                                { "tip", Pt2(orderedPts[0].X, orderedPts[0].Y) },
                                { "tail", Pt2(orderedPts[orderedPts.Count - 1].X, orderedPts[orderedPts.Count - 1].Y) },
                                { "arrow_size", Round(dimasz) },
                                { "coordinates_updated", thisTextUpdated },
                                { "paired_text", finalPairedText }
                            });
                        }

                        if (!keepSource)
                        {
                            try
                            {
                                Entity srcEnt = tr.GetObject(sh.PrimaryId, OpenMode.ForWrite) as Entity;
                                if (srcEnt != null && !srcEnt.IsErased)
                                {
                                    srcEnt.Erase(true);
                                    erasedIds.Add(sh.PrimaryId);
                                }
                            }
                            catch { }

                            for (int ei = 0; ei < sh.ExtraErasedIds.Count; ei++)
                            {
                                ObjectId eid = sh.ExtraErasedIds[ei];
                                if (eid.IsNull || eid.IsErased) continue;
                                try
                                {
                                    Entity secEnt = tr.GetObject(eid, OpenMode.ForWrite) as Entity;
                                    if (secEnt != null && !secEnt.IsErased)
                                    {
                                        secEnt.Erase(true);
                                        erasedIds.Add(eid);
                                    }
                                }
                                catch { }
                            }

                            if (usedHead != null && consumedHeadPrimaryIds.Add(usedHead.PrimaryId))
                            {
                                for (int hci = 0; hci < usedHead.ComponentIds.Count; hci++)
                                {
                                    ObjectId hid = usedHead.ComponentIds[hci];
                                    if (hid.IsNull || hid.IsErased) continue;
                                    try
                                    {
                                        Entity hdEnt = tr.GetObject(hid, OpenMode.ForWrite) as Entity;
                                        if (hdEnt != null && !hdEnt.IsErased)
                                        {
                                            hdEnt.Erase(true);
                                            erasedIds.Add(hid);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }

                    if (clearHighlight && highlightIds.Count > 0)
                    {
                        for (int hi = 0; hi < highlightIds.Count; hi++)
                        {
                            try
                            {
                                Entity he = tr.GetObject(highlightIds[hi], OpenMode.ForWrite) as Entity;
                                if (he != null && !he.IsErased)
                                {
                                    he.Erase(true);
                                    erasedIds.Add(highlightIds[hi]);
                                    highlightErasedCount++;
                                }
                            }
                            catch { }
                        }
                    }

                    db.UpdateExt(true);
                    tr.Commit();
                }

                if (createdIds.Count > 0 || erasedIds.Count > 0 || modifiedSnapshots.Count > 0)
                {
                    CadUndoEntry undo = new CadUndoEntry();
                    undo.ToolName = "convert_arrows_to_leaders";
                    undo.Summary = "Converted " + createdIds.Count.ToString(CultureInfo.InvariantCulture) + " arrow(s) to native Leaders";
                    undo.ErasedIds.AddRange(erasedIds);
                    undo.CreatedIds.AddRange(createdIds);
                    undo.ModifiedSnapshots.AddRange(modifiedSnapshots.Values);
                    PushUndoEntry(doc, undo);
                }

                if (zoom && convertedExt.HasValue && IsFiniteBounds(convertedExt.Value))
                    ZoomToExtents3d(doc, convertedExt.Value, 1.2);
                else
                    doc.Editor.Regen();
                doc.Editor.UpdateScreen();
            }

            if (createdIds.Count == 0 && highlightErasedCount == 0)
            {
                if (matchSimilar)
                    return Err("لم يتم العثور على أسهم مطابقة للعينة المحددة في المخطط. تأكد من تحديد أجزاء سهم واحد (الخط الأفقي + الخط المائل + رأس السهم) وأعد المحاولة.");
                return Err("لم يتم العثور على أسهم مرسومة مطابقة (Polyline/Line + رأس السهم). حدد سهم عينة في أوتوكاد ثم مرر match_similar=true.");
            }

            return Ok(new Dictionary<string, object>
            {
                { "converted_count", createdIds.Count },
                { "coordinates_updated_count", coordsUpdatedCount },
                { "already_leader_count", alreadyLeaderCount },
                { "erased_source_count", erasedIds.Count },
                { "highlight_boxes_removed", highlightErasedCount },
                { "matched_from_sample", matchSimilar },
                { "leader_handles", createdHandles },
                { "details", convertedDetails },
                { "switched_to_model", switchedToModel },
                { "undo_history_steps", GetUndoHistoryCount(doc) }
            });
        }
    }
}
