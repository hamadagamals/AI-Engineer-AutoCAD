using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(AcCoreRegressionHarness.RegressionCommands))]

namespace AcCoreRegressionHarness
{
    public class RegressionCommands
    {
        private static void AssertTrue(bool cond, string message)
        {
            if (!cond) throw new InvalidOperationException("ASSERT_FAIL: " + message);
        }

        private static string GetStrVal(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v) && v != null) ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        private static bool GetBoolVal(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v) && v != null) && Convert.ToBoolean(v, CultureInfo.InvariantCulture);
        }

        private static int GetIntVal(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v) && v != null) ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;
        }

        private static double GetDblVal(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v) && v != null) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0.0;
        }

        private static ObjectId EnsureRectBlock(Transaction tr, BlockTable bt, string name, double halfW, double halfH, short colorIndex)
        {
            if (bt.Has(name)) return bt[name];
            bt.UpgradeOpen();
            BlockTableRecord btr = new BlockTableRecord();
            btr.Name = name;
            ObjectId btrId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            Polyline pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(-halfW, -halfH), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(halfW, -halfH), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(halfW, halfH), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(-halfW, halfH), 0, 0, 0);
            pl.Closed = true;
            pl.ColorIndex = colorIndex;
            btr.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            return btrId;
        }

        private static ObjectId EnsureLayer(Transaction tr, LayerTable lt, string name, short colorIndex)
        {
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            LayerTableRecord ltr = new LayerTableRecord();
            ltr.Name = name;
            ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex);
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        private static ObjectId InsertBlock(BlockTableRecord ms, Transaction tr, ObjectId btrId, Point3d pt, string layer)
        {
            BlockReference br = new BlockReference(pt, btrId);
            br.Layer = layer;
            ObjectId id = ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);
            return id;
        }

        private static ObjectId AddLine(BlockTableRecord ms, Transaction tr, Point3d a, Point3d b, string layer, short colorIndex)
        {
            Line ln = new Line(a, b);
            ln.Layer = layer;
            ln.ColorIndex = colorIndex;
            ObjectId id = ms.AppendEntity(ln);
            tr.AddNewlyCreatedDBObject(ln, true);
            return id;
        }

        private static ObjectId AddText(BlockTableRecord ms, Transaction tr, Point3d pt, string text, double height, string layer)
        {
            DBText dt = new DBText();
            dt.Position = pt;
            dt.TextString = text;
            dt.Height = height;
            dt.Layer = layer;
            ObjectId id = ms.AppendEntity(dt);
            tr.AddNewlyCreatedDBObject(dt, true);
            return id;
        }

        [CommandMethod("ACCOREREGRESS", CommandFlags.Modal)]
        public void RunRegressionSuite()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            try
            {
                string dllPath = Environment.GetEnvironmentVariable("AI_ENG_PLUGIN_DLL");
                if (string.IsNullOrWhiteSpace(dllPath))
                    dllPath = Path.GetFullPath(@"D:\program\AI-Engineer-r50\New folder\workspace\.ai-engineer\autocad\context\bridge\AIEngineer.AutoCAD.Context.dll");

                Assembly ctxAsm = Assembly.LoadFrom(dllPath);
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

                // 1. Schema & Intent Router Validation
                Type agentType = ctxAsm.GetType("AIEngineer.AutoCAD.DirectCadAgentSession");
                AssertTrue(agentType != null, "DirectCadAgentSession missing");
                MethodInfo buildSpecs = agentType.GetMethod("BuildToolSpecs", flags);
                List<Dictionary<string, object>> specs = (List<Dictionary<string, object>>)buildSpecs.Invoke(null, null);

                int coreSchemaErrors = 0;
                int newToolDescErrors = 0;
                HashSet<string> strictTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                    "find_blocks_along_path", "trace_all_circuits", "generate_panel_schedule_and_sld", "audit_electrical_drawing", "manage_project_legend_db"
                };

                foreach (Dictionary<string, object> s in specs)
                {
                    string n = GetStrVal(s, "name");
                    string d = GetStrVal(s, "description");
                    if (string.IsNullOrWhiteSpace(n) || string.IsNullOrWhiteSpace(d)) coreSchemaErrors++;
                    Dictionary<string, object> p = s.ContainsKey("parameters") ? s["parameters"] as Dictionary<string, object> : null;
                    if (p == null) { coreSchemaErrors++; continue; }
                    Dictionary<string, object> props = p.ContainsKey("properties") ? p["properties"] as Dictionary<string, object> : null;
                    string[] req = p.ContainsKey("required") ? p["required"] as string[] : null;
                    if (props != null)
                    {
                        foreach (KeyValuePair<string, object> kv in props)
                        {
                            Dictionary<string, object> pv = kv.Value as Dictionary<string, object>;
                            if (pv == null) { coreSchemaErrors++; continue; }
                            if (string.IsNullOrWhiteSpace(GetStrVal(pv, "type"))) coreSchemaErrors++;
                            if (strictTools.Contains(n) && string.IsNullOrWhiteSpace(GetStrVal(pv, "description"))) newToolDescErrors++;
                            if (GetStrVal(pv, "type") == "array" && !pv.ContainsKey("items")) coreSchemaErrors++;
                        }
                    }
                    if (req != null)
                    {
                        for (int ri = 0; ri < req.Length; ri++)
                        {
                            if (props == null || !props.ContainsKey(req[ri])) coreSchemaErrors++;
                        }
                    }
                }
                ed.WriteMessage("\nAUTOCAD_TOOLS=" + specs.Count + "|SCHEMA_ERRORS=" + (coreSchemaErrors + newToolDescErrors) + "\n");
                AssertTrue(specs.Count == 38, "Expected 38 tools, got " + specs.Count);
                AssertTrue(coreSchemaErrors == 0 && newToolDescErrors == 0, "Schema errors found");

                MethodInfo selectSpecs = agentType.GetMethod("SelectRelevantToolSpecs", flags);
                List<string> rCirc = new List<string>();
                foreach (Dictionary<string, object> rs in (List<Dictionary<string, object>>)selectSpecs.Invoke(null, new object[] { "\u062a\u062a\u0628\u0639 \u0645\u0633\u0627\u0631 \u0627\u0644\u0644\u064a\u0646\u064a\u0627 \u0648\u0627\u0633\u062a\u062e\u0631\u062c \u0639\u062f\u062f \u0627\u0644\u0628\u0644\u0648\u0643\u0627\u062a" }))
                    rCirc.Add(GetStrVal(rs, "name"));
                List<string> rPanel = new List<string>();
                foreach (Dictionary<string, object> rs in (List<Dictionary<string, object>>)selectSpecs.Invoke(null, new object[] { "\u0627\u0639\u0645\u0644 \u062c\u062f\u0648\u0644 \u0627\u0644\u0644\u0648\u062d\u0629 \u0648\u0627\u0644\u0645\u062e\u0637\u0637 \u0627\u0644\u0627\u062d\u0627\u062f\u064a SLD \u0645\u0639 \u062a\u0648\u0632\u064a\u0639 \u0627\u0644\u0641\u0627\u0632\u0627\u062a" }))
                    rPanel.Add(GetStrVal(rs, "name"));
                List<string> rQa = new List<string>();
                foreach (Dictionary<string, object> rs in (List<Dictionary<string, object>>)selectSpecs.Invoke(null, new object[] { "\u0627\u0639\u0645\u0644 \u0641\u062d\u0635 \u0641\u0646\u064a \u0643\u0647\u0631\u0628\u0627\u0626\u064a \u0644\u0644\u0645\u062e\u0637\u0637 \u0648\u0643\u0634\u0641 \u0627\u0644\u0643\u0634\u0627\u0641\u0627\u062a \u0627\u0644\u063a\u064a\u0631 \u0645\u0648\u0635\u0644\u0629 \u0648\u0647\u0628\u0648\u0637 \u0627\u0644\u062c\u0647\u062f" }))
                    rQa.Add(GetStrVal(rs, "name"));

                ed.WriteMessage("\nROUTER_COUNTS=" + rCirc.Count + "," + rPanel.Count + "," + rQa.Count + "\n");
                AssertTrue(rCirc.Contains("find_blocks_along_path") && rCirc.Contains("trace_all_circuits") && rCirc.Contains("manage_project_legend_db"), "Circuit router missing tools");
                AssertTrue(rPanel.Contains("generate_panel_schedule_and_sld") && rPanel.Contains("calculate_lv_electrical"), "Panel router missing tools");
                AssertTrue(rQa.Contains("audit_electrical_drawing"), "QA router missing tool");

                // 2. JIT Method Preparation
                Type execType = ctxAsm.GetType("AIEngineer.AutoCAD.CadToolExecutor");
                AssertTrue(execType != null, "CadToolExecutor missing");
                string[] jitMethods = new string[] {
                    "ResolveEffectiveUnitsPerMeter", "ExpandConnectedPathCurves", "ExpandConnectedPathCurvesCore",
                    "FindBlocksAlongPath", "ExecuteCircuitTracePass", "FinalizeCircuitPathOutput",
                    "TraceAllCircuits", "ScanAllCircuitsInternal", "ComputeDrawingFixtureMetrics",
                    "CollectAllCircuitCandidateCurveIds", "TraceSingleCircuitComponentCached", "BuildCircuitScheduleSheet",
                    "GeneratePanelScheduleAndSld", "AuditElectricalDrawing", "DrawSingleLineDiagram", "BuildBalancedPanelSchedule",
                    "ManageProjectLegendDb"
                };
                foreach (string jm in jitMethods)
                {
                    MethodInfo mi = execType.GetMethod(jm, flags);
                    AssertTrue(mi != null, "Missing JIT method " + jm);
                    RuntimeHelpers.PrepareMethod(mi.MethodHandle);
                }
                ed.WriteMessage("\nJIT_PHASE2_OK\n");

                MethodInfo execTool = execType.GetMethod("ExecuteTool", flags);
                JavaScriptSerializer jss = new JavaScriptSerializer();

                // 3. Test 1: Meter-with-Inches Circuit Tracing + learn_sample
                ObjectId c1WireSeedId;
                List<ObjectId> c2SampleIds = new List<ObjectId>();
                ObjectId f1Btr, f1eBtr, arrowBtr;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    db.Insunits = UnitsValue.Inches;
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    EnsureLayer(tr, lt, "E-LIGHTING", 6);
                    EnsureLayer(tr, lt, "E-FIXTURES", 7);

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    f1Btr = EnsureRectBlock(tr, bt, "F1", 0.30, 0.10, 2);
                    f1eBtr = EnsureRectBlock(tr, bt, "F1E", 0.30, 0.10, 1);
                    arrowBtr = EnsureRectBlock(tr, bt, "ARROW", 0.08, 0.08, 4);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Circuit 1 (LP-B3.2/1, 3 F1)
                    InsertBlock(ms, tr, f1Btr, new Point3d(2.0, 3.0, 0), "E-FIXTURES");
                    InsertBlock(ms, tr, f1Btr, new Point3d(5.0, 3.0, 0), "E-FIXTURES");
                    InsertBlock(ms, tr, f1Btr, new Point3d(8.0, 3.0, 0), "E-FIXTURES");
                    InsertBlock(ms, tr, arrowBtr, new Point3d(10.5, 3.0, 0), "E-LIGHTING");
                    AddText(ms, tr, new Point3d(10.7, 3.15, 0), "LP-B3.2/1", 0.18, "E-LIGHTING");
                    c1WireSeedId = AddLine(ms, tr, new Point3d(2.30, 3.0, 0), new Point3d(4.55, 3.0, 0), "E-LIGHTING", 6);
                    AddLine(ms, tr, new Point3d(5.45, 3.0, 0), new Point3d(6.30, 3.0, 0), "E-LIGHTING", 6);
                    AddLine(ms, tr, new Point3d(6.50, 2.50, 0), new Point3d(6.50, 3.50, 0), "0", 3);
                    AddLine(ms, tr, new Point3d(6.70, 3.0, 0), new Point3d(7.55, 3.0, 0), "E-LIGHTING", 6);
                    AddLine(ms, tr, new Point3d(8.30, 3.0, 0), new Point3d(10.50, 3.0, 0), "E-LIGHTING", 6);

                    // Circuit 2 (LP-B3.2/2, 4 F1)
                    c2SampleIds.Add(InsertBlock(ms, tr, f1Btr, new Point3d(20.0, 3.0, 0), "E-FIXTURES"));
                    c2SampleIds.Add(InsertBlock(ms, tr, f1Btr, new Point3d(23.0, 3.0, 0), "E-FIXTURES"));
                    c2SampleIds.Add(InsertBlock(ms, tr, f1Btr, new Point3d(26.0, 3.0, 0), "E-FIXTURES"));
                    InsertBlock(ms, tr, f1Btr, new Point3d(29.0, 3.0, 0), "E-FIXTURES");
                    InsertBlock(ms, tr, arrowBtr, new Point3d(31.5, 3.0, 0), "E-LIGHTING");
                    AddText(ms, tr, new Point3d(31.7, 3.15, 0), "LP-B3.2/2", 0.18, "E-LIGHTING");
                    c2SampleIds.Add(AddLine(ms, tr, new Point3d(20.30, 3.0, 0), new Point3d(22.70, 3.0, 0), "E-LIGHTING", 6));
                    c2SampleIds.Add(AddLine(ms, tr, new Point3d(23.30, 3.0, 0), new Point3d(25.70, 3.0, 0), "E-LIGHTING", 6));
                    AddLine(ms, tr, new Point3d(26.30, 3.0, 0), new Point3d(28.70, 3.0, 0), "E-LIGHTING", 6);
                    AddLine(ms, tr, new Point3d(29.30, 3.0, 0), new Point3d(31.50, 3.0, 0), "E-LIGHTING", 6);

                    // Circuit 3 (LP-B3.2/3, 2 F1E)
                    InsertBlock(ms, tr, f1eBtr, new Point3d(2.0, 2.0, 0), "E-FIXTURES");
                    InsertBlock(ms, tr, f1eBtr, new Point3d(5.0, 2.0, 0), "E-FIXTURES");
                    InsertBlock(ms, tr, arrowBtr, new Point3d(7.5, 2.0, 0), "E-LIGHTING");
                    AddText(ms, tr, new Point3d(7.7, 2.15, 0), "LP-B3.2/3", 0.18, "E-LIGHTING");
                    AddLine(ms, tr, new Point3d(2.30, 2.0, 0), new Point3d(4.70, 2.0, 0), "E-LIGHTING", 5);
                    AddLine(ms, tr, new Point3d(5.30, 2.0, 0), new Point3d(7.50, 2.0, 0), "E-LIGHTING", 5);

                    tr.Commit();
                }

                ed.SetImpliedSelection(new ObjectId[] { c1WireSeedId });
                Dictionary<string, object> r1a = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "pick_if_empty", false }, { "follow_connected", true }, { "draw_legend", false }, { "select_matches", false } }
                });
                ed.WriteMessage("\nTEST1A=" + jss.Serialize(r1a) + "\n");
                AssertTrue(GetBoolVal(r1a, "ok"), "Test 1A failed");
                AssertTrue(GetBoolVal(r1a, "unit_scale_inferred") && Math.Abs(GetDblVal(r1a, "effective_units_per_m") - 1.0) < 1e-3, "Scale inference failed");
                AssertTrue(GetStrVal(r1a, "circuit_name") == "LP-B3.2/1", "Circuit 1 tag mismatch");
                AssertTrue(GetIntVal(r1a, "matched_blocks_count") == 3, "Circuit 1 expected 3 F1 blocks");
                AssertTrue(Math.Abs(GetDblVal(r1a, "total_path_length_m") - 6.15) < 0.25, "Circuit 1 length mismatch");

                ed.SetImpliedSelection(c2SampleIds.ToArray());
                Dictionary<string, object> r1b = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "pick_if_empty", false }, { "learn_sample", true }, { "draw_legend", false }, { "select_matches", false } }
                });
                AssertTrue(GetBoolVal(r1b, "ok") && GetBoolVal(r1b, "learned_sample") && !GetBoolVal(r1b, "chain_followed") && GetIntVal(r1b, "matched_blocks_count") == 3, "Test 1B sample lock failed");

                // Clear learned sample so ACI:5 Circuit 3 is unrestricted too, then run TraceAllCircuits
                execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "handles", new string[] { c1WireSeedId.Handle.ToString() } }, { "reset_sample", true }, { "draw_legend", false }, { "select_blocks", false } }
                });
                ed.SetImpliedSelection(new ObjectId[0]);
                Dictionary<string, object> r1c = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "trace_all_circuits",
                    new Dictionary<string, object> { { "draw_table", true }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST1C=" + jss.Serialize(r1c) + "\n");
                AssertTrue(GetBoolVal(r1c, "ok") && GetIntVal(r1c, "circuits_count") == 3 && GetIntVal(r1c, "total_blocks_count") == 9 && GetBoolVal(r1c, "table_drawn"), "Test 1C failed");

                // 4. Test 2: GeneratePanelScheduleAndSld + Excel + Undo
                string panelXl = Path.Combine(Path.GetTempPath(), "phase2_panel_test.xlsx");
                if (File.Exists(panelXl)) File.Delete(panelXl);
                Dictionary<string, object> r2 = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "generate_panel_schedule_and_sld",
                    new Dictionary<string, object> { { "draw_panel_schedule", true }, { "draw_sld", true }, { "export_excel", true }, { "excel_path", panelXl }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST2=" + jss.Serialize(r2) + "\n");
                AssertTrue(GetBoolVal(r2, "ok") && GetIntVal(r2, "panels_count") == 1 && GetIntVal(r2, "circuits_count") == 3, "Test 2 counts failed");
                AssertTrue(GetBoolVal(r2, "panel_schedule_drawn") && GetBoolVal(r2, "sld_drawn"), "Test 2 draw flags failed");
                AssertTrue(GetStrVal(r2, "panel_schedule_layer") == "_AI_PANEL_SCHEDULE" && GetStrVal(r2, "sld_layer") == "_AI_SLD", "Test 2 layers failed");
                AssertTrue(GetIntVal(r2, "sld_created_count") > 15 && GetIntVal(r2, "schedule_created_count") > 15, "Test 2 created counts too low");
                AssertTrue(GetBoolVal(r2, "excel_exported") && File.Exists(panelXl), "Test 2 Excel missing");
                ArrayList pArr = r2["panels"] as ArrayList;
                if (pArr == null) { List<object> pList = r2["panels"] as List<object>; pArr = pList != null ? new ArrayList(pList) : null; }
                AssertTrue(pArr != null && pArr.Count == 1, "Test 2 panels payload missing");
                Dictionary<string, object> p0 = (Dictionary<string, object>)pArr[0];
                AssertTrue(Math.Abs(GetDblVal(p0, "phase_r_va") - 160.0) < 0.1 &&
                           Math.Abs(GetDblVal(p0, "phase_y_va") - 120.0) < 0.1 &&
                           Math.Abs(GetDblVal(p0, "phase_b_va") - 80.0) < 0.1, "Test 2 LPT R/Y/B balancing mismatch");

                Dictionary<string, object> rUndo = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "undo_last_action", new Dictionary<string, object> { { "steps", 1 } }
                });
                ed.WriteMessage("\nTEST2_UNDO=" + jss.Serialize(rUndo) + "\n");
                AssertTrue(GetBoolVal(rUndo, "ok") && GetIntVal(rUndo, "undone_steps") >= 1, "Test 2 undo failed: " + jss.Serialize(rUndo));

                // 4b. Test 5: Verified Crossing-Bridge Jump (1.40m across orthogonal Green ACI:3 obstacle)
                ObjectId crossSeedId;
                List<ObjectId> crossTempIds = new List<ObjectId>();
                using (Transaction trCross = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)trCross.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)trCross.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    crossTempIds.Add(InsertBlock(ms, trCross, f1Btr, new Point3d(2.0, -15.0, 0), "E-FIXTURES"));
                    crossTempIds.Add(InsertBlock(ms, trCross, f1Btr, new Point3d(6.5, -15.0, 0), "E-FIXTURES"));
                    crossTempIds.Add(InsertBlock(ms, trCross, f1Btr, new Point3d(9.5, -15.0, 0), "E-FIXTURES"));
                    crossSeedId = AddLine(ms, trCross, new Point3d(2.30, -15.0, 0), new Point3d(3.50, -15.0, 0), "E-LIGHTING", 6);
                    crossTempIds.Add(crossSeedId);
                    // 1.40m crossing gap [3.50 .. 4.90] across orthogonal Green ACI:3 wire at x=4.20
                    crossTempIds.Add(AddLine(ms, trCross, new Point3d(4.20, -16.2, 0), new Point3d(4.20, -13.8, 0), "0", 3));
                    crossTempIds.Add(AddLine(ms, trCross, new Point3d(4.90, -15.0, 0), new Point3d(6.20, -15.0, 0), "E-LIGHTING", 6));
                    crossTempIds.Add(AddLine(ms, trCross, new Point3d(6.80, -15.0, 0), new Point3d(9.20, -15.0, 0), "E-LIGHTING", 6));
                    crossTempIds.Add(InsertBlock(ms, trCross, arrowBtr, new Point3d(11.0, -15.0, 0), "E-LIGHTING"));
                    crossTempIds.Add(AddLine(ms, trCross, new Point3d(9.80, -15.0, 0), new Point3d(11.0, -15.0, 0), "E-LIGHTING", 6));
                    crossTempIds.Add(AddText(ms, trCross, new Point3d(11.2, -14.85, 0), "LP-CROSS/1", 0.18, "E-LIGHTING"));
                    trCross.Commit();
                }
                ed.SetImpliedSelection(new ObjectId[] { crossSeedId });
                Dictionary<string, object> r5 = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "pick_if_empty", false }, { "draw_legend", false }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST5=" + jss.Serialize(r5) + "\n");
                AssertTrue(GetBoolVal(r5, "ok") && GetStrVal(r5, "circuit_name") == "LP-CROSS/1" && GetIntVal(r5, "matched_blocks_count") == 3, "Test 5 crossing-bridge jump failed");

                // Clean up temporary crossing circuit before Test 3 audit
                using (Transaction trDelCross = db.TransactionManager.StartTransaction())
                {
                    for (int di = 0; di < crossTempIds.Count; di++)
                    {
                        Entity de = trDelCross.GetObject(crossTempIds[di], OpenMode.ForWrite, false, true) as Entity;
                        if (de != null && !de.IsErased) de.Erase(true);
                    }
                    trDelCross.Commit();
                }

                // 4c. Test 6: Circuit Memory Recall + AI-Table Selection Guard + Merge + Lock
                ObjectId schedSampleId = ObjectId.Null;
                using (Transaction trFindTbl = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord msR = (BlockTableRecord)trFindTbl.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    foreach (ObjectId oid in msR)
                    {
                        Entity ent = trFindTbl.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent != null && !ent.IsErased && string.Equals(ent.Layer, "_AI_CIRCUIT_SCHEDULE", StringComparison.OrdinalIgnoreCase) && ent is Polyline)
                        {
                            schedSampleId = oid;
                            break;
                        }
                    }
                    trFindTbl.Commit();
                }
                AssertTrue(!schedSampleId.IsNull, "Test 6 could not locate _AI_CIRCUIT_SCHEDULE entity");
                ed.SetImpliedSelection(new ObjectId[] { schedSampleId });
                Dictionary<string, object> r6a = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "circuit_name", "LP-B3.2/2" }, { "select_blocks", true }, { "select_paths", true }, { "draw_legend", false }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST6A=" + jss.Serialize(r6a) + "\n");
                AssertTrue(GetBoolVal(r6a, "ok") && GetBoolVal(r6a, "from_saved_memory") && GetIntVal(r6a, "matched_blocks_count") == 4 &&
                           GetBoolVal(r6a, "selected_paths") && GetBoolVal(r6a, "selected_blocks"), "Test 6A saved memory recall failed");

                // Add extra F1 + wire and merge onto LP-B3.2/2
                ObjectId extraF1Id, extraWireId;
                using (Transaction trM = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)trM.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)trM.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    extraF1Id = InsertBlock(ms, trM, f1Btr, new Point3d(34.0, 6.0, 0), "E-FIXTURES");
                    extraWireId = AddLine(ms, trM, new Point3d(34.3, 6.0, 0), new Point3d(36.0, 6.0, 0), "E-LIGHTING", 6);
                    trM.Commit();
                }
                ed.SetImpliedSelection(new ObjectId[] { extraF1Id, extraWireId });
                Dictionary<string, object> r6b = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "circuit_name", "LP-B3.2/2" }, { "merge_with_saved", true }, { "draw_legend", false }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST6B=" + jss.Serialize(r6b) + "\n");
                AssertTrue(GetBoolVal(r6b, "ok") && GetIntVal(r6b, "matched_blocks_count") == 5 && GetBoolVal(r6b, "user_locked") && GetBoolVal(r6b, "merged_with_saved"), "Test 6B merge failed");

                ed.SetImpliedSelection(new ObjectId[0]);
                Dictionary<string, object> r6bRecall = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "circuit_name", "LP-B3.2/2" }, { "draw_legend", false }, { "zoom", false } }
                });
                AssertTrue(GetBoolVal(r6bRecall, "ok") && GetIntVal(r6bRecall, "matched_blocks_count") == 5, "Test 6B merge recall failed");

                // Prune back via lock_selection_as_circuit on 3 F1 + 2 wires (c2SampleIds)
                ed.SetImpliedSelection(c2SampleIds.ToArray());
                Dictionary<string, object> r6c = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "circuit_name", "LP-B3.2/2" }, { "lock_selection_as_circuit", true }, { "draw_legend", false }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST6C=" + jss.Serialize(r6c) + "\n");
                AssertTrue(GetBoolVal(r6c, "ok") && GetIntVal(r6c, "matched_blocks_count") == 3 && GetBoolVal(r6c, "user_locked"), "Test 6C lock failed");

                ed.SetImpliedSelection(new ObjectId[0]);
                Dictionary<string, object> r6cRecall = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "circuit_name", "LP-B3.2/2" }, { "draw_legend", false }, { "zoom", false } }
                });
                AssertTrue(GetBoolVal(r6cRecall, "ok") && GetIntVal(r6cRecall, "matched_blocks_count") == 3, "Test 6C lock recall failed");

                // Cleanup extra block/wire and reset LP-B3.2/2 unlocked state via overwrite_locked before Test 3
                using (Transaction trClean6 = db.TransactionManager.StartTransaction())
                {
                    Entity eF = trClean6.GetObject(extraF1Id, OpenMode.ForWrite, false, true) as Entity;
                    if (eF != null && !eF.IsErased) eF.Erase(true);
                    Entity eW = trClean6.GetObject(extraWireId, OpenMode.ForWrite, false, true) as Entity;
                    if (eW != null && !eW.IsErased) eW.Erase(true);
                    trClean6.Commit();
                }
                execTool.Invoke(null, new object[] {
                    doc, "trace_all_circuits",
                    new Dictionary<string, object> { { "draw_table", true }, { "overwrite_locked", true }, { "zoom", false } }
                });

                // 4d. Test 7: Zero Table Overlap (_AI_CIRCUIT_SCHEDULE vs _AI_BLOCK_LEGEND)
                ed.SetImpliedSelection(new ObjectId[0]);
                Dictionary<string, object> r7 = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "circuit_name", "LP-B3.2/1" }, { "draw_legend", true }, { "zoom", false } }
                });
                AssertTrue(GetBoolVal(r7, "ok") && GetBoolVal(r7, "legend_drawn"), "Test 7 legend draw failed");
                using (Transaction trBox = db.TransactionManager.StartTransaction())
                {
                    Extents3d? schedBox = null;
                    Extents3d? legBox = null;
                    BlockTableRecord msB = (BlockTableRecord)trBox.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    foreach (ObjectId oid in msB)
                    {
                        Entity ent = trBox.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        Extents3d ex;
                        try { ex = ent.GeometricExtents; } catch { continue; }
                        if (string.Equals(ent.Layer, "_AI_CIRCUIT_SCHEDULE", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!schedBox.HasValue) schedBox = ex;
                            else { Extents3d u = schedBox.Value; u.AddExtents(ex); schedBox = u; }
                        }
                        else if (string.Equals(ent.Layer, "_AI_BLOCK_LEGEND", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!legBox.HasValue) legBox = ex;
                            else { Extents3d u = legBox.Value; u.AddExtents(ex); legBox = u; }
                        }
                    }
                    AssertTrue(schedBox.HasValue && legBox.HasValue, "Test 7 missing schedule/legend extents");
                    bool overlaps = !(schedBox.Value.MaxPoint.X < legBox.Value.MinPoint.X - 1e-3 ||
                                      schedBox.Value.MinPoint.X > legBox.Value.MaxPoint.X + 1e-3 ||
                                      schedBox.Value.MaxPoint.Y < legBox.Value.MinPoint.Y - 1e-3 ||
                                      schedBox.Value.MinPoint.Y > legBox.Value.MaxPoint.Y + 1e-3);
                    ed.WriteMessage("\nTEST7_OVERLAP=" + overlaps + "|SCHED_Y=[" + Math.Round(schedBox.Value.MinPoint.Y, 2) + "," + Math.Round(schedBox.Value.MaxPoint.Y, 2) + "]|LEG_Y=[" + Math.Round(legBox.Value.MinPoint.Y, 2) + "," + Math.Round(legBox.Value.MaxPoint.Y, 2) + "]\n");
                    AssertTrue(!overlaps, "Test 7 tables overlapped!");
                    trBox.Commit();
                }

                // 4e. Test 8: Relative Excel Path Redirect out of Program Files
                Dictionary<string, object> r8 = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "circuit_name", "LP-B3.2/1" }, { "draw_legend", true }, { "export_excel", true }, { "excel_path", "rel_no_dir_test.xlsx" }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST8=" + jss.Serialize(r8) + "\n");
                string r8Xl = GetStrVal(r8, "excel_path");
                AssertTrue(GetBoolVal(r8, "ok") && GetBoolVal(r8, "excel_exported") && File.Exists(r8Xl), "Test 8 Excel redirect failed");
                AssertTrue(r8Xl.IndexOf("Program Files", StringComparison.OrdinalIgnoreCase) < 0, "Test 8 wrote into Program Files");
                try { File.Delete(r8Xl); } catch { }

                // 4f. Test 9A: Deterministic fixture lighting LV calculation (defaults 1P 220V PF=0.9)
                Dictionary<string, object> r9a = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "calculate_lv_electrical",
                    new Dictionary<string, object>
                    {
                        { "name", "ELP-B3.2/6" },
                        { "load_type", "lighting" },
                        { "fixture_count", 7 },
                        { "fixture_watt", 40 },
                        { "length_m", 85.31 },
                        { "draw_table", false },
                        { "export_excel", false },
                        { "zoom", false }
                    }
                });
                ed.WriteMessage("\nTEST9A=" + jss.Serialize(r9a) + "\n");
                AssertTrue(GetBoolVal(r9a, "ok"), "Test 9A failed ok");
                ArrayList r9aRes = r9a["results"] as ArrayList;
                if (r9aRes == null) { List<object> rl = r9a["results"] as List<object>; r9aRes = rl != null ? new ArrayList(rl) : null; }
                AssertTrue(r9aRes != null && r9aRes.Count == 1, "Test 9A results missing");
                Dictionary<string, object> c9 = (Dictionary<string, object>)r9aRes[0];
                AssertTrue(GetIntVal(c9, "phases") == 1 && Math.Abs(GetDblVal(c9, "voltage_v") - 220.0) < 0.1, "Test 9A expected 1P 220V default for lighting");
                AssertTrue(GetDblVal(c9, "load_kw") >= 0.279 && GetDblVal(c9, "load_kw") <= 0.281, "Test 9A load_kw mismatch");
                AssertTrue(GetDblVal(c9, "load_kva") >= 0.310 && GetDblVal(c9, "load_kva") <= 0.312, "Test 9A load_kva mismatch");
                AssertTrue(GetDblVal(c9, "design_current_ib_a") >= 1.38 && GetDblVal(c9, "design_current_ib_a") <= 1.44, "Test 9A Ib mismatch");
                AssertTrue(GetDblVal(c9, "voltage_drop_percent") >= 0.80 && GetDblVal(c9, "voltage_drop_percent") <= 2.00, "Test 9A VD% mismatch");

                // 4g. Test 9B: Circuit Block Legend with Integrated Electrical Load & Max VD% Columns + Excel
                Dictionary<string, object> r9b = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object>
                    {
                        { "circuit_name", "LP-B3.2/1" },
                        { "draw_legend", true },
                        { "include_electrical_calc", true },
                        { "fixture_watt", 40 },
                        { "export_excel", true },
                        { "excel_path", "test9_circuit_elec.xlsx" },
                        { "zoom", false }
                    }
                });
                ed.WriteMessage("\nTEST9B=" + jss.Serialize(r9b) + "\n");
                AssertTrue(GetBoolVal(r9b, "ok") && GetBoolVal(r9b, "legend_drawn") && GetBoolVal(r9b, "legend_has_electrical_columns"), "Test 9B legend electrical columns missing");
                Dictionary<string, object> ec9 = r9b["electrical_calc"] as Dictionary<string, object>;
                AssertTrue(ec9 != null && GetIntVal(ec9, "phases") == 1, "Test 9B electrical_calc missing or wrong phases");
                AssertTrue(GetDblVal(ec9, "load_kw") >= 0.119 && GetDblVal(ec9, "load_kw") <= 0.121, "Test 9B electrical_calc load_kw mismatch");
                AssertTrue(GetDblVal(ec9, "voltage_drop_percent") > 0.0 && GetDblVal(ec9, "voltage_drop_percent") <= 3.0, "Test 9B VD% out of range");
                string r9Xl = GetStrVal(r9b, "excel_path");
                AssertTrue(GetBoolVal(r9b, "excel_exported") && File.Exists(r9Xl), "Test 9B Excel missing");
                try { File.Delete(r9Xl); } catch { }

                // 4h. Test 10: Millimeters-tagged Meter Drawing Isolation + Parallel Obstacle Reject + Merge With Block+Wire
                List<ObjectId> t10TempIds = new List<ObjectId>();
                ObjectId c10ASeedId, c10BSeedId, c10BFixId;
                UnitsValue savedInsunits = db.Insunits;
                using (Transaction tr10 = db.TransactionManager.StartTransaction())
                {
                    db.Insunits = UnitsValue.Millimeters;
                    BlockTable bt = (BlockTable)tr10.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr10.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Circuit A (LP-MM/1, 3 F1 at y=-22.0, x=2.0, 6.0, 9.0) with 1.0m orthogonal crossing gap [3.30..4.30]
                    t10TempIds.Add(InsertBlock(ms, tr10, f1Btr, new Point3d(2.0, -22.0, 0), "E-FIXTURES"));
                    t10TempIds.Add(InsertBlock(ms, tr10, f1Btr, new Point3d(6.0, -22.0, 0), "E-FIXTURES"));
                    t10TempIds.Add(InsertBlock(ms, tr10, f1Btr, new Point3d(9.0, -22.0, 0), "E-FIXTURES"));
                    c10ASeedId = AddLine(ms, tr10, new Point3d(2.30, -22.0, 0), new Point3d(3.30, -22.0, 0), "E-LIGHTING", 6);
                    t10TempIds.Add(c10ASeedId);
                    t10TempIds.Add(AddLine(ms, tr10, new Point3d(3.80, -23.2, 0), new Point3d(3.80, -20.8, 0), "0", 3)); // Orthogonal green crossing wire
                    t10TempIds.Add(AddLine(ms, tr10, new Point3d(4.30, -22.0, 0), new Point3d(5.70, -22.0, 0), "E-LIGHTING", 6));
                    t10TempIds.Add(AddLine(ms, tr10, new Point3d(6.30, -22.0, 0), new Point3d(8.70, -22.0, 0), "E-LIGHTING", 6));
                    t10TempIds.Add(AddLine(ms, tr10, new Point3d(9.30, -22.0, 0), new Point3d(11.0, -22.0, 0), "E-LIGHTING", 6));
                    t10TempIds.Add(InsertBlock(ms, tr10, arrowBtr, new Point3d(11.0, -22.0, 0), "E-LIGHTING"));
                    t10TempIds.Add(AddText(ms, tr10, new Point3d(11.2, -21.85, 0), "LP-MM/1", 0.18, "E-LIGHTING"));

                    // Parallel Green ACI:3 line alongside 2.40m gap [-0.70..1.70] separating Circuit B (must NOT trigger a crossing jump)
                    t10TempIds.Add(AddLine(ms, tr10, new Point3d(-1.0, -22.15, 0), new Point3d(2.0, -22.15, 0), "0", 3));

                    // Separate Circuit B (2 F1 at x=-6.5, -3.5) with same E-LIGHTING layer & ACI:6 color across 2.40m gap [-0.70..1.70]
                    t10TempIds.Add(InsertBlock(ms, tr10, f1Btr, new Point3d(-6.5, -22.0, 0), "E-FIXTURES"));
                    c10BFixId = InsertBlock(ms, tr10, f1Btr, new Point3d(-3.5, -22.0, 0), "E-FIXTURES");
                    t10TempIds.Add(c10BFixId);
                    c10BSeedId = AddLine(ms, tr10, new Point3d(-6.20, -22.0, 0), new Point3d(-3.80, -22.0, 0), "E-LIGHTING", 6);
                    t10TempIds.Add(c10BSeedId);
                    t10TempIds.Add(AddLine(ms, tr10, new Point3d(-3.20, -22.0, 0), new Point3d(-0.70, -22.0, 0), "E-LIGHTING", 6));

                    tr10.Commit();
                }

                ed.SetImpliedSelection(new ObjectId[] { c10ASeedId });
                Dictionary<string, object> r10a = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "pick_if_empty", false }, { "draw_legend", false }, { "include_electrical_calc", true }, { "fixture_watt", 40 }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST10A=" + jss.Serialize(r10a) + "\n");
                AssertTrue(GetBoolVal(r10a, "ok"), "Test 10A failed ok");
                AssertTrue(GetBoolVal(r10a, "unit_scale_inferred") && Math.Abs(GetDblVal(r10a, "effective_units_per_m") - 1.0) < 1e-3, "Test 10A Millimeters scale inference failed");
                AssertTrue(GetStrVal(r10a, "circuit_name") == "LP-MM/1" && GetIntVal(r10a, "matched_blocks_count") == 3, "Test 10A leaked into Circuit B or missed orthogonal bridge");
                AssertTrue(GetDblVal(r10a, "total_path_length_m") >= 6.0 && GetDblVal(r10a, "total_path_length_m") <= 10.0, "Test 10A meter length mismatch");
                Dictionary<string, object> ec10a = r10a["electrical_calc"] as Dictionary<string, object>;
                AssertTrue(ec10a != null && GetDblVal(ec10a, "voltage_drop_percent") >= 0.04, "Test 10A VD% collapsed to 0");

                // Test 10B: Merge new branch while BOTH wire AND fixture block are selected
                ed.SetImpliedSelection(new ObjectId[] { c10BSeedId, c10BFixId });
                Dictionary<string, object> r10b = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "circuit_name", "LP-MM/1" }, { "merge_with_saved", true }, { "follow_connected", true }, { "pick_if_empty", false }, { "draw_legend", false }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST10B=" + jss.Serialize(r10b) + "\n");
                AssertTrue(GetBoolVal(r10b, "ok") && GetBoolVal(r10b, "merged_with_saved") && GetIntVal(r10b, "matched_blocks_count") == 5, "Test 10B merge with wire+block selection failed");

                using (Transaction trDel10 = db.TransactionManager.StartTransaction())
                {
                    db.Insunits = savedInsunits;
                    for (int di = 0; di < t10TempIds.Count; di++)
                    {
                        Entity de = trDel10.GetObject(t10TempIds[di], OpenMode.ForWrite, false, true) as Entity;
                        if (de != null && !de.IsErased) de.Erase(true);
                    }
                    trDel10.Commit();
                }

                // 4i. Test 11: Project Legend Spec DB Extraction ("AS ABOVE" inheritance), Mixed Circuit Auto-Lookup, Stacked Panel Board & Saved-Memory Panel Schedule
                List<ObjectId> t11TempIds = new List<ObjectId>();
                ObjectId c11_1SeedId, c11_2SeedId, c11_3SeedId;
                using (Transaction tr11 = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr11.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId b60 = EnsureRectBlock(tr11, bt, "60x60", 0.30, 0.30, 2);
                    ObjectId b60e = EnsureRectBlock(tr11, bt, "60x60E", 0.30, 0.30, 1);
                    ObjectId bApp = EnsureRectBlock(tr11, bt, "APP", 0.20, 0.20, 3);
                    ObjectId bAppe = EnsureRectBlock(tr11, bt, "APPE", 0.20, 0.20, 1);
                    BlockTableRecord ms = (BlockTableRecord)tr11.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Erase existing _AI_BLOCK_LEGEND before starting clean Test 11 board
                    foreach (ObjectId oid in ms)
                    {
                        Entity ee = tr11.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ee != null && !ee.IsErased && string.Equals(ee.Layer ?? "", "_AI_BLOCK_LEGEND", StringComparison.OrdinalIgnoreCase))
                        {
                            ee.UpgradeOpen();
                            ee.Erase(true);
                        }
                    }

                    // 4-row Legend table at x=-40.0
                    t11TempIds.Add(InsertBlock(ms, tr11, b60, new Point3d(-40.0, 20.0, 0), "E-FIXTURES"));
                    t11TempIds.Add(AddText(ms, tr11, new Point3d(-38.5, 20.0, 0), "LED LIGHTING FIXTURE 45 WATT, RECESSED MOUNTED, 230V", 0.22, "E-FIXTURES"));
                    t11TempIds.Add(InsertBlock(ms, tr11, b60e, new Point3d(-40.0, 18.5, 0), "E-FIXTURES"));
                    t11TempIds.Add(AddText(ms, tr11, new Point3d(-38.5, 18.5, 0), "AS ABOVE BUT WITH EMERGENCY BACKUP BATTERY FOR 1.5 Hrs", 0.22, "E-FIXTURES"));
                    t11TempIds.Add(InsertBlock(ms, tr11, bApp, new Point3d(-40.0, 17.0, 0), "E-FIXTURES"));
                    t11TempIds.Add(AddText(ms, tr11, new Point3d(-38.5, 17.0, 0), "WEATHER PROOF LED LIGHTING FIXTURE 22 WATT, WALL MOUNTED 230V, IP65", 0.22, "E-FIXTURES"));
                    t11TempIds.Add(InsertBlock(ms, tr11, bAppe, new Point3d(-40.0, 15.5, 0), "E-FIXTURES"));
                    t11TempIds.Add(AddText(ms, tr11, new Point3d(-38.5, 15.5, 0), "AS ABOVE BUT WITH EMERGENCY BACKUP BATTERY FOR 1.5 Hrs", 0.22, "E-FIXTURES"));

                    // Circuit 1: LP-DB1/1 = 2x 60x60E (45W each) + 1x APPE (22W) = 112W = 0.112 kW
                    t11TempIds.Add(InsertBlock(ms, tr11, b60e, new Point3d(2.0, -30.0, 0), "E-FIXTURES"));
                    t11TempIds.Add(InsertBlock(ms, tr11, b60e, new Point3d(5.0, -30.0, 0), "E-FIXTURES"));
                    t11TempIds.Add(InsertBlock(ms, tr11, bAppe, new Point3d(8.0, -30.0, 0), "E-FIXTURES"));
                    c11_1SeedId = AddLine(ms, tr11, new Point3d(2.30, -30.0, 0), new Point3d(4.70, -30.0, 0), "E-LIGHTING", 4);
                    t11TempIds.Add(c11_1SeedId);
                    t11TempIds.Add(AddLine(ms, tr11, new Point3d(5.30, -30.0, 0), new Point3d(7.80, -30.0, 0), "E-LIGHTING", 4));
                    t11TempIds.Add(AddLine(ms, tr11, new Point3d(8.20, -30.0, 0), new Point3d(10.5, -30.0, 0), "E-LIGHTING", 4));
                    t11TempIds.Add(InsertBlock(ms, tr11, arrowBtr, new Point3d(10.5, -30.0, 0), "E-LIGHTING"));
                    t11TempIds.Add(AddText(ms, tr11, new Point3d(10.7, -29.85, 0), "LP-DB1/1", 0.18, "E-LIGHTING"));

                    // Circuit 2: LP-DB1/2 = 2x 60x60 (45W each) = 90W = 0.090 kW
                    t11TempIds.Add(InsertBlock(ms, tr11, b60, new Point3d(2.0, -33.0, 0), "E-FIXTURES"));
                    t11TempIds.Add(InsertBlock(ms, tr11, b60, new Point3d(5.0, -33.0, 0), "E-FIXTURES"));
                    c11_2SeedId = AddLine(ms, tr11, new Point3d(2.30, -33.0, 0), new Point3d(4.70, -33.0, 0), "E-LIGHTING", 4);
                    t11TempIds.Add(c11_2SeedId);
                    t11TempIds.Add(AddLine(ms, tr11, new Point3d(5.30, -33.0, 0), new Point3d(7.5, -33.0, 0), "E-LIGHTING", 4));
                    t11TempIds.Add(InsertBlock(ms, tr11, arrowBtr, new Point3d(7.5, -33.0, 0), "E-LIGHTING"));
                    t11TempIds.Add(AddText(ms, tr11, new Point3d(7.7, -32.85, 0), "LP-DB1/2", 0.18, "E-LIGHTING"));

                    // Circuit 3: ELP-DB2/1 = 1x APPE (22W) on new panel ELP-DB2
                    t11TempIds.Add(InsertBlock(ms, tr11, bAppe, new Point3d(2.0, -36.0, 0), "E-FIXTURES"));
                    c11_3SeedId = AddLine(ms, tr11, new Point3d(2.20, -36.0, 0), new Point3d(5.0, -36.0, 0), "E-LIGHTING", 4);
                    t11TempIds.Add(c11_3SeedId);
                    t11TempIds.Add(InsertBlock(ms, tr11, arrowBtr, new Point3d(5.0, -36.0, 0), "E-LIGHTING"));
                    t11TempIds.Add(AddText(ms, tr11, new Point3d(5.2, -35.85, 0), "ELP-DB2/1", 0.18, "E-LIGHTING"));

                    tr11.Commit();
                }

                // 11A: Extract Legend into Project DB
                Dictionary<string, object> r11a = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "manage_project_legend_db",
                    new Dictionary<string, object> { { "action", "extract_from_drawing" } }
                });
                ed.WriteMessage("\nTEST11A=" + jss.Serialize(r11a) + "\n");
                AssertTrue(GetBoolVal(r11a, "ok") && GetIntVal(r11a, "items_count") >= 4, "Test 11A expected >=4 extracted items");
                Dictionary<string, Dictionary<string, object>> dbByName = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                ArrayList items11a = r11a["items"] as ArrayList;
                if (items11a == null) { List<object> il = r11a["items"] as List<object>; items11a = il != null ? new ArrayList(il) : null; }
                AssertTrue(items11a != null, "Test 11A items missing");
                foreach (object io in items11a)
                {
                    Dictionary<string, object> idict = io as Dictionary<string, object>;
                    if (idict != null) dbByName[GetStrVal(idict, "block_name")] = idict;
                }
                AssertTrue(dbByName.ContainsKey("60x60") && Math.Abs(GetDblVal(dbByName["60x60"], "watt") - 45.0) < 0.1, "Test 11A 60x60 expected 45W");
                AssertTrue(dbByName.ContainsKey("60x60E") && Math.Abs(GetDblVal(dbByName["60x60E"], "watt") - 45.0) < 0.1 &&
                           GetBoolVal(dbByName["60x60E"], "is_emergency") && string.Equals(GetStrVal(dbByName["60x60E"], "inherited_from"), "60x60", StringComparison.OrdinalIgnoreCase),
                    "Test 11A 60x60E AS ABOVE inheritance failed");
                AssertTrue(dbByName.ContainsKey("APP") && Math.Abs(GetDblVal(dbByName["APP"], "watt") - 22.0) < 0.1, "Test 11A APP expected 22W");
                AssertTrue(dbByName.ContainsKey("APPE") && Math.Abs(GetDblVal(dbByName["APPE"], "watt") - 22.0) < 0.1 &&
                           GetBoolVal(dbByName["APPE"], "is_emergency") && string.Equals(GetStrVal(dbByName["APPE"], "inherited_from"), "APP", StringComparison.OrdinalIgnoreCase),
                    "Test 11A APPE AS ABOVE inheritance failed");

                // 11B: Trace mixed circuit LP-DB1/1 using auto-lookup from Project DB
                ed.SetImpliedSelection(new ObjectId[] { c11_1SeedId });
                Dictionary<string, object> r11b = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "pick_if_empty", false }, { "include_electrical_calc", true }, { "draw_legend", true }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST11B=" + jss.Serialize(r11b) + "\n");
                AssertTrue(GetBoolVal(r11b, "ok") && GetStrVal(r11b, "circuit_name") == "LP-DB1/1" && GetIntVal(r11b, "matched_blocks_count") == 3, "Test 11B trace failed");
                AssertTrue(GetBoolVal(r11b, "legend_drawn") && GetBoolVal(r11b, "legend_has_electrical_columns") && GetBoolVal(r11b, "legend_stacked_by_panel"), "Test 11B stacked legend flags failed");
                Dictionary<string, object> ec11b = r11b["electrical_calc"] as Dictionary<string, object>;
                AssertTrue(ec11b != null && !GetBoolVal(ec11b, "load_assumed") && GetStrVal(ec11b, "load_source") == "project_legend_db", "Test 11B expected non-assumed DB lookup");
                AssertTrue(Math.Abs(GetDblVal(ec11b, "load_kw") - 0.112) < 0.002, "Test 11B expected exact 0.112 kW (2*45 + 22)");
                double[] lb11_1 = r11b["legend_bounds"] as double[];
                AssertTrue(lb11_1 != null && lb11_1.Length == 4, "Test 11B legend_bounds missing");

                // 11C: Stack LP-DB1/2 below LP-DB1/1, ELP-DB2/1 in new column to the right, and verify in-place replacement of LP-DB1/1
                ed.SetImpliedSelection(new ObjectId[] { c11_2SeedId });
                Dictionary<string, object> r11c1 = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "pick_if_empty", false }, { "include_electrical_calc", true }, { "draw_legend", true }, { "zoom", false } }
                });
                AssertTrue(GetBoolVal(r11c1, "ok") && GetStrVal(r11c1, "circuit_name") == "LP-DB1/2", "Test 11C1 LP-DB1/2 failed");
                double[] lb11_2 = r11c1["legend_bounds"] as double[];
                AssertTrue(lb11_2 != null && Math.Abs(lb11_2[0] - lb11_1[0]) < 0.5 && lb11_2[3] < lb11_1[1] - 0.1,
                    "Test 11C expected LP-DB1/2 stacked vertically underneath LP-DB1/1");

                ed.SetImpliedSelection(new ObjectId[] { c11_3SeedId });
                Dictionary<string, object> r11c2 = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "pick_if_empty", false }, { "include_electrical_calc", true }, { "draw_legend", true }, { "zoom", false } }
                });
                AssertTrue(GetBoolVal(r11c2, "ok") && GetStrVal(r11c2, "circuit_name") == "ELP-DB2/1", "Test 11C2 ELP-DB2/1 failed");
                double[] lb11_3 = r11c2["legend_bounds"] as double[];
                AssertTrue(lb11_3 != null && lb11_3[0] > lb11_1[2] + 0.1,
                    "Test 11C expected ELP-DB2/1 in a new panel column strictly to the right");

                ed.SetImpliedSelection(new ObjectId[0]);
                Dictionary<string, object> r11c3 = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object> { { "circuit_name", "LP-DB1/1" }, { "include_electrical_calc", true }, { "draw_legend", true }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST11C=" + jss.Serialize(r11c3) + "\n");
                AssertTrue(GetBoolVal(r11c3, "ok") && GetBoolVal(r11c3, "legend_replaced_in_place"), "Test 11C expected in-place update of LP-DB1/1");
                double[] lb11_re = r11c3["legend_bounds"] as double[];
                AssertTrue(lb11_re != null && Math.Abs(lb11_re[0] - lb11_1[0]) < 0.5 && Math.Abs(lb11_re[3] - lb11_1[3]) < 0.5,
                    "Test 11C in-place refresh drifted position");

                // 11D: Saved-Memory Panel Schedule using Project DB wattages
                Dictionary<string, object> r11d = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "generate_panel_schedule_and_sld",
                    new Dictionary<string, object> { { "source", "saved_memory" }, { "panel_name", "LP-DB1" }, { "draw_panel_schedule", true }, { "draw_sld", true }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST11D=" + jss.Serialize(r11d) + "\n");
                AssertTrue(GetBoolVal(r11d, "ok") && GetIntVal(r11d, "panels_count") == 1 && GetIntVal(r11d, "circuits_count") == 2, "Test 11D saved_memory panel schedule failed");
                ArrayList p11Arr = r11d["panels"] as ArrayList;
                if (p11Arr == null) { List<object> pl = r11d["panels"] as List<object>; p11Arr = pl != null ? new ArrayList(pl) : null; }
                AssertTrue(p11Arr != null && p11Arr.Count == 1, "Test 11D panels payload missing");
                Dictionary<string, object> p11 = (Dictionary<string, object>)p11Arr[0];
                AssertTrue(GetStrVal(p11, "panel_name") == "LP-DB1" && Math.Abs(GetDblVal(p11, "connected_kw") - 0.202) < 0.005,
                    "Test 11D LP-DB1 connected_kw expected ~0.202 kW (112W + 90W)");

                // Clean up temporary Test 11 entities, legend DB, and AI layers before Test 3
                execTool.Invoke(null, new object[] { doc, "manage_project_legend_db", new Dictionary<string, object> { { "action", "clear_catalog" } } });
                using (Transaction trDel11 = db.TransactionManager.StartTransaction())
                {
                    for (int di = 0; di < t11TempIds.Count; di++)
                    {
                        Entity de = trDel11.GetObject(t11TempIds[di], OpenMode.ForWrite, false, true) as Entity;
                        if (de != null && !de.IsErased) de.Erase(true);
                    }
                    BlockTableRecord ms = (BlockTableRecord)trDel11.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    foreach (ObjectId oid in ms)
                    {
                        Entity ee = trDel11.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ee == null || ee.IsErased) continue;
                        string lyr = ee.Layer ?? "";
                        if (string.Equals(lyr, "_AI_BLOCK_LEGEND", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(lyr, "_AI_PANEL_SCHEDULE", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(lyr, "_AI_SLD", StringComparison.OrdinalIgnoreCase))
                        {
                            ee.UpgradeOpen();
                            ee.Erase(true);
                        }
                    }
                    trDel11.Commit();
                }

                // 5. Test 3: AuditElectricalDrawing (Orphan + Duplicate + Overload + VD>3% + Unit Mismatch)
                using (Transaction trQa = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)trQa.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)trQa.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Defect A: Orphan F1 at (15.0, 8.0)
                    InsertBlock(ms, trQa, f1Btr, new Point3d(15.0, 8.0, 0), "E-FIXTURES");

                    // Defect B: Duplicate LP-B3.2/2 at y=-4.0
                    InsertBlock(ms, trQa, f1Btr, new Point3d(2.0, -4.0, 0), "E-FIXTURES");
                    InsertBlock(ms, trQa, arrowBtr, new Point3d(5.0, -4.0, 0), "E-LIGHTING");
                    AddText(ms, trQa, new Point3d(5.2, -3.85, 0), "LP-B3.2/2", 0.18, "E-LIGHTING");
                    AddLine(ms, trQa, new Point3d(2.30, -4.0, 0), new Point3d(5.0, -4.0, 0), "E-LIGHTING", 2);

                    // Defect C: Overloaded & long 120m circuit LP-B3.2/9 with 20 F1 fixtures at y=-8.0
                    for (int k = 0; k < 20; k++)
                    {
                        double xk = 2.0 + k * 6.0;
                        InsertBlock(ms, trQa, f1Btr, new Point3d(xk, -8.0, 0), "E-FIXTURES");
                        if (k < 19)
                            AddLine(ms, trQa, new Point3d(xk + 0.30, -8.0, 0), new Point3d(xk + 5.70, -8.0, 0), "E-LIGHTING", 4);
                    }
                    AddLine(ms, trQa, new Point3d(116.30, -8.0, 0), new Point3d(122.0, -8.0, 0), "E-LIGHTING", 4);
                    InsertBlock(ms, trQa, arrowBtr, new Point3d(122.0, -8.0, 0), "E-LIGHTING");
                    AddText(ms, trQa, new Point3d(122.2, -7.85, 0), "LP-B3.2/9", 0.18, "E-LIGHTING");

                    trQa.Commit();
                }

                string qaXl = Path.Combine(Path.GetTempPath(), "phase2_qa_test.xlsx");
                if (File.Exists(qaXl)) File.Delete(qaXl);
                Dictionary<string, object> r3 = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "audit_electrical_drawing",
                    new Dictionary<string, object> { { "draw_markers", true }, { "draw_report_table", true }, { "export_excel", true }, { "excel_path", qaXl }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST3=" + jss.Serialize(r3) + "\n");
                AssertTrue(GetBoolVal(r3, "ok") && GetStrVal(r3, "overall_status") == "FAIL", "Test 3 expected FAIL status");
                AssertTrue(GetIntVal(r3, "orphan_fixtures_count") >= 1, "Test 3 missed orphan fixture");
                AssertTrue(GetIntVal(r3, "duplicate_names_count") >= 1, "Test 3 missed duplicate circuit name");
                AssertTrue(GetIntVal(r3, "overloaded_circuits_count") >= 1, "Test 3 missed overloaded circuit");
                AssertTrue(GetIntVal(r3, "vd_violations_count") >= 1, "Test 3 missed VD violation");
                AssertTrue(GetBoolVal(r3, "unit_scale_mismatch"), "Test 3 missed unit scale mismatch");
                AssertTrue(GetBoolVal(r3, "markers_drawn") && GetBoolVal(r3, "report_table_drawn") && GetStrVal(r3, "qa_layer") == "_AI_ELECTRICAL_QA", "Test 3 QA graphics missing");
                AssertTrue(GetBoolVal(r3, "excel_exported") && File.Exists(qaXl), "Test 3 Excel missing");

                // 6. Test 4: Spatial Grid Scale Benchmark (30 circuits x 10 F1 = 300 fixtures)
                using (Transaction trClean = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord ms = (BlockTableRecord)trClean.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    foreach (ObjectId oid in ms)
                    {
                        Entity ent = trClean.GetObject(oid, OpenMode.ForWrite) as Entity;
                        if (ent != null && !ent.IsErased) ent.Erase(true);
                    }
                    trClean.Commit();
                }

                using (Transaction trBench = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)trBench.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)trBench.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    for (int ci = 0; ci < 30; ci++)
                    {
                        int row = ci / 5;
                        int col = ci % 5;
                        double baseX = col * 35.0;
                        double baseY = row * 10.0;
                        short colorIdx = (short)((ci % 6) + 1);
                        if (colorIdx == 3) colorIdx = 6;
                        for (int fi = 0; fi < 10; fi++)
                        {
                            double fx = baseX + fi * 2.5;
                            InsertBlock(ms, trBench, f1Btr, new Point3d(fx, baseY, 0), "E-FIXTURES");
                            if (fi < 9)
                                AddLine(ms, trBench, new Point3d(fx + 0.30, baseY, 0), new Point3d(fx + 2.20, baseY, 0), "E-LIGHTING", colorIdx);
                        }
                        double arrowX = baseX + 9 * 2.5 + 2.0;
                        AddLine(ms, trBench, new Point3d(baseX + 9 * 2.5 + 0.30, baseY, 0), new Point3d(arrowX, baseY, 0), "E-LIGHTING", colorIdx);
                        InsertBlock(ms, trBench, arrowBtr, new Point3d(arrowX, baseY, 0), "E-LIGHTING");
                        AddText(ms, trBench, new Point3d(arrowX + 0.2, baseY + 0.15, 0), "LP-BENCH/" + (ci + 1).ToString(CultureInfo.InvariantCulture), 0.18, "E-LIGHTING");
                    }
                    trBench.Commit();
                }

                Stopwatch sw = Stopwatch.StartNew();
                Dictionary<string, object> r4 = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "trace_all_circuits",
                    new Dictionary<string, object> { { "draw_table", false }, { "zoom", false } }
                });
                sw.Stop();
                ed.WriteMessage("\nBENCHMARK_30C_300F_MS=" + sw.ElapsedMilliseconds + "|CIRCUITS=" + GetIntVal(r4, "circuits_count") + "|BLOCKS=" + GetIntVal(r4, "total_blocks_count") + "\n");
                AssertTrue(GetBoolVal(r4, "ok") && GetIntVal(r4, "circuits_count") == 30 && GetIntVal(r4, "total_blocks_count") == 300, "Benchmark count mismatch");
                AssertTrue(sw.ElapsedMilliseconds < 10000, "Benchmark exceeded 10s: " + sw.ElapsedMilliseconds);

                try { if (File.Exists(panelXl)) File.Delete(panelXl); } catch { }
                try { if (File.Exists(qaXl)) File.Delete(qaXl); } catch { }

                ed.WriteMessage("\nACCORE_REGRESSION_PASS\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nACCORE_REGRESSION_FAIL:" + ex.ToString() + "\n");
            }
        }
    }
}
