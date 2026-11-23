using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        // ==================== Phase 2: 2D Spatial Grid & Block Geometry Cache ====================

        private sealed class SpatialGrid2D<T>
        {
            private readonly double _invCellSize;
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private readonly List<T> _items = new List<T>();
            private readonly List<int> _oversized = new List<int>();
            private int[] _seenStamp = new int[256];
            private int _stamp = 1;

            public SpatialGrid2D(double cellSize)
            {
                if (double.IsNaN(cellSize) || double.IsInfinity(cellSize) || cellSize <= 1e-6)
                    cellSize = 1.0;
                _invCellSize = 1.0 / cellSize;
            }

            public int Count
            {
                get { return _items.Count; }
            }

            private static long EncodeCellKey(int cx, int cy)
            {
                return (((long)cx) << 32) ^ (uint)cy;
            }

            public void Add(double minX, double minY, double maxX, double maxY, T item)
            {
                if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY))
                    return;
                if (minX > maxX) { double tmp = minX; minX = maxX; maxX = tmp; }
                if (minY > maxY) { double tmp = minY; minY = maxY; maxY = tmp; }

                int idx = _items.Count;
                _items.Add(item);
                if (idx >= _seenStamp.Length)
                {
                    int newLen = _seenStamp.Length * 2;
                    while (newLen <= idx) newLen *= 2;
                    Array.Resize(ref _seenStamp, newLen);
                }

                int cx0 = (int)Math.Floor(minX * _invCellSize);
                int cy0 = (int)Math.Floor(minY * _invCellSize);
                int cx1 = (int)Math.Floor(maxX * _invCellSize);
                int cy1 = (int)Math.Floor(maxY * _invCellSize);

                long cellSpanX = (long)cx1 - (long)cx0 + 1L;
                long cellSpanY = (long)cy1 - (long)cy0 + 1L;
                if (cellSpanX <= 0 || cellSpanY <= 0 || cellSpanX > 64 || cellSpanY > 64 || (cellSpanX * cellSpanY) > 64)
                {
                    _oversized.Add(idx);
                    return;
                }

                for (int cx = cx0; cx <= cx1; cx++)
                {
                    for (int cy = cy0; cy <= cy1; cy++)
                    {
                        long key = EncodeCellKey(cx, cy);
                        List<int> bucket;
                        if (!_cells.TryGetValue(key, out bucket))
                        {
                            bucket = new List<int>(4);
                            _cells[key] = bucket;
                        }
                        bucket.Add(idx);
                    }
                }
            }

            public void Query(double minX, double minY, double maxX, double maxY, List<T> results)
            {
                results.Clear();
                if (_items.Count == 0) return;
                if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY))
                    return;
                if (minX > maxX) { double tmp = minX; minX = maxX; maxX = tmp; }
                if (minY > maxY) { double tmp = minY; minY = maxY; maxY = tmp; }

                _stamp++;
                if (_stamp == int.MaxValue)
                {
                    Array.Clear(_seenStamp, 0, _seenStamp.Length);
                    _stamp = 1;
                }

                int cx0 = (int)Math.Floor(minX * _invCellSize);
                int cy0 = (int)Math.Floor(minY * _invCellSize);
                int cx1 = (int)Math.Floor(maxX * _invCellSize);
                int cy1 = (int)Math.Floor(maxY * _invCellSize);

                long cellSpanX = (long)cx1 - (long)cx0 + 1L;
                long cellSpanY = (long)cy1 - (long)cy0 + 1L;
                if (cellSpanX <= 0 || cellSpanY <= 0 || cellSpanX > 200 || cellSpanY > 200 || (cellSpanX * cellSpanY) > 2500)
                {
                    results.AddRange(_items);
                    return;
                }

                for (int cx = cx0; cx <= cx1; cx++)
                {
                    for (int cy = cy0; cy <= cy1; cy++)
                    {
                        List<int> bucket;
                        if (!_cells.TryGetValue(EncodeCellKey(cx, cy), out bucket)) continue;
                        for (int bi = 0; bi < bucket.Count; bi++)
                        {
                            int idx = bucket[bi];
                            if (_seenStamp[idx] == _stamp) continue;
                            _seenStamp[idx] = _stamp;
                            results.Add(_items[idx]);
                        }
                    }
                }

                for (int oi = 0; oi < _oversized.Count; oi++)
                {
                    int idx = _oversized[oi];
                    if (_seenStamp[idx] == _stamp) continue;
                    _seenStamp[idx] = _stamp;
                    results.Add(_items[idx]);
                }
            }
        }

        private sealed class CachedBlockEntry
        {
            public ObjectId Id;
            public string Handle;
            public string BlockName;
            public string Layer;
            public int ColorIndex;
            public BlockReference Br;
            public Extents3d Bounds;
            public double Span;
            public double CenterX;
            public double CenterY;
            public bool IsTerminator;
            public bool IsPanelRoot;
            public bool IsContainer;
            public string AttributeText;
        }

        private sealed class CachedTextTagEntry
        {
            public ObjectId Id;
            public string Text;
            public double X;
            public double Y;
            public string CircuitTag;
            public string PanelPrefix;
        }

        private sealed class DrawingBlockGeometryCache
        {
            public double CellSize;
            public double MaxFixtureSpan;
            public double MaxTerminatorSpan;
            public List<CachedBlockEntry> AllBlocks = new List<CachedBlockEntry>();
            public List<CachedBlockEntry> FixtureBlocks = new List<CachedBlockEntry>();
            public List<CachedBlockEntry> TerminatorBlocks = new List<CachedBlockEntry>();
            public List<CachedTextTagEntry> TextTags = new List<CachedTextTagEntry>();
            public SpatialGrid2D<CachedBlockEntry> FixtureGrid;
            public SpatialGrid2D<CachedBlockEntry> TerminatorGrid;
            public SpatialGrid2D<CachedTextTagEntry> TextTagGrid;

            public void QueryNearbyCircuitTag(
                List<CircuitTerminatorInfo> terminators,
                List<Point2d> openWireTips,
                double searchRadius,
                out string circuitName,
                out string panelName,
                out string terminationType)
            {
                circuitName = "";
                panelName = "";
                terminationType = "open_end";

                if (terminators != null && terminators.Count > 0)
                {
                    bool hasPanel = false;
                    for (int i = 0; i < terminators.Count; i++)
                    {
                        if (terminators[i].Kind == "panel_connection") { hasPanel = true; break; }
                    }
                    terminationType = hasPanel ? "panel_connection" : "home_run_arrow";

                    for (int ti = 0; ti < terminators.Count; ti++)
                    {
                        string lbl = terminators[ti].Label;
                        if (string.IsNullOrWhiteSpace(lbl)) continue;
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

                List<Point2d> anchors = new List<Point2d>();
                if (terminators != null)
                {
                    for (int i = 0; i < terminators.Count; i++)
                        anchors.Add(new Point2d(terminators[i].X, terminators[i].Y));
                }
                if (anchors.Count == 0 && openWireTips != null)
                    anchors.AddRange(openWireTips);
                if (anchors.Count == 0 || TextTagGrid == null) return;

                double minAx = anchors[0].X;
                double maxAx = anchors[0].X;
                double minAy = anchors[0].Y;
                double maxAy = anchors[0].Y;
                for (int i = 1; i < anchors.Count; i++)
                {
                    if (anchors[i].X < minAx) minAx = anchors[i].X;
                    if (anchors[i].X > maxAx) maxAx = anchors[i].X;
                    if (anchors[i].Y < minAy) minAy = anchors[i].Y;
                    if (anchors[i].Y > maxAy) maxAy = anchors[i].Y;
                }

                List<CachedTextTagEntry> nearbyTags = new List<CachedTextTagEntry>();
                TextTagGrid.Query(minAx - searchRadius, minAy - searchRadius, maxAx + searchRadius, maxAy + searchRadius, nearbyTags);

                double bestDist = double.MaxValue;
                for (int ti = 0; ti < nearbyTags.Count; ti++)
                {
                    CachedTextTagEntry tg = nearbyTags[ti];
                    Point2d pos = new Point2d(tg.X, tg.Y);
                    double minAnchorDist = double.MaxValue;
                    for (int ai = 0; ai < anchors.Count; ai++)
                    {
                        double d = anchors[ai].GetDistanceTo(pos);
                        if (d < minAnchorDist) minAnchorDist = d;
                    }
                    if (minAnchorDist > searchRadius) continue;
                    if (minAnchorDist < bestDist)
                    {
                        bestDist = minAnchorDist;
                        circuitName = tg.CircuitTag;
                        panelName = tg.PanelPrefix;
                    }
                }

                if (string.IsNullOrEmpty(panelName) && terminators != null)
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
        }

        private static string ResolveCachedBlockTag(Transaction tr, BlockReference br)
        {
            if (br == null) return "";
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
            return attrText.Trim();
        }

        private static DrawingBlockGeometryCache BuildDrawingBlockGeometryCache(
            Database db,
            Transaction tr,
            Dictionary<string, bool> hiddenMap,
            bool visibleOnly,
            double unitsPerM,
            double medianFixtureSpan,
            double drawingSpan,
            bool includeContainerBlocks,
            List<string> excludeBlocks,
            string blockFilter)
        {
            double cellSize = medianFixtureSpan > 1e-3
                ? Math.Max(0.25, medianFixtureSpan * 3.0)
                : Math.Max(1.0, drawingSpan > 1e-3 ? (drawingSpan / 48.0) : 2.5);

            DrawingBlockGeometryCache cache = new DrawingBlockGeometryCache();
            cache.CellSize = cellSize;
            cache.FixtureGrid = new SpatialGrid2D<CachedBlockEntry>(cellSize);
            cache.TerminatorGrid = new SpatialGrid2D<CachedBlockEntry>(cellSize);
            cache.TextTagGrid = new SpatialGrid2D<CachedTextTagEntry>(cellSize);

            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId mid in ms)
            {
                if (mid.IsErased) continue;
                Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (IsLegendOrHighlightLayer(ent.Layer) || IsAiAnnotationLayer(ent.Layer)) continue;
                if (visibleOnly && !IsEntityVisible(ent, hiddenMap)) continue;

                BlockReference br = ent as BlockReference;
                if (br != null)
                {
                    string bn = ResolveBlockName(tr, br);
                    if (string.IsNullOrEmpty(bn) || IsAnonymousBlockName(bn)) continue;
                    if (IsSystemBlockName(bn) || string.Equals(ent.Layer, "ASHADE", StringComparison.OrdinalIgnoreCase)) continue;

                    if (excludeBlocks != null && excludeBlocks.Count > 0)
                    {
                        bool excluded = false;
                        for (int ei = 0; ei < excludeBlocks.Count; ei++)
                        {
                            string pat = excludeBlocks[ei];
                            if (string.IsNullOrWhiteSpace(pat)) continue;
                            if (string.Equals(bn, pat.Trim(), StringComparison.OrdinalIgnoreCase) || MatchesPatternOrSubstring(bn, pat))
                            {
                                excluded = true;
                                break;
                            }
                        }
                        if (excluded) continue;
                    }

                    Extents3d bex;
                    try { bex = br.GeometricExtents; }
                    catch { continue; }
                    if (!IsFiniteBounds(bex)) continue;

                    double span = Math.Max(Math.Max(0.0, bex.MaxPoint.X - bex.MinPoint.X), Math.Max(0.0, bex.MaxPoint.Y - bex.MinPoint.Y));
                    bool isTerminator = IsCircuitTerminatorArrowBlock(bn);
                    bool isPanelRoot = IsPanelDistributionBlockName(bn);
                    bool isContainer = IsLikelyArchitecturalContainerBlock(bn, span, medianFixtureSpan, drawingSpan, unitsPerM, includeContainerBlocks, blockFilter);

                    string attrTag = (isTerminator || isPanelRoot) ? ResolveCachedBlockTag(tr, br) : "";
                    CachedBlockEntry entry = new CachedBlockEntry
                    {
                        Id = mid,
                        Handle = ent.Handle.ToString(),
                        BlockName = bn,
                        Layer = ent.Layer ?? "0",
                        ColorIndex = ent.ColorIndex,
                        Br = br,
                        Bounds = bex,
                        Span = span,
                        CenterX = (bex.MinPoint.X + bex.MaxPoint.X) * 0.5,
                        CenterY = (bex.MinPoint.Y + bex.MaxPoint.Y) * 0.5,
                        IsTerminator = isTerminator,
                        IsPanelRoot = isPanelRoot,
                        IsContainer = isContainer,
                        AttributeText = attrTag
                    };
                    cache.AllBlocks.Add(entry);

                    if (isTerminator || isPanelRoot)
                    {
                        cache.TerminatorBlocks.Add(entry);
                        if (span > cache.MaxTerminatorSpan) cache.MaxTerminatorSpan = span;
                        cache.TerminatorGrid.Add(bex.MinPoint.X, bex.MinPoint.Y, bex.MaxPoint.X, bex.MaxPoint.Y, entry);
                    }
                    else if (!isContainer)
                    {
                        cache.FixtureBlocks.Add(entry);
                        if (span > cache.MaxFixtureSpan) cache.MaxFixtureSpan = span;
                        cache.FixtureGrid.Add(bex.MinPoint.X, bex.MinPoint.Y, bex.MaxPoint.X, bex.MaxPoint.Y, entry);
                    }
                    continue;
                }

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

                string pPref;
                string circTag = ExtractBestCircuitTagFromText(txt, out pPref);
                if (string.IsNullOrEmpty(circTag)) continue;

                CachedTextTagEntry tagEntry = new CachedTextTagEntry
                {
                    Id = mid,
                    Text = txt,
                    X = pos.X,
                    Y = pos.Y,
                    CircuitTag = circTag,
                    PanelPrefix = pPref ?? ""
                };
                cache.TextTags.Add(tagEntry);
                cache.TextTagGrid.Add(pos.X, pos.Y, pos.X, pos.Y, tagEntry);
            }

            return cache;
        }
    }
}
