using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using GdiColor = System.Drawing.Color;
using GdiFont = System.Drawing.Font;
using CadColor = Autodesk.AutoCAD.Colors.Color;

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        // ==================== Phase 4: High-Resolution Vector Visual Capture ====================

        private sealed class VisualBadgeCandidate
        {
            public string Label;
            public double WorldCx;
            public double WorldCy;
            public double WorldMaxY;
            public int Score;
        }

        private static string CleanMTextForVisual(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw;
            s = s.Replace("\\P", "\n").Replace("\\p", "\n");
            s = Regex.Replace(s, @"\{\\f[^;]*;", "");
            s = Regex.Replace(s, @"\\[CcHhWwTtQqAaFf][^;\\]*;", "");
            s = Regex.Replace(s, @"\\[LlOoKk~]", "");
            s = s.Replace("{", "").Replace("}", "");
            s = Regex.Replace(s, @"%%[uUoO]", "");
            s = Regex.Replace(s, @"%%[cC]", "Ø");
            s = Regex.Replace(s, @"%%[dD]", "°");
            s = Regex.Replace(s, @"%%[pP]", "±");
            s = Regex.Replace(s, @"[ \t]+", " ");
            return s.Trim();
        }

        private static GdiColor ResolveGdiColor(
            Entity ent,
            Dictionary<string, CadColor> layerCadColors,
            GdiColor parentBlockColor)
        {
            try
            {
                if (ent == null) return GdiColor.FromArgb(220, 228, 236);
                CadColor clr = ent.Color;
                if (clr != null)
                {
                    if (clr.ColorMethod == ColorMethod.ByBlock)
                        return parentBlockColor;
                    if (clr.ColorMethod == ColorMethod.ByLayer)
                    {
                        CadColor lClr;
                        if (layerCadColors != null && !string.IsNullOrEmpty(ent.Layer) &&
                            layerCadColors.TryGetValue(ent.Layer, out lClr) && lClr != null)
                        {
                            return CadColorToVisibleGdi(lClr);
                        }
                        return GdiColor.FromArgb(220, 228, 236);
                    }
                    return CadColorToVisibleGdi(clr);
                }
            }
            catch { }
            return GdiColor.FromArgb(220, 228, 236);
        }

        private static GdiColor CadColorToVisibleGdi(CadColor clr)
        {
            if (clr == null) return GdiColor.FromArgb(220, 228, 236);
            if (clr.ColorMethod == ColorMethod.ByAci)
            {
                short idx = clr.ColorIndex;
                switch (idx)
                {
                    case 1: return GdiColor.FromArgb(255, 75, 75);
                    case 2: return GdiColor.FromArgb(255, 240, 75);
                    case 3: return GdiColor.FromArgb(75, 245, 115);
                    case 4: return GdiColor.FromArgb(70, 240, 255);
                    case 5: return GdiColor.FromArgb(95, 160, 255);
                    case 6: return GdiColor.FromArgb(255, 100, 255);
                    case 7: return GdiColor.FromArgb(235, 242, 248);
                    case 8: return GdiColor.FromArgb(145, 155, 170);
                    case 9: return GdiColor.FromArgb(195, 205, 215);
                }
            }
            try
            {
                GdiColor raw = clr.ColorValue;
                int r = raw.R, g = raw.G, b = raw.B;
                if (r + g + b < 130)
                {
                    int boost = (150 - (r + g + b)) / 3;
                    r = Math.Min(235, r + boost + 55);
                    g = Math.Min(235, g + boost + 55);
                    b = Math.Min(235, b + boost + 55);
                }
                return GdiColor.FromArgb(255, r, g, b);
            }
            catch
            {
                return GdiColor.FromArgb(220, 228, 236);
            }
        }

        private static bool VisualBoxesOverlap(double minX1, double minY1, double maxX1, double maxY1,
            double minX2, double minY2, double maxX2, double maxY2)
        {
            return !(maxX1 < minX2 || minX1 > maxX2 || maxY1 < minY2 || minY1 > maxY2);
        }

        private static bool GetActiveGuiViewportBounds(out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = maxX = maxY = 0.0;
            try
            {
                object rawCtr = Application.GetSystemVariable("VIEWCTR");
                object rawSize = Application.GetSystemVariable("VIEWSIZE");
                if (!(rawCtr is Point3d) || rawSize == null) return false;
                Point3d ctr = (Point3d)rawCtr;
                double viewH = Convert.ToDouble(rawSize, CultureInfo.InvariantCulture);
                if (double.IsNaN(viewH) || double.IsInfinity(viewH) || viewH <= 1e-6) return false;

                double aspect = 16.0 / 9.0;
                try
                {
                    object rawScr = Application.GetSystemVariable("SCREENSIZE");
                    if (rawScr is Point2d)
                    {
                        Point2d scr = (Point2d)rawScr;
                        if (scr.X > 10.0 && scr.Y > 10.0) aspect = scr.X / scr.Y;
                    }
                }
                catch { }
                aspect = Math.Max(0.4, Math.Min(3.2, aspect));
                double halfH = viewH * 0.5;
                double halfW = halfH * aspect;
                minX = ctr.X - halfW;
                maxX = ctr.X + halfW;
                minY = ctr.Y - halfH;
                maxY = ctr.Y + halfH;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static PointF WorldToPixel(
            double wx, double wy,
            double capMinX, double capMaxY,
            double offsetX, double offsetY, double scale)
        {
            float px = (float)(offsetX + (wx - capMinX) * scale);
            float py = (float)(offsetY + (capMaxY - wy) * scale);
            return new PointF(px, py);
        }

        private static void DrawVisualPolylineVertexSpan(
            Graphics g, Pen pen,
            Point3d p1, Point3d p2, double bulge,
            double capMinX, double capMaxY, double offsetX, double offsetY, double scale,
            ref int primitivesDrawn)
        {
            if (Math.Abs(bulge) <= 1e-4)
            {
                PointF a = WorldToPixel(p1.X, p1.Y, capMinX, capMaxY, offsetX, offsetY, scale);
                PointF b = WorldToPixel(p2.X, p2.Y, capMinX, capMaxY, offsetX, offsetY, scale);
                g.DrawLine(pen, a, b);
                primitivesDrawn++;
                return;
            }
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord <= 1e-7) return;
            double theta = 4.0 * Math.Atan(Math.Abs(bulge));
            double sinHalf = Math.Sin(theta * 0.5);
            if (Math.Abs(sinHalf) <= 1e-6)
            {
                g.DrawLine(pen,
                    WorldToPixel(p1.X, p1.Y, capMinX, capMaxY, offsetX, offsetY, scale),
                    WorldToPixel(p2.X, p2.Y, capMinX, capMaxY, offsetX, offsetY, scale));
                primitivesDrawn++;
                return;
            }
            double radius = (chord * 0.5) / sinHalf;
            double midX = (p1.X + p2.X) * 0.5;
            double midY = (p1.Y + p2.Y) * 0.5;
            double apothem = radius * Math.Cos(theta * 0.5);
            double sign = bulge >= 0.0 ? 1.0 : -1.0;
            // For positive bulge, center is to the left of p1->p2 if theta < pi, right if theta > pi
            double perpSign = (Math.Abs(bulge) <= 1.0) ? sign : -sign;
            double nx = -dy / chord;
            double ny = dx / chord;
            double cx = midX + nx * apothem * perpSign;
            double cy = midY + ny * apothem * perpSign;
            double startAng = Math.Atan2(p1.Y - cy, p1.X - cx);
            double sweep = theta * sign;
            const int segs = 12;
            PointF[] pts = new PointF[segs + 1];
            for (int si = 0; si <= segs; si++)
            {
                double ang = startAng + sweep * ((double)si / segs);
                double wx = cx + radius * Math.Cos(ang);
                double wy = cy + radius * Math.Sin(ang);
                pts[si] = WorldToPixel(wx, wy, capMinX, capMaxY, offsetX, offsetY, scale);
            }
            g.DrawLines(pen, pts);
            primitivesDrawn++;
        }

        private static void RenderEntityRecursiveForVisual(
            Transaction tr,
            Entity ent,
            Matrix3d xform,
            int depth,
            Dictionary<string, bool> layerHiddenMap,
            Dictionary<string, CadColor> layerColorMap,
            GdiColor parentBlockColor,
            double capMinX, double capMinY, double capMaxX, double capMaxY,
            double offsetX, double offsetY, double scale,
            Graphics g,
            ref int primitivesDrawn,
            ref int textsDrawn,
            int maxPrimitives,
            bool trackTopBlocks,
            Dictionary<string, int> blockCountsInView,
            List<VisualBadgeCandidate> badgeCandidates)
        {
            if (ent == null || ent.IsErased || depth > 4 || primitivesDrawn >= maxPrimitives) return;
            if (depth == 0 && !IsEntityVisible(ent, layerHiddenMap)) return;

            if (depth == 0)
            {
                try
                {
                    Extents3d ex = ent.GeometricExtents;
                    if (!IsFiniteBounds(ex)) return;
                    if (!VisualBoxesOverlap(ex.MinPoint.X, ex.MinPoint.Y, ex.MaxPoint.X, ex.MaxPoint.Y,
                        capMinX, capMinY, capMaxX, capMaxY))
                        return;
                }
                catch { return; }
            }

            GdiColor col = ResolveGdiColor(ent, layerColorMap, parentBlockColor);
            float penWidth = (depth == 0) ? 1.45f : 1.20f;

            BlockReference br = ent as BlockReference;
            if (br != null)
            {
                string bn = ResolveBlockName(tr, br);
                if (string.IsNullOrEmpty(bn) || IsSystemBlockName(bn) || string.Equals(ent.Layer, "ASHADE", StringComparison.OrdinalIgnoreCase))
                    return;

                if (trackTopBlocks && depth == 0 && !IsAnonymousBlockName(bn) && !IsLegendOrHighlightLayer(ent.Layer) &&
                    !IsPanelOrFrameBlockName(bn))
                {
                    int cur = 0;
                    blockCountsInView.TryGetValue(bn, out cur);
                    blockCountsInView[bn] = cur + 1;

                    try
                    {
                        Extents3d bex = br.GeometricExtents;
                        if (IsFiniteBounds(bex))
                        {
                            double bSpan = Math.Max(bex.MaxPoint.X - bex.MinPoint.X, bex.MaxPoint.Y - bex.MinPoint.Y);
                            if (bSpan * scale >= 6.0 && bSpan < (capMaxX - capMinX) * 0.45)
                            {
                                badgeCandidates.Add(new VisualBadgeCandidate
                                {
                                    Label = bn,
                                    WorldCx = (bex.MinPoint.X + bex.MaxPoint.X) * 0.5,
                                    WorldCy = (bex.MinPoint.Y + bex.MaxPoint.Y) * 0.5,
                                    WorldMaxY = bex.MaxPoint.Y,
                                    Score = IsCircuitTerminatorArrowBlock(bn) ? 1 : 10
                                });
                            }
                        }
                    }
                    catch { }
                }

                Matrix3d nextXform = xform * br.BlockTransform;
                ObjectId defId = GetBlockDefinitionId(br);
                if (!defId.IsNull)
                {
                    BlockTableRecord subBtr = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                    if (subBtr != null)
                    {
                        foreach (ObjectId subId in subBtr)
                        {
                            if (subId.IsErased || primitivesDrawn >= maxPrimitives) break;
                            Entity subEnt = tr.GetObject(subId, OpenMode.ForRead) as Entity;
                            if (subEnt == null || subEnt.IsErased || !subEnt.Visible) continue;
                            RenderEntityRecursiveForVisual(
                                tr, subEnt, nextXform, depth + 1,
                                layerHiddenMap, layerColorMap, col,
                                capMinX, capMinY, capMaxX, capMaxY,
                                offsetX, offsetY, scale,
                                g, ref primitivesDrawn, ref textsDrawn, maxPrimitives,
                                false, blockCountsInView, badgeCandidates);
                        }
                    }
                }

                foreach (ObjectId attId in br.AttributeCollection)
                {
                    if (attId.IsErased || primitivesDrawn >= maxPrimitives) break;
                    try
                    {
                        AttributeReference att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                        if (att != null && !att.IsErased && !att.Invisible && att.Visible && !string.IsNullOrWhiteSpace(att.TextString))
                        {
                            RenderTextEntityForVisual(att, xform, ResolveGdiColor(att, layerColorMap, col),
                                capMinX, capMinY, capMaxX, capMaxY, offsetX, offsetY, scale, g, ref primitivesDrawn, ref textsDrawn);
                        }
                    }
                    catch { }
                }
                return;
            }

            if (ent is DBText || ent is MText)
            {
                RenderTextEntityForVisual(ent, xform, col,
                    capMinX, capMinY, capMaxX, capMaxY, offsetX, offsetY, scale, g, ref primitivesDrawn, ref textsDrawn);
                return;
            }

            using (Pen pen = new Pen(col, penWidth))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                if (ent is Line)
                {
                    Line ln = (Line)ent;
                    Point3d p1 = ln.StartPoint.TransformBy(xform);
                    Point3d p2 = ln.EndPoint.TransformBy(xform);
                    g.DrawLine(pen,
                        WorldToPixel(p1.X, p1.Y, capMinX, capMaxY, offsetX, offsetY, scale),
                        WorldToPixel(p2.X, p2.Y, capMinX, capMaxY, offsetX, offsetY, scale));
                    primitivesDrawn++;
                }
                else if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline)
                {
                    Autodesk.AutoCAD.DatabaseServices.Polyline pl = (Autodesk.AutoCAD.DatabaseServices.Polyline)ent;
                    int n = pl.NumberOfVertices;
                    if (n >= 2)
                    {
                        int endIdx = pl.Closed ? n : n - 1;
                        for (int i = 0; i < endIdx; i++)
                        {
                            int ni = (i + 1) % n;
                            Point3d p1 = pl.GetPoint3dAt(i).TransformBy(xform);
                            Point3d p2 = pl.GetPoint3dAt(ni).TransformBy(xform);
                            double xyDet = xform[0, 0] * xform[1, 1] - xform[0, 1] * xform[1, 0];
                            double bulge = (xyDet < -1e-9) ? -pl.GetBulgeAt(i) : pl.GetBulgeAt(i);
                            DrawVisualPolylineVertexSpan(g, pen, p1, p2, bulge,
                                capMinX, capMaxY, offsetX, offsetY, scale, ref primitivesDrawn);
                        }
                    }
                }
                else if (ent is Circle)
                {
                    Circle c = (Circle)ent;
                    if (c.Radius > 1e-7)
                    {
                        const int segs = 28;
                        PointF[] pts = new PointF[segs + 1];
                        for (int si = 0; si <= segs; si++)
                        {
                            double ang = (2.0 * Math.PI * si) / segs;
                            Point3d wp = new Point3d(
                                c.Center.X + c.Radius * Math.Cos(ang),
                                c.Center.Y + c.Radius * Math.Sin(ang), 0.0).TransformBy(xform);
                            pts[si] = WorldToPixel(wp.X, wp.Y, capMinX, capMaxY, offsetX, offsetY, scale);
                        }
                        g.DrawLines(pen, pts);
                        primitivesDrawn++;
                    }
                }
                else if (ent is Arc)
                {
                    Arc arc = (Arc)ent;
                    if (arc.Radius > 1e-7)
                    {
                        double sa = arc.StartAngle;
                        double ea = arc.EndAngle;
                        if (ea <= sa) ea += 2.0 * Math.PI;
                        double sweep = ea - sa;
                        const int segs = 20;
                        PointF[] pts = new PointF[segs + 1];
                        for (int si = 0; si <= segs; si++)
                        {
                            double ang = sa + sweep * ((double)si / segs);
                            Point3d wp = new Point3d(
                                arc.Center.X + arc.Radius * Math.Cos(ang),
                                arc.Center.Y + arc.Radius * Math.Sin(ang), 0.0).TransformBy(xform);
                            pts[si] = WorldToPixel(wp.X, wp.Y, capMinX, capMaxY, offsetX, offsetY, scale);
                        }
                        g.DrawLines(pen, pts);
                        primitivesDrawn++;
                    }
                }
                else if (ent is Ellipse)
                {
                    Ellipse el = (Ellipse)ent;
                    try
                    {
                        double sp = el.StartParam;
                        double ep = el.EndParam;
                        if (ep <= sp) ep += 2.0 * Math.PI;
                        const int segs = 24;
                        PointF[] pts = new PointF[segs + 1];
                        for (int si = 0; si <= segs; si++)
                        {
                            double t = sp + (ep - sp) * ((double)si / segs);
                            Point3d wp = el.GetPointAtParameter(t).TransformBy(xform);
                            pts[si] = WorldToPixel(wp.X, wp.Y, capMinX, capMaxY, offsetX, offsetY, scale);
                        }
                        g.DrawLines(pen, pts);
                        primitivesDrawn++;
                    }
                    catch { }
                }
                else if (ent is Spline)
                {
                    Spline sp = (Spline)ent;
                    try
                    {
                        double t0 = sp.StartParam;
                        double t1 = sp.EndParam;
                        const int segs = 24;
                        PointF[] pts = new PointF[segs + 1];
                        for (int si = 0; si <= segs; si++)
                        {
                            double t = t0 + (t1 - t0) * ((double)si / segs);
                            Point3d wp = sp.GetPointAtParameter(t).TransformBy(xform);
                            pts[si] = WorldToPixel(wp.X, wp.Y, capMinX, capMaxY, offsetX, offsetY, scale);
                        }
                        g.DrawLines(pen, pts);
                        primitivesDrawn++;
                    }
                    catch { }
                }
                else if (ent is Solid)
                {
                    Solid sld = (Solid)ent;
                    try
                    {
                        Point3d p0 = sld.GetPointAt(0).TransformBy(xform);
                        Point3d p1 = sld.GetPointAt(1).TransformBy(xform);
                        Point3d p2 = sld.GetPointAt(2).TransformBy(xform);
                        Point3d p3 = sld.GetPointAt(3).TransformBy(xform);
                        PointF[] poly = new PointF[]
                        {
                            WorldToPixel(p0.X, p0.Y, capMinX, capMaxY, offsetX, offsetY, scale),
                            WorldToPixel(p1.X, p1.Y, capMinX, capMaxY, offsetX, offsetY, scale),
                            WorldToPixel(p3.X, p3.Y, capMinX, capMaxY, offsetX, offsetY, scale),
                            WorldToPixel(p2.X, p2.Y, capMinX, capMaxY, offsetX, offsetY, scale)
                        };
                        using (SolidBrush brs = new SolidBrush(GdiColor.FromArgb(180, col.R, col.G, col.B)))
                            g.FillPolygon(brs, poly);
                        primitivesDrawn++;
                    }
                    catch { }
                }
                else if (ent is Leader)
                {
                    Leader ldr = (Leader)ent;
                    int nv = ldr.NumVertices;
                    if (nv >= 2)
                    {
                        PointF[] pts = new PointF[nv];
                        for (int i = 0; i < nv; i++)
                        {
                            Point3d wp = ldr.VertexAt(i).TransformBy(xform);
                            pts[i] = WorldToPixel(wp.X, wp.Y, capMinX, capMaxY, offsetX, offsetY, scale);
                        }
                        g.DrawLines(pen, pts);
                        primitivesDrawn++;
                    }
                }
                else if (ent is Polyline2d)
                {
                    Polyline2d p2d = (Polyline2d)ent;
                    List<PointF> pts = new List<PointF>();
                    foreach (ObjectId vid in p2d)
                    {
                        Vertex2d v = tr.GetObject(vid, OpenMode.ForRead) as Vertex2d;
                        if (v != null)
                        {
                            Point3d wp = v.Position.TransformBy(xform);
                            pts.Add(WorldToPixel(wp.X, wp.Y, capMinX, capMaxY, offsetX, offsetY, scale));
                        }
                    }
                    if (pts.Count >= 2)
                    {
                        if (p2d.Closed) pts.Add(pts[0]);
                        g.DrawLines(pen, pts.ToArray());
                        primitivesDrawn++;
                    }
                }
                else if (ent is Curve)
                {
                    Curve crv = (Curve)ent;
                    try
                    {
                        Point3d p1 = crv.StartPoint.TransformBy(xform);
                        Point3d p2 = crv.EndPoint.TransformBy(xform);
                        g.DrawLine(pen,
                            WorldToPixel(p1.X, p1.Y, capMinX, capMaxY, offsetX, offsetY, scale),
                            WorldToPixel(p2.X, p2.Y, capMinX, capMaxY, offsetX, offsetY, scale));
                        primitivesDrawn++;
                    }
                    catch { }
                }
            }
        }

        private static void RenderTextEntityForVisual(
            Entity ent,
            Matrix3d xform,
            GdiColor col,
            double capMinX, double capMinY, double capMaxX, double capMaxY,
            double offsetX, double offsetY, double scale,
            Graphics g,
            ref int primitivesDrawn,
            ref int textsDrawn)
        {
            string rawTxt = "";
            double worldH = 0.2;
            double rotRad = 0.0;
            bool isCenterAligned = false;
            Point3d anchorPt = Point3d.Origin;
            Extents3d? boxExt = null;

            if (ent is DBText)
            {
                DBText dt = (DBText)ent;
                rawTxt = (dt.TextString ?? "").Trim();
                worldH = dt.Height;
                rotRad = dt.Rotation;
                anchorPt = dt.Position;
                try
                {
                    Extents3d ex = dt.GeometricExtents;
                    if (IsFiniteBounds(ex))
                    {
                        Point3d p1 = ex.MinPoint.TransformBy(xform);
                        Point3d p2 = ex.MaxPoint.TransformBy(xform);
                        boxExt = new Extents3d(
                            new Point3d(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), 0),
                            new Point3d(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), 0));
                    }
                }
                catch { }
                if (dt.HorizontalMode == TextHorizontalMode.TextCenter ||
                    dt.HorizontalMode == TextHorizontalMode.TextMid)
                    isCenterAligned = true;
            }
            else if (ent is MText)
            {
                MText mt = (MText)ent;
                rawTxt = CleanMTextForVisual(!string.IsNullOrEmpty(mt.Text) ? mt.Text : (mt.Contents ?? ""));
                worldH = mt.TextHeight;
                rotRad = mt.Rotation;
                anchorPt = mt.Location;
                try
                {
                    Extents3d ex = mt.GeometricExtents;
                    if (IsFiniteBounds(ex))
                    {
                        Point3d p1 = ex.MinPoint.TransformBy(xform);
                        Point3d p2 = ex.MaxPoint.TransformBy(xform);
                        boxExt = new Extents3d(
                            new Point3d(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), 0),
                            new Point3d(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), 0));
                    }
                }
                catch { }
                if (mt.Attachment == AttachmentPoint.MiddleCenter ||
                    mt.Attachment == AttachmentPoint.TopCenter ||
                    mt.Attachment == AttachmentPoint.BottomCenter)
                    isCenterAligned = true;
            }
            else
            {
                return;
            }

            string cleanTxt = CleanMTextForVisual(rawTxt);
            if (string.IsNullOrWhiteSpace(cleanTxt) || double.IsNaN(worldH) || double.IsInfinity(worldH) || worldH <= 1e-6)
                return;

            Vector3d upVec = new Vector3d(0.0, worldH, 0.0).TransformBy(xform);
            double effWorldH = upVec.Length;
            float fontPx = (float)(effWorldH * scale * 0.90);
            if (fontPx < 3.0f) return;
            if (fontPx > 72.0f) fontPx = 72.0f;

            Point3d worldPos = boxExt.HasValue
                ? (isCenterAligned
                    ? new Point3d((boxExt.Value.MinPoint.X + boxExt.Value.MaxPoint.X) * 0.5,
                                  (boxExt.Value.MinPoint.Y + boxExt.Value.MaxPoint.Y) * 0.5, 0.0)
                    : new Point3d(boxExt.Value.MinPoint.X,
                                  (boxExt.Value.MinPoint.Y + boxExt.Value.MaxPoint.Y) * 0.5, 0.0))
                : anchorPt.TransformBy(xform);

            if (worldPos.X < capMinX - (capMaxX - capMinX) * 0.5 || worldPos.X > capMaxX + (capMaxX - capMinX) * 0.5 ||
                worldPos.Y < capMinY - (capMaxY - capMinY) * 0.5 || worldPos.Y > capMaxY + (capMaxY - capMinY) * 0.5)
                return;

            PointF pxPt = WorldToPixel(worldPos.X, worldPos.Y, capMinX, capMaxY, offsetX, offsetY, scale);
            Vector3d dirVec = new Vector3d(Math.Cos(rotRad), Math.Sin(rotRad), 0.0).TransformBy(xform);
            float degCcw = (float)(Math.Atan2(dirVec.Y, dirVec.X) * 180.0 / Math.PI);

            using (GdiFont fnt = new GdiFont("Arial", fontPx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush brs = new SolidBrush(col))
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoClip))
            {
                sf.Alignment = isCenterAligned ? StringAlignment.Center : StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;

                float drawFontPx = fontPx;
                if (boxExt.HasValue && Math.Abs(degCcw) < 5.0f)
                {
                    float targetPxW = (float)((boxExt.Value.MaxPoint.X - boxExt.Value.MinPoint.X) * scale);
                    SizeF meas = g.MeasureString(cleanTxt, fnt, 4096, sf);
                    if (targetPxW > 8.0f && meas.Width > targetPxW * 1.12f)
                    {
                        float ratio = Math.Max(0.68f, (targetPxW * 1.05f) / meas.Width);
                        drawFontPx = Math.Max(3.5f, fontPx * ratio);
                    }
                }

                GdiFont drawFnt = (Math.Abs(drawFontPx - fontPx) > 0.3f)
                    ? new GdiFont("Arial", drawFontPx, FontStyle.Regular, GraphicsUnit.Pixel)
                    : fnt;
                try
                {
                    if (Math.Abs(degCcw) > 1.5f)
                    {
                        GraphicsState gs = g.Save();
                        g.TranslateTransform(pxPt.X, pxPt.Y);
                        g.RotateTransform(-degCcw);
                        g.DrawString(cleanTxt, drawFnt, brs, 0f, 0f, sf);
                        g.Restore(gs);
                    }
                    else
                    {
                        g.DrawString(cleanTxt, drawFnt, brs, pxPt.X, pxPt.Y, sf);
                    }
                }
                finally
                {
                    if (!object.ReferenceEquals(drawFnt, fnt)) drawFnt.Dispose();
                }
                textsDrawn++;
                primitivesDrawn++;
            }
        }

        private static Dictionary<string, object> CaptureVisualView(Document doc, Dictionary<string, object> args)
        {
            if (doc == null || doc.Database == null)
                return Err("لا يوجد ملف رسم نشط في أوتوكاد.");
            if (args == null) args = new Dictionary<string, object>();

            Database db = doc.Database;
            string reqScope = (GetStr(args, "scope") ?? "auto").Trim().ToLowerInvariant();
            double[] reqBbox = GetDoubleArray(args, "bbox");
            List<string> reqHandles = GetStringList(args, "handles");
            double paddingRatio = Math.Max(0.02, Math.Min(0.50, GetDouble(args, "padding_ratio", 0.12)));
            int maxDimPx = Math.Max(640, Math.Min(2400, GetInt(args, "max_dimension_px", 1600)));
            bool includeSurrounding = GetBool(args, "include_surrounding", true);
            bool highlightSelection = GetBool(args, "highlight_selection", true);
            bool annotateBlockNames = GetBool(args, "annotate_block_names", true);
            string savePathArg = GetStr(args, "save_path");

            List<string> selHandles = reqHandles.Count > 0 ? reqHandles : GetSelectedHandleStrings(doc);
            HashSet<string> highlightSet = new HashSet<string>(selHandles, StringComparer.OrdinalIgnoreCase);

            string scopeUsed = reqScope;
            double capMinX = 0, capMinY = 0, capMaxX = 100, capMaxY = 100;
            bool hasCapBounds = false;
            List<Extents3d> selectedBoxHighlights = new List<Extents3d>();

            Dictionary<string, int> blockCountsInView = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<VisualBadgeCandidate> badgeCandidates = new List<VisualBadgeCandidate>();
            int primitivesDrawn = 0;
            int textsDrawn = 0;
            int entitiesScanned = 0;
            byte[] pngBytes = null;
            int bmpW = 0, bmpH = 0;
            string outPath = "";

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Dictionary<string, bool> layerHiddenMap = BuildLayerHiddenMap(db, tr);
                Dictionary<string, CadColor> layerColorMap = new Dictionary<string, CadColor>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId lid in lt)
                    {
                        LayerTableRecord ltr = tr.GetObject(lid, OpenMode.ForRead) as LayerTableRecord;
                        if (ltr != null) layerColorMap[ltr.Name] = ltr.Color;
                    }
                }
                catch { }

                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                // 1. Resolve target capture bounds
                if (reqScope == "bbox" && reqBbox != null && reqBbox.Length >= 4)
                {
                    capMinX = Math.Min(reqBbox[0], reqBbox[2]);
                    capMinY = Math.Min(reqBbox[1], reqBbox[3]);
                    capMaxX = Math.Max(reqBbox[0], reqBbox[2]);
                    capMaxY = Math.Max(reqBbox[1], reqBbox[3]);
                    hasCapBounds = (capMaxX - capMinX > 1e-6) && (capMaxY - capMinY > 1e-6);
                    scopeUsed = "bbox";
                }
                else if ((reqScope == "selection" || (reqScope == "auto" && selHandles.Count > 0)) && selHandles.Count > 0)
                {
                    Extents3d? selExt = null;
                    foreach (string sh in selHandles)
                    {
                        ObjectId oid = ResolveHandle(db, sh);
                        if (oid.IsNull || oid.IsErased) continue;
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        try
                        {
                            Extents3d ex = ent.GeometricExtents;
                            if (!IsFiniteBounds(ex)) continue;
                            selectedBoxHighlights.Add(ex);
                            if (!selExt.HasValue) selExt = ex;
                            else { Extents3d u = selExt.Value; u.AddExtents(ex); selExt = u; }
                        }
                        catch { }
                    }
                    if (selExt.HasValue)
                    {
                        capMinX = selExt.Value.MinPoint.X;
                        capMinY = selExt.Value.MinPoint.Y;
                        capMaxX = selExt.Value.MaxPoint.X;
                        capMaxY = selExt.Value.MaxPoint.Y;
                        hasCapBounds = true;
                        scopeUsed = "selection";
                    }
                }

                if (!hasCapBounds && (reqScope == "viewport" || reqScope == "auto"))
                {
                    double vx1, vy1, vx2, vy2;
                    if (GetActiveGuiViewportBounds(out vx1, out vy1, out vx2, out vy2))
                    {
                        // Verify viewport actually intersects ModelSpace extents so headless defaults don't render blank space
                        Extents3d? msExt = null;
                        int chk = 0;
                        foreach (ObjectId oid in ms)
                        {
                            if (++chk > 8000) break;
                            Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                            if (ent == null || !IsEntityVisible(ent, layerHiddenMap)) continue;
                            try
                            {
                                Extents3d ex = ent.GeometricExtents;
                                if (!IsFiniteBounds(ex)) continue;
                                if (!msExt.HasValue) msExt = ex;
                                else { Extents3d u = msExt.Value; u.AddExtents(ex); msExt = u; }
                            }
                            catch { }
                        }
                        if (!msExt.HasValue || VisualBoxesOverlap(vx1, vy1, vx2, vy2,
                            msExt.Value.MinPoint.X, msExt.Value.MinPoint.Y, msExt.Value.MaxPoint.X, msExt.Value.MaxPoint.Y))
                        {
                            capMinX = vx1; capMinY = vy1; capMaxX = vx2; capMaxY = vy2;
                            hasCapBounds = true;
                            scopeUsed = "viewport";
                        }
                    }
                }

                if (!hasCapBounds)
                {
                    Extents3d? visExt = null;
                    foreach (ObjectId oid in ms)
                    {
                        if (oid.IsErased) continue;
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null || !IsEntityVisible(ent, layerHiddenMap)) continue;
                        try
                        {
                            Extents3d ex = ent.GeometricExtents;
                            if (!IsFiniteBounds(ex)) continue;
                            if (!visExt.HasValue) visExt = ex;
                            else { Extents3d u = visExt.Value; u.AddExtents(ex); visExt = u; }
                        }
                        catch { }
                    }
                    if (visExt.HasValue)
                    {
                        capMinX = visExt.Value.MinPoint.X;
                        capMinY = visExt.Value.MinPoint.Y;
                        capMaxX = visExt.Value.MaxPoint.X;
                        capMaxY = visExt.Value.MaxPoint.Y;
                        hasCapBounds = true;
                        scopeUsed = "extents";
                    }
                }

                if (!hasCapBounds)
                {
                    tr.Commit();
                    return Err("تعذر تحديد حدود صالحة لالتقاط الصورة (حدد العناصر المطلوبة أو افتح مخططاً غير فارغ).");
                }

                double rawW = Math.Max(1e-3, capMaxX - capMinX);
                double rawH = Math.Max(1e-3, capMaxY - capMinY);
                double rawMax = Math.Max(rawW, rawH);
                double padX = Math.Max(rawW * paddingRatio, rawMax * 0.03);
                double padY = Math.Max(rawH * paddingRatio, rawMax * 0.03);
                if (scopeUsed != "viewport")
                {
                    capMinX -= padX;
                    capMaxX += padX;
                    capMinY -= padY;
                    capMaxY += padY;
                }

                double worldW = Math.Max(1e-3, capMaxX - capMinX);
                double worldH = Math.Max(1e-3, capMaxY - capMinY);
                double aspectRatio = worldW / worldH;
                if (aspectRatio >= 1.0)
                {
                    bmpW = maxDimPx;
                    bmpH = Math.Max(420, Math.Min(maxDimPx, (int)Math.Round(maxDimPx / aspectRatio)));
                }
                else
                {
                    bmpH = maxDimPx;
                    bmpW = Math.Max(420, Math.Min(maxDimPx, (int)Math.Round(maxDimPx * aspectRatio)));
                }

                const int headerPx = 34;
                const int sidePadPx = 18;
                const int botPadPx = 22;
                double plotW = Math.Max(100.0, bmpW - sidePadPx * 2.0);
                double plotH = Math.Max(100.0, bmpH - headerPx - botPadPx);
                double scale = Math.Min(plotW / worldW, plotH / worldH);
                double offsetX = sidePadPx + (plotW - worldW * scale) * 0.5;
                double offsetY = headerPx + (plotH - worldH * scale) * 0.5;

                using (Bitmap bmp = new Bitmap(bmpW, bmpH, PixelFormat.Format24bppRgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(GdiColor.FromArgb(24, 28, 34));

                    // Render ModelSpace entities intersecting capture viewport
                    const int MaxVisualPrimitives = 25000;
                    const int MaxScannedEnts = 60000;
                    if (!includeSurrounding && highlightSet.Count > 0)
                    {
                        foreach (string sh in selHandles)
                        {
                            ObjectId oid = ResolveHandle(db, sh);
                            if (oid.IsNull || oid.IsErased) continue;
                            Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased) continue;
                            entitiesScanned++;
                            RenderEntityRecursiveForVisual(
                                tr, ent, Matrix3d.Identity, 0,
                                layerHiddenMap, layerColorMap, GdiColor.FromArgb(220, 228, 236),
                                capMinX, capMinY, capMaxX, capMaxY, offsetX, offsetY, scale,
                                g, ref primitivesDrawn, ref textsDrawn, MaxVisualPrimitives,
                                true, blockCountsInView, badgeCandidates);
                        }
                    }
                    else
                    {
                        foreach (ObjectId oid in ms)
                        {
                            if (oid.IsErased || primitivesDrawn >= MaxVisualPrimitives || entitiesScanned >= MaxScannedEnts)
                                break;
                            Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased) continue;
                            entitiesScanned++;
                            RenderEntityRecursiveForVisual(
                                tr, ent, Matrix3d.Identity, 0,
                                layerHiddenMap, layerColorMap, GdiColor.FromArgb(220, 228, 236),
                                capMinX, capMinY, capMaxX, capMaxY, offsetX, offsetY, scale,
                                g, ref primitivesDrawn, ref textsDrawn, MaxVisualPrimitives,
                                true, blockCountsInView, badgeCandidates);
                        }
                    }

                    // Highlight explicitly selected entity bounds cleanly without obscuring geometry
                    if (highlightSelection && selectedBoxHighlights.Count > 0 && selectedBoxHighlights.Count <= 250)
                    {
                        using (Pen hlPen = new Pen(GdiColor.FromArgb(170, 0, 229, 255), 1.5f))
                        {
                            hlPen.DashStyle = DashStyle.Dash;
                            for (int hi = 0; hi < selectedBoxHighlights.Count; hi++)
                            {
                                Extents3d hex = selectedBoxHighlights[hi];
                                PointF tl = WorldToPixel(hex.MinPoint.X, hex.MaxPoint.Y, capMinX, capMaxY, offsetX, offsetY, scale);
                                PointF br = WorldToPixel(hex.MaxPoint.X, hex.MinPoint.Y, capMinX, capMaxY, offsetX, offsetY, scale);
                                float rw = Math.Max(4f, br.X - tl.X);
                                float rh = Math.Max(4f, br.Y - tl.Y);
                                g.DrawRectangle(hlPen, tl.X - 2f, tl.Y - 2f, rw + 4f, rh + 4f);
                            }
                        }
                    }

                    // Draw compact non-overlapping [BlockName] pill badges for visible blocks
                    if (annotateBlockNames && badgeCandidates.Count > 0)
                    {
                        badgeCandidates.Sort(delegate(VisualBadgeCandidate a, VisualBadgeCandidate b)
                        {
                            int s = b.Score.CompareTo(a.Score);
                            if (s != 0) return s;
                            int y = b.WorldCy.CompareTo(a.WorldCy);
                            return y != 0 ? y : a.WorldCx.CompareTo(b.WorldCx);
                        });

                        List<RectangleF> placedBadges = new List<RectangleF>();
                        HashSet<string> badgedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        using (GdiFont badgeFnt = new GdiFont("Consolas", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (SolidBrush badgeBg = new SolidBrush(GdiColor.FromArgb(205, 11, 19, 32)))
                        using (Pen badgeBorder = new Pen(GdiColor.FromArgb(220, 0, 229, 255), 1.0f))
                        using (SolidBrush badgeFg = new SolidBrush(GdiColor.FromArgb(255, 125, 249, 255)))
                        {
                            // Place distinct unseen block names before repeated names so stacked symbols (e.g. D2+D2E) show [D2E]
                            List<VisualBadgeCandidate> ordered = new List<VisualBadgeCandidate>();
                            HashSet<int> queuedIdx = new HashSet<int>();
                            HashSet<string> firstSeenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            for (int bi = 0; bi < badgeCandidates.Count; bi++)
                            {
                                if (firstSeenNames.Add(badgeCandidates[bi].Label))
                                {
                                    ordered.Add(badgeCandidates[bi]);
                                    queuedIdx.Add(bi);
                                }
                            }
                            for (int bi = 0; bi < badgeCandidates.Count; bi++)
                            {
                                if (!queuedIdx.Contains(bi)) ordered.Add(badgeCandidates[bi]);
                            }

                            int drawnBadges = 0;
                            for (int bi = 0; bi < ordered.Count && drawnBadges < 80; bi++)
                            {
                                VisualBadgeCandidate bc = ordered[bi];
                                string lbl = "[" + bc.Label + "]";
                                SizeF sz = g.MeasureString(lbl, badgeFnt);
                                PointF topPt = WorldToPixel(bc.WorldCx, bc.WorldMaxY, capMinX, capMaxY, offsetX, offsetY, scale);
                                float bx = Math.Max(4f, Math.Min(bmpW - sz.Width - 8f, topPt.X - sz.Width * 0.5f));
                                float by = Math.Max(headerPx + 2f, Math.Min(bmpH - botPadPx - sz.Height - 4f, topPt.Y - sz.Height - 4f));
                                RectangleF rect = new RectangleF(bx - 2f, by - 1f, sz.Width + 4f, sz.Height + 2f);
                                bool collides = false;
                                for (int pi = 0; pi < placedBadges.Count; pi++)
                                {
                                    if (rect.IntersectsWith(placedBadges[pi])) { collides = true; break; }
                                }
                                if (collides) continue;
                                placedBadges.Add(rect);
                                badgedNames.Add(bc.Label);
                                g.FillRectangle(badgeBg, rect);
                                g.DrawRectangle(badgeBorder, rect.X, rect.Y, rect.Width, rect.Height);
                                g.DrawString(lbl, badgeFnt, badgeFg, rect.X + 2f, rect.Y + 1f);
                                drawnBadges++;
                            }
                        }
                    }

                    // Top status bar
                    using (SolidBrush hdrBg = new SolidBrush(GdiColor.FromArgb(230, 15, 20, 28)))
                    using (SolidBrush hdrFg = new SolidBrush(GdiColor.FromArgb(226, 232, 240)))
                    using (Pen hdrLine = new Pen(GdiColor.FromArgb(56, 70, 92), 1.0f))
                    using (GdiFont hdrFnt = new GdiFont("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        g.FillRectangle(hdrBg, 0, 0, bmpW, headerPx);
                        g.DrawLine(hdrLine, 0, headerPx - 1, bmpW, headerPx - 1);
                        string dwgName = !string.IsNullOrWhiteSpace(doc.Name) ? Path.GetFileName(doc.Name) : "Drawing.dwg";
                        string banner = string.Format(
                            CultureInfo.InvariantCulture,
                            "AI-ENGINEER VISUAL CAPTURE | {0} | scope={1} | sel={2} | X:[{3:0.##},{4:0.##}] Y:[{5:0.##},{6:0.##}]",
                            dwgName, scopeUsed, selHandles.Count, capMinX, capMaxX, capMinY, capMaxY);
                        g.DrawString(banner, hdrFnt, hdrFg, 10f, 8f);
                    }

                    if (!string.IsNullOrWhiteSpace(savePathArg))
                    {
                        outPath = ExcelTableInterop.ResolveSafeOutputPath(savePathArg, new string[] { ".png" }, ".png", "cad_view");
                    }
                    else
                    {
                        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        string capDir = !string.IsNullOrWhiteSpace(localApp)
                            ? Path.Combine(Path.Combine(localApp, "AI-Engineer"), "visual_captures")
                            : Path.GetTempPath();
                        Directory.CreateDirectory(capDir);
                        try
                        {
                            FileInfo[] oldPngs = new DirectoryInfo(capDir).GetFiles("cad_view_*.png");
                            if (oldPngs.Length > 29)
                            {
                                Array.Sort(oldPngs, delegate(FileInfo a, FileInfo b) { return a.CreationTimeUtc.CompareTo(b.CreationTimeUtc); });
                                for (int fi = 0; fi < oldPngs.Length - 29; fi++)
                                {
                                    try { oldPngs[fi].Delete(); } catch { }
                                }
                            }
                        }
                        catch { }
                        outPath = Path.Combine(capDir, "cad_view_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".png");
                    }

                    string outDir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

                    using (MemoryStream msPng = new MemoryStream())
                    {
                        bmp.Save(msPng, ImageFormat.Png);
                        pngBytes = msPng.ToArray();
                    }
                    File.WriteAllBytes(outPath, pngBytes);
                }

                tr.Commit();
            }

            Dictionary<string, object> blockCountsObj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> kv in blockCountsInView)
                blockCountsObj[kv.Key] = kv.Value;

            return Ok(new Dictionary<string, object>
            {
                { "image_path", outPath },
                { "width_px", bmpW },
                { "height_px", bmpH },
                { "png_bytes", pngBytes != null ? pngBytes.Length : 0 },
                { "scope_used", scopeUsed },
                { "selection_count", selHandles.Count },
                { "bounds", new double[] { Round(capMinX), Round(capMinY), Round(capMaxX), Round(capMaxY) } },
                { "entities_scanned", entitiesScanned },
                { "primitives_rendered", primitivesDrawn },
                { "texts_rendered", textsDrawn },
                { "blocks_in_view", blockCountsObj },
                { "_vision_png_base64", pngBytes != null ? Convert.ToBase64String(pngBytes) : "" },
                { "_vision_mime_type", "image/png" }
            });
        }
    }
}
