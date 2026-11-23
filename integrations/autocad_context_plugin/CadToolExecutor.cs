using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        private const int MaxInspectEntities = 200;
        private const int MaxQueryEntities = 500;
        private const int MaxReturnedHandles = 1000;
        private const int MaxUndoStepsPerDocument = 30;
        private const int BulkLayerSafetyThreshold = 100;

        private static void TrySetFocusToDwgView(Document doc)
        {
            try
            {
                Type utilsType = typeof(Autodesk.AutoCAD.ApplicationServices.Application).Assembly.GetType("Autodesk.AutoCAD.Internal.Utils", false);
                if (utilsType != null)
                {
                    MethodInfo focusMethod = utilsType.GetMethod(
                        "SetFocusToDwgView",
                        BindingFlags.Static | BindingFlags.Public);
                    if (focusMethod != null)
                    {
                        focusMethod.Invoke(null, null);
                        return;
                    }
                }
            }
            catch { }
            try { if (doc != null) doc.Window.Focus(); } catch { }
        }

        private sealed class SavedColorState
        {
            public ColorMethod Method;
            public short ColorIndex;
            public byte R;
            public byte G;
            public byte B;
        }

        private sealed class BlockRecordRenameSnapshot
        {
            public ObjectId BlockRecordId;
            public string OldName;
            public string NewName;
        }

        private sealed class ModifiedEntitySnapshot
        {
            public ObjectId Id;
            public SavedColorState OldColor;
            public string OldLayer;
            public List<Matrix3d> InverseTransforms = new List<Matrix3d>();
            public bool HadBlockRotation;
            public double OldBlockRotation;
            public bool HadBlockRecordChange;
            public ObjectId OldBlockRecordId;
            public bool HadCircleProps;
            public Point3d OldCenter;
            public double OldRadius;
            public bool HadLineProps;
            public Point3d OldStart;
            public Point3d OldEnd;
            public bool HadDbTextProps;
            public string OldDbText;
            public ObjectId OldDbTextStyleId;
            public double OldDbHeight;
            public bool HadMTextProps;
            public string OldMTextContents;
            public ObjectId OldMTextStyleId;
            public double OldMTextHeight;
            public bool HadPolylineClosed;
            public bool OldClosed;
        }

        private sealed class CadUndoEntry
        {
            public string ActionId;
            public string ToolName;
            public string Summary;
            public DateTime TimestampUtc;
            public List<ObjectId> CreatedIds = new List<ObjectId>();
            public List<ObjectId> ErasedIds = new List<ObjectId>();
            public List<ModifiedEntitySnapshot> ModifiedSnapshots = new List<ModifiedEntitySnapshot>();
            public List<BlockRecordRenameSnapshot> BlockRecordRenames = new List<BlockRecordRenameSnapshot>();
            public List<ObjectId> CreatedBlockRecordIds = new List<ObjectId>();
        }

        private static readonly object _undoLock = new object();
        private static readonly ConditionalWeakTable<Database, List<CadUndoEntry>> _undoStacks = new ConditionalWeakTable<Database, List<CadUndoEntry>>();
        private static bool _docDestroyedHooked = false;
        private static int _undoSeq = 0;

        private static void EnsureDocDestroyedHook()
        {
            if (_docDestroyedHooked) return;
            try
            {
                DocumentCollection dm = Application.DocumentManager;
                if (dm != null)
                {
                    dm.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
                    _docDestroyedHooked = true;
                }
            }
            catch { }
        }

        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                if (e == null || e.Document == null || e.Document.Database == null) return;
                lock (_undoLock)
                {
                    _undoStacks.Remove(e.Document.Database);
                }
            }
            catch { }
        }

        private static void PushUndoEntry(Document doc, CadUndoEntry entry)
        {
            if (doc == null || doc.Database == null || entry == null) return;
            if (entry.CreatedIds.Count == 0 && entry.ErasedIds.Count == 0 &&
                entry.ModifiedSnapshots.Count == 0 && entry.BlockRecordRenames.Count == 0 &&
                entry.CreatedBlockRecordIds.Count == 0)
                return;
            Database db = doc.Database;
            lock (_undoLock)
            {
                EnsureDocDestroyedHook();
                List<CadUndoEntry> stack;
                if (!_undoStacks.TryGetValue(db, out stack))
                {
                    stack = new List<CadUndoEntry>();
                    _undoStacks.Add(db, stack);
                }
                entry.ActionId = "undo_" + (++_undoSeq).ToString(CultureInfo.InvariantCulture);
                entry.TimestampUtc = DateTime.UtcNow;
                stack.Add(entry);
                if (stack.Count > MaxUndoStepsPerDocument)
                    stack.RemoveRange(0, stack.Count - MaxUndoStepsPerDocument);
            }
        }

        internal static int GetUndoHistoryCount(Document doc)
        {
            if (doc == null || doc.Database == null) return 0;
            lock (_undoLock)
            {
                List<CadUndoEntry> stack;
                return _undoStacks.TryGetValue(doc.Database, out stack) ? stack.Count : 0;
            }
        }

        internal static Dictionary<string, object> RollbackLastDirectAction(Document doc)
        {
            return UndoLastAction(doc, new Dictionary<string, object> { { "steps", 1 } });
        }

        private static SavedColorState CaptureColor(Color clr)
        {
            if (clr == null)
                return new SavedColorState { Method = ColorMethod.ByLayer, ColorIndex = 256 };
            return new SavedColorState
            {
                Method = clr.ColorMethod,
                ColorIndex = clr.ColorIndex,
                R = clr.Red,
                G = clr.Green,
                B = clr.Blue
            };
        }

        private static Color RestoreColor(SavedColorState st)
        {
            if (st == null) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
            if (st.Method == ColorMethod.ByLayer)
                return Color.FromColorIndex(ColorMethod.ByLayer, 256);
            if (st.Method == ColorMethod.ByBlock)
                return Color.FromColorIndex(ColorMethod.ByBlock, 0);
            if (st.Method == ColorMethod.ByColor)
                return Color.FromRgb(st.R, st.G, st.B);
            return Color.FromColorIndex(ColorMethod.ByAci, st.ColorIndex);
        }

        private static ModifiedEntitySnapshot CaptureEntitySnapshot(Entity ent, Dictionary<ObjectId, ModifiedEntitySnapshot> snapById)
        {
            if (ent == null || ent.ObjectId.IsNull) return null;
            ModifiedEntitySnapshot existing;
            if (snapById.TryGetValue(ent.ObjectId, out existing))
                return existing;

            ModifiedEntitySnapshot snap = new ModifiedEntitySnapshot();
            snap.Id = ent.ObjectId;
            snap.OldColor = CaptureColor(ent.Color);
            snap.OldLayer = ent.Layer ?? "0";

            BlockReference br = ent as BlockReference;
            if (br != null)
            {
                snap.HadBlockRotation = true;
                snap.OldBlockRotation = br.Rotation;
            }
            Circle c = ent as Circle;
            if (c != null)
            {
                snap.HadCircleProps = true;
                snap.OldCenter = c.Center;
                snap.OldRadius = c.Radius;
            }
            Line ln = ent as Line;
            if (ln != null)
            {
                snap.HadLineProps = true;
                snap.OldStart = ln.StartPoint;
                snap.OldEnd = ln.EndPoint;
            }
            DBText dt = ent as DBText;
            if (dt != null)
            {
                snap.HadDbTextProps = true;
                snap.OldDbText = dt.TextString ?? "";
                snap.OldDbTextStyleId = dt.TextStyleId;
                snap.OldDbHeight = dt.Height;
            }
            MText mt = ent as MText;
            if (mt != null)
            {
                snap.HadMTextProps = true;
                snap.OldMTextContents = mt.Contents ?? "";
                snap.OldMTextStyleId = mt.TextStyleId;
                snap.OldMTextHeight = mt.TextHeight;
            }
            Polyline pl = ent as Polyline;
            if (pl != null)
            {
                snap.HadPolylineClosed = true;
                snap.OldClosed = pl.Closed;
            }

            snapById[ent.ObjectId] = snap;
            return snap;
        }

        public static Dictionary<string, object> ExecuteTool(
            Document doc,
            string toolName,
            Dictionary<string, object> args)
        {
            if (args == null) args = new Dictionary<string, object>();
            string name = (toolName ?? "").Trim().ToLowerInvariant();

            if (name == "get_plugin_diagnostics")
                return GetPluginDiagnostics(doc);

            if (doc == null)
                return Err("No active AutoCAD document is open. Ask the user to open or create a DWG file first.");

            EnsureInteractiveFileDiaGuard();

            try
            {
                switch (name)
                {
                    case "inspect_drawing":
                        return InspectDrawing(doc, args);
                    case "query_entities":
                        return QueryEntities(doc, args);
                    case "select_entities":
                        return SelectEntities(doc, args);
                    case "draw_entities":
                        return DrawEntities(doc, args);
                    case "draw_block_legend":
                        return DrawBlockLegend(doc, args);
                    case "modify_entities":
                        return ModifyEntities(doc, args);
                    case "undo_last_action":
                        return UndoLastAction(doc, args);
                    case "copy_entities":
                        return CopyEntities(doc, args);
                    case "delete_entities":
                        return DeleteEntities(doc, args);
                    case "create_block":
                        return CreateBlock(doc, args);
                    case "convert_matching_shapes_to_block":
                        return ConvertMatchingShapesToBlock(doc, args);
                    case "annotate_block_coordinates":
                        return AnnotateBlockCoordinates(doc, args);
                    case "convert_arrows_to_leaders":
                        return ConvertArrowsToLeaders(doc, args);
                    case "export_to_excel":
                        return ExportToExcel(doc, args);
                    case "import_excel_table":
                        return ImportExcelTable(doc, args);
                    case "extract_load_schedule":
                        return ExtractLoadSchedule(doc, args);
                    case "quantity_takeoff":
                        return QuantityTakeoff(doc, args);
                    case "find_blocks_along_path":
                        return FindBlocksAlongPath(doc, args);
                    case "trace_all_circuits":
                        return TraceAllCircuits(doc, args);
                    case "manage_attributes":
                        return ManageAttributes(doc, args);
                    case "text_tools":
                        return TextTools(doc, args);
                    case "advanced_geometry_ops":
                        return AdvancedGeometryOps(doc, args);
                    case "drawing_qa_cleanup":
                        return DrawingQaCleanup(doc, args);
                    case "layout_and_plot_tools":
                        return LayoutAndPlotTools(doc, args);
                    case "manage_block_library":
                        return ManageBlockLibrary(doc, args);
                    case "lighting_design_engine":
                        return LightingDesignEngine(doc, args);
                    case "export_dialux_stf":
                        return ExportDialuxStf(doc, args);
                    case "calculate_lv_electrical":
                        return CalculateLvElectrical(doc, args);
                    case "manage_layers":
                        return ManageLayers(doc, args);
                    case "measure_and_compare":
                        return MeasureAndCompare(doc, args);
                    case "duplicate_drawing":
                        return DuplicateDrawing(doc, args);
                    case "zoom_view":
                        return ZoomView(doc, args);
                    case "execute_template":
                        return ExecuteTemplate(doc, args);
                    case "approve_or_rollback_preview":
                        return ApproveOrRollbackPreview(doc, args);
                    default:
                        return Err("Unknown tool '" + toolName + "'. Check available tools via get_plugin_diagnostics.");
                }
            }
            catch (System.Exception ex)
            {
                return Err(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Dictionary<string, object> Ok(Dictionary<string, object> extra)
        {
            if (extra == null) extra = new Dictionary<string, object>();
            extra["ok"] = true;
            return extra;
        }

        private static Dictionary<string, object> Err(string message)
        {
            return new Dictionary<string, object>
            {
                { "ok", false },
                { "error", message ?? "Unknown AutoCAD execution error." }
            };
        }

        private static Dictionary<string, object> GetPluginDiagnostics(Document doc)
        {
            string dllPath = "";
            string buildTime = "";
            try
            {
                dllPath = Assembly.GetExecutingAssembly().Location ?? "";
                if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
                    buildTime = File.GetLastWriteTime(dllPath).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch { }

            Dictionary<string, object> res = new Dictionary<string, object>();
            res["dll_path"] = dllPath;
            res["dll_build_time"] = buildTime;
            res["autocad_version"] = Application.Version != null ? Application.Version.ToString() : "unknown";
            res["has_active_document"] = doc != null;
            res["undo_history_steps"] = GetUndoHistoryCount(doc);
            if (doc != null)
            {
                res["active_file"] = Path.GetFileName(doc.Name ?? "Drawing.dwg");
                res["insunits"] = doc.Database.Insunits.ToString();
                res["editor_is_quiescent"] = doc.Editor.IsQuiescent;
            }
            res["supported_tools"] = new string[]
            {
                "inspect_drawing", "query_entities", "select_entities", "draw_entities", "draw_block_legend",
                "modify_entities", "undo_last_action", "copy_entities", "delete_entities", "create_block",
                "convert_matching_shapes_to_block", "annotate_block_coordinates", "convert_arrows_to_leaders", "export_to_excel", "import_excel_table",
                "extract_load_schedule", "quantity_takeoff", "find_blocks_along_path", "manage_attributes", "text_tools",
                "advanced_geometry_ops", "drawing_qa_cleanup", "layout_and_plot_tools", "manage_block_library",
                "lighting_design_engine", "export_dialux_stf", "calculate_lv_electrical",
                "manage_layers", "measure_and_compare", "duplicate_drawing", "zoom_view", "execute_template",
                "approve_or_rollback_preview", "get_plugin_diagnostics"
            };
            return Ok(res);
        }

        private static void EnsureInteractiveFileDiaGuard()
        {
            try
            {
                string proc = Path.GetFileNameWithoutExtension(System.Diagnostics.Process.GetCurrentProcess().ProcessName ?? "");
                if (!string.Equals(proc, "acad", StringComparison.OrdinalIgnoreCase)) return;
                object fd = Application.GetSystemVariable("FILEDIA");
                if (fd != null && Convert.ToInt32(fd, CultureInfo.InvariantCulture) == 0)
                    Application.SetSystemVariable("FILEDIA", 1);
            }
            catch { }
        }

        private static bool IsSystemBlockName(string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return false;
            return blockName.StartsWith("AVE_", StringComparison.OrdinalIgnoreCase) ||
                   blockName.StartsWith("_AI_", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(blockName, "ASHADE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLegendOrHighlightLayer(string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return false;
            return string.Equals(layerName, "_AI_BLOCK_LEGEND", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_COORD_TABLE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_EXCEL_TABLE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_PANEL_SCHEDULE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_BOQ_TABLE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_LIGHTING_TABLE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_HIGHLIGHT", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAiAnnotationLayer(string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return false;
            return IsLegendOrHighlightLayer(layerName) ||
                   string.Equals(layerName, "_AI_COORD_CALLOUTS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_AREA_LABELS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_LIGHTING_LABELS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_REV_CLOUD", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "_AI_QA_ISSUE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, DrawingExecutionCommands.ReviewLayerName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAnonymousBlockName(string blockName)
        {
            return !string.IsNullOrEmpty(blockName) && blockName.StartsWith("*", StringComparison.Ordinal);
        }

        private static ObjectId GetBlockDefinitionId(BlockReference br)
        {
            if (br == null) return ObjectId.Null;
            try
            {
                if (br.IsDynamicBlock && !br.DynamicBlockTableRecord.IsNull)
                    return br.DynamicBlockTableRecord;
            }
            catch { }
            return br.BlockTableRecord;
        }

        private static bool IsFiniteBounds(Extents3d ex)
        {
            return Math.Abs(ex.MinPoint.X) < 1e12 && Math.Abs(ex.MaxPoint.X) < 1e12 &&
                   Math.Abs(ex.MinPoint.Y) < 1e12 && Math.Abs(ex.MaxPoint.Y) < 1e12 &&
                   ex.MaxPoint.X >= ex.MinPoint.X && ex.MaxPoint.Y >= ex.MinPoint.Y;
        }

        private static Dictionary<string, object> InspectDrawing(Document doc, Dictionary<string, object> args)
        {
            bool includeEntities = GetBool(args, "include_entities", true);
            int maxEntities = Math.Max(1, Math.Min(MaxInspectEntities, GetInt(args, "max_entities", 120)));

            Database db = doc.Database;
            List<object> layerList = new List<object>();
            List<object> blockList = new List<object>();
            List<string> layoutList = new List<string>();
            List<object> paperspaceLayouts = new List<object>();
            List<object> entityList = new List<object>();
            List<object> selectedEntityList = new List<object>();
            Dictionary<string, int> selectedTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Extents3d? selectedExtents = null;
            List<object> textSamples = new List<object>();
            HashSet<string> seenTextKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> layerEntityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> layerHiddenFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> namedBlockCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> namedVisibleBlockCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> anonOrSystemBlockCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> selectedHandles = GetSelectedHandleStrings(doc);
            HashSet<string> selectedSet = new HashSet<string>(selectedHandles, StringComparer.OrdinalIgnoreCase);

            int totalEntities = 0;
            int namedBlocksTotal = 0;
            int namedVisibleBlocksTotal = 0;
            int anonOrSystemBlocksTotal = 0;
            int legendThumbnailBlocks = 0;
            Extents3d? totalExtents = null;
            string activeLayerName = "0";
            string currentLayoutName = "Model";
            bool isModelTab = db.TileMode;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try { currentLayoutName = LayoutManager.Current.CurrentLayout ?? (isModelTab ? "Model" : "Layout"); } catch { }

                LayerTableRecord activeLtr = tr.GetObject(db.Clayer, OpenMode.ForRead) as LayerTableRecord;
                if (activeLtr != null) activeLayerName = activeLtr.Name;

                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    LayerTableRecord ltr = tr.GetObject(lid, OpenMode.ForRead) as LayerTableRecord;
                    if (ltr == null) continue;
                    layerHiddenFlags[ltr.Name] = ltr.IsOff || ltr.IsFrozen;
                }

                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                List<ObjectId> allModelIds = new List<ObjectId>();
                foreach (ObjectId id in modelSpace)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    allModelIds.Add(id);
                    totalEntities++;

                    string layer = ent.Layer ?? "0";
                    layerEntityCounts[layer] = layerEntityCounts.ContainsKey(layer) ? layerEntityCounts[layer] + 1 : 1;

                    bool layerHidden = layerHiddenFlags.ContainsKey(layer) && layerHiddenFlags[layer];
                    bool entVisible = !layerHidden && ent.Visible;

                    string tname = ent.GetType().Name;
                    typeCounts[tname] = typeCounts.ContainsKey(tname) ? typeCounts[tname] + 1 : 1;

                    BlockReference br = ent as BlockReference;
                    if (br != null)
                    {
                        string bn = ResolveBlockName(tr, br);
                        if (IsLegendOrHighlightLayer(layer))
                        {
                            legendThumbnailBlocks++;
                        }
                        else if (IsAnonymousBlockName(bn) || IsSystemBlockName(bn))
                        {
                            anonOrSystemBlocksTotal++;
                            anonOrSystemBlockCounts[bn] = anonOrSystemBlockCounts.ContainsKey(bn) ? anonOrSystemBlockCounts[bn] + 1 : 1;
                        }
                        else
                        {
                            namedBlocksTotal++;
                            namedBlockCounts[bn] = namedBlockCounts.ContainsKey(bn) ? namedBlockCounts[bn] + 1 : 1;
                            if (entVisible)
                            {
                                namedVisibleBlocksTotal++;
                                namedVisibleBlockCounts[bn] = namedVisibleBlockCounts.ContainsKey(bn) ? namedVisibleBlockCounts[bn] + 1 : 1;
                            }
                        }
                    }

                    try
                    {
                        Extents3d ext = ent.GeometricExtents;
                        if (IsFiniteBounds(ext))
                        {
                            if (!totalExtents.HasValue) totalExtents = ext;
                            else
                            {
                                Extents3d cur = totalExtents.Value;
                                cur.AddExtents(ext);
                                totalExtents = cur;
                            }
                        }
                    }
                    catch { }

                    if (includeEntities && entityList.Count < maxEntities)
                    {
                        entityList.Add(SerializeEntity(tr, ent, selectedSet, entVisible));
                    }

                    string hStr = ent.Handle.ToString();
                    if (selectedSet.Contains(hStr))
                    {
                        selectedTypeCounts[tname] = selectedTypeCounts.ContainsKey(tname) ? selectedTypeCounts[tname] + 1 : 1;
                        try
                        {
                            Extents3d sExt = ent.GeometricExtents;
                            if (IsFiniteBounds(sExt))
                            {
                                if (!selectedExtents.HasValue) selectedExtents = sExt;
                                else
                                {
                                    Extents3d curSel = selectedExtents.Value;
                                    curSel.AddExtents(sExt);
                                    selectedExtents = curSel;
                                }
                            }
                        }
                        catch { }
                        if (selectedEntityList.Count < 80)
                        {
                            selectedEntityList.Add(SerializeEntity(tr, ent, selectedSet, entVisible));
                        }
                    }

                    if (textSamples.Count < 60)
                    {
                        string sampleTxt = ExtractEntityText(tr, ent);
                        if (!string.IsNullOrWhiteSpace(sampleTxt))
                        {
                            string cleanedTxt = sampleTxt.Replace("\r", " ").Replace("\n", " ").Trim();
                            if (cleanedTxt.Length > 100) cleanedTxt = cleanedTxt.Substring(0, 100);
                            Point3d tpos = GetEntityPositionHint(ent);
                            string dedupKey = cleanedTxt + "@" + Math.Round(tpos.X, 1).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(tpos.Y, 1).ToString(CultureInfo.InvariantCulture);
                            if (seenTextKeys.Add(dedupKey))
                            {
                                textSamples.Add(new Dictionary<string, object>
                                {
                                    { "handle", ent.Handle.ToString() },
                                    { "layer", layer },
                                    { "text", cleanedTxt },
                                    { "position", Pt2(tpos.X, tpos.Y) }
                                });
                            }
                        }
                    }
                }

                foreach (ObjectId lid in lt)
                {
                    if (layerList.Count >= 120) break;
                    LayerTableRecord ltr = tr.GetObject(lid, OpenMode.ForRead) as LayerTableRecord;
                    if (ltr == null) continue;
                    string ltName = "Continuous";
                    try
                    {
                        if (!ltr.LinetypeObjectId.IsNull)
                        {
                            LinetypeTableRecord ltt = tr.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead) as LinetypeTableRecord;
                            if (ltt != null) ltName = ltt.Name;
                        }
                    }
                    catch { }

                    layerList.Add(new Dictionary<string, object>
                    {
                        { "name", ltr.Name },
                        { "color_index", (int)ltr.Color.ColorIndex },
                        { "linetype", ltName },
                        { "is_off", ltr.IsOff },
                        { "is_frozen", ltr.IsFrozen },
                        { "is_locked", ltr.IsLocked },
                        { "entity_count", layerEntityCounts.ContainsKey(ltr.Name) ? layerEntityCounts[ltr.Name] : 0 }
                    });
                }

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                int nestedRefsInsideNamedBlocks = 0;
                foreach (ObjectId bid in bt)
                {
                    if (blockList.Count >= 120) break;
                    BlockTableRecord btr = tr.GetObject(bid, OpenMode.ForRead) as BlockTableRecord;
                    if (btr == null || btr.IsLayout || btr.IsAnonymous) continue;
                    int defCount = 0;
                    int subBlockRefs = 0;
                    foreach (ObjectId childId in btr)
                    {
                        defCount++;
                        Entity childEnt = tr.GetObject(childId, OpenMode.ForRead) as Entity;
                        if (childEnt is BlockReference) subBlockRefs++;
                    }
                    int msTotal = namedBlockCounts.ContainsKey(btr.Name) ? namedBlockCounts[btr.Name] : (anonOrSystemBlockCounts.ContainsKey(btr.Name) ? anonOrSystemBlockCounts[btr.Name] : 0);
                    int msVis = namedVisibleBlockCounts.ContainsKey(btr.Name) ? namedVisibleBlockCounts[btr.Name] : 0;
                    if (msTotal > 0 && subBlockRefs > 0) nestedRefsInsideNamedBlocks += msTotal * subBlockRefs;
                    Dictionary<string, object> binfo = new Dictionary<string, object>
                    {
                        { "name", btr.Name },
                        { "definition_entities", defCount },
                        { "inserted_count", msTotal },
                        { "visible_inserted_count", msVis },
                        { "is_system", IsSystemBlockName(btr.Name) }
                    };
                    if (subBlockRefs > 0) binfo["nested_subblock_count"] = subBlockRefs;
                    blockList.Add(binfo);
                }

                DBDictionary layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layoutDict)
                {
                    layoutList.Add(entry.Key);
                    if (string.Equals(entry.Key, "Model", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        Layout lay = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                        if (lay == null || lay.BlockTableRecordId.IsNull) continue;
                        BlockTableRecord psBtr = tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead) as BlockTableRecord;
                        if (psBtr == null) continue;
                        int psEnts = 0;
                        int psNamedBlocks = 0;
                        Dictionary<string, int> psBlockMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (ObjectId pid in psBtr)
                        {
                            Entity pe = tr.GetObject(pid, OpenMode.ForRead) as Entity;
                            if (pe == null) continue;
                            psEnts++;
                            BlockReference pbr = pe as BlockReference;
                            if (pbr != null)
                            {
                                string pbn = ResolveBlockName(tr, pbr);
                                if (!IsAnonymousBlockName(pbn) && !IsSystemBlockName(pbn))
                                {
                                    psNamedBlocks++;
                                    psBlockMap[pbn] = psBlockMap.ContainsKey(pbn) ? psBlockMap[pbn] + 1 : 1;
                                }
                            }
                        }
                        if (psEnts > 0 || psNamedBlocks > 0)
                        {
                            paperspaceLayouts.Add(new Dictionary<string, object>
                            {
                                { "layout", entry.Key },
                                { "total_entities", psEnts },
                                { "named_block_references", psNamedBlocks },
                                { "named_block_counts", psBlockMap }
                            });
                        }
                    }
                    catch { }
                }

                Dictionary<string, int> recentTailLayerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                List<object> recentTailEntities = new List<object>();
                int tailCountWindow = Math.Min(250, allModelIds.Count);
                for (int i = allModelIds.Count - tailCountWindow; i < allModelIds.Count; i++)
                {
                    Entity te = tr.GetObject(allModelIds[i], OpenMode.ForRead) as Entity;
                    if (te == null) continue;
                    string tl = te.Layer ?? "0";
                    recentTailLayerCounts[tl] = recentTailLayerCounts.ContainsKey(tl) ? recentTailLayerCounts[tl] + 1 : 1;
                }
                int tailPreviewLimit = Math.Min(25, allModelIds.Count);
                for (int i = allModelIds.Count - 1; i >= allModelIds.Count - tailPreviewLimit; i--)
                {
                    Entity te = tr.GetObject(allModelIds[i], OpenMode.ForRead) as Entity;
                    if (te == null) continue;
                    Dictionary<string, object> td = new Dictionary<string, object>
                    {
                        { "handle", te.Handle.ToString() },
                        { "type", te.GetType().Name },
                        { "layer", te.Layer ?? "0" }
                    };
                    string tt = ExtractEntityText(tr, te);
                    if (!string.IsNullOrWhiteSpace(tt))
                    {
                        string ct = tt.Replace("\r", " ").Replace("\n", " ").Trim();
                        if (ct.Length > 60) ct = ct.Substring(0, 60);
                        td["text"] = ct;
                    }
                    try
                    {
                        Extents3d tex = te.GeometricExtents;
                        if (IsFiniteBounds(tex))
                            td["bounds"] = new double[] { Round(tex.MinPoint.X), Round(tex.MinPoint.Y), Round(tex.MaxPoint.X), Round(tex.MaxPoint.Y) };
                    }
                    catch { }
                    recentTailEntities.Add(td);
                }

                if (selectedHandles.Count > 0 && selectedEntityList.Count == 0)
                {
                    foreach (string sh in selectedHandles)
                    {
                        ObjectId sid = ResolveHandle(db, sh);
                        if (sid.IsNull || sid.IsErased) continue;
                        Entity se = tr.GetObject(sid, OpenMode.ForRead) as Entity;
                        if (se == null) continue;
                        string stn = se.GetType().Name;
                        selectedTypeCounts[stn] = selectedTypeCounts.ContainsKey(stn) ? selectedTypeCounts[stn] + 1 : 1;
                        try
                        {
                            Extents3d sExt = se.GeometricExtents;
                            if (IsFiniteBounds(sExt))
                            {
                                if (!selectedExtents.HasValue) selectedExtents = sExt;
                                else { Extents3d curSel = selectedExtents.Value; curSel.AddExtents(sExt); selectedExtents = curSel; }
                            }
                        }
                        catch { }
                        if (selectedEntityList.Count < 80)
                        {
                            bool seVis = IsEntityVisible(se, layerHiddenFlags);
                            selectedEntityList.Add(SerializeEntity(tr, se, selectedSet, seVis));
                        }
                    }
                }

                tr.Commit();

                Dictionary<string, object> unitInfo = GetUnitsInfo(db);
                Dictionary<string, object> res = new Dictionary<string, object>();
                res["file_name"] = Path.GetFileName(doc.Name ?? "Drawing.dwg");
                res["full_path"] = doc.Name ?? "";
                res["active_layer"] = activeLayerName;
                res["current_space"] = isModelTab ? "Model" : ("PaperSpace (" + currentLayoutName + ")");
                res["tile_mode"] = isModelTab;
                res["dbmod"] = System.Convert.ToInt32(Application.GetSystemVariable("DBMOD"), CultureInfo.InvariantCulture);
                res["insunits"] = unitInfo["code"];
                res["insunits_name"] = unitInfo["name"];
                res["units_per_cm"] = unitInfo["units_per_cm"];
                res["units_per_m"] = unitInfo["units_per_m"];
                res["total_entities"] = totalEntities;
                res["named_blocks_total"] = namedBlocksTotal;
                res["named_visible_blocks_total"] = namedVisibleBlocksTotal;
                res["anonymous_system_blocks_total"] = anonOrSystemBlocksTotal;
                if (legendThumbnailBlocks > 0) res["legend_thumbnail_blocks"] = legendThumbnailBlocks;
                res["named_block_counts"] = namedBlockCounts;
                res["anonymous_or_system_block_counts"] = anonOrSystemBlockCounts;
                res["selection_count"] = selectedHandles.Count;
                res["selected_handles"] = selectedHandles;
                try { res["pickfirst"] = System.Convert.ToInt32(Application.GetSystemVariable("PICKFIRST"), CultureInfo.InvariantCulture); } catch { }
                if (selectedHandles.Count == 0)
                {
                    List<string> cachedCurveHandles = GetValidCachedUserCurveHandles(db);
                    if (cachedCurveHandles.Count > 0)
                        res["last_user_selected_curve_handles"] = cachedCurveHandles;
                }
                if (selectedHandles.Count > 0)
                {
                    res["selected_type_counts"] = selectedTypeCounts;
                    res["selected_entities_truncated"] = selectedHandles.Count > selectedEntityList.Count;
                    res["selected_entities"] = selectedEntityList;
                    if (selectedExtents.HasValue)
                    {
                        Extents3d sex = selectedExtents.Value;
                        res["selected_bounds"] = new double[] { Round(sex.MinPoint.X), Round(sex.MinPoint.Y), Round(sex.MaxPoint.X), Round(sex.MaxPoint.Y) };
                    }
                }
                res["type_counts"] = typeCounts;
                res["layers"] = layerList;
                res["blocks"] = blockList;
                res["layouts"] = layoutList;
                if (paperspaceLayouts.Count > 0) res["paperspace_layouts"] = paperspaceLayouts;
                if (recentTailLayerCounts.Count > 0) res["recent_tail_layer_counts"] = recentTailLayerCounts;
                if (recentTailEntities.Count > 0) res["recent_tail_entities"] = recentTailEntities;
                if (totalExtents.HasValue)
                {
                    Extents3d ex = totalExtents.Value;
                    res["extents"] = new Dictionary<string, object>
                    {
                        { "min", Pt2(ex.MinPoint.X, ex.MinPoint.Y) },
                        { "max", Pt2(ex.MaxPoint.X, ex.MaxPoint.Y) },
                        { "width", Round(ex.MaxPoint.X - ex.MinPoint.X) },
                        { "height", Round(ex.MaxPoint.Y - ex.MinPoint.Y) }
                    };
                }
                if (includeEntities)
                {
                    res["entities_returned"] = entityList.Count;
                    res["entities_truncated"] = totalEntities > entityList.Count;
                    res["entities"] = entityList;
                }
                if (textSamples.Count > 0)
                {
                    res["text_samples"] = textSamples;
                }
                return Ok(res);
            }
        }

        private static Dictionary<string, object> QueryEntities(Document doc, Dictionary<string, object> args)
        {
            string layerFilter = GetStr(args, "layer");
            string typeFilter = GetStr(args, "entity_type");
            string blockFilter = GetStr(args, "block_name");
            string textFilter = GetStr(args, "text_contains");
            bool selectedOnly = GetBool(args, "selected_only", false);
            bool excludeAnonymous = GetBool(args, "exclude_anonymous", false);
            bool excludeSystem = GetBool(args, "exclude_system", false);
            bool visibleOnly = GetBool(args, "visible_only", false);
            string space = (GetStr(args, "space") ?? "model").Trim().ToLowerInvariant();
            string order = (GetStr(args, "order") ?? "oldest_first").Trim().ToLowerInvariant();
            bool newestFirst = (order == "newest_first" || order == "tail" || order == "desc");
            HashSet<string> handleFilter = GetStringSet(args, "handles");
            double[] bbox = GetDoubleArray(args, "bbox");
            int limit = Math.Max(1, Math.Min(MaxQueryEntities, GetInt(args, "limit", 120)));
            int offset = Math.Max(0, GetInt(args, "offset", 0));

            List<string> selectedHandles = GetSelectedHandleStrings(doc);
            HashSet<string> selectedSet = new HashSet<string>(selectedHandles, StringComparer.OrdinalIgnoreCase);

            List<object> matched = new List<object>();
            List<string> allMatchedHandles = new List<string>();
            Dictionary<string, int> summaryByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> summaryByBlock = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Extents3d? groupExt = null;
            int totalMatched = 0;
            int namedMatchedCount = 0;
            int anonOrSystemMatchedCount = 0;

            Database db = doc.Database;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                List<KeyValuePair<string, ObjectId>> targetSpaces = ResolveTargetSpaces(db, tr, space);

                foreach (KeyValuePair<string, ObjectId> sp in targetSpaces)
                {
                    BlockTableRecord btrSpace = (BlockTableRecord)tr.GetObject(sp.Value, OpenMode.ForRead);
                    List<ObjectId> orderedIds = new List<ObjectId>();
                    foreach (ObjectId sid in btrSpace) orderedIds.Add(sid);
                    if (newestFirst) orderedIds.Reverse();

                    foreach (ObjectId id in orderedIds)
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        bool entVisible = IsEntityVisible(ent, layerHiddenFlags);
                        if (!EntityMatchesFilters(tr, ent, selectedSet, selectedOnly, handleFilter, layerFilter, typeFilter,
                            blockFilter, textFilter, bbox, excludeAnonymous, excludeSystem, visibleOnly, entVisible))
                            continue;

                        string hStr = ent.Handle.ToString();
                        string tn = ent.GetType().Name;
                        summaryByType[tn] = summaryByType.ContainsKey(tn) ? summaryByType[tn] + 1 : 1;

                        BlockReference br = ent as BlockReference;
                        if (br != null)
                        {
                            string bn = ResolveBlockName(tr, br);
                            bool isAnon = IsAnonymousBlockName(bn);
                            bool isSys = IsSystemBlockName(bn);
                            if (isAnon || isSys) anonOrSystemMatchedCount++;
                            else namedMatchedCount++;

                            Dictionary<string, object> bstat;
                            if (!summaryByBlock.ContainsKey(bn))
                            {
                                bstat = new Dictionary<string, object>
                                {
                                    { "count", 0 },
                                    { "visible_count", 0 },
                                    { "is_anonymous", isAnon },
                                    { "is_system", isSys },
                                    { "space", sp.Key }
                                };
                                summaryByBlock[bn] = bstat;
                            }
                            else
                            {
                                bstat = (Dictionary<string, object>)summaryByBlock[bn];
                            }
                            bstat["count"] = (int)bstat["count"] + 1;
                            if (entVisible) bstat["visible_count"] = (int)bstat["visible_count"] + 1;
                        }

                        try
                        {
                            Extents3d ex = ent.GeometricExtents;
                            if (IsFiniteBounds(ex))
                            {
                                if (!groupExt.HasValue) groupExt = ex;
                                else { Extents3d c = groupExt.Value; c.AddExtents(ex); groupExt = c; }
                            }
                        }
                        catch { }

                        if (allMatchedHandles.Count < MaxReturnedHandles)
                            allMatchedHandles.Add(hStr);

                        if (totalMatched >= offset && matched.Count < limit)
                        {
                            Dictionary<string, object> s = SerializeEntity(tr, ent, selectedSet, entVisible);
                            if (!string.Equals(sp.Key, "Model", StringComparison.OrdinalIgnoreCase))
                                s["space"] = sp.Key;
                            matched.Add(s);
                        }
                        totalMatched++;
                    }
                }
                tr.Commit();
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "total_matched", totalMatched },
                { "returned", matched.Count },
                { "offset", offset },
                { "named_blocks_matched", namedMatchedCount },
                { "anonymous_or_system_blocks_matched", anonOrSystemMatchedCount },
                { "summary_by_type", summaryByType },
                { "summary_by_block", summaryByBlock },
                { "matched_handles", allMatchedHandles },
                { "entities", matched }
            };
            if (groupExt.HasValue)
            {
                res["group_bounds"] = new double[]
                {
                    Round(groupExt.Value.MinPoint.X), Round(groupExt.Value.MinPoint.Y),
                    Round(groupExt.Value.MaxPoint.X), Round(groupExt.Value.MaxPoint.Y)
                };
            }
            return Ok(res);
        }

        private static Dictionary<string, object> SelectEntities(Document doc, Dictionary<string, object> args)
        {
            bool clear = GetBool(args, "clear", false);
            bool zoom = GetBool(args, "zoom", true);
            bool highlightBox = GetBool(args, "highlight_box", false);
            string highlightLayer = GetStr(args, "highlight_layer") ?? "_AI_HIGHLIGHT";
            int highlightColor = GetInt(args, "highlight_color_index", 2);

            Database db = doc.Database;
            bool switchedToModel = false;

            if (clear)
            {
                using (DocumentLock docLock = doc.LockDocument())
                {
                    try { Application.SetSystemVariable("PICKFIRST", 1); } catch { }
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                    doc.Editor.Regen();
                    doc.Editor.UpdateScreen();
                }
                return Ok(new Dictionary<string, object>
                {
                    { "cleared", true },
                    { "selected_count", 0 }
                });
            }

            List<string> explicitHandles = GetStringList(args, "handles");
            string layerFilter = GetStr(args, "layer");
            string typeFilter = GetStr(args, "entity_type");
            string blockFilter = GetStr(args, "block_name");
            string textFilter = GetStr(args, "text_contains");
            double[] bbox = GetDoubleArray(args, "bbox");
            bool excludeAnonymous = GetBool(args, "exclude_anonymous", false);
            bool excludeSystem = GetBool(args, "exclude_system", false);
            bool visibleOnly = GetBool(args, "visible_only", false);
            bool selectAll = GetBool(args, "select_all", false);

            if (string.IsNullOrEmpty(typeFilter) && string.IsNullOrEmpty(blockFilter) &&
                string.IsNullOrEmpty(layerFilter) && string.IsNullOrEmpty(textFilter) &&
                bbox == null && (excludeAnonymous || excludeSystem))
            {
                typeFilter = "BlockReference";
            }

            if (!selectAll && explicitHandles.Count == 0 && string.IsNullOrEmpty(typeFilter) &&
                string.IsNullOrEmpty(blockFilter) && string.IsNullOrEmpty(layerFilter) &&
                string.IsNullOrEmpty(textFilter) && (bbox == null || bbox.Length < 4))
            {
                return Err("select_entities requires at least one filter (handles, entity_type, block_name, layer, text_contains, bbox) or explicit select_all=true.");
            }

            List<ObjectId> targetIds = new List<ObjectId>();
            List<string> selectedHandleStrings = new List<string>();
            Dictionary<string, int> selectedBlockCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Extents3d? groupExt = null;
            string boxHandle = null;

            using (DocumentLock docLock = doc.LockDocument())
            {
                // AutoCAD grips/SetImpliedSelection for ModelSpace objects only display visibly in Model space
                if (!db.TileMode)
                {
                    try
                    {
                        LayoutManager.Current.CurrentLayout = "Model";
                        switchedToModel = true;
                    }
                    catch { }
                }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                    if (explicitHandles.Count > 0)
                    {
                        foreach (string h in explicitHandles)
                        {
                            ObjectId id = ResolveHandle(db, h);
                            if (id.IsNull || id.IsErased) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.OwnerId != ms.ObjectId) continue;
                            targetIds.Add(id);
                        }
                    }
                    else
                    {
                        HashSet<string> dummySel = new HashSet<string>();
                        HashSet<string> dummyHandle = new HashSet<string>();
                        foreach (ObjectId id in ms)
                        {
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null) continue;
                            bool entVisible = IsEntityVisible(ent, layerHiddenFlags);
                            if (!EntityMatchesFilters(tr, ent, dummySel, false, dummyHandle, layerFilter, typeFilter,
                                blockFilter, textFilter, bbox, excludeAnonymous, excludeSystem, visibleOnly, entVisible))
                                continue;
                            targetIds.Add(id);
                        }
                    }

                    foreach (ObjectId tid in targetIds)
                    {
                        Entity ent = tr.GetObject(tid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        if (selectedHandleStrings.Count < MaxReturnedHandles)
                            selectedHandleStrings.Add(ent.Handle.ToString());
                        BlockReference br = ent as BlockReference;
                        if (br != null)
                        {
                            string bn = ResolveBlockName(tr, br);
                            selectedBlockCounts[bn] = selectedBlockCounts.ContainsKey(bn) ? selectedBlockCounts[bn] + 1 : 1;
                        }
                        try
                        {
                            Extents3d ex = ent.GeometricExtents;
                            if (IsFiniteBounds(ex))
                            {
                                if (!groupExt.HasValue) groupExt = ex;
                                else { Extents3d c = groupExt.Value; c.AddExtents(ex); groupExt = c; }
                            }
                        }
                        catch { }
                    }

                    if (highlightBox && groupExt.HasValue)
                    {
                        Extents3d ge = groupExt.Value;
                        double span = Math.Max(ge.MaxPoint.X - ge.MinPoint.X, ge.MaxPoint.Y - ge.MinPoint.Y);
                        double pad = Math.Max(20.0, Math.Min(500.0, span * 0.03));
                        Extents3d boxExt = new Extents3d(
                            new Point3d(ge.MinPoint.X - pad, ge.MinPoint.Y - pad, 0.0),
                            new Point3d(ge.MaxPoint.X + pad, ge.MaxPoint.Y + pad, 0.0));
                        string safeBoxLayer = EnsureLayerSafe(db, tr, highlightLayer, ClampColor(highlightColor), null);
                        Polyline box = CreateFramePolyline(boxExt, safeBoxLayer, ClampColor(highlightColor));
                        ms.AppendEntity(box);
                        tr.AddNewlyCreatedDBObject(box, true);
                        boxHandle = box.Handle.ToString();
                        groupExt = boxExt;
                    }

                    tr.Commit();
                }

                try { Application.SetSystemVariable("PICKFIRST", 1); } catch { }
                if (zoom && groupExt.HasValue)
                {
                    ZoomToExtents3d(doc, groupExt.Value, 1.25);
                }
                else
                {
                    doc.Editor.Regen();
                }
                if (targetIds.Count > 0)
                {
                    doc.Editor.SetImpliedSelection(targetIds.ToArray());
                }
                else if (GetBool(args, "clear_if_empty", false))
                {
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                }
                doc.Editor.UpdateScreen();
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "selected_count", targetIds.Count },
                { "selected_handles", selectedHandleStrings },
                { "summary_by_block", selectedBlockCounts },
                { "switched_to_model", switchedToModel }
            };
            if (groupExt.HasValue)
            {
                res["group_bounds"] = new double[]
                {
                    Round(groupExt.Value.MinPoint.X), Round(groupExt.Value.MinPoint.Y),
                    Round(groupExt.Value.MaxPoint.X), Round(groupExt.Value.MaxPoint.Y)
                };
            }
            if (!string.IsNullOrEmpty(boxHandle)) res["highlight_box_handle"] = boxHandle;
            return Ok(res);
        }

        private static Dictionary<string, object> DrawEntities(Document doc, Dictionary<string, object> args)
        {
            List<object> items = AsList(GetObj(args, "entities"));
            if (items.Count == 0)
                return Err("'entities' array is required and cannot be empty.");

            string defaultLayer = GetStr(args, "layer");
            int? defaultColor = GetNullableInt(args, "color_index");
            bool previewOnly = GetBool(args, "preview_only", false);
            bool zoom = GetBool(args, "zoom", true);
            string zoomMode = (GetStr(args, "zoom_mode") ?? "auto").Trim().ToLowerInvariant();

            Database db = doc.Database;
            List<string> createdHandles = new List<string>();
            List<ObjectId> createdObjectIds = new List<ObjectId>();
            List<object> createdDetails = new List<object>();
            List<string> warnings = new List<string>();
            string effectiveDefaultLayer = null;
            Extents3d? createdExt = null;
            int preExistingCount = 0;

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (previewOnly && string.IsNullOrEmpty(defaultLayer))
                        defaultLayer = DrawingExecutionCommands.ReviewLayerName;

                    if (string.IsNullOrEmpty(defaultLayer))
                    {
                        LayerTableRecord curLtr = tr.GetObject(db.Clayer, OpenMode.ForRead) as LayerTableRecord;
                        effectiveDefaultLayer = curLtr != null ? curLtr.Name : "0";
                    }
                    else
                    {
                        short? layerClr = defaultColor.HasValue ? (short?)ClampColor(defaultColor.Value) : (previewOnly ? (short?)1 : null);
                        effectiveDefaultLayer = EnsureLayerSafe(db, tr, defaultLayer, layerClr, null);
                    }

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    foreach (ObjectId existingId in modelSpace) { preExistingCount++; if (preExistingCount > 45) break; }

                    foreach (object rawItem in items)
                    {
                        Dictionary<string, object> item = AsDict(rawItem);
                        if (item == null) continue;

                        string itemLayerReq = GetStr(item, "layer");
                        int? itemColor = GetNullableInt(item, "color_index");
                        string itemLinetype = GetStr(item, "linetype");
                        string targetLayer = !string.IsNullOrEmpty(itemLayerReq)
                            ? EnsureLayerSafe(db, tr, itemLayerReq, itemColor.HasValue ? (short?)ClampColor(itemColor.Value) : null, itemLinetype)
                            : effectiveDefaultLayer;

                        string itemType = (GetStr(item, "type") ?? "").Trim().ToLowerInvariant();
                        if (itemType == "leader" || itemType == "callout")
                        {
                            string lWarn;
                            List<Entity> leaderParts = CreateLeaderEntities(db, tr, item, targetLayer, itemColor ?? (previewOnly ? 1 : (int?)null), out lWarn);
                            if (!string.IsNullOrEmpty(lWarn)) warnings.Add(lWarn);
                            foreach (Entity part in leaderParts)
                            {
                                ObjectId cid = modelSpace.AppendEntity(part);
                                tr.AddNewlyCreatedDBObject(part, true);
                                createdObjectIds.Add(cid);
                                string ch = part.Handle.ToString();
                                createdHandles.Add(ch);
                                try
                                {
                                    Extents3d cex = part.GeometricExtents;
                                    if (IsFiniteBounds(cex))
                                    {
                                        if (!createdExt.HasValue) createdExt = cex;
                                        else { Extents3d cur = createdExt.Value; cur.AddExtents(cex); createdExt = cur; }
                                    }
                                }
                                catch { }
                            }
                            if (leaderParts.Count > 0 && createdDetails.Count < 40)
                            {
                                createdDetails.Add(new Dictionary<string, object>
                                {
                                    { "handle", leaderParts[0].Handle.ToString() },
                                    { "type", "LeaderCallout" },
                                    { "layer", targetLayer },
                                    { "parts_count", leaderParts.Count }
                                });
                            }
                            continue;
                        }

                        string warn;
                        Entity created = CreateSingleEntity(db, tr, bt, item, targetLayer, itemColor ?? (previewOnly ? 1 : (int?)null), out warn);
                        if (!string.IsNullOrEmpty(warn)) warnings.Add(warn);
                        if (created != null)
                        {
                            ObjectId cid = modelSpace.AppendEntity(created);
                            tr.AddNewlyCreatedDBObject(created, true);
                            FinalizeAppendedEntity(db, created, item);
                            AttachAttributesIfBlock(tr, bt, created as BlockReference, item);
                            createdObjectIds.Add(cid);
                            string ch = created.Handle.ToString();
                            createdHandles.Add(ch);
                            try
                            {
                                Extents3d cex = created.GeometricExtents;
                                if (IsFiniteBounds(cex))
                                {
                                    if (!createdExt.HasValue) createdExt = cex;
                                    else { Extents3d cur = createdExt.Value; cur.AddExtents(cex); createdExt = cur; }
                                }
                                if (createdDetails.Count < 40)
                                {
                                    Dictionary<string, object> info = new Dictionary<string, object>
                                    {
                                        { "handle", ch },
                                        { "type", created.GetType().Name },
                                        { "layer", created.Layer ?? "0" },
                                        { "bounds", new double[] { Round(cex.MinPoint.X), Round(cex.MinPoint.Y), Round(cex.MaxPoint.X), Round(cex.MaxPoint.Y) } }
                                    };
                                    createdDetails.Add(info);
                                }
                            }
                            catch
                            {
                                if (createdDetails.Count < 40)
                                {
                                    createdDetails.Add(new Dictionary<string, object>
                                    {
                                        { "handle", ch },
                                        { "type", created.GetType().Name },
                                        { "layer", created.Layer ?? "0" }
                                    });
                                }
                            }
                        }
                    }

                    db.UpdateExt(true);
                    tr.Commit();
                }

                if (createdObjectIds.Count > 0)
                {
                    CadUndoEntry undoEntry = new CadUndoEntry();
                    undoEntry.ToolName = "draw_entities";
                    undoEntry.Summary = "Created " + createdObjectIds.Count.ToString(CultureInfo.InvariantCulture) + " entity(s)";
                    undoEntry.CreatedIds.AddRange(createdObjectIds);
                    PushUndoEntry(doc, undoEntry);
                }

                if (zoom && createdHandles.Count > 0)
                {
                    if ((zoomMode == "created" || (zoomMode == "auto" && preExistingCount > 40)) && createdExt.HasValue)
                    {
                        if (!db.TileMode) { try { LayoutManager.Current.CurrentLayout = "Model"; } catch { } }
                        ZoomToExtents3d(doc, createdExt.Value, 1.4);
                    }
                    else
                    {
                        DrawingExecutionCommands.ZoomExtents(doc);
                    }
                }
                else
                {
                    doc.Editor.Regen();
                    doc.Editor.UpdateScreen();
                }
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "created_count", createdHandles.Count },
                { "created_handles", createdHandles },
                { "created_entities", createdDetails },
                { "layer", effectiveDefaultLayer }
            };
            if (createdExt.HasValue)
            {
                res["created_bounds"] = new double[]
                {
                    Round(createdExt.Value.MinPoint.X), Round(createdExt.Value.MinPoint.Y),
                    Round(createdExt.Value.MaxPoint.X), Round(createdExt.Value.MaxPoint.Y)
                };
            }
            if (warnings.Count > 0) res["warnings"] = warnings;
            return Ok(res);
        }

        private sealed class BlockLegendRowSpec
        {
            public string Name;
            public ObjectId BlockRecordId;
            public int TotalCount;
            public int VisibleCount;
            public string Description;
            public double RawSpan;
            public Dictionary<string, int> ColorVoteCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, SavedColorState> ColorVoteStates = new Dictionary<string, SavedColorState>(StringComparer.Ordinal);
            public SavedColorState DominantColor;
        }

        private static Dictionary<string, object> DrawBlockLegend(Document doc, Dictionary<string, object> args)
        {
            Dictionary<string, string> descMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string> registerDesc = delegate(string rawKey, string rawVal)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(rawVal)) return;
                string k = rawKey.Trim();
                string v = rawVal.Trim();
                descMap[k] = v;
                // Handle occasional LLM quirks like "parir ai 4:15" or "surg 220 (1)"
                string strippedColon = Regex.Replace(k, @"\s*:\s*\d+$", "").Trim();
                if (!string.IsNullOrEmpty(strippedColon) && !descMap.ContainsKey(strippedColon))
                    descMap[strippedColon] = v;
                string strippedParen = Regex.Replace(k, @"\s*\(\d+\)$", "").Trim();
                if (!string.IsNullOrEmpty(strippedParen) && !descMap.ContainsKey(strippedParen))
                    descMap[strippedParen] = v;
            };
            object rawDescObj = GetObj(args, "descriptions");
            Dictionary<string, object> rawDescs = AsDict(rawDescObj);
            if (rawDescs != null)
            {
                foreach (KeyValuePair<string, object> kv in rawDescs)
                {
                    if (kv.Value != null) registerDesc(kv.Key, kv.Value.ToString());
                }
            }
            else
            {
                foreach (object itemObj in AsList(rawDescObj))
                {
                    Dictionary<string, object> item = AsDict(itemObj);
                    if (item == null) continue;
                    string bKey = GetStr(item, "block_name") ?? GetStr(item, "name") ?? GetStr(item, "block");
                    string bDesc = GetStr(item, "description") ?? GetStr(item, "desc");
                    registerDesc(bKey, bDesc);
                }
            }

            HashSet<string> requestedBlockNames = GetStringSet(args, "block_names");
            List<string> excludeLegendBlocks = new List<string>();
            excludeLegendBlocks.AddRange(ExtractStringListArg(args, "exclude_blocks"));
            excludeLegendBlocks.AddRange(ExtractStringListArg(args, "exclude_block_names"));
            excludeLegendBlocks.AddRange(ExtractStringListArg(args, "exclude_block_name"));
            HashSet<string> requestedHandles = GetStringSet(args, "handles");
            string footerLabelReq = GetStr(args, "footer_label");
            bool includeSystem = GetBool(args, "include_system_blocks", false);
            bool includeAnonymous = GetBool(args, "include_anonymous", false);
            bool visibleOnly = GetBool(args, "visible_only", false);
            string legendLayer = GetStr(args, "layer");
            if (string.IsNullOrWhiteSpace(legendLayer)) legendLayer = "_AI_BLOCK_LEGEND";
            bool replaceExisting = GetBool(args, "replace_existing", true);
            List<string> clearLayers = GetStringList(args, "clear_layers");
            List<string> clearHandles = GetStringList(args, "clear_handles");
            string title = GetStr(args, "title");
            if (string.IsNullOrWhiteSpace(title))
                title = "جدول حصر ومفاتيح البلوكات (BLOCK LEGEND)";
            string positionStr = (GetStr(args, "position") ?? "outside_right").Trim().ToLowerInvariant();
            double[] explicitOrigin = GetDoubleArray(args, "position");
            double? customGap = GetNullableDouble(args, "gap");
            bool zoom = GetBool(args, "zoom", true);
            string zoomMode = (GetStr(args, "zoom_mode") ?? "legend").Trim().ToLowerInvariant();

            Database db = doc.Database;
            int erasedCount = 0;
            List<string> warnings = new List<string>();
            List<string> createdHandles = new List<string>();
            List<ObjectId> legendErasedIds = new List<ObjectId>();
            List<object> rowSummaries = new List<object>();
            Extents3d? drawingExt = null;
            Extents3d? legendExt = null;
            string safeLegendLayer = legendLayer;
            int totalBlocksRepresented = 0;
            bool switchedToModel = false;

            using (DocumentLock docLock = doc.LockDocument())
            {
                if (zoom && !db.TileMode)
                {
                    try { LayoutManager.Current.CurrentLayout = "Model"; switchedToModel = true; } catch { }
                }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    safeLegendLayer = EnsureLayerSafe(db, tr, legendLayer, 7, null);
                    ObjectId unicodeStyleId = EnsureUnicodeTextStyle(db, tr);
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                    // 1. Erase old legend / broken table handles BEFORE measuring drawing bounds
                    HashSet<ObjectId> erasedIds = new HashSet<ObjectId>();
                    foreach (string ch in clearHandles)
                    {
                        ObjectId cid = ResolveHandle(db, ch);
                        if (cid.IsNull || cid.IsErased || erasedIds.Contains(cid)) continue;
                        try
                        {
                            Entity ce = tr.GetObject(cid, OpenMode.ForWrite) as Entity;
                            if (ce != null && ce.OwnerId == ms.ObjectId)
                            {
                                ce.Erase(true);
                                erasedIds.Add(cid);
                                legendErasedIds.Add(cid);
                                erasedCount++;
                            }
                        }
                        catch { }
                    }

                    HashSet<string> layersToErase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (replaceExisting && !string.Equals(safeLegendLayer, "0", StringComparison.OrdinalIgnoreCase))
                        layersToErase.Add(safeLegendLayer);
                    foreach (string cl in clearLayers)
                    {
                        if (string.Equals(cl, "0", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(cl, "Defpoints", StringComparison.OrdinalIgnoreCase))
                        {
                            warnings.Add("Skipped unsafe clear_layers entry '" + cl + "'; use clear_handles to remove specific entities on layer 0.");
                            continue;
                        }
                        layersToErase.Add(cl);
                    }

                    if (layersToErase.Count > 0)
                    {
                        foreach (ObjectId mid in ms)
                        {
                            if (erasedIds.Contains(mid)) continue;
                            Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                            if (me == null) continue;
                            if (layersToErase.Contains(me.Layer ?? ""))
                            {
                                try
                                {
                                    me.UpgradeOpen();
                                    me.Erase(true);
                                    erasedIds.Add(mid);
                                    legendErasedIds.Add(mid);
                                    erasedCount++;
                                }
                                catch { }
                            }
                        }
                    }

                    // 2. Scan remaining drawing extents, block counts, and dominant block colors
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                    Dictionary<string, SavedColorState> layerColorMap = new Dictionary<string, SavedColorState>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        LayerTable ltLegend = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        foreach (ObjectId lid in ltLegend)
                        {
                            LayerTableRecord ltr = tr.GetObject(lid, OpenMode.ForRead) as LayerTableRecord;
                            if (ltr != null) layerColorMap[ltr.Name] = CaptureColor(ltr.Color);
                        }
                    }
                    catch { }

                    Dictionary<string, BlockLegendRowSpec> blockStats = new Dictionary<string, BlockLegendRowSpec>(StringComparer.OrdinalIgnoreCase);
                    List<double> blockSpans = new List<double>();
                    Extents3d? visExt = null;
                    Extents3d? allExt = null;

                    foreach (ObjectId mid in ms)
                    {
                        if (mid.IsErased) continue;
                        Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                        if (IsLegendOrHighlightLayer(ent.Layer)) continue;
                        bool entVis = IsEntityVisible(ent, layerHiddenFlags);

                        try
                        {
                            Extents3d ex = ent.GeometricExtents;
                            if (IsFiniteBounds(ex))
                            {
                                if (!allExt.HasValue) allExt = ex;
                                else { Extents3d c = allExt.Value; c.AddExtents(ex); allExt = c; }
                                if (entVis)
                                {
                                    if (!visExt.HasValue) visExt = ex;
                                    else { Extents3d c = visExt.Value; c.AddExtents(ex); visExt = c; }
                                }
                            }
                        }
                        catch { }

                        BlockReference br = ent as BlockReference;
                        if (br != null)
                        {
                            string bn = ResolveBlockName(tr, br);
                            if (string.IsNullOrEmpty(bn)) continue;
                            if (!includeAnonymous && IsAnonymousBlockName(bn)) continue;
                            if (!includeSystem && (IsSystemBlockName(bn) || string.Equals(ent.Layer, "ASHADE", StringComparison.OrdinalIgnoreCase))) continue;
                            if (requestedHandles.Count > 0 && !requestedHandles.Contains(ent.Handle.ToString())) continue;
                            if (requestedBlockNames.Count > 0 && !requestedBlockNames.Contains(bn)) continue;
                            bool excludedLegBlk = false;
                            for (int ei = 0; ei < excludeLegendBlocks.Count; ei++)
                            {
                                string exPat = excludeLegendBlocks[ei];
                                if (string.IsNullOrWhiteSpace(exPat)) continue;
                                if (string.Equals(bn, exPat.Trim(), StringComparison.OrdinalIgnoreCase) || MatchesPatternOrSubstring(bn, exPat))
                                {
                                    excludedLegBlk = true;
                                    break;
                                }
                            }
                            if (excludedLegBlk) continue;

                            BlockLegendRowSpec spec;
                            if (!blockStats.TryGetValue(bn, out spec))
                            {
                                ObjectId defId = bt.Has(bn) ? bt[bn] : GetBlockDefinitionId(br);
                                spec = new BlockLegendRowSpec
                                {
                                    Name = bn,
                                    BlockRecordId = defId,
                                    TotalCount = 0,
                                    VisibleCount = 0,
                                    Description = "",
                                    RawSpan = 0.0
                                };
                                blockStats[bn] = spec;
                            }
                            spec.TotalCount++;
                            if (entVis) spec.VisibleCount++;

                            try
                            {
                                SavedColorState resolvedClr = null;
                                Color brClr = br.Color;
                                if (brClr != null && (brClr.ColorMethod == ColorMethod.ByAci || brClr.ColorMethod == ColorMethod.ByColor))
                                {
                                    resolvedClr = CaptureColor(brClr);
                                }
                                else if ((brClr == null || brClr.ColorMethod == ColorMethod.ByLayer) &&
                                         !string.IsNullOrEmpty(ent.Layer) && layerColorMap.ContainsKey(ent.Layer))
                                {
                                    SavedColorState lc = layerColorMap[ent.Layer];
                                    if (lc != null && (lc.Method == ColorMethod.ByColor ||
                                        (lc.Method == ColorMethod.ByAci && lc.ColorIndex != 7 && lc.ColorIndex != 256 && lc.ColorIndex != 0)))
                                    {
                                        resolvedClr = lc;
                                    }
                                }

                                string cKey = "DEFAULT";
                                if (resolvedClr != null)
                                {
                                    cKey = resolvedClr.Method == ColorMethod.ByColor
                                        ? string.Format(CultureInfo.InvariantCulture, "RGB:{0},{1},{2}", resolvedClr.R, resolvedClr.G, resolvedClr.B)
                                        : ("ACI:" + resolvedClr.ColorIndex.ToString(CultureInfo.InvariantCulture));
                                    spec.ColorVoteStates[cKey] = resolvedClr;
                                }
                                spec.ColorVoteCounts[cKey] = spec.ColorVoteCounts.ContainsKey(cKey) ? spec.ColorVoteCounts[cKey] + 1 : 1;
                            }
                            catch { }

                            if (spec.RawSpan <= 1e-6)
                            {
                                try
                                {
                                    Extents3d bex = br.GeometricExtents;
                                    if (IsFiniteBounds(bex))
                                    {
                                        double bsp = Math.Max(bex.MaxPoint.X - bex.MinPoint.X, bex.MaxPoint.Y - bex.MinPoint.Y);
                                        if (bsp > 1e-6)
                                        {
                                            spec.RawSpan = bsp;
                                            blockSpans.Add(bsp);
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }

                    drawingExt = visExt.HasValue ? visExt : allExt;

                    List<BlockLegendRowSpec> entries = new List<BlockLegendRowSpec>();
                    foreach (BlockLegendRowSpec item in blockStats.Values)
                    {
                        int chosenCount = visibleOnly ? item.VisibleCount : item.TotalCount;
                        if (chosenCount <= 0 && requestedBlockNames.Count == 0) continue;

                        string bestColorKey = null;
                        int bestColorVotes = 0;
                        foreach (KeyValuePair<string, int> cv in item.ColorVoteCounts)
                        {
                            if (cv.Value > bestColorVotes)
                            {
                                bestColorVotes = cv.Value;
                                bestColorKey = cv.Key;
                            }
                        }
                        SavedColorState domClr;
                        if (!string.IsNullOrEmpty(bestColorKey) && bestColorKey != "DEFAULT" &&
                            item.ColorVoteStates.TryGetValue(bestColorKey, out domClr))
                        {
                            item.DominantColor = domClr;
                        }

                        string desc;
                        if (descMap.TryGetValue(item.Name, out desc) && !string.IsNullOrWhiteSpace(desc))
                            item.Description = desc;
                        else
                            item.Description = "بلوك هندسي: " + item.Name;
                        totalBlocksRepresented += chosenCount;
                        entries.Add(item);
                    }

                    if (entries.Count == 0)
                    {
                        db.UpdateExt(true);
                        tr.Commit();
                        return Err("No matching named blocks found in ModelSpace to build Block Legend.");
                    }

                    entries.Sort(delegate(BlockLegendRowSpec a, BlockLegendRowSpec b)
                    {
                        int ca = visibleOnly ? a.VisibleCount : a.TotalCount;
                        int cb = visibleOnly ? b.VisibleCount : b.TotalCount;
                        int cmp = cb.CompareTo(ca);
                        return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    });

                    // 3. Unit-agnostic proportional table sizing
                    double medBlockSpan = 0.0;
                    if (blockSpans.Count > 0)
                    {
                        blockSpans.Sort();
                        medBlockSpan = blockSpans[blockSpans.Count / 2];
                    }

                    double dwgW = drawingExt.HasValue ? Math.Max(1e-3, drawingExt.Value.MaxPoint.X - drawingExt.Value.MinPoint.X) : 100.0;
                    double dwgH = drawingExt.HasValue ? Math.Max(1e-3, drawingExt.Value.MaxPoint.Y - drawingExt.Value.MinPoint.Y) : 100.0;
                    double dwgSpan = Math.Max(dwgW, dwgH);
                    double targetH = Math.Max(dwgH * 0.85, dwgSpan * 0.45);
                    double rowH = targetH / Math.Max(4.0, Math.Min(22.0, entries.Count + 3.15));
                    if (medBlockSpan > 1e-6)
                    {
                        double minRowH = dwgSpan * 0.018;
                        double maxRowH = dwgSpan * 0.14;
                        double blockSuggested = medBlockSpan * 1.35;
                        rowH = Math.Max(rowH, Math.Min(maxRowH, blockSuggested));
                        rowH = Math.Max(minRowH, Math.Min(maxRowH, rowH));
                    }

                    double titleH = rowH * 1.20;
                    double headerH = rowH * 1.08;
                    double footerH = rowH * 0.95;
                    double[] colW = new double[]
                    {
                        rowH * 1.15, // م
                        rowH * 2.55, // شكل البلوك
                        rowH * 3.85, // اسم البلوك
                        rowH * 6.45, // الوصف الهندسي
                        rowH * 1.50  // الكمية
                    };
                    double tableW = 0.0;
                    for (int i = 0; i < colW.Length; i++) tableW += colW[i];
                    double tableH = titleH + headerH + (entries.Count * rowH) + footerH;

                    double gap = customGap.HasValue
                        ? Math.Max(0.0, customGap.Value)
                        : Math.Max(dwgSpan * 0.08, tableW * 0.12);

                    double x0 = 0.0;
                    double yTop = tableH;
                    if (explicitOrigin != null && explicitOrigin.Length >= 2)
                    {
                        x0 = explicitOrigin[0];
                        yTop = explicitOrigin[1];
                    }
                    else if (drawingExt.HasValue)
                    {
                        Extents3d de = drawingExt.Value;
                        double dMidY = (de.MinPoint.Y + de.MaxPoint.Y) / 2.0;
                        double dMidX = (de.MinPoint.X + de.MaxPoint.X) / 2.0;
                        if (positionStr == "outside_left")
                        {
                            x0 = de.MinPoint.X - gap - tableW;
                            yTop = dMidY + tableH / 2.0;
                        }
                        else if (positionStr == "outside_top")
                        {
                            x0 = dMidX - tableW / 2.0;
                            yTop = de.MaxPoint.Y + gap + tableH;
                        }
                        else if (positionStr == "outside_bottom")
                        {
                            x0 = dMidX - tableW / 2.0;
                            yTop = de.MinPoint.Y - gap;
                        }
                        else
                        {
                            // Default: outside_right
                            x0 = de.MaxPoint.X + gap;
                            yTop = dMidY + tableH / 2.0;
                        }

                        // Avoid colliding with an existing _AI_COORD_TABLE on the same side
                        Extents3d? coordTblExt = CollectLayerExtents(ms, tr, "_AI_COORD_TABLE");
                        if (coordTblExt.HasValue && IsFiniteBounds(coordTblExt.Value))
                        {
                            Extents3d ce = coordTblExt.Value;
                            double lx1 = x0, ly1 = yTop - tableH, lx2 = x0 + tableW, ly2 = yTop;
                            bool hitCoord = !(lx2 < ce.MinPoint.X - rowH * 0.5 || lx1 > ce.MaxPoint.X + rowH * 0.5 ||
                                              ly2 < ce.MinPoint.Y - rowH * 0.5 || ly1 > ce.MaxPoint.Y + rowH * 0.5);
                            if (hitCoord)
                            {
                                yTop = ce.MaxPoint.Y + Math.Max(rowH * 1.8, dwgSpan * 0.025) + tableH;
                            }
                        }
                    }

                    double[] colX = new double[colW.Length + 1];
                    colX[0] = x0;
                    for (int i = 0; i < colW.Length; i++) colX[i + 1] = colX[i] + colW[i];

                    double yTitleTop = yTop;
                    double yTitleBot = yTop - titleH;
                    double yHeaderBot = yTitleBot - headerH;
                    double yDataBot = yHeaderBot - (entries.Count * rowH);
                    double yBottom = yDataBot - footerH;

                    // 4. Outer Frame & Grid Lines (Vertical dividers STOP cleanly at yTitleBot!)
                    Extents3d outerBounds = new Extents3d(new Point3d(x0, yBottom, 0.0), new Point3d(x0 + tableW, yTitleTop, 0.0));
                    Polyline outerFrame = CreateFramePolyline(outerBounds, safeLegendLayer, 7);
                    outerFrame.ConstantWidth = rowH * 0.02;
                    ms.AppendEntity(outerFrame);
                    tr.AddNewlyCreatedDBObject(outerFrame, true);
                    createdHandles.Add(outerFrame.Handle.ToString());
                    legendExt = outerBounds;

                    Action<double, double, double, double, short> addGridLine = delegate(double lx1, double ly1, double lx2, double ly2, short clr)
                    {
                        Line gl = new Line(new Point3d(lx1, ly1, 0.0), new Point3d(lx2, ly2, 0.0));
                        gl.Layer = safeLegendLayer;
                        gl.ColorIndex = clr;
                        ms.AppendEntity(gl);
                        tr.AddNewlyCreatedDBObject(gl, true);
                        createdHandles.Add(gl.Handle.ToString());
                    };

                    // Horizontal dividers
                    addGridLine(x0, yTitleBot, x0 + tableW, yTitleBot, 7);
                    addGridLine(x0, yHeaderBot, x0 + tableW, yHeaderBot, 7);
                    for (int r = 1; r <= entries.Count; r++)
                    {
                        double ry = yHeaderBot - (r * rowH);
                        addGridLine(x0, ry, x0 + tableW, ry, 8);
                    }

                    // Inner vertical dividers (start from yTitleBot down to yDataBot; col 4 divider continues through footer)
                    for (int c = 1; c < colW.Length; c++)
                    {
                        double vBot = (c == colW.Length - 1) ? yBottom : yDataBot;
                        addGridLine(colX[c], yTitleBot, colX[c], vBot, 8);
                    }

                    // 5. Merged Title Row & Column Headers using TrueType Unicode MText
                    MText titleMt = CreateLegendCellMText(
                        ms, tr, unicodeStyleId, title,
                        x0 + tableW / 2.0, (yTitleTop + yTitleBot) / 2.0,
                        tableW * 0.94, titleH * 0.78, titleH * 0.36, rowH,
                        AttachmentPoint.MiddleCenter, 2, safeLegendLayer);
                    createdHandles.Add(titleMt.Handle.ToString());

                    string[] headers = new string[] { "م", "شكل البلوك", "اسم البلوك", "الوصف الهندسي", "الكمية" };
                    for (int c = 0; c < headers.Length; c++)
                    {
                        MText hMt = CreateLegendCellMText(
                            ms, tr, unicodeStyleId, headers[c],
                            colX[c] + colW[c] / 2.0, (yTitleBot + yHeaderBot) / 2.0,
                            colW[c] * 0.90, headerH * 0.78, headerH * 0.28, rowH,
                            AttachmentPoint.MiddleCenter, 4, safeLegendLayer);
                        createdHandles.Add(hMt.Handle.ToString());
                    }

                    // 6. Data Rows: Index, Real Centered Block Symbol, Block Name, Arabic Description, Qty
                    for (int r = 0; r < entries.Count; r++)
                    {
                        BlockLegendRowSpec entry = entries[r];
                        double rTop = yHeaderBot - (r * rowH);
                        double rBot = rTop - rowH;
                        double rCy = (rTop + rBot) / 2.0;
                        int qty = visibleOnly ? entry.VisibleCount : entry.TotalCount;

                        // Col 0: Row number
                        MText numMt = CreateLegendCellMText(
                            ms, tr, unicodeStyleId, (r + 1).ToString(CultureInfo.InvariantCulture),
                            colX[0] + colW[0] / 2.0, rCy,
                            colW[0] * 0.85, rowH * 0.76, rowH * 0.24, rowH,
                            AttachmentPoint.MiddleCenter, 7, safeLegendLayer);
                        createdHandles.Add(numMt.Handle.ToString());

                        // Col 1: Actual Scaled BlockReference Symbol Thumbnail
                        double symCx = colX[1] + colW[1] / 2.0;
                        double availSymW = colW[1] * 0.78;
                        double availSymH = rowH * 0.76;
                        bool symInserted = false;
                        if (!entry.BlockRecordId.IsNull)
                        {
                            try
                            {
                                BlockReference thumbBr = new BlockReference(Point3d.Origin, entry.BlockRecordId);
                                thumbBr.Layer = safeLegendLayer;
                                if (entry.DominantColor != null)
                                    thumbBr.Color = RestoreColor(entry.DominantColor);
                                ms.AppendEntity(thumbBr);
                                tr.AddNewlyCreatedDBObject(thumbBr, true);
                                try { thumbBr.RecordGraphicsModified(true); } catch { }
                                Extents3d rawEx = thumbBr.GeometricExtents;
                                if (IsFiniteBounds(rawEx))
                                {
                                    double bw = rawEx.MaxPoint.X - rawEx.MinPoint.X;
                                    double bh = rawEx.MaxPoint.Y - rawEx.MinPoint.Y;
                                    double s = 1.0;
                                    if (bw > 1e-6 && bh > 1e-6)
                                        s = Math.Min(availSymW / bw, availSymH / bh);
                                    else if (bw > 1e-6)
                                        s = availSymW / bw;
                                    else if (bh > 1e-6)
                                        s = availSymH / bh;
                                    if (double.IsNaN(s) || double.IsInfinity(s) || s <= 1e-6) s = 1.0;
                                    thumbBr.ScaleFactors = new Scale3d(s, s, s);

                                    Extents3d scaledEx = thumbBr.GeometricExtents;
                                    if (IsFiniteBounds(scaledEx))
                                    {
                                        double scx = (scaledEx.MinPoint.X + scaledEx.MaxPoint.X) / 2.0;
                                        double scy = (scaledEx.MinPoint.Y + scaledEx.MaxPoint.Y) / 2.0;
                                        thumbBr.TransformBy(Matrix3d.Displacement(new Vector3d(symCx - scx, rCy - scy, 0.0)));
                                    }
                                    createdHandles.Add(thumbBr.Handle.ToString());
                                    symInserted = true;
                                }
                                else
                                {
                                    thumbBr.Erase(true);
                                }
                            }
                            catch { }
                        }
                        if (!symInserted)
                        {
                            MText dashMt = CreateLegendCellMText(
                                ms, tr, unicodeStyleId, "—",
                                symCx, rCy, availSymW, availSymH, rowH * 0.24, rowH,
                                AttachmentPoint.MiddleCenter, 8, safeLegendLayer);
                            createdHandles.Add(dashMt.Handle.ToString());
                        }

                        // Col 2: Block Name
                        MText nameMt = CreateLegendCellMText(
                            ms, tr, unicodeStyleId, entry.Name,
                            colX[2] + colW[2] / 2.0, rCy,
                            colW[2] * 0.90, rowH * 0.80, rowH * 0.24, rowH,
                            AttachmentPoint.MiddleCenter, 7, safeLegendLayer);
                        createdHandles.Add(nameMt.Handle.ToString());

                        // Col 3: Engineering Description
                        MText descMt = CreateLegendCellMText(
                            ms, tr, unicodeStyleId, entry.Description,
                            colX[3] + colW[3] / 2.0, rCy,
                            colW[3] * 0.90, rowH * 0.82, rowH * 0.23, rowH,
                            AttachmentPoint.MiddleCenter, 7, safeLegendLayer);
                        createdHandles.Add(descMt.Handle.ToString());

                        // Col 4: Quantity
                        MText qtyMt = CreateLegendCellMText(
                            ms, tr, unicodeStyleId, qty.ToString(CultureInfo.InvariantCulture),
                            colX[4] + colW[4] / 2.0, rCy,
                            colW[4] * 0.86, rowH * 0.78, rowH * 0.25, rowH,
                            AttachmentPoint.MiddleCenter, 3, safeLegendLayer);
                        createdHandles.Add(qtyMt.Handle.ToString());

                        Dictionary<string, object> rowSummary = new Dictionary<string, object>
                        {
                            { "index", r + 1 },
                            { "block_name", entry.Name },
                            { "description", entry.Description },
                            { "quantity", qty },
                            { "symbol_drawn", symInserted }
                        };
                        if (entry.DominantColor != null)
                            rowSummary["color_index"] = (int)entry.DominantColor.ColorIndex;
                        rowSummaries.Add(rowSummary);
                    }

                    // 7. Footer Total Row
                    double fCy = (yDataBot + yBottom) / 2.0;
                    double leftSpanW = colX[4] - x0;
                    string legendFooterLabel = !string.IsNullOrWhiteSpace(footerLabelReq)
                        ? footerLabelReq.Trim()
                        : (requestedHandles.Count > 0
                            ? "إجمالي البلوكات المحددة (Total Blocks)"
                            : "الإجمالي الكلي للبلوكات بالمخطط (Total Blocks)");
                    MText footLbl = CreateLegendCellMText(
                        ms, tr, unicodeStyleId, legendFooterLabel,
                        x0 + leftSpanW / 2.0, fCy,
                        leftSpanW * 0.92, footerH * 0.76, footerH * 0.28, rowH,
                        AttachmentPoint.MiddleCenter, 4, safeLegendLayer);
                    createdHandles.Add(footLbl.Handle.ToString());

                    MText footQty = CreateLegendCellMText(
                        ms, tr, unicodeStyleId, totalBlocksRepresented.ToString(CultureInfo.InvariantCulture),
                        colX[4] + colW[4] / 2.0, fCy,
                        colW[4] * 0.86, footerH * 0.76, footerH * 0.30, rowH,
                        AttachmentPoint.MiddleCenter, 3, safeLegendLayer);
                    createdHandles.Add(footQty.Handle.ToString());

                    db.UpdateExt(true);
                    tr.Commit();
                }

                CadUndoEntry legendUndo = new CadUndoEntry();
                legendUndo.ToolName = "draw_block_legend";
                legendUndo.Summary = "Block legend (" + rowSummaries.Count.ToString(CultureInfo.InvariantCulture) + " rows)";
                legendUndo.ErasedIds.AddRange(legendErasedIds);
                foreach (string ch in createdHandles)
                {
                    ObjectId cid = ResolveHandle(db, ch);
                    if (!cid.IsNull) legendUndo.CreatedIds.Add(cid);
                }
                PushUndoEntry(doc, legendUndo);

                if (zoom && legendExt.HasValue)
                {
                    if (zoomMode == "all" || zoomMode == "extents")
                    {
                        DrawingExecutionCommands.ZoomExtents(doc);
                    }
                    else if (zoomMode == "both" && drawingExt.HasValue)
                    {
                        Extents3d bothExt = drawingExt.Value;
                        bothExt.AddExtents(legendExt.Value);
                        ZoomToExtents3d(doc, bothExt, 1.12);
                    }
                    else
                    {
                        ZoomToExtents3d(doc, legendExt.Value, 1.18);
                    }
                }
                else
                {
                    doc.Editor.Regen();
                    doc.Editor.UpdateScreen();
                }
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "row_count", rowSummaries.Count },
                { "total_blocks_represented", totalBlocksRepresented },
                { "erased_count", erasedCount },
                { "created_count", createdHandles.Count },
                { "layer", safeLegendLayer },
                { "switched_to_model", switchedToModel },
                { "unicode_font", "Arial (_AI_UNICODE)" },
                { "rows", rowSummaries }
            };

            ExcelSheetData legendSheet = new ExcelSheetData
            {
                Title = title,
                SheetName = "Block_Legend",
                Headers = new List<string> { "م", "اسم البلوك", "الوصف الهندسي", "الكمية" },
                FooterRow = new List<string> { "", "الإجمالي الكلي للبلوكات (Total)", "", totalBlocksRepresented.ToString(CultureInfo.InvariantCulture) }
            };
            for (int r = 0; r < rowSummaries.Count; r++)
            {
                Dictionary<string, object> rs = rowSummaries[r] as Dictionary<string, object>;
                if (rs == null) continue;
                legendSheet.Rows.Add(new List<string>
                {
                    Convert.ToString(rs["index"], CultureInfo.InvariantCulture),
                    Convert.ToString(rs["block_name"], CultureInfo.InvariantCulture),
                    Convert.ToString(rs["description"], CultureInfo.InvariantCulture),
                    Convert.ToString(rs["quantity"], CultureInfo.InvariantCulture)
                });
            }
            lock (s_lastCoordLock)
            {
                s_lastLegendTableByDb[db.UnmanagedObject] = legendSheet;
                s_lastGeneratedTableKindByDb[db.UnmanagedObject] = "block_legend";
            }
            if (GetBool(args, "export_excel", false))
            {
                string xlPath = GetStr(args, "excel_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "block_legend", ".xlsx");
                string savedPath = ExcelTableInterop.WriteSpreadsheet(xlPath, legendSheet);
                res["excel_exported"] = true;
                res["excel_path"] = savedPath;
            }

            if (legendExt.HasValue)
            {
                res["legend_bounds"] = new double[]
                {
                    Round(legendExt.Value.MinPoint.X), Round(legendExt.Value.MinPoint.Y),
                    Round(legendExt.Value.MaxPoint.X), Round(legendExt.Value.MaxPoint.Y)
                };
            }
            if (drawingExt.HasValue)
            {
                res["drawing_bounds"] = new double[]
                {
                    Round(drawingExt.Value.MinPoint.X), Round(drawingExt.Value.MinPoint.Y),
                    Round(drawingExt.Value.MaxPoint.X), Round(drawingExt.Value.MaxPoint.Y)
                };
            }
            if (warnings.Count > 0) res["warnings"] = warnings;
            return Ok(res);
        }

        private static bool ContainsNonAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] > 127) return true;
            }
            return false;
        }

        private static string EscapeMTextPlain(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string r = s.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
            r = r.Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "");
            return r;
        }

        private static ObjectId EnsureUnicodeTextStyle(Database db, Transaction tr)
        {
            const string styleName = "_AI_UNICODE";
            TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            TextStyleTableRecord tstr;
            ObjectId styleId;
            if (tst.Has(styleName))
            {
                styleId = tst[styleName];
                tstr = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForWrite);
            }
            else
            {
                tst.UpgradeOpen();
                tstr = new TextStyleTableRecord();
                tstr.Name = styleName;
                styleId = tst.Add(tstr);
                tr.AddNewlyCreatedDBObject(tstr, true);
            }
            try
            {
                tstr.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor("Arial", false, false, 1, 34);
            }
            catch { }
            tstr.BigFontFileName = "";
            tstr.TextSize = 0.0;
            tstr.XScale = 1.0;
            return styleId;
        }

        private static MText CreateLegendCellMText(
            BlockTableRecord ms,
            Transaction tr,
            ObjectId unicodeStyleId,
            string rawText,
            double centerX,
            double centerY,
            double boxW,
            double boxH,
            double initialHeight,
            double rowH,
            AttachmentPoint attach,
            short colorIndex,
            string layerName)
        {
            MText mt = new MText();
            mt.SetDatabaseDefaults(ms.Database);
            mt.Layer = layerName;
            mt.ColorIndex = colorIndex;
            mt.TextStyleId = unicodeStyleId;
            mt.TextHeight = Math.Max(rowH * 0.08, initialHeight);
            mt.Width = Math.Max(rowH * 0.4, boxW);
            mt.Attachment = attach;
            mt.Location = new Point3d(centerX, centerY, 0.0);
            mt.Contents = EscapeMTextPlain(rawText ?? "");
            ms.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);

            for (int attempt = 0; attempt < 6; attempt++)
            {
                double aw = 0.0, ah = 0.0;
                try { aw = mt.ActualWidth; ah = mt.ActualHeight; }
                catch { break; }
                if ((aw <= boxW * 1.02 || aw <= 1e-6) && (ah <= boxH * 1.02 || ah <= 1e-6))
                    break;
                double scaleDown = 0.82;
                if (ah > boxH && ah > 1e-6)
                    scaleDown = Math.Min(scaleDown, Math.Sqrt((boxH * 0.92) / ah));
                if (aw > boxW && aw > 1e-6)
                    scaleDown = Math.Min(scaleDown, (boxW * 0.92) / aw);
                scaleDown = Math.Max(0.45, Math.Min(0.90, scaleDown));
                double nextH = Math.Max(rowH * 0.07, mt.TextHeight * scaleDown);
                if (Math.Abs(nextH - mt.TextHeight) < 1e-6) break;
                mt.TextHeight = nextH;
            }
            return mt;
        }

        private static void ConvertBlockRecordEntitiesToByBlock(
            Transaction tr,
            ObjectId btrId,
            Dictionary<ObjectId, ModifiedEntitySnapshot> snapById,
            HashSet<ObjectId> visitedBtrs,
            int depth)
        {
            if (depth > 4 || btrId.IsNull || btrId.IsErased || !visitedBtrs.Add(btrId))
                return;
            BlockTableRecord btr = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null || btr.IsLayout) return;

            Color byBlockClr = Color.FromColorIndex(ColorMethod.ByBlock, 0);
            foreach (ObjectId subId in btr)
            {
                if (subId.IsNull || subId.IsErased) continue;
                try
                {
                    Entity subEnt = tr.GetObject(subId, OpenMode.ForWrite, false, true) as Entity;
                    if (subEnt == null) continue;
                    CaptureEntitySnapshot(subEnt, snapById);
                    subEnt.Color = byBlockClr;
                    try { subEnt.RecordGraphicsModified(true); } catch { }
                    BlockReference nestedBr = subEnt as BlockReference;
                    if (nestedBr != null)
                    {
                        foreach (ObjectId attId in nestedBr.AttributeCollection)
                        {
                            try
                            {
                                AttributeReference att = tr.GetObject(attId, OpenMode.ForWrite, false, true) as AttributeReference;
                                if (att != null)
                                {
                                    CaptureEntitySnapshot(att, snapById);
                                    att.Color = byBlockClr;
                                    try { att.RecordGraphicsModified(true); } catch { }
                                }
                            }
                            catch { }
                        }
                        if (!nestedBr.BlockTableRecord.IsNull)
                            ConvertBlockRecordEntitiesToByBlock(tr, nestedBr.BlockTableRecord, snapById, visitedBtrs, depth + 1);
                        if (nestedBr.IsDynamicBlock && !nestedBr.DynamicBlockTableRecord.IsNull)
                            ConvertBlockRecordEntitiesToByBlock(tr, nestedBr.DynamicBlockTableRecord, snapById, visitedBtrs, depth + 1);
                    }
                }
                catch { }
            }
        }

        private static string CleanBlockName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string cleaned = raw.Trim();
            if (cleaned.StartsWith("*", StringComparison.Ordinal)) return null;
            char[] invalid = new char[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`', '\r', '\n', '\t' };
            foreach (char c in invalid) cleaned = cleaned.Replace(c, '_');
            cleaned = cleaned.Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) return null;
            if (cleaned.Length > 200) cleaned = cleaned.Substring(0, 200);
            return cleaned;
        }

        private static bool HasNonRenameEntityEdits(Dictionary<string, object> w)
        {
            if (w == null) return false;
            string[] editKeys = new string[]
            {
                "move", "move_dx", "move_dy", "rotate", "rotation_deg", "scale", "mirror",
                "layer", "color_index", "radius", "diameter", "center", "start", "end",
                "text", "height", "closed"
            };
            foreach (string k in editKeys)
            {
                if (w.ContainsKey(k) && w[k] != null) return true;
            }
            return false;
        }

        private static Dictionary<string, object> ModifyEntities(Document doc, Dictionary<string, object> args)
        {
            List<object> itemList = AsList(GetObj(args, "items"));
            if (itemList.Count == 0 && !(GetObj(args, "edits") is Dictionary<string, object>))
            {
                itemList = AsList(GetObj(args, "edits"));
            }
            List<string> bulkHandles = GetStringList(args, "handles");
            bool selectedOnly = GetBool(args, "selected_only", false);
            Dictionary<string, object> filterObj = AsDict(GetObj(args, "filter"));
            string layerFilter = GetStr(args, "layer_filter") ?? GetStr(args, "filter_layer") ?? (filterObj != null ? (GetStr(filterObj, "layer") ?? GetStr(filterObj, "layer_filter")) : null);
            string blockFilter = GetStr(args, "block_name") ?? (filterObj != null ? GetStr(filterObj, "block_name") : null);
            string typeFilter = GetStr(args, "entity_type") ?? (filterObj != null ? GetStr(filterObj, "entity_type") : null);
            string textFilter = GetStr(args, "text_contains") ?? (filterObj != null ? GetStr(filterObj, "text_contains") : null);
            double[] bbox = GetDoubleArray(args, "bbox") ?? (filterObj != null ? GetDoubleArray(filterObj, "bbox") : null);
            bool excludeAnonymous = GetBool(args, "exclude_anonymous", filterObj != null && GetBool(filterObj, "exclude_anonymous", false));
            bool excludeSystem = GetBool(args, "exclude_system", filterObj != null && GetBool(filterObj, "exclude_system", false));
            bool visibleOnly = GetBool(args, "visible_only", filterObj != null && GetBool(filterObj, "visible_only", false));
            bool confirmBulkLayer = GetBool(args, "confirm_bulk_layer", false);
            bool? includeBlockContentsArg = GetNullableBool(args, "include_block_contents");
            Dictionary<string, object> bulkEdits = AsDict(GetObj(args, "edits"));
            string bulkNewBlockName = GetStr(args, "new_block_name") ?? GetStr(args, "rename_block") ?? GetStr(args, "rename_to");
            if (string.IsNullOrWhiteSpace(bulkNewBlockName) && bulkEdits != null)
                bulkNewBlockName = GetStr(bulkEdits, "new_block_name") ?? GetStr(bulkEdits, "rename_block") ?? GetStr(bulkEdits, "rename_to");

            List<Dictionary<string, object>> workItems = new List<Dictionary<string, object>>();
            if (itemList.Count > 0)
            {
                foreach (object o in itemList)
                {
                    Dictionary<string, object> d = AsDict(o);
                    if (d != null) workItems.Add(d);
                }
            }
            else
            {
                if (bulkEdits == null) bulkEdits = args;
                if (selectedOnly)
                {
                    foreach (string sh in GetSelectedHandleStrings(doc))
                    {
                        Dictionary<string, object> entry = new Dictionary<string, object>(bulkEdits);
                        entry["handle"] = sh;
                        workItems.Add(entry);
                    }
                }
                else if (bulkHandles.Count > 0)
                {
                    foreach (string h in bulkHandles)
                    {
                        Dictionary<string, object> entry = new Dictionary<string, object>(bulkEdits);
                        entry["handle"] = h;
                        workItems.Add(entry);
                    }
                }
                else
                {
                    string singleHandle = GetStr(bulkEdits, "handle");
                    if (string.IsNullOrWhiteSpace(singleHandle)) singleHandle = GetStr(args, "handle");
                    if (!string.IsNullOrWhiteSpace(singleHandle))
                    {
                        Dictionary<string, object> entry = new Dictionary<string, object>(bulkEdits);
                        entry["handle"] = singleHandle.Trim();
                        workItems.Add(entry);
                    }
                }
            }

            bool hasFilterCriteria = !string.IsNullOrEmpty(layerFilter) || !string.IsNullOrEmpty(blockFilter) ||
                                     !string.IsNullOrEmpty(typeFilter) || !string.IsNullOrEmpty(textFilter) ||
                                     (bbox != null && bbox.Length >= 4);

            if (workItems.Count == 0 && !hasFilterCriteria)
                return Err("Specify 'items', 'handles', 'selected_only': true, or filter criteria ('block_name', 'entity_type', 'layer_filter', 'bbox') with edits.");

            Database db = doc.Database;
            List<string> modifiedHandles = new List<string>();
            List<object> modifiedDetails = new List<object>();
            List<string> errors = new List<string>();
            bool zoom = GetBool(args, "zoom", false);
            Dictionary<ObjectId, ModifiedEntitySnapshot> snapById = new Dictionary<ObjectId, ModifiedEntitySnapshot>();
            List<BlockRecordRenameSnapshot> blockRecordRenames = new List<BlockRecordRenameSnapshot>();
            List<ObjectId> createdBlockRecordIds = new List<ObjectId>();
            List<object> renamedBlocksResult = new List<object>();
            Dictionary<string, string> renamedOldToNew = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Extents3d? modifiedExt = null;
            Extents3d? nonLegendExt = null;
            int innerBlockEntitiesUpdated = 0;
            int legendSyncedCount = 0;
            bool subsetClonedOccurred = false;
            bool anyBlockColorEdit = false;
            bool anyBlockRenameEdit = false;
            bool anyOtherEditsRequested = false;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                if (workItems.Count == 0 && hasFilterCriteria)
                {
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                    HashSet<string> emptySel = new HashSet<string>();
                    Dictionary<string, object> eSpec = bulkEdits ?? args;
                    foreach (ObjectId id in ms)
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        if (string.IsNullOrEmpty(layerFilter) && IsLegendOrHighlightLayer(ent.Layer)) continue;
                        bool entVisible = IsEntityVisible(ent, layerHiddenFlags);
                        if (EntityMatchesFilters(tr, ent, emptySel, false, emptySel, layerFilter, typeFilter,
                            blockFilter, textFilter, bbox, excludeAnonymous, excludeSystem, visibleOnly, entVisible))
                        {
                            Dictionary<string, object> entry = new Dictionary<string, object>(eSpec);
                            entry["handle"] = ent.Handle.ToString();
                            workItems.Add(entry);
                        }
                    }

                    bool isBroadLayerOnly = !string.IsNullOrEmpty(layerFilter) &&
                                            string.IsNullOrEmpty(blockFilter) &&
                                            string.IsNullOrEmpty(typeFilter) &&
                                            string.IsNullOrEmpty(textFilter) &&
                                            (bbox == null || bbox.Length < 4);
                    if (isBroadLayerOnly && workItems.Count > BulkLayerSafetyThreshold && !confirmBulkLayer)
                    {
                        tr.Commit();
                        return Err("Safety block: 'layer_filter'=\"" + layerFilter + "\" matched " +
                            workItems.Count.ToString(CultureInfo.InvariantCulture) +
                            " entities! Blocks (like PLC/AI modules) often live on Layer '0' alongside thousands of unrelated lines. To modify ONLY a specific block, pass 'block_name' (e.g. block_name='parir ai 4') or exact 'handles' instead. Only pass confirm_bulk_layer=true if you genuinely want to alter all " +
                            workItems.Count.ToString(CultureInfo.InvariantCulture) + " entities on that entire layer.");
                    }

                    if (workItems.Count == 0)
                    {
                        tr.Commit();
                        return Err("No matching entities found in ModelSpace for the specified filter.");
                    }
                }

                HashSet<ObjectId> workItemIdSet = new HashSet<ObjectId>();
                foreach (Dictionary<string, object> w in workItems)
                {
                    if (GetNullableInt(w, "color_index").HasValue) anyBlockColorEdit = true;
                    string reqRn = GetStr(w, "new_block_name") ?? GetStr(w, "rename_block") ?? GetStr(w, "rename_to") ?? bulkNewBlockName;
                    if (!string.IsNullOrWhiteSpace(reqRn)) anyBlockRenameEdit = true;
                    if (HasNonRenameEntityEdits(w)) anyOtherEditsRequested = true;
                    ObjectId wid = ResolveHandle(db, GetStr(w, "handle"));
                    if (!wid.IsNull && !wid.IsErased) workItemIdSet.Add(wid);
                }

                Dictionary<ObjectId, int> nonLegendTotalByDefId = new Dictionary<ObjectId, int>();
                if (anyBlockColorEdit || anyBlockRenameEdit)
                {
                    foreach (ObjectId mid in ms)
                    {
                        if (mid.IsErased) continue;
                        Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                        BlockReference mbr = me as BlockReference;
                        if (mbr == null || IsLegendOrHighlightLayer(me.Layer)) continue;
                        ObjectId defId = GetBlockDefinitionId(mbr);
                        if (!defId.IsNull)
                            nonLegendTotalByDefId[defId] = nonLegendTotalByDefId.ContainsKey(defId) ? nonLegendTotalByDefId[defId] + 1 : 1;
                        if (!mbr.BlockTableRecord.IsNull && mbr.BlockTableRecord != defId)
                            nonLegendTotalByDefId[mbr.BlockTableRecord] = nonLegendTotalByDefId.ContainsKey(mbr.BlockTableRecord) ? nonLegendTotalByDefId[mbr.BlockTableRecord] + 1 : 1;
                    }
                }

                // 1. Real Block Renaming / Subset Cloning FIRST (so subsequent ByBlock color conversion targets new/cloned BTR cleanly)
                if (anyBlockRenameEdit)
                {
                    Dictionary<ObjectId, Dictionary<string, List<ObjectId>>> renameGroupsByDef = new Dictionary<ObjectId, Dictionary<string, List<ObjectId>>>();
                    HashSet<ObjectId> seenRenameWids = new HashSet<ObjectId>();

                    foreach (Dictionary<string, object> w in workItems)
                    {
                        string reqRn = GetStr(w, "new_block_name") ?? GetStr(w, "rename_block") ?? GetStr(w, "rename_to") ?? bulkNewBlockName;
                        if (string.IsNullOrWhiteSpace(reqRn)) continue;
                        string cleanNew = CleanBlockName(reqRn);
                        if (string.IsNullOrEmpty(cleanNew))
                        {
                            errors.Add("Invalid block name '" + reqRn + "'.");
                            continue;
                        }
                        string wh = GetStr(w, "handle");
                        ObjectId wid = ResolveHandle(db, wh);
                        if (wid.IsNull || wid.IsErased || !seenRenameWids.Add(wid)) continue;
                        Entity we = tr.GetObject(wid, OpenMode.ForRead) as Entity;
                        if (we == null || IsLegendOrHighlightLayer(we.Layer)) continue;
                        BlockReference wbr = we as BlockReference;
                        if (wbr == null)
                        {
                            errors.Add("Handle " + wh + " is not a BlockReference; cannot rename block.");
                            continue;
                        }
                        ObjectId defId = GetBlockDefinitionId(wbr);
                        if (defId.IsNull) continue;

                        Dictionary<string, List<ObjectId>> byTargetName;
                        if (!renameGroupsByDef.TryGetValue(defId, out byTargetName))
                        {
                            byTargetName = new Dictionary<string, List<ObjectId>>(StringComparer.OrdinalIgnoreCase);
                            renameGroupsByDef[defId] = byTargetName;
                        }
                        List<ObjectId> bucket;
                        if (!byTargetName.TryGetValue(cleanNew, out bucket))
                        {
                            bucket = new List<ObjectId>();
                            byTargetName[cleanNew] = bucket;
                        }
                        bucket.Add(wid);
                    }

                    foreach (KeyValuePair<ObjectId, Dictionary<string, List<ObjectId>>> defKv in renameGroupsByDef)
                    {
                        ObjectId defId = defKv.Key;
                        BlockTableRecord srcDefBtr = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                        if (srcDefBtr == null || srcDefBtr.IsLayout) continue;
                        string oldDefName = srcDefBtr.Name ?? "";
                        int nonLegendTotal = nonLegendTotalByDefId.ContainsKey(defId) ? nonLegendTotalByDefId[defId] : 0;

                        if (defKv.Value.Count == 1)
                        {
                            foreach (KeyValuePair<string, List<ObjectId>> grp in defKv.Value)
                            {
                                string targetName = grp.Key;
                                List<ObjectId> targetWids = grp.Value;
                                if (targetWids.Count >= nonLegendTotal && nonLegendTotal > 0)
                                {
                                    if (string.Equals(oldDefName, targetName, StringComparison.Ordinal))
                                    {
                                        renamedBlocksResult.Add(new Dictionary<string, object>
                                        {
                                            { "old_name", oldDefName },
                                            { "new_name", targetName },
                                            { "mode", "already_named" },
                                            { "affected_count", targetWids.Count }
                                        });
                                    }
                                    else if (bt.Has(targetName) && bt[targetName] != defId)
                                    {
                                        errors.Add("Cannot rename block '" + oldDefName + "' to '" + targetName + "': target block name already exists in drawing.");
                                    }
                                    else
                                    {
                                        srcDefBtr.UpgradeOpen();
                                        srcDefBtr.Name = targetName;
                                        BlockRecordRenameSnapshot rsnap = new BlockRecordRenameSnapshot();
                                        rsnap.BlockRecordId = defId;
                                        rsnap.OldName = oldDefName;
                                        rsnap.NewName = targetName;
                                        blockRecordRenames.Add(rsnap);
                                        renamedOldToNew[oldDefName] = targetName;
                                        foreach (ObjectId tid in targetWids)
                                        {
                                            BlockReference rbr = tr.GetObject(tid, OpenMode.ForWrite, false, true) as BlockReference;
                                            if (rbr != null) { try { rbr.RecordGraphicsModified(true); } catch { } }
                                        }
                                        renamedBlocksResult.Add(new Dictionary<string, object>
                                        {
                                            { "old_name", oldDefName },
                                            { "new_name", targetName },
                                            { "mode", "definition_rename" },
                                            { "affected_count", targetWids.Count }
                                        });
                                    }
                                    continue;
                                }
                            }
                            if (defKv.Value.Count == 1)
                            {
                                bool handledFull = false;
                                foreach (KeyValuePair<string, List<ObjectId>> grp in defKv.Value)
                                {
                                    if (grp.Value.Count >= nonLegendTotal && nonLegendTotal > 0) handledFull = true;
                                }
                                if (handledFull) continue;
                            }
                        }

                        // Subset clone rename (either partial selection of insertions or multiple target names)
                        foreach (KeyValuePair<string, List<ObjectId>> grp in defKv.Value)
                        {
                            string targetName = grp.Key;
                            List<ObjectId> targetWids = grp.Value;
                            if (targetWids.Count == 0) continue;
                            if (string.Equals(oldDefName, targetName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (bt.Has(targetName))
                            {
                                errors.Add("Cannot rename selected insertions of '" + oldDefName + "' to '" + targetName + "': block name already exists.");
                                continue;
                            }

                            BlockReference firstBr = tr.GetObject(targetWids[0], OpenMode.ForRead) as BlockReference;
                            ObjectId geomSrcId = (firstBr != null && !firstBr.BlockTableRecord.IsNull) ? firstBr.BlockTableRecord : defId;
                            BlockTableRecord geomSrcBtr = tr.GetObject(geomSrcId, OpenMode.ForRead) as BlockTableRecord;
                            if (geomSrcBtr == null) continue;

                            bt.UpgradeOpen();
                            BlockTableRecord newBtr = new BlockTableRecord();
                            newBtr.Name = targetName;
                            try { newBtr.Origin = geomSrcBtr.Origin; } catch { }
                            try { newBtr.Units = geomSrcBtr.Units; } catch { }
                            ObjectId newBtrId = bt.Add(newBtr);
                            tr.AddNewlyCreatedDBObject(newBtr, true);
                            createdBlockRecordIds.Add(newBtrId);

                            ObjectIdCollection cloneSrcIds = new ObjectIdCollection();
                            foreach (ObjectId cid in geomSrcBtr) cloneSrcIds.Add(cid);
                            if (cloneSrcIds.Count > 0)
                            {
                                IdMapping cloneMap = new IdMapping();
                                db.DeepCloneObjects(cloneSrcIds, newBtrId, cloneMap, false);
                            }

                            int moved = 0;
                            foreach (ObjectId tid in targetWids)
                            {
                                BlockReference targetBr = tr.GetObject(tid, OpenMode.ForWrite, false, true) as BlockReference;
                                if (targetBr == null) continue;
                                ModifiedEntitySnapshot snap = CaptureEntitySnapshot(targetBr, snapById);
                                if (!snap.HadBlockRecordChange)
                                {
                                    snap.HadBlockRecordChange = true;
                                    snap.OldBlockRecordId = targetBr.BlockTableRecord;
                                }
                                targetBr.BlockTableRecord = newBtrId;
                                try { targetBr.RecordGraphicsModified(true); } catch { }
                                moved++;
                            }
                            if (moved > 0)
                            {
                                subsetClonedOccurred = true;
                                if (nonLegendTotalByDefId.ContainsKey(defId))
                                    nonLegendTotalByDefId[defId] = Math.Max(0, nonLegendTotalByDefId[defId] - moved);
                                nonLegendTotalByDefId[newBtrId] = moved;
                                renamedBlocksResult.Add(new Dictionary<string, object>
                                {
                                    { "old_name", oldDefName },
                                    { "new_name", targetName },
                                    { "mode", "subset_clone_rename" },
                                    { "affected_count", moved }
                                });
                            }
                        }
                    }

                    if (!anyOtherEditsRequested && renamedBlocksResult.Count == 0 && errors.Count > 0)
                    {
                        tr.Commit();
                        return Err(string.Join(" | ", errors.ToArray()));
                    }
                }

                // 2. Detect BlockReferences having color edits so block inner geometry reflects requested color cleanly
                if (anyBlockColorEdit && includeBlockContentsArg != false)
                {
                    Dictionary<ObjectId, int> targetedBtrCounts = new Dictionary<ObjectId, int>();
                    foreach (Dictionary<string, object> w in workItems)
                    {
                        if (!GetNullableInt(w, "color_index").HasValue) continue;
                        ObjectId wid = ResolveHandle(db, GetStr(w, "handle"));
                        if (wid.IsNull || wid.IsErased) continue;
                        Entity we = tr.GetObject(wid, OpenMode.ForRead) as Entity;
                        if (we == null || IsLegendOrHighlightLayer(we.Layer)) continue;
                        BlockReference wbr = we as BlockReference;
                        if (wbr == null) continue;

                        ObjectId recId = wbr.BlockTableRecord;
                        if (!recId.IsNull)
                            targetedBtrCounts[recId] = targetedBtrCounts.ContainsKey(recId) ? targetedBtrCounts[recId] + 1 : 1;
                        if (wbr.IsDynamicBlock && !wbr.DynamicBlockTableRecord.IsNull && wbr.DynamicBlockTableRecord != recId)
                        {
                            ObjectId dynId = wbr.DynamicBlockTableRecord;
                            targetedBtrCounts[dynId] = targetedBtrCounts.ContainsKey(dynId) ? targetedBtrCounts[dynId] + 1 : 1;
                        }
                    }

                    if (targetedBtrCounts.Count > 0)
                    {
                        HashSet<ObjectId> visitedBtrs = new HashSet<ObjectId>();
                        int beforeSnapCount = snapById.Count;
                        foreach (KeyValuePair<ObjectId, int> kv in targetedBtrCounts)
                        {
                            int totalCount = nonLegendTotalByDefId.ContainsKey(kv.Key) ? nonLegendTotalByDefId[kv.Key] : kv.Value;
                            if (includeBlockContentsArg == true || kv.Value >= totalCount)
                            {
                                ConvertBlockRecordEntitiesToByBlock(tr, kv.Key, snapById, visitedBtrs, 0);
                            }
                        }
                        innerBlockEntitiesUpdated = snapById.Count - beforeSnapCount;
                    }
                }

                Dictionary<ObjectId, Dictionary<short, int>> appliedColorCountsByDefId = new Dictionary<ObjectId, Dictionary<short, int>>();

                foreach (Dictionary<string, object> w in workItems)
                {
                    string h = GetStr(w, "handle");
                    ObjectId id = ResolveHandle(db, h);
                    if (id.IsNull || id.IsErased)
                    {
                        errors.Add("Handle not found or erased: " + h);
                        continue;
                    }
                    try
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForWrite, false, true) as Entity;
                        if (ent == null) { errors.Add("Object is not an entity: " + h); continue; }
                        ModifiedEntitySnapshot snap = CaptureEntitySnapshot(ent, snapById);
                        ApplyEntityEdits(db, tr, ent, w, snap);

                        int? newClr = GetNullableInt(w, "color_index");
                        BlockReference targetBr = ent as BlockReference;
                        if (targetBr != null && newClr.HasValue)
                        {
                            short cc = ClampColor(newClr.Value);
                            foreach (ObjectId attId in targetBr.AttributeCollection)
                            {
                                try
                                {
                                    AttributeReference att = tr.GetObject(attId, OpenMode.ForWrite, false, true) as AttributeReference;
                                    if (att != null)
                                    {
                                        CaptureEntitySnapshot(att, snapById);
                                        att.ColorIndex = cc;
                                        try { att.RecordGraphicsModified(true); } catch { }
                                    }
                                }
                                catch { }
                            }
                            if (!IsLegendOrHighlightLayer(ent.Layer))
                            {
                                ObjectId[] defKeys = (targetBr.IsDynamicBlock && !targetBr.DynamicBlockTableRecord.IsNull && targetBr.DynamicBlockTableRecord != targetBr.BlockTableRecord)
                                    ? new ObjectId[] { targetBr.BlockTableRecord, targetBr.DynamicBlockTableRecord }
                                    : new ObjectId[] { targetBr.BlockTableRecord };
                                foreach (ObjectId dk in defKeys)
                                {
                                    if (dk.IsNull) continue;
                                    Dictionary<short, int> cMap;
                                    if (!appliedColorCountsByDefId.TryGetValue(dk, out cMap))
                                    {
                                        cMap = new Dictionary<short, int>();
                                        appliedColorCountsByDefId[dk] = cMap;
                                    }
                                    cMap[cc] = cMap.ContainsKey(cc) ? cMap[cc] + 1 : 1;
                                }
                            }
                        }

                        try { ent.RecordGraphicsModified(true); } catch { }

                        string mh = ent.Handle.ToString();
                        modifiedHandles.Add(mh);
                        try
                        {
                            Extents3d mex = ent.GeometricExtents;
                            if (IsFiniteBounds(mex))
                            {
                                if (!modifiedExt.HasValue) modifiedExt = mex;
                                else { Extents3d cur = modifiedExt.Value; cur.AddExtents(mex); modifiedExt = cur; }
                                if (!IsLegendOrHighlightLayer(ent.Layer))
                                {
                                    if (!nonLegendExt.HasValue) nonLegendExt = mex;
                                    else { Extents3d ncur = nonLegendExt.Value; ncur.AddExtents(mex); nonLegendExt = ncur; }
                                }
                            }
                            if (modifiedDetails.Count < 40)
                            {
                                modifiedDetails.Add(new Dictionary<string, object>
                                {
                                    { "handle", mh },
                                    { "type", ent.GetType().Name },
                                    { "layer", ent.Layer ?? "0" },
                                    { "bounds", new double[] { Round(mex.MinPoint.X), Round(mex.MinPoint.Y), Round(mex.MaxPoint.X), Round(mex.MaxPoint.Y) } }
                                });
                            }
                        }
                        catch
                        {
                            if (modifiedDetails.Count < 40)
                            {
                                modifiedDetails.Add(new Dictionary<string, object>
                                {
                                    { "handle", mh },
                                    { "type", ent.GetType().Name },
                                    { "layer", ent.Layer ?? "0" }
                                });
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        errors.Add("Handle " + h + ": " + ex.Message);
                    }
                }

                // 3. Sync existing _AI_BLOCK_LEGEND thumbnails and renamed block name MText labels cleanly
                Dictionary<ObjectId, Color> fullColoredTargetsByDefId = new Dictionary<ObjectId, Color>();
                foreach (KeyValuePair<ObjectId, Dictionary<short, int>> kv in appliedColorCountsByDefId)
                {
                    if (kv.Value.Count != 1) continue;
                    int expectedTotal = nonLegendTotalByDefId.ContainsKey(kv.Key) ? nonLegendTotalByDefId[kv.Key] : 0;
                    foreach (KeyValuePair<short, int> ck in kv.Value)
                    {
                        if (expectedTotal > 0 && ck.Value >= expectedTotal)
                        {
                            fullColoredTargetsByDefId[kv.Key] = ck.Key >= 256
                                ? Color.FromColorIndex(ColorMethod.ByLayer, 256)
                                : Color.FromColorIndex(ColorMethod.ByAci, ck.Key);
                        }
                    }
                }

                if (fullColoredTargetsByDefId.Count > 0 || renamedOldToNew.Count > 0)
                {
                    foreach (ObjectId mid in ms)
                    {
                        if (mid.IsErased || workItemIdSet.Contains(mid)) continue;
                        Entity legEnt = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                        if (legEnt == null || !string.Equals(legEnt.Layer, "_AI_BLOCK_LEGEND", StringComparison.OrdinalIgnoreCase)) continue;

                        BlockReference thumbBr = legEnt as BlockReference;
                        if (thumbBr != null && fullColoredTargetsByDefId.Count > 0)
                        {
                            ObjectId dId = GetBlockDefinitionId(thumbBr);
                            Color targetClr;
                            if (fullColoredTargetsByDefId.TryGetValue(thumbBr.BlockTableRecord, out targetClr) ||
                                (!dId.IsNull && fullColoredTargetsByDefId.TryGetValue(dId, out targetClr)))
                            {
                                legEnt.UpgradeOpen();
                                CaptureEntitySnapshot(legEnt, snapById);
                                thumbBr.Color = targetClr;
                                try { thumbBr.RecordGraphicsModified(true); } catch { }
                                legendSyncedCount++;
                            }
                            continue;
                        }

                        MText legMt = legEnt as MText;
                        if (legMt != null && renamedOldToNew.Count > 0)
                        {
                            string plain = (legMt.Text ?? "").Trim();
                            string newNm;
                            if (!string.IsNullOrEmpty(plain) && renamedOldToNew.TryGetValue(plain, out newNm))
                            {
                                legEnt.UpgradeOpen();
                                CaptureEntitySnapshot(legEnt, snapById);
                                legMt.Contents = EscapeMTextPlain(newNm);
                                try { legMt.RecordGraphicsModified(true); } catch { }
                                legendSyncedCount++;
                            }
                        }
                    }
                }

                db.UpdateExt(true);
                tr.Commit();
            }

            if (snapById.Count > 0 || blockRecordRenames.Count > 0 || createdBlockRecordIds.Count > 0)
            {
                CadUndoEntry undoEntry = new CadUndoEntry();
                undoEntry.ToolName = "modify_entities";
                undoEntry.Summary = renamedBlocksResult.Count > 0
                    ? ("Modified " + modifiedHandles.Count.ToString(CultureInfo.InvariantCulture) + " entity(s) & renamed " + renamedBlocksResult.Count.ToString(CultureInfo.InvariantCulture) + " block(s)")
                    : ("Modified " + modifiedHandles.Count.ToString(CultureInfo.InvariantCulture) +
                        (string.IsNullOrEmpty(blockFilter) ? " entity(s)" : (" entity(s) of block '" + blockFilter + "'")));
                undoEntry.ModifiedSnapshots.AddRange(snapById.Values);
                undoEntry.BlockRecordRenames.AddRange(blockRecordRenames);
                undoEntry.CreatedBlockRecordIds.AddRange(createdBlockRecordIds);
                PushUndoEntry(doc, undoEntry);
            }

            Extents3d? focusExt = nonLegendExt.HasValue ? nonLegendExt : modifiedExt;
            if (zoom)
            {
                if (focusExt.HasValue && (!string.IsNullOrEmpty(blockFilter) || renamedBlocksResult.Count > 0 || modifiedHandles.Count > 0))
                    ZoomToExtents3d(doc, focusExt.Value, 1.25);
                else
                    DrawingExecutionCommands.ZoomExtents(doc);
            }
            else
            {
                doc.Editor.Regen();
                doc.Editor.UpdateScreen();
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "modified_count", modifiedHandles.Count },
                { "modified_handles", modifiedHandles.Count > MaxReturnedHandles ? modifiedHandles.GetRange(0, MaxReturnedHandles) : modifiedHandles },
                { "modified_entities", modifiedDetails },
                { "block_inner_entities_updated", innerBlockEntitiesUpdated },
                { "legend_entities_synced", legendSyncedCount },
                { "undo_history_steps", GetUndoHistoryCount(doc) }
            };
            if (renamedBlocksResult.Count > 0) res["renamed_blocks"] = renamedBlocksResult;
            if (subsetClonedOccurred) res["subset_block_cloned"] = true;
            if (focusExt.HasValue)
            {
                res["modified_bounds"] = new double[]
                {
                    Round(focusExt.Value.MinPoint.X), Round(focusExt.Value.MinPoint.Y),
                    Round(focusExt.Value.MaxPoint.X), Round(focusExt.Value.MaxPoint.Y)
                };
            }
            if (errors.Count > 0) res["errors"] = errors;
            return Ok(res);
        }

        private static Dictionary<string, object> UndoLastAction(Document doc, Dictionary<string, object> args)
        {
            string action = (GetStr(args, "action") ?? "undo").Trim().ToLowerInvariant();
            Database undoDb = doc != null ? doc.Database : null;

            List<CadUndoEntry> snapshotList = new List<CadUndoEntry>();
            lock (_undoLock)
            {
                List<CadUndoEntry> stack;
                if (undoDb != null && _undoStacks.TryGetValue(undoDb, out stack))
                    snapshotList.AddRange(stack);
            }

            if (action == "list_history" || action == "history" || action == "list")
            {
                List<object> items = new List<object>();
                for (int i = snapshotList.Count - 1; i >= 0; i--)
                {
                    CadUndoEntry e = snapshotList[i];
                    items.Add(new Dictionary<string, object>
                    {
                        { "action_id", e.ActionId },
                        { "tool_name", e.ToolName },
                        { "summary", e.Summary },
                        { "created_count", e.CreatedIds.Count },
                        { "erased_count", e.ErasedIds.Count },
                        { "modified_count", e.ModifiedSnapshots.Count },
                        { "renamed_blocks_count", e.BlockRecordRenames.Count },
                        { "created_blocks_count", e.CreatedBlockRecordIds.Count }
                    });
                }
                return Ok(new Dictionary<string, object>
                {
                    { "history_count", items.Count },
                    { "history", items }
                });
            }

            if (snapshotList.Count == 0)
            {
                return Err("No AI CAD changes recorded in this session's undo history to revert. (Changes from an earlier AutoCAD session cannot be undone via undo_last_action; use AutoCAD UNDO / Ctrl+Z or reopen without saving.)");
            }

            int steps = Math.Max(1, Math.Min(10, GetInt(args, "steps", 1)));
            int countToUndo = Math.Min(steps, snapshotList.Count);
            List<CadUndoEntry> toUndo = new List<CadUndoEntry>();
            for (int i = 0; i < countToUndo; i++)
                toUndo.Add(snapshotList[snapshotList.Count - 1 - i]);

            Database db = doc.Database;
            int erasedCreated = 0;
            int unerasedDeleted = 0;
            int restoredModified = 0;
            int revertedBlockRenames = 0;
            int erasedCreatedBlocks = 0;
            List<object> undoneSummaries = new List<object>();

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (CadUndoEntry entry in toUndo)
                {
                    int cErased = 0, cUnerased = 0, cRestored = 0, cRenamed = 0, cErasedBlocks = 0;

                    for (int i = entry.CreatedIds.Count - 1; i >= 0; i--)
                    {
                        ObjectId cid = entry.CreatedIds[i];
                        if (cid.IsNull || cid.IsErased) continue;
                        try
                        {
                            DBObject obj = tr.GetObject(cid, OpenMode.ForWrite, false, true);
                            if (obj != null && !obj.IsErased)
                            {
                                obj.Erase(true);
                                erasedCreated++;
                                cErased++;
                            }
                        }
                        catch { }
                    }

                    for (int i = 0; i < entry.ErasedIds.Count; i++)
                    {
                        ObjectId eid = entry.ErasedIds[i];
                        if (eid.IsNull || !eid.IsErased) continue;
                        try
                        {
                            DBObject obj = tr.GetObject(eid, OpenMode.ForWrite, true, true);
                            if (obj != null && obj.IsErased)
                            {
                                obj.Erase(false);
                                unerasedDeleted++;
                                cUnerased++;
                            }
                        }
                        catch { }
                    }

                    for (int i = entry.ModifiedSnapshots.Count - 1; i >= 0; i--)
                    {
                        ModifiedEntitySnapshot snap = entry.ModifiedSnapshots[i];
                        if (snap.Id.IsNull || snap.Id.IsErased) continue;
                        try
                        {
                            Entity ent = tr.GetObject(snap.Id, OpenMode.ForWrite, false, true) as Entity;
                            if (ent == null) continue;

                            for (int t = 0; t < snap.InverseTransforms.Count; t++)
                            {
                                try { ent.TransformBy(snap.InverseTransforms[t]); } catch { }
                            }
                            if (!string.IsNullOrEmpty(snap.OldLayer) && lt.Has(snap.OldLayer))
                                ent.Layer = snap.OldLayer;
                            ent.Color = RestoreColor(snap.OldColor);

                            if (snap.HadBlockRecordChange && ent is BlockReference && !snap.OldBlockRecordId.IsNull && !snap.OldBlockRecordId.IsErased)
                            {
                                try { ((BlockReference)ent).BlockTableRecord = snap.OldBlockRecordId; } catch { }
                            }
                            if (snap.HadBlockRotation && ent is BlockReference)
                                ((BlockReference)ent).Rotation = snap.OldBlockRotation;
                            if (snap.HadCircleProps && ent is Circle)
                            {
                                Circle cc = (Circle)ent;
                                cc.Center = snap.OldCenter;
                                if (snap.OldRadius > 1e-6) cc.Radius = snap.OldRadius;
                            }
                            if (snap.HadLineProps && ent is Line)
                            {
                                Line ln = (Line)ent;
                                ln.StartPoint = snap.OldStart;
                                ln.EndPoint = snap.OldEnd;
                            }
                            if (snap.HadDbTextProps && ent is DBText)
                            {
                                DBText dt = (DBText)ent;
                                dt.TextString = snap.OldDbText ?? "";
                                if (!snap.OldDbTextStyleId.IsNull) dt.TextStyleId = snap.OldDbTextStyleId;
                                if (snap.OldDbHeight > 1e-6) dt.Height = snap.OldDbHeight;
                            }
                            if (snap.HadMTextProps && ent is MText)
                            {
                                MText mt = (MText)ent;
                                mt.Contents = snap.OldMTextContents ?? "";
                                if (!snap.OldMTextStyleId.IsNull) mt.TextStyleId = snap.OldMTextStyleId;
                                if (snap.OldMTextHeight > 1e-6) mt.TextHeight = snap.OldMTextHeight;
                            }
                            if (snap.HadPolylineClosed && ent is Polyline)
                                ((Polyline)ent).Closed = snap.OldClosed;

                            try { ent.RecordGraphicsModified(true); } catch { }
                            restoredModified++;
                            cRestored++;
                        }
                        catch { }
                    }

                    for (int i = entry.BlockRecordRenames.Count - 1; i >= 0; i--)
                    {
                        BlockRecordRenameSnapshot r = entry.BlockRecordRenames[i];
                        if (r == null || r.BlockRecordId.IsNull || r.BlockRecordId.IsErased) continue;
                        try
                        {
                            BlockTableRecord btr = tr.GetObject(r.BlockRecordId, OpenMode.ForWrite, false, true) as BlockTableRecord;
                            if (btr != null && !string.IsNullOrEmpty(r.OldName))
                            {
                                btr.Name = r.OldName;
                                revertedBlockRenames++;
                                cRenamed++;
                            }
                        }
                        catch { }
                    }

                    for (int i = entry.CreatedBlockRecordIds.Count - 1; i >= 0; i--)
                    {
                        ObjectId bid = entry.CreatedBlockRecordIds[i];
                        if (bid.IsNull || bid.IsErased) continue;
                        try
                        {
                            DBObject bobj = tr.GetObject(bid, OpenMode.ForWrite, false, true);
                            if (bobj != null && !bobj.IsErased)
                            {
                                bobj.Erase(true);
                                erasedCreatedBlocks++;
                                cErasedBlocks++;
                            }
                        }
                        catch { }
                    }

                    undoneSummaries.Add(new Dictionary<string, object>
                    {
                        { "action_id", entry.ActionId },
                        { "tool_name", entry.ToolName },
                        { "summary", entry.Summary },
                        { "erased_created", cErased },
                        { "unerased_deleted", cUnerased },
                        { "restored_modified", cRestored },
                        { "renamed_blocks_reverted", cRenamed },
                        { "erased_created_blocks", cErasedBlocks }
                    });
                }

                db.UpdateExt(true);
                tr.Commit();
            }

            lock (_undoLock)
            {
                List<CadUndoEntry> stack;
                if (undoDb != null && _undoStacks.TryGetValue(undoDb, out stack))
                {
                    int removeN = Math.Min(countToUndo, stack.Count);
                    if (removeN > 0) stack.RemoveRange(stack.Count - removeN, removeN);
                }
            }

            doc.Editor.Regen();
            doc.Editor.UpdateScreen();

            return Ok(new Dictionary<string, object>
            {
                { "undone_steps", countToUndo },
                { "erased_created_count", erasedCreated },
                { "unerased_count", unerasedDeleted },
                { "restored_modified_count", restoredModified },
                { "renamed_blocks_reverted", revertedBlockRenames },
                { "erased_created_blocks", erasedCreatedBlocks },
                { "undone_actions", undoneSummaries },
                { "remaining_undo_steps", GetUndoHistoryCount(doc) }
            });
        }

        private static Dictionary<string, object> CopyEntities(Document doc, Dictionary<string, object> args)
        {
            List<string> handles = GetStringList(args, "handles");
            if (GetBool(args, "selected_only", false))
                handles.AddRange(GetSelectedHandleStrings(doc));

            string layerFilter = GetStr(args, "layer");
            string typeFilter = GetStr(args, "entity_type");
            string blockFilter = GetStr(args, "block_name");
            string textFilter = GetStr(args, "text_contains");
            bool excludeAnonymous = GetBool(args, "exclude_anonymous", false);
            bool excludeSystem = GetBool(args, "exclude_system", false);
            bool visibleOnly = GetBool(args, "visible_only", false);

            if (handles.Count == 0 && string.IsNullOrEmpty(typeFilter) && string.IsNullOrEmpty(blockFilter) &&
                string.IsNullOrEmpty(layerFilter) && string.IsNullOrEmpty(textFilter) &&
                (excludeAnonymous || excludeSystem))
            {
                typeFilter = "BlockReference";
            }
            bool hasFilterCriteria = !string.IsNullOrEmpty(layerFilter) || !string.IsNullOrEmpty(typeFilter) ||
                                     !string.IsNullOrEmpty(blockFilter) || !string.IsNullOrEmpty(textFilter);

            if (handles.Count == 0 && !hasFilterCriteria)
                return Err("Specify 'handles', 'selected_only': true, or filter criteria ('entity_type'/'block_name'/'layer') to copy entities.");

            string autoOutside = (GetStr(args, "auto_offset_outside") ?? "").Trim().ToLowerInvariant();
            double? customGap = GetNullableDouble(args, "gap");
            bool hasExplicitDxDy = args.ContainsKey("dx") || args.ContainsKey("dy");
            double dx = GetDouble(args, "dx", 0.0);
            double dy = GetDouble(args, "dy", 0.0);
            int count = Math.Max(1, Math.Min(100, GetInt(args, "count", 1)));
            string targetLayer = GetStr(args, "target_layer");
            int? colorIndex = GetNullableInt(args, "color_index");
            bool zoom = GetBool(args, "zoom", true);
            bool drawFrame = GetBool(args, "draw_frame", false);
            string frameLayer = GetStr(args, "frame_layer");
            int frameColor = GetInt(args, "frame_color_index", colorIndex ?? 2);
            string frameLabel = GetStr(args, "frame_label");
            bool selectCopied = GetBool(args, "select_copied", false);

            Database db = doc.Database;
            List<string> copiedHandles = new List<string>();
            List<ObjectId> copiedObjectIds = new List<ObjectId>();
            List<ObjectId> allCreatedCopyIds = new List<ObjectId>();
            List<string> errors = new List<string>();
            Extents3d? copiedExt = null;
            string frameHandle = null;
            string labelHandle = null;
            bool switchedToModel = false;

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                    string resolvedLayer = null;
                    if (!string.IsNullOrEmpty(targetLayer))
                        resolvedLayer = EnsureLayerSafe(db, tr, targetLayer, colorIndex.HasValue ? (short?)ClampColor(colorIndex.Value) : null, null);

                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                    List<Entity> srcEntities = new List<Entity>();
                    Extents3d? srcExt = null;
                    Extents3d? allDrawingExt = null;

                    if (handles.Count > 0)
                    {
                        foreach (string h in handles)
                        {
                            ObjectId id = ResolveHandle(db, h);
                            if (id.IsNull || id.IsErased) { errors.Add("Handle not found: " + h); continue; }
                            Entity src = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (src == null) continue;
                            srcEntities.Add(src);
                        }
                    }
                    else
                    {
                        HashSet<string> emptySel = new HashSet<string>();
                        foreach (ObjectId id in ms)
                        {
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null) continue;
                            bool entVisible = IsEntityVisible(ent, layerHiddenFlags);
                            if (EntityMatchesFilters(tr, ent, emptySel, false, emptySel, layerFilter, typeFilter,
                                blockFilter, textFilter, null, excludeAnonymous, excludeSystem, visibleOnly, entVisible))
                            {
                                srcEntities.Add(ent);
                            }
                        }
                    }

                    if (srcEntities.Count == 0)
                    {
                        tr.Commit();
                        return Err("No matching entities found to copy.");
                    }

                    foreach (Entity sEnt in srcEntities)
                    {
                        try
                        {
                            Extents3d ex = sEnt.GeometricExtents;
                            if (IsFiniteBounds(ex))
                            {
                                if (!srcExt.HasValue) srcExt = ex;
                                else { Extents3d c = srcExt.Value; c.AddExtents(ex); srcExt = c; }
                            }
                        }
                        catch { }
                    }

                    if (!string.IsNullOrEmpty(autoOutside) || (!hasExplicitDxDy && count == 1))
                    {
                        foreach (ObjectId id in ms)
                        {
                            Entity eAll = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (eAll == null || eAll is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                            try
                            {
                                Extents3d aex = eAll.GeometricExtents;
                                if (IsFiniteBounds(aex))
                                {
                                    if (!allDrawingExt.HasValue) allDrawingExt = aex;
                                    else { Extents3d c = allDrawingExt.Value; c.AddExtents(aex); allDrawingExt = c; }
                                }
                            }
                            catch { }
                        }

                        if (allDrawingExt.HasValue && srcExt.HasValue)
                        {
                            Extents3d a = allDrawingExt.Value;
                            Extents3d s = srcExt.Value;
                            double srcW = Math.Max(1e-2, s.MaxPoint.X - s.MinPoint.X);
                            double srcH = Math.Max(1e-2, s.MaxPoint.Y - s.MinPoint.Y);
                            double allW = Math.Max(1e-2, a.MaxPoint.X - a.MinPoint.X);
                            double allH = Math.Max(1e-2, a.MaxPoint.Y - a.MinPoint.Y);
                            double gap = customGap.HasValue ? customGap.Value : Math.Max(Math.Min(Math.Max(srcW, srcH) * 0.25, Math.Max(allW, allH) * 0.15), Math.Max(srcW, srcH) * 0.15);
                            string dir = string.IsNullOrEmpty(autoOutside) ? "right" : autoOutside;
                            if (dir == "left") { dx = (a.MinPoint.X - gap) - s.MaxPoint.X; dy = 0.0; }
                            else if (dir == "up") { dx = 0.0; dy = (a.MaxPoint.Y + gap) - s.MinPoint.Y; }
                            else if (dir == "down") { dx = 0.0; dy = (a.MinPoint.Y - gap) - s.MaxPoint.Y; }
                            else { dx = (a.MaxPoint.X + gap) - s.MinPoint.X; dy = 0.0; }
                        }
                        else if (!hasExplicitDxDy)
                        {
                            double uPerM = 1000.0;
                            try { uPerM = Convert.ToDouble(GetUnitsInfo(db)["units_per_m"], CultureInfo.InvariantCulture); } catch { }
                            dx = uPerM;
                            dy = 0.0;
                        }
                    }

                    foreach (Entity src in srcEntities)
                    {
                        for (int i = 1; i <= count; i++)
                        {
                            Entity clone = src.Clone() as Entity;
                            if (clone == null) continue;
                            Matrix3d stepDisp = (Math.Abs(dx) > 1e-9 || Math.Abs(dy) > 1e-9)
                                ? Matrix3d.Displacement(new Vector3d(i * dx, i * dy, 0.0))
                                : Matrix3d.Identity;
                            if (stepDisp != Matrix3d.Identity)
                                clone.TransformBy(stepDisp);
                            if (!string.IsNullOrEmpty(resolvedLayer))
                                clone.Layer = resolvedLayer;
                            if (colorIndex.HasValue)
                                clone.ColorIndex = ClampColor(colorIndex.Value);

                            ObjectId clonedId = ms.AppendEntity(clone);
                            tr.AddNewlyCreatedDBObject(clone, true);
                            CloneAttributesIfBlock(tr, src, clone, stepDisp, resolvedLayer, colorIndex);
                            copiedObjectIds.Add(clonedId);
                            copiedHandles.Add(clone.Handle.ToString());

                            try
                            {
                                Extents3d cex = clone.GeometricExtents;
                                if (IsFiniteBounds(cex))
                                {
                                    if (!copiedExt.HasValue) copiedExt = cex;
                                    else { Extents3d c = copiedExt.Value; c.AddExtents(cex); copiedExt = c; }
                                }
                            }
                            catch { }
                        }
                    }

                    if ((drawFrame || !string.IsNullOrWhiteSpace(frameLabel)) && copiedExt.HasValue)
                    {
                        Extents3d ce = copiedExt.Value;
                        double span = Math.Max(1e-2, Math.Max(ce.MaxPoint.X - ce.MinPoint.X, ce.MaxPoint.Y - ce.MinPoint.Y));
                        double pad = Math.Max(1e-3, span * 0.04);
                        Extents3d padded = new Extents3d(
                            new Point3d(ce.MinPoint.X - pad, ce.MinPoint.Y - pad, 0.0),
                            new Point3d(ce.MaxPoint.X + pad, ce.MaxPoint.Y + pad, 0.0));
                        string fLayer = EnsureLayerSafe(db, tr,
                            !string.IsNullOrEmpty(frameLayer) ? frameLayer : (!string.IsNullOrEmpty(resolvedLayer) ? resolvedLayer : "_AI_HIGHLIGHT"),
                            ClampColor(frameColor), null);

                        if (drawFrame)
                        {
                            Polyline frame = CreateFramePolyline(padded, fLayer, ClampColor(frameColor));
                            ObjectId fid = ms.AppendEntity(frame);
                            tr.AddNewlyCreatedDBObject(frame, true);
                            allCreatedCopyIds.Add(fid);
                            frameHandle = frame.Handle.ToString();
                            copiedExt = padded;
                        }

                        if (!string.IsNullOrWhiteSpace(frameLabel))
                        {
                            double txtH = Math.Max(span * 0.022, Math.Min(pad * 1.15, span * 0.038));
                            MText lbl = new MText();
                            lbl.SetDatabaseDefaults(db);
                            lbl.Layer = fLayer;
                            lbl.ColorIndex = ClampColor(frameColor);
                            lbl.TextStyleId = EnsureUnicodeTextStyle(db, tr);
                            lbl.Contents = EscapeMTextPlain(frameLabel);
                            lbl.TextHeight = txtH;
                            lbl.Width = Math.Max(txtH * 8.0, span * 0.95);
                            lbl.Attachment = AttachmentPoint.BottomLeft;
                            lbl.Location = new Point3d(padded.MinPoint.X, padded.MaxPoint.Y + pad * 0.35, 0.0);
                            ObjectId lid = ms.AppendEntity(lbl);
                            tr.AddNewlyCreatedDBObject(lbl, true);
                            allCreatedCopyIds.Add(lid);
                            labelHandle = lbl.Handle.ToString();
                            Extents3d lex = copiedExt.Value;
                            lex.AddPoint(new Point3d(padded.MaxPoint.X, padded.MaxPoint.Y + pad * 0.45 + txtH * 1.4, 0.0));
                            copiedExt = lex;
                        }
                    }

                    db.UpdateExt(true);
                    tr.Commit();
                }

                allCreatedCopyIds.AddRange(copiedObjectIds);
                if (allCreatedCopyIds.Count > 0)
                {
                    CadUndoEntry undoEntry = new CadUndoEntry();
                    undoEntry.ToolName = "copy_entities";
                    undoEntry.Summary = "Copied " + copiedObjectIds.Count.ToString(CultureInfo.InvariantCulture) + " entity(s)";
                    undoEntry.CreatedIds.AddRange(allCreatedCopyIds);
                    PushUndoEntry(doc, undoEntry);
                }

                if ((zoom || selectCopied) && copiedObjectIds.Count > 0 && !db.TileMode)
                {
                    try { LayoutManager.Current.CurrentLayout = "Model"; switchedToModel = true; } catch { }
                }
                if (zoom && copiedExt.HasValue)
                {
                    ZoomToExtents3d(doc, copiedExt.Value, 1.25);
                }
                else
                {
                    doc.Editor.Regen();
                }
                if (selectCopied && copiedObjectIds.Count > 0)
                {
                    try { Application.SetSystemVariable("PICKFIRST", 1); } catch { }
                    doc.Editor.SetImpliedSelection(copiedObjectIds.ToArray());
                }
                doc.Editor.UpdateScreen();
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "copied_count", copiedHandles.Count },
                { "copied_handles", copiedHandles.Count > MaxReturnedHandles ? copiedHandles.GetRange(0, MaxReturnedHandles) : copiedHandles },
                { "shift", Pt2(dx, dy) },
                { "switched_to_model", switchedToModel }
            };
            if (copiedExt.HasValue)
            {
                res["copied_bounds"] = new double[]
                {
                    Round(copiedExt.Value.MinPoint.X), Round(copiedExt.Value.MinPoint.Y),
                    Round(copiedExt.Value.MaxPoint.X), Round(copiedExt.Value.MaxPoint.Y)
                };
            }
            if (!string.IsNullOrEmpty(frameHandle)) res["frame_handle"] = frameHandle;
            if (!string.IsNullOrEmpty(labelHandle)) res["label_handle"] = labelHandle;
            if (errors.Count > 0) res["errors"] = errors;
            return Ok(res);
        }

        private static Dictionary<string, object> DeleteEntities(Document doc, Dictionary<string, object> args)
        {
            List<string> handles = GetStringList(args, "handles");
            bool selectedOnly = GetBool(args, "selected_only", false);
            string layer = GetStr(args, "layer") ?? GetStr(args, "layer_filter");
            bool clearReview = GetBool(args, "clear_review_layer", false);
            bool clearHighlight = GetBool(args, "clear_highlight_layer", false);
            if (clearReview) layer = DrawingExecutionCommands.ReviewLayerName;
            else if (clearHighlight && string.IsNullOrEmpty(layer)) layer = "_AI_HIGHLIGHT";

            if (selectedOnly) handles.AddRange(GetSelectedHandleStrings(doc));
            if (handles.Count == 0 && string.IsNullOrEmpty(layer))
                return Err("Refusing blind delete: specify 'handles', 'selected_only': true, 'layer', 'clear_highlight_layer': true, or 'clear_review_layer': true.");

            Database db = doc.Database;
            List<string> deleted = new List<string>();
            List<ObjectId> deletedIds = new List<ObjectId>();
            List<string> errors = new List<string>();

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!string.IsNullOrEmpty(layer))
                {
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent != null && string.Equals(ent.Layer, layer, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                ent.UpgradeOpen();
                                string hs = ent.Handle.ToString();
                                ent.Erase(true);
                                deleted.Add(hs);
                                deletedIds.Add(id);
                            }
                            catch (System.Exception ex) { errors.Add(ex.Message); }
                        }
                    }
                }

                foreach (string h in handles)
                {
                    ObjectId id = ResolveHandle(db, h);
                    if (id.IsNull || id.IsErased) { errors.Add("Handle not found or already erased: " + h); continue; }
                    try
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent != null)
                        {
                            string hs = ent.Handle.ToString();
                            ent.Erase(true);
                            deleted.Add(hs);
                            deletedIds.Add(id);
                        }
                    }
                    catch (System.Exception ex) { errors.Add("Handle " + h + ": " + ex.Message); }
                }

                db.UpdateExt(true);
                tr.Commit();
            }

            if (deletedIds.Count > 0)
            {
                CadUndoEntry undoEntry = new CadUndoEntry();
                undoEntry.ToolName = "delete_entities";
                undoEntry.Summary = "Deleted " + deletedIds.Count.ToString(CultureInfo.InvariantCulture) + " entity(s)";
                undoEntry.ErasedIds.AddRange(deletedIds);
                PushUndoEntry(doc, undoEntry);
            }

            doc.Editor.Regen();
            doc.Editor.UpdateScreen();

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "deleted_count", deleted.Count },
                { "deleted_handles", deleted },
                { "undo_history_steps", GetUndoHistoryCount(doc) }
            };
            if (errors.Count > 0) res["errors"] = errors;
            return Ok(res);
        }

        private static Dictionary<string, object> CreateBlock(Document doc, Dictionary<string, object> args)
        {
            string rawName = GetStr(args, "block_name");
            if (string.IsNullOrWhiteSpace(rawName))
                return Err("'block_name' is required.");
            string blockName = CleanBlockName(rawName);
            if (string.IsNullOrEmpty(blockName))
                return Err("Invalid 'block_name'. Use a valid AutoCAD block name without '*' prefix.");

            List<object> items = AsList(GetObj(args, "entities"));
            List<string> explicitHandles = GetStringList(args, "handles");
            bool selectedOnly = GetBool(args, "selected_only", false);
            bool fromExisting = items.Count == 0 && (selectedOnly || explicitHandles.Count > 0);
            if (items.Count == 0 && !fromExisting)
                return Err("Provide either 'selected_only': true / 'handles' (to block existing CAD entities) OR 'entities' array.");

            bool overwrite = GetBool(args, "overwrite", true);
            bool hasExplicitBase = args.ContainsKey("base_point") && args["base_point"] != null;
            Point3d explicitBase = GetPoint3d(args, "base_point", Point3d.Origin);
            double[] insertAt = GetDoubleArray(args, "insert_at");
            string insertLayerArg = GetStr(args, "insert_layer");
            double insertRotDeg = GetDouble(args, "insert_rotation_deg", 0.0);
            bool replaceSelection = GetBool(args, "replace_selection", fromExisting && (insertAt == null || insertAt.Length < 2));
            bool colorByBlock = GetBool(args, "color_byblock", false);
            bool zoom = GetBool(args, "zoom", true);

            Database db = doc.Database;
            string insertedHandle = null;
            int childCount = 0;
            int erasedOrigCount = 0;
            Point3d resolvedOrigin = explicitBase;
            Extents3d? targetExt = null;
            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();
            List<ObjectId> createdBlockIds = new List<ObjectId>();
            List<ObjectId> finalSelectIds = new List<ObjectId>();

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                    List<ObjectId> srcObjectIds = new List<ObjectId>();
                    string dominantSrcLayer = "0";
                    if (fromExisting)
                    {
                        List<string> targetHandleStrs = new List<string>();
                        if (selectedOnly) targetHandleStrs.AddRange(GetSelectedHandleStrings(doc));
                        targetHandleStrs.AddRange(explicitHandles);

                        if (targetHandleStrs.Count == 0)
                        {
                            tr.Commit();
                            return Err("No entities are currently selected in AutoCAD. Select objects first or pass 'handles'.");
                        }

                        LayerTable ltCheck = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        HashSet<string> lockedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (ObjectId lid in ltCheck)
                        {
                            LayerTableRecord ltr = tr.GetObject(lid, OpenMode.ForRead) as LayerTableRecord;
                            if (ltr != null && ltr.IsLocked) lockedLayers.Add(ltr.Name);
                        }

                        HashSet<ObjectId> seenSrc = new HashSet<ObjectId>();
                        Dictionary<string, int> layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        Extents3d? srcExt = null;

                        foreach (string h in targetHandleStrs)
                        {
                            ObjectId sid = ResolveHandle(db, h);
                            if (sid.IsNull || sid.IsErased || !seenSrc.Add(sid)) continue;
                            Entity ent = tr.GetObject(sid, OpenMode.ForRead) as Entity;
                            if (ent == null || ent is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                            string lyr = ent.Layer ?? "0";
                            if (replaceSelection && lockedLayers.Contains(lyr))
                            {
                                tr.Commit();
                                return Err("Cannot replace selected entity on locked layer '" + lyr + "'. Unlock the layer or pass replace_selection=false.");
                            }
                            srcObjectIds.Add(sid);
                            layerCounts[lyr] = layerCounts.ContainsKey(lyr) ? layerCounts[lyr] + 1 : 1;
                            try
                            {
                                Extents3d ex = ent.GeometricExtents;
                                if (IsFiniteBounds(ex))
                                {
                                    if (!srcExt.HasValue) srcExt = ex;
                                    else { Extents3d cur = srcExt.Value; cur.AddExtents(ex); srcExt = cur; }
                                }
                            }
                            catch { }
                        }

                        if (srcObjectIds.Count == 0)
                        {
                            tr.Commit();
                            return Err("None of the selected/specified handles resolved to valid active AutoCAD entities.");
                        }

                        int bestLC = -1;
                        foreach (KeyValuePair<string, int> kv in layerCounts)
                        {
                            if (kv.Value > bestLC) { bestLC = kv.Value; dominantSrcLayer = kv.Key; }
                        }

                        if (!hasExplicitBase && srcExt.HasValue)
                        {
                            resolvedOrigin = new Point3d(
                                (srcExt.Value.MinPoint.X + srcExt.Value.MaxPoint.X) * 0.5,
                                (srcExt.Value.MinPoint.Y + srcExt.Value.MaxPoint.Y) * 0.5,
                                0.0);
                        }
                        targetExt = srcExt;
                    }

                    BlockTableRecord blk;
                    ObjectId blkId;
                    if (bt.Has(blockName))
                    {
                        if (!overwrite)
                        {
                            tr.Commit();
                            return Err("Block '" + blockName + "' already exists and overwrite=false.");
                        }
                        blkId = bt[blockName];
                        blk = (BlockTableRecord)tr.GetObject(blkId, OpenMode.ForWrite);
                        foreach (ObjectId oldId in blk)
                        {
                            DBObject oldObj = tr.GetObject(oldId, OpenMode.ForWrite);
                            if (oldObj != null && !oldObj.IsErased) oldObj.Erase(true);
                        }
                        blk.Origin = Point3d.Origin;
                    }
                    else
                    {
                        blk = new BlockTableRecord();
                        blk.Name = blockName;
                        blk.Origin = fromExisting ? Point3d.Origin : explicitBase;
                        blkId = bt.Add(blk);
                        tr.AddNewlyCreatedDBObject(blk, true);
                        createdBlockIds.Add(blkId);
                    }

                    if (fromExisting)
                    {
                        childCount = CloneEntitiesIntoBlockDefinition(db, tr, srcObjectIds, blk, blkId, resolvedOrigin, colorByBlock);
                        if (childCount == 0)
                        {
                            tr.Commit();
                            return Err("Failed to clone any valid entities into block definition '" + blockName + "'.");
                        }

                        bool shouldInsert = replaceSelection || (insertAt != null && insertAt.Length >= 2);
                        if (shouldInsert)
                        {
                            Point3d insPos = (insertAt != null && insertAt.Length >= 2)
                                ? new Point3d(insertAt[0], insertAt[1], 0.0)
                                : resolvedOrigin;
                            string safeInsLayer = EnsureLayerSafe(db, tr, !string.IsNullOrEmpty(insertLayerArg) ? insertLayerArg : dominantSrcLayer, null, null);
                            BlockReference br = new BlockReference(insPos, blkId);
                            br.Layer = safeInsLayer;
                            br.Rotation = insertRotDeg * Math.PI / 180.0;
                            ObjectId brId = ms.AppendEntity(br);
                            tr.AddNewlyCreatedDBObject(br, true);
                            createdIds.Add(brId);
                            finalSelectIds.Add(brId);
                            insertedHandle = br.Handle.ToString();
                            try
                            {
                                Extents3d bex = br.GeometricExtents;
                                if (IsFiniteBounds(bex)) targetExt = bex;
                            }
                            catch { }
                        }

                        if (replaceSelection)
                        {
                            foreach (ObjectId sid in srcObjectIds)
                            {
                                Entity origEnt = tr.GetObject(sid, OpenMode.ForWrite, false, true) as Entity;
                                if (origEnt != null && !origEnt.IsErased)
                                {
                                    origEnt.Erase(true);
                                    erasedIds.Add(sid);
                                    erasedOrigCount++;
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (object rawItem in items)
                        {
                            Dictionary<string, object> item = AsDict(rawItem);
                            if (item == null) continue;
                            string subLayer = GetStr(item, "layer");
                            string resolvedSubLayer = string.IsNullOrEmpty(subLayer)
                                ? "0"
                                : EnsureLayerSafe(db, tr, subLayer, GetNullableInt(item, "color_index").HasValue ? (short?)ClampColor(GetNullableInt(item, "color_index").Value) : null, null);
                            string warn;
                            Entity child = CreateSingleEntity(db, tr, bt, item, resolvedSubLayer, GetNullableInt(item, "color_index"), out warn);
                            if (child != null)
                            {
                                blk.AppendEntity(child);
                                tr.AddNewlyCreatedDBObject(child, true);
                                FinalizeAppendedEntity(db, child, item);
                                childCount++;
                            }
                        }

                        if (insertAt != null && insertAt.Length >= 2)
                        {
                            string safeInsLayer = EnsureLayerSafe(db, tr, !string.IsNullOrEmpty(insertLayerArg) ? insertLayerArg : "0", null, null);
                            BlockReference br = new BlockReference(new Point3d(insertAt[0], insertAt[1], 0.0), blkId);
                            br.Layer = safeInsLayer;
                            br.Rotation = insertRotDeg * Math.PI / 180.0;
                            ObjectId brId = ms.AppendEntity(br);
                            tr.AddNewlyCreatedDBObject(br, true);
                            createdIds.Add(brId);
                            insertedHandle = br.Handle.ToString();
                        }
                    }

                    db.UpdateExt(true);
                    tr.Commit();
                }

                if (createdIds.Count > 0 || erasedIds.Count > 0 || createdBlockIds.Count > 0)
                {
                    CadUndoEntry undoEntry = new CadUndoEntry();
                    undoEntry.ToolName = "create_block";
                    undoEntry.Summary = fromExisting
                        ? ("Created block '" + blockName + "' from " + childCount.ToString(CultureInfo.InvariantCulture) + " entity(s)")
                        : ("Created block '" + blockName + "'");
                    undoEntry.CreatedIds.AddRange(createdIds);
                    undoEntry.ErasedIds.AddRange(erasedIds);
                    undoEntry.CreatedBlockRecordIds.AddRange(createdBlockIds);
                    PushUndoEntry(doc, undoEntry);
                }

                if (zoom)
                {
                    if (targetExt.HasValue && IsFiniteBounds(targetExt.Value))
                        ZoomToExtents3d(doc, targetExt.Value, 1.35);
                    else if (insertedHandle != null)
                        DrawingExecutionCommands.ZoomExtents(doc);
                }
                else
                {
                    doc.Editor.Regen();
                }

                if (finalSelectIds.Count > 0)
                {
                    try { Application.SetSystemVariable("PICKFIRST", 1); } catch { }
                    try { doc.Editor.SetImpliedSelection(finalSelectIds.ToArray()); } catch { }
                }
                doc.Editor.UpdateScreen();
            }

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "block_name", blockName },
                { "created_from_selection", fromExisting },
                { "definition_entities", childCount },
                { "replaced_source_entities", erasedOrigCount },
                { "inserted_handle", insertedHandle ?? "" },
                { "base_point", Pt2(resolvedOrigin.X, resolvedOrigin.Y) },
                { "undo_history_steps", GetUndoHistoryCount(doc) }
            };
            return Ok(result);
        }

        private static int CloneEntitiesIntoBlockDefinition(
            Database db,
            Transaction tr,
            List<ObjectId> sourceIds,
            BlockTableRecord targetBlk,
            ObjectId targetBlkId,
            Point3d worldOrigin,
            bool colorByBlock)
        {
            if (sourceIds == null || sourceIds.Count == 0 || targetBlk == null) return 0;
            Matrix3d shiftToLocal = Matrix3d.Displacement(Point3d.Origin - worldOrigin);
            int clonedCount = 0;

            try
            {
                ObjectIdCollection srcCol = new ObjectIdCollection();
                foreach (ObjectId sid in sourceIds) srcCol.Add(sid);
                IdMapping idMap = new IdMapping();
                db.DeepCloneObjects(srcCol, targetBlkId, idMap, false);

                foreach (ObjectId sid in sourceIds)
                {
                    if (!idMap.Contains(sid)) continue;
                    IdPair pair = idMap[sid];
                    if (!pair.IsCloned || pair.Value.IsNull) continue;
                    Entity cloned = tr.GetObject(pair.Value, OpenMode.ForWrite, false, true) as Entity;
                    if (cloned == null) continue;
                    if (shiftToLocal != Matrix3d.Identity)
                    {
                        try { cloned.TransformBy(shiftToLocal); } catch { }
                    }
                    if (colorByBlock)
                        cloned.Color = Color.FromColorIndex(ColorMethod.ByBlock, 0);
                    clonedCount++;
                }
                if (clonedCount > 0) return clonedCount;
            }
            catch { }

            // Fallback per-entity Clone() if DeepCloneObjects encounters non-clonable reactors
            foreach (ObjectId sid in sourceIds)
            {
                try
                {
                    Entity src = tr.GetObject(sid, OpenMode.ForRead) as Entity;
                    if (src == null) continue;
                    Entity clone = src.Clone() as Entity;
                    if (clone == null) continue;
                    if (shiftToLocal != Matrix3d.Identity)
                    {
                        try { clone.TransformBy(shiftToLocal); } catch { }
                    }
                    if (colorByBlock)
                        clone.Color = Color.FromColorIndex(ColorMethod.ByBlock, 0);
                    targetBlk.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    CloneAttributesIfBlock(tr, src, clone, shiftToLocal, null, colorByBlock ? (int?)0 : null);
                    clonedCount++;
                }
                catch { }
            }
            return clonedCount;
        }

        // ==================== Deterministic Geometric Shape Matcher & Block Converter ====================

        private enum GeomCat
        {
            CurvePoly = 1,
            Circle = 2,
            Arc = 3,
            Ellipse = 4,
            Solid = 5,
            Hatch = 6,
            Spline = 7,
            BlockRef = 8,
            Text = 9
        }

        private sealed class GeomShape
        {
            public int Index;
            public int ParentPoolIdx = -1;
            public int[] MergedPoolIndices = null;
            public double ParentWeight = 1.0;
            public ObjectId Id;
            public string Handle;
            public string Layer;
            public string ClassName;
            public GeomCat Category;
            public bool IsSelected;
            public bool OnLockedLayer;
            public Point2d Centroid;
            public double FeatureSpan;
            public double TotalLength;
            public double Area;

            // CurvePoly / Spline / Solid / Hatch points
            public Point2d[] Pts;
            public double[] Bulges;
            public bool Closed;
            public bool IsLineOrTwoPtPoly;
            public bool HasSignificantBulge;
            public Point2d LongestP0;
            public Point2d LongestP1;
            public double LongestSpanLen;

            // Circle / Arc / Ellipse
            public Point2d Center;
            public double Radius;
            public double MinorRadius;
            public double SweepAngle;
            public Point2d StartPt;
            public Point2d EndPt;
            public Point2d MidPt;
            public Point2d MajorDir;

            // Hatch / Text / BlockRef
            public string TextOrPattern;
            public double TextHeight;
            public ObjectId RefBlockDefId;
            public double RefScaleX;
            public double RefScaleY;
            public double RefRotation;

            // Match frequency counter on Template items
            public int CandidateFreq;
        }

        private sealed class SimilarityMatch2D
        {
            public Point2d TemplateOrigin;
            public Point2d TargetOrigin;
            public double Scale = 1.0;
            public double CosA;
            public double SinA;
            public double RotationRad;
            public bool MirroredY;

            public Point2d Apply(Point2d p)
            {
                double dx = (p.X - TemplateOrigin.X) * Scale;
                double dy = (p.Y - TemplateOrigin.Y) * Scale;
                if (MirroredY) dy = -dy;
                return new Point2d(
                    TargetOrigin.X + (CosA * dx - SinA * dy),
                    TargetOrigin.Y + (SinA * dx + CosA * dy));
            }
        }

        private sealed class ScaleAngleCandidate
        {
            public double Scale;
            public double Theta;
            public ScaleAngleCandidate(double scale, double theta)
            {
                Scale = scale;
                Theta = theta;
            }
        }

        private sealed class MatchSearchOptions
        {
            public double PosTol;
            public double TolRatio;
            public double SizeTolRatio;
            public bool AllowRotation;
            public bool AllowMirror;
            public bool AllowScale;
            public double MinScale;
            public double MaxScale;
            public bool StrictTypes;
            public bool IgnoreTextContent;
            public bool NormalizeGeometry;
            public bool AllowFuzzy;
            public double MinMatchRatio;
            public int MinEntityMatches;
        }

        private sealed class MatchedClusterInstance
        {
            public List<int> MatchedIndices = new List<int>();
            public List<ObjectId> MatchedIds = new List<ObjectId>();
            public List<string> MatchedHandles = new List<string>();
            public Point2d InsertPosition;
            public double RotationRad;
            public double Scale = 1.0;
            public bool MirroredY;
            public double MatchRatio = 1.0;
            public string MatchMode = "exact";
            public string DominantLayer;
            public Extents3d Bounds;
            public double AvgError;
            public int SelectedHits;
        }

        private static Dictionary<string, object> ConvertMatchingShapesToBlock(Document doc, Dictionary<string, object> args)
        {
            bool dryRun = GetBool(args, "dry_run", GetBool(args, "preview_only", GetBool(args, "match_only", false)));
            string rawName = GetStr(args, "block_name");
            string blockName = string.IsNullOrWhiteSpace(rawName) ? null : CleanBlockName(rawName);
            if (!dryRun && string.IsNullOrEmpty(blockName))
                return Err("'block_name' is required when converting matching shapes into a block (or set dry_run=true to preview matches first).");

            List<string> explicitHandles = GetStringList(args, "handles");
            double[] templateBbox = GetDoubleArray(args, "bbox");
            bool selectedOnly = GetBool(args, "selected_only", explicitHandles.Count == 0 && (templateBbox == null || templateBbox.Length < 4));

            bool allowRotation = GetBool(args, "allow_rotation", true);
            bool? allowMirrorArg = GetNullableBool(args, "allow_mirror");
            bool? allowScaleArg = GetNullableBool(args, "allow_scale");
            bool hasExplicitScaleBounds = args.ContainsKey("min_scale") || args.ContainsKey("max_scale");
            double minScale = Math.Max(0.05, Math.Min(1.0, GetDouble(args, "min_scale", 0.45)));
            double maxScale = Math.Max(1.0, Math.Min(20.0, GetDouble(args, "max_scale", 2.25)));
            double tolRatio = Math.Max(0.002, Math.Min(0.25, GetDouble(args, "tolerance_ratio", 0.03)));
            double sizeTolRatio = Math.Max(0.01, Math.Min(0.35, GetDouble(args, "size_tolerance_ratio", 0.08)));
            bool normalizeGeometry = GetBool(args, "normalize_geometry", true);
            bool? fuzzyMatchArg = GetNullableBool(args, "fuzzy_match");
            double minMatchRatio = Math.Max(0.50, Math.Min(1.0, GetDouble(args, "min_match_ratio", 0.80)));
            bool strictTypes = GetBool(args, "strict_types", false);
            bool ignoreText = GetBool(args, "ignore_text", false);
            bool ignoreTextContent = GetBool(args, "ignore_text_content", false);
            string layerFilter = GetStr(args, "layer_filter") ?? GetStr(args, "layer");
            double? explicitTol = GetNullableDouble(args, "tolerance");
            bool overwrite = GetBool(args, "overwrite", false);
            bool reuseExisting = GetBool(args, "reuse_existing_block", false);
            bool eraseOriginals = GetBool(args, "erase_originals", true);
            bool colorByBlock = GetBool(args, "color_byblock", false);
            string insertLayerArg = GetStr(args, "insert_layer");
            bool selectConverted = GetBool(args, "select_converted", true);
            bool drawHighlightBoxes = GetBool(args, "draw_highlight_boxes", dryRun);
            bool zoom = GetBool(args, "zoom", true);

            Database db = doc.Database;
            List<string> selectedNow = selectedOnly ? GetSelectedHandleStrings(doc) : new List<string>();
            if (selectedOnly && explicitHandles.Count == 0 && (templateBbox == null || templateBbox.Length < 4) && selectedNow.Count == 0)
            {
                return Err("No entities are currently selected in AutoCAD. Select one representative shape/component first (or pass 'handles' / 'bbox').");
            }

            List<MatchedClusterInstance> confirmedClusters = new List<MatchedClusterInstance>();
            List<ObjectId> createdBlockIds = new List<ObjectId>();
            List<ObjectId> createdEntityIds = new List<ObjectId>();
            List<ObjectId> erasedEntityIds = new List<ObjectId>();
            List<string> insertedHandles = new List<string>();
            List<double> rotationsDegFound = new List<double>();
            List<double> scalesFound = new List<double>();
            List<object> instanceSummaries = new List<object>();
            Extents3d? convertedExt = null;
            int templateEntityCount = 0;
            int totalMatchedEntities = 0;
            int erasedCount = 0;
            int exactMatchesCount = 0;
            int scaledMatchesCount = 0;
            int normalizedMatchesCount = 0;
            int fuzzyMatchesCount = 0;
            double usedTol = 0.0;
            bool switchedToModel = false;
            bool templateIncludedText = false;
            bool allowMirrorResolved = false;
            bool allowScaleResolved = false;
            bool fuzzyResolved = false;

            using (DocumentLock docLock = doc.LockDocument())
            {
                if (!db.TileMode)
                {
                    try { LayoutManager.Current.CurrentLayout = "Model"; switchedToModel = true; } catch { }
                }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    HashSet<string> lockedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> hiddenLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId lid in lt)
                    {
                        LayerTableRecord ltr = tr.GetObject(lid, OpenMode.ForRead) as LayerTableRecord;
                        if (ltr == null) continue;
                        if (ltr.IsLocked) lockedLayers.Add(ltr.Name);
                        if (ltr.IsOff || ltr.IsFrozen) hiddenLayers.Add(ltr.Name);
                    }

                    // Check existing block conflict early before scanning/mutating
                    int existingMsUsage = 0;
                    ObjectId existingBlkId = ObjectId.Null;
                    if (!dryRun && bt.Has(blockName))
                    {
                        existingBlkId = bt[blockName];
                        foreach (ObjectId mid in ms)
                        {
                            Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                            BlockReference mbr = me as BlockReference;
                            if (mbr != null && !IsLegendOrHighlightLayer(me.Layer) && GetBlockDefinitionId(mbr) == existingBlkId)
                                existingMsUsage++;
                        }
                        if (!overwrite && !reuseExisting && existingMsUsage > 0)
                        {
                            tr.Commit();
                            return Err("Block '" + blockName + "' already exists and is used " +
                                existingMsUsage.ToString(CultureInfo.InvariantCulture) +
                                " time(s) in ModelSpace. Choose a new 'block_name', or pass 'overwrite': true / 'reuse_existing_block': true.");
                        }
                    }

                    // 1. Resolve template ObjectIds
                    HashSet<ObjectId> templateIdSet = new HashSet<ObjectId>();
                    List<ObjectId> templateIdsOrdered = new List<ObjectId>();
                    HashSet<string> selectedHandleSet = new HashSet<string>(selectedNow, StringComparer.OrdinalIgnoreCase);

                    if (explicitHandles.Count > 0 || selectedOnly)
                    {
                        List<string> rawTplHandles = new List<string>();
                        if (selectedOnly) rawTplHandles.AddRange(selectedNow);
                        rawTplHandles.AddRange(explicitHandles);
                        foreach (string h in rawTplHandles)
                        {
                            ObjectId id = ResolveHandle(db, h);
                            if (id.IsNull || id.IsErased || !templateIdSet.Add(id)) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                            if (ent.OwnerId != ms.ObjectId) continue;
                            templateIdsOrdered.Add(id);
                        }
                    }
                    else if (templateBbox != null && templateBbox.Length >= 4)
                    {
                        double bx1 = Math.Min(templateBbox[0], templateBbox[2]);
                        double by1 = Math.Min(templateBbox[1], templateBbox[3]);
                        double bx2 = Math.Max(templateBbox[0], templateBbox[2]);
                        double by2 = Math.Max(templateBbox[1], templateBbox[3]);
                        foreach (ObjectId id in ms)
                        {
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                            if (IsLegendOrHighlightLayer(ent.Layer)) continue;
                            if (!string.IsNullOrEmpty(layerFilter) && !MatchesPatternOrSubstring(ent.Layer ?? "", layerFilter)) continue;
                            try
                            {
                                Extents3d ex = ent.GeometricExtents;
                                if (!IsFiniteBounds(ex)) continue;
                                if (ex.MinPoint.X >= bx1 - 1e-3 && ex.MaxPoint.X <= bx2 + 1e-3 &&
                                    ex.MinPoint.Y >= by1 - 1e-3 && ex.MaxPoint.Y <= by2 + 1e-3)
                                {
                                    if (templateIdSet.Add(id)) templateIdsOrdered.Add(id);
                                }
                            }
                            catch { }
                        }
                    }

                    if (templateIdsOrdered.Count == 0)
                    {
                        tr.Commit();
                        return Err("No valid ModelSpace entities found in the selected template.");
                    }

                    // 2. Build template GeomShapes
                    List<GeomShape> templateShapes = new List<GeomShape>();
                    List<ObjectId> templateCloneIds = new List<ObjectId>();
                    Extents3d? tplExt = null;
                    int nonTextGeomCount = 0;
                    foreach (ObjectId tid in templateIdsOrdered)
                    {
                        Entity ent = tr.GetObject(tid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        bool isTxt = (ent is DBText) || (ent is MText);
                        if (isTxt) templateIncludedText = true;
                        else nonTextGeomCount++;
                    }
                    bool skipTextInMatch = ignoreText && nonTextGeomCount > 0;

                    foreach (ObjectId tid in templateIdsOrdered)
                    {
                        Entity ent = tr.GetObject(tid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        if (skipTextInMatch && ((ent is DBText) || (ent is MText))) continue;
                        GeomShape gs = TryBuildGeomShape(tr, ent, selectedHandleSet, lockedLayers);
                        if (gs == null)
                        {
                            tr.Commit();
                            return Err("Selected template contains unsupported entity type '" + ent.GetType().Name +
                                "' (handle " + ent.Handle.ToString() + "). Supported types: Line, Polyline, Polyline2d, Circle, Arc, Ellipse, Spline, Solid, Hatch, BlockReference, DBText, MText.");
                        }
                        if (!dryRun && eraseOriginals && gs.OnLockedLayer)
                        {
                            tr.Commit();
                            return Err("Selected template entity " + gs.Handle + " sits on locked layer '" + gs.Layer + "'. Unlock the layer before converting.");
                        }
                        gs.Index = templateShapes.Count;
                        templateShapes.Add(gs);
                        templateCloneIds.Add(tid);

                        try
                        {
                            Extents3d ex = ent.GeometricExtents;
                            if (IsFiniteBounds(ex))
                            {
                                if (!tplExt.HasValue) tplExt = ex;
                                else { Extents3d cur = tplExt.Value; cur.AddExtents(ex); tplExt = cur; }
                            }
                        }
                        catch { }
                    }

                    if (templateShapes.Count == 0 || !tplExt.HasValue)
                    {
                        tr.Commit();
                        return Err("Selected template has no valid geometric bounds.");
                    }

                    templateEntityCount = templateShapes.Count;
                    Extents3d tex = tplExt.Value;
                    double tplW = tex.MaxPoint.X - tex.MinPoint.X;
                    double tplH = tex.MaxPoint.Y - tex.MinPoint.Y;
                    double spanT = Math.Max(1e-4, Math.Sqrt(tplW * tplW + tplH * tplH));
                    Point2d templateOrigin = new Point2d((tex.MinPoint.X + tex.MaxPoint.X) * 0.5, (tex.MinPoint.Y + tex.MaxPoint.Y) * 0.5);

                    usedTol = (explicitTol.HasValue && explicitTol.Value > 1e-6)
                        ? explicitTol.Value
                        : Math.Max(1e-4, spanT * tolRatio);

                    allowMirrorResolved = allowMirrorArg.HasValue ? allowMirrorArg.Value : (!templateIncludedText || skipTextInMatch);

                    int curveAnchorCount = 0;
                    for (int i = 0; i < templateShapes.Count; i++)
                    {
                        GeomCat gc = templateShapes[i].Category;
                        if (gc == GeomCat.Circle || gc == GeomCat.Arc || gc == GeomCat.Ellipse) curveAnchorCount++;
                    }
                    bool hasDistinctiveSymbolGeometry =
                        (curveAnchorCount >= 1 && templateShapes.Count >= 2) ||
                        (templateShapes.Count >= 5);
                    allowScaleResolved = allowScaleArg.HasValue
                        ? allowScaleArg.Value
                        : (hasDistinctiveSymbolGeometry || hasExplicitScaleBounds);

                    fuzzyResolved = fuzzyMatchArg.HasValue
                        ? fuzzyMatchArg.Value
                        : (templateShapes.Count >= 4 && minMatchRatio < 0.999);

                    HashSet<GeomCat> neededCats = new HashSet<GeomCat>();
                    for (int i = 0; i < templateShapes.Count; i++) neededCats.Add(templateShapes[i].Category);
                    if (normalizeGeometry && !strictTypes)
                    {
                        if (neededCats.Contains(GeomCat.Circle) || neededCats.Contains(GeomCat.CurvePoly))
                            neededCats.Add(GeomCat.Arc);
                        if (neededCats.Contains(GeomCat.Arc))
                            neededCats.Add(GeomCat.CurvePoly);
                    }

                    // 3. Scan ModelSpace once to index relevant candidate entities
                    List<GeomShape> pool = new List<GeomShape>();
                    foreach (ObjectId mid in ms)
                    {
                        if (mid.IsErased) continue;
                        Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                        if (me == null || me is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                        string lyr = me.Layer ?? "0";
                        if (IsLegendOrHighlightLayer(lyr)) continue;
                        if (hiddenLayers.Contains(lyr) && !templateIdSet.Contains(mid)) continue;
                        if (!string.IsNullOrEmpty(layerFilter) && !MatchesPatternOrSubstring(lyr, layerFilter) && !templateIdSet.Contains(mid)) continue;

                        GeomCat? cat = QuickCategorizeEntity(me);
                        if (!cat.HasValue || !neededCats.Contains(cat.Value)) continue;

                        GeomShape gs = TryBuildGeomShape(tr, me, selectedHandleSet, lockedLayers);
                        if (gs == null) continue;
                        if (!dryRun && eraseOriginals && gs.OnLockedLayer) continue;

                        gs.Index = pool.Count;
                        gs.ParentPoolIdx = gs.Index;
                        pool.Add(gs);
                    }

                    // 4. Run multi-stage similarity pattern matching (0..360 deg + scale + normalized + fuzzy)
                    MatchSearchOptions searchOpts = new MatchSearchOptions
                    {
                        PosTol = usedTol,
                        TolRatio = tolRatio,
                        SizeTolRatio = sizeTolRatio,
                        AllowRotation = allowRotation,
                        AllowMirror = allowMirrorResolved,
                        AllowScale = allowScaleResolved,
                        MinScale = minScale,
                        MaxScale = maxScale,
                        StrictTypes = strictTypes,
                        IgnoreTextContent = ignoreTextContent,
                        NormalizeGeometry = normalizeGeometry && !strictTypes,
                        AllowFuzzy = fuzzyResolved,
                        MinMatchRatio = minMatchRatio,
                        MinEntityMatches = Math.Max(3, (int)Math.Ceiling(templateShapes.Count * minMatchRatio))
                    };

                    confirmedClusters = FindMatchingClusters(
                        templateShapes,
                        templateOrigin,
                        spanT,
                        pool,
                        searchOpts);

                    // Guard locked layers before any mutation
                    if (!dryRun && eraseOriginals)
                    {
                        foreach (MatchedClusterInstance inst in confirmedClusters)
                        {
                            foreach (int pi in inst.MatchedIndices)
                            {
                                if (pi >= 0 && pi < pool.Count && pool[pi].OnLockedLayer)
                                {
                                    tr.Commit();
                                    return Err("Matched entity " + pool[pi].Handle + " is on locked layer '" + pool[pi].Layer + "'.");
                                }
                            }
                        }
                    }

                    // 5. Execute dry-run highlight OR actual Block conversion + atomic replacement
                    if (dryRun)
                    {
                        List<ObjectId> selectAllIds = new List<ObjectId>();
                        string hlLayer = drawHighlightBoxes ? EnsureLayerSafe(db, tr, "_AI_HIGHLIGHT", 2, null) : "_AI_HIGHLIGHT";
                        for (int ci = 0; ci < confirmedClusters.Count; ci++)
                        {
                            MatchedClusterInstance inst = confirmedClusters[ci];
                            if (inst.MatchMode == "fuzzy") fuzzyMatchesCount++;
                            else if (inst.MatchMode == "normalized") normalizedMatchesCount++;
                            else if (inst.MatchMode == "scaled") scaledMatchesCount++;
                            else exactMatchesCount++;

                            totalMatchedEntities += inst.MatchedIds.Count;
                            selectAllIds.AddRange(inst.MatchedIds);
                            if (IsFiniteBounds(inst.Bounds))
                            {
                                if (!convertedExt.HasValue) convertedExt = inst.Bounds;
                                else { Extents3d cur = convertedExt.Value; cur.AddExtents(inst.Bounds); convertedExt = cur; }
                                if (drawHighlightBoxes)
                                {
                                    double pad = Math.Max(usedTol * 2.0, spanT * inst.Scale * 0.03);
                                    Extents3d frameEx = new Extents3d(
                                        new Point3d(inst.Bounds.MinPoint.X - pad, inst.Bounds.MinPoint.Y - pad, 0.0),
                                        new Point3d(inst.Bounds.MaxPoint.X + pad, inst.Bounds.MaxPoint.Y + pad, 0.0));
                                    Polyline frame = CreateFramePolyline(frameEx, hlLayer, 2);
                                    ObjectId fid = ms.AppendEntity(frame);
                                    tr.AddNewlyCreatedDBObject(frame, true);
                                    createdEntityIds.Add(fid);
                                }
                            }
                            double rotDeg = NormalizeDegrees(inst.RotationRad * 180.0 / Math.PI);
                            RecordUniqueAngle(rotationsDegFound, rotDeg);
                            RecordUniqueAngle(scalesFound, Math.Round(inst.Scale, 3));
                            if (instanceSummaries.Count < 60)
                            {
                                instanceSummaries.Add(new Dictionary<string, object>
                                {
                                    { "index", ci + 1 },
                                    { "position", Pt2(inst.InsertPosition.X, inst.InsertPosition.Y) },
                                    { "rotation_deg", Round(rotDeg) },
                                    { "scale", Round(inst.Scale) },
                                    { "mirrored", inst.MirroredY },
                                    { "match_mode", inst.MatchMode },
                                    { "match_ratio", Round(inst.MatchRatio) },
                                    { "layer", inst.DominantLayer },
                                    { "entity_count", inst.MatchedIds.Count },
                                    { "avg_error", Round(inst.AvgError) }
                                });
                            }
                        }

                        db.UpdateExt(true);
                        tr.Commit();

                        if (createdEntityIds.Count > 0)
                        {
                            CadUndoEntry undoHl = new CadUndoEntry();
                            undoHl.ToolName = "convert_matching_shapes_to_block";
                            undoHl.Summary = "Highlighted " + confirmedClusters.Count.ToString(CultureInfo.InvariantCulture) + " matching shape(s)";
                            undoHl.CreatedIds.AddRange(createdEntityIds);
                            PushUndoEntry(doc, undoHl);
                        }

                        if (zoom && convertedExt.HasValue && IsFiniteBounds(convertedExt.Value))
                            ZoomToExtents3d(doc, convertedExt.Value, 1.25);
                        else
                            doc.Editor.Regen();

                        if (selectConverted && selectAllIds.Count > 0)
                        {
                            try { Application.SetSystemVariable("PICKFIRST", 1); } catch { }
                            try { doc.Editor.SetImpliedSelection(selectAllIds.ToArray()); } catch { }
                        }
                        doc.Editor.UpdateScreen();
                    }
                    else
                    {
                        BlockTableRecord blk;
                        ObjectId blkId;
                        bool reusing = reuseExisting && bt.Has(blockName);
                        if (reusing)
                        {
                            blkId = bt[blockName];
                            blk = (BlockTableRecord)tr.GetObject(blkId, OpenMode.ForRead);
                        }
                        else
                        {
                            if (bt.Has(blockName))
                            {
                                blkId = bt[blockName];
                                blk = (BlockTableRecord)tr.GetObject(blkId, OpenMode.ForWrite);
                                foreach (ObjectId oldId in blk)
                                {
                                    DBObject oldObj = tr.GetObject(oldId, OpenMode.ForWrite);
                                    if (oldObj != null && !oldObj.IsErased) oldObj.Erase(true);
                                }
                                blk.Origin = Point3d.Origin;
                            }
                            else
                            {
                                blk = new BlockTableRecord();
                                blk.Name = blockName;
                                blk.Origin = Point3d.Origin;
                                blkId = bt.Add(blk);
                                tr.AddNewlyCreatedDBObject(blk, true);
                                createdBlockIds.Add(blkId);
                            }

                            int defCloned = CloneEntitiesIntoBlockDefinition(
                                db, tr, templateCloneIds, blk, blkId,
                                new Point3d(templateOrigin.X, templateOrigin.Y, 0.0), colorByBlock);
                            if (defCloned == 0)
                            {
                                tr.Commit();
                                return Err("Failed to clone template entities into block '" + blockName + "'.");
                            }
                        }

                        for (int ci = 0; ci < confirmedClusters.Count; ci++)
                        {
                            MatchedClusterInstance inst = confirmedClusters[ci];
                            if (inst.MatchMode == "fuzzy") fuzzyMatchesCount++;
                            else if (inst.MatchMode == "normalized") normalizedMatchesCount++;
                            else if (inst.MatchMode == "scaled") scaledMatchesCount++;
                            else exactMatchesCount++;

                            totalMatchedEntities += inst.MatchedIds.Count;
                            string targetLyr = !string.IsNullOrEmpty(insertLayerArg)
                                ? EnsureLayerSafe(db, tr, insertLayerArg, null, null)
                                : EnsureLayerSafe(db, tr, string.IsNullOrEmpty(inst.DominantLayer) ? "0" : inst.DominantLayer, null, null);

                            BlockReference br = new BlockReference(new Point3d(inst.InsertPosition.X, inst.InsertPosition.Y, 0.0), blkId);
                            br.Layer = targetLyr;
                            double instScale = (Math.Abs(inst.Scale - 1.0) <= 1e-4) ? 1.0 : inst.Scale;
                            if (inst.MirroredY || Math.Abs(instScale - 1.0) > 1e-4)
                            {
                                br.ScaleFactors = new Scale3d(
                                    instScale,
                                    inst.MirroredY ? -instScale : instScale,
                                    instScale);
                            }
                            br.Rotation = inst.RotationRad;

                            ObjectId brId = ms.AppendEntity(br);
                            tr.AddNewlyCreatedDBObject(br, true);
                            createdEntityIds.Add(brId);
                            insertedHandles.Add(br.Handle.ToString());

                            try
                            {
                                Extents3d bex = br.GeometricExtents;
                                if (IsFiniteBounds(bex))
                                {
                                    if (!convertedExt.HasValue) convertedExt = bex;
                                    else { Extents3d cur = convertedExt.Value; cur.AddExtents(bex); convertedExt = cur; }
                                }
                            }
                            catch { }

                            if (eraseOriginals)
                            {
                                foreach (ObjectId oid in inst.MatchedIds)
                                {
                                    Entity orig = tr.GetObject(oid, OpenMode.ForWrite, false, true) as Entity;
                                    if (orig != null && !orig.IsErased)
                                    {
                                        orig.Erase(true);
                                        erasedEntityIds.Add(oid);
                                        erasedCount++;
                                    }
                                }
                            }

                            double rotDeg = NormalizeDegrees(inst.RotationRad * 180.0 / Math.PI);
                            RecordUniqueAngle(rotationsDegFound, rotDeg);
                            RecordUniqueAngle(scalesFound, Math.Round(instScale, 3));
                            if (instanceSummaries.Count < 60)
                            {
                                instanceSummaries.Add(new Dictionary<string, object>
                                {
                                    { "index", ci + 1 },
                                    { "inserted_handle", br.Handle.ToString() },
                                    { "position", Pt2(inst.InsertPosition.X, inst.InsertPosition.Y) },
                                    { "rotation_deg", Round(rotDeg) },
                                    { "scale", Round(instScale) },
                                    { "mirrored", inst.MirroredY },
                                    { "match_mode", inst.MatchMode },
                                    { "match_ratio", Round(inst.MatchRatio) },
                                    { "layer", targetLyr },
                                    { "replaced_entities", inst.MatchedIds.Count }
                                });
                            }
                        }

                        db.UpdateExt(true);
                        tr.Commit();

                        if (createdEntityIds.Count > 0 || erasedEntityIds.Count > 0 || createdBlockIds.Count > 0)
                        {
                            CadUndoEntry undoEntry = new CadUndoEntry();
                            undoEntry.ToolName = "convert_matching_shapes_to_block";
                            undoEntry.Summary = "Converted " + confirmedClusters.Count.ToString(CultureInfo.InvariantCulture) +
                                " matching shape(s) (" + erasedCount.ToString(CultureInfo.InvariantCulture) + " loose entities) into block '" + blockName + "'";
                            undoEntry.CreatedIds.AddRange(createdEntityIds);
                            undoEntry.ErasedIds.AddRange(erasedEntityIds);
                            undoEntry.CreatedBlockRecordIds.AddRange(createdBlockIds);
                            PushUndoEntry(doc, undoEntry);
                        }

                        if (zoom)
                        {
                            if (convertedExt.HasValue && IsFiniteBounds(convertedExt.Value))
                                ZoomToExtents3d(doc, convertedExt.Value, 1.25);
                            else
                                DrawingExecutionCommands.ZoomExtents(doc);
                        }
                        else
                        {
                            doc.Editor.Regen();
                        }

                        if (selectConverted && createdEntityIds.Count > 0)
                        {
                            try { Application.SetSystemVariable("PICKFIRST", 1); } catch { }
                            try { doc.Editor.SetImpliedSelection(createdEntityIds.ToArray()); } catch { }
                        }
                        doc.Editor.UpdateScreen();
                    }
                }
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "block_name", blockName ?? "" },
                { "dry_run", dryRun },
                { "template_entity_count", templateEntityCount },
                { "template_origin", Pt2(0, 0) },
                { "matched_instances", confirmedClusters.Count },
                { "exact_matches_count", exactMatchesCount },
                { "scaled_matches_count", scaledMatchesCount },
                { "normalized_matches_count", normalizedMatchesCount },
                { "fuzzy_matches_count", fuzzyMatchesCount },
                { "converted_count", dryRun ? 0 : insertedHandles.Count },
                { "erased_originals_count", dryRun ? 0 : erasedCount },
                { "total_matched_entities", totalMatchedEntities },
                { "tolerance_used", Round(usedTol) },
                { "tolerance_ratio_used", Round(tolRatio) },
                { "size_tolerance_ratio_used", Round(sizeTolRatio) },
                { "allow_rotation", allowRotation },
                { "allow_scale", allowScaleResolved },
                { "min_scale", Round(minScale) },
                { "max_scale", Round(maxScale) },
                { "normalize_geometry", normalizeGeometry && !strictTypes },
                { "fuzzy_match", fuzzyResolved },
                { "min_match_ratio", Round(minMatchRatio) },
                { "allow_mirror", allowMirrorResolved },
                { "rotations_deg_found", rotationsDegFound },
                { "scales_found", scalesFound },
                { "inserted_handles", insertedHandles.Count > MaxReturnedHandles ? insertedHandles.GetRange(0, MaxReturnedHandles) : insertedHandles },
                { "instances", instanceSummaries },
                { "switched_to_model", switchedToModel },
                { "undo_history_steps", GetUndoHistoryCount(doc) }
            };
            if (convertedExt.HasValue && IsFiniteBounds(convertedExt.Value))
            {
                res["converted_bounds"] = new double[]
                {
                    Round(convertedExt.Value.MinPoint.X), Round(convertedExt.Value.MinPoint.Y),
                    Round(convertedExt.Value.MaxPoint.X), Round(convertedExt.Value.MaxPoint.Y)
                };
            }
            if (templateIncludedText && !ignoreText && confirmedClusters.Count <= 1)
            {
                res["note"] = "Template contained text entities (DBText/MText), which prevented matching shapes with different tag labels. Retry with 'ignore_text': true to match purely by CAD geometry.";
            }
            return Ok(res);
        }

        private static GeomCat? QuickCategorizeEntity(Entity ent)
        {
            if (ent is Line || ent is Polyline || ent is Polyline2d) return GeomCat.CurvePoly;
            if (ent is Circle) return GeomCat.Circle;
            if (ent is Arc) return GeomCat.Arc;
            if (ent is Ellipse) return GeomCat.Ellipse;
            if (ent is Solid) return GeomCat.Solid;
            if (ent is Hatch) return GeomCat.Hatch;
            if (ent is Spline) return GeomCat.Spline;
            if (ent is BlockReference) return GeomCat.BlockRef;
            if (ent is DBText || ent is MText) return GeomCat.Text;
            return null;
        }

        private static GeomShape TryBuildGeomShape(Transaction tr, Entity ent, HashSet<string> selectedSet, HashSet<string> lockedLayers)
        {
            if (ent == null) return null;
            string hStr = ent.Handle.ToString();
            string lyr = ent.Layer ?? "0";
            GeomShape gs = new GeomShape();
            gs.Id = ent.ObjectId;
            gs.Handle = hStr;
            gs.Layer = lyr;
            gs.ClassName = ent.GetType().Name;
            gs.IsSelected = selectedSet != null && selectedSet.Contains(hStr);
            gs.OnLockedLayer = lockedLayers != null && lockedLayers.Contains(lyr);

            try
            {
                if (ent is Line)
                {
                    Line ln = (Line)ent;
                    gs.Category = GeomCat.CurvePoly;
                    Point2d p0 = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    Point2d p1 = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    gs.Pts = new Point2d[] { p0, p1 };
                    gs.Bulges = new double[] { 0.0, 0.0 };
                    gs.Closed = false;
                    gs.IsLineOrTwoPtPoly = true;
                    gs.HasSignificantBulge = false;
                    double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
                    gs.TotalLength = Math.Sqrt(dx * dx + dy * dy);
                    gs.Centroid = new Point2d((p0.X + p1.X) * 0.5, (p0.Y + p1.Y) * 0.5);
                    gs.FeatureSpan = gs.TotalLength;
                    gs.LongestP0 = p0;
                    gs.LongestP1 = p1;
                    gs.LongestSpanLen = gs.TotalLength;
                    return gs;
                }
                if (ent is Polyline)
                {
                    Polyline pl = (Polyline)ent;
                    int nv = pl.NumberOfVertices;
                    if (nv < 2) return null;
                    List<Point2d> rawPts = new List<Point2d>(nv);
                    List<double> rawBulges = new List<double>(nv);
                    for (int i = 0; i < nv; i++)
                    {
                        rawPts.Add(pl.GetPoint2dAt(i));
                        rawBulges.Add(pl.GetBulgeAt(i));
                    }
                    double lenHint = 0.0;
                    try { lenHint = pl.Length; } catch { }
                    return FinalizePolyShape(gs, rawPts, rawBulges, pl.Closed, lenHint);
                }
                if (ent is Polyline2d)
                {
                    Polyline2d pl2d = (Polyline2d)ent;
                    List<Point2d> rawPts = new List<Point2d>();
                    List<double> rawBulges = new List<double>();
                    foreach (ObjectId vId in pl2d)
                    {
                        Vertex2d v = tr.GetObject(vId, OpenMode.ForRead) as Vertex2d;
                        if (v == null || v.VertexType == Vertex2dType.SplineControlVertex) continue;
                        rawPts.Add(new Point2d(v.Position.X, v.Position.Y));
                        rawBulges.Add(v.Bulge);
                    }
                    if (rawPts.Count < 2) return null;
                    double lenHint = 0.0;
                    try { lenHint = pl2d.Length; } catch { }
                    return FinalizePolyShape(gs, rawPts, rawBulges, pl2d.Closed, lenHint);
                }
                if (ent is Circle)
                {
                    Circle c = (Circle)ent;
                    gs.Category = GeomCat.Circle;
                    gs.Center = new Point2d(c.Center.X, c.Center.Y);
                    gs.Radius = c.Radius;
                    gs.Centroid = gs.Center;
                    gs.TotalLength = 2.0 * Math.PI * c.Radius;
                    gs.Area = Math.PI * c.Radius * c.Radius;
                    gs.FeatureSpan = 2.0 * c.Radius;
                    return gs;
                }
                if (ent is Arc)
                {
                    Arc a = (Arc)ent;
                    gs.Category = GeomCat.Arc;
                    gs.Center = new Point2d(a.Center.X, a.Center.Y);
                    gs.Radius = a.Radius;
                    gs.SweepAngle = a.TotalAngle;
                    gs.TotalLength = a.Length;
                    gs.StartPt = new Point2d(a.StartPoint.X, a.StartPoint.Y);
                    gs.EndPt = new Point2d(a.EndPoint.X, a.EndPoint.Y);
                    Point3d mp;
                    try { mp = a.GetPointAtDist(a.Length * 0.5); }
                    catch
                    {
                        double midA = a.StartAngle + a.TotalAngle * 0.5;
                        mp = new Point3d(a.Center.X + a.Radius * Math.Cos(midA), a.Center.Y + a.Radius * Math.Sin(midA), 0.0);
                    }
                    gs.MidPt = new Point2d(mp.X, mp.Y);
                    gs.Centroid = new Point2d((gs.StartPt.X + gs.MidPt.X + gs.EndPt.X) / 3.0, (gs.StartPt.Y + gs.MidPt.Y + gs.EndPt.Y) / 3.0);
                    gs.FeatureSpan = 2.0 * a.Radius;
                    gs.LongestP0 = gs.StartPt;
                    gs.LongestP1 = gs.EndPt;
                    gs.LongestSpanLen = Dist2D(gs.StartPt, gs.EndPt);
                    return gs;
                }
                if (ent is Ellipse)
                {
                    Ellipse el = (Ellipse)ent;
                    gs.Category = GeomCat.Ellipse;
                    gs.Center = new Point2d(el.Center.X, el.Center.Y);
                    gs.Radius = el.MajorRadius;
                    gs.MinorRadius = el.MinorRadius;
                    gs.Closed = el.Closed;
                    Vector3d maj = el.MajorAxis;
                    double ml = Math.Sqrt(maj.X * maj.X + maj.Y * maj.Y);
                    gs.MajorDir = ml > 1e-9 ? new Point2d(maj.X / ml, maj.Y / ml) : new Point2d(1.0, 0.0);
                    double len = 0.0;
                    try { len = el.GetDistanceAtParameter(el.EndParam); } catch { len = Math.PI * (el.MajorRadius + el.MinorRadius); }
                    gs.TotalLength = len;
                    gs.StartPt = el.Closed ? gs.Center : new Point2d(el.StartPoint.X, el.StartPoint.Y);
                    gs.EndPt = el.Closed ? gs.Center : new Point2d(el.EndPoint.X, el.EndPoint.Y);
                    if (!el.Closed && len > 1e-6)
                    {
                        try { Point3d mp = el.GetPointAtDist(len * 0.5); gs.MidPt = new Point2d(mp.X, mp.Y); }
                        catch { gs.MidPt = gs.Center; }
                        gs.Centroid = new Point2d((gs.StartPt.X + gs.MidPt.X + gs.EndPt.X) / 3.0, (gs.StartPt.Y + gs.MidPt.Y + gs.EndPt.Y) / 3.0);
                    }
                    else
                    {
                        gs.MidPt = gs.Center;
                        gs.Centroid = gs.Center;
                    }
                    gs.FeatureSpan = 2.0 * el.MajorRadius;
                    gs.LongestP0 = gs.Center;
                    gs.LongestP1 = new Point2d(gs.Center.X + gs.MajorDir.X * el.MajorRadius, gs.Center.Y + gs.MajorDir.Y * el.MajorRadius);
                    gs.LongestSpanLen = el.MajorRadius;
                    return gs;
                }
                if (ent is Solid)
                {
                    Solid sd = (Solid)ent;
                    gs.Category = GeomCat.Solid;
                    Point3d p0 = sd.GetPointAt(0);
                    Point3d p1 = sd.GetPointAt(1);
                    Point3d p2 = sd.GetPointAt(2);
                    Point3d p3 = sd.GetPointAt(3);
                    Point2d[] ordered = new Point2d[]
                    {
                        new Point2d(p0.X, p0.Y),
                        new Point2d(p1.X, p1.Y),
                        new Point2d(p3.X, p3.Y),
                        new Point2d(p2.X, p2.Y)
                    };
                    List<Point2d> uniq = new List<Point2d>(4);
                    for (int i = 0; i < 4; i++)
                    {
                        if (uniq.Count == 0 || Dist2D(uniq[uniq.Count - 1], ordered[i]) > 1e-6)
                            uniq.Add(ordered[i]);
                    }
                    if (uniq.Count > 2 && Dist2D(uniq[0], uniq[uniq.Count - 1]) <= 1e-6)
                        uniq.RemoveAt(uniq.Count - 1);
                    if (uniq.Count < 3) return null;
                    gs.Pts = uniq.ToArray();
                    gs.Closed = true;
                    double perim = 0.0;
                    double cx = 0.0, cy = 0.0;
                    for (int i = 0; i < gs.Pts.Length; i++)
                    {
                        Point2d a = gs.Pts[i];
                        Point2d b = gs.Pts[(i + 1) % gs.Pts.Length];
                        perim += Dist2D(a, b);
                        cx += a.X;
                        cy += a.Y;
                    }
                    gs.TotalLength = perim;
                    gs.Centroid = new Point2d(cx / gs.Pts.Length, cy / gs.Pts.Length);
                    ComputeLongestPointSpan(gs);
                    gs.FeatureSpan = Math.Max(1e-4, gs.LongestSpanLen);
                    return gs;
                }
                if (ent is Spline)
                {
                    Spline sp = (Spline)ent;
                    gs.Category = GeomCat.Spline;
                    gs.Closed = sp.Closed;
                    double len = 0.0;
                    try { len = sp.GetDistanceAtParameter(sp.EndParam); } catch { }
                    List<Point2d> samples = new List<Point2d>();
                    if (len > 1e-6)
                    {
                        int nSamples = 10;
                        for (int i = 0; i < nSamples; i++)
                        {
                            double d = (len * i) / (double)(sp.Closed ? nSamples : (nSamples - 1));
                            try
                            {
                                Point3d p = sp.GetPointAtDist(Math.Min(len, Math.Max(0.0, d)));
                                samples.Add(new Point2d(p.X, p.Y));
                            }
                            catch { }
                        }
                    }
                    if (samples.Count < 2)
                    {
                        for (int i = 0; i < sp.NumControlPoints; i++)
                        {
                            Point3d cp = sp.GetControlPointAt(i);
                            samples.Add(new Point2d(cp.X, cp.Y));
                        }
                    }
                    if (samples.Count < 2) return null;
                    gs.Pts = samples.ToArray();
                    gs.TotalLength = len > 1e-6 ? len : ComputeOpenPolylineLength(gs.Pts, sp.Closed);
                    gs.Centroid = ComputeWeightedCentroid(gs.Pts, sp.Closed);
                    ComputeLongestPointSpan(gs);
                    gs.FeatureSpan = Math.Max(1e-4, gs.LongestSpanLen);
                    return gs;
                }
                if (ent is Hatch)
                {
                    Hatch ht = (Hatch)ent;
                    gs.Category = GeomCat.Hatch;
                    gs.TextOrPattern = (ht.PatternName ?? "").ToUpperInvariant();
                    try { gs.Area = Math.Abs(ht.Area); } catch { }
                    List<Point2d> loopSamples = new List<Point2d>();
                    try
                    {
                        int nLoops = Math.Min(4, ht.NumberOfLoops);
                        for (int li = 0; li < nLoops; li++)
                        {
                            HatchLoop lp = ht.GetLoopAt(li);
                            if (lp.IsPolyline && lp.Polyline != null)
                            {
                                int step = Math.Max(1, lp.Polyline.Count / 24);
                                for (int vi = 0; vi < lp.Polyline.Count; vi += step)
                                {
                                    loopSamples.Add(lp.Polyline[vi].Vertex);
                                    if (loopSamples.Count >= 48) break;
                                }
                            }
                            else if (lp.Curves != null)
                            {
                                int step = Math.Max(1, lp.Curves.Count / 24);
                                for (int ci = 0; ci < lp.Curves.Count; ci += step)
                                {
                                    loopSamples.Add(lp.Curves[ci].StartPoint);
                                    if (loopSamples.Count >= 48) break;
                                }
                            }
                            if (loopSamples.Count >= 48) break;
                        }
                    }
                    catch { }
                    Extents3d hex = ht.GeometricExtents;
                    if (!IsFiniteBounds(hex)) return null;
                    if (loopSamples.Count == 0)
                    {
                        loopSamples.Add(new Point2d(hex.MinPoint.X, hex.MinPoint.Y));
                        loopSamples.Add(new Point2d(hex.MaxPoint.X, hex.MinPoint.Y));
                        loopSamples.Add(new Point2d(hex.MaxPoint.X, hex.MaxPoint.Y));
                        loopSamples.Add(new Point2d(hex.MinPoint.X, hex.MaxPoint.Y));
                    }
                    gs.Pts = loopSamples.ToArray();
                    gs.Closed = true;
                    gs.TotalLength = ComputeOpenPolylineLength(gs.Pts, true);
                    gs.Centroid = ComputeWeightedCentroid(gs.Pts, true);
                    ComputeLongestPointSpan(gs);
                    gs.FeatureSpan = Math.Max(1e-4, gs.LongestSpanLen);
                    return gs;
                }
                if (ent is BlockReference)
                {
                    BlockReference br = (BlockReference)ent;
                    gs.Category = GeomCat.BlockRef;
                    gs.RefBlockDefId = GetBlockDefinitionId(br);
                    gs.TextOrPattern = ResolveBlockName(tr, br);
                    gs.Center = new Point2d(br.Position.X, br.Position.Y);
                    gs.RefRotation = br.Rotation;
                    gs.RefScaleX = br.ScaleFactors.X;
                    gs.RefScaleY = br.ScaleFactors.Y;
                    gs.MajorDir = new Point2d(Math.Cos(br.Rotation), Math.Sin(br.Rotation));
                    // Use insertion origin for Centroid so 0..360 deg rotation matches exact transform
                    gs.Centroid = gs.Center;
                    double avgScale = Math.Max(1e-3, (Math.Abs(br.ScaleFactors.X) + Math.Abs(br.ScaleFactors.Y)) * 0.5);
                    gs.FeatureSpan = avgScale;
                    try
                    {
                        Extents3d bex = br.GeometricExtents;
                        if (IsFiniteBounds(bex))
                        {
                            double bw = bex.MaxPoint.X - bex.MinPoint.X;
                            double bh = bex.MaxPoint.Y - bex.MinPoint.Y;
                            gs.FeatureSpan = Math.Max(1e-4, Math.Sqrt(bw * bw + bh * bh));
                        }
                    }
                    catch { }
                    gs.TotalLength = gs.FeatureSpan;
                    gs.LongestP0 = gs.Center;
                    gs.LongestP1 = new Point2d(gs.Center.X + gs.MajorDir.X, gs.Center.Y + gs.MajorDir.Y);
                    gs.LongestSpanLen = 1.0;
                    return gs;
                }
                if (ent is DBText)
                {
                    DBText dt = (DBText)ent;
                    gs.Category = GeomCat.Text;
                    gs.TextOrPattern = (dt.TextString ?? "").Trim();
                    gs.TextHeight = dt.Height;
                    gs.RefRotation = dt.Rotation;
                    gs.MajorDir = new Point2d(Math.Cos(dt.Rotation), Math.Sin(dt.Rotation));
                    Point2d pos = new Point2d(dt.Position.X, dt.Position.Y);
                    gs.Center = pos;
                    gs.Centroid = pos;
                    gs.FeatureSpan = Math.Max(1e-4, dt.Height);
                    try
                    {
                        Extents3d tex = dt.GeometricExtents;
                        if (IsFiniteBounds(tex))
                        {
                            double tw = tex.MaxPoint.X - tex.MinPoint.X;
                            double th = tex.MaxPoint.Y - tex.MinPoint.Y;
                            gs.FeatureSpan = Math.Max(gs.FeatureSpan, Math.Sqrt(tw * tw + th * th));
                        }
                    }
                    catch { }
                    gs.TotalLength = dt.Height;
                    return gs;
                }
                if (ent is MText)
                {
                    MText mt = (MText)ent;
                    gs.Category = GeomCat.Text;
                    gs.TextOrPattern = (mt.Text ?? "").Trim();
                    gs.TextHeight = mt.TextHeight;
                    gs.RefRotation = mt.Rotation;
                    gs.MajorDir = new Point2d(Math.Cos(mt.Rotation), Math.Sin(mt.Rotation));
                    Point2d pos = new Point2d(mt.Location.X, mt.Location.Y);
                    gs.Center = pos;
                    gs.Centroid = pos;
                    gs.FeatureSpan = Math.Max(1e-4, mt.TextHeight);
                    try
                    {
                        Extents3d tex = mt.GeometricExtents;
                        if (IsFiniteBounds(tex))
                        {
                            double tw = tex.MaxPoint.X - tex.MinPoint.X;
                            double th = tex.MaxPoint.Y - tex.MinPoint.Y;
                            gs.FeatureSpan = Math.Max(gs.FeatureSpan, Math.Sqrt(tw * tw + th * th));
                        }
                    }
                    catch { }
                    gs.TotalLength = mt.TextHeight;
                    return gs;
                }
            }
            catch { }
            return null;
        }

        private static GeomShape FinalizePolyShape(
            GeomShape gs, List<Point2d> rawPts, List<double> rawBulges, bool isClosed, double lenHint)
        {
            gs.Category = GeomCat.CurvePoly;
            List<Point2d> pts = new List<Point2d>(rawPts.Count);
            List<double> bulges = new List<double>(rawBulges.Count);

            for (int i = 0; i < rawPts.Count; i++)
            {
                Point2d p = rawPts[i];
                double b = rawBulges[i];
                if (pts.Count > 0 && Dist2D(pts[pts.Count - 1], p) < 1e-6 && Math.Abs(bulges[bulges.Count - 1]) < 1e-5)
                {
                    bulges[bulges.Count - 1] = b;
                    continue;
                }
                pts.Add(p);
                bulges.Add(b);
            }

            if (!isClosed && pts.Count >= 3 && Dist2D(pts[0], pts[pts.Count - 1]) < 1e-5 && Math.Abs(bulges[pts.Count - 1]) < 1e-5)
            {
                isClosed = true;
                pts.RemoveAt(pts.Count - 1);
                bulges.RemoveAt(bulges.Count - 1);
            }

            if (pts.Count < 2) return null;

            // Remove strictly collinear straight interior vertices (fixes PDF-split straight segments)
            if (pts.Count >= 3)
            {
                bool changed = true;
                int guard = 0;
                while (changed && pts.Count >= 3 && guard++ < 64)
                {
                    changed = false;
                    int n = pts.Count;
                    for (int i = 0; i < n; i++)
                    {
                        if (!isClosed && (i == 0 || i == n - 1)) continue;
                        int prev = (i - 1 + n) % n;
                        int next = (i + 1) % n;
                        if (Math.Abs(bulges[prev]) >= 1e-5 || Math.Abs(bulges[i]) >= 1e-5) continue;
                        Point2d a = pts[prev], c = pts[i], d = pts[next];
                        double v1x = c.X - a.X, v1y = c.Y - a.Y;
                        double v2x = d.X - c.X, v2y = d.Y - c.Y;
                        double l1 = Math.Sqrt(v1x * v1x + v1y * v1y);
                        double l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
                        if (l1 < 1e-6 || l2 < 1e-6) continue;
                        double dot = (v1x * v2x + v1y * v2y) / (l1 * l2);
                        if (dot > 0.999999)
                        {
                            double crossPerp = Math.Abs((c.X - a.X) * (d.Y - a.Y) - (c.Y - a.Y) * (d.X - a.X)) / Math.Max(1e-6, Dist2D(a, d));
                            if (crossPerp < 1e-5)
                            {
                                pts.RemoveAt(i);
                                bulges.RemoveAt(i);
                                changed = true;
                                break;
                            }
                        }
                    }
                }
            }

            bool hasBulge = false;
            for (int i = 0; i < bulges.Count; i++)
            {
                int segNext = i + 1;
                if (segNext >= pts.Count && !isClosed) continue;
                if (Math.Abs(bulges[i]) > 1e-4) { hasBulge = true; break; }
            }

            gs.Pts = pts.ToArray();
            gs.Bulges = bulges.ToArray();
            gs.Closed = isClosed;
            gs.HasSignificantBulge = hasBulge;
            gs.IsLineOrTwoPtPoly = (gs.Pts.Length == 2 && !isClosed && !hasBulge);
            double chordLen = ComputeOpenPolylineLength(gs.Pts, isClosed);
            gs.TotalLength = (lenHint > 1e-6 && lenHint >= chordLen * 0.99) ? lenHint : chordLen;
            gs.Centroid = ComputeWeightedCentroid(gs.Pts, isClosed);
            ComputeLongestPointSpan(gs);
            double minX = gs.Pts[0].X, maxX = gs.Pts[0].X, minY = gs.Pts[0].Y, maxY = gs.Pts[0].Y;
            for (int i = 1; i < gs.Pts.Length; i++)
            {
                if (gs.Pts[i].X < minX) minX = gs.Pts[i].X;
                if (gs.Pts[i].X > maxX) maxX = gs.Pts[i].X;
                if (gs.Pts[i].Y < minY) minY = gs.Pts[i].Y;
                if (gs.Pts[i].Y > maxY) maxY = gs.Pts[i].Y;
            }
            gs.FeatureSpan = Math.Max(1e-4, Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY)));
            return gs;
        }

        private static List<MatchedClusterInstance> FindMatchingClusters(
            List<GeomShape> template,
            Point2d templateOrigin,
            double spanT,
            List<GeomShape> pool,
            MatchSearchOptions opts)
        {
            List<MatchedClusterInstance> results = new List<MatchedClusterInstance>();
            if (template.Count == 0 || pool.Count == 0) return results;

            bool[] poolConsumed = new bool[pool.Count];

            // Pass 0: Guarantee user's exact selected cluster is captured first at 1:1
            bool anySelected = false;
            for (int i = 0; i < template.Count; i++) if (template[i].IsSelected) { anySelected = true; break; }
            if (anySelected)
            {
                List<int> selOrder = BuildRarityOrder(template, pool, opts, 1.0 - opts.SizeTolRatio, 1.0 + opts.SizeTolRatio);
                Dictionary<GeomCat, Dictionary<long, List<int>>> selfGrids = BuildFineGrids(pool, Math.Max(opts.PosTol * 2.5, Math.Min(spanT * 0.08, Math.Max(opts.PosTol * 10.0, 1.0))));
                double selfCell = Math.Max(opts.PosTol * 2.5, Math.Min(spanT * 0.08, Math.Max(opts.PosTol * 10.0, 1.0)));
                SimilarityMatch2D identityM = new SimilarityMatch2D
                {
                    TemplateOrigin = templateOrigin,
                    TargetOrigin = templateOrigin,
                    Scale = 1.0,
                    CosA = 1.0,
                    SinA = 0.0,
                    RotationRad = 0.0,
                    MirroredY = false
                };
                MatchedClusterInstance selfInst = TryVerifyCluster(
                    template, selOrder, identityM, pool, poolConsumed, selfGrids, selfCell,
                    opts, 1.0, true, false, null, null, null, null);
                if (selfInst != null)
                {
                    selfInst.MatchMode = "exact";
                    ConfirmRealCluster(selfInst, poolConsumed, results);
                }
            }

            double nominalMinS = Math.Max(0.5, 1.0 - opts.SizeTolRatio);
            double nominalMaxS = 1.0 + opts.SizeTolRatio;

            // Pass 1: Entity-level exact 100% at nominal 1:1 scale (0..360 deg + mirror)
            RunClusterPass(
                template, templateOrigin, spanT, pool, poolConsumed, results, opts,
                nominalMinS, nominalMaxS, 1.0, false, false, "exact");

            // Pass 2: Entity-level exact 100% across different sizes (Scale minScale..maxScale)
            if (opts.AllowScale && (opts.MinScale < nominalMinS - 1e-4 || opts.MaxScale > nominalMaxS + 1e-4))
            {
                RunClusterPass(
                    template, templateOrigin, spanT, pool, poolConsumed, results, opts,
                    opts.MinScale, opts.MaxScale, 1.0, false, false, "scaled");
            }

            // Pass 3: Normalized Geometry Pass (decomposes Polylines -> segments, merges 2-Arc Circles)
            if (opts.NormalizeGeometry)
            {
                List<GeomShape> normTemplate = BuildNormalizedTemplate(template);
                int maxSegsPerPoly = Math.Max(normTemplate.Count * 2 + 6, 24);
                bool[] parentDecomposed;
                List<GeomShape> normPool = BuildNormalizedPool(pool, poolConsumed, opts.PosTol, maxSegsPerPoly, out parentDecomposed);
                if (normTemplate.Count > 0 && normPool.Count >= normTemplate.Count)
                {
                    bool[] normConsumed = new bool[normPool.Count];
                    Point2d normOrigin = templateOrigin;
                    RunClusterPassNormalized(
                        normTemplate, normOrigin, spanT, normPool, normConsumed, pool, poolConsumed, parentDecomposed,
                        results, opts, nominalMinS, nominalMaxS, 1.0, false, "normalized");

                    if (opts.AllowScale && (opts.MinScale < nominalMinS - 1e-4 || opts.MaxScale > nominalMaxS + 1e-4))
                    {
                        RunClusterPassNormalized(
                            normTemplate, normOrigin, spanT, normPool, normConsumed, pool, poolConsumed, parentDecomposed,
                            results, opts, opts.MinScale, opts.MaxScale, 1.0, false, "normalized");
                    }
                }
            }

            // Pass 4: Fuzzy / Minor-Difference Pass (handles shapes missing 1-2 minor details)
            if (opts.AllowFuzzy && template.Count >= 4 && opts.MinMatchRatio < 0.999)
            {
                double passMinS = opts.AllowScale ? opts.MinScale : nominalMinS;
                double passMaxS = opts.AllowScale ? opts.MaxScale : nominalMaxS;
                RunClusterPass(
                    template, templateOrigin, spanT, pool, poolConsumed, results, opts,
                    passMinS, passMaxS, opts.MinMatchRatio, true, true, "fuzzy");
            }

            return results;
        }

        private static void RunClusterPass(
            List<GeomShape> template,
            Point2d templateOrigin,
            double spanT,
            List<GeomShape> pool,
            bool[] poolConsumed,
            List<MatchedClusterInstance> results,
            MatchSearchOptions opts,
            double sMin,
            double sMax,
            double requiredRatio,
            bool isFuzzyPass,
            bool multiSeedFallback,
            string modeLabel)
        {
            bool[] dummyConsumed = new bool[pool.Count];
            Array.Copy(poolConsumed, dummyConsumed, pool.Count);
            RunClusterPassCore(
                template, templateOrigin, spanT,
                pool, dummyConsumed,
                pool, poolConsumed, null,
                results, opts, sMin, sMax, requiredRatio, isFuzzyPass, multiSeedFallback, modeLabel, false);
        }

        private static void RunClusterPassNormalized(
            List<GeomShape> normTemplate,
            Point2d templateOrigin,
            double spanT,
            List<GeomShape> normPool,
            bool[] normConsumed,
            List<GeomShape> realPool,
            bool[] realConsumed,
            bool[] parentDecomposed,
            List<MatchedClusterInstance> results,
            MatchSearchOptions opts,
            double sMin,
            double sMax,
            double requiredRatio,
            bool isFuzzyPass,
            string modeLabel)
        {
            RunClusterPassCore(
                normTemplate, templateOrigin, spanT,
                normPool, normConsumed,
                realPool, realConsumed, parentDecomposed,
                results, opts, sMin, sMax, requiredRatio, isFuzzyPass, false, modeLabel, true);
        }

        private static void RunClusterPassCore(
            List<GeomShape> template,
            Point2d templateOrigin,
            double spanT,
            List<GeomShape> workPool,
            bool[] workConsumed,
            List<GeomShape> realPool,
            bool[] realConsumed,
            bool[] parentDecomposed,
            List<MatchedClusterInstance> results,
            MatchSearchOptions opts,
            double sMin,
            double sMax,
            double requiredRatio,
            bool isFuzzyPass,
            bool multiSeedFallback,
            string modeLabel,
            bool isNormalized)
        {
            if (template.Count == 0 || workPool.Count == 0) return;

            List<int> rarityOrder = BuildRarityOrder(template, workPool, opts, sMin, sMax);
            double fineCell = Math.Max(opts.PosTol * 3.0, Math.Min(spanT * Math.Max(1.0, sMax) * 0.12, Math.Max(opts.PosTol * 12.0, 1.0)));
            Dictionary<GeomCat, Dictionary<long, List<int>>> fineGrids = BuildFineGrids(workPool, fineCell);

            double angleTolRad = 3.0 * Math.PI / 180.0;
            int seedLimit = multiSeedFallback ? Math.Min(3, rarityOrder.Count) : 1;
            int pairChecks = 0;
            const int MaxPairChecks = 250000;

            for (int seedRank = 0; seedRank < seedLimit && pairChecks < MaxPairChecks; seedRank++)
            {
                int seedTplIdx = rarityOrder[seedRank];
                GeomShape tSeed = template[seedTplIdx];
                List<int> seedCandidates = FindAllLocalCandidatesFor(tSeed, workPool, workConsumed, opts, sMin, sMax);
                if (seedCandidates.Count == 0) continue;

                seedCandidates.Sort(delegate(int a, int b)
                {
                    int selCmp = workPool[b].IsSelected.CompareTo(workPool[a].IsSelected);
                    if (selCmp != 0) return selCmp;
                    return a.CompareTo(b);
                });

                // Collect non-concentric second anchors ordered by distinctiveness (sep / freq)
                double minAnchorSep = Math.Max(opts.PosTol * 2.5, spanT * 0.03);
                List<int> anchor2Candidates = new List<int>();
                List<double> anchor2Scores = new List<double>();
                for (int oi = 0; oi < rarityOrder.Count; oi++)
                {
                    int ti = rarityOrder[oi];
                    if (ti == seedTplIdx) continue;
                    GeomShape cand2 = template[ti];
                    double sep = Dist2D(tSeed.Centroid, cand2.Centroid);
                    if (sep < minAnchorSep) continue;
                    double score = sep / Math.Max(1.0, cand2.CandidateFreq);
                    anchor2Candidates.Add(ti);
                    anchor2Scores.Add(score);
                }
                if (anchor2Candidates.Count > 1)
                {
                    for (int i = 0; i < anchor2Candidates.Count - 1; i++)
                    {
                        for (int j = i + 1; j < anchor2Candidates.Count; j++)
                        {
                            if (anchor2Scores[j] > anchor2Scores[i])
                            {
                                double ts = anchor2Scores[i]; anchor2Scores[i] = anchor2Scores[j]; anchor2Scores[j] = ts;
                                int ti = anchor2Candidates[i]; anchor2Candidates[i] = anchor2Candidates[j]; anchor2Candidates[j] = ti;
                            }
                        }
                    }
                }

                if (anchor2Candidates.Count > 0)
                {
                    int anchorLimit = multiSeedFallback ? Math.Min(3, anchor2Candidates.Count) : 1;
                    for (int aRank = 0; aRank < anchorLimit && pairChecks < MaxPairChecks; aRank++)
                    {
                        int anchor2TplIdx = anchor2Candidates[aRank];
                        GeomShape tAnchor2 = template[anchor2TplIdx];
                        double d12 = Dist2D(tSeed.Centroid, tAnchor2.Centroid);
                        double baseAlpha = Math.Atan2(tAnchor2.Centroid.Y - tSeed.Centroid.Y, tAnchor2.Centroid.X - tSeed.Centroid.X);
                        double minDist = Math.Max(1e-5, d12 * sMin - opts.PosTol * Math.Max(1.5, sMin * 2.0));
                        double maxDist = d12 * sMax + opts.PosTol * Math.Max(2.0, sMax * 2.0);
                        double anchorCell = Math.Max(maxDist, 1.0);

                        List<int> anchor2Pool = FindAllLocalCandidatesFor(tAnchor2, workPool, workConsumed, opts, sMin, sMax);
                        Dictionary<long, List<int>> anchor2Grid = new Dictionary<long, List<int>>();
                        for (int ai = 0; ai < anchor2Pool.Count; ai++)
                        {
                            int aIdx = anchor2Pool[ai];
                            GeomShape a = workPool[aIdx];
                            long ak = MakeCellKey(a.Centroid.X, a.Centroid.Y, anchorCell);
                            List<int> list;
                            if (!anchor2Grid.TryGetValue(ak, out list))
                            {
                                list = new List<int>();
                                anchor2Grid[ak] = list;
                            }
                            list.Add(aIdx);
                        }

                        int mirrorPasses = opts.AllowMirror ? 2 : 1;
                        for (int mp = 0; mp < mirrorPasses && pairChecks < MaxPairChecks; mp++)
                        {
                            bool mirrored = (mp == 1);
                            for (int si = 0; si < seedCandidates.Count && pairChecks < MaxPairChecks; si++)
                            {
                                int c1Idx = seedCandidates[si];
                                if (workConsumed[c1Idx] || !IsRealParentAvailable(workPool[c1Idx], realConsumed)) continue;
                                GeomShape c1 = workPool[c1Idx];
                                int ix0 = (int)Math.Floor(c1.Centroid.X / anchorCell);
                                int iy0 = (int)Math.Floor(c1.Centroid.Y / anchorCell);

                                bool matchedForC1 = false;
                                for (int ox = -1; ox <= 1 && !matchedForC1 && pairChecks < MaxPairChecks; ox++)
                                {
                                    for (int oy = -1; oy <= 1 && !matchedForC1 && pairChecks < MaxPairChecks; oy++)
                                    {
                                        long ak = PackCellCoords(ix0 + ox, iy0 + oy);
                                        List<int> c2Bucket;
                                        if (!anchor2Grid.TryGetValue(ak, out c2Bucket)) continue;

                                        for (int bi = 0; bi < c2Bucket.Count; bi++)
                                        {
                                            int c2Idx = c2Bucket[bi];
                                            if (c2Idx == c1Idx || workConsumed[c2Idx]) continue;
                                            GeomShape c2 = workPool[c2Idx];
                                            if (!IsRealParentAvailable(c2, realConsumed)) continue;
                                            if (SharesParentPoolEntity(c1, c2) && !isNormalized) continue;

                                            pairChecks++;
                                            if (pairChecks > MaxPairChecks) break;

                                            double dc = Dist2D(c1.Centroid, c2.Centroid);
                                            if (dc < minDist || dc > maxDist) continue;

                                            double candScale = dc / d12;
                                            if (Math.Abs(candScale - 1.0) <= opts.TolRatio * 0.4 && sMin <= 1.0 && sMax >= 1.0)
                                                candScale = 1.0;
                                            if (candScale < sMin || candScale > sMax) continue;

                                            if (!CheckEntityScaleCompatibility(tSeed, c1, candScale, opts)) continue;
                                            if (!CheckEntityScaleCompatibility(tAnchor2, c2, candScale, opts)) continue;

                                            double alphaM = Math.Atan2(c2.Centroid.Y - c1.Centroid.Y, c2.Centroid.X - c1.Centroid.X);
                                            double theta = mirrored ? (alphaM + baseAlpha) : (alphaM - baseAlpha);
                                            theta = NormalizeRadians(theta);
                                            if (!opts.AllowRotation && Math.Abs(theta) > angleTolRad) continue;

                                            SimilarityMatch2D candM = BuildTransformFromPair(
                                                templateOrigin, tSeed.Centroid, c1.Centroid, theta, candScale, mirrored);
                                            MatchedClusterInstance inst = TryVerifyCluster(
                                                template, rarityOrder, candM, workPool, workConsumed, fineGrids, fineCell,
                                                opts, requiredRatio, false, isFuzzyPass,
                                                isNormalized ? realPool : null,
                                                isNormalized ? realConsumed : null,
                                                isNormalized ? parentDecomposed : null,
                                                isNormalized ? workConsumed : null);
                                            if (inst != null)
                                            {
                                                if (inst.MatchMode == "exact" && (modeLabel == "normalized" || modeLabel == "fuzzy" || Math.Abs(candScale - 1.0) > 1e-3))
                                                    inst.MatchMode = (Math.Abs(candScale - 1.0) > 1e-3 && modeLabel == "exact") ? "scaled" : modeLabel;
                                                else
                                                    inst.MatchMode = modeLabel;
                                                ConfirmClusterInPass(inst, workPool, workConsumed, realPool, realConsumed, results, isNormalized);
                                                matchedForC1 = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Single-entity or Concentric cluster matching (supports 0..360 deg + scale)
                    int dirTplIdx = seedTplIdx;
                    for (int i = 0; i < rarityOrder.Count; i++)
                    {
                        int idx = rarityOrder[i];
                        if (HasDirectionAxis(template[idx])) { dirTplIdx = idx; break; }
                    }
                    GeomShape tDir = template[dirTplIdx];
                    List<int> dirCandidates = (dirTplIdx == seedTplIdx)
                        ? seedCandidates
                        : FindAllLocalCandidatesFor(tDir, workPool, workConsumed, opts, sMin, sMax);

                    int mirrorPasses = opts.AllowMirror ? 2 : 1;
                    for (int mp = 0; mp < mirrorPasses; mp++)
                    {
                        bool mirrored = (mp == 1);
                        for (int si = 0; si < seedCandidates.Count; si++)
                        {
                            int c1Idx = seedCandidates[si];
                            if (workConsumed[c1Idx] || !IsRealParentAvailable(workPool[c1Idx], realConsumed)) continue;
                            GeomShape c1 = workPool[c1Idx];

                            double seedScale = EstimateEntityScale(tSeed, c1);
                            if (seedScale <= 0.0) seedScale = 1.0;
                            if (Math.Abs(seedScale - 1.0) <= opts.TolRatio * 0.4 && sMin <= 1.0 && sMax >= 1.0)
                                seedScale = 1.0;
                            if (seedScale < sMin || seedScale > sMax) continue;

                            List<ScaleAngleCandidate> candTransforms = new List<ScaleAngleCandidate>();
                            if (!HasDirectionAxis(tDir))
                            {
                                AddUniqueScaleAngle(candTransforms, seedScale, 0.0);
                            }
                            else if (tDir.Category == GeomCat.CurvePoly && tDir.Pts != null && tDir.Pts.Length >= 2)
                            {
                                double pairTol = Math.Max(opts.PosTol * Math.Max(2.5, seedScale * 3.0), spanT * seedScale * 0.15);
                                for (int dci = 0; dci < dirCandidates.Count; dci++)
                                {
                                    GeomShape cd = workPool[dirCandidates[dci]];
                                    if (workConsumed[cd.Index] || !IsRealParentAvailable(cd, realConsumed)) continue;
                                    if (Dist2D(cd.Centroid, c1.Centroid) > pairTol) continue;
                                    CollectPolyAlignmentAngles(tDir, cd, mirrored, opts.AllowRotation, angleTolRad, opts.PosTol, sMin, sMax, opts.SizeTolRatio, candTransforms);
                                    if (candTransforms.Count >= 32) break;
                                }
                            }
                            else
                            {
                                double pairTol = Math.Max(opts.PosTol * Math.Max(2.5, seedScale * 3.0), spanT * seedScale * 0.15);
                                for (int dci = 0; dci < dirCandidates.Count; dci++)
                                {
                                    GeomShape cd = workPool[dirCandidates[dci]];
                                    if (workConsumed[cd.Index] || !IsRealParentAvailable(cd, realConsumed)) continue;
                                    if (Dist2D(cd.Centroid, c1.Centroid) > pairTol) continue;
                                    double dirScale = EstimateEntityScale(tDir, cd);
                                    double sUse = (dirScale > 0.0) ? dirScale : seedScale;
                                    if (Math.Abs(sUse - 1.0) <= opts.TolRatio * 0.4 && sMin <= 1.0 && sMax >= 1.0)
                                        sUse = 1.0;
                                    if (sUse < sMin || sUse > sMax) continue;
                                    double tA = GetEntityAxisAngle(tDir);
                                    double cA = GetEntityAxisAngle(cd);
                                    double theta = NormalizeRadians(mirrored ? (cA + tA) : (cA - tA));
                                    if (opts.AllowRotation || Math.Abs(theta) <= angleTolRad)
                                        AddUniqueScaleAngle(candTransforms, sUse, theta);
                                    if (tDir.Category == GeomCat.Solid || tDir.Category == GeomCat.Ellipse || tDir.Category == GeomCat.CurvePoly)
                                    {
                                        double thetaFlip = NormalizeRadians(theta + Math.PI);
                                        if (opts.AllowRotation || Math.Abs(thetaFlip) <= angleTolRad)
                                            AddUniqueScaleAngle(candTransforms, sUse, thetaFlip);
                                    }
                                }
                            }

                            for (int ti = 0; ti < candTransforms.Count; ti++)
                            {
                                ScaleAngleCandidate sa = candTransforms[ti];
                                SimilarityMatch2D candM = BuildTransformFromPair(
                                    templateOrigin, tSeed.Centroid, c1.Centroid, sa.Theta, sa.Scale, mirrored);
                                MatchedClusterInstance inst = TryVerifyCluster(
                                    template, rarityOrder, candM, workPool, workConsumed, fineGrids, fineCell,
                                    opts, requiredRatio, false, isFuzzyPass,
                                    isNormalized ? realPool : null,
                                    isNormalized ? realConsumed : null,
                                    isNormalized ? parentDecomposed : null,
                                    isNormalized ? workConsumed : null);
                                if (inst != null)
                                {
                                    inst.MatchMode = (Math.Abs(sa.Scale - 1.0) > 1e-3 && modeLabel == "exact") ? "scaled" : modeLabel;
                                    ConfirmClusterInPass(inst, workPool, workConsumed, realPool, realConsumed, results, isNormalized);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        private static List<int> BuildRarityOrder(
            List<GeomShape> template, List<GeomShape> pool, MatchSearchOptions opts, double sMin, double sMax)
        {
            for (int i = 0; i < template.Count; i++)
            {
                GeomShape t = template[i];
                int cnt = 0;
                double lenLo = t.TotalLength * sMin * (1.0 - opts.SizeTolRatio) - opts.PosTol;
                double lenHi = t.TotalLength * sMax * (1.0 + opts.SizeTolRatio) + opts.PosTol;
                for (int pi = 0; pi < pool.Count; pi++)
                {
                    GeomShape p = pool[pi];
                    if (p.Category != t.Category) continue;
                    if (p.TotalLength >= lenLo && p.TotalLength <= lenHi) cnt++;
                }
                t.CandidateFreq = cnt;
            }

            List<int> rarityOrder = new List<int>(template.Count);
            for (int i = 0; i < template.Count; i++) rarityOrder.Add(i);
            rarityOrder.Sort(delegate(int a, int b)
            {
                int cmp = template[a].CandidateFreq.CompareTo(template[b].CandidateFreq);
                if (cmp != 0) return cmp;
                return template[b].FeatureSpan.CompareTo(template[a].FeatureSpan);
            });
            return rarityOrder;
        }

        private static Dictionary<GeomCat, Dictionary<long, List<int>>> BuildFineGrids(List<GeomShape> pool, double fineCell)
        {
            Dictionary<GeomCat, Dictionary<long, List<int>>> fineGrids = new Dictionary<GeomCat, Dictionary<long, List<int>>>();
            for (int i = 0; i < pool.Count; i++)
            {
                GeomShape p = pool[i];
                Dictionary<long, List<int>> grid;
                if (!fineGrids.TryGetValue(p.Category, out grid))
                {
                    grid = new Dictionary<long, List<int>>();
                    fineGrids[p.Category] = grid;
                }
                long ck = MakeCellKey(p.Centroid.X, p.Centroid.Y, fineCell);
                List<int> cl;
                if (!grid.TryGetValue(ck, out cl))
                {
                    cl = new List<int>();
                    grid[ck] = cl;
                }
                cl.Add(i);
            }
            return fineGrids;
        }

        private static List<GeomShape> BuildNormalizedTemplate(List<GeomShape> template)
        {
            List<GeomShape> norm = new List<GeomShape>();
            for (int i = 0; i < template.Count; i++)
            {
                GeomShape gs = template[i];
                if (gs.Category == GeomCat.CurvePoly && !gs.IsLineOrTwoPtPoly && gs.Pts != null && gs.Pts.Length >= 2)
                {
                    DecomposePolyIntoNormShapes(gs, -1, false, norm);
                }
                else
                {
                    GeomShape copy = CloneGeomShapeShallow(gs, norm.Count);
                    norm.Add(copy);
                }
            }
            return norm;
        }

        private static List<GeomShape> BuildNormalizedPool(
            List<GeomShape> pool, bool[] poolConsumed, double posTol, int maxSegsPerPoly, out bool[] parentDecomposed)
        {
            parentDecomposed = new bool[pool.Count];
            List<GeomShape> norm = new List<GeomShape>();
            List<int> unconsumedArcIndices = new List<int>();

            for (int i = 0; i < pool.Count; i++)
            {
                if (poolConsumed[i]) continue;
                GeomShape gs = pool[i];
                if (gs.Category == GeomCat.CurvePoly && !gs.IsLineOrTwoPtPoly && gs.Pts != null && gs.Pts.Length >= 2)
                {
                    int segCount = gs.Closed ? gs.Pts.Length : (gs.Pts.Length - 1);
                    if (segCount <= maxSegsPerPoly)
                    {
                        parentDecomposed[i] = true;
                        DecomposePolyIntoNormShapes(gs, i, true, norm);
                        continue;
                    }
                }
                GeomShape copy = CloneGeomShapeShallow(gs, norm.Count);
                copy.ParentPoolIdx = i;
                copy.ParentWeight = 1.0;
                norm.Add(copy);
                if (gs.Category == GeomCat.Arc) unconsumedArcIndices.Add(i);
            }

            // Merge pairs of concentric equal-radius Arcs that together form a ~360 deg Circle
            bool[] arcPaired = new bool[pool.Count];
            for (int ai = 0; ai < unconsumedArcIndices.Count; ai++)
            {
                int idxA = unconsumedArcIndices[ai];
                if (arcPaired[idxA]) continue;
                GeomShape a = pool[idxA];
                for (int bi = ai + 1; bi < unconsumedArcIndices.Count; bi++)
                {
                    int idxB = unconsumedArcIndices[bi];
                    if (arcPaired[idxB]) continue;
                    GeomShape b = pool[idxB];
                    double rTol = Math.Max(posTol * 0.5, a.Radius * 0.04);
                    if (Math.Abs(a.Radius - b.Radius) > rTol) continue;
                    if (Dist2D(a.Center, b.Center) > Math.Max(posTol, a.Radius * 0.08)) continue;
                    double totalSweep = a.SweepAngle + b.SweepAngle;
                    if (Math.Abs(totalSweep - 2.0 * Math.PI) > 0.25) continue;

                    arcPaired[idxA] = true;
                    arcPaired[idxB] = true;
                    double avgR = (a.Radius + b.Radius) * 0.5;
                    Point2d avgC = new Point2d((a.Center.X + b.Center.X) * 0.5, (a.Center.Y + b.Center.Y) * 0.5);
                    GeomShape vCircle = new GeomShape
                    {
                        Index = norm.Count,
                        ParentPoolIdx = idxA,
                        MergedPoolIndices = new int[] { idxA, idxB },
                        ParentWeight = 1.0,
                        Id = a.Id,
                        Handle = a.Handle,
                        Layer = a.Layer,
                        ClassName = "Circle",
                        Category = GeomCat.Circle,
                        IsSelected = a.IsSelected || b.IsSelected,
                        OnLockedLayer = a.OnLockedLayer || b.OnLockedLayer,
                        Center = avgC,
                        Radius = avgR,
                        Centroid = avgC,
                        TotalLength = 2.0 * Math.PI * avgR,
                        Area = Math.PI * avgR * avgR,
                        FeatureSpan = 2.0 * avgR
                    };
                    norm.Add(vCircle);
                    break;
                }
            }

            return norm;
        }

        private static void DecomposePolyIntoNormShapes(
            GeomShape poly, int parentPoolIdx, bool computeWeight, List<GeomShape> output)
        {
            int n = poly.Pts.Length;
            int segs = poly.Closed ? n : (n - 1);
            double polyTotal = Math.Max(1e-9, poly.TotalLength);
            for (int s = 0; s < segs; s++)
            {
                Point2d p0 = poly.Pts[s];
                Point2d p1 = poly.Pts[(s + 1) % n];
                double bulge = (poly.Bulges != null && s < poly.Bulges.Length) ? poly.Bulges[s] : 0.0;
                double chord = Dist2D(p0, p1);
                if (chord < 1e-6) continue;

                if (Math.Abs(bulge) <= 1e-4)
                {
                    GeomShape seg = new GeomShape
                    {
                        Index = output.Count,
                        ParentPoolIdx = parentPoolIdx,
                        ParentWeight = computeWeight ? (chord / polyTotal) : 1.0,
                        Id = poly.Id,
                        Handle = poly.Handle,
                        Layer = poly.Layer,
                        ClassName = "Line",
                        Category = GeomCat.CurvePoly,
                        IsSelected = poly.IsSelected,
                        OnLockedLayer = poly.OnLockedLayer,
                        Pts = new Point2d[] { p0, p1 },
                        Bulges = new double[] { 0.0, 0.0 },
                        Closed = false,
                        IsLineOrTwoPtPoly = true,
                        HasSignificantBulge = false,
                        TotalLength = chord,
                        Centroid = new Point2d((p0.X + p1.X) * 0.5, (p0.Y + p1.Y) * 0.5),
                        FeatureSpan = chord,
                        LongestP0 = p0,
                        LongestP1 = p1,
                        LongestSpanLen = chord
                    };
                    output.Add(seg);
                }
                else
                {
                    double theta = 4.0 * Math.Atan(Math.Abs(bulge));
                    double halfSin = Math.Sin(theta * 0.5);
                    if (halfSin < 1e-6) continue;
                    double r = (chord * 0.5) / halfSin;
                    double sag = Math.Abs(bulge) * (chord * 0.5);
                    double sign = (bulge >= 0.0) ? 1.0 : -1.0;
                    double nx = -(p1.Y - p0.Y) / chord;
                    double ny = (p1.X - p0.X) / chord;
                    Point2d chordMid = new Point2d((p0.X + p1.X) * 0.5, (p0.Y + p1.Y) * 0.5);
                    double dCenter = r * Math.Cos(theta * 0.5) * sign;
                    Point2d center = new Point2d(chordMid.X + nx * dCenter, chordMid.Y + ny * dCenter);
                    Point2d arcMid = new Point2d(chordMid.X - nx * sag * sign, chordMid.Y - ny * sag * sign);
                    double arcLen = r * theta;

                    GeomShape arcSeg = new GeomShape
                    {
                        Index = output.Count,
                        ParentPoolIdx = parentPoolIdx,
                        ParentWeight = computeWeight ? (arcLen / polyTotal) : 1.0,
                        Id = poly.Id,
                        Handle = poly.Handle,
                        Layer = poly.Layer,
                        ClassName = "Arc",
                        Category = GeomCat.Arc,
                        IsSelected = poly.IsSelected,
                        OnLockedLayer = poly.OnLockedLayer,
                        Center = center,
                        Radius = r,
                        SweepAngle = theta,
                        TotalLength = arcLen,
                        StartPt = p0,
                        EndPt = p1,
                        MidPt = arcMid,
                        Centroid = new Point2d((p0.X + arcMid.X + p1.X) / 3.0, (p0.Y + arcMid.Y + p1.Y) / 3.0),
                        FeatureSpan = 2.0 * r,
                        LongestP0 = p0,
                        LongestP1 = p1,
                        LongestSpanLen = chord
                    };
                    output.Add(arcSeg);
                }
            }
        }

        private static GeomShape CloneGeomShapeShallow(GeomShape src, int newIndex)
        {
            return new GeomShape
            {
                Index = newIndex,
                ParentPoolIdx = src.ParentPoolIdx,
                MergedPoolIndices = src.MergedPoolIndices,
                ParentWeight = src.ParentWeight,
                Id = src.Id,
                Handle = src.Handle,
                Layer = src.Layer,
                ClassName = src.ClassName,
                Category = src.Category,
                IsSelected = src.IsSelected,
                OnLockedLayer = src.OnLockedLayer,
                Centroid = src.Centroid,
                FeatureSpan = src.FeatureSpan,
                TotalLength = src.TotalLength,
                Area = src.Area,
                Pts = src.Pts,
                Bulges = src.Bulges,
                Closed = src.Closed,
                IsLineOrTwoPtPoly = src.IsLineOrTwoPtPoly,
                HasSignificantBulge = src.HasSignificantBulge,
                LongestP0 = src.LongestP0,
                LongestP1 = src.LongestP1,
                LongestSpanLen = src.LongestSpanLen,
                Center = src.Center,
                Radius = src.Radius,
                MinorRadius = src.MinorRadius,
                SweepAngle = src.SweepAngle,
                StartPt = src.StartPt,
                EndPt = src.EndPt,
                MidPt = src.MidPt,
                MajorDir = src.MajorDir,
                TextOrPattern = src.TextOrPattern,
                TextHeight = src.TextHeight,
                RefBlockDefId = src.RefBlockDefId,
                RefScaleX = src.RefScaleX,
                RefScaleY = src.RefScaleY,
                RefRotation = src.RefRotation
            };
        }

        private static bool IsRealParentAvailable(GeomShape workItem, bool[] realConsumed)
        {
            if (realConsumed == null) return true;
            if (workItem.MergedPoolIndices != null)
            {
                for (int i = 0; i < workItem.MergedPoolIndices.Length; i++)
                {
                    int pi = workItem.MergedPoolIndices[i];
                    if (pi >= 0 && pi < realConsumed.Length && realConsumed[pi]) return false;
                }
                return true;
            }
            if (workItem.ParentPoolIdx >= 0 && workItem.ParentPoolIdx < realConsumed.Length)
                return !realConsumed[workItem.ParentPoolIdx];
            return true;
        }

        private static bool SharesParentPoolEntity(GeomShape a, GeomShape b)
        {
            return a.ParentPoolIdx >= 0 && a.ParentPoolIdx == b.ParentPoolIdx;
        }

        private static MatchedClusterInstance TryVerifyCluster(
            List<GeomShape> template,
            List<int> rarityOrder,
            SimilarityMatch2D m,
            List<GeomShape> pool,
            bool[] consumed,
            Dictionary<GeomCat, Dictionary<long, List<int>>> fineGrids,
            double fineCell,
            MatchSearchOptions opts,
            double requiredRatio,
            bool preferSelected,
            bool allowFuzzyMissing,
            List<GeomShape> realPool,
            bool[] realConsumed,
            bool[] parentDecomposed,
            bool[] normConsumed)
        {
            MatchedClusterInstance inst = new MatchedClusterInstance();
            HashSet<int> usedInCluster = new HashSet<int>();
            HashSet<int> usedRealPrimitivesInCluster = new HashSet<int>();
            Dictionary<int, double> decomposedWeights = new Dictionary<int, double>();
            double errSum = 0.0;
            double totalTemplateWeight = 0.0;
            double matchedWeight = 0.0;
            int templateCircleCount = 0;
            int matchedCircleCount = 0;
            Extents3d? clusterExt = null;
            Dictionary<string, int> layerVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int ti = 0; ti < template.Count; ti++)
            {
                GeomShape ts = template[ti];
                totalTemplateWeight += Math.Max(1.0, ts.FeatureSpan);
                if (ts.Category == GeomCat.Circle) templateCircleCount++;
            }

            int maxAllowedMisses = allowFuzzyMissing
                ? Math.Max(0, template.Count - opts.MinEntityMatches)
                : 0;
            int missesSoFar = 0;

            for (int oi = 0; oi < rarityOrder.Count; oi++)
            {
                GeomShape t = template[rarityOrder[oi]];
                Point2d expC = m.Apply(t.Centroid);

                Dictionary<long, List<int>> grid;
                if (!fineGrids.TryGetValue(t.Category, out grid))
                {
                    missesSoFar++;
                    if (missesSoFar > maxAllowedMisses) return null;
                    continue;
                }

                double scaledPosTol = Math.Max(1e-4, opts.PosTol * m.Scale);
                double maxSearchDist = Math.Max(scaledPosTol * 1.8, t.FeatureSpan * m.Scale * 0.14);
                int cellRad = Math.Min(4, Math.Max(1, (int)Math.Ceiling(maxSearchDist / fineCell)));
                int ix0 = (int)Math.Floor(expC.X / fineCell);
                int iy0 = (int)Math.Floor(expC.Y / fineCell);

                int bestIdx = -1;
                double bestErr = double.MaxValue;
                bool bestSel = false;

                for (int ox = -cellRad; ox <= cellRad; ox++)
                {
                    for (int oy = -cellRad; oy <= cellRad; oy++)
                    {
                        long ck = PackCellCoords(ix0 + ox, iy0 + oy);
                        List<int> cellList;
                        if (!grid.TryGetValue(ck, out cellList)) continue;

                        for (int ci = 0; ci < cellList.Count; ci++)
                        {
                            int pIdx = cellList[ci];
                            if (consumed[pIdx] || usedInCluster.Contains(pIdx)) continue;
                            GeomShape cand = pool[pIdx];

                            if (realConsumed != null)
                            {
                                if (!IsRealParentAvailable(cand, realConsumed)) continue;
                                if (cand.MergedPoolIndices != null)
                                {
                                    bool conflict = false;
                                    for (int mi = 0; mi < cand.MergedPoolIndices.Length; mi++)
                                    {
                                        if (usedRealPrimitivesInCluster.Contains(cand.MergedPoolIndices[mi])) { conflict = true; break; }
                                    }
                                    if (conflict) continue;
                                }
                                else if (parentDecomposed != null && cand.ParentPoolIdx >= 0 && !parentDecomposed[cand.ParentPoolIdx])
                                {
                                    if (usedRealPrimitivesInCluster.Contains(cand.ParentPoolIdx)) continue;
                                }
                            }

                            double matchErr;
                            if (!ShapeMatchesUnderTransform(t, cand, m, opts, out matchErr))
                                continue;

                            if (preferSelected)
                            {
                                if (cand.IsSelected && !bestSel)
                                {
                                    bestIdx = pIdx;
                                    bestErr = matchErr;
                                    bestSel = true;
                                    continue;
                                }
                                if (!cand.IsSelected && bestSel) continue;
                            }
                            if (matchErr < bestErr)
                            {
                                bestErr = matchErr;
                                bestIdx = pIdx;
                            }
                        }
                    }
                }

                if (bestIdx < 0)
                {
                    missesSoFar++;
                    if (missesSoFar > maxAllowedMisses) return null;
                    continue;
                }

                GeomShape picked = pool[bestIdx];
                usedInCluster.Add(bestIdx);
                if (realConsumed != null)
                {
                    if (picked.MergedPoolIndices != null)
                    {
                        for (int mi = 0; mi < picked.MergedPoolIndices.Length; mi++)
                            usedRealPrimitivesInCluster.Add(picked.MergedPoolIndices[mi]);
                    }
                    else if (picked.ParentPoolIdx >= 0)
                    {
                        if (parentDecomposed != null && parentDecomposed[picked.ParentPoolIdx])
                        {
                            double prevW = decomposedWeights.ContainsKey(picked.ParentPoolIdx) ? decomposedWeights[picked.ParentPoolIdx] : 0.0;
                            decomposedWeights[picked.ParentPoolIdx] = prevW + picked.ParentWeight;
                        }
                        else
                        {
                            usedRealPrimitivesInCluster.Add(picked.ParentPoolIdx);
                        }
                    }
                }

                inst.MatchedIndices.Add(bestIdx);
                inst.MatchedIds.Add(picked.Id);
                inst.MatchedHandles.Add(picked.Handle);
                if (picked.IsSelected) inst.SelectedHits++;
                errSum += bestErr;
                matchedWeight += Math.Max(1.0, t.FeatureSpan);
                if (t.Category == GeomCat.Circle) matchedCircleCount++;

                layerVotes[picked.Layer] = layerVotes.ContainsKey(picked.Layer) ? layerVotes[picked.Layer] + 1 : 1;
                ExpandClusterExtents(ref clusterExt, picked);
            }

            double ratio = (double)inst.MatchedIndices.Count / Math.Max(1, template.Count);
            if (!allowFuzzyMissing)
            {
                if (inst.MatchedIndices.Count != template.Count) return null;
            }
            else
            {
                if (inst.MatchedIndices.Count < opts.MinEntityMatches || ratio < requiredRatio) return null;
                if (matchedWeight < totalTemplateWeight * Math.Max(0.75, requiredRatio - 0.05)) return null;
                if (templateCircleCount > 0 && matchedCircleCount == 0) return null;
            }

            // In Normalized Pass: verify any decomposed ModelSpace Polyline had >= 85% of its length consumed
            if (decomposedWeights.Count > 0)
            {
                foreach (KeyValuePair<int, double> kv in decomposedWeights)
                {
                    if (kv.Value < 0.85) return null;
                }
            }

            inst.InsertPosition = m.TargetOrigin;
            inst.RotationRad = m.RotationRad;
            inst.Scale = m.Scale;
            inst.MirroredY = m.MirroredY;
            inst.MatchRatio = ratio;
            inst.AvgError = errSum / Math.Max(1, inst.MatchedIndices.Count);
            inst.Bounds = clusterExt.HasValue ? clusterExt.Value : new Extents3d(
                new Point3d(m.TargetOrigin.X, m.TargetOrigin.Y, 0.0),
                new Point3d(m.TargetOrigin.X, m.TargetOrigin.Y, 0.0));
            string domLayer = "0";
            int bestVotes = -1;
            foreach (KeyValuePair<string, int> kv in layerVotes)
            {
                if (kv.Value > bestVotes) { bestVotes = kv.Value; domLayer = kv.Key; }
            }
            inst.DominantLayer = domLayer;
            return inst;
        }

        private static void ConfirmRealCluster(
            MatchedClusterInstance inst, bool[] poolConsumed, List<MatchedClusterInstance> results)
        {
            foreach (int pi in inst.MatchedIndices)
            {
                if (pi >= 0 && pi < poolConsumed.Length) poolConsumed[pi] = true;
            }
            results.Add(inst);
        }

        private static void ConfirmClusterInPass(
            MatchedClusterInstance inst,
            List<GeomShape> workPool,
            bool[] workConsumed,
            List<GeomShape> realPool,
            bool[] realConsumed,
            List<MatchedClusterInstance> results,
            bool isNormalized)
        {
            if (!isNormalized)
            {
                for (int i = 0; i < inst.MatchedIndices.Count; i++)
                {
                    int pi = inst.MatchedIndices[i];
                    workConsumed[pi] = true;
                    realConsumed[pi] = true;
                }
                results.Add(inst);
                return;
            }

            HashSet<int> realIdxSet = new HashSet<int>();
            for (int i = 0; i < inst.MatchedIndices.Count; i++)
            {
                int vIdx = inst.MatchedIndices[i];
                workConsumed[vIdx] = true;
                GeomShape gs = workPool[vIdx];
                if (gs.MergedPoolIndices != null)
                {
                    for (int mi = 0; mi < gs.MergedPoolIndices.Length; mi++)
                        realIdxSet.Add(gs.MergedPoolIndices[mi]);
                }
                else if (gs.ParentPoolIdx >= 0)
                {
                    realIdxSet.Add(gs.ParentPoolIdx);
                }
            }

            inst.MatchedIndices.Clear();
            inst.MatchedIds.Clear();
            inst.MatchedHandles.Clear();
            foreach (int rIdx in realIdxSet)
            {
                if (rIdx >= 0 && rIdx < realConsumed.Length)
                {
                    realConsumed[rIdx] = true;
                    inst.MatchedIndices.Add(rIdx);
                    inst.MatchedIds.Add(realPool[rIdx].Id);
                    inst.MatchedHandles.Add(realPool[rIdx].Handle);
                }
            }
            for (int vi = 0; vi < workPool.Count; vi++)
            {
                if (workConsumed[vi]) continue;
                GeomShape wg = workPool[vi];
                if (!IsRealParentAvailable(wg, realConsumed)) workConsumed[vi] = true;
            }
            results.Add(inst);
        }

        private static void ExpandClusterExtents(ref Extents3d? ext, GeomShape gs)
        {
            List<Point2d> pts = new List<Point2d>();
            if (gs.Pts != null && gs.Pts.Length > 0) pts.AddRange(gs.Pts);
            else if (gs.Category == GeomCat.Circle || gs.Category == GeomCat.Ellipse)
            {
                double r = Math.Max(gs.Radius, gs.MinorRadius);
                pts.Add(new Point2d(gs.Center.X - r, gs.Center.Y - r));
                pts.Add(new Point2d(gs.Center.X + r, gs.Center.Y + r));
            }
            else if (gs.Category == GeomCat.Arc)
            {
                pts.Add(gs.StartPt);
                pts.Add(gs.MidPt);
                pts.Add(gs.EndPt);
            }
            else
            {
                pts.Add(new Point2d(gs.Centroid.X - gs.FeatureSpan * 0.35, gs.Centroid.Y - gs.FeatureSpan * 0.35));
                pts.Add(new Point2d(gs.Centroid.X + gs.FeatureSpan * 0.35, gs.Centroid.Y + gs.FeatureSpan * 0.35));
            }
            foreach (Point2d p in pts)
            {
                if (!ext.HasValue) ext = new Extents3d(new Point3d(p.X, p.Y, 0.0), new Point3d(p.X, p.Y, 0.0));
                else
                {
                    Extents3d cur = ext.Value;
                    cur.AddPoint(new Point3d(p.X, p.Y, 0.0));
                    ext = cur;
                }
            }
        }

        private static List<int> FindAllLocalCandidatesFor(
            GeomShape t, List<GeomShape> pool, bool[] consumed, MatchSearchOptions opts, double sMin, double sMax)
        {
            List<int> list = new List<int>();
            double minRatioF = Math.Max(0.2, 1.0 - opts.SizeTolRatio);
            double maxRatioF = 1.0 + opts.SizeTolRatio;

            for (int i = 0; i < pool.Count; i++)
            {
                if (consumed != null && consumed[i]) continue;
                GeomShape c = pool[i];
                if (c.Category != t.Category) continue;
                if (opts.StrictTypes && !string.Equals(t.ClassName, c.ClassName, StringComparison.Ordinal)) continue;
                if (!opts.StrictTypes && t.Category == GeomCat.CurvePoly && (t.IsLineOrTwoPtPoly != c.IsLineOrTwoPtPoly))
                    continue;
                if (t.Category == GeomCat.CurvePoly)
                {
                    if (t.Closed != c.Closed) continue;
                    double loLen = t.TotalLength * sMin * minRatioF - opts.PosTol * sMin * 2.5;
                    double hiLen = t.TotalLength * sMax * maxRatioF + opts.PosTol * sMax * 2.5;
                    if (c.TotalLength < loLen || c.TotalLength > hiLen) continue;
                    if (t.Pts.Length != c.Pts.Length)
                    {
                        if (t.Pts.Length < 6 || c.Pts.Length < 6 || t.HasSignificantBulge || c.HasSignificantBulge) continue;
                    }
                }
                else if (t.Category == GeomCat.Circle)
                {
                    double loR = t.Radius * sMin * minRatioF - opts.PosTol * sMin * 0.5;
                    double hiR = t.Radius * sMax * maxRatioF + opts.PosTol * sMax * 0.5;
                    if (c.Radius < loR || c.Radius > hiR) continue;
                }
                else if (t.Category == GeomCat.Arc)
                {
                    double loR = t.Radius * sMin * minRatioF - opts.PosTol * sMin * 0.5;
                    double hiR = t.Radius * sMax * maxRatioF + opts.PosTol * sMax * 0.5;
                    if (c.Radius < loR || c.Radius > hiR) continue;
                    if (Math.Abs(t.SweepAngle - c.SweepAngle) > 0.14) continue;
                }
                else if (t.Category == GeomCat.Ellipse)
                {
                    if (t.Closed != c.Closed) continue;
                    double loR = t.Radius * sMin * minRatioF - opts.PosTol * sMin * 0.5;
                    double hiR = t.Radius * sMax * maxRatioF + opts.PosTol * sMax * 0.5;
                    if (c.Radius < loR || c.Radius > hiR) continue;
                    double tRatio = t.MinorRadius / Math.Max(1e-9, t.Radius);
                    double cRatio = c.MinorRadius / Math.Max(1e-9, c.Radius);
                    if (Math.Abs(tRatio - cRatio) > opts.SizeTolRatio * 1.5) continue;
                }
                else if (t.Category == GeomCat.Solid)
                {
                    if (t.Pts.Length != c.Pts.Length) continue;
                    double loLen = t.TotalLength * sMin * minRatioF - opts.PosTol * sMin * 2.5;
                    double hiLen = t.TotalLength * sMax * maxRatioF + opts.PosTol * sMax * 2.5;
                    if (c.TotalLength < loLen || c.TotalLength > hiLen) continue;
                }
                else if (t.Category == GeomCat.Hatch)
                {
                    if (!string.Equals(t.TextOrPattern, c.TextOrPattern, StringComparison.OrdinalIgnoreCase)) continue;
                    double loA = t.Area * sMin * sMin * minRatioF * minRatioF - opts.PosTol * t.FeatureSpan * sMin;
                    double hiA = t.Area * sMax * sMax * maxRatioF * maxRatioF + opts.PosTol * t.FeatureSpan * sMax;
                    if (c.Area < loA || c.Area > hiA) continue;
                }
                else if (t.Category == GeomCat.Spline)
                {
                    if (t.Closed != c.Closed) continue;
                    double loLen = t.TotalLength * sMin * minRatioF - opts.PosTol * sMin * 2.5;
                    double hiLen = t.TotalLength * sMax * maxRatioF + opts.PosTol * sMax * 2.5;
                    if (c.TotalLength < loLen || c.TotalLength > hiLen) continue;
                }
                else if (t.Category == GeomCat.BlockRef)
                {
                    if (t.RefBlockDefId != c.RefBlockDefId) continue;
                    double loSx = Math.Abs(t.RefScaleX) * sMin * minRatioF;
                    double hiSx = Math.Abs(t.RefScaleX) * sMax * maxRatioF;
                    if (Math.Abs(c.RefScaleX) < loSx || Math.Abs(c.RefScaleX) > hiSx) continue;
                }
                else if (t.Category == GeomCat.Text)
                {
                    if (!opts.IgnoreTextContent && !string.Equals(t.TextOrPattern, c.TextOrPattern, StringComparison.Ordinal)) continue;
                    double loH = t.TextHeight * sMin * minRatioF - opts.PosTol * sMin * 0.5;
                    double hiH = t.TextHeight * sMax * maxRatioF + opts.PosTol * sMax * 0.5;
                    if (c.TextHeight < loH || c.TextHeight > hiH) continue;
                }
                list.Add(i);
            }
            return list;
        }

        private static bool CheckEntityScaleCompatibility(GeomShape t, GeomShape c, double s, MatchSearchOptions opts)
        {
            double tol = Math.Max(opts.SizeTolRatio, 0.04);
            if (t.Category == GeomCat.Circle || t.Category == GeomCat.Arc)
            {
                double expR = t.Radius * s;
                return Math.Abs(c.Radius - expR) <= Math.Max(opts.PosTol * s * 0.5, expR * tol);
            }
            if (t.Category == GeomCat.Ellipse)
            {
                double expR1 = t.Radius * s;
                double expR2 = t.MinorRadius * s;
                return Math.Abs(c.Radius - expR1) <= Math.Max(opts.PosTol * s * 0.5, expR1 * tol) &&
                       Math.Abs(c.MinorRadius - expR2) <= Math.Max(opts.PosTol * s * 0.5, expR2 * tol);
            }
            if (t.Category == GeomCat.CurvePoly || t.Category == GeomCat.Spline || t.Category == GeomCat.Solid)
            {
                double expL = t.TotalLength * s;
                return Math.Abs(c.TotalLength - expL) <= Math.Max(opts.PosTol * s * 2.5, expL * tol);
            }
            if (t.Category == GeomCat.BlockRef)
            {
                double expSx = Math.Abs(t.RefScaleX) * s;
                return Math.Abs(Math.Abs(c.RefScaleX) - expSx) <= Math.Max(0.02 * s, expSx * tol);
            }
            if (t.Category == GeomCat.Text)
            {
                double expH = t.TextHeight * s;
                return Math.Abs(c.TextHeight - expH) <= Math.Max(opts.PosTol * s * 0.5, expH * tol);
            }
            return true;
        }

        private static double EstimateEntityScale(GeomShape t, GeomShape c)
        {
            if (t.Category == GeomCat.Circle || t.Category == GeomCat.Arc || t.Category == GeomCat.Ellipse)
                return (t.Radius > 1e-9) ? (c.Radius / t.Radius) : 1.0;
            if (t.Category == GeomCat.CurvePoly || t.Category == GeomCat.Spline || t.Category == GeomCat.Solid)
                return (t.TotalLength > 1e-9) ? (c.TotalLength / t.TotalLength) : 1.0;
            if (t.Category == GeomCat.Hatch)
                return (t.Area > 1e-9 && c.Area > 1e-9) ? Math.Sqrt(c.Area / t.Area) : 1.0;
            if (t.Category == GeomCat.BlockRef)
                return (Math.Abs(t.RefScaleX) > 1e-9) ? (Math.Abs(c.RefScaleX) / Math.Abs(t.RefScaleX)) : 1.0;
            if (t.Category == GeomCat.Text)
                return (t.TextHeight > 1e-9) ? (c.TextHeight / t.TextHeight) : 1.0;
            return 1.0;
        }

        private static bool ShapeMatchesUnderTransform(
            GeomShape t, GeomShape cand, SimilarityMatch2D m, MatchSearchOptions opts, out double errOut)
        {
            errOut = double.MaxValue;
            if (cand.Category != t.Category) return false;
            if (opts.StrictTypes && !string.Equals(t.ClassName, cand.ClassName, StringComparison.Ordinal)) return false;

            double s = m.Scale;
            double scaledPosTol = Math.Max(1e-4, opts.PosTol * s);
            double scaledElemTol = Math.Max(scaledPosTol * 0.35, t.FeatureSpan * s * opts.SizeTolRatio);
            double cDist = Dist2D(m.Apply(t.Centroid), cand.Centroid);
            if (cDist > Math.Max(scaledPosTol * 1.5, t.FeatureSpan * s * 0.12)) return false;

            if (t.Category == GeomCat.CurvePoly)
            {
                if (t.IsLineOrTwoPtPoly != cand.IsLineOrTwoPtPoly) return false;
                if (t.Closed != cand.Closed) return false;
                double expectedLen = t.TotalLength * s;
                double lenTol = Math.Max(scaledPosTol * 2.5, expectedLen * opts.SizeTolRatio);
                if (Math.Abs(expectedLen - cand.TotalLength) > lenTol) return false;

                if (t.IsLineOrTwoPtPoly)
                {
                    Point2d q0 = m.Apply(t.Pts[0]);
                    Point2d q1 = m.Apply(t.Pts[1]);
                    double fwd = Math.Max(Dist2D(q0, cand.Pts[0]), Dist2D(q1, cand.Pts[1]));
                    double rev = Math.Max(Dist2D(q0, cand.Pts[1]), Dist2D(q1, cand.Pts[0]));
                    double best = Math.Min(fwd, rev);
                    double lineTol = Math.Max(scaledPosTol, expectedLen * opts.SizeTolRatio * 0.75);
                    if (best <= lineTol) { errOut = best; return true; }
                    return false;
                }

                int n = t.Pts.Length;
                if (n == cand.Pts.Length)
                {
                    Point2d[] tTrans = new Point2d[n];
                    for (int i = 0; i < n; i++) tTrans[i] = m.Apply(t.Pts[i]);

                    if (!t.Closed)
                    {
                        double eFwd = MaxPolylineVertexDist(t, tTrans, cand, m, false);
                        if (eFwd <= scaledPosTol) { errOut = eFwd; return true; }
                        double eRev = MaxPolylineVertexDist(t, tTrans, cand, m, true);
                        if (eRev <= scaledPosTol) { errOut = eRev; return true; }
                        return false;
                    }
                    else
                    {
                        double bestLoopErr = double.MaxValue;
                        for (int st = 0; st < n; st++)
                        {
                            if (Dist2D(tTrans[0], cand.Pts[st]) > scaledPosTol) continue;
                            double eFwd = MaxClosedLoopDist(t, tTrans, cand, m, st, false, scaledPosTol);
                            if (eFwd <= scaledPosTol && eFwd < bestLoopErr) bestLoopErr = eFwd;
                            double eRev = MaxClosedLoopDist(t, tTrans, cand, m, st, true, scaledPosTol);
                            if (eRev <= scaledPosTol && eRev < bestLoopErr) bestLoopErr = eRev;
                        }
                        if (bestLoopErr <= scaledPosTol) { errOut = bestLoopErr; return true; }
                        return false;
                    }
                }
                else if (n >= 6 && cand.Pts.Length >= 6 && !t.HasSignificantBulge && !cand.HasSignificantBulge)
                {
                    Point2d[] tTrans = new Point2d[n];
                    for (int i = 0; i < n; i++) tTrans[i] = m.Apply(t.Pts[i]);
                    double hTol = Math.Max(scaledPosTol, scaledElemTol * 1.4);
                    double d1 = MaxPointsToPolyDist(tTrans, cand.Pts, cand.Closed, hTol);
                    if (d1 > hTol) return false;
                    double d2 = MaxPointsToPolyDist(cand.Pts, tTrans, t.Closed, hTol);
                    if (d2 > hTol) return false;
                    errOut = Math.Max(d1, d2);
                    return true;
                }
                return false;
            }
            if (t.Category == GeomCat.Circle)
            {
                double expectedR = t.Radius * s;
                double rTol = Math.Max(scaledPosTol * 0.5, expectedR * opts.SizeTolRatio);
                double rd = Math.Abs(expectedR - cand.Radius);
                if (rd > rTol || cDist > Math.Max(scaledPosTol, expectedR * 0.15)) return false;
                errOut = Math.Max(rd, cDist);
                return true;
            }
            if (t.Category == GeomCat.Arc)
            {
                double expectedR = t.Radius * s;
                double rTol = Math.Max(scaledPosTol * 0.5, expectedR * opts.SizeTolRatio);
                double rd = Math.Abs(expectedR - cand.Radius);
                if (rd > rTol || Math.Abs(t.SweepAngle - cand.SweepAngle) > 0.14) return false;
                double dCenter = Dist2D(m.Apply(t.Center), cand.Center);
                double dMid = Dist2D(m.Apply(t.MidPt), cand.MidPt);
                if (dCenter > scaledPosTol * 1.25 || dMid > scaledPosTol * 1.5) return false;
                Point2d qs = m.Apply(t.StartPt);
                Point2d qe = m.Apply(t.EndPt);
                double fwd = Math.Max(Dist2D(qs, cand.StartPt), Dist2D(qe, cand.EndPt));
                double rev = Math.Max(Dist2D(qs, cand.EndPt), Dist2D(qe, cand.StartPt));
                double endErr = Math.Min(fwd, rev);
                if (endErr > scaledPosTol * 1.25) return false;
                errOut = Math.Max(Math.Max(dCenter, dMid), endErr);
                return true;
            }
            if (t.Category == GeomCat.Ellipse)
            {
                if (t.Closed != cand.Closed) return false;
                double expR1 = t.Radius * s;
                double expR2 = t.MinorRadius * s;
                double rTol1 = Math.Max(scaledPosTol * 0.5, expR1 * opts.SizeTolRatio);
                double rTol2 = Math.Max(scaledPosTol * 0.5, expR2 * opts.SizeTolRatio);
                double r1 = Math.Abs(expR1 - cand.Radius);
                double r2 = Math.Abs(expR2 - cand.MinorRadius);
                if (r1 > rTol1 || r2 > rTol2) return false;
                double dc = Dist2D(m.Apply(t.Center), cand.Center);
                if (dc > scaledPosTol) return false;
                if (Math.Abs(t.Radius - t.MinorRadius) > Math.Max(opts.PosTol * 0.5, t.Radius * 0.04))
                {
                    Point2d pTipT = m.Apply(new Point2d(t.Center.X + t.MajorDir.X, t.Center.Y + t.MajorDir.Y));
                    Point2d pCenterT = m.Apply(t.Center);
                    double vx = (pTipT.X - pCenterT.X) / s, vy = (pTipT.Y - pCenterT.Y) / s;
                    double dot = Math.Abs(vx * cand.MajorDir.X + vy * cand.MajorDir.Y);
                    if (dot < 0.992) return false;
                }
                if (!t.Closed)
                {
                    double dm = Dist2D(m.Apply(t.MidPt), cand.MidPt);
                    if (dm > scaledPosTol * 1.4) return false;
                    Point2d qs = m.Apply(t.StartPt);
                    Point2d qe = m.Apply(t.EndPt);
                    double endErr = Math.Min(
                        Math.Max(Dist2D(qs, cand.StartPt), Dist2D(qe, cand.EndPt)),
                        Math.Max(Dist2D(qs, cand.EndPt), Dist2D(qe, cand.StartPt)));
                    if (endErr > scaledPosTol) return false;
                }
                errOut = Math.Max(dc, Math.Max(r1, r2));
                return true;
            }
            if (t.Category == GeomCat.Solid)
            {
                int n = t.Pts.Length;
                if (n != cand.Pts.Length) return false;
                Point2d[] tTrans = new Point2d[n];
                for (int i = 0; i < n; i++) tTrans[i] = m.Apply(t.Pts[i]);
                double bestErr = double.MaxValue;
                for (int st = 0; st < n; st++)
                {
                    if (Dist2D(tTrans[0], cand.Pts[st]) > scaledPosTol) continue;
                    double e1 = MaxLoopPtDistOnly(tTrans, cand.Pts, st, false, scaledPosTol);
                    if (e1 < bestErr) bestErr = e1;
                    double e2 = MaxLoopPtDistOnly(tTrans, cand.Pts, st, true, scaledPosTol);
                    if (e2 < bestErr) bestErr = e2;
                }
                if (bestErr <= scaledPosTol) { errOut = bestErr; return true; }
                return false;
            }
            if (t.Category == GeomCat.Spline || t.Category == GeomCat.Hatch)
            {
                if (t.Category == GeomCat.Hatch && !string.Equals(t.TextOrPattern, cand.TextOrPattern, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (t.Pts == null || cand.Pts == null || t.Pts.Length == 0 || cand.Pts.Length == 0)
                    return false;
                Point2d[] tTrans = new Point2d[t.Pts.Length];
                for (int i = 0; i < t.Pts.Length; i++) tTrans[i] = m.Apply(t.Pts[i]);
                double hTol = Math.Max(scaledPosTol * 1.6, scaledElemTol * 1.5);
                double d1 = MaxPointsToPolyDist(tTrans, cand.Pts, cand.Closed, hTol);
                if (d1 > hTol) return false;
                double d2 = MaxPointsToPolyDist(cand.Pts, tTrans, t.Closed, hTol);
                if (d2 > hTol) return false;
                errOut = Math.Max(cDist, Math.Max(d1, d2));
                return true;
            }
            if (t.Category == GeomCat.BlockRef)
            {
                if (t.RefBlockDefId != cand.RefBlockDefId) return false;
                double expSx = Math.Abs(t.RefScaleX) * s;
                if (Math.Abs(Math.Abs(cand.RefScaleX) - expSx) > Math.Max(0.02 * s, expSx * opts.SizeTolRatio))
                    return false;
                double dp = Dist2D(m.Apply(t.Center), cand.Center);
                if (dp > scaledPosTol) return false;
                Point2d pCenterT = m.Apply(t.Center);
                Point2d pDirT = m.Apply(new Point2d(t.Center.X + t.MajorDir.X, t.Center.Y + t.MajorDir.Y));
                double dot = ((pDirT.X - pCenterT.X) / s) * cand.MajorDir.X + ((pDirT.Y - pCenterT.Y) / s) * cand.MajorDir.Y;
                if (dot < 0.992) return false;
                errOut = dp;
                return true;
            }
            if (t.Category == GeomCat.Text)
            {
                if (!opts.IgnoreTextContent && !string.Equals(t.TextOrPattern, cand.TextOrPattern, StringComparison.Ordinal))
                    return false;
                double expH = t.TextHeight * s;
                if (Math.Abs(cand.TextHeight - expH) > Math.Max(scaledPosTol * 0.5, expH * opts.SizeTolRatio))
                    return false;
                if (cDist > scaledPosTol) return false;
                Point2d pCenterT = m.Apply(t.Center);
                Point2d pDirT = m.Apply(new Point2d(t.Center.X + t.MajorDir.X, t.Center.Y + t.MajorDir.Y));
                double dot = ((pDirT.X - pCenterT.X) / s) * cand.MajorDir.X + ((pDirT.Y - pCenterT.Y) / s) * cand.MajorDir.Y;
                if (dot < 0.985) return false;
                errOut = cDist;
                return true;
            }
            return false;
        }

        private static double MaxPolylineVertexDist(
            GeomShape t, Point2d[] tTrans, GeomShape cand, SimilarityMatch2D m, bool reversed)
        {
            int n = tTrans.Length;
            double maxE = 0.0;
            for (int i = 0; i < n; i++)
            {
                int ci = reversed ? (n - 1 - i) : i;
                double d = Dist2D(tTrans[i], cand.Pts[ci]);
                if (d > maxE) maxE = d;
            }
            if (t.HasSignificantBulge || cand.HasSignificantBulge)
            {
                for (int i = 0; i < n - 1; i++)
                {
                    double bt = t.Bulges[i];
                    int cSeg = reversed ? (n - 2 - i) : i;
                    double bc = cand.Bulges[cSeg];
                    if (Math.Abs(bt) > 1e-4 || Math.Abs(bc) > 1e-4)
                    {
                        Point2d mt = m.Apply(PolylineArcMidpoint(t.Pts[i], t.Pts[i + 1], bt));
                        Point2d mc = PolylineArcMidpoint(cand.Pts[cSeg], cand.Pts[cSeg + 1], bc);
                        double dm = Dist2D(mt, mc);
                        if (dm > maxE) maxE = dm;
                    }
                }
            }
            return maxE;
        }

        private static double MaxClosedLoopDist(
            GeomShape t, Point2d[] tTrans, GeomShape cand, SimilarityMatch2D m, int startIdx, bool reversed, double tolLimit)
        {
            int n = tTrans.Length;
            double maxE = 0.0;
            for (int i = 0; i < n; i++)
            {
                int ci = reversed ? ((startIdx - i + n) % n) : ((startIdx + i) % n);
                double d = Dist2D(tTrans[i], cand.Pts[ci]);
                if (d > tolLimit) return d;
                if (d > maxE) maxE = d;
            }
            if (t.HasSignificantBulge || cand.HasSignificantBulge)
            {
                for (int i = 0; i < n; i++)
                {
                    int iNext = (i + 1) % n;
                    double bt = t.Bulges[i];
                    int cV1 = reversed ? ((startIdx - i + n) % n) : ((startIdx + i) % n);
                    int cV2 = reversed ? ((startIdx - i - 1 + n * 2) % n) : ((startIdx + i + 1) % n);
                    int cSeg = reversed ? cV2 : cV1;
                    double bc = cand.Bulges[cSeg];
                    if (Math.Abs(bt) > 1e-4 || Math.Abs(bc) > 1e-4)
                    {
                        Point2d mt = m.Apply(PolylineArcMidpoint(t.Pts[i], t.Pts[iNext], bt));
                        Point2d mc = PolylineArcMidpoint(cand.Pts[cSeg], cand.Pts[(cSeg + 1) % n], bc);
                        double dm = Dist2D(mt, mc);
                        if (dm > tolLimit * 1.4) return dm;
                        if (dm > maxE) maxE = dm;
                    }
                }
            }
            return maxE;
        }

        private static double MaxLoopPtDistOnly(Point2d[] tTrans, Point2d[] cPts, int startIdx, bool reversed, double tolLimit)
        {
            int n = tTrans.Length;
            double maxE = 0.0;
            for (int i = 0; i < n; i++)
            {
                int ci = reversed ? ((startIdx - i + n) % n) : ((startIdx + i) % n);
                double d = Dist2D(tTrans[i], cPts[ci]);
                if (d > tolLimit) return d;
                if (d > maxE) maxE = d;
            }
            return maxE;
        }

        private static double MaxPointsToPolyDist(Point2d[] pts, Point2d[] targetPoly, bool targetClosed, double earlyStopTol)
        {
            if (pts == null || targetPoly == null || targetPoly.Length < 2) return double.MaxValue;
            int nT = targetPoly.Length;
            int segCount = targetClosed ? nT : (nT - 1);
            double maxMin = 0.0;
            for (int i = 0; i < pts.Length; i++)
            {
                Point2d p = pts[i];
                double minSegD = double.MaxValue;
                for (int s = 0; s < segCount; s++)
                {
                    Point2d a = targetPoly[s];
                    Point2d b = targetPoly[(s + 1) % nT];
                    double d = PointToSegmentDist2D(p, a, b);
                    if (d < minSegD) minSegD = d;
                    if (minSegD <= earlyStopTol * 0.25) break;
                }
                if (minSegD > maxMin) maxMin = minSegD;
                if (maxMin > earlyStopTol) return maxMin;
            }
            return maxMin;
        }

        private static double PointToSegmentDist2D(Point2d p, Point2d a, Point2d b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12) return Dist2D(p, a);
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;
            double px = a.X + t * dx, py = a.Y + t * dy;
            double ex = p.X - px, ey = p.Y - py;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        private static Point2d PolylineArcMidpoint(Point2d a, Point2d b, double bulge)
        {
            double mx = (a.X + b.X) * 0.5 + 0.5 * bulge * (b.Y - a.Y);
            double my = (a.Y + b.Y) * 0.5 - 0.5 * bulge * (b.X - a.X);
            return new Point2d(mx, my);
        }

        private static bool HasDirectionAxis(GeomShape gs)
        {
            if (gs.Category == GeomCat.Circle) return false;
            return true;
        }

        private static double GetEntityAxisAngle(GeomShape gs)
        {
            if (gs.Category == GeomCat.Ellipse || gs.Category == GeomCat.BlockRef || gs.Category == GeomCat.Text)
                return Math.Atan2(gs.MajorDir.Y, gs.MajorDir.X);
            return Math.Atan2(gs.LongestP1.Y - gs.LongestP0.Y, gs.LongestP1.X - gs.LongestP0.X);
        }

        private static void CollectPolyAlignmentAngles(
            GeomShape tPoly, GeomShape cPoly, bool mirrored, bool allowRotation,
            double angleTolRad, double posTol, double sMin, double sMax, double sizeTolRatio,
            List<ScaleAngleCandidate> outCandidates)
        {
            if (tPoly.Pts == null || cPoly.Pts == null || tPoly.Pts.Length < 2 || cPoly.Pts.Length < 2) return;
            int bestA = 0, bestB = 1;
            double bestLen = -1.0;
            int nT = tPoly.Pts.Length;
            int tSegs = tPoly.Closed ? nT : (nT - 1);
            for (int i = 0; i < tSegs; i++)
            {
                int j = (i + 1) % nT;
                double d = Dist2D(tPoly.Pts[i], tPoly.Pts[j]);
                if (d > bestLen) { bestLen = d; bestA = i; bestB = j; }
            }
            if (bestLen < 1e-6) return;
            double tAng = Math.Atan2(tPoly.Pts[bestB].Y - tPoly.Pts[bestA].Y, tPoly.Pts[bestB].X - tPoly.Pts[bestA].X);
            double lenLo = bestLen * sMin * (1.0 - sizeTolRatio) - posTol * 2.0;
            double lenHi = bestLen * sMax * (1.0 + sizeTolRatio) + posTol * 2.0;

            int nC = cPoly.Pts.Length;
            int cSegs = cPoly.Closed ? nC : (nC - 1);
            for (int i = 0; i < cSegs; i++)
            {
                int j = (i + 1) % nC;
                double d = Dist2D(cPoly.Pts[i], cPoly.Pts[j]);
                if (d < lenLo || d > lenHi) continue;
                double candScale = d / bestLen;
                if (Math.Abs(candScale - 1.0) <= sizeTolRatio * 0.25 && sMin <= 1.0 && sMax >= 1.0)
                    candScale = 1.0;
                if (candScale < sMin || candScale > sMax) continue;

                double cAng = Math.Atan2(cPoly.Pts[j].Y - cPoly.Pts[i].Y, cPoly.Pts[j].X - cPoly.Pts[i].X);
                double th1 = NormalizeRadians(mirrored ? (cAng + tAng) : (cAng - tAng));
                double th2 = NormalizeRadians(mirrored ? ((cAng + Math.PI) + tAng) : ((cAng + Math.PI) - tAng));
                if (allowRotation || Math.Abs(th1) <= angleTolRad) AddUniqueScaleAngle(outCandidates, candScale, th1);
                if (allowRotation || Math.Abs(th2) <= angleTolRad) AddUniqueScaleAngle(outCandidates, candScale, th2);
            }
            if (outCandidates.Count == 0 && (tPoly.Pts.Length >= 6 || cPoly.Pts.Length >= 6))
            {
                double sEst = EstimateEntityScale(tPoly, cPoly);
                if (sEst >= sMin && sEst <= sMax)
                {
                    double ta = GetEntityAxisAngle(tPoly);
                    double ca = GetEntityAxisAngle(cPoly);
                    double th1 = NormalizeRadians(mirrored ? (ca + ta) : (ca - ta));
                    double th2 = NormalizeRadians(th1 + Math.PI);
                    if (allowRotation || Math.Abs(th1) <= angleTolRad) AddUniqueScaleAngle(outCandidates, sEst, th1);
                    if (allowRotation || Math.Abs(th2) <= angleTolRad) AddUniqueScaleAngle(outCandidates, sEst, th2);
                }
            }
        }

        private static SimilarityMatch2D BuildTransformFromPair(
            Point2d tplOrigin, Point2d tRef, Point2d cRef, double theta, double scale, bool mirrored)
        {
            double cosA = Math.Cos(theta);
            double sinA = Math.Sin(theta);
            double dx = (tRef.X - tplOrigin.X) * scale;
            double dy = (tRef.Y - tplOrigin.Y) * scale;
            if (mirrored) dy = -dy;
            Point2d targetOrigin = new Point2d(
                cRef.X - (cosA * dx - sinA * dy),
                cRef.Y - (sinA * dx + cosA * dy));
            return new SimilarityMatch2D
            {
                TemplateOrigin = tplOrigin,
                TargetOrigin = targetOrigin,
                Scale = scale,
                CosA = cosA,
                SinA = sinA,
                RotationRad = theta,
                MirroredY = mirrored
            };
        }

        private static void AddUniqueScaleAngle(List<ScaleAngleCandidate> list, double scale, double ang)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (Math.Abs(list[i].Scale - scale) < 0.02 && Math.Abs(NormalizeRadians(list[i].Theta - ang)) < 0.005)
                    return;
            }
            list.Add(new ScaleAngleCandidate(scale, ang));
        }

        private static double GetElemSizeTol(GeomShape t, double posTol)
        {
            return Math.Min(posTol, Math.Max(posTol * 0.25, t.FeatureSpan * 0.12));
        }

        private static void ComputeLongestPointSpan(GeomShape gs)
        {
            if (gs.Pts == null || gs.Pts.Length < 2) return;
            int n = gs.Pts.Length;
            int step = Math.Max(1, n / 48);
            double best = -1.0;
            Point2d pA = gs.Pts[0], pB = gs.Pts[1];
            for (int i = 0; i < n; i += step)
            {
                for (int j = i + step; j < n; j += step)
                {
                    double d = Dist2D(gs.Pts[i], gs.Pts[j]);
                    if (d > best) { best = d; pA = gs.Pts[i]; pB = gs.Pts[j]; }
                }
            }
            gs.LongestP0 = pA;
            gs.LongestP1 = pB;
            gs.LongestSpanLen = Math.Max(0.0, best);
        }

        private static double ComputeOpenPolylineLength(Point2d[] pts, bool closed)
        {
            if (pts == null || pts.Length < 2) return 0.0;
            double total = 0.0;
            int segs = closed ? pts.Length : (pts.Length - 1);
            for (int i = 0; i < segs; i++)
                total += Dist2D(pts[i], pts[(i + 1) % pts.Length]);
            return total;
        }

        private static Point2d ComputeWeightedCentroid(Point2d[] pts, bool closed)
        {
            if (pts == null || pts.Length == 0) return Point2d.Origin;
            if (pts.Length == 1) return pts[0];
            int segs = closed ? pts.Length : (pts.Length - 1);
            double wSum = 0.0, xSum = 0.0, ySum = 0.0;
            for (int i = 0; i < segs; i++)
            {
                Point2d a = pts[i];
                Point2d b = pts[(i + 1) % pts.Length];
                double d = Dist2D(a, b);
                if (d < 1e-9) continue;
                wSum += d;
                xSum += d * (a.X + b.X) * 0.5;
                ySum += d * (a.Y + b.Y) * 0.5;
            }
            if (wSum > 1e-9) return new Point2d(xSum / wSum, ySum / wSum);
            double sx = 0.0, sy = 0.0;
            foreach (Point2d p in pts) { sx += p.X; sy += p.Y; }
            return new Point2d(sx / pts.Length, sy / pts.Length);
        }

        private static double Dist2D(Point2d a, Point2d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static long MakeLenBucketKey(GeomCat cat, double len, double step)
        {
            long b = (long)Math.Floor(len / Math.Max(1e-6, step));
            return (((long)cat) << 40) ^ (b & 0xFFFFFFFFFFL);
        }

        private static long MakeCellKey(double x, double y, double cellSize)
        {
            int ix = (int)Math.Floor(x / cellSize);
            int iy = (int)Math.Floor(y / cellSize);
            return PackCellCoords(ix, iy);
        }

        private static long PackCellCoords(int ix, int iy)
        {
            return (((long)ix) << 32) | ((uint)iy);
        }

        private static double NormalizeRadians(double a)
        {
            while (a > Math.PI) a -= 2.0 * Math.PI;
            while (a <= -Math.PI) a += 2.0 * Math.PI;
            return a;
        }

        private static double NormalizeDegrees(double deg)
        {
            while (deg >= 360.0) deg -= 360.0;
            while (deg < 0.0) deg += 360.0;
            if (Math.Abs(deg - 360.0) < 1e-3 || Math.Abs(deg) < 1e-3) return 0.0;
            return deg;
        }

        private static void AddUniqueAngle(List<double> list, double ang)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (Math.Abs(NormalizeRadians(list[i] - ang)) < 0.005) return;
            }
            list.Add(ang);
        }

        private static void RecordUniqueAngle(List<double> degList, double deg)
        {
            double rounded = Math.Round(deg, 2);
            for (int i = 0; i < degList.Count; i++)
            {
                if (Math.Abs(degList[i] - rounded) < 0.05) return;
            }
            if (degList.Count < 16) degList.Add(rounded);
        }

        // ==================== Block Coordinate Extraction, Collision-Free Callouts & Schedule Table ====================

        private sealed class Rect2D
        {
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
            public Rect2D(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }
        }

        private sealed class Seg2D
        {
            public double X1;
            public double Y1;
            public double X2;
            public double Y2;
            public double Pad;
            public Seg2D(double x1, double y1, double x2, double y2, double pad)
            {
                X1 = x1;
                Y1 = y1;
                X2 = x2;
                Y2 = y2;
                Pad = pad;
            }
        }

        private sealed class SpatialGrid2D
        {
            public readonly double CellSize;
            public readonly List<Rect2D> Rects = new List<Rect2D>();
            public readonly List<Seg2D> Segs = new List<Seg2D>();
            private readonly Dictionary<long, List<int>> _rectCells = new Dictionary<long, List<int>>();
            private readonly Dictionary<long, List<int>> _segCells = new Dictionary<long, List<int>>();
            private int _stamp = 1;
            private int[] _rectStamp = new int[256];
            private int[] _segStamp = new int[256];

            public SpatialGrid2D(double cellSize)
            {
                CellSize = Math.Max(1e-3, cellSize);
            }

            private static long PackCell(int ix, int iy)
            {
                return (((long)ix) << 32) | ((uint)iy);
            }

            public void AddRect(double minX, double minY, double maxX, double maxY)
            {
                if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY)) return;
                if (maxX < minX || maxY < minY) return;
                int idx = Rects.Count;
                Rects.Add(new Rect2D(minX, minY, maxX, maxY));
                if (idx >= _rectStamp.Length) Array.Resize(ref _rectStamp, _rectStamp.Length * 2);

                int x0 = (int)Math.Floor(minX / CellSize);
                int y0 = (int)Math.Floor(minY / CellSize);
                int x1 = (int)Math.Floor(maxX / CellSize);
                int y1 = (int)Math.Floor(maxY / CellSize);
                // Guard against pathological giant rects filling thousands of cells
                if ((long)(x1 - x0 + 1) * (long)(y1 - y0 + 1) > 4000) return;

                for (int ix = x0; ix <= x1; ix++)
                {
                    for (int iy = y0; iy <= y1; iy++)
                    {
                        long key = PackCell(ix, iy);
                        List<int> bucket;
                        if (!_rectCells.TryGetValue(key, out bucket))
                        {
                            bucket = new List<int>();
                            _rectCells[key] = bucket;
                        }
                        bucket.Add(idx);
                    }
                }
            }

            public void AddSegment(double x1, double y1, double x2, double y2, double pad)
            {
                if (double.IsNaN(x1) || double.IsNaN(y1) || double.IsNaN(x2) || double.IsNaN(y2)) return;
                double dx = x2 - x1;
                double dy = y2 - y1;
                if (dx * dx + dy * dy < 1e-12) return;

                int idx = Segs.Count;
                Segs.Add(new Seg2D(x1, y1, x2, y2, pad));
                if (idx >= _segStamp.Length) Array.Resize(ref _segStamp, _segStamp.Length * 2);

                double minX = Math.Min(x1, x2) - pad;
                double minY = Math.Min(y1, y2) - pad;
                double maxX = Math.Max(x1, x2) + pad;
                double maxY = Math.Max(y1, y2) + pad;

                int cx0 = (int)Math.Floor(minX / CellSize);
                int cy0 = (int)Math.Floor(minY / CellSize);
                int cx1 = (int)Math.Floor(maxX / CellSize);
                int cy1 = (int)Math.Floor(maxY / CellSize);
                if ((long)(cx1 - cx0 + 1) * (long)(cy1 - cy0 + 1) > 2500)
                {
                    // Rasterize along segment cells via DDA steps so diagonal lines don't spam huge bboxes
                    int steps = Math.Min(600, Math.Max(2, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / CellSize) * 2));
                    HashSet<long> added = new HashSet<long>();
                    for (int s = 0; s <= steps; s++)
                    {
                        double t = (double)s / steps;
                        double px = x1 + dx * t;
                        double py = y1 + dy * t;
                        int bx0 = (int)Math.Floor((px - pad) / CellSize);
                        int by0 = (int)Math.Floor((py - pad) / CellSize);
                        int bx1 = (int)Math.Floor((px + pad) / CellSize);
                        int by1 = (int)Math.Floor((py + pad) / CellSize);
                        for (int ix = bx0; ix <= bx1; ix++)
                        {
                            for (int iy = by0; iy <= by1; iy++)
                            {
                                long key = PackCell(ix, iy);
                                if (!added.Add(key)) continue;
                                List<int> bucket;
                                if (!_segCells.TryGetValue(key, out bucket))
                                {
                                    bucket = new List<int>();
                                    _segCells[key] = bucket;
                                }
                                bucket.Add(idx);
                            }
                        }
                    }
                    return;
                }

                for (int ix = cx0; ix <= cx1; ix++)
                {
                    for (int iy = cy0; iy <= cy1; iy++)
                    {
                        long key = PackCell(ix, iy);
                        List<int> bucket;
                        if (!_segCells.TryGetValue(key, out bucket))
                        {
                            bucket = new List<int>();
                            _segCells[key] = bucket;
                        }
                        bucket.Add(idx);
                    }
                }
            }

            public int CountObstacleHits(double minX, double minY, double maxX, double maxY, double pad)
            {
                int hits = 0;
                int stamp = ++_stamp;
                double qMinX = minX - pad;
                double qMinY = minY - pad;
                double qMaxX = maxX + pad;
                double qMaxY = maxY + pad;

                int x0 = (int)Math.Floor(qMinX / CellSize);
                int y0 = (int)Math.Floor(qMinY / CellSize);
                int x1 = (int)Math.Floor(qMaxX / CellSize);
                int y1 = (int)Math.Floor(qMaxY / CellSize);

                for (int ix = x0; ix <= x1; ix++)
                {
                    for (int iy = y0; iy <= y1; iy++)
                    {
                        long key = PackCell(ix, iy);
                        List<int> rBucket;
                        if (_rectCells.TryGetValue(key, out rBucket))
                        {
                            for (int k = 0; k < rBucket.Count; k++)
                            {
                                int ri = rBucket[k];
                                if (_rectStamp[ri] == stamp) continue;
                                _rectStamp[ri] = stamp;
                                Rect2D r = Rects[ri];
                                if (!(r.MaxX < qMinX || r.MinX > qMaxX || r.MaxY < qMinY || r.MinY > qMaxY))
                                    hits++;
                            }
                        }
                        List<int> sBucket;
                        if (_segCells.TryGetValue(key, out sBucket))
                        {
                            for (int k = 0; k < sBucket.Count; k++)
                            {
                                int si = sBucket[k];
                                if (_segStamp[si] == stamp) continue;
                                _segStamp[si] = stamp;
                                Seg2D s = Segs[si];
                                if (SegmentIntersectsRect(s.X1, s.Y1, s.X2, s.Y2, qMinX - s.Pad, qMinY - s.Pad, qMaxX + s.Pad, qMaxY + s.Pad))
                                    hits++;
                            }
                        }
                    }
                }
                return hits;
            }
        }

        private static bool SegmentIntersectsRect(double x1, double y1, double x2, double y2, double minX, double minY, double maxX, double maxY)
        {
            if (Math.Max(x1, x2) < minX || Math.Min(x1, x2) > maxX ||
                Math.Max(y1, y2) < minY || Math.Min(y1, y2) > maxY)
                return false;

            double t0 = 0.0;
            double t1 = 1.0;
            double dx = x2 - x1;
            double dy = y2 - y1;
            double[] p = new double[] { -dx, dx, -dy, dy };
            double[] q = new double[] { x1 - minX, maxX - x1, y1 - minY, maxY - y1 };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12)
                {
                    if (q[i] < 0.0) return false;
                }
                else
                {
                    double t = q[i] / p[i];
                    if (p[i] < 0.0)
                    {
                        if (t > t1) return false;
                        if (t > t0) t0 = t;
                    }
                    else
                    {
                        if (t < t0) return false;
                        if (t < t1) t1 = t;
                    }
                }
            }
            return t0 <= t1;
        }

        private static bool RectsOverlap(double aMinX, double aMinY, double aMaxX, double aMaxY, double bMinX, double bMinY, double bMaxX, double bMaxY)
        {
            return !(aMaxX < bMinX || aMinX > bMaxX || aMaxY < bMinY || aMinY > bMaxY);
        }

        private static int Orient2D(double ax, double ay, double bx, double by, double cx, double cy)
        {
            double v = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Math.Abs(v) < 1e-9) return 0;
            return v > 0.0 ? 1 : -1;
        }

        private static bool OnSegment2D(double px, double py, double rx, double ry, double qx, double qy)
        {
            return rx <= Math.Max(px, qx) + 1e-9 && rx >= Math.Min(px, qx) - 1e-9 &&
                   ry <= Math.Max(py, qy) + 1e-9 && ry >= Math.Min(py, qy) - 1e-9;
        }

        private static bool SegmentsIntersect2D(double ax1, double ay1, double ax2, double ay2, double bx1, double by1, double bx2, double by2)
        {
            int o1 = Orient2D(ax1, ay1, ax2, ay2, bx1, by1);
            int o2 = Orient2D(ax1, ay1, ax2, ay2, bx2, by2);
            int o3 = Orient2D(bx1, by1, bx2, by2, ax1, ay1);
            int o4 = Orient2D(bx1, by1, bx2, by2, ax2, ay2);
            if (o1 != o2 && o3 != o4) return true;
            if (o1 == 0 && OnSegment2D(ax1, ay1, bx1, by1, ax2, ay2)) return true;
            if (o2 == 0 && OnSegment2D(ax1, ay1, bx2, by2, ax2, ay2)) return true;
            if (o3 == 0 && OnSegment2D(bx1, by1, ax1, ay1, bx2, by2)) return true;
            if (o4 == 0 && OnSegment2D(bx1, by1, ax2, ay2, bx2, by2)) return true;
            return false;
        }

        private static Extents3d? CollectLayerExtents(BlockTableRecord ms, Transaction tr, string layerName)
        {
            if (ms == null || tr == null || string.IsNullOrEmpty(layerName)) return null;
            Extents3d? res = null;
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !string.Equals(ent.Layer, layerName, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Extents3d ex = ent.GeometricExtents;
                    if (IsFiniteBounds(ex))
                    {
                        if (!res.HasValue) res = ex;
                        else { Extents3d cur = res.Value; cur.AddExtents(ex); res = cur; }
                    }
                }
                catch { }
            }
            return res;
        }

        private static void FeedObstacleEntity(
            Transaction tr,
            Entity ent,
            Matrix3d xform,
            SpatialGrid2D grid,
            double medSpan,
            double dwgSpan,
            double strokePad,
            int depth,
            ref int segBudget)
        {
            if (ent == null || segBudget <= 0 || depth > 3) return;
            double boxPad = Math.Max(strokePad, medSpan * 0.04);

            Line ln = ent as Line;
            if (ln != null)
            {
                Point3d p1 = ln.StartPoint.TransformBy(xform);
                Point3d p2 = ln.EndPoint.TransformBy(xform);
                grid.AddSegment(p1.X, p1.Y, p2.X, p2.Y, strokePad);
                segBudget--;
                return;
            }

            Polyline pl = ent as Polyline;
            if (pl != null)
            {
                int n = pl.NumberOfVertices;
                if (n < 2) return;
                int limit = pl.Closed ? n : (n - 1);
                double bMinX = double.MaxValue, bMinY = double.MaxValue, bMaxX = double.MinValue, bMaxY = double.MinValue;
                for (int i = 0; i < limit && segBudget > 0; i++)
                {
                    int j = (i + 1) % n;
                    Point3d a = pl.GetPoint3dAt(i).TransformBy(xform);
                    Point3d b = pl.GetPoint3dAt(j).TransformBy(xform);
                    if (a.X < bMinX) bMinX = a.X;
                    if (a.X > bMaxX) bMaxX = a.X;
                    if (a.Y < bMinY) bMinY = a.Y;
                    if (a.Y > bMaxY) bMaxY = a.Y;
                    if (b.X < bMinX) bMinX = b.X;
                    if (b.X > bMaxX) bMaxX = b.X;
                    if (b.Y < bMinY) bMinY = b.Y;
                    if (b.Y > bMaxY) bMaxY = b.Y;

                    double bulge = 0.0;
                    try { bulge = pl.GetBulgeAt(i); } catch { }
                    if (Math.Abs(bulge) > 1e-4)
                    {
                        try
                        {
                            CircularArc2d arc = pl.GetArcSegment2dAt(i);
                            double angSpan = arc.EndAngle - arc.StartAngle;
                            while (angSpan < 0.0) angSpan += Math.PI * 2.0;
                            int segs = 6;
                            Point2d prev = arc.EvaluatePoint(arc.StartAngle);
                            Point3d pPrev = new Point3d(prev.X, prev.Y, 0.0).TransformBy(xform);
                            for (int k = 1; k <= segs && segBudget > 0; k++)
                            {
                                double ang = arc.StartAngle + (angSpan * k / segs);
                                Point2d cur2 = arc.EvaluatePoint(ang);
                                Point3d pCur = new Point3d(cur2.X, cur2.Y, 0.0).TransformBy(xform);
                                grid.AddSegment(pPrev.X, pPrev.Y, pCur.X, pCur.Y, strokePad);
                                pPrev = pCur;
                                segBudget--;
                            }
                            continue;
                        }
                        catch { }
                    }
                    grid.AddSegment(a.X, a.Y, b.X, b.Y, strokePad);
                    segBudget--;
                }
                if (pl.Closed && bMaxX >= bMinX && (bMaxX - bMinX) <= medSpan * 0.9 && (bMaxY - bMinY) <= medSpan * 0.9)
                    grid.AddRect(bMinX - boxPad, bMinY - boxPad, bMaxX + boxPad, bMaxY + boxPad);
                return;
            }

            Polyline2d pl2 = ent as Polyline2d;
            if (pl2 != null)
            {
                List<Point3d> pts = new List<Point3d>();
                foreach (ObjectId vid in pl2)
                {
                    Vertex2d v = tr.GetObject(vid, OpenMode.ForRead) as Vertex2d;
                    if (v != null) pts.Add(v.Position.TransformBy(xform));
                }
                for (int i = 0; i + 1 < pts.Count && segBudget > 0; i++)
                {
                    grid.AddSegment(pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, strokePad);
                    segBudget--;
                }
                if (pl2.Closed && pts.Count >= 2 && segBudget > 0)
                {
                    grid.AddSegment(pts[pts.Count - 1].X, pts[pts.Count - 1].Y, pts[0].X, pts[0].Y, strokePad);
                    segBudget--;
                }
                return;
            }

            Circle cir = ent as Circle;
            if (cir != null)
            {
                Point3d c = cir.Center.TransformBy(xform);
                double rad = cir.Radius * Math.Max(Math.Abs(xform.CoordinateSystem3d.Xaxis.Length), Math.Abs(xform.CoordinateSystem3d.Yaxis.Length));
                if (rad * 2.0 <= medSpan * 1.4)
                {
                    grid.AddRect(c.X - rad - boxPad, c.Y - rad - boxPad, c.X + rad + boxPad, c.Y + rad + boxPad);
                    segBudget--;
                }
                else
                {
                    int nSeg = 20;
                    Point3d prev = new Point3d(cir.Center.X + cir.Radius, cir.Center.Y, 0.0).TransformBy(xform);
                    for (int k = 1; k <= nSeg && segBudget > 0; k++)
                    {
                        double ang = (Math.PI * 2.0 * k) / nSeg;
                        Point3d cur = new Point3d(cir.Center.X + cir.Radius * Math.Cos(ang), cir.Center.Y + cir.Radius * Math.Sin(ang), 0.0).TransformBy(xform);
                        grid.AddSegment(prev.X, prev.Y, cur.X, cur.Y, strokePad);
                        prev = cur;
                        segBudget--;
                    }
                }
                return;
            }

            Arc arcEnt = ent as Arc;
            if (arcEnt != null)
            {
                double sweep = arcEnt.EndAngle - arcEnt.StartAngle;
                while (sweep <= 0.0) sweep += Math.PI * 2.0;
                int nSeg = Math.Max(4, (int)Math.Ceiling(sweep / (Math.PI / 6.0)));
                Point3d prev = new Point3d(
                    arcEnt.Center.X + arcEnt.Radius * Math.Cos(arcEnt.StartAngle),
                    arcEnt.Center.Y + arcEnt.Radius * Math.Sin(arcEnt.StartAngle), 0.0).TransformBy(xform);
                for (int k = 1; k <= nSeg && segBudget > 0; k++)
                {
                    double ang = arcEnt.StartAngle + sweep * k / nSeg;
                    Point3d cur = new Point3d(
                        arcEnt.Center.X + arcEnt.Radius * Math.Cos(ang),
                        arcEnt.Center.Y + arcEnt.Radius * Math.Sin(ang), 0.0).TransformBy(xform);
                    grid.AddSegment(prev.X, prev.Y, cur.X, cur.Y, strokePad);
                    prev = cur;
                    segBudget--;
                }
                return;
            }

            BlockReference br = ent as BlockReference;
            if (br != null)
            {
                Extents3d bex;
                try { bex = ent.GeometricExtents; }
                catch { return; }
                if (!IsFiniteBounds(bex)) return;

                Point3d pMin = bex.MinPoint;
                Point3d pMax = bex.MaxPoint;
                if (xform != Matrix3d.Identity)
                {
                    Point3d[] corners = new Point3d[]
                    {
                        new Point3d(bex.MinPoint.X, bex.MinPoint.Y, 0.0).TransformBy(xform),
                        new Point3d(bex.MaxPoint.X, bex.MinPoint.Y, 0.0).TransformBy(xform),
                        new Point3d(bex.MaxPoint.X, bex.MaxPoint.Y, 0.0).TransformBy(xform),
                        new Point3d(bex.MinPoint.X, bex.MaxPoint.Y, 0.0).TransformBy(xform)
                    };
                    double tx1 = corners[0].X, tx2 = corners[0].X, ty1 = corners[0].Y, ty2 = corners[0].Y;
                    for (int ci = 1; ci < 4; ci++)
                    {
                        if (corners[ci].X < tx1) tx1 = corners[ci].X;
                        if (corners[ci].X > tx2) tx2 = corners[ci].X;
                        if (corners[ci].Y < ty1) ty1 = corners[ci].Y;
                        if (corners[ci].Y > ty2) ty2 = corners[ci].Y;
                    }
                    pMin = new Point3d(tx1, ty1, 0.0);
                    pMax = new Point3d(tx2, ty2, 0.0);
                }

                double bw = pMax.X - pMin.X;
                double bh = pMax.Y - pMin.Y;
                double bSpan = Math.Max(bw, bh);
                // Large enclosing blocks (titleblocks, cabinet outlines, whole panels) are recursively unpacked
                if (depth < 3 && bSpan > Math.Max(medSpan * 2.5, dwgSpan * 0.06) && segBudget > 50)
                {
                    ObjectId recId = GetBlockDefinitionId(br);
                    BlockTableRecord btr = (!recId.IsNull && !recId.IsErased) ? tr.GetObject(recId, OpenMode.ForRead) as BlockTableRecord : null;
                    if (btr != null && !btr.IsLayout)
                    {
                        Matrix3d combined = xform * br.BlockTransform;
                        foreach (ObjectId cid in btr)
                        {
                            if (segBudget <= 0) break;
                            Entity child = tr.GetObject(cid, OpenMode.ForRead) as Entity;
                            if (child == null || !child.Visible) continue;
                            FeedObstacleEntity(tr, child, combined, grid, medSpan, dwgSpan, strokePad, depth + 1, ref segBudget);
                        }
                        foreach (ObjectId attId in br.AttributeCollection)
                        {
                            try
                            {
                                AttributeReference ar = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                                if (ar == null || !ar.Visible) continue;
                                Extents3d aex = ar.GeometricExtents;
                                if (IsFiniteBounds(aex))
                                {
                                    Point3d ac1 = aex.MinPoint.TransformBy(xform);
                                    Point3d ac2 = aex.MaxPoint.TransformBy(xform);
                                    grid.AddRect(Math.Min(ac1.X, ac2.X) - boxPad, Math.Min(ac1.Y, ac2.Y) - boxPad,
                                                 Math.Max(ac1.X, ac2.X) + boxPad, Math.Max(ac1.Y, ac2.Y) + boxPad);
                                }
                            }
                            catch { }
                        }
                        return;
                    }
                }

                if (bw < dwgSpan * 0.85 && bh < dwgSpan * 0.85)
                {
                    grid.AddRect(pMin.X - boxPad, pMin.Y - boxPad, pMax.X + boxPad, pMax.Y + boxPad);
                    segBudget--;
                }
                return;
            }

            // General DBText / MText / Dimension / Solid / Hatch / Ellipse / Spline
            try
            {
                Extents3d ex = ent.GeometricExtents;
                if (!IsFiniteBounds(ex)) return;
                Point3d e1 = ex.MinPoint.TransformBy(xform);
                Point3d e2 = ex.MaxPoint.TransformBy(xform);
                double w = Math.Abs(e2.X - e1.X);
                double h = Math.Abs(e2.Y - e1.Y);
                bool isLargeFill = (ent is Hatch || ent is Solid) && (w > dwgSpan * 0.55 || h > dwgSpan * 0.55);
                if (!isLargeFill && w < dwgSpan * 0.88 && h < dwgSpan * 0.88)
                {
                    grid.AddRect(Math.Min(e1.X, e2.X) - boxPad, Math.Min(e1.Y, e2.Y) - boxPad,
                                 Math.Max(e1.X, e2.X) + boxPad, Math.Max(e1.Y, e2.Y) + boxPad);
                    segBudget--;
                }
            }
            catch { }
        }

        private static List<Entity> CreateLeaderEntities(
            Database db,
            Transaction tr,
            Dictionary<string, object> item,
            string targetLayer,
            int? defaultColor,
            out string warning)
        {
            warning = null;
            List<Entity> parts = new List<Entity>();
            string text = GetStr(item, "text") ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                warning = "Skipped leader with empty 'text'.";
                return parts;
            }

            Point3d targetPt = GetPoint3d(item, "target", GetPoint3d(item, "p1", GetPoint3d(item, "start", Point3d.Origin)));
            Point3d labelPt = GetPoint3d(item, "label_pos", GetPoint3d(item, "position", GetPoint3d(item, "p2", GetPoint3d(item, "end", new Point3d(targetPt.X + 200.0, targetPt.Y + 200.0, 0.0)))));
            double textH = Math.Max(1e-3, GetDouble(item, "height", 25.0));
            double explicitArrow = GetDouble(item, "arrow_size", textH * 1.45);
            int? leaderClr = GetNullableInt(item, "arrow_color_index") ?? GetNullableInt(item, "color_index") ?? defaultColor;
            int? textClr = GetNullableInt(item, "color_index") ?? defaultColor;
            ObjectId unicodeStyleId = EnsureUnicodeTextStyle(db, tr);

            MText mt = new MText();
            mt.SetDatabaseDefaults(db);
            mt.Layer = targetLayer;
            if (textClr.HasValue) mt.ColorIndex = ClampColor(textClr.Value);
            mt.TextStyleId = unicodeStyleId;
            mt.TextHeight = textH;
            mt.Width = Math.Max(textH * 8.0, GetDouble(item, "width", 0.0));
            mt.Contents = EscapeMTextPlain(text);
            try { mt.BackgroundFill = true; mt.UseBackgroundColor = true; mt.BackgroundScaleFactor = 1.15; } catch { }
            mt.Attachment = AttachmentPoint.BottomCenter;
            mt.Location = new Point3d(labelPt.X, labelPt.Y + textH * 0.22, 0.0);

            double textW = textH * Math.Max(4.0, text.Length * 0.65);
            try { if (mt.ActualWidth > 1e-4) textW = mt.ActualWidth; } catch { }

            double shelfHalf = textW * 0.52 + textH * 0.35;
            double shelfY = labelPt.Y;
            bool labelIsRight = labelPt.X >= targetPt.X;
            Point3d elbowPt = new Point3d(labelIsRight ? (labelPt.X - shelfHalf) : (labelPt.X + shelfHalf), shelfY, 0.0);
            Point3d shelfEnd = new Point3d(labelIsRight ? (labelPt.X + shelfHalf) : (labelPt.X - shelfHalf), shelfY, 0.0);

            Vector3d vToTip = targetPt - elbowPt;
            double totalDist = vToTip.Length;
            double arrowLen = Math.Max(textH * 0.9, Math.Min(explicitArrow, Math.Max(textH * 0.5, totalDist * 0.42)));
            Point3d leaderTip = new Point3d(targetPt.X, targetPt.Y, 0.0);
            Point3d leaderElbow = new Point3d(elbowPt.X, elbowPt.Y, 0.0);
            Point3d leaderShelfEnd = new Point3d(shelfEnd.X, shelfEnd.Y, 0.0);

            Leader ldr = new Leader();
            ldr.SetDatabaseDefaults(db);
            ldr.Layer = targetLayer;
            if (leaderClr.HasValue)
            {
                short aci = (short)ClampColor(leaderClr.Value);
                ldr.ColorIndex = aci;
                ldr.Dimclrd = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci);
            }
            ldr.DimensionStyle = db.Dimstyle;
            try { ldr.Dimldrblk = ObjectId.Null; } catch { }
            ldr.HasArrowHead = true;
            ldr.IsSplined = false;
            ldr.Dimasz = Math.Max(1e-4, arrowLen);
            ldr.AppendVertex(leaderTip);
            if (totalDist > 1e-6)
                ldr.AppendVertex(leaderElbow);
            ldr.AppendVertex(leaderShelfEnd);

            parts.Add(ldr);
            parts.Add(mt);
            return parts;
        }

        private sealed class SamplePointSpec
        {
            public double LocalX;
            public double LocalY;
            public double LocalZ;
            public string Label;
        }

        private sealed class CoordTargetItem
        {
            public int Index;
            public int BlockIndex;
            public int SubPointIndex;
            public string SubPointLabel;
            public int SortBand;
            public ObjectId Id;
            public string Handle;
            public string BlockName;
            public double ReportX;
            public double ReportY;
            public double CenterX;
            public double CenterY;
            public double InsertX;
            public double InsertY;
            public double ArrowX;
            public double ArrowY;
            public double LocalX;
            public double LocalY;
            public bool HasCustomPoint;
            public bool IsBlockRef;
            public Matrix3d BlockXform;
            public Extents3d Bounds;
            public string LabelText;
            public double LabelMinX;
            public double LabelMinY;
            public double LabelMaxX;
            public double LabelMaxY;
            public double ShelfX1;
            public double ShelfX2;
            public double ShelfY;
            public double ElbowX;
            public double ElbowY;
            public double TextCenterX;
            public double UsedTextHeight;
            public double UsedShelfOffset;
            public double UsedDoglegGap;
            public double UsedSpanHalf;
            public int ObstacleHits;
            public bool Placed;
        }

        private sealed class CalloutCandidate
        {
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
            public double ShelfX1;
            public double ShelfX2;
            public double ShelfY;
            public double ElbowX;
            public double ElbowY;
            public double TextCenterX;
            public double UsedTextHeight;
            public double UsedShelfOffset;
            public double UsedDoglegGap;
            public double UsedSpanHalf;
            public int ObstacleHits;
            public int CalloutHits;
            public int ShaftHitsTarget;
            public int ShaftCrossings;
            public double DistSq;
            public double AnglePrefPenalty;
            public double Score;
        }

        private static CalloutCandidate EvaluateCalloutCandidate(
            CoordTargetItem target,
            double boxCx,
            double boxCy,
            double boxW,
            double boxH,
            double textW,
            double textH,
            double padX,
            double shelfOffset,
            double textHeight,
            double doglegGap,
            double angPrefPenalty,
            SpatialGrid2D grid,
            List<CoordTargetItem> allTargets,
            List<CoordTargetItem> placedTargets)
        {
            double lx1 = boxCx - boxW * 0.5;
            double lx2 = boxCx + boxW * 0.5;
            double ly1 = boxCy - boxH * 0.5;
            double ly2 = boxCy + boxH * 0.5;
            double shelfY = ly1 + textHeight * 0.22;
            double shelfSpanHalf = Math.Max(textW * 0.54, boxW * 0.44);
            double textLeft = boxCx - shelfSpanHalf;
            double textRight = boxCx + shelfSpanHalf;
            bool isRight = boxCx >= target.ArrowX;

            double shelfStartX = isRight ? textLeft : textRight;
            double shelfEndX = isRight ? textRight : textLeft;
            double elbowX = isRight ? (shelfStartX - doglegGap) : (shelfStartX + doglegGap);
            double elbowY = shelfY;

            // Ensure leader shaft does NOT cut through its own text box
            double mMinX = boxCx - textW * 0.5 - padX * 0.25;
            double mMaxX = boxCx + textW * 0.5 + padX * 0.25;
            double mMinY = shelfY + shelfOffset * 0.4;
            double mMaxY = ly2;
            if (SegmentIntersectsRect(target.ArrowX, target.ArrowY, elbowX, elbowY, mMinX, mMinY, mMaxX, mMaxY))
            {
                // Flip elbow to whichever horizontal end keeps the shaft strictly outside own text rectangle
                double altStartX = isRight ? textRight : textLeft;
                double altEndX = isRight ? textLeft : textRight;
                double altElbowX = isRight ? (altStartX + doglegGap) : (altStartX - doglegGap);
                if (!SegmentIntersectsRect(target.ArrowX, target.ArrowY, altElbowX, elbowY, mMinX, mMinY, mMaxX, mMaxY))
                {
                    shelfStartX = altStartX;
                    shelfEndX = altEndX;
                    elbowX = altElbowX;
                }
                else
                {
                    return null;
                }
            }

            double boxMargin = textHeight * 0.24;
            int calloutHits = 0;
            int shaftCrossings = 0;
            double overallMinX = Math.Min(lx1, Math.Min(elbowX, Math.Min(shelfStartX, shelfEndX)));
            double overallMaxX = Math.Max(lx2, Math.Max(elbowX, Math.Max(shelfStartX, shelfEndX)));

            for (int p = 0; p < placedTargets.Count; p++)
            {
                CoordTargetItem other = placedTargets[p];
                if (!other.Placed || object.ReferenceEquals(other, target)) continue;
                if (RectsOverlap(overallMinX - boxMargin, ly1 - boxMargin, overallMaxX + boxMargin, ly2 + boxMargin,
                                 Math.Min(other.LabelMinX, Math.Min(other.ElbowX, other.ShelfX2)),
                                 other.LabelMinY,
                                 Math.Max(other.LabelMaxX, Math.Max(other.ElbowX, other.ShelfX2)),
                                 other.LabelMaxY))
                {
                    calloutHits++;
                }
                if (SegmentIntersectsRect(other.ArrowX, other.ArrowY, other.ElbowX, other.ElbowY,
                                          overallMinX - boxMargin * 0.5, ly1 - boxMargin * 0.5, overallMaxX + boxMargin * 0.5, ly2 + boxMargin * 0.5))
                {
                    calloutHits++;
                }
                if (SegmentIntersectsRect(target.ArrowX, target.ArrowY, elbowX, elbowY,
                                          other.LabelMinX - boxMargin * 0.6, other.LabelMinY - boxMargin * 0.6, other.LabelMaxX + boxMargin * 0.6, other.LabelMaxY + boxMargin * 0.6))
                {
                    calloutHits++;
                }
                if (SegmentsIntersect2D(target.ArrowX, target.ArrowY, elbowX, elbowY,
                                        other.ArrowX, other.ArrowY, other.ElbowX, other.ElbowY))
                {
                    shaftCrossings++;
                }
            }

            int shaftHitsTarget = 0;
            for (int t = 0; t < allTargets.Count; t++)
            {
                CoordTargetItem ot = allTargets[t];
                if (ot.Id == target.Id) continue;
                if (SegmentIntersectsRect(target.ArrowX, target.ArrowY, elbowX, elbowY,
                                          ot.Bounds.MinPoint.X, ot.Bounds.MinPoint.Y, ot.Bounds.MaxPoint.X, ot.Bounds.MaxPoint.Y))
                {
                    shaftHitsTarget++;
                }
            }

            int obsHits = grid.CountObstacleHits(overallMinX, ly1, overallMaxX, ly2, textHeight * 0.20);
            double dx = boxCx - target.ArrowX;
            double dy = boxCy - target.ArrowY;
            double distSq = dx * dx + dy * dy;
            double dist = Math.Sqrt(distSq);

            double score =
                calloutHits * 1e9 +
                obsHits * 1e6 +
                shaftCrossings * 1e4 +
                shaftHitsTarget * 1.5e3 +
                dist * (1.0 + angPrefPenalty);

            CalloutCandidate c = new CalloutCandidate();
            c.MinX = overallMinX;
            c.MinY = ly1;
            c.MaxX = overallMaxX;
            c.MaxY = ly2;
            c.ShelfX1 = Math.Min(elbowX, Math.Min(shelfStartX, shelfEndX));
            c.ShelfX2 = Math.Max(elbowX, Math.Max(shelfStartX, shelfEndX));
            c.ShelfY = shelfY;
            c.ElbowX = elbowX;
            c.ElbowY = elbowY;
            c.TextCenterX = boxCx;
            c.UsedTextHeight = textHeight;
            c.UsedShelfOffset = shelfOffset;
            c.UsedDoglegGap = doglegGap;
            c.UsedSpanHalf = shelfSpanHalf;
            c.ObstacleHits = obsHits;
            c.CalloutHits = calloutHits;
            c.ShaftHitsTarget = shaftHitsTarget;
            c.ShaftCrossings = shaftCrossings;
            c.DistSq = distSq;
            c.AnglePrefPenalty = angPrefPenalty;
            c.Score = score;
            return c;
        }

        private static CalloutCandidate FindBestCalloutSlot(
            CoordTargetItem target,
            double boxW,
            double boxH,
            double textW,
            double textH,
            double padX,
            double shelfOffset,
            double textHeight,
            double doglegGap,
            double medSpan,
            SpatialGrid2D grid,
            List<CoordTargetItem> allTargets,
            List<CoordTargetItem> placedTargets)
        {
            // Balanced angles prioritizing above, upper-diagonals, below, lower-diagonals, sides
            double[] anglesDeg = new double[]
            {
                90.0, 68.0, 112.0, -90.0, -68.0, -112.0,
                45.0, 135.0, -45.0, -135.0, 22.0, 158.0, -22.0, -158.0,
                0.0, 180.0, 79.0, 101.0, -79.0, -101.0, 56.0, 124.0, -56.0, -124.0
            };
            double[] anglePen = new double[]
            {
                0.00, 0.02, 0.02, 0.04, 0.06, 0.06,
                0.05, 0.05, 0.08, 0.08, 0.10, 0.10, 0.12, 0.12,
                0.14, 0.14, 0.02, 0.02, 0.06, 0.06, 0.04, 0.04, 0.07, 0.07
            };

            double bw = Math.Max(1e-3, target.Bounds.MaxPoint.X - target.Bounds.MinPoint.X);
            double bh = Math.Max(1e-3, target.Bounds.MaxPoint.Y - target.Bounds.MinPoint.Y);
            double baseClear = Math.Max(medSpan * 0.55, Math.Max(bw, bh) * 0.55) + Math.Max(boxH, boxW) * 0.52;
            double tierStep = Math.Max(boxH * 1.18, medSpan * 0.85);
            double[] tierMults = new double[] { 0.0, 0.85, 1.75, 2.75, 3.85, 5.1, 6.6, 8.4, 10.6, 13.2, 16.2 };

            CalloutCandidate best = null;
            for (int ti = 0; ti < tierMults.Length; ti++)
            {
                double radius = baseClear + tierMults[ti] * tierStep;
                for (int ai = 0; ai < anglesDeg.Length; ai++)
                {
                    double rad = anglesDeg[ai] * Math.PI / 180.0;
                    double cx = target.ArrowX + radius * Math.Cos(rad);
                    double cy = target.ArrowY + radius * Math.Sin(rad);
                    CalloutCandidate cand = EvaluateCalloutCandidate(
                        target, cx, cy, boxW, boxH, textW, textH, padX, shelfOffset, textHeight, doglegGap,
                        anglePen[ai], grid, allTargets, placedTargets);
                    if (cand != null && (best == null || cand.Score < best.Score))
                        best = cand;
                }
                // Early exit once we found a completely obstacle-free, callout-free, uncrossed close slot!
                if (best != null && best.CalloutHits == 0 && best.ObstacleHits == 0 && best.ShaftCrossings == 0 && ti >= 1)
                    break;
            }

            // Local Cartesian pocket scan if radial scan still had drawing obstacle hits
            if (best == null || best.CalloutHits > 0 || best.ObstacleHits > 0)
            {
                double scanSpanX = Math.Max(tierStep * 5.0, boxW * 3.5);
                double scanSpanY = Math.Max(tierStep * 6.5, boxH * 6.0);
                double stepX = Math.Max(boxW * 0.42, medSpan * 0.55);
                double stepY = Math.Max(boxH * 0.55, medSpan * 0.55);
                for (double dy = -scanSpanY; dy <= scanSpanY; dy += stepY)
                {
                    for (double dx = -scanSpanX; dx <= scanSpanX; dx += stepX)
                    {
                        if (dx * dx + dy * dy < baseClear * baseClear * 0.45) continue;
                        CalloutCandidate cand = EvaluateCalloutCandidate(
                            target, target.ArrowX + dx, target.ArrowY + dy, boxW, boxH, textW, textH,
                            padX, shelfOffset, textHeight, doglegGap, 0.05, grid, allTargets, placedTargets);
                        if (cand != null && (best == null || cand.Score < best.Score))
                            best = cand;
                    }
                }
            }

            return best;
        }

        private static readonly object s_lastCoordLock = new object();
        private static readonly Dictionary<IntPtr, string> s_lastCoordSortOrderByDb = new Dictionary<IntPtr, string>();
        private static readonly Dictionary<IntPtr, string> s_lastCoordBlockNameByDb = new Dictionary<IntPtr, string>();
        private static readonly Dictionary<IntPtr, string> s_lastMultiPointModeByDb = new Dictionary<IntPtr, string>();
        private static readonly Dictionary<IntPtr, Dictionary<string, List<SamplePointSpec>>> s_lastBlockSamplePointsByDb =
            new Dictionary<IntPtr, Dictionary<string, List<SamplePointSpec>>>();
        private static readonly Dictionary<IntPtr, ExcelSheetData> s_lastCoordTableByDb = new Dictionary<IntPtr, ExcelSheetData>();
        private static readonly Dictionary<IntPtr, ExcelSheetData> s_lastLegendTableByDb = new Dictionary<IntPtr, ExcelSheetData>();
        private static readonly Dictionary<IntPtr, ExcelSheetData> s_lastPanelScheduleByDb = new Dictionary<IntPtr, ExcelSheetData>();
        private static readonly Dictionary<IntPtr, ExcelSheetData> s_lastBoqTableByDb = new Dictionary<IntPtr, ExcelSheetData>();
        private static readonly Dictionary<IntPtr, ExcelSheetData> s_lastLightingTableByDb = new Dictionary<IntPtr, ExcelSheetData>();
        private static readonly Dictionary<IntPtr, string> s_lastGeneratedTableKindByDb = new Dictionary<IntPtr, string>();
        private static readonly Dictionary<IntPtr, List<string>> s_lastUserCurveHandlesByDb = new Dictionary<IntPtr, List<string>>();

        internal static void RecordUserSelectionSnapshot(Document doc, List<string> handles)
        {
            if (doc == null || handles == null || handles.Count == 0) return;
            try
            {
                Database db = doc.Database;
                List<string> curveHandles = new List<string>();
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    for (int i = 0; i < handles.Count; i++)
                    {
                        ObjectId oid = ResolveHandle(db, handles[i]);
                        if (oid.IsNull || oid.IsErased) continue;
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent is Curve) curveHandles.Add(ent.Handle.ToString());
                    }
                    tr.Commit();
                }
                if (curveHandles.Count > 0)
                {
                    lock (s_lastCoordLock)
                    {
                        s_lastUserCurveHandlesByDb[db.UnmanagedObject] = curveHandles;
                    }
                }
            }
            catch { }
        }

        private static List<string> GetValidCachedUserCurveHandles(Database db)
        {
            List<string> result = new List<string>();
            if (db == null) return result;
            List<string> raw = null;
            lock (s_lastCoordLock)
            {
                s_lastUserCurveHandlesByDb.TryGetValue(db.UnmanagedObject, out raw);
            }
            if (raw == null || raw.Count == 0) return result;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    for (int i = 0; i < raw.Count; i++)
                    {
                        ObjectId oid = ResolveHandle(db, raw[i]);
                        if (oid.IsNull || oid.IsErased) continue;
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent is Curve) result.Add(ent.Handle.ToString());
                    }
                    tr.Commit();
                }
            }
            catch { }
            return result;
        }

        private static string NormalizeCoordSortOrder(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim().ToLowerInvariant()
                .Replace("→", "_to_").Replace("←", "_to_").Replace("->", "_to_")
                .Replace("↓", "_to_").Replace("↑", "_to_")
                .Replace("إلى", "الي").Replace("إ", "ا").Replace("أ", "ا").Replace("آ", "ا").Replace("ى", "ي")
                .Replace("-", "_").Replace(" ", "_");
            while (s.Contains("__")) s = s.Replace("__", "_");
            s = s.Trim('_');

            if (s == "ask" || s == "auto" || s == "prompt" || s == "user_choice" || s == "حسب_الشات" || s == "حسب_الطلب")
                return "";

            if (s == "right_to_left_bottom_to_top" || s == "right_to_left_bt" || s == "rtl_bt" ||
                (s.Contains("يمين") && (s.Contains("تحت") || s.Contains("اسفل")) && !s.StartsWith("من_تحت") && !s.StartsWith("من_اسفل")))
                return "right_to_left_bottom_to_top";
            if (s == "left_to_right_bottom_to_top" || s == "left_to_right_bt" || s == "ltr_bt" ||
                ((s.Contains("شمال") || s.Contains("يسار")) && (s.Contains("تحت") || s.Contains("اسفل")) && !s.StartsWith("من_تحت") && !s.StartsWith("من_اسفل")))
                return "left_to_right_bottom_to_top";

            if (s == "top_to_bottom_rtl" || s == "top_to_bottom_right_to_left" || s == "row_major_top_to_bottom_rtl" ||
                s == "ttb_rtl" || ((s.Contains("فوق") || s.Contains("اعلي")) && s.Contains("يمين")))
                return "top_to_bottom_rtl";
            if (s == "bottom_to_top_rtl" || s == "bottom_to_top_right_to_left" || s == "row_major_bottom_to_top_rtl" ||
                s == "btt_rtl" || ((s.Contains("تحت") || s.Contains("اسفل")) && s.Contains("يمين")))
                return "bottom_to_top_rtl";

            if (s == "right_to_left" || s == "right_to_left_top_to_bottom" || s == "rtl" || s == "right" ||
                s.StartsWith("من_اليمين") || s == "يمين_لشمال" || s == "يمين_ليسار" || s == "اليمين" || s == "يمين")
                return "right_to_left";

            if (s == "top_to_bottom" || s == "top_to_bottom_ltr" || s == "row_major_top_to_bottom" || s == "ttb" || s == "top" ||
                s.StartsWith("من_فوق") || s.StartsWith("من_اعلي") || s.StartsWith("من_الاعلي") || s == "فوق_لتحت" || s == "فوق" || s == "اعلي")
                return "top_to_bottom";

            if (s == "bottom_to_top" || s == "bottom_to_top_ltr" || s == "row_major_bottom_to_top" || s == "btt" || s == "bottom" ||
                s.StartsWith("من_تحت") || s.StartsWith("من_اسفل") || s.StartsWith("من_الاسفل") || s == "تحت_لفوق" || s == "تحت" || s == "اسفل")
                return "bottom_to_top";

            if (s == "left_to_right" || s == "left_to_right_top_to_bottom" || s == "ltr" || s == "left" ||
                s.StartsWith("من_اليسار") || s.StartsWith("من_الشمال") || s == "شمال_ليمين" || s == "يسار_ليمين" || s == "الشمال" || s == "اليسار" || s == "شمال" || s == "يسار")
                return "left_to_right";

            return "";
        }

        private static string DescribeCoordSortOrderAr(string sortOrder)
        {
            switch (sortOrder)
            {
                case "right_to_left": return "من اليمين إلى الشمال (عمودي)";
                case "right_to_left_bottom_to_top": return "من اليمين إلى الشمال (من أسفل لأعلى)";
                case "top_to_bottom": return "من فوق إلى تحت (صفوف: يسار إلى يمين)";
                case "top_to_bottom_rtl": return "من فوق إلى تحت (صفوف: يمين إلى يسار)";
                case "bottom_to_top": return "من تحت إلى فوق (صفوف: يسار إلى يمين)";
                case "bottom_to_top_rtl": return "من تحت إلى فوق (صفوف: يمين إلى يسار)";
                case "left_to_right_bottom_to_top": return "من الشمال إلى اليمين (من أسفل لأعلى)";
                default: return "من الشمال إلى اليمين (عمودي)";
            }
        }

        private static void SortCoordTargets(List<CoordTargetItem> targets, string sortOrder, double medW, double medH)
        {
            if (targets == null || targets.Count <= 1) return;

            bool primaryY =
                sortOrder == "top_to_bottom" || sortOrder == "top_to_bottom_rtl" ||
                sortOrder == "bottom_to_top" || sortOrder == "bottom_to_top_rtl";
            bool primaryDesc =
                sortOrder == "right_to_left" || sortOrder == "right_to_left_bottom_to_top" ||
                sortOrder == "top_to_bottom" || sortOrder == "top_to_bottom_rtl";
            bool secondaryDesc =
                primaryY
                    ? (sortOrder == "top_to_bottom_rtl" || sortOrder == "bottom_to_top_rtl")
                    : (sortOrder == "left_to_right" || sortOrder == "right_to_left");

            double bandTol = primaryY ? Math.Max(1e-3, medH * 0.45) : Math.Max(1e-3, medW * 0.25);

            // Step 1: Strictly order along primary axis and assign discrete 1D cluster Band IDs
            // (guarantees strict weak ordering without non-transitive tolerance comparator bugs).
            targets.Sort(delegate(CoordTargetItem a, CoordTargetItem b)
            {
                double pa = primaryY ? a.ArrowY : a.ArrowX;
                double pb = primaryY ? b.ArrowY : b.ArrowX;
                int pc = primaryDesc ? pb.CompareTo(pa) : pa.CompareTo(pb);
                if (pc != 0) return pc;
                double sa = primaryY ? a.ArrowX : a.ArrowY;
                double sb = primaryY ? b.ArrowX : b.ArrowY;
                int sc = secondaryDesc ? sb.CompareTo(sa) : sa.CompareTo(sb);
                if (sc != 0) return sc;
                return string.Compare(a.Handle ?? "", b.Handle ?? "", StringComparison.Ordinal);
            });

            int band = 0;
            double anchor = primaryY ? targets[0].ArrowY : targets[0].ArrowX;
            targets[0].SortBand = 0;
            for (int i = 1; i < targets.Count; i++)
            {
                double val = primaryY ? targets[i].ArrowY : targets[i].ArrowX;
                if (Math.Abs(val - anchor) > bandTol)
                {
                    band++;
                    anchor = val;
                }
                targets[i].SortBand = band;
            }

            // Step 2: Strict total-order sort by (SortBand, SecondaryAxis, PrimaryAxis, Handle)
            targets.Sort(delegate(CoordTargetItem a, CoordTargetItem b)
            {
                int c = a.SortBand.CompareTo(b.SortBand);
                if (c != 0) return c;
                double sa = primaryY ? a.ArrowX : a.ArrowY;
                double sb = primaryY ? b.ArrowX : b.ArrowY;
                int sc = secondaryDesc ? sb.CompareTo(sa) : sa.CompareTo(sb);
                if (sc != 0) return sc;
                double pa = primaryY ? a.ArrowY : a.ArrowX;
                double pb = primaryY ? b.ArrowY : b.ArrowX;
                int pc = primaryDesc ? pb.CompareTo(pa) : pa.CompareTo(pb);
                if (pc != 0) return pc;
                return string.Compare(a.Handle ?? "", b.Handle ?? "", StringComparison.Ordinal);
            });
        }

        private static string NormalizeMultiPointMode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            while (s.Contains("__")) s = s.Replace("__", "_");
            s = s.Trim('_');
            if (s == "grouped" || s == "block_rows" || s == "rows" || s == "per_block" ||
                s == "subrows" || s == "sub_rows" || s == "block_subrows" ||
                s.Contains("صفوف") || s.Contains("امام_رقم_البلوك") || s.Contains("أمام_رقم_البلوك"))
                return "grouped";
            if (s == "sequential" || s == "seq" || s == "points" || s == "continuous" ||
                s == "per_point" || s.Contains("متسلسل") || s.Contains("وراء_بعض") || s.Contains("ورا_بعض"))
                return "sequential";
            return "";
        }

        private static List<SamplePointSpec> ParseSamplePointList(Dictionary<string, object> args, string key)
        {
            List<SamplePointSpec> list = new List<SamplePointSpec>();
            if (args == null || !args.ContainsKey(key) || args[key] == null) return list;
            IEnumerable seq = args[key] as IEnumerable;
            if (seq == null || args[key] is string) return list;
            int idx = 1;
            foreach (object obj in seq)
            {
                if (obj == null) continue;
                Dictionary<string, object> dict = obj as Dictionary<string, object>;
                if (dict != null)
                {
                    double x = GetDouble(dict, "x", double.NaN);
                    double y = GetDouble(dict, "y", double.NaN);
                    double z = GetDouble(dict, "z", 0.0);
                    if (!double.IsNaN(x) && !double.IsNaN(y) && !double.IsInfinity(x) && !double.IsInfinity(y))
                    {
                        SamplePointSpec sp = new SamplePointSpec();
                        sp.LocalX = x;
                        sp.LocalY = y;
                        sp.LocalZ = double.IsNaN(z) || double.IsInfinity(z) ? 0.0 : z;
                        sp.Label = GetStr(dict, "label") ?? ("P" + idx.ToString(CultureInfo.InvariantCulture));
                        list.Add(sp);
                        idx++;
                    }
                    continue;
                }

                IList arr = obj as IList;
                if (arr != null && arr.Count >= 2)
                {
                    double x, y, z = 0.0;
                    if (TryParseObjDouble(arr[0], out x) && TryParseObjDouble(arr[1], out y) &&
                        !double.IsNaN(x) && !double.IsNaN(y) && !double.IsInfinity(x) && !double.IsInfinity(y))
                    {
                        if (arr.Count >= 3) TryParseObjDouble(arr[2], out z);
                        SamplePointSpec sp = new SamplePointSpec();
                        sp.LocalX = x;
                        sp.LocalY = y;
                        sp.LocalZ = double.IsNaN(z) || double.IsInfinity(z) ? 0.0 : z;
                        sp.Label = "P" + idx.ToString(CultureInfo.InvariantCulture);
                        list.Add(sp);
                        idx++;
                    }
                }
            }
            return list;
        }

        private static bool TryParseObjDouble(object obj, out double val)
        {
            val = 0.0;
            if (obj == null || obj is bool) return false;
            try
            {
                val = Convert.ToDouble(obj, CultureInfo.InvariantCulture);
                return !double.IsNaN(val) && !double.IsInfinity(val);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryInvertMatrix3d(Matrix3d m, out Matrix3d inv)
        {
            inv = Matrix3d.Identity;
            try
            {
                inv = m.Inverse();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private const string CoordLeaderRegApp = "AIENG_COORD_LDR";

        private static void EnsureRegApp(Database db, Transaction tr, string appName)
        {
            if (db == null || tr == null || string.IsNullOrWhiteSpace(appName)) return;
            RegAppTable rat = tr.GetObject(db.RegAppTableId, OpenMode.ForRead) as RegAppTable;
            if (rat == null || rat.Has(appName)) return;
            rat.UpgradeOpen();
            RegAppTableRecord rec = new RegAppTableRecord();
            rec.Name = appName;
            rat.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        private static void AttachCoordLeaderXData(Database db, Transaction tr, Leader ldr, CoordTargetItem item)
        {
            if (ldr == null || item == null) return;
            try
            {
                EnsureRegApp(db, tr, CoordLeaderRegApp);
                ResultBuffer rb = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, CoordLeaderRegApp),
                    new TypedValue((int)DxfCode.ExtendedDataInteger32, item.Index),
                    new TypedValue((int)DxfCode.ExtendedDataInteger32, item.BlockIndex),
                    new TypedValue((int)DxfCode.ExtendedDataInteger32, item.SubPointIndex),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, item.Handle ?? ""),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, item.BlockName ?? "")
                );
                ldr.XData = rb;
            }
            catch { }
        }

        private static bool ReadCoordLeaderXData(
            Leader ldr,
            out int index,
            out int blockIndex,
            out int subIndex,
            out string blockHandle,
            out string blockName)
        {
            index = 0;
            blockIndex = 0;
            subIndex = 0;
            blockHandle = "";
            blockName = "";
            if (ldr == null) return false;
            try
            {
                ResultBuffer rb = ldr.GetXDataForApplication(CoordLeaderRegApp);
                if (rb == null) return false;
                TypedValue[] vals = rb.AsArray();
                int intPos = 0;
                int strPos = 0;
                for (int i = 0; i < vals.Length; i++)
                {
                    if (vals[i].TypeCode == (int)DxfCode.ExtendedDataInteger32 || vals[i].TypeCode == (int)DxfCode.ExtendedDataInteger16)
                    {
                        int v = Convert.ToInt32(vals[i].Value, CultureInfo.InvariantCulture);
                        if (intPos == 0) index = v;
                        else if (intPos == 1) blockIndex = v;
                        else if (intPos == 2) subIndex = v;
                        intPos++;
                    }
                    else if (vals[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    {
                        string sv = Convert.ToString(vals[i].Value, CultureInfo.InvariantCulture) ?? "";
                        if (strPos == 0) blockHandle = sv;
                        else if (strPos == 1) blockName = sv;
                        strPos++;
                    }
                }
                return index > 0;
            }
            catch { return false; }
        }

        private static bool ParseLeadingCalloutIndex(string rawText, out int calloutIndex, out string headerToken)
        {
            calloutIndex = 0;
            headerToken = "";
            if (string.IsNullOrWhiteSpace(rawText)) return false;
            string firstLine = rawText.Trim().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (string.IsNullOrEmpty(firstLine)) return false;
            int spaceIdx = firstLine.IndexOf(' ');
            headerToken = (spaceIdx > 0) ? firstLine.Substring(0, spaceIdx).Trim() : firstLine;

            int pos = 0;
            while (pos < headerToken.Length && !char.IsDigit(headerToken[pos])) pos++;
            int start = pos;
            while (pos < headerToken.Length && char.IsDigit(headerToken[pos])) pos++;
            if (pos == start) return false;
            int val;
            if (int.TryParse(headerToken.Substring(start, pos - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out val) && val > 0)
            {
                calloutIndex = val;
                return true;
            }
            return false;
        }

        private static bool PromptPickReferenceLeader(Document doc, out ObjectId pickedLeaderId, out string errorMsg)
        {
            pickedLeaderId = ObjectId.Null;
            errorMsg = "";
            try
            {
                int cmdActive = Convert.ToInt32(
                    Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CMDACTIVE"),
                    CultureInfo.InvariantCulture);
                if (cmdActive != 0)
                {
                    errorMsg = "An AutoCAD command is currently active (CMDACTIVE=" + cmdActive + "). Press Esc in AutoCAD first, then select the reference Leader.";
                    return false;
                }
            }
            catch { }

            TrySetFocusToDwgView(doc);

            PromptEntityOptions peo = new PromptEntityOptions(
                "\n[AI-Engineer] Click the reference Leader arrow whose position inside the block should be applied to all matching blocks: ");
            peo.SetRejectMessage("\nSelect a Leader entity.");
            peo.AddAllowedClass(typeof(Leader), false);
            PromptEntityResult per = doc.Editor.GetEntity(peo);
            if (per.Status != PromptStatus.OK || per.ObjectId.IsNull)
            {
                errorMsg = "Reference Leader selection was cancelled.";
                return false;
            }
            pickedLeaderId = per.ObjectId;
            return true;
        }

        private static double DistPointToExtents2D(double px, double py, Extents3d ex)
        {
            double dx = (px < ex.MinPoint.X) ? (ex.MinPoint.X - px) : (px > ex.MaxPoint.X ? px - ex.MaxPoint.X : 0.0);
            double dy = (py < ex.MinPoint.Y) ? (ex.MinPoint.Y - py) : (py > ex.MaxPoint.Y ? py - ex.MaxPoint.Y : 0.0);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private sealed class ExistingCalloutLeaderInfo
        {
            public Leader Ldr;
            public int Index;
            public string LabelToken;
            public string BlockHandle;
            public string BlockName;
            public Point3d TipPt;
            public Point3d LastPt;
        }

        private sealed class LabelAnchorInfo
        {
            public int Index;
            public string Token;
            public Point3d Pos;
        }

        private static bool ResolveReferenceLeadersToSamplePoints(
            Database db,
            Transaction tr,
            BlockTableRecord ms,
            string calloutLayer,
            string blockFilter,
            List<string> selectedHandles,
            List<int> refIndices,
            string refLabel,
            bool fromSelectedLeader,
            Dictionary<string, bool> layerHiddenFlags,
            bool visibleOnly,
            bool includeAnonymous,
            bool includeSystem,
            out string inferredBlockName,
            out List<SamplePointSpec> resolvedLocalPoints,
            out string errorMsg)
        {
            inferredBlockName = "";
            resolvedLocalPoints = new List<SamplePointSpec>();
            errorMsg = "";

            List<ExistingCalloutLeaderInfo> leaders = new List<ExistingCalloutLeaderInfo>();
            List<LabelAnchorInfo> labelAnchors = new List<LabelAnchorInfo>();
            HashSet<ObjectId> selectedIdSet = new HashSet<ObjectId>();
            if (selectedHandles != null)
            {
                foreach (string h in selectedHandles)
                {
                    ObjectId sid = ResolveHandle(db, h);
                    if (!sid.IsNull && !sid.IsErased) selectedIdSet.Add(sid);
                }
            }

            List<ExistingCalloutLeaderInfo> selectedLeaders = new List<ExistingCalloutLeaderInfo>();
            foreach (ObjectId mid in ms)
            {
                if (mid.IsErased) continue;
                Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                bool onCalloutLayer = string.Equals(ent.Layer ?? "", calloutLayer, StringComparison.OrdinalIgnoreCase);
                bool isSelected = selectedIdSet.Contains(mid);
                if (!onCalloutLayer && !isSelected) continue;

                Leader ldr = ent as Leader;
                if (ldr != null && ldr.NumVertices >= 2)
                {
                    ExistingCalloutLeaderInfo info = new ExistingCalloutLeaderInfo();
                    info.Ldr = ldr;
                    info.TipPt = ldr.VertexAt(0);
                    info.LastPt = ldr.VertexAt(ldr.NumVertices - 1);
                    int xIdx, xBlkIdx, xSubIdx;
                    string xHandle, xName;
                    if (ReadCoordLeaderXData(ldr, out xIdx, out xBlkIdx, out xSubIdx, out xHandle, out xName))
                    {
                        info.Index = xIdx;
                        info.BlockHandle = xHandle;
                        info.BlockName = xName;
                    }
                    leaders.Add(info);
                    if (isSelected) selectedLeaders.Add(info);
                    continue;
                }

                if (onCalloutLayer)
                {
                    MText mt = ent as MText;
                    if (mt != null)
                    {
                        int parsedIdx;
                        string tok;
                        if (ParseLeadingCalloutIndex(mt.Text, out parsedIdx, out tok) || !string.IsNullOrWhiteSpace(tok))
                        {
                            LabelAnchorInfo la = new LabelAnchorInfo();
                            la.Index = parsedIdx;
                            la.Token = tok ?? "";
                            la.Pos = mt.Location;
                            labelAnchors.Add(la);
                        }
                    }
                    else
                    {
                        DBText dt = ent as DBText;
                        if (dt != null)
                        {
                            int parsedIdx;
                            string tok;
                            if (ParseLeadingCalloutIndex(dt.TextString, out parsedIdx, out tok) || !string.IsNullOrWhiteSpace(tok))
                            {
                                LabelAnchorInfo la = new LabelAnchorInfo();
                                la.Index = parsedIdx;
                                la.Token = tok ?? "";
                                la.Pos = dt.Position;
                                labelAnchors.Add(la);
                            }
                        }
                    }
                }
            }

            // Pair Leaders with nearest MText label token & index
            HashSet<int> usedLabelPos = new HashSet<int>();
            for (int i = 0; i < leaders.Count; i++)
            {
                if (leaders[i].Index > 0 && !string.IsNullOrEmpty(leaders[i].LabelToken)) continue;
                int bestLabelIdx = -1;
                double bestDist = double.MaxValue;
                for (int li = 0; li < labelAnchors.Count; li++)
                {
                    if (usedLabelPos.Contains(li)) continue;
                    double d = leaders[i].LastPt.DistanceTo(labelAnchors[li].Pos);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestLabelIdx = li;
                    }
                }
                if (bestLabelIdx >= 0)
                {
                    if (leaders[i].Index <= 0) leaders[i].Index = labelAnchors[bestLabelIdx].Index;
                    leaders[i].LabelToken = labelAnchors[bestLabelIdx].Token;
                    usedLabelPos.Add(bestLabelIdx);
                }
            }

            List<ExistingCalloutLeaderInfo> chosenLeaders = new List<ExistingCalloutLeaderInfo>();
            if (!string.IsNullOrWhiteSpace(refLabel))
            {
                string normTarget = refLabel.Trim().TrimStart('#');
                ExistingCalloutLeaderInfo match = null;
                for (int i = 0; i < leaders.Count; i++)
                {
                    string tok = (leaders[i].LabelToken ?? "").Trim().TrimStart('#');
                    if (string.Equals(tok, normTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        match = leaders[i];
                        break;
                    }
                }
                if (match == null)
                {
                    errorMsg = "Could not find an existing coordinate Leader labeled '" + refLabel +
                               "' on layer '" + calloutLayer + "'. Select the reference Leader directly in AutoCAD and try again.";
                    return false;
                }
                chosenLeaders.Add(match);
            }
            else if (refIndices != null && refIndices.Count > 0)
            {
                for (int r = 0; r < refIndices.Count; r++)
                {
                    int targetIdx = refIndices[r];
                    ExistingCalloutLeaderInfo match = null;
                    for (int i = 0; i < leaders.Count; i++)
                    {
                        if (leaders[i].Index == targetIdx) { match = leaders[i]; break; }
                    }
                    if (match == null)
                    {
                        errorMsg = "Could not find an existing coordinate Leader callout #" +
                                   targetIdx.ToString(CultureInfo.InvariantCulture) +
                                   " on layer '" + calloutLayer + "'. You can click/select the reference Leader directly in AutoCAD (or use pick_reference_leader) even if its numbering is different.";
                        return false;
                    }
                    chosenLeaders.Add(match);
                }
            }
            else if (fromSelectedLeader || selectedLeaders.Count > 0)
            {
                if (selectedLeaders.Count == 0)
                {
                    errorMsg = "No Leader entity is currently selected to use as the reference arrowhead position.";
                    return false;
                }
                chosenLeaders.AddRange(selectedLeaders);
            }
            else
            {
                errorMsg = "No reference callout index, label, or selected Leader was provided.";
                return false;
            }

            // Collect candidate BlockReferences to map each reference Leader tip to its owning BlockReference
            List<BlockReference> candidateBlocks = new List<BlockReference>();
            foreach (ObjectId mid in ms)
            {
                if (mid.IsErased) continue;
                Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                BlockReference mbr = me as BlockReference;
                if (mbr == null || IsAiAnnotationLayer(me.Layer) || IsLegendOrHighlightLayer(me.Layer)) continue;
                if (visibleOnly && !IsEntityVisible(me, layerHiddenFlags)) continue;
                string bn = ResolveBlockName(tr, mbr);
                if (!includeAnonymous && IsAnonymousBlockName(bn)) continue;
                if (!includeSystem && (IsSystemBlockName(bn) || string.Equals(me.Layer, "ASHADE", StringComparison.OrdinalIgnoreCase))) continue;
                if (!string.IsNullOrWhiteSpace(blockFilter) &&
                    !string.Equals(bn, blockFilter.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    !MatchesPatternOrSubstring(bn, blockFilter))
                    continue;
                candidateBlocks.Add(mbr);
            }

            for (int c = 0; c < chosenLeaders.Count; c++)
            {
                ExistingCalloutLeaderInfo refInfo = chosenLeaders[c];
                BlockReference ownerBr = null;
                if (!string.IsNullOrWhiteSpace(refInfo.BlockHandle))
                {
                    ObjectId bid = ResolveHandle(db, refInfo.BlockHandle);
                    if (!bid.IsNull && !bid.IsErased)
                        ownerBr = tr.GetObject(bid, OpenMode.ForRead) as BlockReference;
                }
                if (ownerBr == null)
                {
                    double bestD = double.MaxValue;
                    for (int b = 0; b < candidateBlocks.Count; b++)
                    {
                        try
                        {
                            Extents3d bex = candidateBlocks[b].GeometricExtents;
                            if (!IsFiniteBounds(bex)) continue;
                            double d = DistPointToExtents2D(refInfo.TipPt.X, refInfo.TipPt.Y, bex);
                            if (d < bestD)
                            {
                                bestD = d;
                                ownerBr = candidateBlocks[b];
                            }
                        }
                        catch { }
                    }
                }
                if (ownerBr == null)
                {
                    errorMsg = "Could not locate the owning BlockReference for reference Leader #" +
                               refInfo.Index.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }

                Matrix3d inv;
                if (!TryInvertMatrix3d(ownerBr.BlockTransform, out inv))
                {
                    errorMsg = "Owning block for Leader #" + refInfo.Index.ToString(CultureInfo.InvariantCulture) + " has a non-invertible transform.";
                    return false;
                }
                Point3d localPt = refInfo.TipPt.TransformBy(inv);
                SamplePointSpec sp = new SamplePointSpec();
                sp.LocalX = localPt.X;
                sp.LocalY = localPt.Y;
                sp.LocalZ = localPt.Z;
                sp.Label = "P" + (c + 1).ToString(CultureInfo.InvariantCulture);
                resolvedLocalPoints.Add(sp);

                if (string.IsNullOrWhiteSpace(inferredBlockName))
                    inferredBlockName = ResolveBlockName(tr, ownerBr);
            }

            return resolvedLocalPoints.Count > 0;
        }

        private static bool PromptPickBlockSamplePoints(
            Document doc,
            string blockFilter,
            List<string> explicitHandles,
            bool selectedOnlyArg,
            string layerFilter,
            bool visibleOnly,
            bool includeAnonymous,
            bool includeSystem,
            out string sampleBlockName,
            out ObjectId sampleBlockId,
            out List<SamplePointSpec> pickedLocalPoints,
            out string errorMsg)
        {
            sampleBlockName = "";
            sampleBlockId = ObjectId.Null;
            pickedLocalPoints = new List<SamplePointSpec>();
            errorMsg = "";

            try
            {
                int cmdActive = Convert.ToInt32(
                    Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CMDACTIVE"),
                    CultureInfo.InvariantCulture);
                if (cmdActive != 0)
                {
                    errorMsg = "An AutoCAD command is currently active (CMDACTIVE=" + cmdActive + "). Press Esc in AutoCAD first, then retry picking points inside the block.";
                    return false;
                }
            }
            catch { }

            Database db = doc.Database;
            Editor ed = doc.Editor;

            // Resolve candidate sample BlockReference from explicit handles, selection, or block_name
            ObjectId resolvedId = ObjectId.Null;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);

                List<string> candidateHandles = new List<string>();
                if (explicitHandles != null && explicitHandles.Count > 0) candidateHandles.AddRange(explicitHandles);
                if (selectedOnlyArg) candidateHandles.AddRange(GetSelectedHandleStrings(doc));

                foreach (string h in candidateHandles)
                {
                    ObjectId id = ResolveHandle(db, h);
                    if (id.IsNull || id.IsErased) continue;
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    BlockReference br = ent as BlockReference;
                    if (br == null || ent.OwnerId != ms.ObjectId || IsAiAnnotationLayer(ent.Layer)) continue;
                    string bn = ResolveBlockName(tr, br);
                    if (!includeAnonymous && IsAnonymousBlockName(bn)) continue;
                    if (!includeSystem && IsSystemBlockName(bn)) continue;
                    if (!string.IsNullOrWhiteSpace(blockFilter) && !MatchesPatternOrSubstring(bn, blockFilter) &&
                        !string.Equals(bn, blockFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                    resolvedId = id;
                    sampleBlockName = bn;
                    break;
                }

                if (resolvedId.IsNull && !string.IsNullOrWhiteSpace(blockFilter))
                {
                    foreach (ObjectId mid in ms)
                    {
                        if (mid.IsErased) continue;
                        Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                        BlockReference br = ent as BlockReference;
                        if (br == null || IsLegendOrHighlightLayer(ent.Layer)) continue;
                        if (visibleOnly && !IsEntityVisible(ent, layerHiddenFlags)) continue;
                        if (!string.IsNullOrEmpty(layerFilter) && !MatchesPatternOrSubstring(ent.Layer ?? "", layerFilter)) continue;
                        string bn = ResolveBlockName(tr, br);
                        if (!includeAnonymous && IsAnonymousBlockName(bn)) continue;
                        if (!includeSystem && (IsSystemBlockName(bn) || string.Equals(ent.Layer, "ASHADE", StringComparison.OrdinalIgnoreCase))) continue;
                        if (string.Equals(bn, blockFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
                            MatchesPatternOrSubstring(bn, blockFilter))
                        {
                            resolvedId = mid;
                            sampleBlockName = bn;
                            break;
                        }
                    }
                }
                tr.Commit();
            }

            TrySetFocusToDwgView(doc);

            if (resolvedId.IsNull)
            {
                PromptEntityOptions peo = new PromptEntityOptions(
                    "\n[AI-Engineer] Select ONE sample block to pick target points on: ");
                peo.SetRejectMessage("\nSelect a BlockReference.");
                peo.AddAllowedClass(typeof(BlockReference), false);
                PromptEntityResult per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK)
                {
                    errorMsg = "Sample block selection was cancelled.";
                    return false;
                }
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockReference br = tr.GetObject(per.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (br == null)
                    {
                        errorMsg = "Selected entity is not a valid BlockReference.";
                        return false;
                    }
                    resolvedId = per.ObjectId;
                    sampleBlockName = ResolveBlockName(tr, br);
                    tr.Commit();
                }
            }

            sampleBlockId = resolvedId;
            Matrix3d sampleXform = Matrix3d.Identity;
            Matrix3d sampleInverse = Matrix3d.Identity;
            Extents3d? sampleExt = null;
            bool highlighted = false;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockReference br = tr.GetObject(sampleBlockId, OpenMode.ForRead) as BlockReference;
                if (br == null)
                {
                    errorMsg = "Sample BlockReference could not be opened.";
                    return false;
                }
                sampleXform = br.BlockTransform;
                if (!TryInvertMatrix3d(sampleXform, out sampleInverse))
                {
                    errorMsg = "Sample block '" + sampleBlockName + "' has a non-invertible transform (zero scale).";
                    return false;
                }
                try
                {
                    Extents3d ex = br.GeometricExtents;
                    if (IsFiniteBounds(ex)) sampleExt = ex;
                }
                catch { }
                try { br.Highlight(); highlighted = true; } catch { }
                tr.Commit();
            }

            try
            {
                if (sampleExt.HasValue)
                {
                    try { ZoomToExtents3d(doc, sampleExt.Value, 2.2); } catch { }
                }

                TrySetFocusToDwgView(doc);

                while (pickedLocalPoints.Count < 32)
                {
                    PromptPointOptions ppo = new PromptPointOptions(
                        "\n[AI-Engineer | " + sampleBlockName + "] Click target point #" +
                        (pickedLocalPoints.Count + 1).ToString(CultureInfo.InvariantCulture) +
                        " inside block (Enter / Right-Click = Done, Esc = Cancel): ");
                    ppo.AllowNone = true;
                    PromptPointResult ppr = ed.GetPoint(ppo);
                    if (ppr.Status == PromptStatus.OK)
                    {
                        Point3d wcsPt = ppr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                        Point3d localPt = wcsPt.TransformBy(sampleInverse);
                        SamplePointSpec sp = new SamplePointSpec();
                        sp.LocalX = localPt.X;
                        sp.LocalY = localPt.Y;
                        sp.LocalZ = localPt.Z;
                        sp.Label = "P" + (pickedLocalPoints.Count + 1).ToString(CultureInfo.InvariantCulture);
                        pickedLocalPoints.Add(sp);
                        ed.WriteMessage(
                            "\n  -> Captured " + sp.Label + " at WCS (" +
                            wcsPt.X.ToString("F3", CultureInfo.InvariantCulture) + ", " +
                            wcsPt.Y.ToString("F3", CultureInfo.InvariantCulture) + ")");
                    }
                    else if (ppr.Status == PromptStatus.None)
                    {
                        if (pickedLocalPoints.Count == 0)
                        {
                            errorMsg = "No points were picked inside block '" + sampleBlockName + "'. Click at least one point before pressing Enter.";
                            return false;
                        }
                        break;
                    }
                    else
                    {
                        errorMsg = "Point picking inside block '" + sampleBlockName + "' was cancelled.";
                        return false;
                    }
                }
            }
            finally
            {
                if (highlighted)
                {
                    try
                    {
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            Entity ent = tr.GetObject(sampleBlockId, OpenMode.ForRead) as Entity;
                            if (ent != null) ent.Unhighlight();
                            tr.Commit();
                        }
                    }
                    catch { }
                }
            }

            return pickedLocalPoints.Count > 0;
        }

        private static Dictionary<string, object> AnnotateBlockCoordinates(Document doc, Dictionary<string, object> args)
        {
            string blockFilter = GetStr(args, "block_name");
            List<string> explicitHandles = GetStringList(args, "handles");
            bool selectedOnlyArg = GetBool(args, "selected_only", string.IsNullOrWhiteSpace(blockFilter) && explicitHandles.Count == 0);
            bool? allInstancesArg = GetNullableBool(args, "all_instances");
            string layerFilter = GetStr(args, "layer_filter") ?? GetStr(args, "layer");
            bool visibleOnly = GetBool(args, "visible_only", false);
            bool includeAnonymous = GetBool(args, "include_anonymous", false);
            bool includeSystem = GetBool(args, "include_system", false);
            string rawSortOrder = GetStr(args, "sort_order");
            string normalizedSortOrder = NormalizeCoordSortOrder(rawSortOrder);
            string coordSource = (GetStr(args, "coordinate_source") ?? "geometric_center").Trim().ToLowerInvariant();
            string arrowTarget = (GetStr(args, "arrow_target") ?? "geometric_center").Trim().ToLowerInvariant();
            int decimals = Math.Max(0, Math.Min(4, GetInt(args, "decimals", 2)));
            string numPrefix = GetStr(args, "prefix") ?? "#";
            bool tableOnly = GetBool(args, "table_only", false);
            bool drawCallouts = tableOnly ? false : GetBool(args, "draw_callouts", true);
            bool drawTable = GetBool(args, "draw_table", true);
            bool pickSamplePoints = GetBool(args, "pick_sample_points", false);
            bool clearSamplePoints = GetBool(args, "clear_sample_points", false);
            bool useSavedSamplePoints = GetBool(args, "use_saved_sample_points", true);
            int refCalloutIdx = GetInt(args, "reference_callout_index", 0);
            if (refCalloutIdx <= 0) refCalloutIdx = GetInt(args, "reference_leader_index", 0);
            List<int> refCalloutIndices = new List<int>();
            if (refCalloutIdx > 0) refCalloutIndices.Add(refCalloutIdx);
            foreach (object rawRef in AsList(GetObj(args, "reference_callout_indices")))
            {
                double dv;
                if (TryParseObjDouble(rawRef, out dv))
                {
                    int iv = (int)Math.Round(dv);
                    if (iv > 0 && !refCalloutIndices.Contains(iv)) refCalloutIndices.Add(iv);
                }
            }
            bool fromSelectedLeader = GetBool(args, "from_selected_leader", false);
            bool pickRefLeader = GetBool(args, "pick_reference_leader", false);
            string refCalloutLabel = GetStr(args, "reference_callout_label") ?? "";
            bool hasRefLeaderReq = (refCalloutIndices.Count > 0 || !string.IsNullOrWhiteSpace(refCalloutLabel) || fromSelectedLeader || pickRefLeader);
            string rawMultiPointMode = GetStr(args, "multi_point_mode");
            string normalizedMultiPointMode = NormalizeMultiPointMode(rawMultiPointMode);
            List<SamplePointSpec> explicitLocalPts = ParseSamplePointList(args, "local_points");
            List<SamplePointSpec> explicitSamplePts = ParseSamplePointList(args, "sample_points");

            Database db = doc.Database;
            List<SamplePointSpec> pickedLocalPts = null;
            ObjectId pickedSampleBlockId = ObjectId.Null;
            string pickedSampleBlockName = "";
            string pickedRefLeaderHandle = "";
            if (pickSamplePoints)
            {
                if (!db.TileMode)
                {
                    try { LayoutManager.Current.CurrentLayout = "Model"; } catch { }
                }
                string pickErr;
                if (!PromptPickBlockSamplePoints(
                    doc, blockFilter, explicitHandles, selectedOnlyArg, layerFilter,
                    visibleOnly, includeAnonymous, includeSystem,
                    out pickedSampleBlockName, out pickedSampleBlockId, out pickedLocalPts, out pickErr))
                {
                    return Err(pickErr);
                }
                if (string.IsNullOrWhiteSpace(blockFilter) && !string.IsNullOrWhiteSpace(pickedSampleBlockName))
                    blockFilter = pickedSampleBlockName;
            }
            else if (pickRefLeader)
            {
                if (!db.TileMode)
                {
                    try { LayoutManager.Current.CurrentLayout = "Model"; } catch { }
                }
                ObjectId pickedLdrId;
                string pickLdrErr;
                if (!PromptPickReferenceLeader(doc, out pickedLdrId, out pickLdrErr))
                    return Err(pickLdrErr);
                using (Transaction trTmp = db.TransactionManager.StartTransaction())
                {
                    Entity pe = trTmp.GetObject(pickedLdrId, OpenMode.ForRead) as Entity;
                    if (pe != null) pickedRefLeaderHandle = pe.Handle.ToString();
                    trTmp.Commit();
                }
            }

            string sortOrder = normalizedSortOrder;
            string multiPointMode = normalizedMultiPointMode;
            string rememberedBlockName = "";
            lock (s_lastCoordLock)
            {
                s_lastCoordBlockNameByDb.TryGetValue(db.UnmanagedObject, out rememberedBlockName);
                if (string.IsNullOrEmpty(sortOrder))
                {
                    string remembered;
                    if ((tableOnly || hasRefLeaderReq || pickSamplePoints) &&
                        s_lastCoordSortOrderByDb.TryGetValue(db.UnmanagedObject, out remembered) &&
                        !string.IsNullOrEmpty(remembered))
                        sortOrder = remembered;
                    else
                        sortOrder = "left_to_right";
                }
                if (drawCallouts || !s_lastCoordSortOrderByDb.ContainsKey(db.UnmanagedObject) || !string.IsNullOrEmpty(normalizedSortOrder))
                    s_lastCoordSortOrderByDb[db.UnmanagedObject] = sortOrder;

                if (string.IsNullOrEmpty(multiPointMode))
                {
                    string rememberedMode;
                    if (s_lastMultiPointModeByDb.TryGetValue(db.UnmanagedObject, out rememberedMode) && !string.IsNullOrEmpty(rememberedMode))
                        multiPointMode = rememberedMode;
                    else
                        multiPointMode = "sequential";
                }
                if (!string.IsNullOrEmpty(normalizedMultiPointMode) || !s_lastMultiPointModeByDb.ContainsKey(db.UnmanagedObject))
                    s_lastMultiPointModeByDb[db.UnmanagedObject] = multiPointMode;
            }
            string tablePosStr = (GetStr(args, "table_position") ?? "outside_right").Trim().ToLowerInvariant();
            double[] explicitTableOrigin = GetDoubleArray(args, "table_position");
            double? tableGapArg = GetNullableDouble(args, "table_gap");
            string tableTitleArg = GetStr(args, "table_title");
            string calloutLayerReq = GetStr(args, "callout_layer") ?? "_AI_COORD_CALLOUTS";
            string tableLayerReq = GetStr(args, "table_layer") ?? "_AI_COORD_TABLE";
            bool replaceExisting = GetBool(args, "replace_existing", true);
            double? explicitTextHeight = GetNullableDouble(args, "text_height");
            int calloutColorIdx = ClampColor(GetInt(args, "callout_color_index", 2));
            int arrowColorIdx = ClampColor(GetInt(args, "arrow_color_index", 4));
            bool? includeBlockNameArg = GetNullableBool(args, "include_block_name");
            bool zoom = GetBool(args, "zoom", true);
            string zoomMode = (GetStr(args, "zoom_mode") ?? "auto").Trim().ToLowerInvariant();

            List<string> selectedHandles = selectedOnlyArg ? GetSelectedHandleStrings(doc) : new List<string>();
            if (!string.IsNullOrWhiteSpace(pickedRefLeaderHandle) && !selectedHandles.Contains(pickedRefLeaderHandle))
                selectedHandles.Add(pickedRefLeaderHandle);
            HashSet<string> selectedSet = new HashSet<string>(selectedHandles, StringComparer.OrdinalIgnoreCase);
            HashSet<string> explicitHandleSet = new HashSet<string>(explicitHandles, StringComparer.OrdinalIgnoreCase);

            List<CoordTargetItem> blockTargets = new List<CoordTargetItem>();
            List<CoordTargetItem> targets = new List<CoordTargetItem>();
            List<SamplePointSpec> activeLocalPoints = null;
            bool hasActiveSamplePoints = false;
            int pointsPerBlock = 1;
            bool isGroupedMulti = false;
            List<string> createdHandles = new List<string>();
            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();
            List<object> blockRowsOut = new List<object>();
            List<string> warnings = new List<string>();
            HashSet<string> distinctTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Extents3d? mainDrawingExt = null;
            Extents3d? calloutsExt = null;
            Extents3d? targetsExt = null;
            Extents3d? tableExt = null;
            string safeCalloutLayer = calloutLayerReq;
            string safeTableLayer = tableLayerReq;
            bool selectionExpandedFromSample = false;
            int obstacleFreeCount = 0;
            int calloutFreeCount = 0;
            int crossingCount = 0;
            double usedTextH = 0.0;
            bool switchedToModel = false;

            using (DocumentLock docLock = doc.LockDocument())
            {
                if (zoom && !db.TileMode)
                {
                    try { LayoutManager.Current.CurrentLayout = "Model"; switchedToModel = true; } catch { }
                }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                    ObjectId unicodeStyleId = EnsureUnicodeTextStyle(db, tr);

                    if (drawCallouts) safeCalloutLayer = EnsureLayerSafe(db, tr, calloutLayerReq, (short)calloutColorIdx, null);
                    if (drawTable) safeTableLayer = EnsureLayerSafe(db, tr, tableLayerReq, 7, null);

                    // 1. Resolve target entities BEFORE erasing old annotations
                    List<ObjectId> selectedBlockIds = new List<ObjectId>();
                    List<ObjectId> selectedNonBlockIds = new List<ObjectId>();
                    bool hasSelectedLeader = false;
                    Dictionary<string, int> selectedBlockNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    if (selectedHandles.Count > 0 || explicitHandles.Count > 0)
                    {
                        List<string> rawPick = new List<string>();
                        rawPick.AddRange(explicitHandles);
                        rawPick.AddRange(selectedHandles);
                        HashSet<ObjectId> seenPick = new HashSet<ObjectId>();
                        foreach (string h in rawPick)
                        {
                            ObjectId id = ResolveHandle(db, h);
                            if (id.IsNull || id.IsErased || !seenPick.Add(id)) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.OwnerId != ms.ObjectId) continue;
                            if (ent is Leader)
                            {
                                hasSelectedLeader = true;
                                continue;
                            }
                            if (IsAiAnnotationLayer(ent.Layer)) continue;
                            BlockReference br = ent as BlockReference;
                            if (br != null)
                            {
                                string bn = ResolveBlockName(tr, br);
                                if (!includeAnonymous && IsAnonymousBlockName(bn)) continue;
                                if (!includeSystem && IsSystemBlockName(bn)) continue;
                                selectedBlockIds.Add(id);
                                selectedBlockNames[bn] = selectedBlockNames.ContainsKey(bn) ? selectedBlockNames[bn] + 1 : 1;
                            }
                            else
                            {
                                selectedNonBlockIds.Add(id);
                            }
                        }
                    }

                    // 1b. If engineer asked to match a reference Leader callout (#N, label, picked, or selected Leader), read its arrowhead BEFORE erasing old callouts
                    List<SamplePointSpec> refLeaderLocalPts = null;
                    string refLeaderInferredBlock = "";
                    if (hasRefLeaderReq || (hasSelectedLeader && selectedBlockIds.Count == 0 && selectedNonBlockIds.Count == 0))
                    {
                        string refErr;
                        List<SamplePointSpec> resolvedRefPts;
                        string resolvedRefBlock;
                        string searchBlockFilter = !string.IsNullOrWhiteSpace(blockFilter) ? blockFilter : rememberedBlockName;
                        if (!ResolveReferenceLeadersToSamplePoints(
                            db, tr, ms, safeCalloutLayer, searchBlockFilter, selectedHandles,
                            refCalloutIndices, refCalloutLabel, fromSelectedLeader || hasSelectedLeader || !string.IsNullOrWhiteSpace(pickedRefLeaderHandle),
                            layerHiddenFlags, visibleOnly, includeAnonymous, includeSystem,
                            out resolvedRefBlock, out resolvedRefPts, out refErr))
                        {
                            tr.Commit();
                            return Err(refErr);
                        }
                        refLeaderLocalPts = resolvedRefPts;
                        refLeaderInferredBlock = resolvedRefBlock;
                        if (string.IsNullOrWhiteSpace(blockFilter) && !string.IsNullOrWhiteSpace(refLeaderInferredBlock))
                            blockFilter = refLeaderInferredBlock;
                    }

                    // Determine target set
                    HashSet<ObjectId> targetIdSet = new HashSet<ObjectId>();
                    string resolvedSingleBlockName = blockFilter;

                    if (string.IsNullOrWhiteSpace(resolvedSingleBlockName))
                    {
                        if (selectedBlockIds.Count > 0)
                        {
                            if (selectedBlockNames.Count == 1)
                            {
                                string onlyName = null;
                                foreach (string k in selectedBlockNames.Keys) { onlyName = k; break; }
                                // Count total insertions of this block in ModelSpace
                                int totalModelInsertions = 0;
                                foreach (ObjectId mid in ms)
                                {
                                    if (mid.IsErased) continue;
                                    Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                                    BlockReference mbr = me as BlockReference;
                                    if (mbr == null || IsLegendOrHighlightLayer(me.Layer)) continue;
                                    if (visibleOnly && !IsEntityVisible(me, layerHiddenFlags)) continue;
                                    if (!string.IsNullOrEmpty(layerFilter) && !MatchesPatternOrSubstring(me.Layer ?? "", layerFilter)) continue;
                                    if (string.Equals(ResolveBlockName(tr, mbr), onlyName, StringComparison.OrdinalIgnoreCase))
                                        totalModelInsertions++;
                                }

                                bool shouldExpand = allInstancesArg.HasValue
                                    ? allInstancesArg.Value
                                    : (selectedBlockIds.Count == 1 || selectedBlockIds.Count >= totalModelInsertions);

                                if (shouldExpand)
                                {
                                    resolvedSingleBlockName = onlyName;
                                    if (selectedBlockIds.Count < totalModelInsertions) selectionExpandedFromSample = true;
                                }
                                else
                                {
                                    foreach (ObjectId bid in selectedBlockIds) targetIdSet.Add(bid);
                                }
                            }
                            else
                            {
                                foreach (ObjectId bid in selectedBlockIds) targetIdSet.Add(bid);
                            }
                        }
                        else if (selectedNonBlockIds.Count > 0)
                        {
                            if (selectedNonBlockIds.Count > 40)
                            {
                                tr.Commit();
                                return Err("Selected " + selectedNonBlockIds.Count + " loose non-block entities without a block name. Convert to a block first (`convert_matching_shapes_to_block`) or select <= 40 specific entities / a BlockReference.");
                            }
                            foreach (ObjectId nid in selectedNonBlockIds) targetIdSet.Add(nid);
                        }
                        else if (!string.IsNullOrWhiteSpace(rememberedBlockName) && (tableOnly || hasRefLeaderReq || pickSamplePoints))
                        {
                            resolvedSingleBlockName = rememberedBlockName;
                        }
                        else
                        {
                            tr.Commit();
                            return Err("No BlockReference is currently selected and 'block_name' was not provided. Select a block (e.g. Fuse) in AutoCAD or pass 'block_name'.");
                        }
                    }

                    // If we have a block_name filter (either passed directly or expanded from selection), collect matching BlockReferences
                    if (!string.IsNullOrWhiteSpace(resolvedSingleBlockName))
                    {
                        bool exactMatchFound = false;
                        List<ObjectId> exactMatches = new List<ObjectId>();
                        List<ObjectId> patternMatches = new List<ObjectId>();
                        foreach (ObjectId mid in ms)
                        {
                            if (mid.IsErased) continue;
                            Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                            BlockReference mbr = me as BlockReference;
                            if (mbr == null || IsLegendOrHighlightLayer(me.Layer)) continue;
                            if (visibleOnly && !IsEntityVisible(me, layerHiddenFlags)) continue;
                            if (!string.IsNullOrEmpty(layerFilter) && !MatchesPatternOrSubstring(me.Layer ?? "", layerFilter)) continue;
                            string bn = ResolveBlockName(tr, mbr);
                            if (!includeAnonymous && IsAnonymousBlockName(bn)) continue;
                            if (!includeSystem && (IsSystemBlockName(bn) || string.Equals(me.Layer, "ASHADE", StringComparison.OrdinalIgnoreCase))) continue;

                            if (string.Equals(bn, resolvedSingleBlockName.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                exactMatchFound = true;
                                exactMatches.Add(mid);
                            }
                            else if (MatchesPatternOrSubstring(bn, resolvedSingleBlockName))
                            {
                                patternMatches.Add(mid);
                            }
                        }
                        List<ObjectId> chosen = exactMatchFound ? exactMatches : patternMatches;
                        foreach (ObjectId cid in chosen) targetIdSet.Add(cid);
                    }

                    if (targetIdSet.Count == 0)
                    {
                        tr.Commit();
                        return Err("No matching block insertions found for '" + (resolvedSingleBlockName ?? "") + "' in ModelSpace.");
                    }

                    // 2. Replace old AI coordinate callouts/table BEFORE obstacle scan
                    if (replaceExisting)
                    {
                        HashSet<string> eraseLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (drawCallouts && !string.Equals(safeCalloutLayer, "0", StringComparison.OrdinalIgnoreCase))
                            eraseLayers.Add(safeCalloutLayer);
                        if (drawTable && !string.Equals(safeTableLayer, "0", StringComparison.OrdinalIgnoreCase))
                            eraseLayers.Add(safeTableLayer);

                        if (eraseLayers.Count > 0)
                        {
                            foreach (ObjectId mid in ms)
                            {
                                if (mid.IsErased) continue;
                                Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                                if (me != null && eraseLayers.Contains(me.Layer ?? ""))
                                {
                                    try
                                    {
                                        me.UpgradeOpen();
                                        me.Erase(true);
                                        erasedIds.Add(mid);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }

                    // 3. Collect block/entity data + main drawing bounds
                    List<double> blockWidths = new List<double>();
                    List<double> blockHeights = new List<double>();

                    foreach (ObjectId mid in ms)
                    {
                        if (mid.IsErased) continue;
                        Entity ent = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                        if (!IsAiAnnotationLayer(ent.Layer))
                        {
                            try
                            {
                                Extents3d ex = ent.GeometricExtents;
                                if (IsFiniteBounds(ex))
                                {
                                    if (!mainDrawingExt.HasValue) mainDrawingExt = ex;
                                    else { Extents3d c = mainDrawingExt.Value; c.AddExtents(ex); mainDrawingExt = c; }
                                }
                            }
                            catch { }
                        }

                        if (!targetIdSet.Contains(mid)) continue;
                        Extents3d tex;
                        try { tex = ent.GeometricExtents; }
                        catch { continue; }
                        if (!IsFiniteBounds(tex)) continue;

                        double cx = (tex.MinPoint.X + tex.MaxPoint.X) * 0.5;
                        double cy = (tex.MinPoint.Y + tex.MaxPoint.Y) * 0.5;
                        double insX = cx, insY = cy;
                        string bName = ent.GetType().Name;
                        bool isBr = false;
                        Matrix3d brXform = Matrix3d.Identity;
                        BlockReference br = ent as BlockReference;
                        if (br != null)
                        {
                            bName = ResolveBlockName(tr, br);
                            insX = br.Position.X;
                            insY = br.Position.Y;
                            isBr = true;
                            brXform = br.BlockTransform;
                        }
                        distinctTargetNames.Add(bName);

                        double repX = (coordSource == "insertion_point") ? insX : cx;
                        double repY = (coordSource == "insertion_point") ? insY : cy;
                        double arrX = (arrowTarget == "insertion_point") ? insX : cx;
                        double arrY = (arrowTarget == "insertion_point") ? insY : cy;

                        CoordTargetItem item = new CoordTargetItem();
                        item.Id = mid;
                        item.Handle = ent.Handle.ToString();
                        item.BlockName = bName;
                        item.IsBlockRef = isBr;
                        item.BlockXform = brXform;
                        item.CenterX = cx;
                        item.CenterY = cy;
                        item.InsertX = insX;
                        item.InsertY = insY;
                        item.ReportX = repX;
                        item.ReportY = repY;
                        item.ArrowX = arrX;
                        item.ArrowY = arrY;
                        item.Bounds = tex;
                        blockTargets.Add(item);

                        blockWidths.Add(Math.Max(1e-4, tex.MaxPoint.X - tex.MinPoint.X));
                        blockHeights.Add(Math.Max(1e-4, tex.MaxPoint.Y - tex.MinPoint.Y));

                        if (!targetsExt.HasValue) targetsExt = tex;
                        else { Extents3d cur = targetsExt.Value; cur.AddExtents(tex); targetsExt = cur; }
                    }

                    if (blockTargets.Count == 0)
                    {
                        tr.Commit();
                        return Err("Resolved target entities have no valid geometric bounds.");
                    }

                    blockWidths.Sort();
                    blockHeights.Sort();
                    double medW = blockWidths[blockWidths.Count / 2];
                    double medH = blockHeights[blockHeights.Count / 2];
                    double medSpan = Math.Max(medW, medH);

                    // 4. Deterministic strict sorting of blocks per engineer-selected direction (L->R, R->L, Top->Bot, Bot->Top)
                    SortCoordTargets(blockTargets, sortOrder, medW, medH);

                    // 4b. Resolve sample points inside block (if picked interactively, matched from reference Leader, passed explicitly, or remembered)
                    string primaryBlockName = !string.IsNullOrWhiteSpace(pickedSampleBlockName)
                        ? pickedSampleBlockName
                        : (!string.IsNullOrWhiteSpace(refLeaderInferredBlock)
                            ? refLeaderInferredBlock
                            : (distinctTargetNames.Count == 1 ? blockTargets[0].BlockName : (resolvedSingleBlockName ?? "")));

                    if (pickedLocalPts != null && pickedLocalPts.Count > 0)
                    {
                        activeLocalPoints = pickedLocalPts;
                    }
                    else if (refLeaderLocalPts != null && refLeaderLocalPts.Count > 0)
                    {
                        activeLocalPoints = refLeaderLocalPts;
                    }
                    else if (explicitLocalPts.Count > 0)
                    {
                        activeLocalPoints = explicitLocalPts;
                    }
                    else if (explicitSamplePts.Count > 0)
                    {
                        CoordTargetItem sampleItem = null;
                        if (selectedBlockIds.Count > 0)
                        {
                            ObjectId firstSelId = selectedBlockIds[0];
                            for (int b = 0; b < blockTargets.Count; b++)
                            {
                                if (blockTargets[b].Id == firstSelId && blockTargets[b].IsBlockRef)
                                {
                                    sampleItem = blockTargets[b];
                                    break;
                                }
                            }
                        }
                        if (sampleItem == null)
                        {
                            for (int b = 0; b < blockTargets.Count; b++)
                            {
                                if (blockTargets[b].IsBlockRef) { sampleItem = blockTargets[b]; break; }
                            }
                        }
                        Matrix3d inv;
                        if (sampleItem != null && TryInvertMatrix3d(sampleItem.BlockXform, out inv))
                        {
                            activeLocalPoints = new List<SamplePointSpec>();
                            for (int p = 0; p < explicitSamplePts.Count; p++)
                            {
                                SamplePointSpec rawSp = explicitSamplePts[p];
                                Point3d lp = new Point3d(rawSp.LocalX, rawSp.LocalY, rawSp.LocalZ).TransformBy(inv);
                                SamplePointSpec locSp = new SamplePointSpec();
                                locSp.LocalX = lp.X;
                                locSp.LocalY = lp.Y;
                                locSp.LocalZ = lp.Z;
                                locSp.Label = rawSp.Label;
                                activeLocalPoints.Add(locSp);
                            }
                            if (string.IsNullOrWhiteSpace(primaryBlockName)) primaryBlockName = sampleItem.BlockName;
                        }
                    }

                    lock (s_lastCoordLock)
                    {
                        Dictionary<string, List<SamplePointSpec>> dbMap;
                        if (!s_lastBlockSamplePointsByDb.TryGetValue(db.UnmanagedObject, out dbMap))
                        {
                            dbMap = new Dictionary<string, List<SamplePointSpec>>(StringComparer.OrdinalIgnoreCase);
                            s_lastBlockSamplePointsByDb[db.UnmanagedObject] = dbMap;
                        }
                        if (clearSamplePoints && !string.IsNullOrWhiteSpace(primaryBlockName))
                        {
                            dbMap.Remove(primaryBlockName);
                        }
                        else if (activeLocalPoints != null && activeLocalPoints.Count > 0 && !string.IsNullOrWhiteSpace(primaryBlockName))
                        {
                            dbMap[primaryBlockName] = activeLocalPoints;
                        }
                        else if (!clearSamplePoints && useSavedSamplePoints && !string.IsNullOrWhiteSpace(primaryBlockName))
                        {
                            List<SamplePointSpec> savedPts;
                            if (dbMap.TryGetValue(primaryBlockName, out savedPts) && savedPts != null && savedPts.Count > 0)
                                activeLocalPoints = savedPts;
                        }
                    }

                    hasActiveSamplePoints = (activeLocalPoints != null && activeLocalPoints.Count > 0);
                    pointsPerBlock = hasActiveSamplePoints ? activeLocalPoints.Count : 1;
                    bool allBlocksUseSameMulti = hasActiveSamplePoints && blockTargets.TrueForAll(delegate(CoordTargetItem bItem)
                    {
                        return bItem.IsBlockRef && (string.IsNullOrWhiteSpace(primaryBlockName) ||
                            string.Equals(bItem.BlockName, primaryBlockName, StringComparison.OrdinalIgnoreCase));
                    });
                    isGroupedMulti = allBlocksUseSameMulti && pointsPerBlock > 1 && multiPointMode == "grouped";

                    for (int b = 0; b < blockTargets.Count; b++)
                    {
                        CoordTargetItem blk = blockTargets[b];
                        int blkIdx = b + 1;
                        bool expandThisBlock = hasActiveSamplePoints && blk.IsBlockRef &&
                            (string.IsNullOrWhiteSpace(primaryBlockName) ||
                             string.Equals(blk.BlockName, primaryBlockName, StringComparison.OrdinalIgnoreCase));

                        if (expandThisBlock)
                        {
                            for (int p = 0; p < activeLocalPoints.Count; p++)
                            {
                                SamplePointSpec sp = activeLocalPoints[p];
                                Point3d wpt = new Point3d(sp.LocalX, sp.LocalY, sp.LocalZ).TransformBy(blk.BlockXform);
                                CoordTargetItem ptItem = new CoordTargetItem();
                                ptItem.BlockIndex = blkIdx;
                                ptItem.SubPointIndex = p + 1;
                                ptItem.SubPointLabel = !string.IsNullOrWhiteSpace(sp.Label)
                                    ? sp.Label
                                    : ("P" + (p + 1).ToString(CultureInfo.InvariantCulture));
                                ptItem.HasCustomPoint = true;
                                ptItem.LocalX = sp.LocalX;
                                ptItem.LocalY = sp.LocalY;
                                ptItem.Id = blk.Id;
                                ptItem.Handle = blk.Handle;
                                ptItem.BlockName = blk.BlockName;
                                ptItem.IsBlockRef = true;
                                ptItem.BlockXform = blk.BlockXform;
                                ptItem.CenterX = blk.CenterX;
                                ptItem.CenterY = blk.CenterY;
                                ptItem.InsertX = blk.InsertX;
                                ptItem.InsertY = blk.InsertY;
                                ptItem.ReportX = wpt.X;
                                ptItem.ReportY = wpt.Y;
                                ptItem.ArrowX = wpt.X;
                                ptItem.ArrowY = wpt.Y;
                                ptItem.Bounds = blk.Bounds;
                                targets.Add(ptItem);
                            }
                        }
                        else
                        {
                            blk.BlockIndex = blkIdx;
                            blk.SubPointIndex = 1;
                            blk.SubPointLabel = "P1";
                            targets.Add(blk);
                        }
                    }

                    bool includeBlkNameLabel = includeBlockNameArg.HasValue ? includeBlockNameArg.Value : (distinctTargetNames.Count > 1);
                    string fmt = "F" + decimals.ToString(CultureInfo.InvariantCulture);
                    string longestLabel = "";
                    for (int i = 0; i < targets.Count; i++)
                    {
                        targets[i].Index = i + 1;
                        string tag = isGroupedMulti
                            ? (numPrefix + targets[i].BlockIndex.ToString(CultureInfo.InvariantCulture) + "." +
                               targets[i].SubPointIndex.ToString(CultureInfo.InvariantCulture))
                            : (numPrefix + targets[i].Index.ToString(CultureInfo.InvariantCulture));
                        string head = includeBlkNameLabel ? (tag + " " + targets[i].BlockName) : tag;
                        string lbl = head + "\nX: " + targets[i].ReportX.ToString(fmt, CultureInfo.InvariantCulture) +
                                     "\nY: " + targets[i].ReportY.ToString(fmt, CultureInfo.InvariantCulture);
                        targets[i].LabelText = lbl;
                        if (lbl.Length > longestLabel.Length) longestLabel = lbl;
                    }

                    double dwgW = mainDrawingExt.HasValue ? Math.Max(medW, mainDrawingExt.Value.MaxPoint.X - mainDrawingExt.Value.MinPoint.X) : medSpan * 10.0;
                    double dwgH = mainDrawingExt.HasValue ? Math.Max(medH, mainDrawingExt.Value.MaxPoint.Y - mainDrawingExt.Value.MinPoint.Y) : medSpan * 10.0;
                    double dwgSpan = Math.Max(1e-3, Math.Max(dwgW, dwgH));

                    // Compute nearest-neighbor pitch p20NN between distinct blocks for proportional label sizing
                    double p20NN = medSpan * 4.0;
                    if (blockTargets.Count > 1)
                    {
                        List<double> nnDists = new List<double>();
                        for (int i = 0; i < blockTargets.Count; i++)
                        {
                            double bestD = double.MaxValue;
                            for (int j = 0; j < blockTargets.Count; j++)
                            {
                                if (i == j) continue;
                                double dx = blockTargets[i].CenterX - blockTargets[j].CenterX;
                                double dy = blockTargets[i].CenterY - blockTargets[j].CenterY;
                                double d = Math.Sqrt(dx * dx + dy * dy);
                                if (d > medSpan * 0.1 && d < bestD) bestD = d;
                            }
                            if (bestD < double.MaxValue) nnDists.Add(bestD);
                        }
                        if (nnDists.Count > 0)
                        {
                            nnDists.Sort();
                            p20NN = nnDists[Math.Max(0, Math.Min(nnDists.Count - 1, nnDists.Count / 5))];
                        }
                    }

                    // 5. Place non-overlapping Leader Callouts if requested
                    if (drawCallouts)
                    {
                        double textH;
                        if (explicitTextHeight.HasValue && explicitTextHeight.Value > 1e-5)
                        {
                            textH = explicitTextHeight.Value;
                        }
                        else
                        {
                            textH = medSpan * (pointsPerBlock > 1 ? 0.15 : 0.20);
                            if (p20NN > 1e-5) textH = Math.Min(textH, p20NN * (pointsPerBlock > 1 ? 0.12 : 0.145));
                            textH = Math.Max(dwgSpan * 0.0020, Math.Min(dwgSpan * 0.025, textH));
                            textH = Math.Max(textH, medSpan * (pointsPerBlock > 1 ? 0.068 : 0.095));
                        }
                        usedTextH = textH;

                        // Measure actual TrueType font extents in AutoCAD
                        double measW = textH * 8.0;
                        double measH = textH * 3.8;
                        MText probeMt = new MText();
                        probeMt.SetDatabaseDefaults(db);
                        probeMt.TextStyleId = unicodeStyleId;
                        probeMt.TextHeight = textH;
                        probeMt.Width = 0.0;
                        probeMt.Contents = EscapeMTextPlain(longestLabel);
                        try
                        {
                            if (probeMt.ActualWidth > 1e-4) measW = probeMt.ActualWidth;
                            if (probeMt.ActualHeight > 1e-4) measH = probeMt.ActualHeight;
                        }
                        catch { }
                        probeMt.Dispose();

                        double padX = textH * 0.36;
                        double padY = textH * 0.22;
                        double shelfOffset = textH * 0.22;
                        double doglegGap = textH * 0.32;
                        double boxW = measW + 2.0 * padX;
                        double boxH = measH + shelfOffset + padY + textH * 0.22;

                        // Build spatial occupancy grid of ALL existing visible drawing obstacles
                        double strokePad = Math.Max(textH * 0.14, medSpan * 0.03);
                        SpatialGrid2D grid = new SpatialGrid2D(Math.Max(medSpan * 0.75, boxH * 0.65));
                        int segBudget = 30000;
                        foreach (ObjectId mid in ms)
                        {
                            if (mid.IsErased) continue;
                            Entity me = tr.GetObject(mid, OpenMode.ForRead) as Entity;
                            if (me == null || me is Autodesk.AutoCAD.DatabaseServices.Viewport) continue;
                            if (IsAiAnnotationLayer(me.Layer)) continue;
                            if (!IsEntityVisible(me, layerHiddenFlags)) continue;
                            if (targetIdSet.Contains(mid))
                            {
                                try
                                {
                                    Extents3d tex = me.GeometricExtents;
                                    if (IsFiniteBounds(tex))
                                    {
                                        double bpad = Math.Max(strokePad, medSpan * 0.05);
                                        grid.AddRect(tex.MinPoint.X - bpad, tex.MinPoint.Y - bpad,
                                                     tex.MaxPoint.X + bpad, tex.MaxPoint.Y + bpad);
                                    }
                                }
                                catch { }
                                continue;
                            }
                            FeedObstacleEntity(tr, me, Matrix3d.Identity, grid, medSpan, dwgSpan, strokePad, 0, ref segBudget);
                        }

                        // Place center-outward or staggered so dense middle blocks aren't boxed out by outer ones
                        List<CoordTargetItem> placedTargets = new List<CoordTargetItem>();
                        List<int> order = new List<int>();
                        for (int i = 0; i < targets.Count; i++) order.Add(i);

                        for (int oi = 0; oi < order.Count; oi++)
                        {
                            CoordTargetItem tgt = targets[order[oi]];
                            CalloutCandidate best = FindBestCalloutSlot(
                                tgt, boxW, boxH, measW, measH, padX, shelfOffset, textH, doglegGap,
                                medSpan, grid, targets, placedTargets);

                            // If even radial/grid search hits drawing obstacles, retry compact scale cleanly for that cramped cluster
                            if (best != null && best.ObstacleHits > 0 && best.CalloutHits == 0 && !explicitTextHeight.HasValue)
                            {
                                double cTextH = Math.Max(dwgSpan * 0.0014, textH * 0.72);
                                double ratio = cTextH / textH;
                                CalloutCandidate compactCand = FindBestCalloutSlot(
                                    tgt, boxW * ratio, boxH * ratio, measW * ratio, measH * ratio,
                                    padX * ratio, shelfOffset * ratio, cTextH, doglegGap * ratio,
                                    medSpan, grid, targets, placedTargets);
                                if (compactCand != null && compactCand.CalloutHits == 0 && compactCand.ObstacleHits < best.ObstacleHits)
                                {
                                    best = compactCand;
                                }
                            }

                            if (best != null)
                            {
                                tgt.LabelMinX = best.MinX;
                                tgt.LabelMinY = best.MinY;
                                tgt.LabelMaxX = best.MaxX;
                                tgt.LabelMaxY = best.MaxY;
                                tgt.ShelfX1 = best.ShelfX1;
                                tgt.ShelfX2 = best.ShelfX2;
                                tgt.ShelfY = best.ShelfY;
                                tgt.ElbowX = best.ElbowX;
                                tgt.ElbowY = best.ElbowY;
                                tgt.TextCenterX = best.TextCenterX;
                                tgt.UsedTextHeight = best.UsedTextHeight;
                                tgt.UsedShelfOffset = best.UsedShelfOffset;
                                tgt.UsedDoglegGap = best.UsedDoglegGap;
                                tgt.UsedSpanHalf = best.UsedSpanHalf;
                                tgt.ObstacleHits = best.ObstacleHits;
                                tgt.Placed = true;
                                placedTargets.Add(tgt);
                            }
                        }

                        // Post-placement 2-opt uncrossing pass for intersecting leader shafts
                        for (int pass = 0; pass < 2; pass++)
                        {
                            bool improved = false;
                            for (int i = 0; i < targets.Count; i++)
                            {
                                if (!targets[i].Placed) continue;
                                for (int j = i + 1; j < targets.Count; j++)
                                {
                                    if (!targets[j].Placed) continue;
                                    if (!SegmentsIntersect2D(
                                            targets[i].ArrowX, targets[i].ArrowY, targets[i].ElbowX, targets[i].ElbowY,
                                            targets[j].ArrowX, targets[j].ArrowY, targets[j].ElbowX, targets[j].ElbowY))
                                        continue;

                                    // Compute swapped elbow side against each box
                                    double cxI = targets[j].TextCenterX;
                                    double cxJ = targets[i].TextCenterX;
                                    double spanHalfForI = targets[j].UsedSpanHalf > 0.0 ? targets[j].UsedSpanHalf : Math.Max(measW * 0.54, boxW * 0.44);
                                    double spanHalfForJ = targets[i].UsedSpanHalf > 0.0 ? targets[i].UsedSpanHalf : Math.Max(measW * 0.54, boxW * 0.44);
                                    double dgForI = targets[j].UsedDoglegGap > 0.0 ? targets[j].UsedDoglegGap : doglegGap;
                                    double dgForJ = targets[i].UsedDoglegGap > 0.0 ? targets[i].UsedDoglegGap : doglegGap;
                                    bool rightINew = cxI >= targets[i].ArrowX;
                                    bool rightJNew = cxJ >= targets[j].ArrowX;
                                    double elXINew = rightINew ? (cxI - spanHalfForI - dgForI) : (cxI + spanHalfForI + dgForI);
                                    double elXJNew = rightJNew ? (cxJ - spanHalfForJ - dgForJ) : (cxJ + spanHalfForJ + dgForJ);
                                    double shYINew = targets[j].ShelfY;
                                    double shYJNew = targets[i].ShelfY;

                                    if (SegmentsIntersect2D(
                                            targets[i].ArrowX, targets[i].ArrowY, elXINew, shYINew,
                                            targets[j].ArrowX, targets[j].ArrowY, elXJNew, shYJNew))
                                        continue;

                                    // Accept swap!
                                    double tmpMinX = targets[i].LabelMinX, tmpMinY = targets[i].LabelMinY, tmpMaxX = targets[i].LabelMaxX, tmpMaxY = targets[i].LabelMaxY;
                                    double tmpSh1 = targets[i].ShelfX1, tmpSh2 = targets[i].ShelfX2, tmpShY = targets[i].ShelfY, tmpCx = targets[i].TextCenterX;
                                    double tmpTh = targets[i].UsedTextHeight, tmpSo = targets[i].UsedShelfOffset, tmpDg = targets[i].UsedDoglegGap, tmpSp = targets[i].UsedSpanHalf;
                                    int tmpObs = targets[i].ObstacleHits;

                                    targets[i].LabelMinX = targets[j].LabelMinX; targets[i].LabelMinY = targets[j].LabelMinY;
                                    targets[i].LabelMaxX = targets[j].LabelMaxX; targets[i].LabelMaxY = targets[j].LabelMaxY;
                                    targets[i].ShelfX1 = Math.Min(cxI - spanHalfForI, elXINew);
                                    targets[i].ShelfX2 = Math.Max(cxI + spanHalfForI, elXINew);
                                    targets[i].ShelfY = shYINew;
                                    targets[i].ElbowX = elXINew; targets[i].ElbowY = shYINew;
                                    targets[i].TextCenterX = cxI;
                                    targets[i].UsedTextHeight = targets[j].UsedTextHeight;
                                    targets[i].UsedShelfOffset = targets[j].UsedShelfOffset;
                                    targets[i].UsedDoglegGap = targets[j].UsedDoglegGap;
                                    targets[i].UsedSpanHalf = targets[j].UsedSpanHalf;
                                    targets[i].ObstacleHits = targets[j].ObstacleHits;

                                    targets[j].LabelMinX = tmpMinX; targets[j].LabelMinY = tmpMinY;
                                    targets[j].LabelMaxX = tmpMaxX; targets[j].LabelMaxY = tmpMaxY;
                                    targets[j].ShelfX1 = Math.Min(cxJ - spanHalfForJ, elXJNew);
                                    targets[j].ShelfX2 = Math.Max(cxJ + spanHalfForJ, elXJNew);
                                    targets[j].ShelfY = shYJNew;
                                    targets[j].ElbowX = elXJNew; targets[j].ElbowY = shYJNew;
                                    targets[j].TextCenterX = cxJ;
                                    targets[j].UsedTextHeight = tmpTh;
                                    targets[j].UsedShelfOffset = tmpSo;
                                    targets[j].UsedDoglegGap = tmpDg;
                                    targets[j].UsedSpanHalf = tmpSp;
                                    targets[j].ObstacleHits = tmpObs;
                                    improved = true;
                                }
                            }
                            if (!improved) break;
                        }

                        // Count final callout overlaps, obstacle hits & shaft crossings, and draw entities
                        for (int i = 0; i < targets.Count; i++)
                        {
                            CoordTargetItem item = targets[i];
                            if (!item.Placed) continue;
                            if (item.ObstacleHits == 0) obstacleFreeCount++;
                            bool hitsSibling = false;
                            for (int j = 0; j < targets.Count; j++)
                            {
                                if (i == j || !targets[j].Placed) continue;
                                if (RectsOverlap(item.LabelMinX, item.LabelMinY, item.LabelMaxX, item.LabelMaxY,
                                                 targets[j].LabelMinX, targets[j].LabelMinY, targets[j].LabelMaxX, targets[j].LabelMaxY))
                                    hitsSibling = true;
                                if (j > i && SegmentsIntersect2D(
                                        item.ArrowX, item.ArrowY, item.ElbowX, item.ElbowY,
                                        targets[j].ArrowX, targets[j].ArrowY, targets[j].ElbowX, targets[j].ElbowY))
                                    crossingCount++;
                            }
                            if (!hitsSibling) calloutFreeCount++;

                            double itemTh = item.UsedTextHeight > 1e-6 ? item.UsedTextHeight : textH;
                            double itemSo = item.UsedShelfOffset > 1e-6 ? item.UsedShelfOffset : shelfOffset;
                            Point3d tipPt = new Point3d(item.ArrowX, item.ArrowY, 0.0);
                            Point3d elbowPt = new Point3d(item.ElbowX, item.ElbowY, 0.0);
                            double farShelfX = (Math.Abs(item.ElbowX - item.ShelfX1) <= Math.Abs(item.ElbowX - item.ShelfX2))
                                ? item.ShelfX2
                                : item.ShelfX1;
                            Point3d shelfEndPt = new Point3d(farShelfX, item.ShelfY, 0.0);
                            Vector3d vDir = tipPt - elbowPt;
                            double dLen = vDir.Length;
                            double arrLen = Math.Max(itemTh * 1.15, Math.Min(dLen * 0.42, Math.Max(itemTh * 1.55, medSpan * 0.24)));

                            Leader ldr = new Leader();
                            ldr.SetDatabaseDefaults(db);
                            ldr.Layer = safeCalloutLayer;
                            ldr.ColorIndex = (short)arrowColorIdx;
                            ldr.DimensionStyle = db.Dimstyle;
                            try { ldr.Dimldrblk = ObjectId.Null; } catch { }
                            ldr.HasArrowHead = true;
                            ldr.IsSplined = false;
                            ldr.Dimclrd = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)arrowColorIdx);
                            ldr.Dimasz = Math.Max(1e-4, arrLen);
                            ldr.AppendVertex(tipPt);
                            if (dLen > 1e-6)
                                ldr.AppendVertex(elbowPt);
                            ldr.AppendVertex(shelfEndPt);
                            ObjectId ldrId = ms.AppendEntity(ldr);
                            tr.AddNewlyCreatedDBObject(ldr, true);
                            AttachCoordLeaderXData(db, tr, ldr, item);
                            createdIds.Add(ldrId);
                            createdHandles.Add(ldr.Handle.ToString());

                            MText mt = new MText();
                            mt.SetDatabaseDefaults(db);
                            mt.Layer = safeCalloutLayer;
                            mt.ColorIndex = (short)calloutColorIdx;
                            mt.TextStyleId = unicodeStyleId;
                            mt.TextHeight = itemTh;
                            mt.Width = 0.0;
                            mt.Contents = EscapeMTextPlain(item.LabelText);
                            mt.Attachment = AttachmentPoint.BottomCenter;
                            mt.Location = new Point3d(item.TextCenterX, item.ShelfY + itemSo, 0.0);
                            try { mt.BackgroundFill = true; mt.UseBackgroundColor = true; mt.BackgroundScaleFactor = 1.15; } catch { }
                            ObjectId midText = ms.AppendEntity(mt);
                            tr.AddNewlyCreatedDBObject(mt, true);
                            createdIds.Add(midText);
                            createdHandles.Add(mt.Handle.ToString());

                            Extents3d callEx = new Extents3d(
                                new Point3d(Math.Min(item.ArrowX, item.LabelMinX), Math.Min(item.ArrowY, item.LabelMinY), 0.0),
                                new Point3d(Math.Max(item.ArrowX, item.LabelMaxX), Math.Max(item.ArrowY, item.LabelMaxY), 0.0));
                            if (!calloutsExt.HasValue) calloutsExt = callEx;
                            else { Extents3d c = calloutsExt.Value; c.AddExtents(callEx); calloutsExt = c; }
                        }
                    }

                    // 6. Build Coordinate Schedule Table outside drawing extents AND outside any existing _AI_BLOCK_LEGEND
                    if (drawTable)
                    {
                        Extents3d? baseExt = mainDrawingExt ?? targetsExt;
                        Extents3d? activeCalloutLayerExt = CollectLayerExtents(ms, tr, safeCalloutLayer);
                        Extents3d? avoidExt = baseExt;
                        if (activeCalloutLayerExt.HasValue && IsFiniteBounds(activeCalloutLayerExt.Value))
                        {
                            if (!avoidExt.HasValue) avoidExt = activeCalloutLayerExt;
                            else { Extents3d c = avoidExt.Value; c.AddExtents(activeCalloutLayerExt.Value); avoidExt = c; }
                        }
                        double avW = avoidExt.HasValue ? Math.Max(medSpan, avoidExt.Value.MaxPoint.X - avoidExt.Value.MinPoint.X) : dwgW;
                        double avH = avoidExt.HasValue ? Math.Max(medSpan, avoidExt.Value.MaxPoint.Y - avoidExt.Value.MinPoint.Y) : dwgH;
                        double avSpan = Math.Max(avW, avH);

                        double targetTblH = Math.Max(avH * 0.72, avSpan * 0.36);
                        double rowH = targetTblH / Math.Max(5.0, Math.Min(26.0, targets.Count + 3.2));
                        double minRowH = Math.Max(avSpan * 0.014, medSpan * 0.65);
                        double maxRowH = avSpan * 0.11;
                        rowH = Math.Max(minRowH, Math.Min(maxRowH, rowH));

                        double titleH = rowH * 1.25;
                        double headerH = rowH * 1.08;
                        double footerH = rowH * 0.95;
                        double[] colW = isGroupedMulti
                            ? new double[]
                            {
                                rowH * 1.55, // رقم البلوك
                                rowH * 2.95, // اسم البلوك
                                rowH * 1.45, // النقطة
                                rowH * 3.00, // إحداثي X
                                rowH * 3.00  // إحداثي Y
                            }
                            : new double[]
                            {
                                rowH * 1.55, // رقم البلوك / النقطة
                                rowH * 3.15, // اسم البلوك
                                rowH * 3.10, // إحداثي X
                                rowH * 3.10  // إحداثي Y
                            };
                        double tblW = 0.0;
                        for (int c = 0; c < colW.Length; c++) tblW += colW[c];
                        double tblH = titleH + headerH + (targets.Count * rowH) + footerH;
                        double gap = tableGapArg.HasValue
                            ? Math.Max(0.0, tableGapArg.Value)
                            : Math.Max(avSpan * 0.075, tblW * 0.12);

                        double x0 = 0.0;
                        double yTop = tblH;
                        if (explicitTableOrigin != null && explicitTableOrigin.Length >= 2)
                        {
                            x0 = explicitTableOrigin[0];
                            yTop = explicitTableOrigin[1];
                        }
                        else if (avoidExt.HasValue)
                        {
                            Extents3d de = avoidExt.Value;
                            double dMidX = (de.MinPoint.X + de.MaxPoint.X) * 0.5;
                            double dMidY = (de.MinPoint.Y + de.MaxPoint.Y) * 0.5;
                            if (tablePosStr == "outside_left")
                            {
                                x0 = de.MinPoint.X - gap - tblW;
                                yTop = dMidY + tblH * 0.5;
                            }
                            else if (tablePosStr == "outside_top")
                            {
                                x0 = dMidX - tblW * 0.5;
                                yTop = de.MaxPoint.Y + gap + tblH;
                            }
                            else if (tablePosStr == "outside_bottom")
                            {
                                x0 = dMidX - tblW * 0.5;
                                yTop = de.MinPoint.Y - gap;
                            }
                            else
                            {
                                x0 = de.MaxPoint.X + gap;
                                yTop = dMidY + tblH * 0.5;
                            }

                            // Avoid overlapping existing _AI_BLOCK_LEGEND if present on the same side
                            Extents3d? legendBox = CollectLayerExtents(ms, tr, "_AI_BLOCK_LEGEND");
                            if (legendBox.HasValue && IsFiniteBounds(legendBox.Value))
                            {
                                Extents3d lb = legendBox.Value;
                                double tx1 = x0, ty1 = yTop - tblH, tx2 = x0 + tblW, ty2 = yTop;
                                if (RectsOverlap(tx1 - rowH * 0.6, ty1 - rowH * 0.6, tx2 + rowH * 0.6, ty2 + rowH * 0.6,
                                                 lb.MinPoint.X, lb.MinPoint.Y, lb.MaxPoint.X, lb.MaxPoint.Y))
                                {
                                    double padLeg = Math.Max(rowH * 1.8, avSpan * 0.025);
                                    if (tablePosStr == "outside_top")
                                        yTop = lb.MaxPoint.Y + padLeg + tblH;
                                    else
                                        yTop = lb.MinPoint.Y - padLeg;
                                }
                            }
                        }

                        double[] colX = new double[colW.Length + 1];
                        colX[0] = x0;
                        for (int c = 0; c < colW.Length; c++) colX[c + 1] = colX[c] + colW[c];
                        double yTitleTop = yTop;
                        double yTitleBot = yTop - titleH;
                        double yHeaderBot = yTitleBot - headerH;
                        double yDataBot = yHeaderBot - (targets.Count * rowH);
                        double yBottom = yDataBot - footerH;

                        Extents3d outerBounds = new Extents3d(new Point3d(x0, yBottom, 0.0), new Point3d(x0 + tblW, yTitleTop, 0.0));
                        tableExt = outerBounds;

                        Polyline frame = CreateFramePolyline(outerBounds, safeTableLayer, 7);
                        frame.ConstantWidth = rowH * 0.02;
                        ObjectId fid = ms.AppendEntity(frame);
                        tr.AddNewlyCreatedDBObject(frame, true);
                        createdIds.Add(fid);
                        createdHandles.Add(frame.Handle.ToString());

                        Action<double, double, double, double, short> addTblLine = delegate(double lx1, double ly1, double lx2, double ly2, short clr)
                        {
                            Line gl = new Line(new Point3d(lx1, ly1, 0.0), new Point3d(lx2, ly2, 0.0));
                            gl.Layer = safeTableLayer;
                            gl.ColorIndex = clr;
                            ObjectId gid = ms.AppendEntity(gl);
                            tr.AddNewlyCreatedDBObject(gl, true);
                            createdIds.Add(gid);
                            createdHandles.Add(gl.Handle.ToString());
                        };

                        addTblLine(x0, yTitleBot, x0 + tblW, yTitleBot, 7);
                        addTblLine(x0, yHeaderBot, x0 + tblW, yHeaderBot, 7);
                        for (int r = 1; r <= targets.Count; r++)
                        {
                            double ry = yHeaderBot - (r * rowH);
                            if (isGroupedMulti)
                            {
                                bool isBlockEnd = (r == targets.Count) || (targets[r].BlockIndex != targets[r - 1].BlockIndex);
                                double lineStartX = isBlockEnd ? x0 : colX[2];
                                addTblLine(lineStartX, ry, x0 + tblW, ry, isBlockEnd ? (short)7 : (short)8);
                            }
                            else
                            {
                                addTblLine(x0, ry, x0 + tblW, ry, 8);
                            }
                        }
                        for (int c = 1; c < colW.Length; c++)
                        {
                            double vBot = (c == colW.Length - 1) ? yBottom : yDataBot;
                            addTblLine(colX[c], yTitleBot, colX[c], vBot, 8);
                        }

                        string resolvedTitle = !string.IsNullOrWhiteSpace(tableTitleArg)
                            ? tableTitleArg.Trim()
                            : (distinctTargetNames.Count == 1
                                ? ("جدول إحداثيات بلوك " + targets[0].BlockName + " (COORDINATES)")
                                : "جدول إحداثيات البلوكات (COORDINATES SCHEDULE)");

                        MText titleMt = CreateLegendCellMText(
                            ms, tr, unicodeStyleId, resolvedTitle,
                            x0 + tblW * 0.5, (yTitleTop + yTitleBot) * 0.5,
                            tblW * 0.94, titleH * 0.78, titleH * 0.34, rowH,
                            AttachmentPoint.MiddleCenter, 2, safeTableLayer);
                        createdIds.Add(titleMt.ObjectId);
                        createdHandles.Add(titleMt.Handle.ToString());

                        string[] tblHeaders = isGroupedMulti
                            ? new string[] { "رقم البلوك", "اسم البلوك", "النقطة", "إحداثي X", "إحداثي Y" }
                            : new string[] { (pointsPerBlock > 1 ? "رقم النقطة" : "رقم البلوك"), "اسم البلوك", "إحداثي X", "إحداثي Y" };
                        for (int c = 0; c < tblHeaders.Length; c++)
                        {
                            MText hMt = CreateLegendCellMText(
                                ms, tr, unicodeStyleId, tblHeaders[c],
                                colX[c] + colW[c] * 0.5, (yTitleBot + yHeaderBot) * 0.5,
                                colW[c] * 0.90, headerH * 0.78, headerH * 0.27, rowH,
                                AttachmentPoint.MiddleCenter, 4, safeTableLayer);
                            createdIds.Add(hMt.ObjectId);
                            createdHandles.Add(hMt.Handle.ToString());
                        }

                        if (isGroupedMulti)
                        {
                            int r = 0;
                            while (r < targets.Count)
                            {
                                int grpStart = r;
                                int blkIdx = targets[r].BlockIndex;
                                while (r < targets.Count && targets[r].BlockIndex == blkIdx) r++;
                                int grpCount = r - grpStart;
                                double grpMidY = yHeaderBot - (grpStart + grpCount * 0.5) * rowH;

                                MText c0 = CreateLegendCellMText(ms, tr, unicodeStyleId, numPrefix + blkIdx.ToString(CultureInfo.InvariantCulture),
                                    colX[0] + colW[0] * 0.5, grpMidY, colW[0] * 0.86, rowH * grpCount * 0.76, rowH * 0.27, rowH, AttachmentPoint.MiddleCenter, 2, safeTableLayer);
                                MText c1 = CreateLegendCellMText(ms, tr, unicodeStyleId, targets[grpStart].BlockName,
                                    colX[1] + colW[1] * 0.5, grpMidY, colW[1] * 0.90, rowH * grpCount * 0.78, rowH * 0.24, rowH, AttachmentPoint.MiddleCenter, 7, safeTableLayer);
                                createdIds.Add(c0.ObjectId); createdHandles.Add(c0.Handle.ToString());
                                createdIds.Add(c1.ObjectId); createdHandles.Add(c1.Handle.ToString());

                                for (int sub = grpStart; sub < r; sub++)
                                {
                                    CoordTargetItem item = targets[sub];
                                    double rCy = yHeaderBot - (sub + 0.5) * rowH;
                                    MText c2 = CreateLegendCellMText(ms, tr, unicodeStyleId, item.SubPointLabel,
                                        colX[2] + colW[2] * 0.5, rCy, colW[2] * 0.86, rowH * 0.76, rowH * 0.24, rowH, AttachmentPoint.MiddleCenter, 4, safeTableLayer);
                                    MText c3 = CreateLegendCellMText(ms, tr, unicodeStyleId, item.ReportX.ToString(fmt, CultureInfo.InvariantCulture),
                                        colX[3] + colW[3] * 0.5, rCy, colW[3] * 0.90, rowH * 0.78, rowH * 0.24, rowH, AttachmentPoint.MiddleCenter, 3, safeTableLayer);
                                    MText c4 = CreateLegendCellMText(ms, tr, unicodeStyleId, item.ReportY.ToString(fmt, CultureInfo.InvariantCulture),
                                        colX[4] + colW[4] * 0.5, rCy, colW[4] * 0.90, rowH * 0.78, rowH * 0.24, rowH, AttachmentPoint.MiddleCenter, 3, safeTableLayer);
                                    createdIds.Add(c2.ObjectId); createdHandles.Add(c2.Handle.ToString());
                                    createdIds.Add(c3.ObjectId); createdHandles.Add(c3.Handle.ToString());
                                    createdIds.Add(c4.ObjectId); createdHandles.Add(c4.Handle.ToString());
                                }
                            }

                            double fCy = (yDataBot + yBottom) * 0.5;
                            double spanL = colX[4] - x0;
                            MText fLbl = CreateLegendCellMText(ms, tr, unicodeStyleId, "إجمالي البلوكات / النقاط (Total)",
                                x0 + spanL * 0.5, fCy, spanL * 0.92, footerH * 0.76, footerH * 0.26, rowH, AttachmentPoint.MiddleCenter, 4, safeTableLayer);
                            MText fCnt = CreateLegendCellMText(ms, tr, unicodeStyleId,
                                blockTargets.Count.ToString(CultureInfo.InvariantCulture) + " / " + targets.Count.ToString(CultureInfo.InvariantCulture),
                                colX[4] + colW[4] * 0.5, fCy, colW[4] * 0.86, footerH * 0.76, footerH * 0.27, rowH, AttachmentPoint.MiddleCenter, 3, safeTableLayer);
                            createdIds.Add(fLbl.ObjectId); createdHandles.Add(fLbl.Handle.ToString());
                            createdIds.Add(fCnt.ObjectId); createdHandles.Add(fCnt.Handle.ToString());
                        }
                        else
                        {
                            for (int r = 0; r < targets.Count; r++)
                            {
                                CoordTargetItem item = targets[r];
                                double rCy = yHeaderBot - (r + 0.5) * rowH;

                                MText c0 = CreateLegendCellMText(ms, tr, unicodeStyleId, numPrefix + item.Index.ToString(CultureInfo.InvariantCulture),
                                    colX[0] + colW[0] * 0.5, rCy, colW[0] * 0.86, rowH * 0.76, rowH * 0.25, rowH, AttachmentPoint.MiddleCenter, 2, safeTableLayer);
                                MText c1 = CreateLegendCellMText(ms, tr, unicodeStyleId, item.BlockName,
                                    colX[1] + colW[1] * 0.5, rCy, colW[1] * 0.90, rowH * 0.78, rowH * 0.24, rowH, AttachmentPoint.MiddleCenter, 7, safeTableLayer);
                                MText c2 = CreateLegendCellMText(ms, tr, unicodeStyleId, item.ReportX.ToString(fmt, CultureInfo.InvariantCulture),
                                    colX[2] + colW[2] * 0.5, rCy, colW[2] * 0.90, rowH * 0.78, rowH * 0.24, rowH, AttachmentPoint.MiddleCenter, 3, safeTableLayer);
                                MText c3 = CreateLegendCellMText(ms, tr, unicodeStyleId, item.ReportY.ToString(fmt, CultureInfo.InvariantCulture),
                                    colX[3] + colW[3] * 0.5, rCy, colW[3] * 0.90, rowH * 0.78, rowH * 0.24, rowH, AttachmentPoint.MiddleCenter, 3, safeTableLayer);

                                createdIds.Add(c0.ObjectId); createdHandles.Add(c0.Handle.ToString());
                                createdIds.Add(c1.ObjectId); createdHandles.Add(c1.Handle.ToString());
                                createdIds.Add(c2.ObjectId); createdHandles.Add(c2.Handle.ToString());
                                createdIds.Add(c3.ObjectId); createdHandles.Add(c3.Handle.ToString());
                            }

                            double fCy = (yDataBot + yBottom) * 0.5;
                            double spanL = colX[3] - x0;
                            string footerLblTxt = pointsPerBlock > 1 ? "إجمالي النقاط (Total Points)" : "إجمالي البلوكات (Total)";
                            MText fLbl = CreateLegendCellMText(ms, tr, unicodeStyleId, footerLblTxt,
                                x0 + spanL * 0.5, fCy, spanL * 0.92, footerH * 0.76, footerH * 0.27, rowH, AttachmentPoint.MiddleCenter, 4, safeTableLayer);
                            MText fCnt = CreateLegendCellMText(ms, tr, unicodeStyleId, targets.Count.ToString(CultureInfo.InvariantCulture),
                                colX[3] + colW[3] * 0.5, fCy, colW[3] * 0.86, footerH * 0.76, footerH * 0.29, rowH, AttachmentPoint.MiddleCenter, 3, safeTableLayer);
                            createdIds.Add(fLbl.ObjectId); createdHandles.Add(fLbl.Handle.ToString());
                            createdIds.Add(fCnt.ObjectId); createdHandles.Add(fCnt.Handle.ToString());
                        }
                    }

                    for (int i = 0; i < targets.Count; i++)
                    {
                        CoordTargetItem item = targets[i];
                        Dictionary<string, object> row = new Dictionary<string, object>
                        {
                            { "index", item.Index },
                            { "block_index", item.BlockIndex },
                            { "point_index", item.SubPointIndex },
                            { "point_label", item.SubPointLabel },
                            { "handle", item.Handle },
                            { "block_name", item.BlockName },
                            { "x", Math.Round(item.ReportX, decimals) },
                            { "y", Math.Round(item.ReportY, decimals) },
                            { "center", Pt2(item.CenterX, item.CenterY) },
                            { "arrow_target", Pt2(item.ArrowX, item.ArrowY) }
                        };
                        if (item.HasCustomPoint)
                            row["local_point"] = Pt2(item.LocalX, item.LocalY);
                        if (Math.Abs(item.InsertX - item.CenterX) > 1e-2 || Math.Abs(item.InsertY - item.CenterY) > 1e-2)
                            row["insertion_point"] = Pt2(item.InsertX, item.InsertY);
                        if (drawCallouts && item.Placed)
                        {
                            row["label_position"] = Pt2(item.TextCenterX, item.ShelfY);
                            row["obstacle_free"] = (item.ObstacleHits == 0);
                        }
                        if (blockRowsOut.Count < 500) blockRowsOut.Add(row);
                    }

                    db.UpdateExt(true);
                    tr.Commit();
                }

                if (createdIds.Count > 0 || erasedIds.Count > 0)
                {
                    CadUndoEntry undo = new CadUndoEntry();
                    undo.ToolName = "annotate_block_coordinates";
                    undo.Summary = "Annotated coordinates (" + blockTargets.Count.ToString(CultureInfo.InvariantCulture) + " blocks, " +
                                   targets.Count.ToString(CultureInfo.InvariantCulture) + " points)";
                    undo.ErasedIds.AddRange(erasedIds);
                    undo.CreatedIds.AddRange(createdIds);
                    PushUndoEntry(doc, undo);
                }

                if (zoom)
                {
                    Extents3d? zoomTarget = null;
                    Extents3d? focusCallouts = calloutsExt ?? targetsExt;
                    if (zoomMode == "table" && tableExt.HasValue)
                        zoomTarget = tableExt;
                    else if (zoomMode == "callouts" && focusCallouts.HasValue)
                        zoomTarget = focusCallouts;
                    else if (zoomMode == "both" || zoomMode == "all")
                    {
                        zoomTarget = focusCallouts ?? tableExt;
                        if (zoomTarget.HasValue && tableExt.HasValue)
                        {
                            Extents3d z = zoomTarget.Value;
                            z.AddExtents(tableExt.Value);
                            zoomTarget = z;
                        }
                    }
                    else
                    {
                        // auto: focus table when only table requested; focus callouts+targets if table is distant so labels stay crisp!
                        if (!drawCallouts && tableExt.HasValue)
                        {
                            zoomTarget = tableExt;
                        }
                        else if (focusCallouts.HasValue)
                        {
                            zoomTarget = focusCallouts;
                            if (tableExt.HasValue)
                            {
                                Extents3d both = focusCallouts.Value;
                                both.AddExtents(tableExt.Value);
                                double fSpan = Math.Max(focusCallouts.Value.MaxPoint.X - focusCallouts.Value.MinPoint.X,
                                                        focusCallouts.Value.MaxPoint.Y - focusCallouts.Value.MinPoint.Y);
                                double bSpan = Math.Max(both.MaxPoint.X - both.MinPoint.X, both.MaxPoint.Y - both.MinPoint.Y);
                                if (bSpan <= Math.Max(1e-3, fSpan) * 2.3) zoomTarget = both;
                            }
                        }
                    }

                    if (zoomTarget.HasValue && IsFiniteBounds(zoomTarget.Value))
                        ZoomToExtents3d(doc, zoomTarget.Value, 1.18);
                    else
                        doc.Editor.Regen();
                }
                else
                {
                    doc.Editor.Regen();
                }
                doc.Editor.UpdateScreen();
            }

            string xlTitle = !string.IsNullOrWhiteSpace(tableTitleArg)
                ? tableTitleArg.Trim()
                : (distinctTargetNames.Count == 1
                    ? ("جدول إحداثيات بلوك " + targets[0].BlockName + " (COORDINATES)")
                    : "جدول إحداثيات البلوكات (COORDINATES SCHEDULE)");
            string xlNumFmt = "0." + new string('0', decimals);
            ExcelSheetData coordSheet;
            if (isGroupedMulti)
            {
                coordSheet = new ExcelSheetData
                {
                    Title = xlTitle,
                    SheetName = distinctTargetNames.Count == 1 ? targets[0].BlockName : "Coordinates",
                    Headers = new List<string> { "رقم البلوك", "اسم البلوك", "النقطة", "إحداثي X", "إحداثي Y" },
                    FooterRow = new List<string>
                    {
                        "", "إجمالي البلوكات / النقاط (Total)", "",
                        blockTargets.Count.ToString(CultureInfo.InvariantCulture) + " blocks",
                        targets.Count.ToString(CultureInfo.InvariantCulture) + " pts"
                    }
                };
                int prevBlk = -1;
                for (int i = 0; i < targets.Count; i++)
                {
                    CoordTargetItem item = targets[i];
                    bool firstOfBlock = (item.BlockIndex != prevBlk);
                    prevBlk = item.BlockIndex;
                    coordSheet.Rows.Add(new List<string>
                    {
                        firstOfBlock ? (numPrefix + item.BlockIndex.ToString(CultureInfo.InvariantCulture)) : "",
                        firstOfBlock ? item.BlockName : "",
                        item.SubPointLabel,
                        item.ReportX.ToString(xlNumFmt, CultureInfo.InvariantCulture),
                        item.ReportY.ToString(xlNumFmt, CultureInfo.InvariantCulture)
                    });
                }
            }
            else
            {
                coordSheet = new ExcelSheetData
                {
                    Title = xlTitle,
                    SheetName = distinctTargetNames.Count == 1 ? targets[0].BlockName : "Coordinates",
                    Headers = new List<string> { (pointsPerBlock > 1 ? "رقم النقطة" : "رقم البلوك"), "اسم البلوك", "إحداثي X", "إحداثي Y" },
                    FooterRow = new List<string>
                    {
                        "", (pointsPerBlock > 1 ? "إجمالي النقاط (Total Points)" : "إجمالي البلوكات (Total)"), "",
                        targets.Count.ToString(CultureInfo.InvariantCulture)
                    }
                };
                for (int i = 0; i < targets.Count; i++)
                {
                    CoordTargetItem item = targets[i];
                    coordSheet.Rows.Add(new List<string>
                    {
                        numPrefix + item.Index.ToString(CultureInfo.InvariantCulture),
                        item.BlockName,
                        item.ReportX.ToString(xlNumFmt, CultureInfo.InvariantCulture),
                        item.ReportY.ToString(xlNumFmt, CultureInfo.InvariantCulture)
                    });
                }
            }
            lock (s_lastCoordLock)
            {
                s_lastCoordTableByDb[db.UnmanagedObject] = coordSheet;
                s_lastGeneratedTableKindByDb[db.UnmanagedObject] = "coordinates";
                if (distinctTargetNames.Count == 1 && !string.IsNullOrWhiteSpace(targets[0].BlockName))
                    s_lastCoordBlockNameByDb[db.UnmanagedObject] = targets[0].BlockName;
            }

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "block_count", blockTargets.Count },
                { "total_count", targets.Count },
                { "points_per_block", pointsPerBlock },
                { "multi_point_mode", multiPointMode },
                { "leader_entity_type", "Leader" },
                { "block_names", new List<string>(distinctTargetNames) },
                { "selection_expanded_from_sample", selectionExpandedFromSample },
                { "sort_order", sortOrder },
                { "sort_order_ar", DescribeCoordSortOrderAr(sortOrder) },
                { "coordinate_source", hasActiveSamplePoints ? "sample_block_points" : coordSource },
                { "arrow_target_mode", hasActiveSamplePoints ? "sample_block_points" : arrowTarget },
                { "callouts_drawn", drawCallouts },
                { "table_drawn", drawTable },
                { "created_count", createdIds.Count },
                { "erased_count", erasedIds.Count },
                { "switched_to_model", switchedToModel },
                { "undo_history_steps", GetUndoHistoryCount(doc) },
                { "coordinates", blockRowsOut }
            };
            if (hasActiveSamplePoints)
            {
                List<object> localPtsOut = new List<object>();
                for (int p = 0; p < activeLocalPoints.Count; p++)
                {
                    localPtsOut.Add(new Dictionary<string, object>
                    {
                        { "label", activeLocalPoints[p].Label },
                        { "x", Round(activeLocalPoints[p].LocalX) },
                        { "y", Round(activeLocalPoints[p].LocalY) }
                    });
                }
                result["local_points"] = localPtsOut;
            }
            if (GetBool(args, "export_excel", false))
            {
                string xlTag = (distinctTargetNames.Count == 1 ? targets[0].BlockName : "blocks") + "_coordinates";
                string xlPath = GetStr(args, "excel_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, xlTag, ".xlsx");
                string savedPath = ExcelTableInterop.WriteSpreadsheet(xlPath, coordSheet);
                result["excel_exported"] = true;
                result["excel_path"] = savedPath;
            }
            if (drawCallouts)
            {
                result["callout_layer"] = safeCalloutLayer;
                result["callout_text_height"] = Round(usedTextH);
                result["obstacle_free_count"] = obstacleFreeCount;
                result["callout_non_overlapping_count"] = calloutFreeCount;
                result["leader_crossing_count"] = crossingCount;
                if (calloutsExt.HasValue)
                {
                    result["callout_bounds"] = new double[]
                    {
                        Round(calloutsExt.Value.MinPoint.X), Round(calloutsExt.Value.MinPoint.Y),
                        Round(calloutsExt.Value.MaxPoint.X), Round(calloutsExt.Value.MaxPoint.Y)
                    };
                }
            }
            if (drawTable)
            {
                result["table_layer"] = safeTableLayer;
                if (tableExt.HasValue)
                {
                    result["table_bounds"] = new double[]
                    {
                        Round(tableExt.Value.MinPoint.X), Round(tableExt.Value.MinPoint.Y),
                        Round(tableExt.Value.MaxPoint.X), Round(tableExt.Value.MaxPoint.Y)
                    };
                }
            }
            if (mainDrawingExt.HasValue)
            {
                result["drawing_bounds"] = new double[]
                {
                    Round(mainDrawingExt.Value.MinPoint.X), Round(mainDrawingExt.Value.MinPoint.Y),
                    Round(mainDrawingExt.Value.MaxPoint.X), Round(mainDrawingExt.Value.MaxPoint.Y)
                };
            }
            if (warnings.Count > 0) result["warnings"] = warnings;
            return Ok(result);
        }

        // ==================== Two-Way Excel <-> AutoCAD Interop Tools ====================

        private sealed class ExtractedTextCell
        {
            public string Text;
            public double CenterX;
            public double CenterY;
            public double MinX;
            public double MaxX;
            public double Height;
        }

        private static string CleanCadTextValue(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw;
            s = s.Replace("\\P", " ").Replace("\\p", " ").Replace("\\~", " ").Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            s = Regex.Replace(s, @"\\[fFcChHwWaAqQtTlLoOkKpP][^;]*;", "");
            s = Regex.Replace(s, @"\\[\\{}]", "");
            s = s.Replace("{", "").Replace("}", "");
            s = s.Replace("%%C", "Ø").Replace("%%c", "Ø").Replace("%%D", "°").Replace("%%d", "°").Replace("%%P", "±").Replace("%%p", "±");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        private static string ExtractEntityTextString(Entity ent)
        {
            if (ent == null) return "";
            MText mt = ent as MText;
            if (mt != null)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(mt.Text))
                        return Regex.Replace(mt.Text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
                }
                catch { }
                return CleanCadTextValue(mt.Contents);
            }
            DBText dt = ent as DBText;
            if (dt != null)
            {
                return CleanCadTextValue(dt.TextString);
            }
            return "";
        }

        private static ExcelSheetData ExtractAutoCadTableData(Table tbl, string sheetName)
        {
            if (tbl == null || tbl.Rows.Count == 0 || tbl.Columns.Count == 0) return null;
            int rowCount = tbl.Rows.Count;
            int colCount = tbl.Columns.Count;
            List<List<string>> rawRows = new List<List<string>>();
            for (int r = 0; r < rowCount; r++)
            {
                List<string> row = new List<string>();
                for (int c = 0; c < colCount; c++)
                {
                    string val = "";
                    try { val = CleanCadTextValue(tbl.Cells[r, c].TextString); }
                    catch { val = ""; }
                    row.Add(val);
                }
                rawRows.Add(row);
            }

            ExcelSheetData data = new ExcelSheetData();
            data.SheetName = string.IsNullOrWhiteSpace(sheetName) ? "AutoCAD_Table" : sheetName;
            int idx = 0;
            // Detect single merged title row at top
            if (rawRows.Count >= 2)
            {
                int nonEmptyFirst = 0;
                string firstText = "";
                for (int c = 0; c < rawRows[0].Count; c++)
                {
                    if (!string.IsNullOrWhiteSpace(rawRows[0][c]))
                    {
                        nonEmptyFirst++;
                        if (string.IsNullOrEmpty(firstText)) firstText = rawRows[0][c];
                    }
                }
                if (nonEmptyFirst == 1 && colCount > 1)
                {
                    data.Title = firstText;
                    idx = 1;
                }
            }
            if (idx < rawRows.Count)
            {
                data.Headers = new List<string>(rawRows[idx]);
                idx++;
            }
            for (int r = idx; r < rawRows.Count; r++)
            {
                data.Rows.Add(new List<string>(rawRows[r]));
            }
            return data;
        }

        private static ExcelSheetData ExtractGridFromTextCells(List<ExtractedTextCell> cells, string defaultTitle, string sheetName)
        {
            if (cells == null || cells.Count == 0) return null;

            List<double> heights = new List<double>();
            bool hasArabicAny = false;
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].Height > 1e-9 && cells[i].Height < 1e9) heights.Add(cells[i].Height);
                if (!hasArabicAny && ExcelTableInterop.HasArabic(cells[i].Text)) hasArabicAny = true;
            }
            heights.Sort();
            double medianH = heights.Count > 0 ? heights[heights.Count / 2] : 2.5;
            double rowTol = Math.Max(1e-5, medianH * 0.72);

            // Sort top-to-bottom (Y descending)
            cells.Sort(delegate(ExtractedTextCell a, ExtractedTextCell b)
            {
                int cy = b.CenterY.CompareTo(a.CenterY);
                if (cy != 0) return cy;
                return a.CenterX.CompareTo(b.CenterX);
            });

            List<List<ExtractedTextCell>> rawRows = new List<List<ExtractedTextCell>>();
            List<double> rowAnchorY = new List<double>();
            for (int i = 0; i < cells.Count; i++)
            {
                ExtractedTextCell cell = cells[i];
                int matchedRow = -1;
                double bestDy = double.MaxValue;
                for (int r = 0; r < rawRows.Count; r++)
                {
                    double dy = Math.Abs(cell.CenterY - rowAnchorY[r]);
                    if (dy <= rowTol && dy < bestDy)
                    {
                        bestDy = dy;
                        matchedRow = r;
                    }
                }
                if (matchedRow < 0)
                {
                    rawRows.Add(new List<ExtractedTextCell> { cell });
                    rowAnchorY.Add(cell.CenterY);
                }
                else
                {
                    rawRows[matchedRow].Add(cell);
                    // Update running average Y
                    double sumY = 0;
                    for (int k = 0; k < rawRows[matchedRow].Count; k++) sumY += rawRows[matchedRow][k].CenterY;
                    rowAnchorY[matchedRow] = sumY / rawRows[matchedRow].Count;
                }
            }

            if (rawRows.Count == 0) return null;

            // Check optional single-cell top title banner
            string detectedTitle = defaultTitle ?? "";
            int startRowIdx = 0;
            if (rawRows.Count >= 2 && rawRows[0].Count == 1)
            {
                bool hasMultiColBelow = false;
                for (int r = 1; r < rawRows.Count; r++)
                {
                    if (rawRows[r].Count > 1) { hasMultiColBelow = true; break; }
                }
                if (hasMultiColBelow)
                {
                    detectedTitle = rawRows[0][0].Text;
                    startRowIdx = 1;
                }
            }

            // Build column anchors from header row + dense rows (skipping 1-cell banners/footers if denser rows exist)
            List<ExtractedTextCell> anchorCandidates = new List<ExtractedTextCell>();
            int maxCellsInRow = 1;
            for (int r = startRowIdx; r < rawRows.Count; r++)
            {
                if (rawRows[r].Count > maxCellsInRow) maxCellsInRow = rawRows[r].Count;
            }
            for (int r = startRowIdx; r < rawRows.Count; r++)
            {
                if (rawRows[r].Count == maxCellsInRow || (r == startRowIdx && rawRows[r].Count >= 2))
                    anchorCandidates.AddRange(rawRows[r]);
            }
            if (anchorCandidates.Count == 0)
            {
                for (int r = startRowIdx; r < rawRows.Count; r++) anchorCandidates.AddRange(rawRows[r]);
            }

            anchorCandidates.Sort(delegate(ExtractedTextCell a, ExtractedTextCell b) { return a.CenterX.CompareTo(b.CenterX); });
            List<double> colCenters = new List<double>();
            List<double> colMinXs = new List<double>();
            List<double> colMaxXs = new List<double>();
            double minColTol = Math.Max(1e-5, medianH * 1.1);
            for (int i = 0; i < anchorCandidates.Count; i++)
            {
                ExtractedTextCell c = anchorCandidates[i];
                int matchCol = -1;
                double bestDx = double.MaxValue;
                for (int col = 0; col < colCenters.Count; col++)
                {
                    bool intervalsOverlap = (c.MinX <= colMaxXs[col] + medianH * 0.25) && (c.MaxX >= colMinXs[col] - medianH * 0.25);
                    double dx = Math.Abs(c.CenterX - colCenters[col]);
                    if ((intervalsOverlap || dx <= minColTol) && dx < bestDx)
                    {
                        bestDx = dx;
                        matchCol = col;
                    }
                }
                if (matchCol < 0)
                {
                    colCenters.Add(c.CenterX);
                    colMinXs.Add(c.MinX);
                    colMaxXs.Add(c.MaxX);
                }
                else
                {
                    colCenters[matchCol] = (colCenters[matchCol] + c.CenterX) * 0.5;
                    if (c.MinX < colMinXs[matchCol]) colMinXs[matchCol] = c.MinX;
                    if (c.MaxX > colMaxXs[matchCol]) colMaxXs[matchCol] = c.MaxX;
                }
            }

            int numCols = colCenters.Count;
            if (numCols == 0) return null;

            // Sort column centers LTR (ascending X), then reverse for Arabic RTL so logical col 0 is rightmost column!
            List<int> colOrder = new List<int>();
            for (int c = 0; c < numCols; c++) colOrder.Add(c);
            colOrder.Sort(delegate(int a, int b) { return colCenters[a].CompareTo(colCenters[b]); });
            if (hasArabicAny && numCols > 1) colOrder.Reverse();

            List<double> orderedColCenters = new List<double>(numCols);
            for (int c = 0; c < numCols; c++) orderedColCenters.Add(colCenters[colOrder[c]]);

            List<List<string>> gridRows = new List<List<string>>();
            for (int r = startRowIdx; r < rawRows.Count; r++)
            {
                string[] cellsInRow = new string[numCols];
                for (int c = 0; c < numCols; c++) cellsInRow[c] = "";

                List<ExtractedTextCell> rowCells = rawRows[r];
                for (int k = 0; k < rowCells.Count; k++)
                {
                    ExtractedTextCell item = rowCells[k];
                    int bestCol = 0;
                    double bestDist = double.MaxValue;
                    for (int c = 0; c < numCols; c++)
                    {
                        double d = Math.Abs(item.CenterX - orderedColCenters[c]);
                        if (d < bestDist) { bestDist = d; bestCol = c; }
                    }
                    if (string.IsNullOrEmpty(cellsInRow[bestCol]))
                        cellsInRow[bestCol] = item.Text;
                    else
                        cellsInRow[bestCol] = cellsInRow[bestCol] + " " + item.Text;
                }
                gridRows.Add(new List<string>(cellsInRow));
            }

            ExcelSheetData result = new ExcelSheetData();
            result.Title = detectedTitle;
            result.SheetName = string.IsNullOrWhiteSpace(sheetName) ? "Table" : sheetName;
            if (gridRows.Count >= 1)
            {
                result.Headers = new List<string>(gridRows[0]);
                for (int r = 1; r < gridRows.Count; r++)
                    result.Rows.Add(gridRows[r]);
            }
            return result;
        }

        private static ExcelSheetData ExtractLayerGridFromModelSpace(BlockTableRecord ms, Transaction tr, string layerName, string sheetName)
        {
            List<ExtractedTextCell> cells = new List<ExtractedTextCell>();
            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (!string.Equals(ent.Layer, layerName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!(ent is DBText) && !(ent is MText)) continue;

                string txt = ExtractEntityTextString(ent);
                if (string.IsNullOrWhiteSpace(txt)) continue;

                Extents3d ex;
                try { ex = ent.GeometricExtents; }
                catch { continue; }
                if (!IsFiniteBounds(ex)) continue;

                double h = (ent is MText) ? ((MText)ent).TextHeight : ((DBText)ent).Height;
                cells.Add(new ExtractedTextCell
                {
                    Text = txt,
                    CenterX = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5,
                    CenterY = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5,
                    MinX = ex.MinPoint.X,
                    MaxX = ex.MaxPoint.X,
                    Height = h > 1e-9 ? h : Math.Max(1.0, ex.MaxPoint.Y - ex.MinPoint.Y)
                });
            }
            if (cells.Count == 0) return null;
            return ExtractGridFromTextCells(cells, "", sheetName);
        }

        private static ExcelSheetData BuildLiveCoordinatesSheet(Document doc, BlockTableRecord ms, Transaction tr, Dictionary<string, bool> layerHiddenFlags, HashSet<string> selectedSet, string blockFilter, string sortOrderRaw, int decimals, string customTitle)
        {
            string sortOrder = NormalizeCoordSortOrder(sortOrderRaw);
            if (string.IsNullOrEmpty(sortOrder)) sortOrder = "left_to_right";
            List<CoordTargetItem> targets = new List<CoordTargetItem>();
            HashSet<string> distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (IsAiAnnotationLayer(ent.Layer)) continue;
                BlockReference br = ent as BlockReference;
                if (br == null) continue;

                string hStr = ent.Handle.ToString();
                if (selectedSet != null && selectedSet.Count > 0 && !selectedSet.Contains(hStr)) continue;

                ObjectId defId = GetBlockDefinitionId(br);
                BlockTableRecord defRec = (!defId.IsNull) ? (tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord) : null;
                string bName = (defRec != null) ? defRec.Name : br.Name;
                if (IsAnonymousBlockName(bName) || IsSystemBlockName(bName)) continue;

                if (!string.IsNullOrEmpty(blockFilter) &&
                    !MatchesPatternOrSubstring(bName, blockFilter) &&
                    !string.Equals(bName, blockFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                Extents3d ex;
                try { ex = ent.GeometricExtents; }
                catch { continue; }
                if (!IsFiniteBounds(ex)) continue;

                double cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                double cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                distinctNames.Add(bName);
                targets.Add(new CoordTargetItem
                {
                    Handle = hStr,
                    BlockName = bName,
                    CenterX = cx,
                    CenterY = cy,
                    ArrowX = cx,
                    ArrowY = cy,
                    ReportX = cx,
                    ReportY = cy,
                    Bounds = ex
                });
            }

            if (targets.Count == 0) return null;

            List<double> widths = new List<double>();
            List<double> heights = new List<double>();
            for (int i = 0; i < targets.Count; i++)
            {
                widths.Add(Math.Max(1e-6, targets[i].Bounds.MaxPoint.X - targets[i].Bounds.MinPoint.X));
                heights.Add(Math.Max(1e-6, targets[i].Bounds.MaxPoint.Y - targets[i].Bounds.MinPoint.Y));
            }
            widths.Sort();
            heights.Sort();
            double medW = widths[widths.Count / 2];
            double medH = heights[heights.Count / 2];
            SortCoordTargets(targets, sortOrder, medW, medH);

            string numFmt = "0." + new string('0', Math.Max(0, Math.Min(6, decimals)));
            string title = !string.IsNullOrWhiteSpace(customTitle)
                ? customTitle
                : (distinctNames.Count == 1
                    ? ("جدول إحداثيات بلوك " + targets[0].BlockName + " (COORDINATES)")
                    : "جدول إحداثيات البلوكات (COORDINATES SCHEDULE)");

            ExcelSheetData sheet = new ExcelSheetData
            {
                Title = title,
                SheetName = distinctNames.Count == 1 ? targets[0].BlockName : "Coordinates",
                Headers = new List<string> { "رقم البلوك", "اسم البلوك", "إحداثي X", "إحداثي Y" },
                FooterRow = new List<string> { "", "إجمالي البلوكات (Total)", "", targets.Count.ToString(CultureInfo.InvariantCulture) }
            };
            for (int i = 0; i < targets.Count; i++)
            {
                CoordTargetItem item = targets[i];
                sheet.Rows.Add(new List<string>
                {
                    "#" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    item.BlockName,
                    item.ReportX.ToString(numFmt, CultureInfo.InvariantCulture),
                    item.ReportY.ToString(numFmt, CultureInfo.InvariantCulture)
                });
            }
            return sheet;
        }

        private static ExcelSheetData BuildLiveBlockLegendSheet(BlockTableRecord ms, Transaction tr, Dictionary<string, bool> layerHiddenFlags, string customTitle)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (IsAiAnnotationLayer(ent.Layer)) continue;
                BlockReference br = ent as BlockReference;
                if (br == null) continue;
                ObjectId defId = GetBlockDefinitionId(br);
                BlockTableRecord defRec = (!defId.IsNull) ? (tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord) : null;
                string bName = (defRec != null) ? defRec.Name : br.Name;
                if (IsAnonymousBlockName(bName) || IsSystemBlockName(bName)) continue;

                int c;
                counts.TryGetValue(bName, out c);
                counts[bName] = c + 1;
            }
            if (counts.Count == 0) return null;

            List<string> names = new List<string>(counts.Keys);
            names.Sort(delegate(string a, string b)
            {
                int cmp = counts[b].CompareTo(counts[a]);
                return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            ExcelSheetData sheet = new ExcelSheetData
            {
                Title = !string.IsNullOrWhiteSpace(customTitle) ? customTitle : "جدول حصر ومفاتيح البلوكات (BLOCK INVENTORY)",
                SheetName = "Block_Inventory",
                Headers = new List<string> { "م", "اسم البلوك", "الكمية" }
            };
            int total = 0;
            for (int i = 0; i < names.Count; i++)
            {
                int qty = counts[names[i]];
                total += qty;
                sheet.Rows.Add(new List<string>
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    names[i],
                    qty.ToString(CultureInfo.InvariantCulture)
                });
            }
            sheet.FooterRow = new List<string> { "", "الإجمالي الكلي للبلوكات (Total)", total.ToString(CultureInfo.InvariantCulture) };
            return sheet;
        }

        private static List<string> ExtractStringListArg(Dictionary<string, object> args, string key)
        {
            List<string> outList = new List<string>();
            if (args == null) return outList;
            object raw;
            if (!args.TryGetValue(key, out raw) || raw == null) return outList;
            System.Collections.IEnumerable seq = raw as System.Collections.IEnumerable;
            if (seq == null || raw is string) return outList;
            foreach (object item in seq)
            {
                outList.Add(item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : "");
            }
            return outList;
        }

        private static ExcelSheetData BuildCustomSheetFromArgs(Dictionary<string, object> args, string docName)
        {
            List<string> headers = ExtractStringListArg(args, "headers");
            object rawRowsObj;
            if (!args.TryGetValue("rows", out rawRowsObj) || rawRowsObj == null) return null;
            System.Collections.IEnumerable rawRows = rawRowsObj as System.Collections.IEnumerable;
            if (rawRows == null || rawRowsObj is string) return null;

            List<List<string>> rows = new List<List<string>>();
            foreach (object rowObj in rawRows)
            {
                if (rowObj == null) continue;
                Dictionary<string, object> dictRow = rowObj as Dictionary<string, object>;
                if (dictRow != null)
                {
                    if (headers.Count == 0)
                    {
                        foreach (string k in dictRow.Keys) headers.Add(k);
                    }
                    List<string> rVals = new List<string>();
                    for (int c = 0; c < headers.Count; c++)
                    {
                        object cellVal;
                        dictRow.TryGetValue(headers[c], out cellVal);
                        rVals.Add(cellVal != null ? Convert.ToString(cellVal, CultureInfo.InvariantCulture) : "");
                    }
                    rows.Add(rVals);
                    continue;
                }

                System.Collections.IEnumerable arrRow = rowObj as System.Collections.IEnumerable;
                if (arrRow != null && !(rowObj is string))
                {
                    List<string> rVals = new List<string>();
                    foreach (object cv in arrRow)
                        rVals.Add(cv != null ? Convert.ToString(cv, CultureInfo.InvariantCulture) : "");
                    rows.Add(rVals);
                }
            }

            if (headers.Count == 0 && rows.Count == 0) return null;
            ExcelSheetData sheet = new ExcelSheetData
            {
                Title = GetStr(args, "title") ?? "",
                SheetName = GetStr(args, "sheet_name") ?? "Export",
                Headers = headers,
                Rows = rows
            };
            List<string> footer = ExtractStringListArg(args, "footer_row");
            if (footer.Count > 0) sheet.FooterRow = footer;
            return sheet;
        }

        private static Dictionary<string, object> ExportToExcel(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            string reqSource = (GetStr(args, "source") ?? "auto").Trim().ToLowerInvariant();
            string reqFormat = (GetStr(args, "format") ?? "xlsx").Trim().ToLowerInvariant();
            string filePath = GetStr(args, "file_path") ?? GetStr(args, "output_path") ?? GetStr(args, "path");
            bool browse = GetBool(args, "browse", false) || GetBool(args, "prompt_save_dialog", false);
            bool openFile = GetBool(args, "open_file", false);
            string customTitle = GetStr(args, "title");
            string customSheet = GetStr(args, "sheet_name");
            string blockFilter = GetStr(args, "block_name");
            string sortOrder = GetStr(args, "sort_order") ?? "left_to_right";
            int decimals = Math.Max(0, Math.Min(6, GetInt(args, "decimals", 2)));

            if (reqFormat != "csv" && reqFormat != "xlsx") reqFormat = "xlsx";
            if (!string.IsNullOrWhiteSpace(filePath) && filePath.Trim().ToLowerInvariant().EndsWith(".csv"))
                reqFormat = "csv";

            ExcelSheetData sheet = null;
            string sourceUsed = reqSource;

            // 1. Custom tabular data passed explicitly
            if (reqSource == "custom" || (args != null && args.ContainsKey("rows")))
            {
                sheet = BuildCustomSheetFromArgs(args, doc.Name);
                if (sheet != null) sourceUsed = "custom";
            }

            // 2. Inspect selection / cache / ModelSpace tables
            if (sheet == null)
            {
                List<string> selHandles = GetSelectedHandleStrings(doc);
                HashSet<string> selSet = new HashSet<string>(selHandles, StringComparer.OrdinalIgnoreCase);

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);

                    if (reqSource == "selected_table" || (reqSource == "auto" && selHandles.Count > 0))
                    {
                        Table selectedTbl = null;
                        List<ExtractedTextCell> selTexts = new List<ExtractedTextCell>();
                        bool hasSelectedBlock = false;
                        foreach (string hStr in selHandles)
                        {
                            ObjectId oid = ResolveHandle(db, hStr);
                            if (oid.IsNull || oid.IsErased) continue;
                            Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased) continue;
                            if (ent is Table && selectedTbl == null)
                            {
                                selectedTbl = (Table)ent;
                                break;
                            }
                            if (ent is DBText || ent is MText)
                            {
                                string txt = ExtractEntityTextString(ent);
                                if (string.IsNullOrWhiteSpace(txt)) continue;
                                try
                                {
                                    Extents3d ex = ent.GeometricExtents;
                                    if (!IsFiniteBounds(ex)) continue;
                                    double th = (ent is MText) ? ((MText)ent).TextHeight : ((DBText)ent).Height;
                                    selTexts.Add(new ExtractedTextCell
                                    {
                                        Text = txt,
                                        CenterX = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5,
                                        CenterY = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5,
                                        MinX = ex.MinPoint.X,
                                        MaxX = ex.MaxPoint.X,
                                        Height = th > 1e-9 ? th : 2.5
                                    });
                                }
                                catch { }
                            }
                            else if (ent is BlockReference)
                            {
                                hasSelectedBlock = true;
                            }
                        }

                        if (selectedTbl != null)
                        {
                            sheet = ExtractAutoCadTableData(selectedTbl, customSheet ?? "Selected_Table");
                            sourceUsed = "selected_autocad_table";
                        }
                        else if (selTexts.Count >= 2)
                        {
                            sheet = ExtractGridFromTextCells(selTexts, customTitle ?? "جدول مستخرج من التحديد", customSheet ?? "Selected_Table");
                            sourceUsed = "selected_table_texts";
                        }
                        else if (reqSource == "selected_table" && hasSelectedBlock)
                        {
                            sheet = BuildLiveCoordinatesSheet(doc, ms, tr, layerHiddenFlags, selSet, blockFilter, sortOrder, decimals, customTitle);
                            sourceUsed = "selected_blocks_coordinates";
                        }
                    }

                    if (sheet == null && (reqSource == "coordinates" || reqSource == "panel_schedule" || reqSource == "boq" || reqSource == "lighting" || reqSource == "auto"))
                    {
                        ExcelSheetData cachedCoord = null;
                        ExcelSheetData cachedLegend = null;
                        ExcelSheetData cachedPanel = null;
                        ExcelSheetData cachedBoq = null;
                        ExcelSheetData cachedLighting = null;
                        string lastKind = null;
                        lock (s_lastCoordLock)
                        {
                            s_lastCoordTableByDb.TryGetValue(db.UnmanagedObject, out cachedCoord);
                            s_lastLegendTableByDb.TryGetValue(db.UnmanagedObject, out cachedLegend);
                            s_lastPanelScheduleByDb.TryGetValue(db.UnmanagedObject, out cachedPanel);
                            s_lastBoqTableByDb.TryGetValue(db.UnmanagedObject, out cachedBoq);
                            s_lastLightingTableByDb.TryGetValue(db.UnmanagedObject, out cachedLighting);
                            s_lastGeneratedTableKindByDb.TryGetValue(db.UnmanagedObject, out lastKind);
                        }

                        if (reqSource == "lighting")
                        {
                            if (cachedLighting != null) { sheet = cachedLighting; sourceUsed = "cached_lighting_schedule"; }
                            else
                            {
                                sheet = ExtractLayerGridFromModelSpace(ms, tr, "_AI_LIGHTING_TABLE", "Lighting_Schedule");
                                if (sheet != null) sourceUsed = "drawing_lighting_table_layer";
                            }
                        }
                        else if (reqSource == "panel_schedule")
                        {
                            if (cachedPanel != null) { sheet = cachedPanel; sourceUsed = "cached_panel_schedule"; }
                            else
                            {
                                sheet = ExtractLayerGridFromModelSpace(ms, tr, "_AI_PANEL_SCHEDULE", "Panel_Schedule");
                                if (sheet != null) sourceUsed = "drawing_panel_schedule_layer";
                            }
                        }
                        else if (reqSource == "boq")
                        {
                            if (cachedBoq != null) { sheet = cachedBoq; sourceUsed = "cached_boq_table"; }
                            else
                            {
                                sheet = ExtractLayerGridFromModelSpace(ms, tr, "_AI_BOQ_TABLE", "BOQ_Takeoff");
                                if (sheet != null) sourceUsed = "drawing_boq_table_layer";
                            }
                        }
                        else if (reqSource == "coordinates")
                        {
                            if (string.IsNullOrWhiteSpace(blockFilter) && cachedCoord != null)
                            {
                                sheet = cachedCoord;
                                sourceUsed = "cached_coordinates_table";
                            }
                            else
                            {
                                if (string.IsNullOrWhiteSpace(blockFilter))
                                    sheet = ExtractLayerGridFromModelSpace(ms, tr, "_AI_COORD_TABLE", "Coordinates");
                                if (sheet != null)
                                    sourceUsed = "drawing_coord_table_layer";
                                else
                                {
                                    sheet = BuildLiveCoordinatesSheet(doc, ms, tr, layerHiddenFlags, selSet, blockFilter, sortOrder, decimals, customTitle);
                                    sourceUsed = "live_block_coordinates";
                                }
                            }
                        }
                        else // auto
                        {
                            if (string.Equals(lastKind, "lighting", StringComparison.OrdinalIgnoreCase) && cachedLighting != null)
                            {
                                sheet = cachedLighting;
                                sourceUsed = "cached_lighting_schedule";
                            }
                            else if (string.Equals(lastKind, "panel_schedule", StringComparison.OrdinalIgnoreCase) && cachedPanel != null)
                            {
                                sheet = cachedPanel;
                                sourceUsed = "cached_panel_schedule";
                            }
                            else if (string.Equals(lastKind, "boq", StringComparison.OrdinalIgnoreCase) && cachedBoq != null)
                            {
                                sheet = cachedBoq;
                                sourceUsed = "cached_boq_table";
                            }
                            else if (string.Equals(lastKind, "block_legend", StringComparison.OrdinalIgnoreCase) && cachedLegend != null)
                            {
                                sheet = cachedLegend;
                                sourceUsed = "cached_block_legend";
                            }
                            else if (cachedCoord != null)
                            {
                                sheet = cachedCoord;
                                sourceUsed = "cached_coordinates_table";
                            }
                            else if (cachedPanel != null)
                            {
                                sheet = cachedPanel;
                                sourceUsed = "cached_panel_schedule";
                            }
                            else if (cachedBoq != null)
                            {
                                sheet = cachedBoq;
                                sourceUsed = "cached_boq_table";
                            }
                            else if (cachedLegend != null)
                            {
                                sheet = cachedLegend;
                                sourceUsed = "cached_block_legend";
                            }
                        }
                    }

                    if (sheet == null && reqSource == "block_legend")
                    {
                        ExcelSheetData cachedLegend = null;
                        lock (s_lastCoordLock) { s_lastLegendTableByDb.TryGetValue(db.UnmanagedObject, out cachedLegend); }
                        if (cachedLegend != null)
                        {
                            sheet = cachedLegend;
                            sourceUsed = "cached_block_legend";
                        }
                        else
                        {
                            sheet = ExtractLayerGridFromModelSpace(ms, tr, "_AI_BLOCK_LEGEND", "Block_Legend");
                            if (sheet != null)
                            {
                                sourceUsed = "drawing_block_legend_layer";
                            }
                            else
                            {
                                sheet = BuildLiveBlockLegendSheet(ms, tr, layerHiddenFlags, customTitle);
                                sourceUsed = "live_block_inventory";
                            }
                        }
                    }

                    // Auto fallbacks from live ModelSpace tables or entities
                    if (sheet == null && reqSource == "auto")
                    {
                        sheet = ExtractLayerGridFromModelSpace(ms, tr, "_AI_COORD_TABLE", "Coordinates");
                        if (sheet != null) sourceUsed = "drawing_coord_table_layer";
                    }
                    if (sheet == null && reqSource == "auto")
                    {
                        sheet = ExtractLayerGridFromModelSpace(ms, tr, "_AI_BLOCK_LEGEND", "Block_Legend");
                        if (sheet != null) sourceUsed = "drawing_block_legend_layer";
                    }
                    if (sheet == null && reqSource == "auto")
                    {
                        sheet = ExtractLayerGridFromModelSpace(ms, tr, "_AI_EXCEL_TABLE", "Imported_Table");
                        if (sheet != null) sourceUsed = "drawing_excel_table_layer";
                    }
                    if (sheet == null && reqSource == "auto")
                    {
                        foreach (ObjectId id in ms)
                        {
                            Table tbl = tr.GetObject(id, OpenMode.ForRead) as Table;
                            if (tbl == null || tbl.IsErased) continue;
                            sheet = ExtractAutoCadTableData(tbl, customSheet ?? "AutoCAD_Table");
                            if (sheet != null) { sourceUsed = "modelspace_autocad_table"; break; }
                        }
                    }
                    if (sheet == null && reqSource == "auto")
                    {
                        if (selSet.Count > 0)
                        {
                            sheet = BuildLiveCoordinatesSheet(doc, ms, tr, layerHiddenFlags, selSet, blockFilter, sortOrder, decimals, customTitle);
                            if (sheet != null) sourceUsed = "selected_blocks_coordinates";
                        }
                    }
                    if (sheet == null && reqSource == "auto")
                    {
                        sheet = BuildLiveBlockLegendSheet(ms, tr, layerHiddenFlags, customTitle);
                        if (sheet != null) sourceUsed = "live_block_inventory";
                    }

                    tr.Commit();
                }
            }

            if (sheet == null || ((sheet.Headers == null || sheet.Headers.Count == 0) && (sheet.Rows == null || sheet.Rows.Count == 0)))
                return Err("لم يتم العثور على جدول أو بلوكات في الرسم لتصديرها إلى Excel. قم باستخراج الإحداثيات أو إنشاء جدول البلوكات أو تحديد الجدول داخل الرسم أولاً.");

            if (!string.IsNullOrWhiteSpace(customTitle)) sheet.Title = customTitle;
            if (!string.IsNullOrWhiteSpace(customSheet)) sheet.SheetName = customSheet;

            if (string.IsNullOrWhiteSpace(filePath))
            {
                string tag = !string.IsNullOrWhiteSpace(sheet.SheetName) ? sheet.SheetName : "autocad_table";
                filePath = ExcelTableInterop.ResolveDefaultExportPath(doc, tag, "." + reqFormat);
            }
            else if (reqFormat == "csv" && !filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                filePath = Path.ChangeExtension(filePath, ".csv");
            }

            if (browse)
            {
                string initDir = Path.GetDirectoryName(filePath);
                string defName = Path.GetFileName(filePath);
                string picked = ExcelTableInterop.BrowseSaveSpreadsheetPath(defName, initDir);
                if (string.IsNullOrWhiteSpace(picked))
                    return Err("تم إلغاء حفظ ملف Excel من قِبل المستخدم.");
                filePath = picked;
                if (filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) reqFormat = "csv";
                else reqFormat = "xlsx";
            }

            string savedPath = ExcelTableInterop.WriteSpreadsheet(filePath, sheet);
            bool opened = openFile && ExcelTableInterop.TryOpenWithShell(savedPath);

            int colCount = sheet.Headers != null ? sheet.Headers.Count : 0;
            if (sheet.Rows != null)
            {
                for (int r = 0; r < sheet.Rows.Count; r++)
                    if (sheet.Rows[r] != null && sheet.Rows[r].Count > colCount) colCount = sheet.Rows[r].Count;
            }

            List<object> previewRows = new List<object>();
            if (sheet.Rows != null)
            {
                int maxPrev = Math.Min(10, sheet.Rows.Count);
                for (int r = 0; r < maxPrev; r++) previewRows.Add(sheet.Rows[r]);
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "file_path", savedPath },
                { "format", reqFormat },
                { "source_used", sourceUsed },
                { "sheet_name", sheet.SheetName ?? "" },
                { "title", sheet.Title ?? "" },
                { "column_count", colCount },
                { "row_count", sheet.Rows != null ? sheet.Rows.Count : 0 },
                { "headers", sheet.Headers ?? new List<string>() },
                { "has_footer", sheet.FooterRow != null && sheet.FooterRow.Count > 0 },
                { "arabic_rtl", ExcelTableInterop.IsSpreadsheetArabic(sheet) },
                { "opened_with_shell", opened },
                { "preview_rows", previewRows }
            };
            return Ok(res);
        }

        private static double MeasureTextSpanInUnits(string text, double textH)
        {
            if (string.IsNullOrEmpty(text)) return textH * 1.5;
            double units = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '\u0600' && c <= '\u06FF') units += 0.68;
                else if (c == ' ') units += 0.35;
                else if (char.IsUpper(c)) units += 0.72;
                else if (char.IsDigit(c) || c == '.' || c == '-' || c == '#') units += 0.60;
                else units += 0.62;
            }
            return Math.Max(textH * 2.4, units * textH);
        }

		private static Dictionary<string, object> ImportExcelTable(Document doc, Dictionary<string, object> args)
		{
			Database database = doc.Database;
			string text = GetStr(args, "file_path") ?? GetStr(args, "path") ?? GetStr(args, "excel_path");
			bool @bool = GetBool(args, "browse", false);
			string str = GetStr(args, "sheet_name");
			string str2 = GetStr(args, "title");
			bool bool2 = GetBool(args, "has_header", true);
			bool bool3 = GetBool(args, "use_autocad_table", false);
			string requestedLayer = GetStr(args, "layer") ?? "_AI_EXCEL_TABLE";
			bool bool4 = GetBool(args, "replace_existing", false);
			string text2 = (GetStr(args, "position") ?? "outside_right").Trim().ToLowerInvariant();
			double[] array = GetDoubleArray(args, "insert_at") ?? GetDoubleArray(args, "position");
			double? nullableDouble = GetNullableDouble(args, "gap");
			double? nullableDouble2 = GetNullableDouble(args, "text_height");
			int maxRows = Math.Max(1, Math.Min(500, GetInt(args, "max_rows", 250)));
			int maxCols = Math.Max(1, Math.Min(40, GetInt(args, "max_cols", 25)));
			bool bool5 = GetBool(args, "zoom", true);
			string text3 = (GetStr(args, "zoom_mode") ?? "table").Trim().ToLowerInvariant();
			if (@bool || string.IsNullOrWhiteSpace(text))
			{
				string initialDir = null;
				try
				{
					if (!string.IsNullOrWhiteSpace(doc.Name) && Path.IsPathRooted(doc.Name))
					{
						initialDir = Path.GetDirectoryName(doc.Name);
					}
				}
				catch
				{
				}
				string text4 = ExcelTableInterop.BrowseOpenSpreadsheetPath(initialDir);
				if (string.IsNullOrWhiteSpace(text4))
				{
					return Err("لم يتم تحديد ملف Excel للاستيراد (تم إلغاء نافذة الاختيار أو لم ي\u064fمر\u0651ر file_path).");
				}
				text = text4;
			}
			if (!File.Exists(text))
			{
				return Err("ملف Excel المطلوب غير موجود: " + text);
			}
			ExcelSheetData excelSheetData = ExcelTableInterop.ReadSpreadsheet(text, str, maxRows, maxCols);
			List<string> list = new List<string>(excelSheetData.Headers ?? new List<string>());
			List<List<string>> list2 = new List<List<string>>();
			if (bool2)
			{
				if (excelSheetData.Rows != null)
				{
					for (int i = 0; i < excelSheetData.Rows.Count; i++)
					{
						list2.Add(new List<string>(excelSheetData.Rows[i]));
					}
				}
			}
			else
			{
				if (list.Count > 0)
				{
					list2.Add(list);
				}
				if (excelSheetData.Rows != null)
				{
					for (int i = 0; i < excelSheetData.Rows.Count; i++)
					{
						list2.Add(new List<string>(excelSheetData.Rows[i]));
					}
				}
				list = new List<string>();
			}
			int count = list.Count;
			for (int i = 0; i < list2.Count; i++)
			{
				if (list2[i].Count > count)
				{
					count = list2[i].Count;
				}
			}
			if (count == 0 || (list.Count == 0 && list2.Count == 0))
			{
				return Err("ملف Excel فارغ أو لا يحتوي على صفوف بيانات قابلة للرسم في الورقة المختارة.");
			}
			while (list.Count < count && bool2)
			{
				list.Add("");
			}
			for (int i = 0; i < list2.Count; i++)
			{
				while (list2[i].Count < count)
				{
					list2[i].Add("");
				}
			}
			bool flag = ExcelTableInterop.IsSpreadsheetArabic(excelSheetData);
			string text5 = str2;
			if (text5 == null)
			{
				text5 = ((!string.IsNullOrWhiteSpace(excelSheetData.Title)) ? excelSheetData.Title : (Path.GetFileNameWithoutExtension(text) + " (" + (excelSheetData.SheetName ?? "Sheet1") + ")"));
			}
			bool flag2 = false;
			try
			{
				if (!database.TileMode)
				{
					database.TileMode = true;
					flag2 = true;
				}
			}
			catch
			{
			}
			List<ObjectId> list3 = new List<ObjectId>();
			List<ObjectId> list4 = new List<ObjectId>();
			Extents3d? extents3d = null;
			string text6 = "";
			double num = 2.5;
			using (doc.LockDocument())
			{
				using (Transaction transaction = database.TransactionManager.StartTransaction())
				{
					BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
					BlockTableRecord blockTableRecord = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
					Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(database, transaction);
					text6 = EnsureLayerSafe(database, transaction, requestedLayer, 7, "Continuous");
					if (bool4)
					{
						foreach (ObjectId item in blockTableRecord)
						{
							Entity entity = transaction.GetObject(item, OpenMode.ForRead) as Entity;
							if (!(entity == null) && !entity.IsErased && string.Equals(entity.Layer, text6, StringComparison.OrdinalIgnoreCase))
							{
								Entity entity2 = (Entity)transaction.GetObject(item, OpenMode.ForWrite);
								entity2.Erase();
								list3.Add(item);
							}
						}
					}
					Extents3d? extents3d2 = null;
					foreach (ObjectId item2 in blockTableRecord)
					{
						Entity entity3 = transaction.GetObject(item2, OpenMode.ForRead) as Entity;
						if (entity3 == null || entity3.IsErased || IsAiAnnotationLayer(entity3.Layer) || !IsEntityVisible(entity3, layerHiddenFlags))
						{
							continue;
						}
						try
						{
							Extents3d geometricExtents = entity3.GeometricExtents;
							if (IsFiniteBounds(geometricExtents))
							{
								if (!extents3d2.HasValue)
								{
									extents3d2 = geometricExtents;
									continue;
								}
								Extents3d value = extents3d2.Value;
								value.AddExtents(geometricExtents);
								extents3d2 = value;
							}
						}
						catch
						{
						}
					}
					Extents3d? extents3d3 = CollectLayerExtents(blockTableRecord, transaction, "_AI_BLOCK_LEGEND");
					Extents3d? extents3d4 = CollectLayerExtents(blockTableRecord, transaction, "_AI_COORD_TABLE");
					Extents3d? extents3d5 = (bool4 ? null : CollectLayerExtents(blockTableRecord, transaction, text6));
					double num2 = (extents3d2.HasValue ? Math.Max(1.0, extents3d2.Value.MaxPoint.X - extents3d2.Value.MinPoint.X) : 200.0);
					double num3 = (extents3d2.HasValue ? Math.Max(1.0, extents3d2.Value.MaxPoint.Y - extents3d2.Value.MinPoint.Y) : 140.0);
					double num4 = Math.Max(num2, num3);
					double num5 = Convert.ToDouble(GetUnitsInfo(database)["units_per_cm"], CultureInfo.InvariantCulture);
					if (nullableDouble2.HasValue && nullableDouble2.Value > 1E-06)
					{
						num = nullableDouble2.Value;
					}
					else
					{
						double val = ((num4 > 1.0) ? Math.Max(0.45 * num5, num4 * 0.0115) : (2.5 * num5));
						num = Math.Min(Math.Max(val, 0.25 * num5), Math.Max(5.0 * num5, num3 * 0.055));
					}
					double num6 = num * 2.55;
					double num7 = ((!string.IsNullOrWhiteSpace(text5)) ? (num * 3.1) : 0.0);
					double num8 = ((bool2 && list.Count > 0) ? (num * 2.75) : 0.0);
					double[] array2 = new double[count];
					for (int j = 0; j < count; j++)
					{
						double num9 = num * 4.5;
						if (bool2 && j < list.Count)
						{
							num9 = Math.Max(num9, MeasureTextSpanInUnits(list[j], num * 1.05));
						}
						for (int i = 0; i < list2.Count; i++)
						{
							string text7 = list2[i][j];
							num9 = Math.Max(num9, MeasureTextSpanInUnits(text7, num));
						}
						array2[j] = Math.Min(num * 38.0, num9 + num * 2.2);
					}
					double num10 = 0.0;
					for (int j = 0; j < count; j++)
					{
						num10 += array2[j];
					}
					if (!string.IsNullOrWhiteSpace(text5))
					{
						double num11 = MeasureTextSpanInUnits(text5, num * 1.15) + num * 3.0;
						if (num11 > num10 && num10 > 1E-06)
						{
							double num12 = num11 / num10;
							num10 = num11;
							for (int j = 0; j < count; j++)
							{
								array2[j] *= num12;
							}
						}
					}
					double num13 = num7 + num8 + (double)list2.Count * num6;
					double num14 = ((nullableDouble.HasValue && nullableDouble.Value > 0.0) ? nullableDouble.Value : Math.Max(num10 * 0.06, num2 * 0.06));
					double num15 = 0.0;
					double num16 = num13;
					if (array != null && array.Length >= 2)
					{
						num15 = array[0];
						num16 = array[1];
					}
					else if (extents3d2.HasValue)
					{
						Extents3d value2 = extents3d2.Value;
						if (text2 == "outside_left" || text2 == "left")
						{
							num15 = value2.MinPoint.X - num14 - num10;
							num16 = value2.MaxPoint.Y;
						}
						else if (text2 == "outside_top" || text2 == "top")
						{
							num15 = value2.MinPoint.X;
							num16 = value2.MaxPoint.Y + num14 + num13;
						}
						else if (text2 == "outside_bottom" || text2 == "bottom")
						{
							num15 = value2.MinPoint.X;
							num16 = value2.MinPoint.Y - num14;
						}
						else
						{
							num15 = value2.MaxPoint.X + num14;
							num16 = value2.MaxPoint.Y;
						}
					}
					List<Extents3d> list5 = new List<Extents3d>();
					if (extents3d3.HasValue && IsFiniteBounds(extents3d3.Value))
					{
						list5.Add(extents3d3.Value);
					}
					if (extents3d4.HasValue && IsFiniteBounds(extents3d4.Value))
					{
						list5.Add(extents3d4.Value);
					}
					if (extents3d5.HasValue && IsFiniteBounds(extents3d5.Value))
					{
						list5.Add(extents3d5.Value);
					}
					if (array == null && list5.Count > 0)
					{
						for (int k = 0; k < list5.Count + 2; k++)
						{
							bool flag3 = false;
							double num17 = num15;
							double num18 = num16;
							double num19 = num15 + num10;
							double num20 = num16 - num13;
							for (int l = 0; l < list5.Count; l++)
							{
								Extents3d extents3d6 = list5[l];
								if (RectsOverlap(num17 - num14 * 0.2, num20 - num14 * 0.2, num19 + num14 * 0.2, num18 + num14 * 0.2, extents3d6.MinPoint.X, extents3d6.MinPoint.Y, extents3d6.MaxPoint.X, extents3d6.MaxPoint.Y))
								{
									num16 = extents3d6.MinPoint.Y - num14 * 0.65;
									flag3 = true;
									break;
								}
							}
							if (!flag3)
							{
								break;
							}
						}
					}
					if (bool3)
					{
						Table table = new Table();
						table.Layer = text6;
						table.TableStyle = database.Tablestyle;
						int numRows = ((num7 > 0.0) ? 1 : 0) + ((num8 > 0.0) ? 1 : 0) + list2.Count;
						table.SetSize(numRows, count);
						table.Position = new Point3d(num15, num16, 0.0);
						for (int j = 0; j < count; j++)
						{
							int num21 = (flag ? (count - 1 - j) : j);
							table.Columns[j].Width = array2[num21];
						}
						int num22 = 0;
						if (num7 > 0.0)
						{
							table.Rows[num22].Height = num7;
							if (count > 1)
							{
								table.MergeCells(CellRange.Create(table, num22, 0, num22, count - 1));
							}
							table.Cells[num22, 0].TextString = text5;
							table.Cells[num22, 0].TextHeight = num * 1.15;
							table.Cells[num22, 0].Alignment = CellAlignment.MiddleCenter;
							num22++;
						}
						if (num8 > 0.0)
						{
							table.Rows[num22].Height = num8;
							for (int j = 0; j < count; j++)
							{
								int num21 = (flag ? (count - 1 - j) : j);
								table.Cells[num22, j].TextString = list[num21] ?? "";
								table.Cells[num22, j].TextHeight = num * 1.05;
								table.Cells[num22, j].Alignment = CellAlignment.MiddleCenter;
							}
							num22++;
						}
						for (int m = 0; m < list2.Count; m++)
						{
							table.Rows[num22].Height = num6;
							for (int j = 0; j < count; j++)
							{
								int num21 = (flag ? (count - 1 - j) : j);
								table.Cells[num22, j].TextString = list2[m][num21] ?? "";
								table.Cells[num22, j].TextHeight = num;
								table.Cells[num22, j].Alignment = CellAlignment.MiddleCenter;
							}
							num22++;
						}
						table.GenerateLayout();
						blockTableRecord.AppendEntity(table);
						transaction.AddNewlyCreatedDBObject(table, true);
						list4.Add(table.ObjectId);
						try
						{
							Extents3d geometricExtents2 = table.GeometricExtents;
							if (IsFiniteBounds(geometricExtents2))
							{
								extents3d = geometricExtents2;
							}
						}
						catch
						{
						}
					}
					else
					{
						ObjectId unicodeStyleId = EnsureUnicodeTextStyle(database, transaction);
						double num23 = num15;
						double num24 = num15 + num10;
						double num25 = num16;
						double y = num16 - num13;
						Extents3d extents3d7 = new Extents3d(new Point3d(num23, y, 0.0), new Point3d(num24, num25, 0.0));
						Autodesk.AutoCAD.DatabaseServices.Polyline polyline = CreateFramePolyline(extents3d7, text6, 7);
						polyline.ConstantWidth = num * 0.06;
						blockTableRecord.AppendEntity(polyline);
						transaction.AddNewlyCreatedDBObject(polyline, true);
						list4.Add(polyline.ObjectId);
						extents3d = extents3d7;
						double[] array3 = new double[count];
						double[] array4 = new double[count];
						double num26 = num23;
						for (int n = 0; n < count; n++)
						{
							int num21 = (flag ? (count - 1 - n) : n);
							array3[n] = num26;
							array4[n] = array2[num21];
							num26 += array4[n];
						}
						double num27 = num25;
						if (num7 > 0.0)
						{
							double num28 = num27 - num7;
							MText mText = CreateLegendCellMText(blockTableRecord, transaction, unicodeStyleId, text5, (num23 + num24) * 0.5, (num27 + num28) * 0.5, num10, num7, num * 1.15, num7, AttachmentPoint.MiddleCenter, 2, text6);
							list4.Add(mText.ObjectId);
							Line line = new Line(new Point3d(num23, num28, 0.0), new Point3d(num24, num28, 0.0));
							line.Layer = text6;
							line.ColorIndex = 7;
							blockTableRecord.AppendEntity(line);
							transaction.AddNewlyCreatedDBObject(line, true);
							list4.Add(line.ObjectId);
							num27 = num28;
						}
						double y2 = num27;
						if (num8 > 0.0)
						{
							double num28 = num27 - num8;
							for (int n = 0; n < count; n++)
							{
								int num21 = (flag ? (count - 1 - n) : n);
								string rawText = list[num21] ?? "";
								double centerX = array3[n] + array4[n] * 0.5;
								MText mText2 = CreateLegendCellMText(blockTableRecord, transaction, unicodeStyleId, rawText, centerX, (num27 + num28) * 0.5, array4[n], num8, num * 1.02, num8, AttachmentPoint.MiddleCenter, 4, text6);
								list4.Add(mText2.ObjectId);
							}
							Line line = new Line(new Point3d(num23, num28, 0.0), new Point3d(num24, num28, 0.0));
							line.Layer = text6;
							line.ColorIndex = 7;
							blockTableRecord.AppendEntity(line);
							transaction.AddNewlyCreatedDBObject(line, true);
							list4.Add(line.ObjectId);
							num27 = num28;
						}
						for (int m = 0; m < list2.Count; m++)
						{
							double num28 = num27 - num6;
							for (int n = 0; n < count; n++)
							{
								int num21 = (flag ? (count - 1 - n) : n);
								string rawText2 = list2[m][num21] ?? "";
								double centerX = array3[n] + array4[n] * 0.5;
								MText mText3 = CreateLegendCellMText(blockTableRecord, transaction, unicodeStyleId, rawText2, centerX, (num27 + num28) * 0.5, array4[n], num6, num, num6, AttachmentPoint.MiddleCenter, 7, text6);
								list4.Add(mText3.ObjectId);
							}
							if (m < list2.Count - 1)
							{
								Line line2 = new Line(new Point3d(num23, num28, 0.0), new Point3d(num24, num28, 0.0));
								line2.Layer = text6;
								line2.ColorIndex = 8;
								blockTableRecord.AppendEntity(line2);
								transaction.AddNewlyCreatedDBObject(line2, true);
								list4.Add(line2.ObjectId);
							}
							num27 = num28;
						}
						for (int n = 1; n < count; n++)
						{
							double x = array3[n];
							Line line3 = new Line(new Point3d(x, y2, 0.0), new Point3d(x, y, 0.0));
							line3.Layer = text6;
							line3.ColorIndex = 8;
							blockTableRecord.AppendEntity(line3);
							transaction.AddNewlyCreatedDBObject(line3, true);
							list4.Add(line3.ObjectId);
						}
					}
					database.UpdateExt(true);
					transaction.Commit();
				}
				if (list4.Count > 0 || list3.Count > 0)
				{
					CadUndoEntry cadUndoEntry = new CadUndoEntry();
					cadUndoEntry.ToolName = "import_excel_table";
					cadUndoEntry.Summary = "Imported Excel table (" + Path.GetFileName(text) + ", " + list2.Count.ToString(CultureInfo.InvariantCulture) + " rows)";
					cadUndoEntry.ErasedIds.AddRange(list3);
					cadUndoEntry.CreatedIds.AddRange(list4);
					PushUndoEntry(doc, cadUndoEntry);
				}
				if (bool5 && extents3d.HasValue && IsFiniteBounds(extents3d.Value))
				{
					if (text3 == "both" || text3 == "all")
					{
						ZoomToExtents3d(doc, extents3d.Value, 1.35);
					}
					else
					{
						ZoomToExtents3d(doc, extents3d.Value, 1.15);
					}
				}
				else
				{
					doc.Editor.Regen();
				}
				doc.Editor.UpdateScreen();
			}
			Dictionary<string, object> dictionary = new Dictionary<string, object>();
			dictionary.Add("file_path", text);
			dictionary.Add("sheet_name", excelSheetData.SheetName ?? "");
			dictionary.Add("available_sheets", excelSheetData.AvailableSheets ?? new List<string>());
			dictionary.Add("title", text5 ?? "");
			dictionary.Add("headers", list);
			dictionary.Add("row_count", list2.Count);
			dictionary.Add("column_count", count);
			dictionary.Add("layer", text6);
			dictionary.Add("table_mode", bool3 ? "autocad_table" : "unicode_grid");
			dictionary.Add("arabic_rtl", flag);
			dictionary.Add("text_height", Round(num));
			dictionary.Add("created_count", list4.Count);
			dictionary.Add("erased_count", list3.Count);
			dictionary.Add("switched_to_model", flag2);
			dictionary.Add("undo_history_steps", GetUndoHistoryCount(doc));
			Dictionary<string, object> dictionary2 = dictionary;
			if (extents3d.HasValue)
			{
				dictionary2["table_bounds"] = new double[4]
				{
					Round(extents3d.Value.MinPoint.X),
					Round(extents3d.Value.MinPoint.Y),
					Round(extents3d.Value.MaxPoint.X),
					Round(extents3d.Value.MaxPoint.Y)
				};
			}
			return Ok(dictionary2);
		}

		private static Dictionary<string, object> ManageLayers(Document doc, Dictionary<string, object> args)
		{
			string text = (GetStr(args, "action") ?? "list").Trim().ToLowerInvariant();
			string str = GetStr(args, "name");
			int? nullableInt = GetNullableInt(args, "color_index");
			string str2 = GetStr(args, "linetype");
			Database database = doc.Database;
			List<object> list = new List<object>();
			using (doc.LockDocument())
			{
				using (Transaction transaction = database.TransactionManager.StartTransaction())
				{
					LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
					if (text == "list")
					{
						foreach (ObjectId item in layerTable)
						{
							LayerTableRecord layerTableRecord = transaction.GetObject(item, OpenMode.ForRead) as LayerTableRecord;
							if (!(layerTableRecord == null))
							{
								list.Add(new Dictionary<string, object>
								{
									{ "name", layerTableRecord.Name },
									{
										"color_index",
										(int)layerTableRecord.Color.ColorIndex
									},
									{ "is_off", layerTableRecord.IsOff },
									{ "is_frozen", layerTableRecord.IsFrozen },
									{ "is_locked", layerTableRecord.IsLocked }
								});
							}
						}
						transaction.Commit();
						Dictionary<string, object> dictionary = new Dictionary<string, object>();
						dictionary.Add("layers", list);
						return Ok(dictionary);
					}
					if (string.IsNullOrWhiteSpace(str))
					{
						return Err("'name' is required for layer action '" + text + "'.");
					}
					string text2 = SanitizeSymbolName(str);
					if (text == "create" || text == "update")
					{
						string text3 = EnsureLayerSafe(database, transaction, text2, nullableInt.HasValue ? new short?(ClampColor(nullableInt.Value)) : null, str2);
						LayerTableRecord layerTableRecord = (LayerTableRecord)transaction.GetObject(layerTable[text3], OpenMode.ForWrite);
						if (args.ContainsKey("is_off"))
						{
							layerTableRecord.IsOff = GetBool(args, "is_off", false);
						}
						if (args.ContainsKey("is_frozen") && database.Clayer != layerTableRecord.ObjectId)
						{
							layerTableRecord.IsFrozen = GetBool(args, "is_frozen", false);
						}
						if (args.ContainsKey("is_locked"))
						{
							layerTableRecord.IsLocked = GetBool(args, "is_locked", false);
						}
						transaction.Commit();
						doc.Editor.Regen();
						doc.Editor.UpdateScreen();
						Dictionary<string, object> dictionary2 = new Dictionary<string, object>();
						dictionary2.Add("layer", text3);
						dictionary2.Add("action", text);
						return Ok(dictionary2);
					}
					if (text == "set_current")
					{
						string text3 = EnsureLayerSafe(database, transaction, text2, nullableInt.HasValue ? new short?(ClampColor(nullableInt.Value)) : null, str2);
						database.Clayer = layerTable[text3];
						transaction.Commit();
						Dictionary<string, object> dictionary3 = new Dictionary<string, object>();
						dictionary3.Add("active_layer", text3);
						return Ok(dictionary3);
					}
					if (text == "delete")
					{
						if (string.Equals(text2, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(text2, "Defpoints", StringComparison.OrdinalIgnoreCase))
						{
							return Err("Cannot delete system layer '" + text2 + "'.");
						}
						if (!layerTable.Has(text2))
						{
							return Err("Layer '" + text2 + "' does not exist.");
						}
						ObjectId objectId = layerTable[text2];
						if (database.Clayer == objectId)
						{
							return Err("Cannot delete the currently active layer '" + text2 + "'. Switch active layer first.");
						}
						LayerTableRecord layerTableRecord = (LayerTableRecord)transaction.GetObject(objectId, OpenMode.ForWrite);
						layerTableRecord.Erase(true);
						transaction.Commit();
						Dictionary<string, object> dictionary4 = new Dictionary<string, object>();
						dictionary4.Add("deleted_layer", text2);
						return Ok(dictionary4);
					}
					return Err("Unsupported manage_layers action: " + text);
				}
			}
		}

		private static Dictionary<string, object> MeasureAndCompare(Document doc, Dictionary<string, object> args)
		{
			string text = (GetStr(args, "mode") ?? "distance").Trim().ToLowerInvariant();
			Database database = doc.Database;
			using (doc.LockDocument())
			{
				using (Transaction transaction = database.TransactionManager.StartTransaction())
				{
					if (text == "distance")
					{
						string str = GetStr(args, "handle_a");
						string str2 = GetStr(args, "handle_b");
						if (!string.IsNullOrEmpty(str) && !string.IsNullOrEmpty(str2))
						{
							ObjectId id = ResolveHandle(database, str);
							ObjectId id2 = ResolveHandle(database, str2);
							if (id.IsNull || id2.IsNull)
							{
								return Err("One or both entity handles were not found.");
							}
							Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
							Entity entity2 = transaction.GetObject(id2, OpenMode.ForRead) as Entity;
							if (entity == null || entity2 == null)
							{
								return Err("Handle does not refer to an Entity.");
							}
							Extents3d geometricExtents = entity.GeometricExtents;
							Extents3d geometricExtents2 = entity2.GeometricExtents;
							Point2d point2d = new Point2d((geometricExtents.MinPoint.X + geometricExtents.MaxPoint.X) / 2.0, (geometricExtents.MinPoint.Y + geometricExtents.MaxPoint.Y) / 2.0);
							Point2d point2d2 = new Point2d((geometricExtents2.MinPoint.X + geometricExtents2.MaxPoint.X) / 2.0, (geometricExtents2.MinPoint.Y + geometricExtents2.MaxPoint.Y) / 2.0);
							double num = point2d2.X - point2d.X;
							double num2 = point2d2.Y - point2d.Y;
							double v = Math.Sqrt(num * num + num2 * num2);
							double num3 = Math.Max(0.0, Math.Max(geometricExtents2.MinPoint.X - geometricExtents.MaxPoint.X, geometricExtents.MinPoint.X - geometricExtents2.MaxPoint.X));
							double num4 = Math.Max(0.0, Math.Max(geometricExtents2.MinPoint.Y - geometricExtents.MaxPoint.Y, geometricExtents.MinPoint.Y - geometricExtents2.MaxPoint.Y));
							double v2 = Math.Sqrt(num3 * num3 + num4 * num4);
							bool flag = !(geometricExtents.MaxPoint.X < geometricExtents2.MinPoint.X) && !(geometricExtents2.MaxPoint.X < geometricExtents.MinPoint.X) && !(geometricExtents.MaxPoint.Y < geometricExtents2.MinPoint.Y) && !(geometricExtents2.MaxPoint.Y < geometricExtents.MinPoint.Y);
							transaction.Commit();
							Dictionary<string, object> dictionary = new Dictionary<string, object>();
							dictionary.Add("center_a", Pt2(point2d.X, point2d.Y));
							dictionary.Add("center_b", Pt2(point2d2.X, point2d2.Y));
							dictionary.Add("dx", Round(num));
							dictionary.Add("dy", Round(num2));
							dictionary.Add("center_distance", Round(v));
							dictionary.Add("clear_gap_x", Round(num3));
							dictionary.Add("clear_gap_y", Round(num4));
							dictionary.Add("clear_bbox_gap", Round(v2));
							dictionary.Add("bboxes_overlap", flag);
							return Ok(dictionary);
						}
						double[] doubleArray = GetDoubleArray(args, "p1");
						double[] doubleArray2 = GetDoubleArray(args, "p2");
						if (doubleArray == null || doubleArray.Length < 2 || doubleArray2 == null || doubleArray2.Length < 2)
						{
							return Err("Provide either ('handle_a' & 'handle_b') or ('p1' & 'p2').");
						}
						double num5 = doubleArray2[0] - doubleArray[0];
						double num6 = doubleArray2[1] - doubleArray[1];
						transaction.Commit();
						Dictionary<string, object> dictionary2 = new Dictionary<string, object>();
						dictionary2.Add("dx", Round(num5));
						dictionary2.Add("dy", Round(num6));
						dictionary2.Add("distance", Round(Math.Sqrt(num5 * num5 + num6 * num6)));
						return Ok(dictionary2);
					}
					Dictionary<string, object> dictionary3 = ComputeGroupStats(database, transaction, GetStringList(args, "handles_a"), GetStr(args, "layer_a"));
					if (text == "entities_summary")
					{
						transaction.Commit();
						return Ok(dictionary3);
					}
					Dictionary<string, object> value = ComputeGroupStats(database, transaction, GetStringList(args, "handles_b"), GetStr(args, "layer_b"));
					transaction.Commit();
					Dictionary<string, object> dictionary4 = new Dictionary<string, object>();
					dictionary4.Add("group_a", dictionary3);
					dictionary4.Add("group_b", value);
					return Ok(dictionary4);
				}
			}
		}

		private static Dictionary<string, object> DuplicateDrawing(Document doc, Dictionary<string, object> args)
		{
			string text = (GetStr(args, "mode") ?? "in_drawing").Trim().ToLowerInvariant();
			if (text == "dwg_file")
			{
				string text2 = GetStr(args, "file_path");
				if (string.IsNullOrWhiteSpace(text2))
				{
					string text3 = DrawingExecutionCommands.ResolveOutputDirectory(doc, "output");
					Directory.CreateDirectory(text3);
					string text4 = Path.GetFileNameWithoutExtension(doc.Name ?? "drawing");
					if (string.IsNullOrWhiteSpace(text4))
					{
						text4 = "drawing";
					}
					text2 = Path.Combine(text3, text4 + "_copy_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".dwg");
				}
				else
				{
					string directoryName = Path.GetDirectoryName(text2);
					if (!string.IsNullOrEmpty(directoryName))
					{
						Directory.CreateDirectory(directoryName);
					}
				}
				Database database = doc.Database;
				ObjectIdCollection objectIdCollection = new ObjectIdCollection();
				using (doc.LockDocument())
				{
					using (Transaction transaction = database.TransactionManager.StartTransaction())
					{
						BlockTableRecord blockTableRecord = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
						foreach (ObjectId item2 in blockTableRecord)
						{
							objectIdCollection.Add(item2);
						}
						transaction.Commit();
					}
					if (objectIdCollection.Count == 0)
					{
						return Err("Active drawing is empty; nothing to export.");
					}
					using (Database database2 = database.Wblock(objectIdCollection, Point3d.Origin))
					{
						database2.SaveAs(text2, DwgVersion.Current);
					}
				}
				Dictionary<string, object> dictionary = new Dictionary<string, object>();
				dictionary.Add("saved_path", text2);
				dictionary.Add("entity_count", objectIdCollection.Count);
				return Ok(dictionary);
			}
			if (GetBool(args, "as_block", false))
			{
				string description;
				bool isClarification;
				string errorMessage;
				if (!DrawingExecutionCommands.DuplicateActiveDrawing(doc, GetStr(args, "direction") ?? "right", out description, out isClarification, out errorMessage))
				{
					return Err(errorMessage);
				}
				Dictionary<string, object> dictionary2 = new Dictionary<string, object>();
				dictionary2.Add("description", description);
				return Ok(dictionary2);
			}
			string text5 = (GetStr(args, "direction") ?? "right").Trim().ToLowerInvariant();
			double? nullableDouble = GetNullableDouble(args, "gap");
			string str = GetStr(args, "layer");
			Database database3 = doc.Database;
			List<string> list = new List<string>();
			List<ObjectId> list2 = new List<ObjectId>();
			double x = 0.0;
			double y = 0.0;
			using (doc.LockDocument())
			{
				using (Transaction transaction = database3.TransactionManager.StartTransaction())
				{
					string text6 = ((!string.IsNullOrEmpty(str)) ? EnsureLayerSafe(database3, transaction, str, null, null) : null);
					BlockTableRecord blockTableRecord = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database3), OpenMode.ForWrite);
					List<Entity> list3 = new List<Entity>();
					Extents3d? extents3d = null;
					foreach (ObjectId item3 in blockTableRecord)
					{
						Entity entity = transaction.GetObject(item3, OpenMode.ForRead) as Entity;
						if (entity == null || entity is Autodesk.AutoCAD.DatabaseServices.Viewport)
						{
							continue;
						}
						list3.Add(entity);
						try
						{
							Extents3d geometricExtents = entity.GeometricExtents;
							if (!extents3d.HasValue)
							{
								extents3d = geometricExtents;
								continue;
							}
							Extents3d value = extents3d.Value;
							value.AddExtents(geometricExtents);
							extents3d = value;
						}
						catch
						{
						}
					}
					if (list3.Count == 0)
					{
						return Err("Active drawing is empty; there are no entities to duplicate.");
					}
					double num = (extents3d.HasValue ? Math.Max(100.0, extents3d.Value.MaxPoint.X - extents3d.Value.MinPoint.X) : 1000.0);
					double num2 = (extents3d.HasValue ? Math.Max(100.0, extents3d.Value.MaxPoint.Y - extents3d.Value.MinPoint.Y) : 1000.0);
					double num3 = (nullableDouble.HasValue ? nullableDouble.Value : Math.Max(Math.Max(num, num2) * 0.25, 250.0));
					switch (text5)
					{
					case "left":
						x = 0.0 - (num + num3);
						break;
					case "up":
						y = num2 + num3;
						break;
					case "down":
						y = 0.0 - (num2 + num3);
						break;
					default:
						x = num + num3;
						break;
					}
					Matrix3d matrix3d = Matrix3d.Displacement(new Vector3d(x, y, 0.0));
					foreach (Entity item4 in list3)
					{
						Entity entity2 = item4.Clone() as Entity;
						if (!(entity2 == null))
						{
							entity2.TransformBy(matrix3d);
							if (!string.IsNullOrEmpty(text6))
							{
								entity2.Layer = text6;
							}
							ObjectId item = blockTableRecord.AppendEntity(entity2);
							transaction.AddNewlyCreatedDBObject(entity2, true);
							CloneAttributesIfBlock(transaction, item4, entity2, matrix3d, text6, null);
							list2.Add(item);
							list.Add(entity2.Handle.ToString());
						}
					}
					database3.UpdateExt(true);
					transaction.Commit();
				}
			}
			if (list2.Count > 0)
			{
				CadUndoEntry cadUndoEntry = new CadUndoEntry();
				cadUndoEntry.ToolName = "duplicate_drawing";
				cadUndoEntry.Summary = "Duplicated drawing (" + list2.Count.ToString(CultureInfo.InvariantCulture) + " entities)";
				cadUndoEntry.CreatedIds.AddRange(list2);
				PushUndoEntry(doc, cadUndoEntry);
			}
			DrawingExecutionCommands.ZoomExtents(doc);
			Dictionary<string, object> dictionary3 = new Dictionary<string, object>();
			dictionary3.Add("copied_count", list.Count);
			dictionary3.Add("shift", Pt2(x, y));
			dictionary3.Add("copied_handles", list);
			return Ok(dictionary3);
		}

		private static Dictionary<string, object> ZoomView(Document doc, Dictionary<string, object> args)
		{
			string text = (GetStr(args, "mode") ?? "extents").Trim().ToLowerInvariant();
			Database database = doc.Database;
			bool flag = false;
			using (doc.LockDocument())
			{
				if (!database.TileMode)
				{
					try
					{
						LayoutManager.Current.CurrentLayout = "Model";
						flag = true;
					}
					catch
					{
					}
				}
				if (text == "extents")
				{
					DrawingExecutionCommands.ZoomExtents(doc);
					Dictionary<string, object> dictionary = new Dictionary<string, object>();
					dictionary.Add("zoomed", "extents");
					dictionary.Add("switched_to_model", flag);
					return Ok(dictionary);
				}
				Extents3d? extents3d = null;
				if (text == "window")
				{
					double[] doubleArray = GetDoubleArray(args, "bbox");
					if (doubleArray == null || doubleArray.Length < 4)
					{
						return Err("'bbox': [minX, minY, maxX, maxY] required.");
					}
					double x = Math.Min(doubleArray[0], doubleArray[2]);
					double x2 = Math.Max(doubleArray[0], doubleArray[2]);
					double y = Math.Min(doubleArray[1], doubleArray[3]);
					double y2 = Math.Max(doubleArray[1], doubleArray[3]);
					extents3d = new Extents3d(new Point3d(x, y, 0.0), new Point3d(x2, y2, 0.0));
				}
				else if (text == "handles")
				{
					List<string> stringList = GetStringList(args, "handles");
					using (Transaction transaction = database.TransactionManager.StartTransaction())
					{
						foreach (string item in stringList)
						{
							ObjectId id = ResolveHandle(database, item);
							if (id.IsNull || id.IsErased)
							{
								continue;
							}
							Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
							if (entity == null)
							{
								continue;
							}
							try
							{
								Extents3d geometricExtents = entity.GeometricExtents;
								if (IsFiniteBounds(geometricExtents))
								{
									if (!extents3d.HasValue)
									{
										extents3d = geometricExtents;
										continue;
									}
									Extents3d value = extents3d.Value;
									value.AddExtents(geometricExtents);
									extents3d = value;
								}
							}
							catch
							{
							}
						}
						transaction.Commit();
					}
				}
				if (!extents3d.HasValue)
				{
					DrawingExecutionCommands.ZoomExtents(doc);
					Dictionary<string, object> dictionary2 = new Dictionary<string, object>();
					dictionary2.Add("zoomed", "extents_fallback");
					dictionary2.Add("switched_to_model", flag);
					return Ok(dictionary2);
				}
				ZoomToExtents3d(doc, extents3d.Value, 1.25);
				Extents3d value2 = extents3d.Value;
				Dictionary<string, object> dictionary3 = new Dictionary<string, object>();
				dictionary3.Add("zoomed", text);
				dictionary3.Add("switched_to_model", flag);
				dictionary3.Add("bounds", new double[4]
				{
					Round(value2.MinPoint.X),
					Round(value2.MinPoint.Y),
					Round(value2.MaxPoint.X),
					Round(value2.MaxPoint.Y)
				});
				return Ok(dictionary3);
			}
		}

		private static void ZoomToExtents3d(Document doc, Extents3d targetExt, double paddingFactor)
		{
			Extents3d extents3d = targetExt;
			using (ViewTableRecord viewTableRecord = doc.Editor.GetCurrentView())
			{
				Matrix3d matrix3d = Matrix3d.PlaneToWorld(viewTableRecord.ViewDirection);
				matrix3d = Matrix3d.Displacement(viewTableRecord.Target - Point3d.Origin) * matrix3d;
				matrix3d = (Matrix3d.Rotation(0.0 - viewTableRecord.ViewTwist, viewTableRecord.ViewDirection, viewTableRecord.Target) * matrix3d).Inverse();
				extents3d.TransformBy(matrix3d);
				double num = Math.Max(1.05, paddingFactor);
				double num2 = Math.Max(0.0001, (extents3d.MaxPoint.X - extents3d.MinPoint.X) * num);
				double num3 = Math.Max(0.0001, (extents3d.MaxPoint.Y - extents3d.MinPoint.Y) * num);
				double num4 = viewTableRecord.Width / Math.Max(1E-06, viewTableRecord.Height);
				if (num2 / num3 > num4)
				{
					num3 = num2 / num4;
				}
				else
				{
					num2 = num3 * num4;
				}
				viewTableRecord.Width = num2;
				viewTableRecord.Height = num3;
				viewTableRecord.CenterPoint = new Point2d((extents3d.MinPoint.X + extents3d.MaxPoint.X) / 2.0, (extents3d.MinPoint.Y + extents3d.MaxPoint.Y) / 2.0);
				doc.Editor.SetCurrentView(viewTableRecord);
			}
			doc.Editor.Regen();
			doc.Editor.UpdateScreen();
		}

		private static Dictionary<string, object> ExecuteTemplate(Document doc, Dictionary<string, object> args)
		{
			string text = (GetStr(args, "template") ?? "").Trim().ToLowerInvariant();
			string errorMessage;
			if (text == "apartment_150sqm")
			{
				if (!DrawingExecutionCommands.DrawHouseConcept(doc, out errorMessage))
				{
					return Err(errorMessage);
				}
				Dictionary<string, object> dictionary = new Dictionary<string, object>();
				dictionary.Add("template", text);
				dictionary.Add("status", "inserted");
				return Ok(dictionary);
			}
			if (text == "apartment_150sqm_electrical")
			{
				if (!DrawingExecutionCommands.DrawElectricalDistribution(doc, out errorMessage))
				{
					return Err(errorMessage);
				}
				Dictionary<string, object> dictionary2 = new Dictionary<string, object>();
				dictionary2.Add("template", text);
				dictionary2.Add("status", "inserted");
				return Ok(dictionary2);
			}
			return Err("Unknown template '" + text + "'. Use 'apartment_150sqm' or 'apartment_150sqm_electrical'.");
		}

		private static Dictionary<string, object> ApproveOrRollbackPreview(Document doc, Dictionary<string, object> args)
		{
			string text = (GetStr(args, "action") ?? "").Trim().ToLowerInvariant();
			if (text == "approve" || text == "commit")
			{
				DrawingExecutionCommands.ApproveReview(doc);
				Dictionary<string, object> dictionary = new Dictionary<string, object>();
				dictionary.Add("action", "approved");
				return Ok(dictionary);
			}
			if (text == "rollback" || text == "revert")
			{
				DrawingExecutionCommands.RollbackReview(doc);
				Dictionary<string, object> dictionary2 = new Dictionary<string, object>();
				dictionary2.Add("action", "rolled_back");
				return Ok(dictionary2);
			}
			return Err("Specify 'action': 'approve' or 'rollback'.");
		}

		private static string SanitizeSymbolName(string raw)
		{
			string text = (raw ?? "LAYER_AI").Trim();
			char[] array = new char[16]
			{
				'<', '>', '/', '\\', '"', ':', ';', '?', '*', '|',
				',', '=', '`', '\r', '\n', '\t'
			};
			char[] array2 = array;
			char[] array3 = array2;
			foreach (char oldChar in array3)
			{
				text = text.Replace(oldChar, '_');
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				text = "LAYER_AI";
			}
			if (text.Length > 200)
			{
				text = text.Substring(0, 200);
			}
			return text;
		}

		private static string EnsureLayerSafe(Database db, Transaction tr, string requestedLayer, short? colorIndex, string linetypeName)
		{
			string text = SanitizeSymbolName(requestedLayer);
			LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
			LayerTableRecord layerTableRecord;
			if (layerTable.Has(text))
			{
				layerTableRecord = (LayerTableRecord)tr.GetObject(layerTable[text], OpenMode.ForWrite);
				if (colorIndex.HasValue)
				{
					layerTableRecord.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex.Value);
				}
				if (!string.IsNullOrWhiteSpace(linetypeName))
				{
					layerTableRecord.LinetypeObjectId = ResolveLinetypeSafe(db, tr, linetypeName);
				}
				layerTableRecord.IsOff = false;
				if (db.Clayer != layerTableRecord.ObjectId)
				{
					layerTableRecord.IsFrozen = false;
				}
				return layerTableRecord.Name;
			}
			layerTable.UpgradeOpen();
			layerTableRecord = new LayerTableRecord();
			layerTableRecord.Name = text;
			layerTableRecord.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)(((int?)colorIndex) ?? (string.Equals(text, "_AI_PREVIEW_REVIEW", StringComparison.OrdinalIgnoreCase) ? 1 : 7)));
			layerTableRecord.LinetypeObjectId = ResolveLinetypeSafe(db, tr, linetypeName);
			layerTable.Add(layerTableRecord);
			tr.AddNewlyCreatedDBObject(layerTableRecord, true);
			return text;
		}

		private static ObjectId ResolveLinetypeSafe(Database db, Transaction tr, string linetypeName)
		{
			if (string.IsNullOrWhiteSpace(linetypeName))
			{
				return db.ContinuousLinetype;
			}
			LinetypeTable linetypeTable = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
			if (linetypeTable.Has(linetypeName))
			{
				return linetypeTable[linetypeName];
			}
			try
			{
				db.LoadLineTypeFile(linetypeName, "acadiso.lin");
				if (linetypeTable.Has(linetypeName))
				{
					return linetypeTable[linetypeName];
				}
			}
			catch
			{
			}
			try
			{
				db.LoadLineTypeFile(linetypeName, "acad.lin");
				if (linetypeTable.Has(linetypeName))
				{
					return linetypeTable[linetypeName];
				}
			}
			catch
			{
			}
			return db.ContinuousLinetype;
		}

		private static Entity CreateSingleEntity(Database db, Transaction tr, BlockTable bt, Dictionary<string, object> item, string layerName, int? defaultColor, out string warning)
		{
			warning = null;
			string text = (GetStr(item, "type") ?? "").Trim().ToLowerInvariant();
			Entity entity = null;
			switch (text)
			{
			case "circle":
			{
				Point3d point3d4 = GetPoint3d(item, "center", Point3d.Origin);
				double? nullableDouble3 = GetNullableDouble(item, "radius");
				double? nullableDouble4 = GetNullableDouble(item, "diameter");
				double double5 = (nullableDouble3.HasValue ? nullableDouble3.Value : (nullableDouble4.HasValue ? (nullableDouble4.Value / 2.0) : 100.0));
				if (double5 <= 1E-06)
				{
					warning = "Skipped circle with non-positive radius.";
					return null;
				}
				entity = new Circle(point3d4, Vector3d.ZAxis, double5);
				break;
			}
			case "line":
			{
				Point3d point3d = GetPoint3d(item, "start", Point3d.Origin);
				Point3d point3d2 = GetPoint3d(item, "end", new Point3d(100.0, 0.0, 0.0));
				entity = new Line(point3d, point3d2);
				break;
			}
			default:
				if (!(text == "square"))
				{
					if (text == "polyline" || text == "polygon")
					{
						List<object> list = AsList(GetObj(item, "points"));
						if (list.Count < 2)
						{
							warning = "Skipped polyline with fewer than 2 points.";
							return null;
						}
						Autodesk.AutoCAD.DatabaseServices.Polyline polyline = new Autodesk.AutoCAD.DatabaseServices.Polyline(list.Count);
						int num = 0;
						foreach (object item2 in list)
						{
							double[] array = ToDoublePair(item2);
							if (array != null)
							{
								polyline.AddVertexAt(num++, new Point2d(array[0], array[1]), 0.0, 0.0, 0.0);
							}
						}
						if (polyline.NumberOfVertices < 2)
						{
							warning = "Skipped polyline with invalid coordinates.";
							return null;
						}
						polyline.Closed = GetBool(item, "closed", text == "polygon");
						double? nullableDouble = GetNullableDouble(item, "constant_width");
						if (nullableDouble.HasValue && nullableDouble.Value >= 0.0)
						{
							polyline.ConstantWidth = nullableDouble.Value;
						}
						entity = polyline;
						break;
					}
					Point3d point3d;
					Point3d point3d2;
					Vector3d vector3d;
					double num2;
					Vector3d vector3d2;
					Point3d defPt;
					Point3d point3d3;
					string dimensionText;
					string str;
					Point3d point3d5;
					BlockReference blockReference;
					double[] doubleArray;
					switch (text)
					{
					case "arc":
					{
						Point3d point3d4 = GetPoint3d(item, "center", Point3d.Origin);
						double double5 = GetDouble(item, "radius", 100.0);
						if (double5 <= 1E-06)
						{
							warning = "Skipped arc with non-positive radius.";
							return null;
						}
						double startAngle = GetDouble(item, "start_angle", 0.0) * Math.PI / 180.0;
						double endAngle = GetDouble(item, "end_angle", 180.0) * Math.PI / 180.0;
						entity = new Arc(point3d4, double5, startAngle, endAngle);
						break;
					}
					case "ellipse":
					{
						Point3d point3d4 = GetPoint3d(item, "center", Point3d.Origin);
						double num4 = Math.Max(0.001, GetDouble(item, "major_radius", GetDouble(item, "radius_x", 200.0)));
						double num5 = Math.Max(0.001, GetDouble(item, "minor_radius", GetDouble(item, "radius_y", 100.0)));
						double rotation = GetDouble(item, "rotation_deg", 0.0) * Math.PI / 180.0;
						if (num5 > num4)
						{
							double num6 = num4;
							num4 = num5;
							num5 = num6;
							rotation += Math.PI / 2.0;
						}
						Vector3d majorAxis = new Vector3d(num4 * Math.Cos(rotation), num4 * Math.Sin(rotation), 0.0);
						double radiusRatio = Math.Max(0.0001, Math.Min(1.0, num5 / num4));
						entity = new Ellipse(point3d4, Vector3d.ZAxis, majorAxis, radiusRatio, 0.0, Math.PI * 2.0);
						break;
					}
					case "text":
					{
						string text3 = GetStr(item, "text") ?? "";
						if (string.IsNullOrEmpty(text3))
						{
							warning = "Skipped empty text.";
							return null;
						}
						point3d5 = GetPoint3d(item, "position", Point3d.Origin);
						double num3 = Math.Max(0.001, GetDouble(item, "height", 25.0));
						double rotation = GetDouble(item, "rotation_deg", 0.0) * Math.PI / 180.0;
						bool flag2 = ContainsNonAscii(text3);
						bool @bool = GetBool(item, "force_dbtext", false);
						if (flag2 && !@bool)
						{
							MText mText = new MText();
							mText.SetDatabaseDefaults(db);
							mText.TextStyleId = EnsureUnicodeTextStyle(db, tr);
							mText.Contents = EscapeMTextPlain(text3);
							mText.TextHeight = num3;
							mText.Width = GetDouble(item, "width", 0.0);
							mText.Rotation = rotation;
							switch ((GetStr(item, "align") ?? "center").Trim().ToLowerInvariant())
							{
							case "middle_center":
								mText.Attachment = AttachmentPoint.MiddleCenter;
								break;
							case "right":
								mText.Attachment = AttachmentPoint.BottomRight;
								break;
							case "left":
								mText.Attachment = AttachmentPoint.BottomLeft;
								break;
							default:
								mText.Attachment = AttachmentPoint.BottomCenter;
								break;
							}
							mText.Location = point3d5;
							entity = mText;
						}
						else
						{
							DBText dBText = new DBText();
							dBText.SetDatabaseDefaults(db);
							dBText.TextStyleId = (flag2 ? EnsureUnicodeTextStyle(db, tr) : db.Textstyle);
							dBText.TextString = text3;
							dBText.Height = num3;
							dBText.Rotation = rotation;
							dBText.Position = point3d5;
							entity = dBText;
						}
						break;
					}
					case "mtext":
					{
						string text3 = GetStr(item, "text") ?? "";
						if (string.IsNullOrEmpty(text3))
						{
							warning = "Skipped empty mtext.";
							return null;
						}
						point3d5 = GetPoint3d(item, "position", Point3d.Origin);
						double num3 = Math.Max(0.001, GetDouble(item, "height", 25.0));
						MText mText = new MText();
						mText.SetDatabaseDefaults(db);
						if (ContainsNonAscii(text3))
						{
							mText.TextStyleId = EnsureUnicodeTextStyle(db, tr);
						}
						mText.Contents = text3;
						mText.TextHeight = num3;
						mText.Width = GetDouble(item, "width", 0.0);
						mText.Rotation = GetDouble(item, "rotation_deg", 0.0) * Math.PI / 180.0;
						switch ((GetStr(item, "align") ?? "").Trim().ToLowerInvariant())
						{
						case "middle_center":
							mText.Attachment = AttachmentPoint.MiddleCenter;
							break;
						case "center":
							mText.Attachment = AttachmentPoint.BottomCenter;
							break;
						case "right":
							mText.Attachment = AttachmentPoint.BottomRight;
							break;
						case "left":
							mText.Attachment = AttachmentPoint.BottomLeft;
							break;
						}
						mText.Location = point3d5;
						entity = mText;
						break;
					}
					default:
					{
						switch (text)
						{
						case "hatch":
							break;
						default:
							if (!(text == "insert"))
							{
								warning = "Unsupported entity type '" + text + "'.";
								return null;
							}
							goto IL_0c74;
						case "block_reference":
							goto IL_0c74;
						case "linear_dimension":
							goto IL_0e3f;
						}
						List<object> list2 = AsList(GetObj(item, "points"));
						if (list2.Count < 3)
						{
							double @double = GetDouble(item, "x", 0.0);
							double double2 = GetDouble(item, "y", 0.0);
							double double3 = GetDouble(item, "width", 0.0);
							double double4 = GetDouble(item, "height", 0.0);
							if (double3 > 0.0 && double4 > 0.0)
							{
								List<object> list3 = new List<object>();
								list3.Add(new double[2] { @double, double2 });
								list3.Add(new double[2]
								{
									@double + double3,
									double2
								});
								list3.Add(new double[2]
								{
									@double + double3,
									double2 + double4
								});
								list3.Add(new double[2]
								{
									@double,
									double2 + double4
								});
								list2 = list3;
							}
						}
						if (list2.Count < 3)
						{
							warning = "Skipped hatch without 'points' (>=3) or positive 'width'/'height'.";
							return null;
						}
						Point2dCollection point2dCollection = new Point2dCollection();
						DoubleCollection doubleCollection = new DoubleCollection();
						foreach (object item3 in list2)
						{
							double[] array = ToDoublePair(item3);
							if (array != null && array.Length >= 2)
							{
								point2dCollection.Add(new Point2d(array[0], array[1]));
								doubleCollection.Add(0.0);
							}
						}
						if (point2dCollection.Count < 3)
						{
							warning = "Skipped hatch with fewer than 3 valid vertices.";
							return null;
						}
						if (point2dCollection[0].GetDistanceTo(point2dCollection[point2dCollection.Count - 1]) > 1E-06)
						{
							point2dCollection.Add(point2dCollection[0]);
							doubleCollection.Add(0.0);
						}
						Hatch hatch = new Hatch();
						hatch.SetDatabaseDefaults(db);
						string text2 = GetStr(item, "pattern_name") ?? "ANSI31";
						bool flag = string.Equals(text2, "SOLID", StringComparison.OrdinalIgnoreCase);
						hatch.SetHatchPattern(HatchPatternType.PreDefined, text2);
						if (!flag)
						{
							hatch.PatternScale = Math.Max(0.01, GetDouble(item, "pattern_scale", 1.0));
						}
						hatch.Associative = false;
						hatch.AppendLoop(HatchLoopTypes.Default, point2dCollection, doubleCollection);
						entity = hatch;
						break;
					}
					case "dimension":
					case "aligned_dimension":
						goto IL_0e3f;
						IL_0e3f:
						point3d = GetPoint3d(item, "p1", Point3d.Origin);
						point3d2 = GetPoint3d(item, "p2", new Point3d(100.0, 0.0, 0.0));
						vector3d = point3d2 - point3d;
						num2 = Math.Max(10.0, vector3d.Length);
						vector3d2 = ((vector3d.Length > 1E-06) ? new Vector3d((0.0 - vector3d.Y) / vector3d.Length, vector3d.X / vector3d.Length, 0.0) : Vector3d.YAxis);
						defPt = new Point3d((point3d.X + point3d2.X) / 2.0 + vector3d2.X * num2 * 0.25, (point3d.Y + point3d2.Y) / 2.0 + vector3d2.Y * num2 * 0.25, 0.0);
						point3d3 = GetPoint3d(item, "dim_line_point", defPt);
						dimensionText = GetStr(item, "text_override") ?? "";
						if (text == "linear_dimension")
						{
							double rotation = GetDouble(item, "rotation_deg", 0.0) * Math.PI / 180.0;
							RotatedDimension rotatedDimension = new RotatedDimension(rotation, point3d, point3d2, point3d3, dimensionText, db.Dimstyle);
							rotatedDimension.SetDatabaseDefaults(db);
							entity = rotatedDimension;
						}
						else
						{
							AlignedDimension alignedDimension = new AlignedDimension(point3d, point3d2, point3d3, dimensionText, db.Dimstyle);
							alignedDimension.SetDatabaseDefaults(db);
							entity = alignedDimension;
						}
						break;
						IL_0c74:
						str = GetStr(item, "block_name");
						if (string.IsNullOrEmpty(str) || !bt.Has(str))
						{
							warning = "Block '" + (str ?? "") + "' does not exist in drawing. Use create_block first or check inspect_drawing.blocks.";
							return null;
						}
						point3d5 = GetPoint3d(item, "position", Point3d.Origin);
						blockReference = new BlockReference(point3d5, bt[str]);
						doubleArray = GetDoubleArray(item, "scale");
						if (doubleArray != null && doubleArray.Length >= 2)
						{
							double x = ((Math.Abs(doubleArray[0]) < 1E-06) ? 1.0 : doubleArray[0]);
							double y = ((Math.Abs(doubleArray[1]) < 1E-06) ? 1.0 : doubleArray[1]);
							double z = ((doubleArray.Length >= 3 && Math.Abs(doubleArray[2]) >= 1E-06) ? doubleArray[2] : 1.0);
							blockReference.ScaleFactors = new Scale3d(x, y, z);
						}
						else
						{
							double? nullableDouble2 = GetNullableDouble(item, "scale");
							if (nullableDouble2.HasValue && Math.Abs(nullableDouble2.Value) >= 1E-06)
							{
								blockReference.ScaleFactors = new Scale3d(nullableDouble2.Value);
							}
						}
						blockReference.Rotation = GetDouble(item, "rotation_deg", 0.0) * Math.PI / 180.0;
						entity = blockReference;
						break;
					}
					break;
				}
				goto case "rect";
			case "rect":
			case "rectangle":
			{
				double double6 = GetDouble(item, "side", GetDouble(item, "size", GetDouble(item, "length", 500.0)));
				double num7 = GetDouble(item, "width", double6);
				double num3 = GetDouble(item, "height", GetDouble(item, "side", GetDouble(item, "size", num7)));
				double[] doubleArray2 = GetDoubleArray(item, "p1");
				double[] doubleArray3 = GetDoubleArray(item, "p2");
				double[] doubleArray4 = GetDoubleArray(item, "center");
				double num8;
				double num9;
				if (doubleArray2 != null && doubleArray2.Length >= 2 && doubleArray3 != null && doubleArray3.Length >= 2)
				{
					num8 = Math.Min(doubleArray2[0], doubleArray3[0]);
					num9 = Math.Min(doubleArray2[1], doubleArray3[1]);
					num7 = Math.Abs(doubleArray3[0] - doubleArray2[0]);
					num3 = Math.Abs(doubleArray3[1] - doubleArray2[1]);
				}
				else if (doubleArray4 != null && doubleArray4.Length >= 2 && !item.ContainsKey("x") && !item.ContainsKey("y"))
				{
					num8 = doubleArray4[0] - num7 / 2.0;
					num9 = doubleArray4[1] - num3 / 2.0;
				}
				else
				{
					num8 = GetDouble(item, "x", 0.0);
					num9 = GetDouble(item, "y", 0.0);
				}
				Autodesk.AutoCAD.DatabaseServices.Polyline polyline = new Autodesk.AutoCAD.DatabaseServices.Polyline(4);
				polyline.AddVertexAt(0, new Point2d(num8, num9), 0.0, 0.0, 0.0);
				polyline.AddVertexAt(1, new Point2d(num8 + num7, num9), 0.0, 0.0, 0.0);
				polyline.AddVertexAt(2, new Point2d(num8 + num7, num9 + num3), 0.0, 0.0, 0.0);
				polyline.AddVertexAt(3, new Point2d(num8, num9 + num3), 0.0, 0.0, 0.0);
				polyline.Closed = true;
				double? nullableDouble = GetNullableDouble(item, "constant_width");
				if (nullableDouble.HasValue && nullableDouble.Value >= 0.0)
				{
					polyline.ConstantWidth = nullableDouble.Value;
				}
				entity = polyline;
				break;
			}
			}
			if (entity != null)
			{
				entity.Layer = layerName;
				int? num10 = GetNullableInt(item, "color_index") ?? defaultColor;
				if (num10.HasValue)
				{
					entity.ColorIndex = ClampColor(num10.Value);
				}
			}
			return entity;
		}

		private static void FinalizeAppendedEntity(Database db, Entity ent, Dictionary<string, object> item)
		{
			DBText dBText = ent as DBText;
			if (dBText != null)
			{
				string text = (GetStr(item, "align") ?? "center").Trim().ToLowerInvariant();
				Point3d point3d = GetPoint3d(item, "position", dBText.Position);
				if (text == "center" || text == "middle_center")
				{
					dBText.HorizontalMode = TextHorizontalMode.TextCenter;
					dBText.VerticalMode = ((!(text == "middle_center")) ? TextVerticalMode.TextBottom : TextVerticalMode.TextVerticalMid);
					dBText.AlignmentPoint = point3d;
					dBText.AdjustAlignment(db);
				}
				else if (text == "right")
				{
					dBText.HorizontalMode = TextHorizontalMode.TextRight;
					dBText.AlignmentPoint = point3d;
					dBText.AdjustAlignment(db);
				}
				return;
			}
			Hatch hatch = ent as Hatch;
			if (!(hatch != null))
			{
				return;
			}
			try
			{
				hatch.EvaluateHatch(true);
			}
			catch
			{
			}
		}

		private static Point3d GetEntityPositionHint(Entity ent)
		{
			if (ent is DBText)
			{
				return ((DBText)ent).Position;
			}
			if (ent is MText)
			{
				return ((MText)ent).Location;
			}
			if (ent is BlockReference)
			{
				return ((BlockReference)ent).Position;
			}
			try
			{
				Extents3d geometricExtents = ent.GeometricExtents;
				return new Point3d((geometricExtents.MinPoint.X + geometricExtents.MaxPoint.X) / 2.0, (geometricExtents.MinPoint.Y + geometricExtents.MaxPoint.Y) / 2.0, 0.0);
			}
			catch
			{
				return Point3d.Origin;
			}
		}

		private static void CloneAttributesIfBlock(Transaction tr, Entity src, Entity clone, Matrix3d disp, string resolvedLayer, int? colorIndex)
		{
			BlockReference blockReference = src as BlockReference;
			BlockReference blockReference2 = clone as BlockReference;
			if (blockReference == null || blockReference2 == null || blockReference.AttributeCollection == null || blockReference.AttributeCollection.Count == 0)
			{
				return;
			}
			foreach (ObjectId item in blockReference.AttributeCollection)
			{
				try
				{
					AttributeReference attributeReference = tr.GetObject(item, OpenMode.ForRead) as AttributeReference;
					if (attributeReference == null)
					{
						continue;
					}
					AttributeReference attributeReference2 = attributeReference.Clone() as AttributeReference;
					if (!(attributeReference2 == null))
					{
						if (disp != Matrix3d.Identity)
						{
							attributeReference2.TransformBy(disp);
						}
						if (!string.IsNullOrEmpty(resolvedLayer))
						{
							attributeReference2.Layer = resolvedLayer;
						}
						if (colorIndex.HasValue)
						{
							attributeReference2.ColorIndex = ClampColor(colorIndex.Value);
						}
						blockReference2.AttributeCollection.AppendAttribute(attributeReference2);
						tr.AddNewlyCreatedDBObject(attributeReference2, true);
					}
				}
				catch
				{
				}
			}
		}

		private static void AttachAttributesIfBlock(Transaction tr, BlockTable bt, BlockReference br, Dictionary<string, object> item)
		{
			if (br == null)
			{
				return;
			}
			Dictionary<string, object> dictionary = AsDict(GetObj(item, "attributes"));
			if (dictionary == null || dictionary.Count == 0)
			{
				return;
			}
			BlockTableRecord blockTableRecord = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
			if (blockTableRecord == null || !blockTableRecord.HasAttributeDefinitions)
			{
				return;
			}
			foreach (ObjectId item2 in blockTableRecord)
			{
				AttributeDefinition attributeDefinition = tr.GetObject(item2, OpenMode.ForRead) as AttributeDefinition;
				if (!(attributeDefinition == null) && !attributeDefinition.Constant)
				{
					AttributeReference attributeReference = new AttributeReference();
					attributeReference.SetAttributeFromBlock(attributeDefinition, br.BlockTransform);
					object value;
					if (dictionary.TryGetValue(attributeDefinition.Tag, out value) && value != null)
					{
						attributeReference.TextString = value.ToString();
					}
					br.AttributeCollection.AppendAttribute(attributeReference);
					tr.AddNewlyCreatedDBObject(attributeReference, true);
				}
			}
		}

		private static void ApplyEntityEdits(Database db, Transaction tr, Entity ent, Dictionary<string, object> edits)
		{
			ApplyEntityEdits(db, tr, ent, edits, null);
		}

		private static void ApplyEntityEdits(Database db, Transaction tr, Entity ent, Dictionary<string, object> edits, ModifiedEntitySnapshot snap)
		{
			double[] doubleArray = GetDoubleArray(edits, "move");
			if (doubleArray != null && doubleArray.Length >= 2)
			{
				double num = doubleArray[0];
				double num2 = doubleArray[1];
				double num3 = ((doubleArray.Length >= 3) ? doubleArray[2] : 0.0);
				ent.TransformBy(Matrix3d.Displacement(new Vector3d(num, num2, num3)));
				if (snap != null)
				{
					snap.InverseTransforms.Insert(0, Matrix3d.Displacement(new Vector3d(0.0 - num, 0.0 - num2, 0.0 - num3)));
				}
			}
			else if (edits.ContainsKey("move_dx") || edits.ContainsKey("move_dy"))
			{
				double num = GetDouble(edits, "move_dx", 0.0);
				double num2 = GetDouble(edits, "move_dy", 0.0);
				ent.TransformBy(Matrix3d.Displacement(new Vector3d(num, num2, 0.0)));
				if (snap != null)
				{
					snap.InverseTransforms.Insert(0, Matrix3d.Displacement(new Vector3d(0.0 - num, 0.0 - num2, 0.0)));
				}
			}
			Dictionary<string, object> dictionary = AsDict(GetObj(edits, "rotate"));
			if (dictionary != null)
			{
				Point3d point3d = GetPoint3d(dictionary, "origin", Point3d.Origin);
				double num4 = GetDouble(dictionary, "angle_deg", 0.0) * Math.PI / 180.0;
				ent.TransformBy(Matrix3d.Rotation(num4, Vector3d.ZAxis, point3d));
				if (snap != null)
				{
					snap.InverseTransforms.Insert(0, Matrix3d.Rotation(0.0 - num4, Vector3d.ZAxis, point3d));
				}
			}
			else if (edits.ContainsKey("rotation_deg") && ent is BlockReference)
			{
				((BlockReference)ent).Rotation = GetDouble(edits, "rotation_deg", 0.0) * Math.PI / 180.0;
			}
			Dictionary<string, object> dictionary2 = AsDict(GetObj(edits, "scale"));
			if (dictionary2 != null)
			{
				Point3d point3d = GetPoint3d(dictionary2, "origin", Point3d.Origin);
				double @double = GetDouble(dictionary2, "factor", 1.0);
				if (Math.Abs(@double) > 1E-06)
				{
					ent.TransformBy(Matrix3d.Scaling(@double, point3d));
					if (snap != null)
					{
						snap.InverseTransforms.Insert(0, Matrix3d.Scaling(1.0 / @double, point3d));
					}
				}
			}
			Dictionary<string, object> dictionary3 = AsDict(GetObj(edits, "mirror"));
			if (dictionary3 != null)
			{
				Point3d point3d2 = GetPoint3d(dictionary3, "p1", Point3d.Origin);
				Point3d point3d3 = GetPoint3d(dictionary3, "p2", new Point3d(0.0, 1.0, 0.0));
				ent.TransformBy(Matrix3d.Mirroring(new Line3d(point3d2, point3d3)));
				if (snap != null)
				{
					snap.InverseTransforms.Insert(0, Matrix3d.Mirroring(new Line3d(point3d2, point3d3)));
				}
			}
			string str = GetStr(edits, "layer");
			if (!string.IsNullOrEmpty(str))
			{
				ent.Layer = EnsureLayerSafe(db, tr, str, null, GetStr(edits, "linetype"));
			}
			int? nullableInt = GetNullableInt(edits, "color_index");
			if (nullableInt.HasValue)
			{
				ent.ColorIndex = ClampColor(nullableInt.Value);
			}
			Circle circle = ent as Circle;
			if (circle != null)
			{
				double? nullableDouble = GetNullableDouble(edits, "radius");
				double? nullableDouble2 = GetNullableDouble(edits, "diameter");
				if (nullableDouble.HasValue && nullableDouble.Value > 1E-06)
				{
					circle.Radius = nullableDouble.Value;
				}
				else if (nullableDouble2.HasValue && nullableDouble2.Value > 1E-06)
				{
					circle.Radius = nullableDouble2.Value / 2.0;
				}
				if (edits.ContainsKey("center"))
				{
					circle.Center = GetPoint3d(edits, "center", circle.Center);
				}
			}
			Line line = ent as Line;
			if (line != null)
			{
				if (edits.ContainsKey("start"))
				{
					line.StartPoint = GetPoint3d(edits, "start", line.StartPoint);
				}
				if (edits.ContainsKey("end"))
				{
					line.EndPoint = GetPoint3d(edits, "end", line.EndPoint);
				}
			}
			DBText dBText = ent as DBText;
			if (dBText != null)
			{
				string str2 = GetStr(edits, "text");
				if (str2 != null)
				{
					dBText.TextString = str2;
					if (ContainsNonAscii(str2))
					{
						dBText.TextStyleId = EnsureUnicodeTextStyle(db, tr);
					}
				}
				double? nullableDouble3 = GetNullableDouble(edits, "height");
				if (nullableDouble3.HasValue && nullableDouble3.Value > 1E-06)
				{
					dBText.Height = nullableDouble3.Value;
				}
			}
			MText mText = ent as MText;
			if (mText != null)
			{
				string str2 = GetStr(edits, "text");
				if (str2 != null)
				{
					mText.Contents = str2;
					if (ContainsNonAscii(str2))
					{
						mText.TextStyleId = EnsureUnicodeTextStyle(db, tr);
					}
				}
				double? nullableDouble3 = GetNullableDouble(edits, "height");
				if (nullableDouble3.HasValue && nullableDouble3.Value > 1E-06)
				{
					mText.TextHeight = nullableDouble3.Value;
				}
			}
			Autodesk.AutoCAD.DatabaseServices.Polyline polyline = ent as Autodesk.AutoCAD.DatabaseServices.Polyline;
			if (polyline != null && edits.ContainsKey("closed"))
			{
				polyline.Closed = GetBool(edits, "closed", polyline.Closed);
			}
		}

		private static Dictionary<string, object> ComputeGroupStats(Database db, Transaction tr, List<string> handles, string layerName)
		{
			int num = 0;
			double num2 = 0.0;
			double num3 = 0.0;
			Extents3d? extents3d = null;
			HashSet<string> hashSet = new HashSet<string>(handles, StringComparer.OrdinalIgnoreCase);
			BlockTableRecord blockTableRecord = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
			foreach (ObjectId item in blockTableRecord)
			{
				Entity entity = tr.GetObject(item, OpenMode.ForRead) as Entity;
				if (entity == null || (hashSet.Count > 0 && !hashSet.Contains(entity.Handle.ToString())) || (!string.IsNullOrEmpty(layerName) && !string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}
				num++;
				try
				{
					Extents3d geometricExtents = entity.GeometricExtents;
					if (!extents3d.HasValue)
					{
						extents3d = geometricExtents;
					}
					else
					{
						Extents3d value = extents3d.Value;
						value.AddExtents(geometricExtents);
						extents3d = value;
					}
				}
				catch
				{
				}
				try
				{
					if (entity is Circle)
					{
						Circle circle = (Circle)entity;
						num2 += circle.Area;
						num3 += circle.Circumference;
					}
					else if (entity is Line)
					{
						num3 += ((Line)entity).Length;
					}
					else if (entity is Arc)
					{
						num3 += ((Arc)entity).Length;
					}
					else if (entity is Autodesk.AutoCAD.DatabaseServices.Polyline)
					{
						Autodesk.AutoCAD.DatabaseServices.Polyline polyline = (Autodesk.AutoCAD.DatabaseServices.Polyline)entity;
						num3 += polyline.Length;
						if (polyline.Closed)
						{
							num2 += Math.Abs(polyline.Area);
						}
					}
					else if (entity is Ellipse)
					{
						num2 += Math.Abs(((Ellipse)entity).Area);
					}
					else if (entity is Hatch)
					{
						num2 += Math.Abs(((Hatch)entity).Area);
					}
				}
				catch
				{
				}
			}
			Dictionary<string, object> dictionary = new Dictionary<string, object>();
			dictionary.Add("entity_count", num);
			dictionary.Add("total_area", Round(num2));
			dictionary.Add("total_length", Round(num3));
			Dictionary<string, object> dictionary2 = dictionary;
			if (extents3d.HasValue)
			{
				dictionary2["width"] = Round(extents3d.Value.MaxPoint.X - extents3d.Value.MinPoint.X);
				dictionary2["height"] = Round(extents3d.Value.MaxPoint.Y - extents3d.Value.MinPoint.Y);
				dictionary2["min"] = Pt2(extents3d.Value.MinPoint.X, extents3d.Value.MinPoint.Y);
				dictionary2["max"] = Pt2(extents3d.Value.MaxPoint.X, extents3d.Value.MaxPoint.Y);
			}
			return dictionary2;
		}

		private static Dictionary<string, bool> BuildLayerHiddenMap(Database db, Transaction tr)
		{
			Dictionary<string, bool> dictionary = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
			foreach (ObjectId item in layerTable)
			{
				LayerTableRecord layerTableRecord = tr.GetObject(item, OpenMode.ForRead) as LayerTableRecord;
				if (layerTableRecord != null)
				{
					dictionary[layerTableRecord.Name] = layerTableRecord.IsOff || layerTableRecord.IsFrozen;
				}
			}
			return dictionary;
		}

		private static bool IsEntityVisible(Entity ent, Dictionary<string, bool> layerHiddenFlags)
		{
			if (ent == null || !ent.Visible)
			{
				return false;
			}
			string key = ent.Layer ?? "0";
			return layerHiddenFlags == null || !layerHiddenFlags.ContainsKey(key) || !layerHiddenFlags[key];
		}

		private static List<KeyValuePair<string, ObjectId>> ResolveTargetSpaces(Database db, Transaction tr, string space)
		{
			List<KeyValuePair<string, ObjectId>> list = new List<KeyValuePair<string, ObjectId>>();
			ObjectId blockModelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
			if (space == "model" || string.IsNullOrEmpty(space))
			{
				list.Add(new KeyValuePair<string, ObjectId>("Model", blockModelSpaceId));
				return list;
			}
			if (space == "all")
			{
				list.Add(new KeyValuePair<string, ObjectId>("Model", blockModelSpaceId));
			}
			DBDictionary dBDictionary = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
			foreach (DBDictionaryEntry item in dBDictionary)
			{
				if (string.Equals(item.Key, "Model", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				try
				{
					Layout layout = tr.GetObject(item.Value, OpenMode.ForRead) as Layout;
					if (layout != null && !layout.BlockTableRecordId.IsNull)
					{
						list.Add(new KeyValuePair<string, ObjectId>(item.Key, layout.BlockTableRecordId));
					}
				}
				catch
				{
				}
			}
			if (list.Count == 0)
			{
				list.Add(new KeyValuePair<string, ObjectId>("Model", blockModelSpaceId));
			}
			return list;
		}

		private static bool MatchesPatternOrSubstring(string value, string pattern)
		{
			if (string.IsNullOrEmpty(pattern))
			{
				return true;
			}
			string text = pattern.Trim();
			if (text == "*" || text == "*.*")
			{
				return true;
			}
			string text2 = value ?? "";
			if (text.IndexOf('*') >= 0 || text.IndexOf('?') >= 0)
			{
				string pattern2 = "^" + Regex.Escape(text).Replace("\\*", ".*").Replace("\\?", ".") + "$";
				return Regex.IsMatch(text2, pattern2, RegexOptions.IgnoreCase);
			}
			return text2.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static bool EntityMatchesFilters(Transaction tr, Entity ent, HashSet<string> selectedSet, bool selectedOnly, HashSet<string> handleFilter, string layerFilter, string typeFilter, string blockFilter, string textFilter, double[] bbox, bool excludeAnonymous, bool excludeSystem, bool visibleOnly, bool entVisible)
		{
			if (visibleOnly && !entVisible)
			{
				return false;
			}
			string item = ent.Handle.ToString();
			if (selectedOnly && (selectedSet == null || !selectedSet.Contains(item)))
			{
				return false;
			}
			if (handleFilter != null && handleFilter.Count > 0 && !handleFilter.Contains(item))
			{
				return false;
			}
			if (!string.IsNullOrEmpty(layerFilter) && layerFilter.Trim() != "*" && !MatchesPatternOrSubstring(ent.Layer ?? "", layerFilter))
			{
				return false;
			}
			if (!string.IsNullOrEmpty(typeFilter) && typeFilter.Trim() != "*" && !MatchesPatternOrSubstring(ent.GetType().Name, typeFilter))
			{
				return false;
			}
			if (excludeSystem && string.IsNullOrEmpty(layerFilter) && IsLegendOrHighlightLayer(ent.Layer))
			{
				return false;
			}
			BlockReference blockReference = ent as BlockReference;
			if (blockReference != null)
			{
				string text = ResolveBlockName(tr, blockReference);
				if (excludeAnonymous && IsAnonymousBlockName(text))
				{
					return false;
				}
				if (excludeSystem && (IsSystemBlockName(text) || string.Equals(ent.Layer, "ASHADE", StringComparison.OrdinalIgnoreCase)))
				{
					return false;
				}
				if (!string.IsNullOrEmpty(blockFilter) && !MatchesPatternOrSubstring(text, blockFilter))
				{
					return false;
				}
			}
			else if (!string.IsNullOrEmpty(blockFilter))
			{
				return false;
			}
			if (!string.IsNullOrEmpty(textFilter))
			{
				string text2 = ExtractEntityText(tr, ent);
				if (string.IsNullOrEmpty(text2) || text2.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0)
				{
					return false;
				}
			}
			if (bbox != null && bbox.Length >= 4)
			{
				try
				{
					Extents3d geometricExtents = ent.GeometricExtents;
					if (!IsFiniteBounds(geometricExtents))
					{
						return false;
					}
					if (geometricExtents.MaxPoint.X < bbox[0] || geometricExtents.MinPoint.X > bbox[2] || geometricExtents.MaxPoint.Y < bbox[1] || geometricExtents.MinPoint.Y > bbox[3])
					{
						return false;
					}
				}
				catch
				{
					return false;
				}
			}
			return true;
		}

		private static Autodesk.AutoCAD.DatabaseServices.Polyline CreateFramePolyline(Extents3d ext, string layerName, short colorIndex)
		{
			Autodesk.AutoCAD.DatabaseServices.Polyline polyline = new Autodesk.AutoCAD.DatabaseServices.Polyline(4);
			polyline.AddVertexAt(0, new Point2d(ext.MinPoint.X, ext.MinPoint.Y), 0.0, 0.0, 0.0);
			polyline.AddVertexAt(1, new Point2d(ext.MaxPoint.X, ext.MinPoint.Y), 0.0, 0.0, 0.0);
			polyline.AddVertexAt(2, new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y), 0.0, 0.0, 0.0);
			polyline.AddVertexAt(3, new Point2d(ext.MinPoint.X, ext.MaxPoint.Y), 0.0, 0.0, 0.0);
			polyline.Closed = true;
			polyline.Layer = layerName;
			polyline.ColorIndex = colorIndex;
			return polyline;
		}

		private static Dictionary<string, object> SerializeEntity(Transaction tr, Entity ent, HashSet<string> selectedSet, bool entVisible)
		{
			string text = ent.Handle.ToString();
			Dictionary<string, object> dictionary = new Dictionary<string, object>();
			dictionary["handle"] = text;
			dictionary["type"] = ent.GetType().Name;
			dictionary["layer"] = ent.Layer ?? "0";
			dictionary["color_index"] = ent.ColorIndex;
			dictionary["visible"] = entVisible;
			if (selectedSet != null && selectedSet.Contains(text))
			{
				dictionary["selected"] = true;
			}
			try
			{
				Extents3d geometricExtents = ent.GeometricExtents;
				if (IsFiniteBounds(geometricExtents))
				{
					dictionary["bounds"] = new double[4]
					{
						Round(geometricExtents.MinPoint.X),
						Round(geometricExtents.MinPoint.Y),
						Round(geometricExtents.MaxPoint.X),
						Round(geometricExtents.MaxPoint.Y)
					};
					dictionary["center"] = Pt2((geometricExtents.MinPoint.X + geometricExtents.MaxPoint.X) * 0.5, (geometricExtents.MinPoint.Y + geometricExtents.MaxPoint.Y) * 0.5);
				}
			}
			catch
			{
			}
			if (ent is Line)
			{
				Line line = (Line)ent;
				dictionary["start"] = Pt2(line.StartPoint.X, line.StartPoint.Y);
				dictionary["end"] = Pt2(line.EndPoint.X, line.EndPoint.Y);
				dictionary["length"] = Round(line.Length);
				dictionary["angle_deg"] = Round(line.Angle * 180.0 / Math.PI);
			}
			else if (ent is Circle)
			{
				Circle circle = (Circle)ent;
				dictionary["center"] = Pt2(circle.Center.X, circle.Center.Y);
				dictionary["radius"] = Round(circle.Radius);
				dictionary["diameter"] = Round(circle.Diameter);
				try
				{
					dictionary["area"] = Round(circle.Area);
				}
				catch
				{
				}
			}
			else if (ent is Arc)
			{
				Arc arc = (Arc)ent;
				dictionary["center"] = Pt2(arc.Center.X, arc.Center.Y);
				dictionary["radius"] = Round(arc.Radius);
				dictionary["start_angle_deg"] = Round(arc.StartAngle * 180.0 / Math.PI);
				dictionary["end_angle_deg"] = Round(arc.EndAngle * 180.0 / Math.PI);
				try
				{
					dictionary["length"] = Round(arc.Length);
				}
				catch
				{
				}
			}
			else if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline)
			{
				Autodesk.AutoCAD.DatabaseServices.Polyline polyline = (Autodesk.AutoCAD.DatabaseServices.Polyline)ent;
				dictionary["closed"] = polyline.Closed;
				dictionary["vertex_count"] = polyline.NumberOfVertices;
				List<object> list = new List<object>();
				int num = Math.Min(32, polyline.NumberOfVertices);
				for (int i = 0; i < num; i++)
				{
					Point2d point2dAt = polyline.GetPoint2dAt(i);
					list.Add(Pt2(point2dAt.X, point2dAt.Y));
				}
				dictionary["points"] = list;
				try
				{
					dictionary["length"] = Round(polyline.Length);
				}
				catch
				{
				}
				if (polyline.Closed)
				{
					try
					{
						dictionary["area"] = Round(Math.Abs(polyline.Area));
					}
					catch
					{
					}
				}
			}
			else if (ent is Ellipse)
			{
				Ellipse ellipse = (Ellipse)ent;
				dictionary["center"] = Pt2(ellipse.Center.X, ellipse.Center.Y);
				dictionary["major_radius"] = Round(ellipse.MajorRadius);
				dictionary["minor_radius"] = Round(ellipse.MinorRadius);
				try
				{
					dictionary["area"] = Round(Math.Abs(ellipse.Area));
				}
				catch
				{
				}
			}
			else if (ent is DBText)
			{
				DBText dBText = (DBText)ent;
				dictionary["position"] = Pt2(dBText.Position.X, dBText.Position.Y);
				dictionary["text"] = dBText.TextString ?? "";
				dictionary["height"] = Round(dBText.Height);
			}
			else if (ent is MText)
			{
				MText mText = (MText)ent;
				dictionary["position"] = Pt2(mText.Location.X, mText.Location.Y);
				dictionary["text"] = mText.Text ?? "";
				dictionary["height"] = Round(mText.TextHeight);
			}
			else if (ent is BlockReference)
			{
				BlockReference blockReference = (BlockReference)ent;
				object obj7 = (dictionary["block_name"] = ResolveBlockName(tr, blockReference));
				string blockName = (string)obj7;
				if (IsAnonymousBlockName(blockName))
				{
					dictionary["is_anonymous"] = true;
				}
				if (IsSystemBlockName(blockName) || string.Equals(ent.Layer, "ASHADE", StringComparison.OrdinalIgnoreCase))
				{
					dictionary["is_system"] = true;
				}
				dictionary["position"] = Pt2(blockReference.Position.X, blockReference.Position.Y);
				dictionary["rotation_deg"] = Round(blockReference.Rotation * 180.0 / Math.PI);
				dictionary["scale"] = new double[3]
				{
					Round(blockReference.ScaleFactors.X),
					Round(blockReference.ScaleFactors.Y),
					Round(blockReference.ScaleFactors.Z)
				};
				List<object> list2 = new List<object>();
				foreach (ObjectId item in blockReference.AttributeCollection)
				{
					try
					{
						AttributeReference attributeReference = tr.GetObject(item, OpenMode.ForRead) as AttributeReference;
						if (attributeReference != null)
						{
							list2.Add(new Dictionary<string, object>
							{
								{ "tag", attributeReference.Tag },
								{
									"text",
									attributeReference.TextString ?? ""
								}
							});
						}
					}
					catch
					{
					}
				}
				if (list2.Count > 0)
				{
					dictionary["attributes"] = list2;
				}
			}
			else if (ent is Dimension)
			{
				Dimension dimension = (Dimension)ent;
				dictionary["dimension_text"] = dimension.DimensionText ?? "";
				try
				{
					dictionary["measurement"] = Round(dimension.Measurement);
				}
				catch
				{
				}
			}
			else if (ent is Hatch)
			{
				Hatch hatch = (Hatch)ent;
				dictionary["pattern_name"] = hatch.PatternName ?? "";
				try
				{
					dictionary["area"] = Round(Math.Abs(hatch.Area));
				}
				catch
				{
				}
			}
			return dictionary;
		}

		private static string ResolveBlockName(Transaction tr, BlockReference br)
		{
			string name = br.Name;
			try
			{
				if (br.IsDynamicBlock && !br.DynamicBlockTableRecord.IsNull)
				{
					BlockTableRecord blockTableRecord = tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
					if (blockTableRecord != null)
					{
						name = blockTableRecord.Name;
					}
				}
			}
			catch
			{
			}
			return name ?? "";
		}

		private static string ExtractEntityText(Transaction tr, Entity ent)
		{
			if (ent is DBText)
			{
				return ((DBText)ent).TextString ?? "";
			}
			if (ent is MText)
			{
				return ((MText)ent).Text ?? "";
			}
			if (ent is Dimension)
			{
				return ((Dimension)ent).DimensionText ?? "";
			}
			if (ent is BlockReference)
			{
				BlockReference blockReference = (BlockReference)ent;
				StringBuilder stringBuilder = new StringBuilder();
				foreach (ObjectId item in blockReference.AttributeCollection)
				{
					try
					{
						AttributeReference attributeReference = tr.GetObject(item, OpenMode.ForRead) as AttributeReference;
						if (attributeReference != null && !string.IsNullOrEmpty(attributeReference.TextString))
						{
							stringBuilder.Append(attributeReference.TextString).Append(" ");
						}
					}
					catch
					{
					}
				}
				return stringBuilder.ToString();
			}
			return "";
		}

		internal static List<string> GetSelectedHandleStrings(Document doc)
		{
			List<string> list = new List<string>();
			if (doc == null) return list;
			try
			{
				using (DocumentLock docLock = doc.LockDocument())
				{
					PromptSelectionResult promptSelectionResult = doc.Editor.SelectImplied();
					if (promptSelectionResult != null && promptSelectionResult.Status == PromptStatus.OK && promptSelectionResult.Value != null)
					{
						using (Transaction transaction = doc.Database.TransactionManager.StartTransaction())
						{
							foreach (SelectedObject item in promptSelectionResult.Value)
							{
								if (item != null && !item.ObjectId.IsNull && !item.ObjectId.IsErased)
								{
									DBObject @object = transaction.GetObject(item.ObjectId, OpenMode.ForRead);
									if (@object != null && !@object.IsErased)
									{
										list.Add(@object.Handle.ToString());
									}
								}
							}
							transaction.Commit();
						}
					}
				}
			}
			catch
			{
			}
			return list;
		}

		private static ObjectId ResolveHandle(Database db, string handleStr)
		{
			if (string.IsNullOrWhiteSpace(handleStr))
			{
				return ObjectId.Null;
			}
			try
			{
				string text = handleStr.Trim();
				if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				{
					text = text.Substring(2);
				}
				long value = Convert.ToInt64(text, 16);
				Handle objHandle = new Handle(value);
				return db.GetObjectId(false, objHandle, 0);
			}
			catch
			{
				return ObjectId.Null;
			}
		}

		private static Dictionary<string, object> GetUnitsInfo(Database db)
		{
			UnitsValue insunits = db.Insunits;
			double v = 10.0;
			double v2 = 1000.0;
			switch (insunits)
			{
			case UnitsValue.Centimeters:
				v = 1.0;
				v2 = 100.0;
				break;
			case UnitsValue.Meters:
				v = 0.01;
				v2 = 1.0;
				break;
			case UnitsValue.Inches:
				v = 0.39370078740157477;
				v2 = 39.37007874015748;
				break;
			case UnitsValue.Feet:
				v = 25.0 / 762.0;
				v2 = 3.2808398950131235;
				break;
			}
			Dictionary<string, object> dictionary = new Dictionary<string, object>();
			dictionary.Add("code", (int)insunits);
			dictionary.Add("name", insunits.ToString());
			dictionary.Add("units_per_cm", Round(v));
			dictionary.Add("units_per_m", Round(v2));
			return dictionary;
		}

		private static short ClampColor(int c)
		{
			if (c < 0)
			{
				return 256;
			}
			if (c > 256)
			{
				return 256;
			}
			return (short)c;
		}

		private static double Round(double v)
		{
			if (double.IsNaN(v) || double.IsInfinity(v))
			{
				return 0.0;
			}
			return Math.Round(v, 4);
		}

		private static double[] Pt2(double x, double y)
		{
			return new double[2]
			{
				Round(x),
				Round(y)
			};
		}

		private static object GetObj(Dictionary<string, object> d, string key)
		{
			if (d == null)
			{
				return null;
			}
			object value;
			return d.TryGetValue(key, out value) ? value : null;
		}

		private static string GetStr(Dictionary<string, object> d, string key)
		{
			object obj = GetObj(d, key);
			return (obj != null) ? obj.ToString() : null;
		}

		private static bool GetBool(Dictionary<string, object> d, string key, bool defVal)
		{
			object obj = GetObj(d, key);
			if (obj == null)
			{
				return defVal;
			}
			if (obj is bool)
			{
				return (bool)obj;
			}
			bool result;
			if (bool.TryParse(obj.ToString(), out result))
			{
				return result;
			}
			return defVal;
		}

		private static bool? GetNullableBool(Dictionary<string, object> d, string key)
		{
			object obj = GetObj(d, key);
			if (obj == null)
			{
				return null;
			}
			if (obj is bool)
			{
				return (bool)obj;
			}
			bool result;
			if (bool.TryParse(obj.ToString(), out result))
			{
				return result;
			}
			return null;
		}

		private static int GetInt(Dictionary<string, object> d, string key, int defVal)
		{
			int? nullableInt = GetNullableInt(d, key);
			return nullableInt.HasValue ? nullableInt.Value : defVal;
		}

		private static int? GetNullableInt(Dictionary<string, object> d, string key)
		{
			object obj = GetObj(d, key);
			if (obj == null)
			{
				return null;
			}
			try
			{
				return Convert.ToInt32(obj, CultureInfo.InvariantCulture);
			}
			catch
			{
				return null;
			}
		}

		private static double GetDouble(Dictionary<string, object> d, string key, double defVal)
		{
			double? nullableDouble = GetNullableDouble(d, key);
			return nullableDouble.HasValue ? nullableDouble.Value : defVal;
		}

		private static double? GetNullableDouble(Dictionary<string, object> d, string key)
		{
			object obj = GetObj(d, key);
			if (obj == null)
			{
				return null;
			}
			try
			{
				return Convert.ToDouble(obj, CultureInfo.InvariantCulture);
			}
			catch
			{
				return null;
			}
		}

		private static Dictionary<string, object> AsDict(object o)
		{
			return o as Dictionary<string, object>;
		}

		private static List<object> AsList(object o)
		{
			List<object> list = new List<object>();
			if (o == null || o is string)
			{
				return list;
			}
			IEnumerable enumerable = o as IEnumerable;
			if (enumerable != null)
			{
				foreach (object item in enumerable)
				{
					list.Add(item);
				}
			}
			return list;
		}

		private static List<string> GetStringList(Dictionary<string, object> d, string key)
		{
			List<string> list = new List<string>();
			foreach (object item in AsList(GetObj(d, key)))
			{
				if (item != null)
				{
					string text = item.ToString().Trim();
					if (text.Length > 0)
					{
						list.Add(text);
					}
				}
			}
			return list;
		}

		private static HashSet<string> GetStringSet(Dictionary<string, object> d, string key)
		{
			return new HashSet<string>(GetStringList(d, key), StringComparer.OrdinalIgnoreCase);
		}

		private static double[] GetDoubleArray(Dictionary<string, object> d, string key)
		{
			return ToDoublePair(GetObj(d, key));
		}

		private static double[] ToDoublePair(object o)
		{
			List<object> list = AsList(o);
			if (list.Count == 0)
			{
				return null;
			}
			double[] array = new double[list.Count];
			for (int i = 0; i < list.Count; i++)
			{
				try
				{
					array[i] = Convert.ToDouble(list[i], CultureInfo.InvariantCulture);
				}
				catch
				{
					array[i] = 0.0;
				}
			}
			return array;
		}

		private static Point3d GetPoint3d(Dictionary<string, object> d, string key, Point3d defPt)
		{
			double[] array = ToDoublePair(GetObj(d, key));
			if (array == null || array.Length < 2)
			{
				return defPt;
			}
			return new Point3d(array[0], array[1], (array.Length >= 3) ? array[2] : 0.0);
		}
    }
}
