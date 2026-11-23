using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace AIEngineer.Revit.Direct
{
    internal static partial class RevitToolExecutor
    {
        private sealed class ParamBackup
        {
            public ElementId Id;
            public string ParamName;
            public string OldValue;
        }

        private sealed class RevitUndoRecord
        {
            public string ActionId;
            public string ToolName;
            public string Summary;
            public bool HasRedPreview;
            public ElementId PreviewViewId = ElementId.InvalidElementId;
            public List<ElementId> CreatedIds = new List<ElementId>();
            public List<ParamBackup> ModifiedParams = new List<ParamBackup>();
        }

        private static readonly object _undoLock = new object();
        private static readonly ConditionalWeakTable<Document, List<RevitUndoRecord>> _undoByDoc =
            new ConditionalWeakTable<Document, List<RevitUndoRecord>>();
        private static int _undoSeq = 0;

        internal static void RecordUndo(Document doc, View previewView, string tool, string summary, List<ElementId> created, bool redPreview)
        {
            if (doc == null) return;
            RevitUndoRecord rec = new RevitUndoRecord
            {
                ToolName = tool,
                Summary = summary,
                HasRedPreview = redPreview,
                PreviewViewId = (redPreview && previewView != null) ? previewView.Id : ElementId.InvalidElementId
            };
            if (created != null) rec.CreatedIds.AddRange(created);
            PushUndo(doc, rec);
        }

        private static void PushUndo(Document doc, RevitUndoRecord rec)
        {
            if (doc == null || rec == null) return;
            lock (_undoLock)
            {
                rec.ActionId = "rvt_undo_" + (++_undoSeq).ToString(CultureInfo.InvariantCulture);
                List<RevitUndoRecord> list;
                if (!_undoByDoc.TryGetValue(doc, out list))
                {
                    list = new List<RevitUndoRecord>();
                    _undoByDoc.Add(doc, list);
                }
                list.Add(rec);
                if (list.Count > 30) list.RemoveRange(0, list.Count - 30);
            }
        }

        internal static int GetUndoCount(Document doc)
        {
            if (doc == null) return 0;
            lock (_undoLock)
            {
                List<RevitUndoRecord> list;
                return _undoByDoc.TryGetValue(doc, out list) ? list.Count : 0;
            }
        }

        internal static void ApplyRedPreviewOverrides(View view, IList<ElementId> ids)
        {
            if (view == null || ids == null || ids.Count == 0) return;
            try
            {
                OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                Color red = new Color(239, 68, 68);
                ogs.SetProjectionLineColor(red);
                ogs.SetProjectionLineWeight(5);
                for (int i = 0; i < ids.Count; i++)
                {
                    try { view.SetElementOverrides(ids[i], ogs); } catch { }
                }
            }
            catch { }
        }

        internal static void ClearPreviewOverrides(View view, IList<ElementId> ids)
        {
            if (view == null || ids == null || ids.Count == 0) return;
            OverrideGraphicSettings clear = new OverrideGraphicSettings();
            for (int i = 0; i < ids.Count; i++)
            {
                try { view.SetElementOverrides(ids[i], clear); } catch { }
            }
        }

        public static Dictionary<string, object> ExecuteTool(UIApplication uiApp, string toolName, Dictionary<string, object> args)
        {
            if (uiApp == null || uiApp.ActiveUIDocument == null || uiApp.ActiveUIDocument.Document == null)
                return Err("No active Revit document is open.");

            UIDocument uidoc = uiApp.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;
            Dictionary<string, object> safeArgs = args ?? new Dictionary<string, object>();

            try
            {
                switch ((toolName ?? "").Trim().ToLowerInvariant())
                {
                    case "inspect_model": return InspectModel(uidoc, doc, activeView, safeArgs);
                    case "query_elements": return QueryElements(doc, safeArgs);
                    case "select_and_zoom": return SelectAndZoom(uidoc, doc, safeArgs);
                    case "place_electrical_family": return PlaceElectricalFamily(doc, activeView, safeArgs);
                    case "place_room_outlets": return PlaceRoomOutlets(doc, activeView, safeArgs);
                    case "create_electrical_circuits": return CreateElectricalCircuits(doc, safeArgs);
                    case "modify_parameters": return ModifyParameters(doc, safeArgs);
                    case "delete_elements": return DeleteElements(doc, safeArgs);
                    case "quantity_takeoff_mep": return QuantityTakeoffMep(doc, safeArgs);
                    case "export_to_excel": return ExportToExcel(doc, safeArgs);
                    case "distribute_lighting": return DistributeLighting(doc, activeView, safeArgs);
                    case "export_dialux_stf": return ExportDialuxStf(doc, safeArgs);
                    case "calculate_lv_electrical": return CalculateLvElectrical(doc, safeArgs);
                    case "approve_or_rollback_preview": return ApproveOrRollbackPreview(doc, activeView, safeArgs);
                    case "undo_last_action": return UndoLastAction(doc, activeView, safeArgs);
                    default:
                        return Err("Unknown Revit tool: " + toolName);
                }
            }
            catch (Exception ex)
            {
                return Err("Tool '" + toolName + "' failed: " + ex.Message);
            }
        }

        private static Dictionary<string, object> InspectModel(UIDocument uidoc, Document doc, View activeView, Dictionary<string, object> args)
        {
            List<object> levels = new List<object>();
            foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(Level)))
            {
                Level lv = el as Level;
                if (lv == null) continue;
                levels.Add(new Dictionary<string, object>
                {
                    { "id", RevitCompat.GetIdValue(lv.Id) },
                    { "name", lv.Name },
                    { "elevation_m", Math.Round(RevitCompat.FeetToMeters(lv.Elevation), 3) }
                });
            }

            List<RevitRoomBounds> rooms = CollectAllRooms(doc, null, null, true);
            List<object> roomList = new List<object>();
            for (int i = 0; i < Math.Min(rooms.Count, 80); i++)
            {
                RevitRoomBounds r = rooms[i];
                roomList.Add(new Dictionary<string, object>
                {
                    { "id", r.Id }, { "name", r.Name }, { "number", r.Number },
                    { "level", r.LevelName }, { "area_m2", r.AreaM2 }, { "height_m", r.HeightM },
                    { "from_link", r.FromLink }
                });
            }

            List<object> panels = new List<object>();
            foreach (Element el in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment).WhereElementIsNotElementType())
            {
                panels.Add(new Dictionary<string, object>
                {
                    { "id", RevitCompat.GetIdValue(el.Id) },
                    { "name", el.Name },
                    { "panel_name", RevitCompat.ReadParamString(el, BuiltInParameter.RBS_ELEC_PANEL_NAME) },
                    { "mark", RevitCompat.ReadParamString(el, BuiltInParameter.ALL_MODEL_MARK) }
                });
            }

            int circuitCount = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).GetElementCount();
            int lightingCount = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType().GetElementCount();
            int fixtureCount = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalFixtures).WhereElementIsNotElementType().GetElementCount();

            ICollection<ElementId> selIds = uidoc.Selection.GetElementIds();
            List<long> selList = new List<long>();
            foreach (ElementId sid in selIds) selList.Add(RevitCompat.GetIdValue(sid));

            return Ok(new Dictionary<string, object>
            {
                { "document_title", doc.Title },
                { "revit_version", doc.Application.VersionName },
                { "active_view", activeView != null ? activeView.Name : "" },
                { "levels_count", levels.Count },
                { "levels", levels },
                { "rooms_and_spaces_count", rooms.Count },
                { "rooms_sample", roomList },
                { "lighting_fixtures_count", lightingCount },
                { "electrical_fixtures_count", fixtureCount },
                { "electrical_equipment_panels", panels },
                { "electrical_circuits_count", circuitCount },
                { "loaded_lighting_types", ListLoadedFamilyTypeNames(doc, BuiltInCategory.OST_LightingFixtures, 25) },
                { "loaded_outlet_types", ListLoadedFamilyTypeNames(doc, BuiltInCategory.OST_ElectricalFixtures, 25) },
                { "loaded_panel_types", ListLoadedFamilyTypeNames(doc, BuiltInCategory.OST_ElectricalEquipment, 25) },
                { "selected_element_ids", selList },
                { "undo_history_steps", GetUndoCount(doc) }
            });
        }

        private static Dictionary<string, object> QueryElements(Document doc, Dictionary<string, object> args)
        {
            string catName = (GetStr(args, "category") ?? "lighting_fixtures").Trim().ToLowerInvariant();
            string levelFilter = GetStr(args, "level_name");
            string nameFilter = GetStr(args, "name_contains");
            string markFilter = GetStr(args, "mark");
            int limit = Math.Max(1, Math.Min(300, GetInt(args, "limit", 100)));

            BuiltInCategory bic;
            if (!TryResolveCategory(catName, out bic))
                return Err("Unsupported category '" + catName + "'. Use: lighting_fixtures, electrical_fixtures, electrical_equipment, electrical_circuits, conduits, cable_trays, wires, rooms, spaces, walls, ceilings.");

            FilteredElementCollector col = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType();
            List<object> items = new List<object>();
            int totalMatched = 0;

            foreach (Element el in col)
            {
                Level lv = RevitCompat.IsValidId(el.LevelId) ? (doc.GetElement(el.LevelId) as Level) : null;
                string lvName = lv != null ? lv.Name : "";
                if (!string.IsNullOrWhiteSpace(levelFilter) && lvName.IndexOf(levelFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                FamilyInstance fi = el as FamilyInstance;
                string famType = fi != null && fi.Symbol != null ? (fi.Symbol.FamilyName + " : " + fi.Symbol.Name) : el.Name;
                if (!string.IsNullOrWhiteSpace(nameFilter) && famType.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                string mark = RevitCompat.ReadParamString(el, BuiltInParameter.ALL_MODEL_MARK);
                if (!string.IsNullOrWhiteSpace(markFilter) && mark.IndexOf(markFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                totalMatched++;
                if (items.Count >= limit) continue;

                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    { "id", RevitCompat.GetIdValue(el.Id) },
                    { "family_type", famType },
                    { "level", lvName },
                    { "mark", mark },
                    { "panel", RevitCompat.ReadParamString(el, BuiltInParameter.RBS_ELEC_PANEL_NAME) },
                    { "circuit", RevitCompat.ReadParamString(el, BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER) }
                };
                LocationPoint lp = el.Location as LocationPoint;
                if (lp != null && lp.Point != null)
                {
                    row["x_m"] = Math.Round(RevitCompat.FeetToMeters(lp.Point.X), 3);
                    row["y_m"] = Math.Round(RevitCompat.FeetToMeters(lp.Point.Y), 3);
                    row["z_m"] = Math.Round(RevitCompat.FeetToMeters(lp.Point.Z), 3);
                }
                items.Add(row);
            }

            return Ok(new Dictionary<string, object>
            {
                { "category", catName },
                { "total_matched", totalMatched },
                { "returned_count", items.Count },
                { "elements", items }
            });
        }

        private static Dictionary<string, object> SelectAndZoom(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            List<ElementId> ids = ParseElementIds(doc, args, "element_ids");
            if (ids.Count == 0) return Err("No valid existing ElementIds provided for select_and_zoom.");
            uidoc.Selection.SetElementIds(ids);
            if (GetBool(args, "zoom", true))
            {
                try { uidoc.ShowElements(ids); } catch { }
            }
            return Ok(new Dictionary<string, object> { { "selected_count", ids.Count } });
        }

        private static Dictionary<string, object> PlaceElectricalFamily(Document doc, View activeView, Dictionary<string, object> args)
        {
            string catStr = (GetStr(args, "category") ?? "electrical_fixtures").Trim().ToLowerInvariant();
            BuiltInCategory bic;
            if (!TryResolveCategory(catStr, out bic)) bic = BuiltInCategory.OST_ElectricalFixtures;
            string famReq = GetStr(args, "family_type") ?? GetStr(args, "family_name");
            FamilySymbol sym = FindLoadedFamilySymbol(doc, bic, famReq);
            if (sym == null)
            {
                Dictionary<string, object> err = Err("Family/Type not found in model for category '" + catStr + "' and query '" + (famReq ?? "") + "'.");
                err["available_types"] = ListLoadedFamilyTypeNames(doc, bic, 25);
                return err;
            }

            Level lvl = ResolveLevelByNameOrDefault(doc, activeView, GetStr(args, "level_name"));
            if (lvl == null) return Err("No valid Level found in the Revit document.");

            double xM = GetDouble(args, "x_m", 0.0);
            double yM = GetDouble(args, "y_m", 0.0);
            double elevM = GetDouble(args, "elevation_m", 0.0);
            double rotDeg = GetDouble(args, "rotation_deg", 0.0);
            string mark = GetStr(args, "mark");
            string panelName = GetStr(args, "panel_name");
            bool previewRed = GetBool(args, "preview_red", true);

            ElementId createdId = ElementId.InvalidElementId;
            using (Transaction tr = new Transaction(doc, "AI-Engineer: Place " + sym.Name))
            {
                tr.Start();
                if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                XYZ pt = new XYZ(RevitCompat.MetersToFeet(xM), RevitCompat.MetersToFeet(yM), lvl.Elevation + RevitCompat.MetersToFeet(elevM));
                FamilyInstance fi = doc.Create.NewFamilyInstance(pt, sym, lvl, StructuralType.NonStructural);
                if (fi == null) { tr.RollBack(); return Err("Revit could not place this family at the specified point (it may require a Wall/Ceiling host)."); }

                if (Math.Abs(rotDeg) > 1e-4)
                {
                    Line axis = Line.CreateBound(pt, new XYZ(pt.X, pt.Y, pt.Z + 1.0));
                    ElementTransformUtils.RotateElement(doc, fi.Id, axis, rotDeg * Math.PI / 180.0);
                }
                Parameter offP = fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                if (offP != null && !offP.IsReadOnly) offP.Set(RevitCompat.MetersToFeet(elevM));
                if (!string.IsNullOrWhiteSpace(mark))
                {
                    Parameter mp = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (mp != null && !mp.IsReadOnly) mp.Set(mark);
                }
                if (!string.IsNullOrWhiteSpace(panelName))
                {
                    Parameter pp = fi.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME);
                    if (pp != null && !pp.IsReadOnly) pp.Set(panelName);
                }
                createdId = fi.Id;
                if (previewRed && activeView != null)
                    ApplyRedPreviewOverrides(activeView, new List<ElementId> { createdId });
                tr.Commit();
            }

            RecordUndo(doc, activeView, "place_electrical_family", "Placed " + sym.FamilyName + " : " + sym.Name, new List<ElementId> { createdId }, previewRed);
            return Ok(new Dictionary<string, object>
            {
                { "element_id", RevitCompat.GetIdValue(createdId) },
                { "family_type", sym.FamilyName + " : " + sym.Name },
                { "level", lvl.Name },
                { "x_m", xM }, { "y_m", yM }, { "elevation_m", elevM }
            });
        }

        private static Dictionary<string, object> PlaceRoomOutlets(Document doc, View activeView, Dictionary<string, object> args)
        {
            string levelFilter = GetStr(args, "level_name");
            string roomFilter = GetStr(args, "room_name");
            string famReq = GetStr(args, "family_type") ?? GetStr(args, "family_name");
            double mountM = GetDouble(args, "mounting_height_m", 0.30);
            double spacingM = Math.Max(1.2, GetDouble(args, "spacing_m", 3.5));
            double insetM = Math.Max(0.02, Math.Min(0.5, GetDouble(args, "wall_inset_m", 0.08)));
            int maxPerRoom = Math.Max(1, Math.Min(20, GetInt(args, "max_per_room", 6)));
            bool previewRed = GetBool(args, "preview_red", true);

            FamilySymbol sym = FindLoadedFamilySymbol(doc, BuiltInCategory.OST_ElectricalFixtures, famReq);
            if (sym == null)
            {
                Dictionary<string, object> err = Err("No matching Electrical Fixture (socket/outlet) family loaded in the model.");
                err["available_outlet_types"] = ListLoadedFamilyTypeNames(doc, BuiltInCategory.OST_ElectricalFixtures, 25);
                return err;
            }

            List<RevitRoomBounds> rooms = CollectAllRooms(doc, levelFilter, roomFilter, true);
            if (rooms.Count == 0) return Err("No matching Rooms or MEP Spaces found.");

            Dictionary<string, Level> levelsByName = IndexLevelsByName(doc);
            Level fallbackLvl = GetDefaultLevel(doc, activeView);
            List<ElementId> createdIds = new List<ElementId>();
            string firstErr = null;

            using (Transaction tr = new Transaction(doc, "AI-Engineer: Place Room Outlets"))
            {
                tr.Start();
                if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                for (int rIdx = 0; rIdx < rooms.Count; rIdx++)
                {
                    RevitRoomBounds r = rooms[rIdx];
                    Level lvl = ResolveLevelForRoomOrElevation(levelsByName, r.LevelName, r.LevelElevationM, fallbackLvl);
                    if (lvl == null) continue;
                    double w = Math.Max(1.0, r.MaxXm - r.MinXm);
                    double l = Math.Max(1.0, r.MaxYm - r.MinYm);

                    List<double[]> wallPts = new List<double[]>();
                    int nX = Math.Max(1, Math.Min(3, (int)Math.Floor(w / spacingM)));
                    int nY = Math.Max(1, Math.Min(3, (int)Math.Floor(l / spacingM)));
                    for (int k = 0; k < nX; k++)
                    {
                        double x = r.MinXm + (k + 0.5) * (w / nX);
                        wallPts.Add(new double[] { x, r.MinYm + insetM });
                        wallPts.Add(new double[] { x, r.MaxYm - insetM });
                    }
                    for (int k = 0; k < nY; k++)
                    {
                        double y = r.MinYm + (k + 0.5) * (l / nY);
                        wallPts.Add(new double[] { r.MinXm + insetM, y });
                        wallPts.Add(new double[] { r.MaxXm - insetM, y });
                    }

                    int placedHere = 0;
                    for (int p = 0; p < wallPts.Count && placedHere < maxPerRoom; p++)
                    {
                        if (!IsPointInPolygon2D(r.PolygonM, wallPts[p][0], wallPts[p][1])) continue;
                        try
                        {
                            XYZ pt = new XYZ(RevitCompat.MetersToFeet(wallPts[p][0]), RevitCompat.MetersToFeet(wallPts[p][1]), lvl.Elevation + RevitCompat.MetersToFeet(mountM));
                            FamilyInstance fi = doc.Create.NewFamilyInstance(pt, sym, lvl, StructuralType.NonStructural);
                            if (fi != null)
                            {
                                Parameter offP = fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                                if (offP != null && !offP.IsReadOnly) offP.Set(RevitCompat.MetersToFeet(mountM));
                                Parameter mp = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                                if (mp != null && !mp.IsReadOnly) mp.Set("P" + (rIdx + 1) + "-" + (placedHere + 1).ToString("D2", CultureInfo.InvariantCulture));
                                createdIds.Add(fi.Id);
                                placedHere++;
                            }
                        }
                        catch (Exception ex) { if (firstErr == null) firstErr = ex.Message; }
                    }
                }
                if (previewRed && activeView != null && createdIds.Count > 0)
                    ApplyRedPreviewOverrides(activeView, createdIds);
                tr.Commit();
            }

            if (createdIds.Count == 0)
            {
                Dictionary<string, object> fErr = Err("0 outlets could be placed using '" + sym.FamilyName + " : " + sym.Name + "' (family may be Wall/Face-hosted). Detail: " + (firstErr ?? "NewFamilyInstance returned null."));
                fErr["available_outlet_types"] = ListLoadedFamilyTypeNames(doc, BuiltInCategory.OST_ElectricalFixtures, 25);
                return fErr;
            }

            RecordUndo(doc, activeView, "place_room_outlets", "Placed " + createdIds.Count + " outlets across " + rooms.Count + " rooms", createdIds, previewRed);

            return Ok(new Dictionary<string, object>
            {
                { "rooms_processed", rooms.Count },
                { "outlets_placed", createdIds.Count },
                { "family_used", sym.FamilyName + " : " + sym.Name },
                { "mounting_height_m", mountM }
            });
        }

        private static Dictionary<string, object> CreateElectricalCircuits(Document doc, Dictionary<string, object> args)
        {
            List<ElementId> ids = ParseElementIds(doc, args, "element_ids");
            if (ids.Count == 0) return Err("Provide valid existing element_ids to group into electrical circuits.");
            int maxPerCircuit = Math.Max(1, Math.Min(40, GetInt(args, "max_devices_per_circuit", 10)));
            string panelNameReq = GetStr(args, "panel_name");
            long? panelIdReq = GetNullableLong(args, "panel_id");

            FamilyInstance targetPanel = null;
            if (panelIdReq.HasValue && panelIdReq.Value > 0)
                targetPanel = doc.GetElement(RevitCompat.CreateId(panelIdReq.Value)) as FamilyInstance;
            if (targetPanel == null && !string.IsNullOrWhiteSpace(panelNameReq))
            {
                foreach (Element el in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment).WhereElementIsNotElementType())
                {
                    FamilyInstance fi = el as FamilyInstance;
                    if (fi == null) continue;
                    string pn = RevitCompat.ReadParamString(fi, BuiltInParameter.RBS_ELEC_PANEL_NAME);
                    if (string.Equals(pn, panelNameReq, StringComparison.OrdinalIgnoreCase) ||
                        fi.Name.IndexOf(panelNameReq, StringComparison.OrdinalIgnoreCase) >= 0)
                    { targetPanel = fi; break; }
                }
            }

            bool panelRequested = (panelIdReq.HasValue && panelIdReq.Value > 0) || !string.IsNullOrWhiteSpace(panelNameReq);
            if (panelRequested && targetPanel == null)
                return Err("Requested distribution panel was not found in the active Revit model (panel_name='" + (panelNameReq ?? "") + "'). Call inspect_model to list real panels.");

            List<object> createdCircuits = new List<object>();
            List<ElementId> createdIds = new List<ElementId>();
            List<string> panelWarnings = new List<string>();
            using (Transaction tr = new Transaction(doc, "AI-Engineer: Create Electrical Circuits"))
            {
                tr.Start();
                for (int offset = 0; offset < ids.Count; offset += maxPerCircuit)
                {
                    List<ElementId> batch = ids.GetRange(offset, Math.Min(maxPerCircuit, ids.Count - offset));
                    ElectricalSystem sys = RevitCompat.CreatePowerCircuit(doc, batch);
                    if (sys != null)
                    {
                        string pErr = null;
                        if (targetPanel != null)
                        {
                            try { sys.SelectPanel(targetPanel); }
                            catch (Exception selEx)
                            {
                                pErr = selEx.Message;
                                panelWarnings.Add("Circuit " + (sys.CircuitNumber ?? sys.Name) + ": " + selEx.Message);
                            }
                        }
                        createdIds.Add(sys.Id);
                        Dictionary<string, object> cInfo = new Dictionary<string, object>
                        {
                            { "circuit_id", RevitCompat.GetIdValue(sys.Id) },
                            { "circuit_number", sys.CircuitNumber ?? sys.Name },
                            { "panel", sys.PanelName ?? "" },
                            { "devices_count", batch.Count }
                        };
                        if (!string.IsNullOrEmpty(pErr)) cInfo["panel_error"] = pErr;
                        createdCircuits.Add(cInfo);
                    }
                }
                tr.Commit();
            }

            if (createdIds.Count > 0)
                RecordUndo(doc, null, "create_electrical_circuits", "Created " + createdIds.Count + " electrical circuits", createdIds, false);

            Dictionary<string, object> cResp = new Dictionary<string, object>
            {
                { "circuits_created", createdCircuits.Count },
                { "panel_assigned", (targetPanel != null && panelWarnings.Count == 0) ? targetPanel.Name : "partial_or_unassigned" },
                { "circuits", createdCircuits }
            };
            if (panelWarnings.Count > 0) cResp["panel_errors"] = panelWarnings;
            return Ok(cResp);
        }

        private static Dictionary<string, object> ModifyParameters(Document doc, Dictionary<string, object> args)
        {
            List<ElementId> ids = ParseElementIds(doc, args, "element_ids");
            string paramName = GetStr(args, "parameter_name");
            if (ids.Count == 0) return Err("element_ids is required.");
            if (string.IsNullOrWhiteSpace(paramName)) return Err("parameter_name is required.");
            if (!args.ContainsKey("value")) return Err("value is required.");
            object val = args["value"];

            List<ParamBackup> backups = new List<ParamBackup>();
            List<string> errors = new List<string>();
            int updated = 0;

            using (Transaction tr = new Transaction(doc, "AI-Engineer: Modify Parameter " + paramName))
            {
                tr.Start();
                for (int i = 0; i < ids.Count; i++)
                {
                    Element el = doc.GetElement(ids[i]);
                    string oldVal, err;
                    if (RevitCompat.SetParameterValue(el, paramName, val, out oldVal, out err))
                    {
                        backups.Add(new ParamBackup { Id = ids[i], ParamName = paramName, OldValue = oldVal });
                        updated++;
                    }
                    else if (errors.Count < 10)
                    {
                        errors.Add("ID " + RevitCompat.GetIdValue(ids[i]) + ": " + err);
                    }
                }
                tr.Commit();
            }

            if (backups.Count > 0)
            {
                RevitUndoRecord rec = new RevitUndoRecord
                {
                    ToolName = "modify_parameters",
                    Summary = "Updated '" + paramName + "' on " + updated + " elements"
                };
                rec.ModifiedParams.AddRange(backups);
                PushUndo(doc, rec);
            }

            Dictionary<string, object> resp = new Dictionary<string, object>
            {
                { "updated_count", updated },
                { "parameter_name", paramName }
            };
            if (errors.Count > 0) resp["errors"] = errors;
            return Ok(resp);
        }

        private static Dictionary<string, object> DeleteElements(Document doc, Dictionary<string, object> args)
        {
            List<ElementId> ids = ParseElementIds(doc, args, "element_ids");
            if (ids.Count == 0) return Err("No valid existing element_ids to delete.");
            ICollection<ElementId> deleted;
            using (Transaction tr = new Transaction(doc, "AI-Engineer: Delete Elements"))
            {
                tr.Start();
                deleted = doc.Delete(ids);
                tr.Commit();
            }
            return Ok(new Dictionary<string, object> { { "deleted_count", deleted != null ? deleted.Count : 0 } });
        }

        private static Dictionary<string, object> QuantityTakeoffMep(Document doc, Dictionary<string, object> args)
        {
            double wastePct = Math.Max(0.0, Math.Min(50.0, GetDouble(args, "waste_percent", 10.0)));
            double wasteMult = 1.0 + (wastePct / 100.0);

            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            BuiltInCategory[] countCats = new BuiltInCategory[]
            {
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_LightingDevices,
                BuiltInCategory.OST_FireAlarmDevices,
                BuiltInCategory.OST_DataDevices
            };
            for (int c = 0; c < countCats.Length; c++)
            {
                foreach (Element el in new FilteredElementCollector(doc).OfCategory(countCats[c]).WhereElementIsNotElementType())
                {
                    FamilyInstance fi = el as FamilyInstance;
                    string key = (el.Category != null ? el.Category.Name : "MEP") + " | " +
                                 (fi != null && fi.Symbol != null ? (fi.Symbol.FamilyName + " : " + fi.Symbol.Name) : el.Name);
                    int cur;
                    counts.TryGetValue(key, out cur);
                    counts[key] = cur + 1;
                }
            }

            double conduitM = SumLinearCategoryM(doc, BuiltInCategory.OST_Conduit);
            double trayM = SumLinearCategoryM(doc, BuiltInCategory.OST_CableTray);
            List<RevitRoomBounds> rooms = CollectAllRooms(doc, null, null, true);
            double totalAreaM2 = 0.0;
            for (int i = 0; i < rooms.Count; i++) totalAreaM2 += rooms[i].AreaM2;

            RevitExcelSheet xl = new RevitExcelSheet();
            xl.Title = "حصر الكميات الكهربائية والميكانيكية (BIM BOQ) - " + doc.Title;
            xl.SheetName = "MEP_BOQ";
            xl.Headers = new List<string> { "البند / التصنيف", "الوحدة", "الكمية الصافية", "مع الهالك (" + wastePct + "%)" };
            List<object> boqItems = new List<object>();

            foreach (KeyValuePair<string, int> kv in counts)
            {
                boqItems.Add(new Dictionary<string, object> { { "item", kv.Key }, { "unit", "Nos" }, { "qty", kv.Value } });
                xl.Rows.Add(new List<string> { kv.Key, "عدد", kv.Value.ToString(CultureInfo.InvariantCulture), kv.Value.ToString(CultureInfo.InvariantCulture) });
            }
            if (conduitM > 0.01)
            {
                xl.Rows.Add(new List<string> { "Conduit Runs / مواسير الكهرباء", "متر طولي", Math.Round(conduitM, 2).ToString(CultureInfo.InvariantCulture), Math.Round(conduitM * wasteMult, 2).ToString(CultureInfo.InvariantCulture) });
            }
            if (trayM > 0.01)
            {
                xl.Rows.Add(new List<string> { "Cable Trays / حوامل الكابلات", "متر طولي", Math.Round(trayM, 2).ToString(CultureInfo.InvariantCulture), Math.Round(trayM * wasteMult, 2).ToString(CultureInfo.InvariantCulture) });
            }

            Dictionary<string, object> resp = new Dictionary<string, object>
            {
                { "waste_percent", wastePct },
                { "unique_mep_types", counts.Count },
                { "conduit_length_m", Math.Round(conduitM, 2) },
                { "conduit_with_waste_m", Math.Round(conduitM * wasteMult, 2) },
                { "cable_tray_length_m", Math.Round(trayM, 2) },
                { "cable_tray_with_waste_m", Math.Round(trayM * wasteMult, 2) },
                { "rooms_count", rooms.Count },
                { "total_rooms_area_m2", Math.Round(totalAreaM2, 2) },
                { "items", boqItems }
            };
            if (GetBool(args, "export_excel", false))
            {
                string docRef = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName;
                string reqPath = GetStr(args, "excel_path");
                string outPath = !string.IsNullOrWhiteSpace(reqPath) ? reqPath : RevitExcelExporter.ResolveDefaultPath(docRef, "revit_mep_boq", ".xlsx");
                resp["excel_exported"] = true;
                resp["excel_path"] = RevitExcelExporter.WriteXlsx(outPath, xl);
            }
            return Ok(resp);
        }

        private static Dictionary<string, object> ExportToExcel(Document doc, Dictionary<string, object> args)
        {
            string schedName = GetStr(args, "schedule_name");
            RevitExcelSheet sheet = new RevitExcelSheet();

            if (!string.IsNullOrWhiteSpace(schedName))
            {
                ViewSchedule vs = null;
                foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)))
                {
                    ViewSchedule cand = el as ViewSchedule;
                    if (cand != null && cand.Name.IndexOf(schedName, StringComparison.OrdinalIgnoreCase) >= 0) { vs = cand; break; }
                }
                if (vs == null) return Err("Schedule '" + schedName + "' not found in active Revit document.");
                TableData td = vs.GetTableData();
                TableSectionData body = td.GetSectionData(SectionType.Body);
                sheet.Title = vs.Name;
                sheet.SheetName = vs.Name;
                for (int r = 0; r < body.NumberOfRows; r++)
                {
                    List<string> row = new List<string>();
                    for (int c = 0; c < body.NumberOfColumns; c++)
                        row.Add(vs.GetCellText(SectionType.Body, r, c) ?? "");
                    if (r == 0) sheet.Headers = row;
                    else sheet.Rows.Add(row);
                }
            }
            else
            {
                return QuantityTakeoffMep(doc, new Dictionary<string, object> { { "export_excel", true }, { "excel_path", GetStr(args, "excel_path") } });
            }

            string docRef = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName;
            string reqXlPath = GetStr(args, "excel_path");
            string path = !string.IsNullOrWhiteSpace(reqXlPath) ? reqXlPath : RevitExcelExporter.ResolveDefaultPath(docRef, sheet.SheetName, ".xlsx");
            string written = RevitExcelExporter.WriteXlsx(path, sheet);
            return Ok(new Dictionary<string, object> { { "excel_path", written }, { "rows_exported", sheet.Rows.Count } });
        }

        private static Dictionary<string, object> ApproveOrRollbackPreview(Document doc, View activeView, Dictionary<string, object> args)
        {
            string action = (GetStr(args, "action") ?? "approve").Trim().ToLowerInvariant();
            if (action == "rollback" || action == "cancel" || action == "undo")
                return UndoLastAction(doc, activeView, new Dictionary<string, object> { { "steps", 1 } });

            List<RevitUndoRecord> pending = new List<RevitUndoRecord>();
            int totalIds = 0;
            lock (_undoLock)
            {
                List<RevitUndoRecord> list;
                if (_undoByDoc.TryGetValue(doc, out list))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].HasRedPreview)
                        {
                            pending.Add(list[i]);
                            for (int k = 0; k < list[i].CreatedIds.Count; k++)
                            {
                                if (doc.GetElement(list[i].CreatedIds[k]) != null)
                                    totalIds++;
                            }
                        }
                    }
                }
            }
            if (pending.Count == 0 || totalIds == 0)
            {
                lock (_undoLock)
                {
                    for (int i = 0; i < pending.Count; i++) pending[i].HasRedPreview = false;
                }
                return Ok(new Dictionary<string, object> { { "approved_count", 0 }, { "message", "No pending red preview elements to approve." } });
            }

            using (Transaction tr = new Transaction(doc, "AI-Engineer: Approve Preview"))
            {
                tr.Start();
                for (int i = 0; i < pending.Count; i++)
                {
                    RevitUndoRecord rec = pending[i];
                    View origView = RevitCompat.IsValidId(rec.PreviewViewId) ? (doc.GetElement(rec.PreviewViewId) as View) : null;
                    if (origView != null) ClearPreviewOverrides(origView, rec.CreatedIds);
                    if (activeView != null && (origView == null || RevitCompat.GetIdValue(origView.Id) != RevitCompat.GetIdValue(activeView.Id)))
                        ClearPreviewOverrides(activeView, rec.CreatedIds);
                }
                tr.Commit();
            }
            lock (_undoLock)
            {
                for (int i = 0; i < pending.Count; i++)
                    pending[i].HasRedPreview = false;
            }
            return Ok(new Dictionary<string, object> { { "approved_count", totalIds } });
        }

        private static Dictionary<string, object> UndoLastAction(Document doc, View activeView, Dictionary<string, object> args)
        {
            string action = (GetStr(args, "action") ?? "undo").Trim().ToLowerInvariant();
            List<RevitUndoRecord> snap = new List<RevitUndoRecord>();
            lock (_undoLock)
            {
                List<RevitUndoRecord> list;
                if (_undoByDoc.TryGetValue(doc, out list)) snap.AddRange(list);
            }
            if (action == "list_history" || action == "history")
            {
                List<object> hist = new List<object>();
                for (int i = snap.Count - 1; i >= 0; i--)
                    hist.Add(new Dictionary<string, object> { { "action_id", snap[i].ActionId }, { "tool", snap[i].ToolName }, { "summary", snap[i].Summary } });
                return Ok(new Dictionary<string, object> { { "history_count", hist.Count }, { "history", hist } });
            }
            if (snap.Count == 0) return Err("No AI-Engineer actions recorded in this session to undo (use Revit Ctrl+Z for earlier changes).");

            int steps = Math.Max(1, Math.Min(snap.Count, GetInt(args, "steps", 1)));
            int deletedCount = 0, restoredParams = 0;
            using (Transaction tr = new Transaction(doc, "AI-Engineer: Undo " + steps + " step(s)"))
            {
                tr.Start();
                for (int s = 0; s < steps; s++)
                {
                    RevitUndoRecord rec = snap[snap.Count - 1 - s];
                    if (rec.CreatedIds.Count > 0)
                    {
                        List<ElementId> valid = new List<ElementId>();
                        for (int i = 0; i < rec.CreatedIds.Count; i++)
                            if (doc.GetElement(rec.CreatedIds[i]) != null) valid.Add(rec.CreatedIds[i]);
                        if (valid.Count > 0)
                        {
                            ICollection<ElementId> delRes = doc.Delete(valid);
                            deletedCount += (delRes != null ? delRes.Count : valid.Count);
                        }
                    }
                    for (int i = rec.ModifiedParams.Count - 1; i >= 0; i--)
                    {
                        ParamBackup pb = rec.ModifiedParams[i];
                        string dummyOld, dummyErr;
                        // Pass convertMetricAlias=false because OldValue is already in raw Revit internal units!
                        if (RevitCompat.SetParameterValue(doc.GetElement(pb.Id), pb.ParamName, pb.OldValue, false, out dummyOld, out dummyErr))
                            restoredParams++;
                    }
                }
                tr.Commit();
            }

            lock (_undoLock)
            {
                List<RevitUndoRecord> list;
                if (_undoByDoc.TryGetValue(doc, out list) && list.Count >= steps)
                    list.RemoveRange(list.Count - steps, steps);
            }
            return Ok(new Dictionary<string, object>
            {
                { "undone_steps", steps },
                { "deleted_created_elements", deletedCount },
                { "restored_parameters", restoredParams },
                { "remaining_undo_steps", GetUndoCount(doc) }
            });
        }

        // ==================== Shared Helpers ====================

        private static double SumLinearCategoryM(Document doc, BuiltInCategory bic)
        {
            double feet = 0.0;
            foreach (Element el in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                double len = RevitCompat.ReadParamDouble(el, BuiltInParameter.CURVE_ELEM_LENGTH, 0.0);
                if (len <= 0.0)
                {
                    LocationCurve lc = el.Location as LocationCurve;
                    if (lc != null && lc.Curve != null) len = lc.Curve.Length;
                }
                feet += Math.Max(0.0, len);
            }
            return RevitCompat.FeetToMeters(feet);
        }

        private static bool TryResolveCategory(string name, out BuiltInCategory bic)
        {
            switch (name)
            {
                case "lighting": case "lighting_fixtures": bic = BuiltInCategory.OST_LightingFixtures; return true;
                case "outlets": case "sockets": case "electrical_fixtures": bic = BuiltInCategory.OST_ElectricalFixtures; return true;
                case "panels": case "electrical_equipment": bic = BuiltInCategory.OST_ElectricalEquipment; return true;
                case "circuits": case "electrical_circuits": bic = BuiltInCategory.OST_ElectricalCircuit; return true;
                case "conduits": bic = BuiltInCategory.OST_Conduit; return true;
                case "cable_trays": bic = BuiltInCategory.OST_CableTray; return true;
                case "wires": bic = BuiltInCategory.OST_Wire; return true;
                case "rooms": bic = BuiltInCategory.OST_Rooms; return true;
                case "spaces": bic = BuiltInCategory.OST_MEPSpaces; return true;
                case "walls": bic = BuiltInCategory.OST_Walls; return true;
                case "ceilings": bic = BuiltInCategory.OST_Ceilings; return true;
                default: bic = BuiltInCategory.INVALID; return false;
            }
        }

        internal static FamilySymbol FindLoadedFamilySymbol(Document doc, BuiltInCategory bic, string query)
        {
            FilteredElementCollector col = new FilteredElementCollector(doc).OfCategory(bic).OfClass(typeof(FamilySymbol));
            FamilySymbol first = null;
            string q = (query ?? "").Trim();
            foreach (Element el in col)
            {
                FamilySymbol fs = el as FamilySymbol;
                if (fs == null) continue;
                if (first == null) first = fs;
                if (string.IsNullOrEmpty(q)) return fs;
                string full = fs.FamilyName + " : " + fs.Name;
                if (string.Equals(fs.Name, q, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(full, q, StringComparison.OrdinalIgnoreCase) ||
                    full.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    return fs;
            }
            return string.IsNullOrEmpty(q) ? first : null;
        }

        internal static List<string> ListLoadedFamilyTypeNames(Document doc, BuiltInCategory bic, int max)
        {
            List<string> list = new List<string>();
            foreach (Element el in new FilteredElementCollector(doc).OfCategory(bic).OfClass(typeof(FamilySymbol)))
            {
                FamilySymbol fs = el as FamilySymbol;
                if (fs == null) continue;
                list.Add(fs.FamilyName + " : " + fs.Name);
                if (list.Count >= max) break;
            }
            return list;
        }

        internal static Dictionary<string, Level> IndexLevelsByName(Document doc)
        {
            Dictionary<string, Level> map = new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);
            foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(Level)))
            {
                Level lv = el as Level;
                if (lv != null && !map.ContainsKey(lv.Name)) map[lv.Name] = lv;
            }
            return map;
        }

        internal static Level GetDefaultLevel(Document doc, View activeView)
        {
            if (activeView != null && activeView.GenLevel != null) return activeView.GenLevel;
            foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(Level)))
            {
                Level lv = el as Level;
                if (lv != null) return lv;
            }
            return null;
        }

        internal static Level ResolveLevelForRoomOrElevation(Dictionary<string, Level> map, string roomLevelName, double roomElevM, Level fallback)
        {
            if (map != null && map.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(roomLevelName))
                {
                    Level exact;
                    if (map.TryGetValue(roomLevelName, out exact)) return exact;
                    foreach (KeyValuePair<string, Level> kv in map)
                        if (kv.Key.IndexOf(roomLevelName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            roomLevelName.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                            return kv.Value;
                }
                // Closest host level by elevation (handles Linked Arch models with different level naming!)
                Level closest = null;
                double bestDiff = double.MaxValue;
                foreach (KeyValuePair<string, Level> kv in map)
                {
                    double diff = Math.Abs(RevitCompat.FeetToMeters(kv.Value.Elevation) - roomElevM);
                    if (diff < bestDiff) { bestDiff = diff; closest = kv.Value; }
                }
                if (closest != null && bestDiff <= 1.0) return closest;
            }
            return fallback;
        }

        internal static Level ResolveLevelForRoom(Dictionary<string, Level> map, string roomLevelName, Level fallback)
        {
            if (!string.IsNullOrWhiteSpace(roomLevelName) && map != null)
            {
                Level exact;
                if (map.TryGetValue(roomLevelName, out exact)) return exact;
                foreach (KeyValuePair<string, Level> kv in map)
                    if (kv.Key.IndexOf(roomLevelName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        roomLevelName.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                        return kv.Value;
            }
            return fallback;
        }

        internal static Level ResolveLevelByNameOrDefault(Document doc, View activeView, string levelName)
        {
            if (string.IsNullOrWhiteSpace(levelName)) return GetDefaultLevel(doc, activeView);
            return ResolveLevelForRoom(IndexLevelsByName(doc), levelName, GetDefaultLevel(doc, activeView));
        }

        private static List<ElementId> ParseElementIds(Document doc, Dictionary<string, object> args, string key)
        {
            List<ElementId> list = new List<ElementId>();
            if (args == null || !args.ContainsKey(key) || args[key] == null) return list;
            IEnumerable seq = args[key] as IEnumerable;
            if (seq == null || args[key] is string) return list;
            foreach (object o in seq)
            {
                try
                {
                    long val = Convert.ToInt64(o, CultureInfo.InvariantCulture);
                    ElementId id = RevitCompat.CreateId(val);
                    if (doc.GetElement(id) != null) list.Add(id);
                }
                catch { }
            }
            return list;
        }

        internal static Dictionary<string, object> Ok(Dictionary<string, object> data)
        {
            data["ok"] = true;
            return data;
        }

        internal static Dictionary<string, object> Err(string message)
        {
            return new Dictionary<string, object> { { "ok", false }, { "error", message ?? "Unknown error." } };
        }

        internal static string GetStr(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v) && v != null) ? v.ToString() : null;
        }

        internal static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { }
            }
            return def;
        }

        internal static long? GetNullableLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { }
            }
            return null;
        }

        internal static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { }
            }
            return def;
        }

        internal static double? GetNullableDouble(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { }
            }
            return null;
        }

        internal static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                if (v is bool) return (bool)v;
                bool parsed;
                if (bool.TryParse(v.ToString(), out parsed)) return parsed;
            }
            return def;
        }
    }
}
