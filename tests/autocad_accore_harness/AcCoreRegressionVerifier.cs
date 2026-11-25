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
                    "find_blocks_along_path", "trace_all_circuits", "generate_panel_schedule_and_sld", "audit_electrical_drawing", "manage_project_legend_db", "guided_circuit_inventory", "capture_visual_view", "design_panel_manufacturing_layout", "build_distribution_network_and_sld"
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
                AssertTrue(specs.Count == 42, "Expected 42 tools, got " + specs.Count);
                AssertTrue(coreSchemaErrors == 0 && newToolDescErrors == 0, "Schema errors found");

                MethodInfo selectSpecs = agentType.GetMethod("SelectRelevantToolSpecs", flags);
                List<string> rCirc = new List<string>();
                foreach (Dictionary<string, object> rs in (List<Dictionary<string, object>>)selectSpecs.Invoke(null, new object[] { "\u062a\u062a\u0628\u0639 \u0645\u0633\u0627\u0631 \u0627\u0644\u0644\u064a\u0646\u064a\u0627 \u0648\u0627\u0633\u062e\u0631\u062c \u0639\u062f\u062f \u0627\u0644\u0628\u0644\u0648\u0643\u0627\u062a" }))
                    rCirc.Add(GetStrVal(rs, "name"));
                List<string> rPanel = new List<string>();
                foreach (Dictionary<string, object> rs in (List<Dictionary<string, object>>)selectSpecs.Invoke(null, new object[] { "\u0627\u0639\u0645\u0644 \u062c\u062f\u0648\u0644 \u0627\u0644\u0644\u0648\u062d\u0629 \u0648\u0627\u0644\u0645\u062e\u0637\u0637 \u0627\u0644\u0627\u062d\u0627\u062f\u064a SLD \u0645\u0639 \u062a\u0648\u0632\u064a\u0639 \u0627\u0644\u0641\u0627\u0632\u0627\u062a" }))
                    rPanel.Add(GetStrVal(rs, "name"));
                List<string> rQa = new List<string>();
                foreach (Dictionary<string, object> rs in (List<Dictionary<string, object>>)selectSpecs.Invoke(null, new object[] { "\u0627\u0639\u0645\u0644 \u0641\u062d\u0635 \u0641\u0646\u064a \u0643\u0647\u0631\u0628\u0627\u0626\u064a \u0644\u0644\u0645\u062e\u0637\u0637 \u0648\u0643\u0634\u0641 \u0627\u0644\u0643\u0634\u0627\u0641\u0627\u062a \u0627\u0644\u063a\u064a\u0631 \u0645\u0648\u0635\u0644\u0629 \u0648\u0647\u0628\u0648\u0637 \u0627\u0644\u062c\u0647\u062f" }))
                    rQa.Add(GetStrVal(rs, "name"));
                List<string> rMfg = new List<string>();
                foreach (Dictionary<string, object> rs in (List<Dictionary<string, object>>)selectSpecs.Invoke(null, new object[] { "\u062a\u0635\u0646\u064a\u0639 \u062f\u0648\u0644\u0627\u0628 \u0644\u0648\u062d\u0629 \u0643\u0647\u0631\u0628\u0627\u0621 3d \u0645\u0639 \u0628\u0627\u0631\u0627\u062a \u0646\u062d\u0627\u0633 \u0648\u062a\u0648\u0632\u064a\u0639 \u062d\u0631\u0627\u0631\u064a" }))
                    rMfg.Add(GetStrVal(rs, "name"));
                List<string> rInv = new List<string>();
                foreach (Dictionary<string, object> rs in (List<Dictionary<string, object>>)selectSpecs.Invoke(null, new object[] { "\u0627\u0628\u062f\u0623 \u062c\u0631\u062f \u0627\u0644\u0644\u064a\u0646\u064a\u0627\u062a \u0644\u0648\u062d\u0629 \u0628\u0644\u0648\u062d\u0629" }))
                    rInv.Add(GetStrVal(rs, "name"));
                List<string> rDist = new List<string>();
                foreach (Dictionary<string, object> rs in (List<Dictionary<string, object>>)selectSpecs.Invoke(null, new object[] { "\u0645\u062e\u0637\u0637 \u0643\u0627\u0628\u0644\u0627\u062a \u0627\u0644\u0645\u062d\u0648\u0644 RMU \u0648\u0627\u0644\u0645\u0648\u0644\u062f ATS \u0648\u0627\u0644\u0644\u0627\u0646\u062f\u0633\u0643\u064a\u0628 Master SLD" }))
                    rDist.Add(GetStrVal(rs, "name"));

                ed.WriteMessage("\nROUTER_COUNTS=" + rCirc.Count + "," + rPanel.Count + "," + rQa.Count + "," + rMfg.Count + "," + rInv.Count + "," + rDist.Count + "\n");
                AssertTrue(rCirc.Contains("find_blocks_along_path") && rCirc.Contains("trace_all_circuits") && rCirc.Contains("manage_project_legend_db") && rCirc.Contains("guided_circuit_inventory"), "Circuit router missing tools");
                AssertTrue(rPanel.Contains("generate_panel_schedule_and_sld") && rPanel.Contains("calculate_lv_electrical"), "Panel router missing tools");
                AssertTrue(rQa.Contains("audit_electrical_drawing"), "QA router missing tool");
                AssertTrue(rMfg.Contains("design_panel_manufacturing_layout"), "Panel Mfg router missing tool");
                AssertTrue(rInv.Contains("guided_circuit_inventory") && rInv.Contains("manage_project_legend_db"), "Guided inventory router missing tools");
                AssertTrue(rDist.Contains("build_distribution_network_and_sld"), "Distribution network router missing build_distribution_network_and_sld");

                // 2. JIT Method Preparation
                Type execType = ctxAsm.GetType("AIEngineer.AutoCAD.CadToolExecutor");
                AssertTrue(execType != null, "CadToolExecutor missing");
                string[] jitMethods = new string[] {
                    "ResolveEffectiveUnitsPerMeter", "ExpandConnectedPathCurves", "ExpandConnectedPathCurvesCore",
                    "FindBlocksAlongPath", "ExecuteCircuitTracePass", "FinalizeCircuitPathOutput",
                    "TraceAllCircuits", "ScanAllCircuitsInternal", "ComputeDrawingFixtureMetrics",
                    "CollectAllCircuitCandidateCurveIds", "TraceSingleCircuitComponentCached", "BuildCircuitScheduleSheet",
                    "GeneratePanelScheduleAndSld", "AuditElectricalDrawing", "DrawSingleLineDiagram", "BuildBalancedPanelSchedule",
                    "ManageProjectLegendDb", "GuidedCircuitInventory", "CaptureVisualView", "DesignPanelManufacturingLayout",
                    "BuildDistributionNetworkAndSld"
                };
                MethodInfo[] allExecMethods = execType.GetMethods(flags);
                foreach (string jm in jitMethods)
                {
                    int preparedCount = 0;
                    for (int miIdx = 0; miIdx < allExecMethods.Length; miIdx++)
                    {
                        if (string.Equals(allExecMethods[miIdx].Name, jm, StringComparison.Ordinal))
                        {
                            RuntimeHelpers.PrepareMethod(allExecMethods[miIdx].MethodHandle);
                            preparedCount++;
                        }
                    }
                    AssertTrue(preparedCount > 0, "Missing JIT method " + jm);
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

                // 4j. Test 12: Multi-Column Legend Extraction + DB-Driven draw_block_legend + Visual PNG Capture
                List<ObjectId> t12TempIds = new List<ObjectId>();
                ObjectId t12F1 = ObjectId.Null, t12F1e = ObjectId.Null, t12D1 = ObjectId.Null, t12D2 = ObjectId.Null, t12D2e = ObjectId.Null;
                using (Transaction tr12 = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr12.GetObject(db.BlockTableId, OpenMode.ForRead);
                    t12F1 = EnsureRectBlock(tr12, bt, "F1", 0.30, 0.10, 2);
                    t12F1e = EnsureRectBlock(tr12, bt, "F1E", 0.30, 0.10, 1);
                    t12D1 = EnsureRectBlock(tr12, bt, "D1", 0.25, 0.25, 4);
                    t12D2 = EnsureRectBlock(tr12, bt, "D2", 0.25, 0.25, 6);
                    t12D2e = EnsureRectBlock(tr12, bt, "D2E", 0.25, 0.25, 1);
                    BlockTableRecord ms = (BlockTableRecord)tr12.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Left Legend Column (Cx=-60.0): D1 (15W), D3-skipped? No, D1 + F1? Left col: D1=15W, D2=23W, D2E overlapping D2 at row 2? Put D2 & D2E in right col!
                    // Headers above left column
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-60.2, 23.0, 0), "LEGEND:", 0.25, "0"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-60.2, 22.0, 0), "SYMBOL", 0.20, "0"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-57.5, 22.0, 0), "DESCRIPTION", 0.20, "0"));

                    // Left col (X=-60.0): D1=15W, F1=36W (make Left col have MORE text lines than Right col!)
                    t12TempIds.Add(InsertBlock(ms, tr12, t12D1, new Point3d(-60.0, 20.5, 0), "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-59.3, 20.5, 0), "D1", 0.18, "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-58.5, 20.7, 0), "WEATHER PROOF LED LIGHTING FIXTURE 15 WATT, DOWNLIGHT", 0.16, "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-58.5, 20.4, 0), "SURFACE MOUNTED 230V IP54", 0.16, "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-58.5, 20.1, 0), "4000K 2200LM", 0.16, "E-FIXTURES"));

                    t12TempIds.Add(InsertBlock(ms, tr12, t12D2, new Point3d(-60.0, 19.0, 0), "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-59.3, 19.0, 0), "D2", 0.18, "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-58.5, 19.2, 0), "LED LIGHTING FIXTURE 23 WATT, DOWNLIGHT RECESSED", 0.16, "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-58.5, 18.9, 0), "230V IP54 IK02 2100LM", 0.16, "E-FIXTURES"));

                    // Right col (X=-51.0): F1=36W, F1E=AS ABOVE, plus overlapping D2+D2E with D2E tag -> picks D2E!
                    t12TempIds.Add(InsertBlock(ms, tr12, t12F1, new Point3d(-51.0, 20.5, 0), "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-50.2, 20.5, 0), "F1", 0.18, "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-49.5, 20.6, 0), "WEATHER PROOF LED LIGHTING FIXTURE 36 WATT, SURFACE", 0.16, "E-FIXTURES"));

                    t12TempIds.Add(InsertBlock(ms, tr12, t12F1e, new Point3d(-51.0, 19.0, 0), "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-50.2, 19.0, 0), "F1E", 0.18, "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-49.5, 19.0, 0), "AS ABOVE BUT WITH EMERGENCY BACKUP BATTERY FOR 1.5 Hrs", 0.16, "E-FIXTURES"));

                    t12TempIds.Add(InsertBlock(ms, tr12, t12D2, new Point3d(-51.0, 17.5, 0), "E-FIXTURES"));
                    t12TempIds.Add(InsertBlock(ms, tr12, t12D2e, new Point3d(-51.0, 17.5, 0), "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-50.2, 17.5, 0), "D2E", 0.18, "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-49.5, 17.5, 0), "AS ABOVE 23W EMERGENCY BACKUP", 0.16, "E-FIXTURES"));

                    // Far-away floor-plan circuit tag noise aligned vertically at X=-60.0 (Y=-25..-28)
                    t12TempIds.Add(InsertBlock(ms, tr12, t12D1, new Point3d(-60.0, -25.0, 0), "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-58.5, -25.0, 0), "ELP-B1.2/1", 0.18, "E-LIGHTING"));
                    t12TempIds.Add(InsertBlock(ms, tr12, t12F1, new Point3d(-60.0, -27.0, 0), "E-FIXTURES"));
                    t12TempIds.Add(AddText(ms, tr12, new Point3d(-58.5, -27.0, 0), "UP TO NEXT FLOOR", 0.18, "E-LIGHTING"));
                    tr12.Commit();
                }

                Dictionary<string, object> r12a = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "manage_project_legend_db",
                    new Dictionary<string, object> { { "action", "extract_from_drawing" } }
                });
                ed.WriteMessage("\nTEST12A=" + jss.Serialize(r12a) + "\n");
                AssertTrue(GetBoolVal(r12a, "ok") && GetIntVal(r12a, "items_count") == 5, "Test 12A expected exact 5 clean multi-col Legend items");
                Dictionary<string, Dictionary<string, object>> db12 = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                ArrayList items12a = r12a["items"] as ArrayList;
                if (items12a == null) { List<object> il12 = r12a["items"] as List<object>; items12a = il12 != null ? new ArrayList(il12) : null; }
                AssertTrue(items12a != null, "Test 12A items missing");
                foreach (object io in items12a)
                {
                    Dictionary<string, object> idict = io as Dictionary<string, object>;
                    if (idict != null) db12[GetStrVal(idict, "block_name")] = idict;
                }
                AssertTrue(db12.ContainsKey("D1") && Math.Abs(GetDblVal(db12["D1"], "watt") - 15.0) < 0.1, "Test 12A D1 expected 15W");
                AssertTrue(db12.ContainsKey("D2") && Math.Abs(GetDblVal(db12["D2"], "watt") - 23.0) < 0.1, "Test 12A D2 expected 23W");
                AssertTrue(db12.ContainsKey("F1") && Math.Abs(GetDblVal(db12["F1"], "watt") - 36.0) < 0.1, "Test 12A F1 expected 36W (must NOT read Left col 15W!)");
                AssertTrue(db12.ContainsKey("F1E") && Math.Abs(GetDblVal(db12["F1E"], "watt") - 36.0) < 0.1 && GetBoolVal(db12["F1E"], "is_emergency"), "Test 12A F1E expected 36W inherited from F1");
                AssertTrue(db12.ContainsKey("D2E") && !GetStrVal(db12["D2E"], "description").Contains("ELP-"), "Test 12A D2E overlapping deduction failed");

                // 12B: draw_block_legend WITHOUT explicit descriptions must auto-pull from Project Legend DB
                Dictionary<string, object> r12b = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "draw_block_legend",
                    new Dictionary<string, object> { { "block_names", new string[] { "F1", "F1E" } }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST12B=" + jss.Serialize(r12b) + "\n");
                AssertTrue(GetBoolVal(r12b, "ok") && GetIntVal(r12b, "row_count") >= 2, "Test 12B draw_block_legend failed");
                ArrayList rows12b = r12b["rows"] as ArrayList;
                if (rows12b == null) { List<object> rl12 = r12b["rows"] as List<object>; rows12b = rl12 != null ? new ArrayList(rl12) : null; }
                bool foundF1DbDesc = false;
                if (rows12b != null)
                {
                    foreach (object ro in rows12b)
                    {
                        Dictionary<string, object> rd = ro as Dictionary<string, object>;
                        if (rd != null && GetStrVal(rd, "block_name") == "F1" && GetStrVal(rd, "description").Contains("36 WATT"))
                            foundF1DbDesc = true;
                    }
                }
                AssertTrue(foundF1DbDesc, "Test 12B draw_block_legend did not auto-pull 36 WATT F1 description from Project Legend DB");

                // 12C: capture_visual_view PNG rendering verification on Test 12 Legend selection
                ed.SetImpliedSelection(t12TempIds.ToArray());
                string capTestPng = Path.Combine(Path.GetTempPath(), "accore_vis_test12c.png");
                if (File.Exists(capTestPng)) File.Delete(capTestPng);
                Dictionary<string, object> r12c = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "capture_visual_view",
                    new Dictionary<string, object> { { "scope", "selection" }, { "annotate_block_names", true }, { "save_path", capTestPng } }
                });
                AssertTrue(GetBoolVal(r12c, "ok") && File.Exists(capTestPng), "Test 12C capture_visual_view failed to write PNG");
                string visB64 = GetStrVal(r12c, "_vision_png_base64");
                AssertTrue(!string.IsNullOrEmpty(visB64) && visB64.Length > 1000, "Test 12C _vision_png_base64 too small");
                byte[] pngHdr = File.ReadAllBytes(capTestPng);
                AssertTrue(pngHdr.Length > 8 && pngHdr[0] == 0x89 && pngHdr[1] == 0x50 && pngHdr[2] == 0x4E && pngHdr[3] == 0x47, "Test 12C invalid PNG magic bytes");
                ed.WriteMessage("\nTEST12C=OK|W=" + GetIntVal(r12c, "width_px") + "|H=" + GetIntVal(r12c, "height_px") + "|BYTES=" + pngHdr.Length + "\n");
                ed.SetImpliedSelection(new ObjectId[0]);

                // Cleanup Test 12 temporary entities and files
                try { if (File.Exists(capTestPng)) File.Delete(capTestPng); } catch { }
                execTool.Invoke(null, new object[] { doc, "manage_project_legend_db", new Dictionary<string, object> { { "action", "clear_catalog" } } });
                using (Transaction trDel12 = db.TransactionManager.StartTransaction())
                {
                    for (int di = 0; di < t12TempIds.Count; di++)
                    {
                        Entity de = trDel12.GetObject(t12TempIds[di], OpenMode.ForWrite, false, true) as Entity;
                        if (de != null && !de.IsErased) de.Erase(true);
                    }
                    BlockTableRecord ms = (BlockTableRecord)trDel12.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    foreach (ObjectId oid in ms)
                    {
                        Entity ee = trDel12.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ee != null && !ee.IsErased && string.Equals(ee.Layer ?? "", "_AI_BLOCK_LEGEND", StringComparison.OrdinalIgnoreCase))
                        {
                            ee.UpgradeOpen();
                            ee.Erase(true);
                        }
                    }
                    trDel12.Commit();
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

                // 7. Test 13: Phase 5 Switchgear & 2D/3D Panel Manufacturing (Auto 2D+3D + ACADE + Thermal + Busbar + 3D-from-2D)
                string mfgXl = Path.Combine(Path.GetTempPath(), "phase5_panel_mfg.xlsx");
                if (File.Exists(mfgXl)) File.Delete(mfgXl);
                Dictionary<string, object> r13a = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "design_panel_manufacturing_layout",
                    new Dictionary<string, object>
                    {
                        { "mode", "auto_2d_and_3d" },
                        { "panel_name", "MCC-01" },
                        { "manufacturer", "Schneider Electric" },
                        { "cable_entry", "bottom" },
                        { "ambient_temp_c", 40.0 },
                        { "components", new object[]
                            {
                                new Dictionary<string, object> { { "tag", "QFMAIN" }, { "part_number", "LV429630" }, { "category", "mccb" }, { "poles", 3 }, { "rating_a", 100.0 } },
                                new Dictionary<string, object> { { "tag", "VFD1" }, { "part_number", "ATV320U22N4C" }, { "category", "vfd" }, { "poles", 3 }, { "rating_a", 5.6 }, { "circuit_name", "PUMP-1" } },
                                new Dictionary<string, object> { { "tag", "QF1" }, { "part_number", "A9F74116" }, { "category", "mcb" }, { "poles", 1 }, { "rating_a", 16.0 }, { "circuit_name", "L1" }, { "phase", "R" } },
                                new Dictionary<string, object> { { "tag", "QF2" }, { "part_number", "A9F74332" }, { "category", "mcb" }, { "poles", 3 }, { "rating_a", 32.0 }, { "circuit_name", "M1" }, { "phase", "RYB" }, { "connected_to", "KM1" } },
                                new Dictionary<string, object> { { "tag", "KM1" }, { "part_number", "LC1D32M7" }, { "category", "contactor" }, { "poles", 3 }, { "rating_a", 32.0 }, { "circuit_name", "M1" }, { "phase", "RYB" } }
                            }
                        },
                        { "export_excel", true },
                        { "excel_path", mfgXl },
                        { "zoom", false }
                    }
                });
                ed.WriteMessage("\nTEST13A=OK|COMP=" + GetIntVal(r13a, "components_count") + "|WIRES=" + GetIntVal(r13a, "wires_count") + "|CNC=" + GetIntVal(r13a, "cnc_holes_count") + "\n");
                AssertTrue(GetBoolVal(r13a, "ok") && GetBoolVal(r13a, "layout_2d_drawn") && GetBoolVal(r13a, "assembly_3d_drawn"), "Test 13A failed 2D/3D draw");
                AssertTrue(GetBoolVal(r13a, "zero_collision_verified") && GetBoolVal(r13a, "side_section_drawn") && GetIntVal(r13a, "cnc_holes_count") > 0, "Test 13A failed zero-collision / side section / CNC");
                AssertTrue(GetBoolVal(r13a, "excel_exported") && File.Exists(mfgXl), "Test 13A missing BOM Excel");
                AssertTrue(GetIntVal(r13a, "components_count") >= 5 && GetIntVal(r13a, "wires_count") >= 5, "Test 13A missing components/wires");

                Dictionary<string, object> r13b = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "design_panel_manufacturing_layout",
                    new Dictionary<string, object>
                    {
                        { "mode", "3d_from_2d" },
                        { "panel_name", "MCC-01" },
                        { "draw_bom_table", false },
                        { "zoom", false }
                    }
                });
                ed.WriteMessage("\nTEST13B=OK|3D=" + GetBoolVal(r13b, "assembly_3d_drawn") + "\n");
                AssertTrue(GetBoolVal(r13b, "ok") && GetBoolVal(r13b, "assembly_3d_drawn") && !GetBoolVal(r13b, "layout_2d_drawn"), "Test 13B failed 3d_from_2d");

                Dictionary<string, object> r13c = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "design_panel_manufacturing_layout",
                    new Dictionary<string, object> { { "mode", "trace_connections" }, { "trace_target", "KM1" } }
                });
                ed.WriteMessage("\nTEST13C=OK|IN=" + GetIntVal(r13c, "incoming_count") + "|OUT=" + GetIntVal(r13c, "outgoing_count") + "\n");
                AssertTrue(GetBoolVal(r13c, "ok") && GetIntVal(r13c, "incoming_count") >= 1 && GetIntVal(r13c, "outgoing_count") >= 1, "Test 13C failed trace_connections");

                Dictionary<string, object> r13d = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "design_panel_manufacturing_layout",
                    new Dictionary<string, object> { { "mode", "resize_busbar" }, { "target_busbar_size", "28x6" } }
                });
                ed.WriteMessage("\nTEST13D=OK|STD=" + GetStrVal(r13d, "selected_standard_busbar") + "\n");
                AssertTrue(GetBoolVal(r13d, "ok") && GetStrVal(r13d, "selected_standard_busbar") == "30x5 Cu", "Test 13D failed resize_busbar");

                Dictionary<string, object> r13dMulti = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "design_panel_manufacturing_layout",
                    new Dictionary<string, object> { { "mode", "resize_busbar" }, { "target_busbar_size", "2x60x10" } }
                });
                AssertTrue(GetBoolVal(r13dMulti, "ok") && GetStrVal(r13dMulti, "selected_standard_busbar") == "2x(60x10) Cu", "Test 13D failed multi-run 2x60x10 resize_busbar");

                Dictionary<string, object> r13dUndo = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "undo_last_action",
                    new Dictionary<string, object> { { "steps", 1 } }
                });
                AssertTrue(GetBoolVal(r13dUndo, "ok"), "Test 13D undo resize_busbar failed");

                Dictionary<string, object> r13e = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "design_panel_manufacturing_layout",
                    new Dictionary<string, object> { { "mode", "snap_and_reroute" }, { "panel_name", "MCC-01" }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST13E=OK|ZCOL=" + GetBoolVal(r13e, "zero_collision_verified") + "\n");
                AssertTrue(GetBoolVal(r13e, "ok") && GetBoolVal(r13e, "zero_collision_verified"), "Test 13E failed snap_and_reroute");

                Dictionary<string, object> r13Hist = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "undo_last_action",
                    new Dictionary<string, object> { { "action", "list_history" } }
                });
                AssertTrue(GetBoolVal(r13Hist, "ok") && GetIntVal(r13Hist, "history_count") >= 3, "Test 13E missing interactive panel undo history");

                Dictionary<string, object> r13f = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "design_panel_manufacturing_layout",
                    new Dictionary<string, object> { { "mode", "sld_from_panel" }, { "panel_name", "MCC-01" } }
                });
                AssertTrue(GetBoolVal(r13f, "ok") && GetBoolVal(r13f, "sld_drawn"), "Test 13F sld_from_panel failed");

                Dictionary<string, object> r13g = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "design_panel_manufacturing_layout",
                    new Dictionary<string, object> { { "mode", "panel_from_sld" }, { "draw_bom_table", false }, { "zoom", false } }
                });
                ed.WriteMessage("\nTEST13F=OK|SLD=" + GetBoolVal(r13f, "sld_drawn") + "|PANEL_FROM_SLD=" + GetBoolVal(r13g, "ok") + "\n");
                AssertTrue(GetBoolVal(r13g, "ok") && GetBoolVal(r13g, "layout_2d_drawn") && GetBoolVal(r13g, "assembly_3d_drawn"), "Test 13F panel_from_sld failed");
                try { if (File.Exists(mfgXl)) File.Delete(mfgXl); } catch { }

                // Test 13G: Dedicated Panel Manufacturing Commands + All 8 Z-Depth View Presets
                Type pmCmdType = execType.Assembly.GetType("AIEngineer.AutoCAD.PanelManufacturingCommands");
                AssertTrue(pmCmdType != null && pmCmdType.GetMethod("ShowPanelManufacturingPalette") != null, "Test 13G missing AIENGPANEL command class");
                string[] allPresets = new string[] {
                    "full_assembly", "busbars_only", "wiring_uncovered", "no_door",
                    "cnc_drilling_only", "components_only", "2d_only", "3d_only"
                };
                for (int pi = 0; pi < allPresets.Length; pi++)
                {
                    Dictionary<string, object> rPre = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                        doc, "design_panel_manufacturing_layout",
                        new Dictionary<string, object> { { "mode", "set_view_preset" }, { "view_preset", allPresets[pi] } }
                    });
                    AssertTrue(GetBoolVal(rPre, "ok"), "Test 13G set_view_preset failed for " + allPresets[pi]);
                }
                ed.WriteMessage("\nTEST13G=OK|AIENGPANEL_PRESETS=8\n");

                // 8. Test 14: Phase 6 Guided Circuit Inventory, Cross-Plan Lighting+Power Merge & Zero-Hallucination Spec Gate
                using (Transaction trClean14 = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord ms = (BlockTableRecord)trClean14.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    foreach (ObjectId oid in ms)
                    {
                        Entity ent = trClean14.GetObject(oid, OpenMode.ForWrite) as Entity;
                        if (ent != null && !ent.IsErased) ent.Erase(true);
                    }
                    trClean14.Commit();
                }

                // Clear Legend DB catalog so TEST14B verifies strict zero-hallucination block when specs are missing
                execTool.Invoke(null, new object[] {
                    doc, "manage_project_legend_db",
                    new Dictionary<string, object> { { "action", "clear_catalog" } }
                });

                string brokenBranchHandle14 = "";
                string wrongWireHandle14 = "";
                string randomInspectWire14 = "";
                string pwrWireHandle14 = "";
                using (Transaction tr14 = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt14 = (LayerTable)tr14.GetObject(db.LayerTableId, OpenMode.ForRead);
                    EnsureLayer(tr14, lt14, "E-LIGHTING", 6);
                    EnsureLayer(tr14, lt14, "E-POWER", 4);
                    EnsureLayer(tr14, lt14, "E-FIXTURES", 7);

                    BlockTable bt14 = (BlockTable)tr14.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId sockBtr14 = EnsureRectBlock(tr14, bt14, "SOCK_2P", 0.30, 0.20, 4);
                    BlockTableRecord ms14 = (BlockTableRecord)tr14.GetObject(bt14[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Circuit DB-1/1 (Lighting): 1 F1 main + 1 F1 on disconnected broken branch + wrong stray wire
                    InsertBlock(ms14, tr14, f1Btr, new Point3d(2.0, 2.0, 0), "E-FIXTURES");
                    AddLine(ms14, tr14, new Point3d(2.30, 2.0, 0), new Point3d(6.0, 2.0, 0), "E-LIGHTING", 6);
                    InsertBlock(ms14, tr14, arrowBtr, new Point3d(6.0, 2.0, 0), "E-LIGHTING");
                    AddText(ms14, tr14, new Point3d(6.2, 2.15, 0), "DB-1/1", 0.18, "E-LIGHTING");

                    // Broken branch (>2m gap) connecting to an extra F1
                    ObjectId brkId14 = AddLine(ms14, tr14, new Point3d(10.0, 2.0, 0), new Point3d(12.0, 2.0, 0), "E-LIGHTING", 6);
                    AddLine(ms14, tr14, new Point3d(12.0, 2.0, 0), new Point3d(14.0, 2.0, 0), "E-LIGHTING", 6);
                    InsertBlock(ms14, tr14, f1Btr, new Point3d(14.30, 2.0, 0), "E-FIXTURES");
                    brokenBranchHandle14 = tr14.GetObject(brkId14, OpenMode.ForRead).Handle.ToString();

                    // Wrong stray wire + F1E far enough from DB-1/1 (> tagSearchRadius) so it stays unnamed until removed in TEST14C
                    ObjectId wrgId14 = AddLine(ms14, tr14, new Point3d(2.0, -4.0, 0), new Point3d(5.0, -4.0, 0), "E-LIGHTING", 5);
                    InsertBlock(ms14, tr14, f1eBtr, new Point3d(5.30, -4.0, 0), "E-FIXTURES");
                    wrongWireHandle14 = tr14.GetObject(wrgId14, OpenMode.ForRead).Handle.ToString();

                    // Circuit DB-1/5 (Lighting)
                    InsertBlock(ms14, tr14, f1Btr, new Point3d(2.0, 8.0, 0), "E-FIXTURES");
                    ObjectId r5WireId = AddLine(ms14, tr14, new Point3d(2.30, 8.0, 0), new Point3d(6.0, 8.0, 0), "E-LIGHTING", 6);
                    InsertBlock(ms14, tr14, arrowBtr, new Point3d(6.0, 8.0, 0), "E-LIGHTING");
                    AddText(ms14, tr14, new Point3d(6.2, 8.15, 0), "DB-1/5", 0.18, "E-LIGHTING");
                    randomInspectWire14 = tr14.GetObject(r5WireId, OpenMode.ForRead).Handle.ToString();

                    // Circuit DB-1/10 (Lighting) to test numeric sort (1, 5, 10)
                    InsertBlock(ms14, tr14, f1Btr, new Point3d(2.0, 14.0, 0), "E-FIXTURES");
                    AddLine(ms14, tr14, new Point3d(2.30, 14.0, 0), new Point3d(6.0, 14.0, 0), "E-LIGHTING", 6);
                    InsertBlock(ms14, tr14, arrowBtr, new Point3d(6.0, 14.0, 0), "E-LIGHTING");
                    AddText(ms14, tr14, new Point3d(6.2, 14.15, 0), "DB-1/10", 0.18, "E-LIGHTING");

                    // Circuit DB-1/2 (Power) for separate-discipline scan + numeric merge (1, 2, 5, 10)
                    InsertBlock(ms14, tr14, sockBtr14, new Point3d(25.0, 2.0, 0), "E-POWER");
                    ObjectId pwrId14 = AddLine(ms14, tr14, new Point3d(25.30, 2.0, 0), new Point3d(29.0, 2.0, 0), "E-POWER", 4);
                    InsertBlock(ms14, tr14, arrowBtr, new Point3d(29.0, 2.0, 0), "E-POWER");
                    AddText(ms14, tr14, new Point3d(29.2, 2.15, 0), "DB-1/2", 0.18, "E-POWER");
                    pwrWireHandle14 = tr14.GetObject(pwrId14, OpenMode.ForRead).Handle.ToString();

                    // Orphan fixture not wired to any circuit
                    InsertBlock(ms14, tr14, f1Btr, new Point3d(40.0, 20.0, 0), "E-FIXTURES");

                    tr14.Commit();
                }

                // TEST14A: Start guided inventory for lighting only, expect natural numeric order 1, 5, 10 & legend_ready=false
                Dictionary<string, object> r14a = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "start" }, { "discipline", "lighting" }, { "strict_legend_specs", true }, { "zoom", false } }
                });
                Dictionary<string, object> cur14a = r14a.ContainsKey("current_circuit") ? r14a["current_circuit"] as Dictionary<string, object> : null;
                AssertTrue(GetBoolVal(r14a, "ok") && GetBoolVal(r14a, "active") && cur14a != null && GetStrVal(cur14a, "circuit_name") == "DB-1/1", "Test 14A failed start");
                AssertTrue(!GetBoolVal(r14a, "legend_ready") && GetBoolVal(r14a, "needs_user_specs"), "Test 14A expected needs_user_specs=true");
                System.Collections.IEnumerable qPrev14 = r14a.ContainsKey("queue_preview") ? r14a["queue_preview"] as System.Collections.IEnumerable : null;
                List<string> qNames14 = new List<string>();
                if (qPrev14 != null) { foreach (object qo in qPrev14) if (qo != null) qNames14.Add(qo.ToString()); }
                AssertTrue(qNames14.Count >= 3 && qNames14[0].StartsWith("DB-1/1 ") && qNames14[1].StartsWith("DB-1/5 ") && qNames14[2].StartsWith("DB-1/10 "), "Test 14A numeric order mismatch: " + string.Join(", ", qNames14.ToArray()));
                ed.WriteMessage("\nTEST14A=OK|QUEUE=" + string.Join(",", qNames14.ToArray()) + "\n");

                // TEST14B: Strict Zero-Hallucination Gate blocks approval when specs missing (0 VA/kW, never 40 VA)
                Dictionary<string, object> r14b = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "approve_next" }, { "zoom", false } }
                });
                Dictionary<string, object> cur14b = r14b.ContainsKey("current_circuit") ? r14b["current_circuit"] as Dictionary<string, object> : null;
                AssertTrue(GetBoolVal(r14b, "ok") && GetBoolVal(r14b, "blocked_for_missing_specs") && GetBoolVal(r14b, "needs_user_specs"), "Test 14B did not block missing specs");
                AssertTrue(cur14b != null && Convert.ToDouble(cur14b["load_kva"], CultureInfo.InvariantCulture) == 0.0, "Test 14B assumed load instead of 0");
                ed.WriteMessage("\nTEST14B=OK|BLOCKED_NO_40VA_HALLUCINATION\n");

                // TEST14C: add_selection traces broken branch forward; remove_selection excludes wrong curve permanently across refresh
                Dictionary<string, object> r14cAdd = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "add_selection" }, { "handles", new string[] { brokenBranchHandle14, wrongWireHandle14 } }, { "zoom", false } }
                });
                Dictionary<string, object> cur14c1 = r14cAdd["current_circuit"] as Dictionary<string, object>;
                AssertTrue(GetBoolVal(r14cAdd, "ok") && GetIntVal(cur14c1, "matched_blocks_count") >= 2, "Test 14C add_selection failed");

                Dictionary<string, object> r14cRem = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "remove_selection" }, { "handles", new string[] { wrongWireHandle14 } }, { "zoom", false } }
                });
                Dictionary<string, object> r14cRef = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "refresh" }, { "zoom", false } }
                });
                Dictionary<string, object> cur14c2 = r14cRef["current_circuit"] as Dictionary<string, object>;
                AssertTrue(GetBoolVal(r14cRem, "ok") && GetBoolVal(r14cRef, "ok") && GetIntVal(cur14c2, "excluded_handles_count") >= 1 && GetIntVal(cur14c2, "matched_blocks_count") == 2, "Test 14C remove+refresh failed");
                ed.WriteMessage("\nTEST14C=OK|BLOCKS=" + GetIntVal(cur14c2, "matched_blocks_count") + "|EXCL=" + GetIntVal(cur14c2, "excluded_handles_count") + "\n");

                // TEST14E (mid-session): inspect_selection inspects DB-1/5 without advancing queue current_index (still 0 on DB-1/1)
                Dictionary<string, object> r14e = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "inspect_selection" }, { "handles", new string[] { randomInspectWire14 } }, { "zoom", false } }
                });
                Dictionary<string, object> st14e = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "status" } }
                });
                AssertTrue(GetBoolVal(r14e, "ok") && GetStrVal(r14e, "circuit_name") == "DB-1/5" && GetIntVal(st14e, "current_index") == 0, "Test 14E random inspect altered active index");
                ed.WriteMessage("\nTEST14E=OK|RANDOM_INSPECT=DB-1/5|IDX=0\n");

                // TEST14D: Save Legend DB specs then approve DB-1/1 -> advances to DB-1/5, then approve DB-1/5 & DB-1/10
                execTool.Invoke(null, new object[] {
                    doc, "manage_project_legend_db",
                    new Dictionary<string, object>
                    {
                        { "action", "set_items" },
                        { "items", new object[]
                            {
                                new Dictionary<string, object> { { "block_name", "F1" }, { "watt", 36.0 }, { "category", "lighting" }, { "description", "LED Panel 60x60 36W" } },
                                new Dictionary<string, object> { { "block_name", "F1E" }, { "watt", 42.0 }, { "is_emergency", true }, { "category", "lighting" } },
                                new Dictionary<string, object> { { "block_name", "SOCK_2P" }, { "watt", 250.0 }, { "category", "socket" }, { "description", "Double Socket 250W" } }
                            }
                        }
                    }
                });
                Dictionary<string, object> r14d = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "approve_next" }, { "zoom", false } }
                });
                Dictionary<string, object> nxt14d = r14d["current_circuit"] as Dictionary<string, object>;
                AssertTrue(GetBoolVal(r14d, "ok") && GetBoolVal(r14d, "approved") && GetStrVal(r14d, "approved_circuit") == "DB-1/1" && nxt14d != null && GetStrVal(nxt14d, "circuit_name") == "DB-1/5", "Test 14D approve_next failed");
                execTool.Invoke(null, new object[] { doc, "guided_circuit_inventory", new Dictionary<string, object> { { "action", "approve_next" }, { "zoom", false } } });
                execTool.Invoke(null, new object[] { doc, "guided_circuit_inventory", new Dictionary<string, object> { { "action", "approve_next" }, { "zoom", false } } });
                ed.WriteMessage("\nTEST14D=OK|APPROVED_LIGHTING=1,5,10\n");

                // TEST14F: Inspect & save Power circuit DB-1/2 and verify unified numeric merge (1, 2, 5, 10) in Panel Schedule + SLD
                Dictionary<string, object> r14fPwr = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "inspect_selection" }, { "handles", new string[] { pwrWireHandle14 } }, { "save_to_panel", true }, { "zoom", false } }
                });
                AssertTrue(GetBoolVal(r14fPwr, "ok") && GetBoolVal(r14fPwr, "saved_to_panel") && GetStrVal(r14fPwr, "discipline") == "power", "Test 14F power circuit save failed");

                Dictionary<string, object> r14fSched = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "generate_panel_schedule_and_sld",
                    new Dictionary<string, object> { { "source", "saved_memory" }, { "approved_only", true }, { "panel_name", "DB-1" }, { "strict_legend_specs", true }, { "draw_panel_schedule", false }, { "draw_sld", false }, { "zoom", false } }
                });
                AssertTrue(GetBoolVal(r14fSched, "ok") && !GetBoolVal(r14fSched, "needs_user_specs"), "Test 14F merged schedule failed");
                List<Dictionary<string, object>> ps14List = new List<Dictionary<string, object>>();
                System.Collections.IEnumerable ps14Enum = r14fSched.ContainsKey("panels") ? r14fSched["panels"] as System.Collections.IEnumerable : null;
                if (ps14Enum != null) { foreach (object po in ps14Enum) if (po is Dictionary<string, object>) ps14List.Add((Dictionary<string, object>)po); }
                AssertTrue(ps14List.Count >= 1, "Test 14F missing panel list");
                Dictionary<string, object> db1Panel = ps14List[0];
                List<Dictionary<string, object>> db1Circs = new List<Dictionary<string, object>>();
                System.Collections.IEnumerable c14Enum = db1Panel.ContainsKey("circuits") ? db1Panel["circuits"] as System.Collections.IEnumerable : null;
                if (c14Enum != null) { foreach (object co in c14Enum) if (co is Dictionary<string, object>) db1Circs.Add((Dictionary<string, object>)co); }
                AssertTrue(db1Circs.Count >= 4, "Test 14F missing merged circuits");
                AssertTrue(GetStrVal(db1Circs[0], "circuit_name") == "DB-1/1" &&
                           GetStrVal(db1Circs[1], "circuit_name") == "DB-1/2" &&
                           GetStrVal(db1Circs[2], "circuit_name") == "DB-1/5" &&
                           GetStrVal(db1Circs[3], "circuit_name") == "DB-1/10", "Test 14F numeric ordering 1,2,5,10 failed");
                ed.WriteMessage("\nTEST14F=OK|MERGED_ORDER=1,2,5,10\n");

                // TEST14G: audit_remaining reports orphan fixtures and exact missing circuit gaps (3,4,6,7,8,9)
                Dictionary<string, object> r14g = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "audit_remaining" }, { "highlight", false }, { "zoom", false } }
                });
                AssertTrue(GetBoolVal(r14g, "ok") && GetIntVal(r14g, "orphan_fixtures_count") >= 1, "Test 14G missed orphan fixture");
                ed.WriteMessage("\nTEST14G=OK|ORPHANS=" + GetIntVal(r14g, "orphan_fixtures_count") + "\n");

                // 9. Test 15: Phase 7 Commercial Polish — XData COPY Recovery, MV/LV + Landscape Master SLD, DIALux Review & Wizard
                // TEST15A: Verify AIENG_CIRCUIT_TAG XData on DB-1/1, simulate COPY + erase old handles, and recall saved circuit
                int xdataTagged15a = 0;
                using (Transaction tr15a = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord ms15 = (BlockTableRecord)tr15a.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    List<Entity> db11Orig = new List<Entity>();
                    foreach (ObjectId oid in ms15)
                    {
                        if (oid.IsErased) continue;
                        Entity ent = tr15a.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        ResultBuffer rb = ent.GetXDataForApplication("AIENG_CIRCUIT_TAG");
                        if (rb == null) continue;
                        TypedValue[] tvs = rb.AsArray();
                        for (int ti = 0; ti < tvs.Length; ti++)
                        {
                            string xs = tvs[ti].Value != null ? tvs[ti].Value.ToString() : "";
                            if (xs.IndexOf("|DB-1/1|", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                db11Orig.Add(ent);
                                break;
                            }
                        }
                    }
                    xdataTagged15a = db11Orig.Count;
                    AssertTrue(xdataTagged15a >= 2, "Test 15A expected >= 2 XData-tagged entities for DB-1/1");
                    for (int ei = 0; ei < db11Orig.Count; ei++)
                    {
                        Entity orig = db11Orig[ei];
                        Entity copyEnt = orig.Clone() as Entity;
                        if (copyEnt != null)
                        {
                            copyEnt.TransformBy(Matrix3d.Displacement(new Vector3d(100.0, 100.0, 0.0)));
                            ms15.AppendEntity(copyEnt);
                            tr15a.AddNewlyCreatedDBObject(copyEnt, true);
                        }
                        orig.UpgradeOpen();
                        orig.Erase(true);
                    }
                    tr15a.Commit();
                }

                Dictionary<string, object> r15a = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "guided_circuit_inventory",
                    new Dictionary<string, object> { { "action", "recall" }, { "circuit_name", "DB-1/1" }, { "zoom", false } }
                });
                Dictionary<string, object> cur15a = r15a.ContainsKey("current_circuit") ? r15a["current_circuit"] as Dictionary<string, object> : null;
                AssertTrue(GetBoolVal(r15a, "ok") && GetBoolVal(r15a, "recalled") && GetBoolVal(r15a, "recovered_via_xdata") && cur15a != null && GetStrVal(cur15a, "circuit_name") == "DB-1/1",
                    "Test 15A XData recall recovery failed");
                ed.WriteMessage("\nTEST15A=OK|TAGGED=" + xdataTagged15a + "|RECOVERED_XDATA=" + GetBoolVal(r15a, "recovered_via_xdata") + "\n");

                // TEST15B: Strict Zero-Hallucination Gate on missing downstream panel load
                Dictionary<string, object> r15b = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "build_distribution_network_and_sld",
                    new Dictionary<string, object>
                    {
                        { "source", "explicit" },
                        { "strict_loads", true },
                        { "draw_in_cad", false },
                        { "export_excel", false },
                        { "nodes", new object[]
                            {
                                new Dictionary<string, object> { { "name", "RMU-1" }, { "kind", "rmu" } },
                                new Dictionary<string, object> { { "name", "TX-1" }, { "kind", "transformer" }, { "parent", "RMU-1" } },
                                new Dictionary<string, object> { { "name", "MDB-1" }, { "kind", "mdb" }, { "parent", "TX-1" } },
                                new Dictionary<string, object> { { "name", "DB-UNKNOWN" }, { "kind", "db" }, { "parent", "MDB-1" }, { "feeder_length_m", 30.0 } }
                            }
                        }
                    }
                });
                AssertTrue(GetBoolVal(r15b, "ok") && GetBoolVal(r15b, "blocked_for_missing_loads") && GetBoolVal(r15b, "needs_user_loads"),
                    "Test 15B expected blocked_for_missing_loads=true");
                ed.WriteMessage("\nTEST15B=OK|STRICT_MISSING_LOAD_GATE\n");

                // TEST15C: Full RMU->TX->MDB->ATS/GEN->SMDB->DB(DB hydrated) + Landscape Feeder Pillar -> Master SLD + Excel
                string xl15c = Path.Combine(Path.GetTempPath(), "accore_master_dist_15c.xlsx");
                try { if (File.Exists(xl15c)) File.Delete(xl15c); } catch { }
                Dictionary<string, object> r15c = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "build_distribution_network_and_sld",
                    new Dictionary<string, object>
                    {
                        { "source", "explicit" },
                        { "strict_loads", true },
                        { "draw_in_cad", true },
                        { "export_excel", true },
                        { "excel_path", xl15c },
                        { "zoom", false },
                        { "nodes", new object[]
                            {
                                new Dictionary<string, object> { { "name", "RMU-1" }, { "kind", "rmu" } },
                                new Dictionary<string, object> { { "name", "TX-1" }, { "kind", "transformer" }, { "parent", "RMU-1" }, { "feeder_length_m", 15.0 } },
                                new Dictionary<string, object> { { "name", "MDB-1" }, { "kind", "mdb" }, { "parent", "TX-1" }, { "feeder_length_m", 12.0 } },
                                new Dictionary<string, object> { { "name", "ATS-1" }, { "kind", "ats" }, { "parent", "MDB-1" }, { "feeder_length_m", 10.0 }, { "is_emergency", true } },
                                new Dictionary<string, object> { { "name", "GEN-1" }, { "kind", "generator" }, { "parent", "ATS-1" }, { "feeder_length_m", 18.0 }, { "is_emergency", true } },
                                new Dictionary<string, object> { { "name", "SMDB-1" }, { "kind", "smdb" }, { "parent", "MDB-1" }, { "feeder_length_m", 20.0 } },
                                new Dictionary<string, object> { { "name", "DB-1" }, { "kind", "db" }, { "parent", "SMDB-1" }, { "feeder_length_m", 25.0 } },
                                new Dictionary<string, object> { { "name", "FP-LAND-1" }, { "kind", "feeder_pillar" }, { "parent", "MDB-1" }, { "feeder_length_m", 65.0 }, { "load_kw", 18.0 }, { "is_landscape", true }, { "install_method", "buried" } }
                            }
                        }
                    }
                });
                AssertTrue(GetBoolVal(r15c, "ok") && GetIntVal(r15c, "nodes_count") >= 6 &&
                           GetIntVal(r15c, "landscape_feeders_count") >= 1 && GetDblVal(r15c, "transformer_kva") >= 100.0 &&
                           GetDblVal(r15c, "generator_kva") > 0.0 && GetDblVal(r15c, "ats_rating_a") > 0.0 &&
                           GetBoolVal(r15c, "drawn_in_cad") && GetIntVal(r15c, "cad_entities_created") > 0 &&
                           GetBoolVal(r15c, "excel_exported") && File.Exists(xl15c),
                    "Test 15C master distribution build failed");
                execTool.Invoke(null, new object[] { doc, "undo_last_action", new Dictionary<string, object> { { "steps", 1 } } });
                try { if (File.Exists(xl15c)) File.Delete(xl15c); } catch { }
                ed.WriteMessage("\nTEST15C=OK|NODES=" + GetIntVal(r15c, "nodes_count") + "|TX_KVA=" + GetDblVal(r15c, "transformer_kva") + "|LAND=" + GetIntVal(r15c, "landscape_feeders_count") + "\n");

                // TEST15D: DIALux missing-parameter preview & override + STF export verification
                using (Transaction tr15d = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt15 = (LayerTable)tr15d.GetObject(db.LayerTableId, OpenMode.ForRead);
                    EnsureLayer(tr15d, lt15, "A-AREA", 3);
                    BlockTable bt15 = (BlockTable)tr15d.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId lumNewBtr = EnsureRectBlock(tr15d, bt15, "LUM_NEW", 0.30, 0.30, 2);
                    BlockTableRecord ms15d = (BlockTableRecord)tr15d.GetObject(bt15[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    Polyline rmPl = new Polyline(4);
                    rmPl.AddVertexAt(0, new Point2d(60.0, 0.0), 0, 0, 0);
                    rmPl.AddVertexAt(1, new Point2d(66.0, 0.0), 0, 0, 0);
                    rmPl.AddVertexAt(2, new Point2d(66.0, 5.0), 0, 0, 0);
                    rmPl.AddVertexAt(3, new Point2d(60.0, 5.0), 0, 0, 0);
                    rmPl.Closed = true;
                    rmPl.Layer = "A-AREA";
                    ms15d.AppendEntity(rmPl);
                    tr15d.AddNewlyCreatedDBObject(rmPl, true);
                    AddText(ms15d, tr15d, new Point3d(63.0, 2.5, 0), "Office 101", 0.25, "A-AREA");
                    InsertBlock(ms15d, tr15d, lumNewBtr, new Point3d(62.0, 2.5, 0), "E-LIGHTING");
                    tr15d.Commit();
                }

                string stf15d = Path.Combine(Path.GetTempPath(), "accore_dialux_15d.stf");
                try { if (File.Exists(stf15d)) File.Delete(stf15d); } catch { }
                Dictionary<string, object> r15dPrev = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "export_dialux_stf",
                    new Dictionary<string, object> { { "preview_missing_only", true }, { "file_path", stf15d } }
                });
                Dictionary<string, object> miss15d = r15dPrev.ContainsKey("missing_parameters_summary") ? r15dPrev["missing_parameters_summary"] as Dictionary<string, object> : null;
                AssertTrue(GetBoolVal(r15dPrev, "ok") && miss15d != null && GetIntVal(miss15d, "missing_count") >= 1,
                    "Test 15D preview_missing_only expected missing_count >= 1");

                Dictionary<string, object> r15dExp = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "export_dialux_stf",
                    new Dictionary<string, object>
                    {
                        { "file_path", stf15d },
                        { "save_to_project_db", true },
                        { "room_overrides", new object[]
                            {
                                new Dictionary<string, object> { { "room_name", "Office 101" }, { "ceiling_height_m", 3.4 }, { "mounting_height_m", 3.4 }, { "workplane_m", 0.80 } }
                            }
                        },
                        { "luminaire_overrides", new object[]
                            {
                                new Dictionary<string, object> { { "block_name", "LUM_NEW" }, { "watt", 40.0 }, { "lumens", 4200.0 } }
                            }
                        }
                    }
                });
                AssertTrue(GetBoolVal(r15dExp, "ok") && File.Exists(stf15d), "Test 15D DIALux STF export failed");
                string stfBody = File.ReadAllText(stf15d);
                AssertTrue(stfBody.IndexOf("Height=3.400", StringComparison.Ordinal) >= 0 &&
                           stfBody.IndexOf("[LUMINAIRE.L1]", StringComparison.Ordinal) >= 0 &&
                           stfBody.IndexOf("Load=40", StringComparison.Ordinal) >= 0 &&
                           stfBody.IndexOf("Flux=4200", StringComparison.Ordinal) >= 0,
                    "Test 15D STF output missing overridden height/luminaire specs");
                try { if (File.Exists(stf15d)) File.Delete(stf15d); } catch { }
                ed.WriteMessage("\nTEST15D=OK|STF_OVERRIDES_SAVED\n");

                // TEST15E: Verify DialuxWizardCommands exported in Context assembly
                Type dialuxCmdType = execType.Assembly.GetType("AIEngineer.AutoCAD.DialuxWizardCommands");
                AssertTrue(dialuxCmdType != null && dialuxCmdType.GetMethod("RunDialuxExportWizard") != null, "Test 15E missing AIENGDIALUX command class");
                ed.WriteMessage("\nTEST15E=OK|AIENGDIALUX=PRESENT\n");

                // TEST16A: Multi-column 8-col gridded Legend (with TYPE sketch diagrams + RECOMMENDED sub-lines + range wattages + zero-load panel/accessory)
                execTool.Invoke(null, new object[] {
                    doc, "manage_project_legend_db",
                    new Dictionary<string, object> { { "action", "set_item" }, { "block_name", "STALE_OLD_BLOCK" }, { "watt", 99.0 }, { "description", "Stale from another DWG" } }
                });
                List<string> handles16a = new List<string>();
                string wire16aCirc = "";
                using (Transaction tr16a = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt16 = (LayerTable)tr16a.GetObject(db.LayerTableId, OpenMode.ForRead);
                    EnsureLayer(tr16a, lt16, "E-LEGEND-16A", 7);
                    EnsureLayer(tr16a, lt16, "E-LTG-16A-CIRC", 4);
                    BlockTable bt16 = (BlockTable)tr16a.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId pedBtr = EnsureRectBlock(tr16a, bt16, "ped post", 0.4, 0.4, 6);
                    ObjectId up9Btr = EnsureRectBlock(tr16a, bt16, "upligh9w", 0.4, 0.4, 3);
                    ObjectId spotBtr = EnsureRectBlock(tr16a, bt16, "SPOT", 0.4, 0.4, 2);
                    ObjectId spoteBtr = EnsureRectBlock(tr16a, bt16, "SPOTE", 0.35, 0.35, 5);
                    ObjectId sideBtr = EnsureRectBlock(tr16a, bt16, "Sidelight", 0.4, 0.4, 1);
                    ObjectId panel16Btr = EnsureRectBlock(tr16a, bt16, "A$Cf9b77d30", 0.4, 0.4, 3);
                    ObjectId tranBtr = EnsureRectBlock(tr16a, bt16, "traneleled", 0.4, 0.4, 5);
                    BlockTableRecord ms16a = (BlockTableRecord)tr16a.GetObject(bt16[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    double[] yGrid = new double[] { 27.0, 24.0, 20.0, 16.0, 12.0, 8.0, 4.0, 0.0 };
                    for (int yi = 0; yi < yGrid.Length; yi++)
                        handles16a.Add(AddLine(ms16a, tr16a, new Point3d(120.0, yGrid[yi], 0), new Point3d(176.0, yGrid[yi], 0), "E-LEGEND-16A", 7).Handle.ToString());
                    double[] xGrid = new double[] { 120.0, 122.5, 127.0, 132.5, 149.0, 154.0, 159.0, 164.0, 176.0 };
                    for (int xi = 0; xi < xGrid.Length; xi++)
                        handles16a.Add(AddLine(ms16a, tr16a, new Point3d(xGrid[xi], 0.0, 0), new Point3d(xGrid[xi], 27.0, 0), "E-LEGEND-16A", 7).Handle.ToString());

                    // Short diagram lines inside TYPE column & short sub-divider lines inside RECOMMENDED TYPE (must be filtered by FilterMajorGridAxes)
                    handles16a.Add(AddLine(ms16a, tr16a, new Point3d(128.0, 21.0, 0), new Point3d(131.5, 21.0, 0), "E-LEGEND-16A", 7).Handle.ToString());
                    handles16a.Add(AddLine(ms16a, tr16a, new Point3d(129.8, 20.5, 0), new Point3d(129.8, 23.0, 0), "E-LEGEND-16A", 7).Handle.ToString());
                    handles16a.Add(AddLine(ms16a, tr16a, new Point3d(164.0, 20.8, 0), new Point3d(176.0, 20.8, 0), "E-LEGEND-16A", 7).Handle.ToString());
                    handles16a.Add(AddLine(ms16a, tr16a, new Point3d(164.0, 16.8, 0), new Point3d(176.0, 16.8, 0), "E-LEGEND-16A", 7).Handle.ToString());

                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(120.8, 25.5, 0), "CODE", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(123.5, 25.5, 0), "ICON", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(128.5, 25.5, 0), "TYPE", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(134.0, 25.5, 0), "DESCRIPTION", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(150.0, 25.5, 0), "QTY AS-BUILT", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(155.0, 25.5, 0), "QTY SHOP", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(160.0, 25.5, 0), "QTY BOQ", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(165.5, 25.5, 0), "RECOMMENDED TYPE", 0.30, "E-LEGEND-16A").Handle.ToString());

                    // Row 1 (Y=22): ped post -> 24W
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(121.0, 22.0, 0), "C", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(InsertBlock(ms16a, tr16a, pedBtr, new Point3d(124.7, 22.0, 0), "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(128.0, 22.2, 0), "BOLLARD LIGHTING", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(133.5, 22.0, 0), "LED NOT MORE THAN 24 W, LUMEN NOT LESS THAN 2500 lm", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(150.0, 22.0, 0), "55 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(155.0, 22.0, 0), "55 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(160.0, 22.0, 0), "55 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());

                    // Row 2 (Y=18): upligh9w + "9-12 W LED" -> 9W (prefers matching block-name wattage within range)
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(121.0, 18.0, 0), "E", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(InsertBlock(ms16a, tr16a, up9Btr, new Point3d(124.7, 18.0, 0), "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(128.0, 18.0, 0), "UP LED LIGHTING", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(133.5, 18.0, 0), "FLOOR RECESSED FIXTURE 9-12 W LED IP 67", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(150.0, 18.0, 0), "17 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(155.0, 18.0, 0), "17 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(160.0, 18.0, 0), "17 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());

                    // Row 3 (Y=14): SPOT + incidental SPOTE inside TYPE column -> SPOT=9W, SPOTE NOT extracted
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(121.0, 14.0, 0), "F", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(InsertBlock(ms16a, tr16a, spotBtr, new Point3d(124.7, 14.0, 0), "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(128.0, 14.8, 0), "SPOT LIGHTING", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(InsertBlock(ms16a, tr16a, spoteBtr, new Point3d(129.7, 13.6, 0), "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(133.5, 14.0, 0), "SPIKE FIXTURE 9 W LED WARM COLOR 3000K", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(150.0, 14.0, 0), "244 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(155.0, 14.0, 0), "248 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(160.0, 14.0, 0), "244 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());

                    // Row 4 (Y=10): Sidelight + "3-6W LED" -> 6W (upper bound of range)
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(121.0, 10.0, 0), "G", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(InsertBlock(ms16a, tr16a, sideBtr, new Point3d(124.7, 10.0, 0), "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(128.0, 10.0, 0), "WALL RECESSED LIGHT", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(133.5, 10.0, 0), "WALL RECESSED FIXTURE 3-6W LED IP 65", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(150.0, 10.0, 0), "123 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(155.0, 10.0, 0), "123 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(160.0, 10.0, 0), "111 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());

                    // Row 5 (Y=6): A$Cf9b77d30 -> panel (0W non-load)
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(121.0, 6.0, 0), "Z", 0.30, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(InsertBlock(ms16a, tr16a, panel16Btr, new Point3d(124.7, 6.0, 0), "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(128.0, 6.0, 0), "DB LIGHTING DISTRIBUTION PANEL", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(133.5, 6.0, 0), "OUTDOOR TYPE, WATER PROOF WITH SUN SHADE", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(150.0, 6.0, 0), "6 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(155.0, 6.0, 0), "6 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(160.0, 6.0, 0), "6 PCS", 0.25, "E-LEGEND-16A").Handle.ToString());

                    // Row 6 (Y=2): traneleled -> accessory (0W non-load)
                    handles16a.Add(InsertBlock(ms16a, tr16a, tranBtr, new Point3d(124.7, 2.0, 0), "E-LEGEND-16A").Handle.ToString());
                    handles16a.Add(AddText(ms16a, tr16a, new Point3d(133.5, 2.0, 0), "ELECTRICAL BOX FOR LED TRANSFORMER", 0.25, "E-LEGEND-16A").Handle.ToString());

                    // Drawing blocks + isolated test circuit at Y=-10 to verify strict_legend_specs calc
                    InsertBlock(ms16a, tr16a, pedBtr, new Point3d(126.0, -10.0, 0), "E-LTG-16A-CIRC");
                    InsertBlock(ms16a, tr16a, spotBtr, new Point3d(130.0, -10.0, 0), "E-LTG-16A-CIRC");
                    InsertBlock(ms16a, tr16a, tranBtr, new Point3d(134.0, -10.0, 0), "E-LTG-16A-CIRC");
                    InsertBlock(ms16a, tr16a, up9Btr, new Point3d(140.0, -10.0, 0), "E-LTG-16A-CIRC");
                    InsertBlock(ms16a, tr16a, sideBtr, new Point3d(142.0, -10.0, 0), "E-LTG-16A-CIRC");
                    InsertBlock(ms16a, tr16a, panel16Btr, new Point3d(144.0, -10.0, 0), "E-LTG-16A-CIRC");
                    wire16aCirc = AddLine(ms16a, tr16a, new Point3d(125.0, -10.0, 0), new Point3d(135.0, -10.0, 0), "E-LTG-16A-CIRC", 4).Handle.ToString();
                    tr16a.Commit();
                }

                Dictionary<string, object> r16a = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "manage_project_legend_db",
                    new Dictionary<string, object> { { "action", "extract_from_selection" }, { "handles", handles16a.ToArray() } }
                });
                IList extItems16a = r16a.ContainsKey("extracted_items") ? r16a["extracted_items"] as IList : null;
                AssertTrue(GetBoolVal(r16a, "ok") && extItems16a != null && extItems16a.Count == 6,
                    "Test 16A expected strictly 6 ICON items from 8-column gridded Legend (got " + (extItems16a != null ? extItems16a.Count : 0) + ")");
                double wPed = 0.0, wUp9 = 0.0, wSpot16 = 0.0, wSide = 0.0;
                string catPanel16 = "", catTran16 = "";
                bool sawStale16a = false, sawSpote16a = false;
                foreach (object itemObj in extItems16a)
                {
                    Dictionary<string, object> it = itemObj as Dictionary<string, object>;
                    if (it == null) continue;
                    string bn = GetStrVal(it, "block_name");
                    if (bn == "ped post") wPed = GetDblVal(it, "watt");
                    else if (bn == "upligh9w") wUp9 = GetDblVal(it, "watt");
                    else if (bn == "SPOT") wSpot16 = GetDblVal(it, "watt");
                    else if (bn == "Sidelight") wSide = GetDblVal(it, "watt");
                    else if (bn == "A$Cf9b77d30") catPanel16 = GetStrVal(it, "category");
                    else if (bn == "traneleled") catTran16 = GetStrVal(it, "category");
                    else if (bn == "SPOTE") sawSpote16a = true;
                    else if (bn == "STALE_OLD_BLOCK") sawStale16a = true;
                }
                AssertTrue(!sawStale16a && !sawSpote16a &&
                           Math.Abs(wPed - 24.0) < 0.01 && Math.Abs(wUp9 - 9.0) < 0.01 &&
                           Math.Abs(wSpot16 - 9.0) < 0.01 && Math.Abs(wSide - 6.0) < 0.01 &&
                           catPanel16 == "panel" && catTran16 == "accessory",
                    "Test 16A 8-col Legend mismatch (ped=" + wPed + ", up9=" + wUp9 + ", spot=" + wSpot16 +
                    ", side=" + wSide + ", panelCat=" + catPanel16 + ", tranCat=" + catTran16 + ", spote=" + sawSpote16a + ")");

                Dictionary<string, object> r16aCalc = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object>
                    {
                        { "handles", new string[] { wire16aCirc } },
                        { "circuit_name", "C16A-CALC" },
                        { "include_electrical_calc", true },
                        { "strict_legend_specs", true },
                        { "draw_legend", false }, { "select_blocks", false }, { "select_paths", false }, { "zoom", false }
                    }
                });
                Dictionary<string, object> ec16a = r16aCalc.ContainsKey("electrical_calc") ? r16aCalc["electrical_calc"] as Dictionary<string, object> : null;
                AssertTrue(GetBoolVal(r16aCalc, "ok") && !GetBoolVal(r16aCalc, "needs_user_specs") &&
                           GetStrVal(r16aCalc, "load_source") == "project_legend_db" && ec16a != null &&
                           Math.Abs(GetDblVal(ec16a, "load_kw") - 0.033) < 0.002,
                    "Test 16A electrical calc with Legend DB + 0W accessory failed (load_source=" + GetStrVal(r16aCalc, "load_source") +
                    ", load_kw=" + (ec16a != null ? GetDblVal(ec16a, "load_kw") : -1.0) + ")");
                ed.WriteMessage("\nTEST16A=OK|GRID_EXTRACTED=6|PED=" + wPed + "|UPLIGH9W=" + wUp9 + "|SPOT=" + wSpot16 + "|SIDE=" + wSide + "|CALC_KW=0.033\n");

                // TEST16B: Nearest-competing-wire fixture ownership arbitration
                string wire16bA = "";
                string wire16bB = "";
                using (Transaction tr16b = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt16b = (LayerTable)tr16b.GetObject(db.LayerTableId, OpenMode.ForRead);
                    EnsureLayer(tr16b, lt16b, "E-LTG-16B-A", 1);
                    EnsureLayer(tr16b, lt16b, "E-LTG-16B-B", 3);
                    BlockTable bt16b = (BlockTable)tr16b.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId fix16bBtr = EnsureRectBlock(tr16b, bt16b, "FIX16B", 0.6, 0.6, 7);
                    BlockTableRecord ms16b = (BlockTableRecord)tr16b.GetObject(bt16b[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Fixture 1 owned by Wire A (Wire A passes right through center (183,20))
                    InsertBlock(ms16b, tr16b, fix16bBtr, new Point3d(183.0, 20.0, 0), "E-LTG-16B-A");
                    // Fixture 2 edge touches Wire A tolerance (x=189.1 -> edge 189.4 dist 0.30, center dist 0.90), while Wire B passes dead-center x=190.0
                    InsertBlock(ms16b, tr16b, fix16bBtr, new Point3d(190.0, 20.0, 0), "E-LTG-16B-B");
                    wire16bA = AddLine(ms16b, tr16b, new Point3d(180.0, 20.0, 0), new Point3d(189.1, 20.0, 0), "E-LTG-16B-A", 1).Handle.ToString();
                    wire16bB = AddLine(ms16b, tr16b, new Point3d(190.0, 17.0, 0), new Point3d(190.0, 25.0, 0), "E-LTG-16B-B", 3).Handle.ToString();
                    tr16b.Commit();
                }

                Dictionary<string, object> r16bA = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object>
                    {
                        { "handles", new string[] { wire16bA } },
                        { "draw_legend", false }, { "select_blocks", false }, { "select_paths", false }, { "zoom", false }
                    }
                });
                Dictionary<string, object> r16bB = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object>
                    {
                        { "handles", new string[] { wire16bB } },
                        { "draw_legend", false }, { "select_blocks", false }, { "select_paths", false }, { "zoom", false }
                    }
                });
                AssertTrue(GetBoolVal(r16bA, "ok") && GetIntVal(r16bA, "matched_blocks_count") == 1 &&
                           GetIntVal(r16bA, "skipped_closer_other_wire_count") >= 1 &&
                           GetBoolVal(r16bB, "ok") && GetIntVal(r16bB, "matched_blocks_count") == 1,
                    "Test 16B competing wire arbitration failed (A=" + GetIntVal(r16bA, "matched_blocks_count") +
                    ", skipA=" + GetIntVal(r16bA, "skipped_closer_other_wire_count") + ", B=" + GetIntVal(r16bB, "matched_blocks_count") + ")");
                ed.WriteMessage("\nTEST16B=OK|WIRE_A_MATCHED=1|SKIPPED_CLOSER=" + GetIntVal(r16bA, "skipped_closer_other_wire_count") + "|WIRE_B_MATCHED=1\n");

                // TEST16C: 3-line feeder callout extraction + DB-LCP6(14,16,18) 3-phase propagation into Panel Schedule
                string wire16c = "";
                List<string> calloutHandles16c = new List<string>();
                using (Transaction tr16c = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt16c = (LayerTable)tr16c.GetObject(db.LayerTableId, OpenMode.ForRead);
                    EnsureLayer(tr16c, lt16c, "E-PWR-16C", 6);
                    BlockTable bt16c = (BlockTable)tr16c.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId fix16cBtr = EnsureRectBlock(tr16c, bt16c, "FIX16C", 0.4, 0.4, 6);
                    BlockTableRecord ms16c = (BlockTableRecord)tr16c.GetObject(bt16c[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    InsertBlock(ms16c, tr16c, fix16cBtr, new Point3d(210.0, 5.0, 0), "E-PWR-16C");
                    wire16c = AddLine(ms16c, tr16c, new Point3d(205.0, 5.0, 0), new Point3d(215.0, 5.0, 0), "E-PWR-16C", 6).Handle.ToString();
                    calloutHandles16c.Add(AddText(ms16c, tr16c, new Point3d(206.0, 12.0, 0), "4X10mm2+6MM2 UPVC 50mm class4", 0.25, "E-PWR-16C").Handle.ToString());
                    calloutHandles16c.Add(AddText(ms16c, tr16c, new Point3d(206.0, 11.5, 0), "under F.F.L 60cm DB-LCP6(14,16,18) V.D", 0.25, "E-PWR-16C").Handle.ToString());
                    calloutHandles16c.Add(AddText(ms16c, tr16c, new Point3d(206.0, 11.0, 0), "0.54%", 0.25, "E-PWR-16C").Handle.ToString());
                    tr16c.Commit();
                }

                Dictionary<string, object> r16cSeed = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object>
                    {
                        { "handles", new string[] { wire16c } },
                        { "circuit_name", "TEMP-16C" },
                        { "draw_legend", false }, { "select_blocks", false }, { "select_paths", false }, { "zoom", false }
                    }
                });
                AssertTrue(GetBoolVal(r16cSeed, "ok"), "Test 16C seed trace failed");

                Dictionary<string, object> r16cCallout = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object>
                    {
                        { "handles", calloutHandles16c.ToArray() },
                        { "draw_legend", false }, { "select_blocks", false }, { "select_paths", false }, { "zoom", false }
                    }
                });
                AssertTrue(GetBoolVal(r16cCallout, "ok") && GetBoolVal(r16cCallout, "callout_applied") &&
                           GetStrVal(r16cCallout, "circuit_name") == "DB-LCP6(14,16,18)" &&
                           GetStrVal(r16cCallout, "panel_name") == "DB-LCP6" &&
                           GetIntVal(r16cCallout, "panel_ways_count") == 3 &&
                           GetIntVal(r16cCallout, "cable_cores_count") == 4 &&
                           GetIntVal(r16cCallout, "total_conductors_count") == 5 &&
                           GetStrVal(r16cCallout, "conduit_spec").ToUpperInvariant().Contains("UPVC") &&
                           Math.Abs(GetDblVal(r16cCallout, "annotated_vd_percent") - 0.54) < 0.01,
                    "Test 16C callout extraction/application failed");

                Dictionary<string, object> r16cPanel = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "generate_panel_schedule_and_sld",
                    new Dictionary<string, object>
                    {
                        { "source", "saved_memory" },
                        { "strict_legend_specs", false },
                        { "draw_panel_schedule", false },
                        { "draw_sld", false },
                        { "export_excel", false }
                    }
                });
                bool foundLcp6Way = false;
                IList panels16c = r16cPanel.ContainsKey("panels") ? r16cPanel["panels"] as IList : null;
                if (panels16c != null)
                {
                    foreach (object pObj in panels16c)
                    {
                        Dictionary<string, object> pd = pObj as Dictionary<string, object>;
                        if (pd == null || GetStrVal(pd, "panel_name") != "DB-LCP6") continue;
                        IList cList = pd.ContainsKey("circuits") ? pd["circuits"] as IList : null;
                        if (cList == null) continue;
                        foreach (object cObj in cList)
                        {
                            Dictionary<string, object> cd = cObj as Dictionary<string, object>;
                            if (cd != null && GetStrVal(cd, "circuit_name") == "DB-LCP6(14,16,18)" &&
                                GetStrVal(cd, "phase") == "RYB" &&
                                GetStrVal(cd, "cable").ToUpperInvariant().Contains("4X10+6MM2"))
                            {
                                foundLcp6Way = true;
                            }
                        }
                    }
                }
                AssertTrue(GetBoolVal(r16cPanel, "ok") && foundLcp6Way, "Test 16C panel schedule did not preserve DB-LCP6 3-phase way & annotated cable");
                ed.WriteMessage("\nTEST16C=OK|CIRC=DB-LCP6(14,16,18)|CORES=4|COND=5|VD=0.54\n");

                // TEST16D: lock_selection_as_circuit exact subset lock without wire blowup
                string wire16dA = "", wire16dB = "", blk16d1 = "", blk16d2 = "";
                using (Transaction tr16d = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt16d = (LayerTable)tr16d.GetObject(db.LayerTableId, OpenMode.ForRead);
                    EnsureLayer(tr16d, lt16d, "E-LTG-16D", 5);
                    BlockTable bt16d = (BlockTable)tr16d.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId fix16dBtr = EnsureRectBlock(tr16d, bt16d, "FIX16D", 0.35, 0.35, 5);
                    BlockTableRecord ms16d = (BlockTableRecord)tr16d.GetObject(bt16d[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    blk16d1 = InsertBlock(ms16d, tr16d, fix16dBtr, new Point3d(227.0, 5.0, 0), "E-LTG-16D").Handle.ToString();
                    blk16d2 = InsertBlock(ms16d, tr16d, fix16dBtr, new Point3d(232.0, 5.0, 0), "E-LTG-16D").Handle.ToString();
                    InsertBlock(ms16d, tr16d, fix16dBtr, new Point3d(234.5, 5.0, 0), "E-LTG-16D");
                    wire16dA = AddLine(ms16d, tr16d, new Point3d(225.0, 5.0, 0), new Point3d(230.0, 5.0, 0), "E-LTG-16D", 5).Handle.ToString();
                    wire16dB = AddLine(ms16d, tr16d, new Point3d(230.0, 5.0, 0), new Point3d(235.0, 5.0, 0), "E-LTG-16D", 5).Handle.ToString();
                    AddLine(ms16d, tr16d, new Point3d(235.15, 5.0, 0), new Point3d(265.0, 5.0, 0), "E-LTG-16D", 5);
                    tr16d.Commit();
                }

                Dictionary<string, object> r16dTrace = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object>
                    {
                        { "handles", new string[] { wire16dA, wire16dB } },
                        { "circuit_name", "C16D-LOCK" },
                        { "connect_gaps", false },
                        { "follow_connected", true },
                        { "draw_legend", false }, { "select_blocks", false }, { "select_paths", false }, { "zoom", false }
                    }
                });
                AssertTrue(GetBoolVal(r16dTrace, "ok") && GetIntVal(r16dTrace, "matched_blocks_count") == 3 &&
                           Math.Abs(GetDblVal(r16dTrace, "total_path_length_m") - 10.0) < 0.1,
                    "Test 16D initial trace failed");

                Dictionary<string, object> r16dLock = (Dictionary<string, object>)execTool.Invoke(null, new object[] {
                    doc, "find_blocks_along_path",
                    new Dictionary<string, object>
                    {
                        { "circuit_name", "C16D-LOCK" },
                        { "lock_selection_as_circuit", true },
                        { "handles", new string[] { blk16d1, blk16d2 } },
                        { "draw_legend", false }, { "select_blocks", false }, { "select_paths", false }, { "zoom", false }
                    }
                });
                AssertTrue(GetBoolVal(r16dLock, "ok") && GetBoolVal(r16dLock, "user_locked") &&
                           GetIntVal(r16dLock, "matched_blocks_count") == 2 &&
                           Math.Abs(GetDblVal(r16dLock, "total_path_length_m") - 10.0) < 0.1,
                    "Test 16D exact lock failed (matched=" + GetIntVal(r16dLock, "matched_blocks_count") +
                    ", len=" + GetDblVal(r16dLock, "total_path_length_m") + ")");
                ed.WriteMessage("\nTEST16D=OK|LOCKED_BLOCKS=2|LEN_M=10\n");

                ed.WriteMessage("\nACCORE_REGRESSION_PASS\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nACCORE_REGRESSION_FAIL:" + ex.ToString() + "\n");
            }
        }
    }
}
