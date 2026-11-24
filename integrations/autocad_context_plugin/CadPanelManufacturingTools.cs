using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

[assembly: CommandClass(typeof(AIEngineer.AutoCAD.PanelManufacturingCommands))]

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        // ==================== Phase 5.1: Dynamic Zero-Collision 2D/3D Panel Manufacturing Engine ====================

        private const string PanelCompRegApp = "AIENG_PANEL_COMP";
        private const string PanelBusbarRegApp = "AIENG_PANEL_BUSBAR";
        private const string PanelNetRegApp = "AIENG_PANEL_NET";
        private const string PanelRailRegApp = "AIENG_PANEL_RAIL";
        private const string PanelSldRegApp = "AIENG_PANEL_SLD";

        // 2D Engineering, Side Cross-Section, CNC & Interactive Trace Layers
        private const string LayerPanel2D = "_AI_PANEL_2D_LAYOUT";
        private const string LayerPanelSideSection = "_AI_PANEL_SIDE_SECTION";
        private const string LayerPanelCncDrilling = "_AI_CNC_DRILLING";
        private const string LayerPanelGuideAlign = "_AI_GUIDE_ALIGNMENT";
        private const string LayerPanelTraceHighlight = "_AI_PANEL_TRACE_HIGHLIGHT";
        private const string LayerPanelBomTable = "_AI_PANEL_BOM_TABLE";

        // 8 Ordered Physical Z-Depth 3D Layers (Back -> Front, toggleable individually or via presets)
        private const string LayerPanel3D00Enclosure = "_AI_3D_00_ENCLOSURE";
        private const string LayerPanel3D01MountingPlate = "_AI_3D_01_MOUNTING_PLATE";
        private const string LayerPanel3D02RailsInsulators = "_AI_3D_02_RAILS_INSULATORS";
        private const string LayerPanel3D03DuctsBody = "_AI_3D_03_DUCTS_BODY";
        private const string LayerPanel3D03DuctsCover = "_AI_3D_03_DUCTS_COVER";
        private const string LayerPanel3D04Components = "_AI_3D_04_COMPONENTS";
        private const string LayerPanel3D05BusbarsCopper = "_AI_3D_05_BUSBARS_COPPER";
        private const string LayerPanel3D06Wiring = "_AI_3D_06_WIRING";
        private const string LayerPanel3D07DoorDeadFront = "_AI_3D_07_DOOR_DEADFRONT";

        internal sealed class SwitchgearPartSpec
        {
            public string PartNumber = "";
            public string Manufacturer = "Schneider Electric";
            public string Series = "";
            public string Category = "mcb";
            public int Poles = 1;
            public double RatedAmps = 16.0;
            public double IcuKa = 6.0;
            public double WidthMm = 18.0;
            public double HeightMm = 85.0;
            public double DepthMm = 78.5;
            public bool IsDinRail = true;
            public double HeatLossWatts = 2.5;
            public double ArcClearanceTopMm = 35.0;
            public double MinCableBendMm = 60.0;
            public double[] LineTermOffsetMm = new double[] { 9.0, 77.0, 24.0 };
            public double[] LoadTermOffsetMm = new double[] { 9.0, 8.0, 24.0 };
        }

        internal sealed class CopperBusbarSpec
        {
            public string Designation = "20x5 Cu";
            public double WidthMm = 20.0;
            public double ThicknessMm = 5.0;
            public int Runs = 1;
            public double MaxAmps35K = 270.0;
            public double ResistanceMohmPerM = 0.193;
        }

        internal sealed class EnclosureSpec
        {
            public string PartNumber = "NSYCRN86250";
            public string Series = "Schneider Spacial CRN";
            public string EnclosureType = "wall_mount";
            public double WidthMm = 600.0;
            public double HeightMm = 800.0;
            public double DepthMm = 250.0;
        }

        internal sealed class CoolingFanSpec
        {
            public string PartNumber = "NSYCVF38M230PF";
            public string Series = "ClimaSys Forced Ventilation 38 m3/h";
            public double AirflowM3h = 38.0;
            public double WidthMm = 137.0;
            public double HeightMm = 137.0;
            public double DepthMm = 66.0;
        }

        internal sealed class SwitchgearCatalogData
        {
            public List<SwitchgearPartSpec> Components = new List<SwitchgearPartSpec>();
            public List<CopperBusbarSpec> Busbars = new List<CopperBusbarSpec>();
            public List<EnclosureSpec> Enclosures = new List<EnclosureSpec>();
            public List<CoolingFanSpec> Fans = new List<CoolingFanSpec>();
        }

        internal sealed class PlacedPanelComponent
        {
            public string Tag = "";
            public SwitchgearPartSpec Part = new SwitchgearPartSpec();
            public int Qty = 1;
            public string CircuitName = "";
            public string Phase = "R";
            public double CableMm2 = 2.5;
            public double XMm;
            public double YMm;
            public double ZMm = 10.5;
            public int RailIndex;
            public string Zone = "distribution_din";
            public string FromTerminal = "1/L1";
            public string ToTerminal = "2/T1";
            public string WireNo = "";
            public string VaultSource = "parametric_3d";
            public string VaultPath = "";
            public string TargetTerminalTag = "";
            public string ConnectedToTag = "";
            public bool IsFedFromUpstream;
            public bool HasManualCoords;
        }

        internal sealed class PanelBusbarRun
        {
            public string Label = "R";
            public string Designation = "20x5 Cu";
            public short ColorIndex = 1;
            public double WidthMm = 20.0;
            public double ThicknessMm = 5.0;
            public int Runs = 1;
            public bool IsDropper;
            public bool IsComb;
            public List<double[]> PointsMm = new List<double[]>();
            public List<double[]> BoltJointsMm = new List<double[]>();
        }

        internal sealed class PanelWireConnection
        {
            public string WireNo = "";
            public string FromTag = "";
            public string FromTerm = "";
            public string ToTag = "";
            public string ToTerm = "";
            public double SizeMm2 = 2.5;
            public string Phase = "R";
            public string ColorName = "Red";
            public short ColorIndex = 1;
            public double LengthMm;
            public string FerruleLabel = "";
            public List<double[]> PointsMm = new List<double[]>();
        }

        internal sealed class CncDrillHole
        {
            public double XMm;
            public double YMm;
            public double DiameterMm = 5.5;
            public string Purpose = "DIN Rail M5";
        }

        internal sealed class PanelManufacturingPlan
        {
            public string PanelName = "MDB-01";
            public string Manufacturer = "Schneider Electric";
            public string CableEntry = "bottom";
            public double AmbientTempC = 35.0;
            public double MaxInternalTempC = 55.0;
            public double TotalLoadKva = 10.0;
            public double IncomerAmps = 63.0;
            public double FaultKa = 25.0;
            public bool UseCombBusbar = true;
            public CopperBusbarSpec MainBusbar = new CopperBusbarSpec();
            public CopperBusbarSpec NeutralBusbar = new CopperBusbarSpec();
            public CopperBusbarSpec PeBusbar = new CopperBusbarSpec();
            public EnclosureSpec Enclosure = new EnclosureSpec();
            public double TotalHeatLossDevicesW;
            public double TotalHeatLossBusbarW;
            public double TotalHeatLossW;
            public double EffectiveSurfaceM2;
            public double TempRiseC;
            public double InternalTopTempC;
            public bool NaturalCoolingOk = true;
            public double RequiredAirflowM3h;
            public CoolingFanSpec SelectedFan;
            public double SpareSpacePercent;
            public int BusbarInsulatorCount = 3;
            public double MinDoorClearanceMm = 45.0;
            public double MaxDuctFillPercent;
            public int CollisionCount;
            public bool ZeroCollisionVerified = true;
            public List<PlacedPanelComponent> Components = new List<PlacedPanelComponent>();
            public List<PanelBusbarRun> Busbars = new List<PanelBusbarRun>();
            public List<double[]> DinRailsMm = new List<double[]>();
            public List<double[]> WireDuctsMm = new List<double[]>();
            public List<PanelWireConnection> WireConnections = new List<PanelWireConnection>();
            public List<CncDrillHole> CncHoles = new List<CncDrillHole>();
            public List<string> Warnings = new List<string>();
        }

        private static readonly object s_catalogLock = new object();
        private static SwitchgearCatalogData s_cachedCatalog;

        private static string TryLocateSwitchgearCatalogPath()
        {
            try
            {
                string envRoot = Environment.GetEnvironmentVariable("AI_ENGINEER_ROOT");
                if (!string.IsNullOrWhiteSpace(envRoot))
                {
                    string cand = Path.Combine(Path.Combine(envRoot.Trim(), "data"), "switchgear_3d_catalog.json");
                    if (File.Exists(cand)) return cand;
                }
                string asmDir = Path.GetDirectoryName(typeof(CadToolExecutor).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    DirectoryInfo cur = new DirectoryInfo(asmDir);
                    for (int i = 0; cur != null && i < 8; i++)
                    {
                        string cand = Path.Combine(Path.Combine(cur.FullName, "data"), "switchgear_3d_catalog.json");
                        if (File.Exists(cand)) return cand;
                        cur = cur.Parent;
                    }
                }
            }
            catch { }
            return null;
        }

        private static SwitchgearPartSpec MakePart(
            string pn, string mfg, string series, string cat, int poles, double amps, double icu,
            double w, double h, double d, bool din, double watts, double arcTop, double bend)
        {
            return new SwitchgearPartSpec
            {
                PartNumber = pn,
                Manufacturer = mfg,
                Series = series,
                Category = cat,
                Poles = poles,
                RatedAmps = amps,
                IcuKa = icu,
                WidthMm = w,
                HeightMm = h,
                DepthMm = d,
                IsDinRail = din,
                HeatLossWatts = watts,
                ArcClearanceTopMm = arcTop,
                MinCableBendMm = bend,
                LineTermOffsetMm = new double[] { w * 0.5, h * 0.90, Math.Min(d * 0.45, 42.0) },
                LoadTermOffsetMm = new double[] { w * 0.5, h * 0.10, Math.Min(d * 0.45, 42.0) }
            };
        }

        private static SwitchgearCatalogData BuildDefaultCatalog()
        {
            SwitchgearCatalogData cat = new SwitchgearCatalogData();
            cat.Components.AddRange(new SwitchgearPartSpec[]
            {
                MakePart("LV429630", "Schneider Electric", "ComPact NSX100F TM100D", "mccb", 3, 100, 36, 105, 161, 86, false, 19.5, 60, 150),
                MakePart("LV431630", "Schneider Electric", "ComPact NSX250F TM250D", "mccb", 3, 250, 36, 105, 161, 86, false, 38.0, 80, 220),
                MakePart("LV432693", "Schneider Electric", "ComPact NSX400F MicroLogic 2.3", "mccb", 3, 400, 36, 140, 255, 110, false, 54.0, 100, 300),
                MakePart("NW08H1", "Schneider Electric", "MasterPact NW08 H1", "acb", 3, 800, 65, 441, 352, 297, false, 95.0, 120, 380),
                MakePart("A9F74110", "Schneider Electric", "Acti9 iC60N C10", "mcb", 1, 10, 6, 18, 85, 78.5, true, 1.8, 35, 50),
                MakePart("A9F74116", "Schneider Electric", "Acti9 iC60N C16", "mcb", 1, 16, 6, 18, 85, 78.5, true, 2.3, 35, 50),
                MakePart("A9F74120", "Schneider Electric", "Acti9 iC60N C20", "mcb", 1, 20, 6, 18, 85, 78.5, true, 2.8, 35, 50),
                MakePart("A9F74132", "Schneider Electric", "Acti9 iC60N C32", "mcb", 1, 32, 6, 18, 85, 78.5, true, 3.6, 35, 60),
                MakePart("A9F74316", "Schneider Electric", "Acti9 iC60N 3P C16", "mcb", 3, 16, 6, 54, 85, 78.5, true, 6.6, 35, 60),
                MakePart("A9F74325", "Schneider Electric", "Acti9 iC60N 3P C25", "mcb", 3, 25, 6, 54, 85, 78.5, true, 8.4, 35, 70),
                MakePart("A9F74332", "Schneider Electric", "Acti9 iC60N 3P C32", "mcb", 3, 32, 6, 54, 85, 78.5, true, 9.8, 35, 75),
                MakePart("A9F74340", "Schneider Electric", "Acti9 iC60N 3P C40", "mcb", 3, 40, 6, 54, 85, 78.5, true, 11.1, 40, 80),
                MakePart("A9F74363", "Schneider Electric", "Acti9 iC60N 3P C63", "mcb", 3, 63, 6, 54, 85, 78.5, true, 14.4, 40, 90),
                MakePart("LC1D18M7", "Schneider Electric", "TeSys D LC1D18 18A 220VAC", "contactor", 3, 18, 6, 45, 77, 86, true, 4.2, 25, 60),
                MakePart("LC1D32M7", "Schneider Electric", "TeSys D LC1D32 32A 220VAC", "contactor", 3, 32, 6, 45, 85, 92, true, 6.8, 25, 70),
                MakePart("ATV320U22N4C", "Schneider Electric", "Altivar 320 2.2kW 3P 380V VFD", "vfd", 3, 5.6, 10, 105, 143, 158, true, 82.0, 100, 100),
                MakePart("ABL8REM24050", "Schneider Electric", "Phaseo 24VDC 5A Power Supply", "psu", 1, 5, 6, 54, 120, 120, true, 18.0, 50, 60),
                MakePart("NSYTRV42", "Schneider Electric", "Linergy TR 4mm2 Screw Terminal", "terminal", 1, 32, 6, 6.2, 47.7, 47.5, true, 0.4, 10, 40),
                MakePart("NSYTRV162", "Schneider Electric", "Linergy TR 16mm2 Screw Terminal", "terminal", 1, 76, 6, 12.2, 55.5, 54.0, true, 0.9, 10, 50),
                MakePart("XT1N160", "ABB", "Tmax XT1N 160 TMD", "mccb", 3, 160, 36, 76.2, 130, 70, false, 26.0, 75, 180),
                MakePart("S201-C16", "ABB", "System pro M S201 C16", "mcb", 1, 16, 6, 17.5, 88, 69, true, 2.5, 35, 50),
                MakePart("S203-C32", "ABB", "System pro M S203 C32", "mcb", 3, 32, 6, 52.5, 88, 69, true, 9.6, 35, 70),
                MakePart("3VA1110", "Siemens", "SENTRON 3VA1110 100A", "mccb", 3, 100, 36, 76.2, 130, 70, false, 21.0, 60, 160),
                MakePart("5SY4116-7", "Siemens", "SENTRON 5SY4 1P C16", "mcb", 1, 16, 10, 18, 90, 70, true, 2.1, 35, 50),
                MakePart("5SY4332-7", "Siemens", "SENTRON 5SY4 3P C32", "mcb", 3, 32, 10, 54, 90, 70, true, 8.7, 35, 70)
            });
            cat.Busbars.AddRange(new CopperBusbarSpec[]
            {
                new CopperBusbarSpec { Designation = "15x3 Cu", WidthMm = 15, ThicknessMm = 3, Runs = 1, MaxAmps35K = 155, ResistanceMohmPerM = 0.430 },
                new CopperBusbarSpec { Designation = "20x3 Cu", WidthMm = 20, ThicknessMm = 3, Runs = 1, MaxAmps35K = 200, ResistanceMohmPerM = 0.320 },
                new CopperBusbarSpec { Designation = "20x5 Cu", WidthMm = 20, ThicknessMm = 5, Runs = 1, MaxAmps35K = 270, ResistanceMohmPerM = 0.193 },
                new CopperBusbarSpec { Designation = "25x5 Cu", WidthMm = 25, ThicknessMm = 5, Runs = 1, MaxAmps35K = 325, ResistanceMohmPerM = 0.155 },
                new CopperBusbarSpec { Designation = "30x5 Cu", WidthMm = 30, ThicknessMm = 5, Runs = 1, MaxAmps35K = 380, ResistanceMohmPerM = 0.129 },
                new CopperBusbarSpec { Designation = "30x10 Cu", WidthMm = 30, ThicknessMm = 10, Runs = 1, MaxAmps35K = 550, ResistanceMohmPerM = 0.065 },
                new CopperBusbarSpec { Designation = "40x10 Cu", WidthMm = 40, ThicknessMm = 10, Runs = 1, MaxAmps35K = 700, ResistanceMohmPerM = 0.049 },
                new CopperBusbarSpec { Designation = "50x10 Cu", WidthMm = 50, ThicknessMm = 10, Runs = 1, MaxAmps35K = 850, ResistanceMohmPerM = 0.039 },
                new CopperBusbarSpec { Designation = "60x10 Cu", WidthMm = 60, ThicknessMm = 10, Runs = 1, MaxAmps35K = 1000, ResistanceMohmPerM = 0.033 },
                new CopperBusbarSpec { Designation = "80x10 Cu", WidthMm = 80, ThicknessMm = 10, Runs = 1, MaxAmps35K = 1250, ResistanceMohmPerM = 0.025 },
                new CopperBusbarSpec { Designation = "2x(60x10) Cu", WidthMm = 60, ThicknessMm = 10, Runs = 2, MaxAmps35K = 1700, ResistanceMohmPerM = 0.017 },
                new CopperBusbarSpec { Designation = "2x(80x10) Cu", WidthMm = 80, ThicknessMm = 10, Runs = 2, MaxAmps35K = 2150, ResistanceMohmPerM = 0.013 }
            });
            cat.Enclosures.AddRange(new EnclosureSpec[]
            {
                new EnclosureSpec { PartNumber = "NSYCRN64200", Series = "Schneider Spacial CRN", EnclosureType = "wall_mount", WidthMm = 400, HeightMm = 600, DepthMm = 200 },
                new EnclosureSpec { PartNumber = "NSYCRN86250", Series = "Schneider Spacial CRN", EnclosureType = "wall_mount", WidthMm = 600, HeightMm = 800, DepthMm = 250 },
                new EnclosureSpec { PartNumber = "NSYCRN106250", Series = "Schneider Spacial CRN", EnclosureType = "wall_mount", WidthMm = 600, HeightMm = 1000, DepthMm = 250 },
                new EnclosureSpec { PartNumber = "NSYCRN108300", Series = "Schneider Spacial CRN", EnclosureType = "wall_mount", WidthMm = 800, HeightMm = 1000, DepthMm = 300 },
                new EnclosureSpec { PartNumber = "AE1280.500", Series = "Rittal AE Compact", EnclosureType = "wall_mount", WidthMm = 800, HeightMm = 1200, DepthMm = 300 },
                new EnclosureSpec { PartNumber = "NSYSF16840", Series = "Schneider Spacial SF", EnclosureType = "floor_standing", WidthMm = 800, HeightMm = 1600, DepthMm = 400 },
                new EnclosureSpec { PartNumber = "NSYSF18840", Series = "Schneider Spacial SF", EnclosureType = "floor_standing", WidthMm = 800, HeightMm = 1800, DepthMm = 400 },
                new EnclosureSpec { PartNumber = "VX8805.000", Series = "Rittal VX25 Baying", EnclosureType = "floor_standing", WidthMm = 800, HeightMm = 2000, DepthMm = 500 },
                new EnclosureSpec { PartNumber = "VX8005.000", Series = "Rittal VX25 Baying", EnclosureType = "floor_standing", WidthMm = 1000, HeightMm = 2000, DepthMm = 500 }
            });
            cat.Fans.AddRange(new CoolingFanSpec[]
            {
                new CoolingFanSpec { PartNumber = "NSYCVF38M230PF", Series = "ClimaSys Forced Ventilation 38 m3/h", AirflowM3h = 38, WidthMm = 137, HeightMm = 137, DepthMm = 66 },
                new CoolingFanSpec { PartNumber = "NSYCVF85M230PF", Series = "ClimaSys Forced Ventilation 85 m3/h", AirflowM3h = 85, WidthMm = 170, HeightMm = 170, DepthMm = 82 },
                new CoolingFanSpec { PartNumber = "NSYCVF165M230PF", Series = "ClimaSys Forced Ventilation 165 m3/h", AirflowM3h = 165, WidthMm = 268, HeightMm = 248, DepthMm = 103 },
                new CoolingFanSpec { PartNumber = "NSYCVF300M230PF", Series = "ClimaSys Forced Ventilation 300 m3/h", AirflowM3h = 300, WidthMm = 268, HeightMm = 248, DepthMm = 116 }
            });
            return cat;
        }

        internal static SwitchgearCatalogData ResolveSwitchgearCatalog()
        {
            lock (s_catalogLock)
            {
                if (s_cachedCatalog != null) return s_cachedCatalog;
                SwitchgearCatalogData fallback = BuildDefaultCatalog();
                try
                {
                    string jsonPath = TryLocateSwitchgearCatalogPath();
                    if (!string.IsNullOrEmpty(jsonPath) && File.Exists(jsonPath))
                    {
                        string raw = File.ReadAllText(jsonPath, Encoding.UTF8);
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        Dictionary<string, object> root = ser.DeserializeObject(raw) as Dictionary<string, object>;
                        if (root != null)
                        {
                            SwitchgearCatalogData parsed = new SwitchgearCatalogData();
                            IEnumerable compsEn = root.ContainsKey("components") ? root["components"] as IEnumerable : null;
                            if (compsEn != null)
                            {
                                foreach (object o in compsEn)
                                {
                                    Dictionary<string, object> d = o as Dictionary<string, object>;
                                    if (d == null) continue;
                                    double[] lOff = GetDoubleArray(d, "line_terminal_offset_mm");
                                    double[] oOff = GetDoubleArray(d, "load_terminal_offset_mm");
                                    double w = Math.Max(5.0, GetDouble(d, "width_mm", 18.0));
                                    double h = Math.Max(15.0, GetDouble(d, "height_mm", 85.0));
                                    double dp = Math.Max(15.0, GetDouble(d, "depth_mm", 78.5));
                                    parsed.Components.Add(new SwitchgearPartSpec
                                    {
                                        PartNumber = (GetStr(d, "part_number") ?? "").Trim(),
                                        Manufacturer = GetStr(d, "manufacturer") ?? "Schneider Electric",
                                        Series = GetStr(d, "series") ?? "",
                                        Category = (GetStr(d, "category") ?? "mcb").Trim().ToLowerInvariant(),
                                        Poles = Math.Max(1, GetInt(d, "poles", 1)),
                                        RatedAmps = Math.Max(1.0, GetDouble(d, "rated_amps", 16.0)),
                                        IcuKa = Math.Max(3.0, GetDouble(d, "icu_ka", 6.0)),
                                        WidthMm = w,
                                        HeightMm = h,
                                        DepthMm = dp,
                                        IsDinRail = GetBool(d, "din_rail", true),
                                        HeatLossWatts = Math.Max(0.1, GetDouble(d, "heat_loss_watts", 2.0)),
                                        ArcClearanceTopMm = Math.Max(10.0, GetDouble(d, "arc_clearance_top_mm", 35.0)),
                                        MinCableBendMm = Math.Max(30.0, GetDouble(d, "min_cable_bend_mm", 60.0)),
                                        LineTermOffsetMm = (lOff != null && lOff.Length >= 3) ? lOff : new double[] { w * 0.5, h * 0.90, Math.Min(dp * 0.45, 42.0) },
                                        LoadTermOffsetMm = (oOff != null && oOff.Length >= 3) ? oOff : new double[] { w * 0.5, h * 0.10, Math.Min(dp * 0.45, 42.0) }
                                    });
                                }
                            }
                            IEnumerable barsEn = root.ContainsKey("copper_busbars") ? root["copper_busbars"] as IEnumerable : null;
                            if (barsEn != null)
                            {
                                foreach (object o in barsEn)
                                {
                                    Dictionary<string, object> d = o as Dictionary<string, object>;
                                    if (d == null) continue;
                                    parsed.Busbars.Add(new CopperBusbarSpec
                                    {
                                        Designation = GetStr(d, "designation") ?? "20x5 Cu",
                                        WidthMm = GetDouble(d, "width_mm", 20.0),
                                        ThicknessMm = GetDouble(d, "thickness_mm", 5.0),
                                        Runs = Math.Max(1, GetInt(d, "runs", 1)),
                                        MaxAmps35K = GetDouble(d, "max_amps_35k", 270.0),
                                        ResistanceMohmPerM = GetDouble(d, "resistance_mohm_per_m", 0.193)
                                    });
                                }
                            }
                            IEnumerable encEn = root.ContainsKey("enclosures") ? root["enclosures"] as IEnumerable : null;
                            if (encEn != null)
                            {
                                foreach (object o in encEn)
                                {
                                    Dictionary<string, object> d = o as Dictionary<string, object>;
                                    if (d == null) continue;
                                    parsed.Enclosures.Add(new EnclosureSpec
                                    {
                                        PartNumber = GetStr(d, "part_number") ?? "NSYCRN86250",
                                        Series = GetStr(d, "series") ?? "Spacial",
                                        EnclosureType = GetStr(d, "type") ?? "wall_mount",
                                        WidthMm = GetDouble(d, "width_mm", 600.0),
                                        HeightMm = GetDouble(d, "height_mm", 800.0),
                                        DepthMm = GetDouble(d, "depth_mm", 250.0)
                                    });
                                }
                            }
                            IEnumerable fanEn = root.ContainsKey("cooling_fans") ? root["cooling_fans"] as IEnumerable : null;
                            if (fanEn != null)
                            {
                                foreach (object o in fanEn)
                                {
                                    Dictionary<string, object> d = o as Dictionary<string, object>;
                                    if (d == null) continue;
                                    parsed.Fans.Add(new CoolingFanSpec
                                    {
                                        PartNumber = GetStr(d, "part_number") ?? "NSYCVF38M230PF",
                                        Series = GetStr(d, "series") ?? "ClimaSys",
                                        AirflowM3h = GetDouble(d, "airflow_m3h", 38.0),
                                        WidthMm = GetDouble(d, "width_mm", 137.0),
                                        HeightMm = GetDouble(d, "height_mm", 137.0),
                                        DepthMm = GetDouble(d, "depth_mm", 66.0)
                                    });
                                }
                            }
                            if (parsed.Components.Count > 0 && parsed.Busbars.Count > 0 && parsed.Enclosures.Count > 0 && parsed.Fans.Count > 0)
                            {
                                s_cachedCatalog = parsed;
                                return s_cachedCatalog;
                            }
                        }
                    }
                }
                catch { }
                s_cachedCatalog = fallback;
                return s_cachedCatalog;
            }
        }

        private static SwitchgearPartSpec ClonePartSpec(SwitchgearPartSpec src)
        {
            return new SwitchgearPartSpec
            {
                PartNumber = src.PartNumber ?? "",
                Manufacturer = src.Manufacturer ?? "Schneider Electric",
                Series = src.Series ?? "",
                Category = src.Category ?? "mcb",
                Poles = src.Poles,
                RatedAmps = src.RatedAmps,
                IcuKa = src.IcuKa,
                WidthMm = src.WidthMm,
                HeightMm = src.HeightMm,
                DepthMm = src.DepthMm,
                IsDinRail = src.IsDinRail,
                HeatLossWatts = src.HeatLossWatts,
                ArcClearanceTopMm = src.ArcClearanceTopMm,
                MinCableBendMm = src.MinCableBendMm,
                LineTermOffsetMm = new double[] { src.LineTermOffsetMm[0], src.LineTermOffsetMm[1], src.LineTermOffsetMm[2] },
                LoadTermOffsetMm = new double[] { src.LoadTermOffsetMm[0], src.LoadTermOffsetMm[1], src.LoadTermOffsetMm[2] }
            };
        }

        internal static SwitchgearPartSpec ResolvePartFromCatalog(
            SwitchgearCatalogData catalog,
            string partNumber,
            string manufacturer,
            string category,
            int poles,
            double amps,
            double icuKa,
            double? customWidthMm,
            double? customHeightMm,
            double? customDepthMm,
            double? customHeatW)
        {
            SwitchgearCatalogData cat = catalog ?? ResolveSwitchgearCatalog();
            string pn = (partNumber ?? "").Trim();
            if (!string.IsNullOrEmpty(pn))
            {
                for (int i = 0; i < cat.Components.Count; i++)
                {
                    if (string.Equals(cat.Components[i].PartNumber, pn, StringComparison.OrdinalIgnoreCase))
                    {
                        SwitchgearPartSpec matched = ClonePartSpec(cat.Components[i]);
                        if (customWidthMm.HasValue && customWidthMm.Value > 3.0) matched.WidthMm = customWidthMm.Value;
                        if (customHeightMm.HasValue && customHeightMm.Value > 10.0) matched.HeightMm = customHeightMm.Value;
                        if (customDepthMm.HasValue && customDepthMm.Value > 10.0) matched.DepthMm = customDepthMm.Value;
                        if (customHeatW.HasValue && customHeatW.Value >= 0.0) matched.HeatLossWatts = customHeatW.Value;
                        return matched;
                    }
                }
                int safePoles = poles >= 3 ? 3 : 1;
                string safeCat = string.IsNullOrWhiteSpace(category) ? (amps > 63.0 ? "mccb" : "mcb") : category.Trim().ToLowerInvariant();
                double cw = (customWidthMm.HasValue && customWidthMm.Value > 3.0) ? customWidthMm.Value : (safeCat == "vfd" ? 105.0 : (safeCat == "mccb" ? 105.0 : (safePoles * 18.0)));
                double ch = (customHeightMm.HasValue && customHeightMm.Value > 10.0) ? customHeightMm.Value : (safeCat == "vfd" ? 145.0 : (safeCat == "mccb" ? 161.0 : 85.0));
                double cd = (customDepthMm.HasValue && customDepthMm.Value > 10.0) ? customDepthMm.Value : (safeCat == "vfd" ? 155.0 : (safeCat == "mccb" ? 86.0 : 78.5));
                double chw = (customHeatW.HasValue && customHeatW.Value >= 0.0) ? customHeatW.Value : Math.Round(Math.Max(1.8, amps * 0.14 * safePoles), 1);
                return MakePart(
                    pn,
                    string.IsNullOrWhiteSpace(manufacturer) ? "Custom / TraceParts" : manufacturer.Trim(),
                    "Parametric Part (" + pn + ")",
                    safeCat,
                    safePoles,
                    Math.Max(1.0, amps),
                    Math.Max(6.0, icuKa),
                    cw, ch, cd,
                    safeCat != "mccb" && safeCat != "acb",
                    chw,
                    safeCat == "mccb" || safeCat == "acb" || safeCat == "vfd" ? 80.0 : 35.0,
                    safeCat == "mccb" || safeCat == "acb" ? 180.0 : 60.0);
            }

            string normMfg = string.IsNullOrWhiteSpace(manufacturer) ? "Schneider Electric" : manufacturer.Trim();
            string normCat = string.IsNullOrWhiteSpace(category) ? (amps > 63.0 ? "mccb" : "mcb") : category.Trim().ToLowerInvariant();
            if (normCat == "inverter" || normCat == "drive") normCat = "vfd";
            if (normCat == "incomer") normCat = amps >= 630.0 ? "acb" : (amps > 63.0 ? "mccb" : "mcb");

            SwitchgearPartSpec best = null;
            for (int pass = 0; pass < 3 && best == null; pass++)
            {
                bool matchMfg = pass == 0;
                bool matchAmps = pass <= 1;
                double bestScore = double.MaxValue;
                for (int i = 0; i < cat.Components.Count; i++)
                {
                    SwitchgearPartSpec item = cat.Components[i];
                    if (!string.Equals(item.Category, normCat, StringComparison.OrdinalIgnoreCase)) continue;
                    if (normCat == "mcb" && item.Poles != (poles >= 3 ? 3 : 1)) continue;
                    if (matchMfg && item.Manufacturer.IndexOf(normMfg, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (matchAmps && item.RatedAmps < amps - 1e-3) continue;
                    double score = (item.RatedAmps >= amps ? (item.RatedAmps - amps) : (10000.0 + (amps - item.RatedAmps))) +
                                   (item.IcuKa >= icuKa ? 0.0 : 500.0);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = item;
                    }
                }
            }

            if (best != null)
            {
                SwitchgearPartSpec c = ClonePartSpec(best);
                if (customWidthMm.HasValue && customWidthMm.Value > 3.0) c.WidthMm = customWidthMm.Value;
                if (customHeightMm.HasValue && customHeightMm.Value > 10.0) c.HeightMm = customHeightMm.Value;
                if (customDepthMm.HasValue && customDepthMm.Value > 10.0) c.DepthMm = customDepthMm.Value;
                if (customHeatW.HasValue && customHeatW.Value >= 0.0) c.HeatLossWatts = customHeatW.Value;
                return c;
            }

            return MakePart(
                normCat.ToUpperInvariant() + "-" + ((int)Math.Ceiling(amps)).ToString(CultureInfo.InvariantCulture) + "A",
                normMfg,
                "Generic IEC Switchgear",
                normCat,
                poles >= 3 ? 3 : 1,
                amps,
                Math.Max(6.0, icuKa),
                Math.Max(18.0, (poles >= 3 ? 3 : 1) * 18.0), 85.0, 78.5, true,
                Math.Round(Math.Max(2.0, amps * 0.14), 1), 35.0, 60.0);
        }

        private static List<string> ResolvePanel3DVaultDirectories(string customDir)
        {
            List<string> dirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(customDir)) dirs.Add(customDir.Trim());
            try
            {
                string envRoot = Environment.GetEnvironmentVariable("AI_ENGINEER_ROOT");
                if (!string.IsNullOrWhiteSpace(envRoot))
                    dirs.Add(Path.Combine(Path.Combine(envRoot.Trim(), "workspace"), "panel_3d_library"));
                string asmDir = Path.GetDirectoryName(typeof(CadToolExecutor).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    DirectoryInfo cur = new DirectoryInfo(asmDir);
                    for (int i = 0; cur != null && i < 8; i++)
                    {
                        string cand = Path.Combine(Path.Combine(cur.FullName, "workspace"), "panel_3d_library");
                        if (Directory.Exists(cand)) { dirs.Add(cand); break; }
                        cur = cur.Parent;
                    }
                }
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localApp))
                    dirs.Add(Path.Combine(Path.Combine(localApp, "AI-Engineer"), "panel_3d_library"));
            }
            catch { }
            return dirs;
        }

        private static string SanitizeCatalogBlockName(string raw)
        {
            string s = Regex.Replace((raw ?? "PART").Trim().ToUpperInvariant(), @"[^A-Z0-9_\-]+", "_", RegexOptions.None, TimeSpan.FromMilliseconds(500));
            return string.IsNullOrWhiteSpace(s) ? "PART" : s;
        }

        private static CopperBusbarSpec SelectCopperBusbar(SwitchgearCatalogData catalog, double designAmps, double faultKa)
        {
            List<CopperBusbarSpec> bars = (catalog != null && catalog.Busbars.Count > 0) ? catalog.Busbars : BuildDefaultCatalog().Busbars;
            double reqAmps = Math.Max(designAmps * 1.10, faultKa >= 35.0 ? 250.0 : 100.0);
            for (int i = 0; i < bars.Count; i++)
            {
                if (bars[i].MaxAmps35K >= reqAmps - 1e-6) return bars[i];
            }
            return bars[bars.Count - 1];
        }

        internal static CopperBusbarSpec MatchNearestStandardBusbar(SwitchgearCatalogData catalog, string rawSpec, double? ampsHint)
        {
            List<CopperBusbarSpec> bars = (catalog != null && catalog.Busbars.Count > 0) ? catalog.Busbars : BuildDefaultCatalog().Busbars;
            if (!string.IsNullOrWhiteSpace(rawSpec))
            {
                Match m = Regex.Match(rawSpec.Trim(), @"(\d+(?:\.\d+)?)\s*[xX*]\s*(\d+(?:\.\d+)?)", RegexOptions.None, TimeSpan.FromMilliseconds(200));
                if (m.Success)
                {
                    double reqW = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double reqT = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    double reqArea = reqW * reqT;
                    CopperBusbarSpec best = null;
                    double bestDiff = double.MaxValue;
                    for (int i = 0; i < bars.Count; i++)
                    {
                        double bArea = bars[i].WidthMm * bars[i].ThicknessMm * bars[i].Runs;
                        double diff = Math.Abs(bArea - reqArea) * 2.0 + Math.Abs(bars[i].WidthMm - reqW) + Math.Abs(bars[i].ThicknessMm - reqT) * 3.0;
                        if (diff < bestDiff)
                        {
                            bestDiff = diff;
                            best = bars[i];
                        }
                    }
                    if (best != null) return best;
                }
            }
            return SelectCopperBusbar(catalog, ampsHint ?? 160.0, 25.0);
        }

        private static CoolingFanSpec SelectCoolingFan(SwitchgearCatalogData catalog, double reqAirflowM3h)
        {
            List<CoolingFanSpec> fans = (catalog != null && catalog.Fans.Count > 0) ? catalog.Fans : BuildDefaultCatalog().Fans;
            for (int i = 0; i < fans.Count; i++)
            {
                if (fans[i].AirflowM3h >= reqAirflowM3h - 1e-6) return fans[i];
            }
            return fans[fans.Count - 1];
        }

        private static double[] GetComponentTerminalPortMm(PlacedPanelComponent comp, bool isLine, int poleIndex, string cableEntry)
        {
            double w = comp.Part.WidthMm;
            double h = comp.Part.HeightMm;
            double d = comp.Part.DepthMm;
            double xRel = comp.Part.Poles >= 3
                ? w * (0.20 + 0.30 * Math.Max(0, Math.Min(2, poleIndex)))
                : (w * 0.50);
            bool invertIncomer = comp.Zone == "incomer" && string.Equals(cableEntry, "top", StringComparison.OrdinalIgnoreCase);
            bool topPort = invertIncomer ? !isLine : isLine;
            double yRel = topPort ? (h * 0.90) : (h * 0.10);
            double zRel = Math.Min(42.0, d * 0.45);
            return new double[] { Math.Round(comp.XMm + xRel, 2), Math.Round(comp.YMm + yRel, 2), Math.Round(comp.ZMm + zRel, 2) };
        }

        internal static PanelManufacturingPlan ComputePanelManufacturingPlan(
            string panelName,
            string manufacturer,
            string cableEntryRaw,
            double ambientTempC,
            double maxInternalTempC,
            double sparePercent,
            double incomerAmpsHint,
            double faultKa,
            List<PlacedPanelComponent> rawComponents,
            List<string> vaultDirs)
        {
            return ComputePanelManufacturingPlan(
                panelName, manufacturer, cableEntryRaw, ambientTempC, maxInternalTempC,
                sparePercent, incomerAmpsHint, faultKa, rawComponents, vaultDirs, false, null, null, true);
        }

        internal static PanelManufacturingPlan ComputePanelManufacturingPlan(
            string panelName,
            string manufacturer,
            string cableEntryRaw,
            double ambientTempC,
            double maxInternalTempC,
            double sparePercent,
            double incomerAmpsHint,
            double faultKa,
            List<PlacedPanelComponent> rawComponents,
            List<string> vaultDirs,
            bool preserveManualCoords,
            double? overrideEncWMm,
            double? overrideEncHMm,
            bool useCombBusbar)
        {
            SwitchgearCatalogData catalog = ResolveSwitchgearCatalog();
            string cableEntry = string.Equals((cableEntryRaw ?? "bottom").Trim(), "top", StringComparison.OrdinalIgnoreCase) ? "top" : "bottom";

            PanelManufacturingPlan plan = new PanelManufacturingPlan();
            plan.PanelName = string.IsNullOrWhiteSpace(panelName) ? "MDB-01" : panelName.Trim();
            plan.Manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? "Schneider Electric" : manufacturer.Trim();
            plan.CableEntry = cableEntry;
            plan.AmbientTempC = ambientTempC;
            plan.MaxInternalTempC = maxInternalTempC;
            plan.FaultKa = Math.Max(6.0, faultKa);
            plan.UseCombBusbar = useCombBusbar;

            PlacedPanelComponent incomerComp = null;
            List<PlacedPanelComponent> thermalTopComps = new List<PlacedPanelComponent>();
            List<PlacedPanelComponent> modularComps = new List<PlacedPanelComponent>();
            List<PlacedPanelComponent> terminalComps = new List<PlacedPanelComponent>();

            HashSet<string> downstreamTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rawComponents.Count; i++)
            {
                if (rawComponents[i] != null && !string.IsNullOrWhiteSpace(rawComponents[i].ConnectedToTag))
                    downstreamTargets.Add(rawComponents[i].ConnectedToTag.Trim());
            }

            double sumBranchAmps = 0.0;
            for (int i = 0; i < rawComponents.Count; i++)
            {
                PlacedPanelComponent comp = rawComponents[i];
                if (comp == null || comp.Part == null) continue;
                comp.IsFedFromUpstream = !string.IsNullOrWhiteSpace(comp.Tag) && downstreamTargets.Contains(comp.Tag.Trim());
                string c = (comp.Part.Category ?? "mcb").ToLowerInvariant();
                if (c == "vfd" || c == "psu" || c == "transformer")
                {
                    comp.Zone = "thermal_top";
                    thermalTopComps.Add(comp);
                    sumBranchAmps += comp.Part.RatedAmps * comp.Qty;
                }
                else if (c == "terminal")
                {
                    comp.Zone = "terminals";
                    terminalComps.Add(comp);
                }
                else if ((c == "acb" || c == "mccb") && incomerComp == null)
                {
                    comp.Zone = "incomer";
                    incomerComp = comp;
                }
                else
                {
                    comp.Zone = "distribution_din";
                    modularComps.Add(comp);
                    sumBranchAmps += comp.Part.RatedAmps * (comp.Part.Poles >= 3 ? 1.0 : 0.35) * comp.Qty;
                }
            }

            if (incomerComp == null)
            {
                double estIncomerA = Math.Max(incomerAmpsHint, Math.Max(63.0, sumBranchAmps * 0.70));
                string incomerCat = estIncomerA >= 630.0 ? "acb" : (estIncomerA > 63.0 ? "mccb" : "mcb");
                SwitchgearPartSpec incSpec = ResolvePartFromCatalog(
                    catalog, null, plan.Manufacturer, incomerCat, 3, estIncomerA, plan.FaultKa, null, null, null, null);
                incomerComp = new PlacedPanelComponent
                {
                    Tag = "QFMAIN",
                    Part = incSpec,
                    Qty = 1,
                    CircuitName = plan.PanelName + "-MAIN",
                    Phase = "RYB",
                    CableMm2 = estIncomerA >= 250 ? 120.0 : (estIncomerA >= 100 ? 35.0 : 16.0),
                    Zone = "incomer"
                };
            }
            if (string.IsNullOrWhiteSpace(incomerComp.Tag)) incomerComp.Tag = "QFMAIN";
            plan.IncomerAmps = Math.Max(incomerAmpsHint, incomerComp.Part.RatedAmps);

            // Auto-create outgoing terminals XT1 only for terminal-end devices (skip upstream items with connected_to pointing to another device)
            HashSet<string> compTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rawComponents.Count; i++)
                if (rawComponents[i] != null && !string.IsNullOrEmpty(rawComponents[i].Tag)) compTags.Add(rawComponents[i].Tag.Trim());

            if (terminalComps.Count == 0 && !preserveManualCoords)
            {
                List<PlacedPanelComponent> leafComps = new List<PlacedPanelComponent>();
                leafComps.AddRange(modularComps);
                leafComps.AddRange(thermalTopComps);
                for (int i = 0; i < leafComps.Count; i++)
                {
                    PlacedPanelComponent mc = leafComps[i];
                    if (!string.IsNullOrWhiteSpace(mc.ConnectedToTag) && compTags.Contains(mc.ConnectedToTag.Trim()))
                        continue; // flows into downstream component inside the same cabinet
                    int polesNeeded = Math.Max(1, mc.Part.Poles);
                    SwitchgearPartSpec tSpec = ResolvePartFromCatalog(
                        catalog, mc.CableMm2 >= 10.0 ? "NSYTRV162" : "NSYTRV42",
                        plan.Manufacturer, "terminal", 1, mc.Part.RatedAmps, 6.0, null, null, null, null);
                    for (int p = 0; p < polesNeeded; p++)
                    {
                        string tTag = "XT1:" + (terminalComps.Count + 1).ToString(CultureInfo.InvariantCulture);
                        if (p == 0 && string.IsNullOrEmpty(mc.TargetTerminalTag)) mc.TargetTerminalTag = tTag;
                        terminalComps.Add(new PlacedPanelComponent
                        {
                            Tag = tTag,
                            Part = ClonePartSpec(tSpec),
                            Qty = 1,
                            CircuitName = mc.CircuitName,
                            Phase = mc.Phase,
                            CableMm2 = mc.CableMm2,
                            Zone = "terminals",
                            WireNo = !string.IsNullOrEmpty(mc.WireNo) ? mc.WireNo : ("W-" + (i + 1).ToString("00", CultureInfo.InvariantCulture))
                        });
                    }
                }
            }
            else if (preserveManualCoords && terminalComps.Count > 0)
            {
                int tCursor = 0;
                for (int i = 0; i < modularComps.Count && tCursor < terminalComps.Count; i++)
                {
                    modularComps[i].TargetTerminalTag = terminalComps[tCursor].Tag;
                    tCursor += Math.Max(1, modularComps[i].Part.Poles);
                }
            }

            plan.MainBusbar = SelectCopperBusbar(catalog, plan.IncomerAmps, plan.FaultKa);
            plan.NeutralBusbar = SelectCopperBusbar(catalog, Math.Max(63.0, plan.IncomerAmps * 0.60), plan.FaultKa * 0.6);
            plan.PeBusbar = SelectCopperBusbar(catalog, Math.Max(63.0, plan.IncomerAmps * 0.50), plan.FaultKa * 0.6);

            double marginSideMm = 68.0;
            double gapCompMm = 4.5;
            double ductHWall = 50.0;
            double ductDepthMm = 60.0;
            double plateBackZ = 3.0;
            double railFrontZ = 10.5;
            double busPitch = 40.0;
            double busChamberH = 3.0 * busPitch + plan.MainBusbar.WidthMm + 45.0; // strictly isolated vertical chamber

            if (preserveManualCoords)
            {
                double encWM = overrideEncWMm.HasValue && overrideEncWMm.Value > 350.0 ? overrideEncWMm.Value : 800.0;
                double encHM = overrideEncHMm.HasValue && overrideEncHMm.Value > 500.0 ? overrideEncHMm.Value : 1000.0;
                double maxPartD = incomerComp.Part.DepthMm;
                for (int i = 0; i < rawComponents.Count; i++) if (rawComponents[i] != null) maxPartD = Math.Max(maxPartD, rawComponents[i].Part.DepthMm);
                plan.Enclosure = new EnclosureSpec
                {
                    PartNumber = "2D-CONVERTED",
                    Series = "Converted From Live 2D Panel",
                    EnclosureType = encHM >= 1600.0 ? "floor_standing" : "wall_mount",
                    WidthMm = Math.Round(encWM, 1),
                    HeightMm = Math.Round(encHM, 1),
                    DepthMm = Math.Max(250.0, Math.Ceiling((maxPartD + 75.0) / 50.0) * 50.0)
                };
                plan.SpareSpacePercent = sparePercent;
                for (int i = 0; i < rawComponents.Count; i++)
                    if (rawComponents[i] != null) plan.Components.Add(rawComponents[i]);

                double barY = Math.Max(180.0, Math.Min(encHM - 200.0, incomerComp.YMm + incomerComp.Part.HeightMm + 45.0));
                AddThreePhaseBusbarsWithDroppers(plan, marginSideMm, encWM - marginSideMm, barY, incomerComp);
                AddNeutralAndPeBars(plan, encWM - marginSideMm - 180.0, encWM - marginSideMm, cableEntry == "bottom" ? 55.0 : (encHM - 72.0));
                ReconstructDinRailsAndDuctsFromLivePositions(plan, encWM);
            }
            else
            {
                double spareRatio = Math.Max(0.15, Math.Min(0.50, sparePercent / 100.0));
                EnclosureSpec chosenEnc = null;
                List<EnclosureSpec> encList = (catalog != null && catalog.Enclosures.Count > 0) ? catalog.Enclosures : BuildDefaultCatalog().Enclosures;
                double bestInnerW = 464.0;
                List<List<PlacedPanelComponent>> finalRowsMod = new List<List<PlacedPanelComponent>>();
                List<List<PlacedPanelComponent>> finalRowsTherm = new List<List<PlacedPanelComponent>>();
                double finalSparePct = 20.0;
                double reqHeightMm = 600.0;

                for (int ei = 0; ei < encList.Count; ei++)
                {
                    EnclosureSpec cand = encList[ei];
                    double usableW = cand.WidthMm - 2.0 * marginSideMm;
                    double usableRowWWithSpare = usableW * (1.0 - spareRatio);
                    double maxPartDepth = incomerComp.Part.DepthMm;
                    for (int i = 0; i < thermalTopComps.Count; i++) maxPartDepth = Math.Max(maxPartDepth, thermalTopComps[i].Part.DepthMm);
                    if (cand.DepthMm < maxPartDepth + 55.0) continue; // Ensure >= 25mm+ Door Clearance
                    if (cand.WidthMm < Math.Max(incomerComp.Part.WidthMm + 160.0, 400.0)) continue;

                    List<List<PlacedPanelComponent>> tRows = PackComponentsIntoRows(thermalTopComps, usableW - 20.0, 14.0);
                    List<List<PlacedPanelComponent>> mRows = PackComponentsIntoRows(modularComps, Math.Max(180.0, usableRowWWithSpare), gapCompMm);
                    List<List<PlacedPanelComponent>> xtRows = PackComponentsIntoRows(terminalComps, usableW, 2.0);

                    double hThermal = 0.0;
                    for (int r = 0; r < tRows.Count; r++) hThermal += MaxRowHeight(tRows[r]) + ductHWall + 50.0;
                    double hMod = 0.0;
                    for (int r = 0; r < mRows.Count; r++) hMod += MaxRowHeight(mRows[r]) + ductHWall + 40.0;
                    double hXt = xtRows.Count > 0 ? (xtRows.Count * (68.0 + ductHWall)) : 95.0;
                    double hIncomer = incomerComp.Part.HeightMm + incomerComp.Part.ArcClearanceTopMm + 45.0;
                    double bendSpace = Math.Max(140.0, incomerComp.Part.MinCableBendMm);
                    double totalReqH = 90.0 + bendSpace + hXt + hIncomer + busChamberH + hMod + hThermal + 110.0;

                    if (cand.HeightMm >= totalReqH)
                    {
                        chosenEnc = cand;
                        bestInnerW = usableW;
                        finalRowsMod = mRows;
                        finalRowsTherm = tRows;
                        reqHeightMm = totalReqH;
                        double usedModW = 0.0;
                        for (int r = 0; r < mRows.Count; r++)
                            for (int c = 0; c < mRows[r].Count; c++) usedModW += mRows[r][c].Part.WidthMm + gapCompMm;
                        double availModW = Math.Max(1.0, mRows.Count * usableW);
                        finalSparePct = Math.Round(Math.Max(0.0, ((availModW - usedModW) / availModW) * 100.0), 1);
                        break;
                    }
                }

                if (chosenEnc == null)
                {
                    chosenEnc = new EnclosureSpec
                    {
                        PartNumber = "VX-CUSTOM",
                        Series = "Custom Industrial Baying Enclosure",
                        EnclosureType = "floor_standing",
                        WidthMm = 1000.0,
                        HeightMm = Math.Max(2000.0, Math.Ceiling((reqHeightMm + 150.0) / 200.0) * 200.0),
                        DepthMm = 500.0
                    };
                    bestInnerW = chosenEnc.WidthMm - 2.0 * marginSideMm;
                    finalRowsTherm = PackComponentsIntoRows(thermalTopComps, bestInnerW - 20.0, 14.0);
                    finalRowsMod = PackComponentsIntoRows(modularComps, bestInnerW * (1.0 - spareRatio), gapCompMm);
                    finalSparePct = sparePercent;
                }
                plan.Enclosure = chosenEnc;
                plan.SpareSpacePercent = finalSparePct;

                double encW = chosenEnc.WidthMm;
                double encH = chosenEnc.HeightMm;
                double bendH = Math.Max(130.0, Math.Min(250.0, incomerComp.Part.MinCableBendMm));
                List<List<PlacedPanelComponent>> xtRowsFinal = PackComponentsIntoRows(terminalComps, bestInnerW, 2.0);

                // Zone 1 (Top Exhaust): VFDs / PSUs strictly at top
                double cursorTopY = encH - 80.0;
                for (int r = 0; r < finalRowsTherm.Count; r++)
                {
                    List<PlacedPanelComponent> row = finalRowsTherm[r];
                    double rH = MaxRowHeight(row);
                    double compBotY = cursorTopY - rH;
                    double cursorX = marginSideMm + 12.0;
                    for (int c = 0; c < row.Count; c++)
                    {
                        PlacedPanelComponent cp = row[c];
                        cp.XMm = SnapToModuleGrid(cursorX, 9.0);
                        cp.YMm = compBotY;
                        cp.ZMm = cp.Part.IsDinRail ? railFrontZ : plateBackZ;
                        cp.RailIndex = r + 1;
                        plan.Components.Add(cp);
                        cursorX += cp.Part.WidthMm + 14.0;
                    }
                    if (row.Count > 0)
                        plan.DinRailsMm.Add(new double[] { marginSideMm, compBotY + rH * 0.5 - 17.5, bestInnerW, 35.0, 7.5 });
                    double ductBotY = compBotY - ductHWall - 18.0;
                    plan.WireDuctsMm.Add(new double[] { marginSideMm, ductBotY, bestInnerW, ductHWall, ductDepthMm });
                    cursorTopY = ductBotY - 24.0;
                }

                if (cableEntry == "bottom")
                {
                    double xtBotY = bendH + 25.0;
                    double maxXtTopY = xtBotY + 55.0;
                    for (int r = 0; r < xtRowsFinal.Count; r++)
                    {
                        double rowY = xtBotY + r * 68.0;
                        double cx = marginSideMm + 10.0;
                        for (int c = 0; c < xtRowsFinal[r].Count; c++)
                        {
                            PlacedPanelComponent xt = xtRowsFinal[r][c];
                            xt.XMm = cx;
                            xt.YMm = rowY;
                            xt.ZMm = railFrontZ;
                            plan.Components.Add(xt);
                            cx += xt.Part.WidthMm + 2.0;
                        }
                        plan.DinRailsMm.Add(new double[] { marginSideMm, rowY + 8.0, bestInnerW, 35.0, 7.5 });
                        maxXtTopY = Math.Max(maxXtTopY, rowY + 58.0);
                    }

                    double duct1Y = maxXtTopY + 18.0;
                    plan.WireDuctsMm.Add(new double[] { marginSideMm, duct1Y, bestInnerW, ductHWall, ductDepthMm });

                    double incomerBotY = duct1Y + ductHWall + 25.0;
                    incomerComp.XMm = marginSideMm + 18.0;
                    incomerComp.YMm = incomerBotY;
                    incomerComp.ZMm = plateBackZ;
                    plan.Components.Add(incomerComp);

                    double busBaseY = incomerBotY + incomerComp.Part.HeightMm + Math.Max(45.0, incomerComp.Part.ArcClearanceTopMm * 0.75);
                    AddThreePhaseBusbarsWithDroppers(plan, marginSideMm + 10.0, encW - marginSideMm - 10.0, busBaseY, incomerComp);
                    AddNeutralAndPeBars(plan, encW - marginSideMm - 180.0, encW - marginSideMm - 10.0, bendH - 55.0);

                    double dinStartY = busBaseY + busChamberH;
                    for (int r = 0; r < finalRowsMod.Count; r++)
                    {
                        List<PlacedPanelComponent> row = finalRowsMod[r];
                        double rH = MaxRowHeight(row);
                        double compBotY = dinStartY;
                        double railCenterY = compBotY + rH * 0.5;
                        double cx = marginSideMm + 18.0;
                        for (int c = 0; c < row.Count; c++)
                        {
                            PlacedPanelComponent mc = row[c];
                            mc.XMm = SnapToModuleGrid(cx, mc.Part.Poles == 1 ? 18.0 : 9.0);
                            mc.YMm = railCenterY - mc.Part.HeightMm * 0.5; // Magnetic DIN clip alignment!
                            mc.ZMm = mc.Part.IsDinRail ? railFrontZ : plateBackZ;
                            mc.RailIndex = r + 1;
                            plan.Components.Add(mc);
                            cx = mc.XMm + mc.Part.WidthMm + gapCompMm;
                        }
                        plan.DinRailsMm.Add(new double[] { marginSideMm, railCenterY - 17.5, bestInnerW, 35.0, 7.5 });
                        AddCombBusbarForRow(plan, row, marginSideMm + 10.0);
                        double midDuctY = compBotY + rH + 20.0;
                        plan.WireDuctsMm.Add(new double[] { marginSideMm, midDuctY, bestInnerW, ductHWall, ductDepthMm });
                        dinStartY = midDuctY + ductHWall + 22.0;
                    }

                    double vDuctTop = Math.Max(encH - 70.0, dinStartY);
                    plan.WireDuctsMm.Add(new double[] { marginSideMm - 44.0, duct1Y, 38.0, Math.Max(120.0, vDuctTop - duct1Y), ductDepthMm });
                    plan.WireDuctsMm.Add(new double[] { encW - marginSideMm + 6.0, duct1Y, 38.0, Math.Max(120.0, vDuctTop - duct1Y), ductDepthMm });
                }
                else
                {
                    double xtTopBaseY = cursorTopY - 62.0;
                    for (int r = 0; r < xtRowsFinal.Count; r++)
                    {
                        double rowY = xtTopBaseY - r * 68.0;
                        double cx = marginSideMm + 10.0;
                        for (int c = 0; c < xtRowsFinal[r].Count; c++)
                        {
                            PlacedPanelComponent xt = xtRowsFinal[r][c];
                            xt.XMm = cx;
                            xt.YMm = rowY;
                            xt.ZMm = railFrontZ;
                            plan.Components.Add(xt);
                            cx += xt.Part.WidthMm + 2.0;
                        }
                        plan.DinRailsMm.Add(new double[] { marginSideMm, rowY + 8.0, bestInnerW, 35.0, 7.5 });
                        xtTopBaseY = rowY;
                    }
                    AddNeutralAndPeBars(plan, encW - marginSideMm - 180.0, encW - marginSideMm - 10.0, encH - 72.0);

                    double upperDuctY = xtTopBaseY - ductHWall - 18.0;
                    plan.WireDuctsMm.Add(new double[] { marginSideMm, upperDuctY, bestInnerW, ductHWall, ductDepthMm });

                    double incomerBotY = upperDuctY - incomerComp.Part.HeightMm - 25.0;
                    incomerComp.XMm = marginSideMm + 18.0;
                    incomerComp.YMm = incomerBotY;
                    incomerComp.ZMm = plateBackZ;
                    plan.Components.Add(incomerComp);

                    double busBaseY = incomerBotY - busChamberH;
                    AddThreePhaseBusbarsWithDroppers(plan, marginSideMm + 10.0, encW - marginSideMm - 10.0, busBaseY, incomerComp);

                    double dinStartY = busBaseY - 45.0;
                    for (int r = 0; r < finalRowsMod.Count; r++)
                    {
                        List<PlacedPanelComponent> row = finalRowsMod[r];
                        double rH = MaxRowHeight(row);
                        double compBotY = dinStartY - rH;
                        double railCenterY = compBotY + rH * 0.5;
                        double cx = marginSideMm + 18.0;
                        for (int c = 0; c < row.Count; c++)
                        {
                            PlacedPanelComponent mc = row[c];
                            mc.XMm = SnapToModuleGrid(cx, mc.Part.Poles == 1 ? 18.0 : 9.0);
                            mc.YMm = railCenterY - mc.Part.HeightMm * 0.5;
                            mc.ZMm = mc.Part.IsDinRail ? railFrontZ : plateBackZ;
                            mc.RailIndex = r + 1;
                            plan.Components.Add(mc);
                            cx = mc.XMm + mc.Part.WidthMm + gapCompMm;
                        }
                        plan.DinRailsMm.Add(new double[] { marginSideMm, railCenterY - 17.5, bestInnerW, 35.0, 7.5 });
                        AddCombBusbarForRow(plan, row, marginSideMm + 10.0);
                        double lowDuctY = compBotY - ductHWall - 20.0;
                        plan.WireDuctsMm.Add(new double[] { marginSideMm, lowDuctY, bestInnerW, ductHWall, ductDepthMm });
                        dinStartY = lowDuctY - 22.0;
                    }
                    double vDuctBot = Math.Max(45.0, dinStartY);
                    plan.WireDuctsMm.Add(new double[] { marginSideMm - 44.0, vDuctBot, 38.0, Math.Max(120.0, xtTopBaseY - vDuctBot), ductDepthMm });
                    plan.WireDuctsMm.Add(new double[] { encW - marginSideMm + 6.0, vDuctBot, 38.0, Math.Max(120.0, xtTopBaseY - vDuctBot), ductDepthMm });
                }
            }

            // Strict Anti-Hallucination AABB Collision Verification + CNC Holes + Thermal + 3D Netlist Wiring + Duct Fill Check
            VerifyAndResolvePanelCollisions(plan, preserveManualCoords);
            BuildCncDrillingPlan(plan);
            FinalizeThermalAndVaultPlan(plan, catalog, vaultDirs);
            BuildPanel3DWiringRoutes(plan);
            EvaluateAndUpsizeDuctFill(plan);

            return plan;
        }

        private static double SnapToModuleGrid(double xMm, double stepMm)
        {
            double s = Math.Max(1.0, stepMm);
            return Math.Round(Math.Ceiling(xMm / s) * s, 2);
        }

        private static void ReconstructDinRailsAndDuctsFromLivePositions(PanelManufacturingPlan plan, double encWMm)
        {
            List<double> railYs = new List<double>();
            for (int i = 0; i < plan.Components.Count; i++)
            {
                PlacedPanelComponent c = plan.Components[i];
                if (!c.Part.IsDinRail) continue;
                double cy = c.YMm + c.Part.HeightMm * 0.5;
                bool found = false;
                for (int ri = 0; ri < railYs.Count; ri++)
                    if (Math.Abs(railYs[ri] - cy) < 25.0) { found = true; break; }
                if (!found)
                {
                    railYs.Add(cy);
                    plan.DinRailsMm.Add(new double[] { 68.0, cy - 17.5, Math.Max(200.0, encWMm - 136.0), 35.0, 7.5 });
                    plan.WireDuctsMm.Add(new double[] { 68.0, cy + 52.0, Math.Max(200.0, encWMm - 136.0), 50.0, 60.0 });
                }
            }
        }

        private static void VerifyAndResolvePanelCollisions(PanelManufacturingPlan plan, bool readOnlyAudit)
        {
            // 1. Check horizontal component overlaps & clamp to enclosure bounds
            int collisions = 0;
            double maxFrontZ = 0.0;
            for (int i = 0; i < plan.Components.Count; i++)
            {
                PlacedPanelComponent a = plan.Components[i];
                maxFrontZ = Math.Max(maxFrontZ, a.ZMm + a.Part.DepthMm);
                for (int j = i + 1; j < plan.Components.Count; j++)
                {
                    PlacedPanelComponent b = plan.Components[j];
                    bool overlapX = a.XMm < (b.XMm + b.Part.WidthMm - 0.5) && (a.XMm + a.Part.WidthMm - 0.5) > b.XMm;
                    bool overlapY = a.YMm < (b.YMm + b.Part.HeightMm - 0.5) && (a.YMm + a.Part.HeightMm - 0.5) > b.YMm;
                    if (overlapX && overlapY)
                    {
                        if (!readOnlyAudit)
                        {
                            b.XMm = Math.Round(a.XMm + a.Part.WidthMm + 4.0, 1);
                            overlapX = false;
                        }
                        if (overlapX) collisions++;
                    }
                }

                // 2. Strictly verify no component overlaps Main Horizontal Copper Busbars!
                for (int bi = 0; bi < plan.Busbars.Count; bi++)
                {
                    PanelBusbarRun bar = plan.Busbars[bi];
                    if (bar.IsDropper || bar.IsComb || bar.PointsMm.Count < 2) continue;
                    double bMinX = Math.Min(bar.PointsMm[0][0], bar.PointsMm[1][0]);
                    double bMaxX = Math.Max(bar.PointsMm[0][0], bar.PointsMm[1][0]);
                    double bMinY = Math.Min(bar.PointsMm[0][1], bar.PointsMm[1][1]) - bar.WidthMm * 0.5;
                    double bMaxY = Math.Max(bar.PointsMm[0][1], bar.PointsMm[1][1]) + bar.WidthMm * 0.5;
                    bool ovX = a.XMm < (bMaxX - 1.0) && (a.XMm + a.Part.WidthMm - 1.0) > bMinX;
                    bool ovY = a.YMm < (bMaxY - 1.0) && (a.YMm + a.Part.HeightMm - 1.0) > bMinY;
                    if (ovX && ovY) collisions++;
                }

                // 3. Strictly verify component stays within enclosure boundary
                if (a.XMm < 25.0 || (a.XMm + a.Part.WidthMm) > (plan.Enclosure.WidthMm - 25.0) ||
                    a.YMm < 25.0 || (a.YMm + a.Part.HeightMm) > (plan.Enclosure.HeightMm - 25.0))
                {
                    plan.Warnings.Add("Component '" + a.Tag + "' approaches enclosure boundary margin.");
                }
            }

            plan.MinDoorClearanceMm = Math.Round(plan.Enclosure.DepthMm - maxFrontZ, 1);
            if (plan.MinDoorClearanceMm < 20.0)
            {
                plan.Enclosure.DepthMm = Math.Ceiling((maxFrontZ + 45.0) / 50.0) * 50.0;
                plan.MinDoorClearanceMm = Math.Round(plan.Enclosure.DepthMm - maxFrontZ, 1);
            }
            plan.CollisionCount = collisions;
            plan.ZeroCollisionVerified = (collisions == 0);
            if (collisions > 0)
                plan.Warnings.Add("Geometric collision detected between " + collisions.ToString(CultureInfo.InvariantCulture) + " component/busbar pairs.");
        }

        private static void BuildCncDrillingPlan(PanelManufacturingPlan plan)
        {
            for (int i = 0; i < plan.DinRailsMm.Count; i++)
            {
                double[] r = plan.DinRailsMm[i];
                double yMid = Math.Round(r[1] + r[3] * 0.5, 1);
                plan.CncHoles.Add(new CncDrillHole { XMm = Math.Round(r[0] + 18.0, 1), YMm = yMid, DiameterMm = 5.5, Purpose = "DIN Rail M5 Left" });
                plan.CncHoles.Add(new CncDrillHole { XMm = Math.Round(r[0] + r[2] * 0.5, 1), YMm = yMid, DiameterMm = 5.5, Purpose = "DIN Rail M5 Center" });
                plan.CncHoles.Add(new CncDrillHole { XMm = Math.Round(r[0] + r[2] - 18.0, 1), YMm = yMid, DiameterMm = 5.5, Purpose = "DIN Rail M5 Right" });
            }
            for (int i = 0; i < plan.WireDuctsMm.Count; i++)
            {
                double[] d = plan.WireDuctsMm[i];
                if (d[2] > d[3])
                {
                    double yMid = Math.Round(d[1] + d[3] * 0.5, 1);
                    plan.CncHoles.Add(new CncDrillHole { XMm = Math.Round(d[0] + 25.0, 1), YMm = yMid, DiameterMm = 4.5, Purpose = "Duct Rivet M4" });
                    plan.CncHoles.Add(new CncDrillHole { XMm = Math.Round(d[0] + d[2] - 25.0, 1), YMm = yMid, DiameterMm = 4.5, Purpose = "Duct Rivet M4" });
                }
            }
            for (int i = 0; i < plan.Components.Count; i++)
            {
                PlacedPanelComponent c = plan.Components[i];
                if (c.Zone == "incomer")
                {
                    plan.CncHoles.Add(new CncDrillHole { XMm = Math.Round(c.XMm + 12.0, 1), YMm = Math.Round(c.YMm + 12.0, 1), DiameterMm = 6.6, Purpose = c.Tag + " M6 Mount" });
                    plan.CncHoles.Add(new CncDrillHole { XMm = Math.Round(c.XMm + c.Part.WidthMm - 12.0, 1), YMm = Math.Round(c.YMm + 12.0, 1), DiameterMm = 6.6, Purpose = c.Tag + " M6 Mount" });
                    plan.CncHoles.Add(new CncDrillHole { XMm = Math.Round(c.XMm + 12.0, 1), YMm = Math.Round(c.YMm + c.Part.HeightMm - 12.0, 1), DiameterMm = 6.6, Purpose = c.Tag + " M6 Mount" });
                    plan.CncHoles.Add(new CncDrillHole { XMm = Math.Round(c.XMm + c.Part.WidthMm - 12.0, 1), YMm = Math.Round(c.YMm + c.Part.HeightMm - 12.0, 1), DiameterMm = 6.6, Purpose = c.Tag + " M6 Mount" });
                }
            }
        }

        private static List<List<PlacedPanelComponent>> PackComponentsIntoRows(List<PlacedPanelComponent> items, double maxRowW, double gapMm)
        {
            List<List<PlacedPanelComponent>> rows = new List<List<PlacedPanelComponent>>();
            if (items == null || items.Count == 0) return rows;
            List<PlacedPanelComponent> cur = new List<PlacedPanelComponent>();
            double curW = 0.0;
            for (int i = 0; i < items.Count; i++)
            {
                PlacedPanelComponent it = items[i];
                double w = it.Part != null ? it.Part.WidthMm : 18.0;
                if (cur.Count > 0 && curW + gapMm + w > maxRowW)
                {
                    rows.Add(cur);
                    cur = new List<PlacedPanelComponent>();
                    curW = 0.0;
                }
                if (cur.Count > 0) curW += gapMm;
                cur.Add(it);
                curW += w;
            }
            if (cur.Count > 0) rows.Add(cur);
            return rows;
        }

        private static double MaxRowHeight(List<PlacedPanelComponent> row)
        {
            double maxH = 85.0;
            if (row == null) return maxH;
            for (int i = 0; i < row.Count; i++)
                if (row[i].Part != null) maxH = Math.Max(maxH, row[i].Part.HeightMm);
            return maxH;
        }

        private static void AddThreePhaseBusbarsWithDroppers(
            PanelManufacturingPlan plan, double xStart, double xEnd, double baseY, PlacedPanelComponent incomer)
        {
            double pitch = 40.0;
            double barZ = 45.0;
            string[] labels = new string[] { "R", "Y", "B" };
            short[] colors = new short[] { 1, 2, 5 };
            Dictionary<string, double> barYByPhase = new Dictionary<string, double>();
            int supCount = Math.Max(2, (int)Math.Ceiling((xEnd - xStart) / 300.0) + 1);

            for (int i = 0; i < 3; i++)
            {
                double y = Math.Round(baseY + (2 - i) * pitch + plan.MainBusbar.WidthMm * 0.5, 1);
                barYByPhase[labels[i]] = y;
                PanelBusbarRun bar = new PanelBusbarRun
                {
                    Label = labels[i],
                    Designation = plan.MainBusbar.Designation,
                    ColorIndex = colors[i],
                    WidthMm = plan.MainBusbar.WidthMm,
                    ThicknessMm = plan.MainBusbar.ThicknessMm,
                    Runs = plan.MainBusbar.Runs,
                    PointsMm = new List<double[]>
                    {
                        new double[] { xStart, y, barZ },
                        new double[] { xEnd, y, barZ }
                    }
                };
                for (int si = 0; si < supCount; si++)
                {
                    double sx = xStart + ((xEnd - xStart) * si) / Math.Max(1, supCount - 1);
                    bar.BoltJointsMm.Add(new double[] { Math.Round(sx, 1), y, barZ });
                }
                plan.Busbars.Add(bar);
            }
            plan.BusbarInsulatorCount = supCount;

            // Exact Terminal-Port Z-Bend Copper Droppers connecting QFMAIN Load Ports directly onto R, Y, B Busbars
            if (incomer != null)
            {
                double dropW = Math.Max(12.0, Math.Min(plan.MainBusbar.WidthMm, incomer.Part.WidthMm * 0.18));
                double dropThk = Math.Max(3.0, plan.MainBusbar.ThicknessMm);
                for (int pi = 0; pi < 3; pi++)
                {
                    double[] port = GetComponentTerminalPortMm(incomer, false, pi, plan.CableEntry);
                    double barY = barYByPhase[labels[pi]];
                    PanelBusbarRun drop = new PanelBusbarRun
                    {
                        Label = "DROP-" + labels[pi],
                        Designation = dropW.ToString("0", CultureInfo.InvariantCulture) + "x" + dropThk.ToString("0", CultureInfo.InvariantCulture) + " Cu",
                        ColorIndex = colors[pi],
                        WidthMm = dropW,
                        ThicknessMm = dropThk,
                        Runs = 1,
                        IsDropper = true,
                        PointsMm = new List<double[]>
                        {
                            new double[] { port[0], port[1], port[2] },
                            new double[] { port[0], port[1], barZ },
                            new double[] { port[0], barY, barZ }
                        }
                    };
                    drop.BoltJointsMm.Add(new double[] { port[0], port[1], port[2] });
                    drop.BoltJointsMm.Add(new double[] { port[0], barY, barZ });
                    plan.Busbars.Add(drop);
                }
            }
        }

        private static void AddNeutralAndPeBars(PanelManufacturingPlan plan, double xStart, double xEnd, double baseY)
        {
            plan.Busbars.Add(new PanelBusbarRun
            {
                Label = "N",
                Designation = plan.NeutralBusbar.Designation,
                ColorIndex = 4,
                WidthMm = plan.NeutralBusbar.WidthMm,
                ThicknessMm = plan.NeutralBusbar.ThicknessMm,
                Runs = plan.NeutralBusbar.Runs,
                PointsMm = new List<double[]>
                {
                    new double[] { xStart, Math.Max(30.0, baseY + 26.0), 25.0 },
                    new double[] { xEnd, Math.Max(30.0, baseY + 26.0), 25.0 }
                }
            });
            plan.Busbars.Add(new PanelBusbarRun
            {
                Label = "PE",
                Designation = plan.PeBusbar.Designation,
                ColorIndex = 3,
                WidthMm = plan.PeBusbar.WidthMm,
                ThicknessMm = plan.PeBusbar.ThicknessMm,
                Runs = plan.PeBusbar.Runs,
                PointsMm = new List<double[]>
                {
                    new double[] { xStart, Math.Max(14.0, baseY), 12.0 },
                    new double[] { xEnd, Math.Max(14.0, baseY), 12.0 }
                }
            });
        }

        private static void AddCombBusbarForRow(PanelManufacturingPlan plan, List<PlacedPanelComponent> row, double xStartHint)
        {
            if (!plan.UseCombBusbar || row == null || row.Count < 2) return;
            List<PlacedPanelComponent> mcbs = new List<PlacedPanelComponent>();
            for (int i = 0; i < row.Count; i++)
            {
                if (string.Equals(row[i].Part.Category, "mcb", StringComparison.OrdinalIgnoreCase) && !row[i].IsFedFromUpstream)
                    mcbs.Add(row[i]);
            }
            if (mcbs.Count < 2) return;

            PlacedPanelComponent first = mcbs[0];
            PlacedPanelComponent last = mcbs[mcbs.Count - 1];
            double[] pFirst = GetComponentTerminalPortMm(first, true, 0, plan.CableEntry);
            double[] pLast = GetComponentTerminalPortMm(last, true, last.Part.Poles - 1, plan.CableEntry);
            double combY = pFirst[1];
            double combZ = pFirst[2] + 5.0;
            plan.Busbars.Add(new PanelBusbarRun
            {
                Label = "COMB-R" + first.RailIndex.ToString(CultureInfo.InvariantCulture),
                Designation = "16mm2 Cu Comb Busbar",
                ColorIndex = 4,
                WidthMm = 10.0,
                ThicknessMm = 3.5,
                Runs = 1,
                IsComb = true,
                PointsMm = new List<double[]>
                {
                    new double[] { Math.Max(xStartHint, pFirst[0]), combY, combZ },
                    new double[] { pLast[0], combY, combZ }
                }
            });
        }

        private static void FinalizeThermalAndVaultPlan(
            PanelManufacturingPlan plan, SwitchgearCatalogData catalog, List<string> vaultDirs)
        {
            double devLoss = 0.0;
            for (int i = 0; i < plan.Components.Count; i++)
            {
                PlacedPanelComponent cp = plan.Components[i];
                devLoss += cp.Part.HeatLossWatts * Math.Max(1, cp.Qty);

                if (vaultDirs != null && !string.IsNullOrWhiteSpace(cp.Part.PartNumber))
                {
                    string pn = cp.Part.PartNumber.Trim();
                    for (int vi = 0; vi < vaultDirs.Count; vi++)
                    {
                        string vd = vaultDirs[vi];
                        if (string.IsNullOrWhiteSpace(vd) || !Directory.Exists(vd)) continue;
                        string dwgCand = Path.Combine(vd, pn + ".dwg");
                        if (File.Exists(dwgCand)) { cp.VaultSource = "vault_dwg"; cp.VaultPath = dwgCand; break; }
                        string stpCand = Path.Combine(vd, pn + ".step");
                        if (!File.Exists(stpCand)) stpCand = Path.Combine(vd, pn + ".stp");
                        if (File.Exists(stpCand)) { cp.VaultSource = "vault_step_parametric"; cp.VaultPath = stpCand; }
                    }
                }
            }

            double barLenM = Math.Max(0.4, (plan.Enclosure.WidthMm - 136.0) / 1000.0);
            double rOhm = (plan.MainBusbar.ResistanceMohmPerM / 1000.0) * barLenM;
            double barLoss = Math.Round(3.0 * plan.IncomerAmps * plan.IncomerAmps * rOhm * 0.45, 1);

            plan.TotalHeatLossDevicesW = Math.Round(devLoss, 1);
            plan.TotalHeatLossBusbarW = barLoss;
            plan.TotalHeatLossW = Math.Round(devLoss + barLoss, 1);

            double wM = plan.Enclosure.WidthMm / 1000.0;
            double hM = plan.Enclosure.HeightMm / 1000.0;
            double dM = plan.Enclosure.DepthMm / 1000.0;
            double ae = Math.Round(1.4 * wM * hM + 1.8 * dM * hM + 1.0 * wM * dM, 3);
            plan.EffectiveSurfaceM2 = ae;

            double deltaT = plan.TotalHeatLossW > 0.1
                ? Math.Round(0.55 * Math.Pow(plan.TotalHeatLossW, 0.804) / Math.Max(0.25, ae), 1)
                : 0.0;
            plan.TempRiseC = deltaT;
            plan.InternalTopTempC = Math.Round(plan.AmbientTempC + deltaT, 1);

            double allowableDelta = Math.Max(5.0, plan.MaxInternalTempC - plan.AmbientTempC);
            if (plan.InternalTopTempC > plan.MaxInternalTempC)
            {
                plan.NaturalCoolingOk = false;
                double reqFlow = Math.Round((3.1 * plan.TotalHeatLossW) / allowableDelta * 1.25, 1);
                plan.RequiredAirflowM3h = Math.Max(25.0, reqFlow);
                plan.SelectedFan = SelectCoolingFan(catalog, plan.RequiredAirflowM3h);
                plan.Warnings.Add(
                    "تحذير حراري IEC 61439: درجة الحرارة الداخلية المتوقعة بدون تهوية (" +
                    plan.InternalTopTempC.ToString("0.1", CultureInfo.InvariantCulture) + "C) تتجاوز الحد الأقصى (" +
                    plan.MaxInternalTempC.ToString("0.1", CultureInfo.InvariantCulture) + "C). يلزم تركيب مروحة فلتر " +
                    plan.SelectedFan.PartNumber + " (" + plan.SelectedFan.AirflowM3h.ToString("0", CultureInfo.InvariantCulture) + " m3/h).");
            }
            else
            {
                plan.NaturalCoolingOk = true;
                plan.RequiredAirflowM3h = 0.0;
            }
        }

        private static void BuildPanel3DWiringRoutes(PanelManufacturingPlan plan)
        {
            Dictionary<string, PlacedPanelComponent> byTag = new Dictionary<string, PlacedPanelComponent>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plan.Components.Count; i++)
            {
                if (!string.IsNullOrEmpty(plan.Components[i].Tag))
                    byTag[plan.Components[i].Tag] = plan.Components[i];
            }

            Dictionary<string, PanelBusbarRun> barsByPhase = new Dictionary<string, PanelBusbarRun>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plan.Busbars.Count; i++)
            {
                if (!plan.Busbars[i].IsDropper && !plan.Busbars[i].IsComb)
                    barsByPhase[plan.Busbars[i].Label] = plan.Busbars[i];
            }

            double ductMidZ = 34.0;
            int wIdx = 0;
            for (int i = 0; i < plan.Components.Count; i++)
            {
                PlacedPanelComponent comp = plan.Components[i];
                if (comp.Zone == "incomer" || comp.Zone == "terminals") continue;
                wIdx++;
                string wNo = !string.IsNullOrWhiteSpace(comp.WireNo) ? comp.WireNo : ("W-" + wIdx.ToString("00", CultureInfo.InvariantCulture));
                comp.WireNo = wNo;
                string phKey = (comp.Phase == "Y" || comp.Phase == "B") ? comp.Phase : "R";

                // 1. Incoming feed to component ONLY if not fed directly from an upstream series device (e.g. QF2 -> KM1)
                if (!comp.IsFedFromUpstream)
                {
                    PanelBusbarRun srcBar;
                    if (!barsByPhase.TryGetValue(phKey, out srcBar)) barsByPhase.TryGetValue("R", out srcBar);
                    if (srcBar != null && srcBar.PointsMm.Count >= 2)
                    {
                        double[] linePt = GetComponentTerminalPortMm(comp, true, 0, plan.CableEntry);
                        double minBarX = Math.Min(srcBar.PointsMm[0][0], srcBar.PointsMm[1][0]);
                        double maxBarX = Math.Max(srcBar.PointsMm[0][0], srcBar.PointsMm[1][0]);
                        double clampedX = Math.Max(minBarX, Math.Min(maxBarX, linePt[0]));
                        double barY = srcBar.PointsMm[0][1];
                        double barZ = srcBar.PointsMm[0][2];

                        List<double[]> feedPts = new List<double[]>
                        {
                            new double[] { clampedX, barY, barZ },
                            new double[] { linePt[0], barY, ductMidZ },
                            new double[] { linePt[0], linePt[1], ductMidZ },
                            new double[] { linePt[0], linePt[1], linePt[2] }
                        };
                        plan.WireConnections.Add(new PanelWireConnection
                        {
                            WireNo = wNo + "-IN",
                            FromTag = "BUSBAR-" + phKey,
                            FromTerm = phKey,
                            ToTag = comp.Tag,
                            ToTerm = "1/L1",
                            SizeMm2 = comp.CableMm2,
                            Phase = comp.Phase,
                            ColorName = comp.Phase == "RYB" ? "3P-RYB" : (phKey == "R" ? "Red" : (phKey == "Y" ? "Yellow" : "Blue")),
                            ColorIndex = GetPhaseColorIndex(comp.Phase),
                            LengthMm = ComputePolyline3DLength(feedPts),
                            FerruleLabel = wNo + "/" + comp.Tag + ":1",
                            PointsMm = feedPts
                        });
                    }
                }

                // 2. Outgoing wire: either Series ConnectedTo downstream device (QF2 -> KM1 -> VFD1) OR to Outgoing Terminal XT1
                PlacedPanelComponent dst = null;
                string dstTermLabel = "1/L1";
                bool dstIsTerminal = false;
                if (!string.IsNullOrWhiteSpace(comp.ConnectedToTag) && byTag.TryGetValue(comp.ConnectedToTag.Trim(), out dst))
                {
                    dstTermLabel = dst.Zone == "terminals" ? "1" : "1/L1";
                    dstIsTerminal = dst.Zone == "terminals";
                }
                else if (!string.IsNullOrEmpty(comp.TargetTerminalTag) && byTag.TryGetValue(comp.TargetTerminalTag, out dst))
                {
                    dstTermLabel = "1";
                    dstIsTerminal = true;
                }

                if (dst != null)
                {
                    double[] srcLoad = GetComponentTerminalPortMm(comp, false, 0, plan.CableEntry);
                    double[] dstLine = GetComponentTerminalPortMm(dst, true, 0, plan.CableEntry);
                    double horizDuctY = FindNearestHorizontalDuctY(plan, srcLoad[1]) ?? ((srcLoad[1] + dstLine[1]) * 0.5);
                    double dstDuctY = FindNearestHorizontalDuctY(plan, dstLine[1]) ?? horizDuctY;
                    List<double[]> outPts = new List<double[]>
                    {
                        new double[] { srcLoad[0], srcLoad[1], srcLoad[2] },
                        new double[] { srcLoad[0], horizDuctY, ductMidZ }
                    };
                    if (Math.Abs(horizDuctY - dstDuctY) > 10.0)
                    {
                        double sideDuctX = srcLoad[0] < plan.Enclosure.WidthMm * 0.5 ? 46.0 : (plan.Enclosure.WidthMm - 46.0);
                        outPts.Add(new double[] { sideDuctX, horizDuctY, ductMidZ });
                        outPts.Add(new double[] { sideDuctX, dstDuctY, ductMidZ });
                    }
                    outPts.Add(new double[] { dstLine[0], dstDuctY, ductMidZ });
                    outPts.Add(new double[] { dstLine[0], dstLine[1], ductMidZ });
                    outPts.Add(new double[] { dstLine[0], dstLine[1], dstLine[2] });

                    plan.WireConnections.Add(new PanelWireConnection
                    {
                        WireNo = wNo,
                        FromTag = comp.Tag,
                        FromTerm = "2/T1",
                        ToTag = dst.Tag,
                        ToTerm = dstTermLabel,
                        SizeMm2 = comp.CableMm2,
                        Phase = comp.Phase,
                        ColorName = comp.Phase == "RYB" ? "3P-RYB" : (phKey == "R" ? "Red" : (phKey == "Y" ? "Yellow" : "Blue")),
                        ColorIndex = GetPhaseColorIndex(comp.Phase),
                        LengthMm = ComputePolyline3DLength(outPts),
                        FerruleLabel = wNo + "/" + dst.Tag + (dstIsTerminal ? "" : ":1"),
                        PointsMm = outPts
                    });
                }
            }
        }

        private static void EvaluateAndUpsizeDuctFill(PanelManufacturingPlan plan)
        {
            double maxFill = 0.0;
            for (int di = 0; di < plan.WireDuctsMm.Count; di++)
            {
                double[] d = plan.WireDuctsMm[di];
                double dMinX = d[0], dMinY = d[1], dMaxX = d[0] + d[2], dMaxY = d[1] + d[3];
                double wireAreaMm2 = 0.0;
                for (int wi = 0; wi < plan.WireConnections.Count; wi++)
                {
                    PanelWireConnection wc = plan.WireConnections[wi];
                    bool passes = false;
                    for (int pi = 0; pi < wc.PointsMm.Count; pi++)
                    {
                        double[] p = wc.PointsMm[pi];
                        if (p[0] >= dMinX - 15.0 && p[0] <= dMaxX + 15.0 && p[1] >= dMinY - 15.0 && p[1] <= dMaxY + 15.0)
                        { passes = true; break; }
                    }
                    if (!passes) continue;
                    int cores = wc.Phase == "RYB" ? 3 : 1;
                    double outerDia = Math.Sqrt(wc.SizeMm2) * 1.45 + 1.4;
                    wireAreaMm2 += cores * (Math.PI * outerDia * outerDia * 0.25);
                }
                double internalDuctArea = Math.Max(100.0, d[2] * d[4] * 0.85);
                double fillPct = (wireAreaMm2 / internalDuctArea) * 100.0;
                if (fillPct > 50.0 && d[4] < 80.0)
                {
                    d[4] = 80.0;
                    d[2] = Math.Max(d[2], 60.0);
                    internalDuctArea = d[2] * d[4] * 0.85;
                    fillPct = (wireAreaMm2 / internalDuctArea) * 100.0;
                }
                maxFill = Math.Max(maxFill, fillPct);
            }
            plan.MaxDuctFillPercent = Math.Round(maxFill, 1);
        }

        private static double? FindNearestHorizontalDuctY(PanelManufacturingPlan plan, double targetY)
        {
            double bestDist = double.MaxValue;
            double? bestY = null;
            for (int i = 0; i < plan.WireDuctsMm.Count; i++)
            {
                double[] d = plan.WireDuctsMm[i];
                if (d[2] < d[3]) continue;
                double midY = d[1] + d[3] * 0.5;
                double dist = Math.Abs(midY - targetY);
                if (dist < bestDist) { bestDist = dist; bestY = midY; }
            }
            return bestY;
        }

        private static double ComputePolyline3DLength(List<double[]> pts)
        {
            double sum = 0.0;
            for (int i = 1; i < pts.Count; i++)
            {
                double dx = pts[i][0] - pts[i - 1][0];
                double dy = pts[i][1] - pts[i - 1][1];
                double dz = pts[i][2] - pts[i - 1][2];
                sum += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return Math.Round(sum, 1);
        }

        // ==================== CAD Parsers & Live 2D/Busbar/SLD Scanners ====================

        private static List<PlacedPanelComponent> ParseRawPanelComponentsArg(
            SwitchgearCatalogData catalog, object rawCompsObj, string defaultMfg, double faultKa)
        {
            List<PlacedPanelComponent> list = new List<PlacedPanelComponent>();
            IEnumerable en = rawCompsObj as IEnumerable;
            if (en == null || rawCompsObj is string) return list;

            int seq = 0;
            foreach (object obj in en)
            {
                Dictionary<string, object> d = obj as Dictionary<string, object>;
                if (d == null) continue;
                int qty = Math.Max(1, GetInt(d, "qty", 1));
                string baseTag = GetStr(d, "tag") ?? GetStr(d, "tag1");
                string pn = GetStr(d, "part_number") ?? GetStr(d, "cat");
                string mfg = GetStr(d, "manufacturer") ?? GetStr(d, "mfg") ?? defaultMfg;
                string cat = GetStr(d, "category") ?? GetStr(d, "type") ?? "";
                int poles = Math.Max(1, GetInt(d, "poles", 1));
                double amps = Math.Max(1.0, GetDouble(d, "rating_a", GetDouble(d, "rated_amps", 16.0)));
                string cName = GetStr(d, "circuit_name") ?? GetStr(d, "circuit") ?? "";
                string phase = (GetStr(d, "phase") ?? (poles >= 3 ? "RYB" : "R")).Trim().ToUpperInvariant();
                double cabMm2 = Math.Max(1.5, GetDouble(d, "cable_mm2", amps > 100 ? 35.0 : (amps > 40 ? 10.0 : (amps > 20 ? 4.0 : 2.5))));
                string connTo = GetStr(d, "connected_to") ?? GetStr(d, "downstream_tag") ?? "";

                SwitchgearPartSpec spec = ResolvePartFromCatalog(
                    catalog, pn, mfg, cat, poles, amps, faultKa,
                    GetNullableDouble(d, "width_mm"),
                    GetNullableDouble(d, "height_mm"),
                    GetNullableDouble(d, "depth_mm"),
                    GetNullableDouble(d, "heat_loss_w"));

                for (int q = 0; q < qty; q++)
                {
                    seq++;
                    string tag = !string.IsNullOrWhiteSpace(baseTag)
                        ? (qty > 1 ? (baseTag.Trim() + "_" + (q + 1).ToString(CultureInfo.InvariantCulture)) : baseTag.Trim())
                        : (spec.Category == "vfd" ? "VFD" : (spec.Category == "contactor" ? "KM" : (spec.Category == "psu" ? "PS" : "QF"))) + seq.ToString(CultureInfo.InvariantCulture);
                    list.Add(new PlacedPanelComponent
                    {
                        Tag = tag,
                        Part = ClonePartSpec(spec),
                        Qty = 1,
                        CircuitName = !string.IsNullOrWhiteSpace(cName) ? cName : tag,
                        Phase = phase,
                        CableMm2 = cabMm2,
                        FromTerminal = "1/L1",
                        ToTerminal = "2/T1",
                        WireNo = GetStr(d, "wire_no") ?? ("W-" + seq.ToString("00", CultureInfo.InvariantCulture)),
                        ConnectedToTag = connTo.Trim()
                    });
                }
            }
            return list;
        }

        private static List<PlacedPanelComponent> ConvertPanelScheduleWaysToComponents(
            SwitchgearCatalogData catalog, BalancedPanelSchedule panel, string manufacturer, double faultKa)
        {
            List<PlacedPanelComponent> list = new List<PlacedPanelComponent>();
            if (panel == null) return list;

            if (panel.MainFeeder != null)
            {
                SwitchgearPartSpec mainSpec = ResolvePartFromCatalog(
                    catalog, null, manufacturer,
                    panel.MainFeeder.BreakerRatingA >= 630.0 ? "acb" : (panel.MainFeeder.BreakerRatingA > 63.0 ? "mccb" : "mcb"),
                    3, panel.MainFeeder.BreakerRatingA, faultKa, null, null, null, null);
                list.Add(new PlacedPanelComponent
                {
                    Tag = "QFMAIN",
                    Part = mainSpec,
                    Qty = 1,
                    CircuitName = panel.PanelName + "-MAIN",
                    Phase = "RYB",
                    CableMm2 = Math.Max(10.0, panel.MainFeeder.CableSizeMm2),
                    Zone = "incomer",
                    FromTerminal = "1,3,5",
                    ToTerminal = "2,4,6",
                    WireNo = "MAIN-FEED"
                });
            }

            int seq = 0;
            for (int i = 0; i < panel.Ways.Count; i++)
            {
                BalancedCircuitWay w = panel.Ways[i];
                if (w.IsSpare) continue;
                seq++;
                double inA = w.Calc != null ? w.Calc.BreakerRatingA : 16.0;
                double cab = w.Calc != null ? w.Calc.CableSizeMm2 : 2.5;
                string cat = inA > 63.0 ? "mccb" : "mcb";
                SwitchgearPartSpec spec = ResolvePartFromCatalog(catalog, null, manufacturer, cat, w.Phases, inA, faultKa, null, null, null, null);
                list.Add(new PlacedPanelComponent
                {
                    Tag = "QF" + seq.ToString(CultureInfo.InvariantCulture),
                    Part = spec,
                    Qty = 1,
                    CircuitName = w.CircuitName,
                    Phase = string.IsNullOrWhiteSpace(w.PhaseLabel) ? "R" : w.PhaseLabel,
                    CableMm2 = cab,
                    Zone = "distribution_din",
                    FromTerminal = "1/L1",
                    ToTerminal = "2/T1",
                    WireNo = "W-" + seq.ToString("00", CultureInfo.InvariantCulture)
                });
            }
            return list;
        }

        private static string GetEntityXDataAscii(Entity ent, string regApp)
        {
            if (ent == null) return null;
            ResultBuffer rb = ent.GetXDataForApplication(regApp);
            if (rb == null) return null;
            TypedValue[] vals = rb.AsArray();
            for (int vi = 0; vi < vals.Length; vi++)
            {
                if (vals[vi].TypeCode == (int)DxfCode.ExtendedDataAsciiString && vals[vi].Value != null)
                    return vals[vi].Value.ToString();
            }
            return null;
        }

        private static void ApplyLive2DBusbarOverrides(
            Database db, Transaction tr, SwitchgearCatalogData catalog, PanelManufacturingPlan plan,
            double originXMm, double originYMm, double mmToDwg)
        {
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            Dictionary<string, PanelBusbarRun> liveMains = new Dictionary<string, PanelBusbarRun>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId oid in ms)
            {
                if (oid.IsErased) continue;
                Polyline pl = tr.GetObject(oid, OpenMode.ForRead) as Polyline;
                if (pl == null || pl.IsErased || pl.NumberOfVertices < 2) continue;
                string xd = GetEntityXDataAscii(pl, PanelBusbarRegApp);
                if (string.IsNullOrEmpty(xd)) continue;
                string[] p = xd.Split('|');
                if (p.Length < 8 || p[7] != "MAIN") continue;
                string lbl = p[1], desig = p[2];
                double wMm = 20.0, tMm = 5.0; int runs = 1;
                double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out wMm);
                double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out tMm);
                int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out runs);
                CopperBusbarSpec matchedSpec = MatchNearestStandardBusbar(catalog, desig, null);
                if ((lbl == "R" || lbl == "Y" || lbl == "B") && string.Equals(matchedSpec.Designation, desig, StringComparison.OrdinalIgnoreCase))
                    plan.MainBusbar = matchedSpec;
                PanelBusbarRun run = new PanelBusbarRun
                {
                    Label = lbl,
                    Designation = desig,
                    ColorIndex = GetPhaseColorIndex(lbl),
                    WidthMm = wMm,
                    ThicknessMm = tMm,
                    Runs = runs
                };
                for (int vi = 0; vi < pl.NumberOfVertices; vi++)
                {
                    Point2d p2 = pl.GetPoint2dAt(vi);
                    run.PointsMm.Add(new double[] { Math.Round(p2.X / mmToDwg - originXMm, 2), Math.Round(p2.Y / mmToDwg - originYMm, 2), 45.0 });
                }
                liveMains[lbl] = run;
            }
            if (liveMains.Count == 0) return;
            for (int i = 0; i < plan.Busbars.Count; i++)
            {
                PanelBusbarRun live;
                if (!plan.Busbars[i].IsDropper && !plan.Busbars[i].IsComb && liveMains.TryGetValue(plan.Busbars[i].Label, out live))
                    plan.Busbars[i] = live;
            }
        }

        private static bool TryExtract2DPanelComponentsFromDrawing(
            Database db,
            Transaction tr,
            SwitchgearCatalogData catalog,
            List<string> selectedHandles,
            double mmToDwg,
            out List<PlacedPanelComponent> outComps,
            out double panelOriginXMm,
            out double panelOriginYMm,
            out double encWMm,
            out double encHMm,
            out List<double> railCenterYsMm)
        {
            outComps = new List<PlacedPanelComponent>();
            panelOriginXMm = 0.0;
            panelOriginYMm = 0.0;
            encWMm = 0.0;
            encHMm = 0.0;
            railCenterYsMm = new List<double>();

            HashSet<string> selSet = selectedHandles != null && selectedHandles.Count > 0
                ? new HashSet<string>(selectedHandles, StringComparer.OrdinalIgnoreCase)
                : null;

            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            double? encBorderMinX = null, encBorderMinY = null, encBorderMaxX = null, encBorderMaxY = null;

            foreach (ObjectId oid in ms)
            {
                if (oid.IsErased) continue;
                Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;

                string railXd = GetEntityXDataAscii(ent, PanelRailRegApp);
                if (!string.IsNullOrEmpty(railXd))
                {
                    string[] rp = railXd.Split('|');
                    double rcY;
                    if (rp.Length >= 3 && double.TryParse(rp[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rcY))
                        railCenterYsMm.Add(rcY);
                    continue;
                }

                if (selSet != null && !selSet.Contains(ent.Handle.ToString())) continue;

                if (ent is Polyline && string.Equals(ent.Layer, LayerPanel2D, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Extents3d ext = ent.GeometricExtents;
                        double w = ext.MaxPoint.X - ext.MinPoint.X;
                        double h = ext.MaxPoint.Y - ext.MinPoint.Y;
                        if (w >= 350.0 * mmToDwg && h >= 500.0 * mmToDwg &&
                            (!encBorderMinX.HasValue || (w * h) > ((encBorderMaxX.Value - encBorderMinX.Value) * (encBorderMaxY.Value - encBorderMinY.Value))))
                        {
                            encBorderMinX = ext.MinPoint.X;
                            encBorderMinY = ext.MinPoint.Y;
                            encBorderMaxX = ext.MaxPoint.X;
                            encBorderMaxY = ext.MaxPoint.Y;
                        }
                    }
                    catch { }
                    continue;
                }

                BlockReference br = ent as BlockReference;
                if (br == null) continue;

                string xdataRaw = GetEntityXDataAscii(br, PanelCompRegApp);
                string tag1 = "", catPart = "", mfg = "", ratingStr = "", wireNo = "";
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    AttributeReference ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                    if (ar == null) continue;
                    string t = (ar.Tag ?? "").Trim().ToUpperInvariant();
                    string v = (ar.TextString ?? "").Trim();
                    if (t == "TAG1") tag1 = v;
                    else if (t == "CAT") catPart = v;
                    else if (t == "MFG") mfg = v;
                    else if (t == "RATING1") ratingStr = v;
                    else if (t == "WIRENO") wireNo = v;
                }

                bool isPanelComp = !string.IsNullOrEmpty(xdataRaw) ||
                    (string.Equals(br.Layer, LayerPanel2D, StringComparison.OrdinalIgnoreCase) && (!string.IsNullOrEmpty(tag1) || !string.IsNullOrEmpty(catPart))) ||
                    (!string.IsNullOrEmpty(tag1) && !string.IsNullOrEmpty(catPart));
                if (!isPanelComp) continue;

                string catType = "mcb", circName = "", phase = "R", connTo = "";
                int poles = 1;
                double amps = 16.0, cabMm2 = 2.5;
                double? xdW = null, xdH = null, xdD = null;
                if (!string.IsNullOrEmpty(xdataRaw))
                {
                    string[] parts = xdataRaw.Split('|');
                    if (parts.Length >= 10)
                    {
                        if (string.IsNullOrEmpty(tag1)) tag1 = parts[1];
                        if (string.IsNullOrEmpty(catPart)) catPart = parts[2];
                        catType = parts[3];
                        int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out poles);
                        double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out amps);
                        circName = parts[6];
                        phase = parts[7];
                        double.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out cabMm2);
                        string[] dimS = parts[9].Split('x');
                        double tmp;
                        if (dimS.Length >= 3)
                        {
                            if (double.TryParse(dimS[0], NumberStyles.Float, CultureInfo.InvariantCulture, out tmp)) xdW = tmp;
                            if (double.TryParse(dimS[1], NumberStyles.Float, CultureInfo.InvariantCulture, out tmp)) xdH = tmp;
                            if (double.TryParse(dimS[2], NumberStyles.Float, CultureInfo.InvariantCulture, out tmp)) xdD = tmp;
                        }
                        if (parts.Length >= 11) connTo = parts[10];
                    }
                }
                else
                {
                    Match rm = Regex.Match(ratingStr ?? "", @"(\d+(?:\.\d+)?)\s*A", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                    if (rm.Success) double.TryParse(rm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out amps);
                }

                SwitchgearPartSpec spec = ResolvePartFromCatalog(catalog, catPart, mfg, catType, poles, amps, 25.0, xdW, xdH, xdD, null);
                PlacedPanelComponent pc = new PlacedPanelComponent
                {
                    Tag = string.IsNullOrEmpty(tag1) ? ("COMP-" + (outComps.Count + 1).ToString(CultureInfo.InvariantCulture)) : tag1,
                    Part = spec,
                    Qty = 1,
                    CircuitName = !string.IsNullOrEmpty(circName) ? circName : tag1,
                    Phase = string.IsNullOrEmpty(phase) ? "R" : phase,
                    CableMm2 = cabMm2,
                    XMm = br.Position.X / mmToDwg,
                    YMm = br.Position.Y / mmToDwg,
                    ZMm = spec.IsDinRail ? 10.5 : 3.0,
                    WireNo = wireNo,
                    ConnectedToTag = connTo,
                    HasManualCoords = true,
                    Zone = (spec.Category == "vfd" || spec.Category == "psu") ? "thermal_top"
                        : (spec.Category == "terminal" ? "terminals"
                        : ((spec.Category == "acb" || spec.Category == "mccb") ? "incomer" : "distribution_din"))
                };
                outComps.Add(pc);
                minX = Math.Min(minX, pc.XMm);
                minY = Math.Min(minY, pc.YMm);
                maxX = Math.Max(maxX, pc.XMm + spec.WidthMm);
                maxY = Math.Max(maxY, pc.YMm + spec.HeightMm);
            }

            if (outComps.Count == 0) return false;
            if (encBorderMinX.HasValue)
            {
                panelOriginXMm = encBorderMinX.Value / mmToDwg;
                panelOriginYMm = encBorderMinY.Value / mmToDwg;
                encWMm = (encBorderMaxX.Value - encBorderMinX.Value) / mmToDwg;
                encHMm = (encBorderMaxY.Value - encBorderMinY.Value) / mmToDwg;
            }
            else
            {
                panelOriginXMm = Math.Max(0.0, minX - 68.0);
                panelOriginYMm = Math.Max(0.0, minY - 120.0);
                encWMm = Math.Max(600.0, Math.Ceiling(((maxX - minX) + 150.0) / 100.0) * 100.0);
                encHMm = Math.Max(800.0, Math.Ceiling(((maxY - minY) + 260.0) / 200.0) * 200.0);
            }
            for (int i = 0; i < outComps.Count; i++)
            {
                outComps[i].XMm = Math.Round(outComps[i].XMm - panelOriginXMm, 2);
                outComps[i].YMm = Math.Round(outComps[i].YMm - panelOriginYMm, 2);
            }
            return true;
        }

        private static List<PlacedPanelComponent> ParseSldFromDrawing(
            Database db, Transaction tr, SwitchgearCatalogData catalog, string defaultMfg, double faultKa, out string detectedPanelName)
        {
            detectedPanelName = "SLD-PANEL";
            List<PlacedPanelComponent> result = new List<PlacedPanelComponent>();
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

            // 1. Check for rich AIENG_PANEL_SLD tags written by sld_from_panel
            List<string> rawSldTags = new List<string>();
            List<string> genericLabels = new List<string>();
            foreach (ObjectId oid in ms)
            {
                if (oid.IsErased) continue;
                Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased) continue;
                string sldXd = GetEntityXDataAscii(ent, PanelSldRegApp);
                if (!string.IsNullOrEmpty(sldXd)) rawSldTags.Add(sldXd);
                else if (string.Equals(ent.Layer, "_AI_SLD", StringComparison.OrdinalIgnoreCase) || string.Equals(ent.Layer, "0", StringComparison.OrdinalIgnoreCase))
                {
                    string txt = (ent as MText != null) ? ((MText)ent).Text : ((ent as DBText != null) ? ((DBText)ent).TextString : null);
                    if (!string.IsNullOrWhiteSpace(txt)) genericLabels.Add(txt.Trim());
                }
            }

            if (rawSldTags.Count > 0)
            {
                for (int i = 0; i < rawSldTags.Count; i++)
                {
                    string[] p = rawSldTags[i].Split('|');
                    if (p.Length < 9) continue;
                    if (!string.IsNullOrEmpty(p[1])) detectedPanelName = p[1];
                    string tag = p[3], pn = p[4], cat = p[5];
                    double amps = 16.0, cabMm2 = 2.5; int poles = 1;
                    double.TryParse(p[6], NumberStyles.Float, CultureInfo.InvariantCulture, out amps);
                    int.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out poles);
                    double.TryParse(p[8], NumberStyles.Float, CultureInfo.InvariantCulture, out cabMm2);
                    string connTo = p.Length >= 10 ? p[9] : "";
                    SwitchgearPartSpec spec = ResolvePartFromCatalog(catalog, pn, defaultMfg, cat, poles, amps, faultKa, null, null, null, null);
                    result.Add(new PlacedPanelComponent
                    {
                        Tag = tag,
                        Part = spec,
                        Qty = 1,
                        CircuitName = tag,
                        Phase = poles >= 3 ? "RYB" : "R",
                        CableMm2 = cabMm2,
                        ConnectedToTag = connTo,
                        Zone = (cat == "mccb" || cat == "acb") && result.Count == 0 ? "incomer" : ((cat == "vfd" || cat == "psu") ? "thermal_top" : "distribution_din")
                    });
                }
                if (result.Count > 0) return result;
            }

            // 2. Parse freehand / general SLD labels ("100A MCCB", "32A MCB -> KM1 -> VFD 2.2kW", etc.)
            int seq = 0;
            for (int i = 0; i < genericLabels.Count; i++)
            {
                string line = genericLabels[i];
                if (line.IndexOf("SINGLE LINE", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Match ampsM = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*A\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                if (!ampsM.Success && line.IndexOf("VFD", StringComparison.OrdinalIgnoreCase) < 0 && line.IndexOf("KM", StringComparison.OrdinalIgnoreCase) < 0) continue;
                seq++;
                double amps = ampsM.Success ? double.Parse(ampsM.Groups[1].Value, CultureInfo.InvariantCulture) : 16.0;
                bool isMccb = line.IndexOf("MCCB", StringComparison.OrdinalIgnoreCase) >= 0 || amps > 63.0;
                bool isVfd = line.IndexOf("VFD", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("INVERTER", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isKm = line.IndexOf("KM", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("CONTACTOR", StringComparison.OrdinalIgnoreCase) >= 0;
                int poles = (isMccb || isVfd || isKm || line.IndexOf("3P", StringComparison.OrdinalIgnoreCase) >= 0) ? 3 : 1;
                string cat = isVfd ? "vfd" : (isKm ? "contactor" : (isMccb ? "mccb" : "mcb"));
                SwitchgearPartSpec spec = ResolvePartFromCatalog(catalog, null, defaultMfg, cat, poles, amps, faultKa, null, null, null, null);
                result.Add(new PlacedPanelComponent
                {
                    Tag = isMccb && seq == 1 ? "QFMAIN" : ("QF" + seq.ToString(CultureInfo.InvariantCulture)),
                    Part = spec,
                    Qty = 1,
                    CircuitName = "SLD-" + seq.ToString(CultureInfo.InvariantCulture),
                    Phase = poles >= 3 ? "RYB" : "R",
                    CableMm2 = amps > 63 ? 35.0 : (amps > 25 ? 6.0 : 2.5)
                });
            }
            return result;
        }

        // ==================== CAD 2D/3D Renderers, Side Section, CNC & Tracer ====================

        private static void EraseLayerEntities(BlockTableRecord ms, Transaction tr, string layerName, List<ObjectId> erasedIds)
        {
            foreach (ObjectId oid in ms)
            {
                if (oid.IsErased) continue;
                Entity e = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                if (e != null && !e.IsErased && string.Equals(e.Layer, layerName, StringComparison.OrdinalIgnoreCase))
                {
                    Entity ew = (Entity)tr.GetObject(oid, OpenMode.ForWrite);
                    ew.Erase(true);
                    erasedIds.Add(oid);
                }
            }
        }

        private static void EraseAllPanelLayers(BlockTableRecord ms, Transaction tr, bool draw2D, bool draw3D, bool drawSide, bool drawCnc, List<ObjectId> erasedIds)
        {
            if (draw2D)
            {
                EraseLayerEntities(ms, tr, LayerPanel2D, erasedIds);
                EraseLayerEntities(ms, tr, LayerPanelGuideAlign, erasedIds);
            }
            if (drawSide) EraseLayerEntities(ms, tr, LayerPanelSideSection, erasedIds);
            if (drawCnc) EraseLayerEntities(ms, tr, LayerPanelCncDrilling, erasedIds);
            if (draw3D)
            {
                EraseLayerEntities(ms, tr, LayerPanel3D00Enclosure, erasedIds);
                EraseLayerEntities(ms, tr, LayerPanel3D01MountingPlate, erasedIds);
                EraseLayerEntities(ms, tr, LayerPanel3D02RailsInsulators, erasedIds);
                EraseLayerEntities(ms, tr, LayerPanel3D03DuctsBody, erasedIds);
                EraseLayerEntities(ms, tr, LayerPanel3D03DuctsCover, erasedIds);
                EraseLayerEntities(ms, tr, LayerPanel3D04Components, erasedIds);
                EraseLayerEntities(ms, tr, LayerPanel3D05BusbarsCopper, erasedIds);
                EraseLayerEntities(ms, tr, LayerPanel3D06Wiring, erasedIds);
                EraseLayerEntities(ms, tr, LayerPanel3D07DoorDeadFront, erasedIds);
            }
        }

        private static Polyline Draw2DRect(
            BlockTableRecord ms, Transaction tr, string layer, short colorIdx,
            double xMm, double yMm, double wMm, double hMm, double scale, bool thickBorder,
            List<ObjectId> createdIds)
        {
            Polyline pl = new Polyline(4);
            pl.SetDatabaseDefaults(ms.Database);
            pl.Layer = layer;
            pl.ColorIndex = colorIdx;
            double x1 = xMm * scale, y1 = yMm * scale;
            double x2 = (xMm + wMm) * scale, y2 = (yMm + hMm) * scale;
            double wid = thickBorder ? (1.2 * scale) : 0.0;
            pl.AddVertexAt(0, new Point2d(x1, y1), 0.0, wid, wid);
            pl.AddVertexAt(1, new Point2d(x2, y1), 0.0, wid, wid);
            pl.AddVertexAt(2, new Point2d(x2, y2), 0.0, wid, wid);
            pl.AddVertexAt(3, new Point2d(x1, y2), 0.0, wid, wid);
            pl.Closed = true;
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            createdIds.Add(pl.ObjectId);
            return pl;
        }

        private static void DrawCircleMm(
            BlockTableRecord ms, Transaction tr, string layer, short colorIdx,
            double cxMm, double cyMm, double diameterMm, double scale, List<ObjectId> createdIds)
        {
            Circle c = new Circle(new Point3d(cxMm * scale, cyMm * scale, 0.0), Vector3d.ZAxis, Math.Max(0.5, diameterMm * 0.5) * scale);
            c.SetDatabaseDefaults(ms.Database);
            c.Layer = layer;
            c.ColorIndex = colorIdx;
            ms.AppendEntity(c);
            tr.AddNewlyCreatedDBObject(c, true);
            createdIds.Add(c.ObjectId);
        }

        private static ObjectId GetOrCreate2DPanelComponentBlock(
            Database db, BlockTable bt, Transaction tr, SwitchgearPartSpec part, ObjectId unicodeStyleId, double scale)
        {
            string bName = "AI_PNL2D_" + SanitizeCatalogBlockName(part.PartNumber);
            if (bt.Has(bName)) return bt[bName];

            BlockTableRecord btr = new BlockTableRecord();
            btr.Name = bName;
            btr.Origin = Point3d.Origin;
            double w = part.WidthMm * scale;
            double h = part.HeightMm * scale;

            Polyline body = new Polyline(4);
            body.SetDatabaseDefaults(db);
            body.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
            body.AddVertexAt(1, new Point2d(w, 0), 0, 0, 0);
            body.AddVertexAt(2, new Point2d(w, h), 0, 0, 0);
            body.AddVertexAt(3, new Point2d(0, h), 0, 0, 0);
            body.Closed = true;
            btr.AppendEntity(body);

            // DIN clip center tick on left edge
            Polyline clip = new Polyline(2);
            clip.SetDatabaseDefaults(db);
            clip.ColorIndex = 4;
            clip.AddVertexAt(0, new Point2d(-1.5 * scale, h * 0.5), 0, 0, 0);
            clip.AddVertexAt(1, new Point2d(w + 1.5 * scale, h * 0.5), 0, 0, 0);
            btr.AppendEntity(clip);

            // Toggle/actuator box
            Polyline toggle = new Polyline(4);
            toggle.SetDatabaseDefaults(db);
            toggle.ColorIndex = 1;
            toggle.AddVertexAt(0, new Point2d(w * 0.25, h * 0.42), 0, 0, 0);
            toggle.AddVertexAt(1, new Point2d(w * 0.75, h * 0.42), 0, 0, 0);
            toggle.AddVertexAt(2, new Point2d(w * 0.75, h * 0.58), 0, 0, 0);
            toggle.AddVertexAt(3, new Point2d(w * 0.25, h * 0.58), 0, 0, 0);
            toggle.Closed = true;
            btr.AppendEntity(toggle);

            string[] tags = new string[] { "TAG1", "MFG", "CAT", "RATING1", "LOC", "INST", "TERM01", "TERM02", "WIRENO" };
            for (int i = 0; i < tags.Length; i++)
            {
                AttributeDefinition ad = new AttributeDefinition();
                ad.SetDatabaseDefaults(db);
                ad.Tag = tags[i];
                ad.Prompt = tags[i];
                ad.TextString = "";
                ad.TextStyleId = unicodeStyleId;
                ad.Height = Math.Max(2.8 * scale, Math.Min(6.5 * scale, w * 0.22));
                ad.Position = i == 0
                    ? new Point3d(w * 0.08, h + 2.5 * scale, 0.0)
                    : (i == 3 ? new Point3d(w * 0.08, h * 0.20, 0.0) : new Point3d(w * 0.08, h * 0.05, 0.0));
                ad.Invisible = (i != 0 && i != 3);
                ad.ColorIndex = i == 0 ? (short)3 : (short)7;
                btr.AppendEntity(ad);
            }

            if (!bt.IsWriteEnabled) bt.UpgradeOpen();
            ObjectId btrId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            return btrId;
        }

        private static ObjectId GetOrCreate3DPanelComponentBlock(
            Database db, BlockTable bt, Transaction tr, PlacedPanelComponent comp, double scale)
        {
            SwitchgearPartSpec part = comp.Part;
            string bName = "AI_PNL3D_" + SanitizeCatalogBlockName(part.PartNumber);
            if (bt.Has(bName)) return bt[bName];

            if (comp.VaultSource == "vault_dwg" && !string.IsNullOrEmpty(comp.VaultPath) && File.Exists(comp.VaultPath))
            {
                try
                {
                    using (Database srcDb = new Database(false, true))
                    {
                        srcDb.ReadDwgFile(comp.VaultPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                        return db.Insert(bName, srcDb, true);
                    }
                }
                catch { comp.VaultSource = "parametric_3d_fallback"; }
            }

            BlockTableRecord btr = new BlockTableRecord();
            btr.Name = bName;
            btr.Origin = Point3d.Origin;

            double w = part.WidthMm * scale;
            double h = part.HeightMm * scale;
            double d = part.DepthMm * scale;
            short bodyColor = part.Category == "vfd" ? (short)2 : (part.Category == "mccb" || part.Category == "acb" ? (short)7 : (part.Category == "contactor" ? (short)6 : (short)8));

            AppendSolidBoxToBtr(db, btr, w, h, d * 0.85, w * 0.5, h * 0.5, d * 0.425, bodyColor);
            AppendSolidBoxToBtr(db, btr, w * 0.65, h * 0.48, d * 0.15, w * 0.5, h * 0.5, d * 0.90, 252);
            AppendSolidBoxToBtr(db, btr, w * 0.40, h * 0.14, d * 0.12, w * 0.5, h * 0.5, d * 1.02, 1);

            if (!bt.IsWriteEnabled) bt.UpgradeOpen();
            ObjectId btrId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            return btrId;
        }

        private static void AppendSolidBoxToBtr(
            Database db, BlockTableRecord btr, double sx, double sy, double sz,
            double cx, double cy, double cz, short colorIndex)
        {
            double x = Math.Max(1e-4, sx), y = Math.Max(1e-4, sy), z = Math.Max(1e-4, sz);
            try
            {
                Solid3d s = new Solid3d();
                s.SetDatabaseDefaults(db);
                s.ColorIndex = colorIndex;
                s.CreateBox(x, y, z);
                s.TransformBy(Matrix3d.Displacement(new Vector3d(cx, cy, cz)));
                btr.AppendEntity(s);
            }
            catch
            {
                Polyline pl = new Polyline(4);
                pl.SetDatabaseDefaults(db);
                pl.ColorIndex = colorIndex;
                pl.AddVertexAt(0, new Point2d(cx - x * 0.5, cy - y * 0.5), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(cx + x * 0.5, cy - y * 0.5), 0, 0, 0);
                pl.AddVertexAt(2, new Point2d(cx + x * 0.5, cy + y * 0.5), 0, 0, 0);
                pl.AddVertexAt(3, new Point2d(cx - x * 0.5, cy + y * 0.5), 0, 0, 0);
                pl.Closed = true;
                pl.Elevation = cz;
                btr.AppendEntity(pl);
            }
        }

        private static void AddSolidBoxToMs(
            Database db, BlockTableRecord ms, Transaction tr, string layer, short colorIndex,
            double sx, double sy, double sz, double cx, double cy, double cz, List<ObjectId> createdIds)
        {
            double x = Math.Max(1e-4, sx), y = Math.Max(1e-4, sy), z = Math.Max(1e-4, sz);
            try
            {
                Solid3d s = new Solid3d();
                s.SetDatabaseDefaults(db);
                s.Layer = layer;
                s.ColorIndex = colorIndex;
                s.CreateBox(x, y, z);
                s.TransformBy(Matrix3d.Displacement(new Vector3d(cx, cy, cz)));
                ms.AppendEntity(s);
                tr.AddNewlyCreatedDBObject(s, true);
                createdIds.Add(s.ObjectId);
            }
            catch
            {
                Polyline pl = new Polyline(4);
                pl.SetDatabaseDefaults(db);
                pl.Layer = layer;
                pl.ColorIndex = colorIndex;
                pl.AddVertexAt(0, new Point2d(cx - x * 0.5, cy - y * 0.5), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(cx + x * 0.5, cy - y * 0.5), 0, 0, 0);
                pl.AddVertexAt(2, new Point2d(cx + x * 0.5, cy + y * 0.5), 0, 0, 0);
                pl.AddVertexAt(3, new Point2d(cx - x * 0.5, cy + y * 0.5), 0, 0, 0);
                pl.Closed = true;
                pl.Elevation = cz;
                ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                createdIds.Add(pl.ObjectId);
            }
        }

        private static void DrawPanel2DAnd3DAssemblies(
            Database db,
            BlockTableRecord ms,
            Transaction tr,
            PanelManufacturingPlan plan,
            double origin2DXMm,
            double origin2DYMm,
            double origin3DXMm,
            double origin3DYMm,
            double scale,
            bool draw2D,
            bool draw3D,
            bool drawSideSection,
            bool drawCncDrilling,
            ObjectId unicodeStyleId,
            List<ObjectId> createdIds)
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            EnsureRegApp(db, tr, PanelCompRegApp);
            EnsureRegApp(db, tr, PanelBusbarRegApp);
            EnsureRegApp(db, tr, PanelNetRegApp);
            EnsureRegApp(db, tr, PanelRailRegApp);

            double encW = plan.Enclosure.WidthMm;
            double encH = plan.Enclosure.HeightMm;
            double encD = plan.Enclosure.DepthMm;

            if (draw2D)
            {
                string lyr2d = EnsureLayerSafe(db, tr, LayerPanel2D, 7, "Continuous");
                string lyrAlign = EnsureLayerSafe(db, tr, LayerPanelGuideAlign, 4, "Continuous");

                Draw2DRect(ms, tr, lyr2d, 7, origin2DXMm, origin2DYMm, encW, encH, scale, true, createdIds);
                Draw2DRect(ms, tr, lyr2d, 8, origin2DXMm + 25.0, origin2DYMm + 25.0, encW - 50.0, encH - 50.0, scale, false, createdIds);

                double glandY = plan.CableEntry == "bottom" ? (origin2DYMm + 12.0) : (origin2DYMm + encH - 42.0);
                Draw2DRect(ms, tr, lyr2d, 3, origin2DXMm + 60.0, glandY, encW - 120.0, 28.0, scale, false, createdIds);
                AddSldText(ms, tr, unicodeStyleId, lyr2d, 3,
                    "CABLE ENTRY: " + plan.CableEntry.ToUpperInvariant() + " (GLAND PLATE)",
                    (origin2DXMm + encW * 0.5) * scale, (glandY + 14.0) * scale, 9.0 * scale, (encW - 140.0) * scale,
                    AttachmentPoint.MiddleCenter, 0.0, createdIds);

                // DIN Rails + Magnetic Centerline Guides (AIENG_PANEL_RAIL XData) + 18mm module ticks
                for (int i = 0; i < plan.DinRailsMm.Count; i++)
                {
                    double[] r = plan.DinRailsMm[i];
                    Draw2DRect(ms, tr, lyr2d, 253, origin2DXMm + r[0], origin2DYMm + r[1], r[2], r[3], scale, false, createdIds);
                    double rcY = r[1] + r[3] * 0.5;
                    Polyline guide = new Polyline(2);
                    guide.SetDatabaseDefaults(db);
                    guide.Layer = lyrAlign;
                    guide.ColorIndex = 4;
                    guide.AddVertexAt(0, new Point2d((origin2DXMm + r[0]) * scale, (origin2DYMm + rcY) * scale), 0, 0, 0);
                    guide.AddVertexAt(1, new Point2d((origin2DXMm + r[0] + r[2]) * scale, (origin2DYMm + rcY) * scale), 0, 0, 0);
                    ms.AppendEntity(guide);
                    tr.AddNewlyCreatedDBObject(guide, true);
                    guide.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, PanelRailRegApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, string.Format(
                            CultureInfo.InvariantCulture, "v1|{0}|{1:0.##}|{2:0.##}|{3:0.##}", i + 1, rcY, r[0], r[0] + r[2])));
                    createdIds.Add(guide.ObjectId);
                }

                // Wire Ducts + Fill ratio annotation
                for (int i = 0; i < plan.WireDuctsMm.Count; i++)
                {
                    double[] d = plan.WireDuctsMm[i];
                    Draw2DRect(ms, tr, lyr2d, 4, origin2DXMm + d[0], origin2DYMm + d[1], d[2], d[3], scale, false, createdIds);
                }

                // Dynamic Grip-Stretchable Copper Busbars, Z-Bend Droppers & Bolt Joints
                for (int i = 0; i < plan.Busbars.Count; i++)
                {
                    PanelBusbarRun brRun = plan.Busbars[i];
                    if (brRun.PointsMm.Count < 2) continue;
                    Polyline barPl = new Polyline(brRun.PointsMm.Count);
                    barPl.SetDatabaseDefaults(db);
                    barPl.Layer = lyr2d;
                    barPl.ColorIndex = brRun.ColorIndex;
                    double barWidDwg = Math.Max(4.0, brRun.WidthMm) * scale;
                    for (int pi = 0; pi < brRun.PointsMm.Count; pi++)
                        barPl.AddVertexAt(pi, new Point2d((origin2DXMm + brRun.PointsMm[pi][0]) * scale, (origin2DYMm + brRun.PointsMm[pi][1]) * scale), 0.0, barWidDwg, barWidDwg);
                    ms.AppendEntity(barPl);
                    tr.AddNewlyCreatedDBObject(barPl, true);
                    barPl.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, PanelBusbarRegApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, string.Format(
                            CultureInfo.InvariantCulture, "v1|{0}|{1}|{2:0.##}|{3:0.##}|{4}|{5:0.#}|{6}",
                            brRun.Label, brRun.Designation, brRun.WidthMm, brRun.ThicknessMm, brRun.Runs, plan.IncomerAmps, brRun.IsDropper ? "DROP" : (brRun.IsComb ? "COMB" : "MAIN"))));
                    createdIds.Add(barPl.ObjectId);

                    for (int bi = 0; bi < brRun.BoltJointsMm.Count; bi++)
                    {
                        double[] bj = brRun.BoltJointsMm[bi];
                        DrawCircleMm(ms, tr, lyr2d, 7, origin2DXMm + bj[0], origin2DYMm + bj[1], 9.0, scale, createdIds);
                    }
                }

                // 2D Wiring Polylines with AIENG_PANEL_NET XData so Trace Works Right from 2D Wires Too!
                for (int wi = 0; wi < plan.WireConnections.Count; wi++)
                {
                    PanelWireConnection wc = plan.WireConnections[wi];
                    if (wc.PointsMm.Count < 2) continue;
                    Polyline wPl = new Polyline(wc.PointsMm.Count);
                    wPl.SetDatabaseDefaults(db);
                    wPl.Layer = lyr2d;
                    wPl.ColorIndex = wc.ColorIndex;
                    for (int pi = 0; pi < wc.PointsMm.Count; pi++)
                        wPl.AddVertexAt(pi, new Point2d((origin2DXMm + wc.PointsMm[pi][0]) * scale, (origin2DYMm + wc.PointsMm[pi][1]) * scale), 0.0, 0.8 * scale, 0.8 * scale);
                    ms.AppendEntity(wPl);
                    tr.AddNewlyCreatedDBObject(wPl, true);
                    string netStr = string.Format(
                        CultureInfo.InvariantCulture,
                        "v1|{0}|{1}:{2}|{3}:{4}|{5:0.#}|{6}|{7}|{8:0.#}|{9}",
                        wc.WireNo, wc.FromTag, wc.FromTerm, wc.ToTag, wc.ToTerm, wc.SizeMm2, wc.Phase, wc.ColorName, wc.LengthMm, wc.FerruleLabel);
                    if (netStr.Length > 240) netStr = netStr.Substring(0, 240);
                    wPl.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, PanelNetRegApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, netStr));
                    createdIds.Add(wPl.ObjectId);
                }

                // 2D Components with ACADE Attributes + XData
                for (int i = 0; i < plan.Components.Count; i++)
                {
                    PlacedPanelComponent cp = plan.Components[i];
                    ObjectId blkId = GetOrCreate2DPanelComponentBlock(db, bt, tr, cp.Part, unicodeStyleId, scale);
                    BlockTableRecord btrDef = (BlockTableRecord)tr.GetObject(blkId, OpenMode.ForRead);
                    Point3d insPt = new Point3d((origin2DXMm + cp.XMm) * scale, (origin2DYMm + cp.YMm) * scale, 0.0);
                    BlockReference br = new BlockReference(insPt, blkId);
                    br.Layer = lyr2d;
                    ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);
                    createdIds.Add(br.ObjectId);

                    Dictionary<string, string> attMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "TAG1", cp.Tag },
                        { "MFG", cp.Part.Manufacturer },
                        { "CAT", cp.Part.PartNumber },
                        { "RATING1", cp.Part.RatedAmps.ToString("0.#", CultureInfo.InvariantCulture) + "A " + cp.Part.Poles + "P" },
                        { "LOC", plan.PanelName },
                        { "INST", cp.Zone },
                        { "TERM01", cp.FromTerminal },
                        { "TERM02", cp.ToTerminal },
                        { "WIRENO", cp.WireNo }
                    };

                    if (btrDef.HasAttributeDefinitions)
                    {
                        foreach (ObjectId aid in btrDef)
                        {
                            AttributeDefinition ad = tr.GetObject(aid, OpenMode.ForRead) as AttributeDefinition;
                            if (ad == null || ad.Constant) continue;
                            AttributeReference ar = new AttributeReference();
                            ar.SetAttributeFromBlock(ad, br.BlockTransform);
                            string val;
                            if (attMap.TryGetValue(ad.Tag, out val) && val != null) ar.TextString = val;
                            br.AttributeCollection.AppendAttribute(ar);
                            tr.AddNewlyCreatedDBObject(ar, true);
                        }
                    }

                    string xdStr = string.Format(
                        CultureInfo.InvariantCulture,
                        "v1|{0}|{1}|{2}|{3}|{4:0.#}|{5}|{6}|{7:0.#}|{8:0.#}x{9:0.#}x{10:0.#}|{11}",
                        cp.Tag, cp.Part.PartNumber, cp.Part.Category, cp.Part.Poles, cp.Part.RatedAmps,
                        cp.CircuitName ?? "", cp.Phase ?? "R", cp.CableMm2,
                        cp.Part.WidthMm, cp.Part.HeightMm, cp.Part.DepthMm, cp.ConnectedToTag ?? "");
                    if (xdStr.Length > 240) xdStr = xdStr.Substring(0, 240);
                    br.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, PanelCompRegApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, xdStr));
                }

                string titleLine = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} | {1} ({2:0}x{3:0}x{4:0}mm) | Bus: {5} | Fill: {6:0.#}% | Tmax={7:0.1}C {8}",
                    plan.PanelName, plan.Enclosure.PartNumber, encW, encH, encD,
                    plan.MainBusbar.Designation, plan.MaxDuctFillPercent, plan.InternalTopTempC,
                    plan.NaturalCoolingOk ? "[NATURAL OK]" : ("[FAN: " + (plan.SelectedFan != null ? plan.SelectedFan.PartNumber : "REQ") + "]"));
                AddSldText(ms, tr, unicodeStyleId, lyr2d, 7, titleLine,
                    origin2DXMm * scale, (origin2DYMm + encH + 35.0) * scale, 18.0 * scale, Math.Max(420.0, encW) * scale,
                    AttachmentPoint.BottomLeft, 0.0, createdIds);
            }

            // 2D Side Cross-Section View (Back->Front Z-Depth Stack + Door Clearance mm)
            if (drawSideSection)
            {
                string lyrSide = EnsureLayerSafe(db, tr, LayerPanelSideSection, 6, "Continuous");
                double sideX = origin2DXMm - encD - 160.0;
                Draw2DRect(ms, tr, lyrSide, 8, sideX, origin2DYMm, encD, encH, scale, true, createdIds); // Cabinet Side Shell
                Draw2DRect(ms, tr, lyrSide, 7, sideX, origin2DYMm + 25.0, 3.0, encH - 50.0, scale, false, createdIds); // Mounting Plate Z=0..3
                Draw2DRect(ms, tr, lyrSide, 2, sideX + encD - 5.0, origin2DYMm, 5.0, encH, scale, true, createdIds); // Front Door
                for (int i = 0; i < plan.DinRailsMm.Count; i++)
                {
                    double[] r = plan.DinRailsMm[i];
                    Draw2DRect(ms, tr, lyrSide, 253, sideX + 3.0, origin2DYMm + r[1], 7.5, r[3], scale, false, createdIds);
                }
                for (int i = 0; i < plan.WireDuctsMm.Count; i++)
                {
                    double[] d = plan.WireDuctsMm[i];
                    if (d[2] > d[3])
                        Draw2DRect(ms, tr, lyrSide, 4, sideX + 3.0, origin2DYMm + d[1], d[4], d[3], scale, false, createdIds);
                }
                for (int i = 0; i < plan.Busbars.Count; i++)
                {
                    PanelBusbarRun b = plan.Busbars[i];
                    if (b.IsDropper || b.PointsMm.Count == 0) continue;
                    double bz = b.PointsMm[0][2], by = b.PointsMm[0][1];
                    Draw2DRect(ms, tr, lyrSide, b.ColorIndex, sideX + bz - b.ThicknessMm * 0.5, origin2DYMm + by - b.WidthMm * 0.5, b.ThicknessMm, b.WidthMm, scale, true, createdIds);
                }
                for (int i = 0; i < plan.Components.Count; i++)
                {
                    PlacedPanelComponent cp = plan.Components[i];
                    Draw2DRect(ms, tr, lyrSide, cp.Zone == "thermal_top" ? (short)2 : (short)3,
                        sideX + cp.ZMm, origin2DYMm + cp.YMm, cp.Part.DepthMm, cp.Part.HeightMm, scale, false, createdIds);
                }
                AddSldText(ms, tr, unicodeStyleId, lyrSide, 6,
                    "SIDE VIEW Z-DEPTH | DOOR CLEARANCE: " + plan.MinDoorClearanceMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm",
                    sideX * scale, (origin2DYMm + encH + 35.0) * scale, 14.0 * scale, Math.Max(260.0, encD) * scale,
                    AttachmentPoint.BottomLeft, 0.0, createdIds);
            }

            // Workshop CNC Mounting Plate Drilling Template
            if (drawCncDrilling)
            {
                string lyrCnc = EnsureLayerSafe(db, tr, LayerPanelCncDrilling, 1, "Continuous");
                for (int i = 0; i < plan.CncHoles.Count; i++)
                {
                    CncDrillHole h = plan.CncHoles[i];
                    DrawCircleMm(ms, tr, lyrCnc, 1, origin2DXMm + h.XMm, origin2DYMm + h.YMm, h.DiameterMm, scale, createdIds);
                }
            }

            // 8 Ordered Physical Z-Depth 3D Layers
            if (draw3D)
            {
                string lyrEnc = EnsureLayerSafe(db, tr, LayerPanel3D00Enclosure, 8, "Continuous");
                string lyrPlate = EnsureLayerSafe(db, tr, LayerPanel3D01MountingPlate, 7, "Continuous");
                string lyrRails = EnsureLayerSafe(db, tr, LayerPanel3D02RailsInsulators, 253, "Continuous");
                string lyrDuctBody = EnsureLayerSafe(db, tr, LayerPanel3D03DuctsBody, 4, "Continuous");
                string lyrDuctCover = EnsureLayerSafe(db, tr, LayerPanel3D03DuctsCover, 140, "Continuous");
                string lyrComps = EnsureLayerSafe(db, tr, LayerPanel3D04Components, 7, "Continuous");
                string lyrBus = EnsureLayerSafe(db, tr, LayerPanel3D05BusbarsCopper, 1, "Continuous");
                string lyrWire = EnsureLayerSafe(db, tr, LayerPanel3D06Wiring, 3, "Continuous");
                string lyrDoor = EnsureLayerSafe(db, tr, LayerPanel3D07DoorDeadFront, 252, "Continuous");

                // Depth Layer 00: Enclosure Back & Sides
                double wallT = 2.5;
                AddSolidBoxToMs(db, ms, tr, lyrEnc, 8, wallT * scale, encH * scale, encD * scale,
                    (origin3DXMm + wallT * 0.5) * scale, (origin3DYMm + encH * 0.5) * scale, encD * 0.5 * scale, createdIds);
                AddSolidBoxToMs(db, ms, tr, lyrEnc, 8, wallT * scale, encH * scale, encD * scale,
                    (origin3DXMm + encW - wallT * 0.5) * scale, (origin3DYMm + encH * 0.5) * scale, encD * 0.5 * scale, createdIds);
                AddSolidBoxToMs(db, ms, tr, lyrEnc, 8, encW * scale, wallT * scale, encD * scale,
                    (origin3DXMm + encW * 0.5) * scale, (origin3DYMm + encH - wallT * 0.5) * scale, encD * 0.5 * scale, createdIds);
                AddSolidBoxToMs(db, ms, tr, lyrEnc, 8, encW * scale, wallT * scale, encD * scale,
                    (origin3DXMm + encW * 0.5) * scale, (origin3DYMm + wallT * 0.5) * scale, encD * 0.5 * scale, createdIds);
                if (plan.SelectedFan != null)
                {
                    AddSolidBoxToMs(db, ms, tr, lyrEnc, 3,
                        plan.SelectedFan.WidthMm * scale, plan.SelectedFan.DepthMm * scale, plan.SelectedFan.HeightMm * scale,
                        (origin3DXMm + encW - plan.SelectedFan.WidthMm * 0.5 - 20.0) * scale,
                        (origin3DYMm + encH + plan.SelectedFan.DepthMm * 0.5) * scale,
                        (encD * 0.5) * scale, createdIds);
                }

                // Depth Layer 01: Galvanized Mounting Plate Z = 0..3mm
                AddSolidBoxToMs(db, ms, tr, lyrPlate, 254, (encW - 40.0) * scale, (encH - 40.0) * scale, 3.0 * scale,
                    (origin3DXMm + encW * 0.5) * scale, (origin3DYMm + encH * 0.5) * scale, 1.5 * scale, createdIds);

                // Depth Layer 02: DIN Rails (Z = 3..10.5mm) + Busbar Insulators (Z = 3..42mm)
                for (int i = 0; i < plan.DinRailsMm.Count; i++)
                {
                    double[] r = plan.DinRailsMm[i];
                    AddSolidBoxToMs(db, ms, tr, lyrRails, 253,
                        r[2] * scale, r[3] * scale, r[4] * scale,
                        (origin3DXMm + r[0] + r[2] * 0.5) * scale,
                        (origin3DYMm + r[1] + r[3] * 0.5) * scale,
                        (3.0 + r[4] * 0.5) * scale, createdIds);
                }
                for (int bi = 0; bi < plan.Busbars.Count; bi++)
                {
                    PanelBusbarRun bar = plan.Busbars[bi];
                    for (int ji = 0; ji < bar.BoltJointsMm.Count; ji++)
                    {
                        if (bar.IsDropper) continue;
                        double[] bj = bar.BoltJointsMm[ji];
                        AddSolidBoxToMs(db, ms, tr, lyrRails, 1, 22.0 * scale, 22.0 * scale, (bj[2] - 3.0) * scale,
                            (origin3DXMm + bj[0]) * scale, (origin3DYMm + bj[1]) * scale, ((bj[2] + 3.0) * 0.5) * scale, createdIds);
                    }
                }

                // Depth Layer 03A + 03B: Wire Duct Body & Separate Toggleable Duct Cover
                for (int i = 0; i < plan.WireDuctsMm.Count; i++)
                {
                    double[] d = plan.WireDuctsMm[i];
                    double bodyH = Math.Max(10.0, d[4] - 3.0);
                    AddSolidBoxToMs(db, ms, tr, lyrDuctBody, 4,
                        d[2] * scale, d[3] * scale, bodyH * scale,
                        (origin3DXMm + d[0] + d[2] * 0.5) * scale,
                        (origin3DYMm + d[1] + d[3] * 0.5) * scale,
                        (3.0 + bodyH * 0.5) * scale, createdIds);
                    AddSolidBoxToMs(db, ms, tr, lyrDuctCover, 140,
                        d[2] * scale, d[3] * scale, 3.0 * scale,
                        (origin3DXMm + d[0] + d[2] * 0.5) * scale,
                        (origin3DYMm + d[1] + d[3] * 0.5) * scale,
                        (3.0 + bodyH + 1.5) * scale, createdIds);
                }

                // Depth Layer 04: 3D Components
                for (int i = 0; i < plan.Components.Count; i++)
                {
                    PlacedPanelComponent cp = plan.Components[i];
                    ObjectId blk3Id = GetOrCreate3DPanelComponentBlock(db, bt, tr, cp, scale);
                    Point3d p3 = new Point3d((origin3DXMm + cp.XMm) * scale, (origin3DYMm + cp.YMm) * scale, cp.ZMm * scale);
                    BlockReference br3 = new BlockReference(p3, blk3Id);
                    br3.Layer = lyrComps;
                    ms.AppendEntity(br3);
                    tr.AddNewlyCreatedDBObject(br3, true);
                    createdIds.Add(br3.ObjectId);
                }

                // Depth Layer 05: 3D Copper Busbars, Z-Bend Droppers, Comb Busbars & Bolt Joints
                for (int i = 0; i < plan.Busbars.Count; i++)
                {
                    PanelBusbarRun bar = plan.Busbars[i];
                    for (int pi = 1; pi < bar.PointsMm.Count; pi++)
                    {
                        double[] p0 = bar.PointsMm[pi - 1];
                        double[] p1 = bar.PointsMm[pi];
                        double dx = p1[0] - p0[0], dy = p1[1] - p0[1], dz = p1[2] - p0[2];
                        double sx = Math.Abs(dx) > 1.0 ? Math.Abs(dx) : bar.WidthMm;
                        double sy = Math.Abs(dy) > 1.0 ? Math.Abs(dy) : bar.WidthMm;
                        double sz = Math.Abs(dz) > 1.0 ? Math.Abs(dz) : bar.ThicknessMm;
                        AddSolidBoxToMs(db, ms, tr, lyrBus, bar.ColorIndex,
                            sx * scale, sy * scale, sz * scale,
                            (origin3DXMm + (p0[0] + p1[0]) * 0.5) * scale,
                            (origin3DYMm + (p0[1] + p1[1]) * 0.5) * scale,
                            ((p0[2] + p1[2]) * 0.5) * scale, createdIds);
                    }
                    for (int ji = 0; ji < bar.BoltJointsMm.Count; ji++)
                    {
                        double[] bj = bar.BoltJointsMm[ji];
                        AddSolidBoxToMs(db, ms, tr, lyrBus, 7, 10.0 * scale, 10.0 * scale, (bar.ThicknessMm + 6.0) * scale,
                            (origin3DXMm + bj[0]) * scale, (origin3DYMm + bj[1]) * scale, bj[2] * scale, createdIds);
                    }
                }

                // Depth Layer 06: 3D Routed Wires & Jumpers with AIENG_PANEL_NET XData
                for (int wi = 0; wi < plan.WireConnections.Count; wi++)
                {
                    PanelWireConnection wc = plan.WireConnections[wi];
                    if (wc.PointsMm.Count < 2) continue;
                    double wireDiaMm = Math.Max(3.2, Math.Min(16.0, Math.Sqrt(wc.SizeMm2) * 1.4));
                    Polyline3d p3d = new Polyline3d();
                    p3d.SetDatabaseDefaults(db);
                    p3d.Layer = lyrWire;
                    p3d.ColorIndex = wc.ColorIndex;
                    ms.AppendEntity(p3d);
                    tr.AddNewlyCreatedDBObject(p3d, true);
                    for (int pi = 0; pi < wc.PointsMm.Count; pi++)
                    {
                        double[] pt = wc.PointsMm[pi];
                        PolylineVertex3d vx = new PolylineVertex3d(new Point3d((origin3DXMm + pt[0]) * scale, (origin3DYMm + pt[1]) * scale, pt[2] * scale));
                        p3d.AppendVertex(vx);
                        tr.AddNewlyCreatedDBObject(vx, true);
                        if (pi > 0)
                        {
                            double[] prev = wc.PointsMm[pi - 1];
                            double dx = pt[0] - prev[0], dy = pt[1] - prev[1], dz = pt[2] - prev[2];
                            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > 0.5)
                            {
                                AddSolidBoxToMs(db, ms, tr, lyrWire, wc.ColorIndex,
                                    Math.Max(wireDiaMm, Math.Abs(dx)) * scale,
                                    Math.Max(wireDiaMm, Math.Abs(dy)) * scale,
                                    Math.Max(wireDiaMm, Math.Abs(dz)) * scale,
                                    (origin3DXMm + (prev[0] + pt[0]) * 0.5) * scale,
                                    (origin3DYMm + (prev[1] + pt[1]) * 0.5) * scale,
                                    ((prev[2] + pt[2]) * 0.5) * scale, createdIds);
                            }
                        }
                    }
                    string netStr = string.Format(
                        CultureInfo.InvariantCulture,
                        "v1|{0}|{1}:{2}|{3}:{4}|{5:0.#}|{6}|{7}|{8:0.#}|{9}",
                        wc.WireNo, wc.FromTag, wc.FromTerm, wc.ToTag, wc.ToTerm, wc.SizeMm2, wc.Phase, wc.ColorName, wc.LengthMm, wc.FerruleLabel);
                    if (netStr.Length > 240) netStr = netStr.Substring(0, 240);
                    p3d.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, PanelNetRegApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, netStr));
                    createdIds.Add(p3d.ObjectId);
                }

                // Depth Layer 07: Toggleable Door / Dead-Front Frame
                AddSolidBoxToMs(db, ms, tr, lyrDoor, 252, (encW - 10.0) * scale, (encH - 10.0) * scale, 2.5 * scale,
                    (origin3DXMm + encW * 0.5) * scale, (origin3DYMm + encH * 0.5) * scale, (encD - 2.0) * scale, createdIds);

                AddSldText(ms, tr, unicodeStyleId, lyrEnc, 4,
                    "3D Z-STACK ASSEMBLY: " + plan.PanelName + " (" + plan.Components.Count.ToString(CultureInfo.InvariantCulture) + " parts, " + plan.WireConnections.Count.ToString(CultureInfo.InvariantCulture) + " wires)",
                    origin3DXMm * scale, (origin3DYMm + encH + 35.0) * scale, 18.0 * scale, Math.Max(420.0, encW) * scale,
                    AttachmentPoint.BottomLeft, 0.0, createdIds);
            }
        }

        private static ExcelSheetData BuildPanelManufacturingBomSheet(PanelManufacturingPlan plan)
        {
            ExcelSheetData sheet = new ExcelSheetData();
            sheet.SheetName = "BOM_" + plan.PanelName;
            sheet.Title = "قائمة خامات تصنيع اللوحة (BOM + Thermal + Busbar): " + plan.PanelName +
                          " | " + plan.Enclosure.PartNumber + " (" + plan.Enclosure.WidthMm.ToString("0", CultureInfo.InvariantCulture) + "x" +
                          plan.Enclosure.HeightMm.ToString("0", CultureInfo.InvariantCulture) + "x" + plan.Enclosure.DepthMm.ToString("0", CultureInfo.InvariantCulture) + "mm)";
            sheet.Headers = new List<string>
            {
                "TAG1", "MFG", "CAT (Part Number)", "الوصف / السعة", "الأبعاد (mm)", "الفقد الحراري (W)", "مصدر الـ 3D"
            };

            sheet.Rows.Add(new List<string>
            {
                "CAB-1", plan.Enclosure.Series, plan.Enclosure.PartNumber,
                "Enclosure (" + plan.Enclosure.EnclosureType + ", Entry: " + plan.CableEntry + ", DoorClear: " + plan.MinDoorClearanceMm.ToString("0.#", CultureInfo.InvariantCulture) + "mm)",
                plan.Enclosure.WidthMm.ToString("0", CultureInfo.InvariantCulture) + "x" + plan.Enclosure.HeightMm.ToString("0", CultureInfo.InvariantCulture) + "x" + plan.Enclosure.DepthMm.ToString("0", CultureInfo.InvariantCulture),
                "-", "parametric_3d"
            });
            sheet.Rows.Add(new List<string>
            {
                "BUS-RYB", "IEC 61439 Cu", plan.MainBusbar.Designation,
                "Main 3P Busbar (" + plan.MainBusbar.MaxAmps35K.ToString("0", CultureInfo.InvariantCulture) + "A) + N: " + plan.NeutralBusbar.Designation + " + PE: " + plan.PeBusbar.Designation,
                plan.MainBusbar.WidthMm.ToString("0", CultureInfo.InvariantCulture) + "x" + plan.MainBusbar.ThicknessMm.ToString("0", CultureInfo.InvariantCulture),
                plan.TotalHeatLossBusbarW.ToString("0.#", CultureInfo.InvariantCulture), "parametric_3d"
            });
            if (plan.SelectedFan != null)
            {
                sheet.Rows.Add(new List<string>
                {
                    "FAN-1", "Schneider ClimaSys", plan.SelectedFan.PartNumber,
                    plan.SelectedFan.Series + " (Req: " + plan.RequiredAirflowM3h.ToString("0", CultureInfo.InvariantCulture) + " m3/h)",
                    plan.SelectedFan.WidthMm.ToString("0", CultureInfo.InvariantCulture) + "x" + plan.SelectedFan.HeightMm.ToString("0", CultureInfo.InvariantCulture) + "x" + plan.SelectedFan.DepthMm.ToString("0", CultureInfo.InvariantCulture),
                    "Cooling", "parametric_3d"
                });
            }
            for (int i = 0; i < plan.Components.Count; i++)
            {
                PlacedPanelComponent c = plan.Components[i];
                sheet.Rows.Add(new List<string>
                {
                    c.Tag, c.Part.Manufacturer, c.Part.PartNumber,
                    c.Part.Series + " (" + c.Part.RatedAmps.ToString("0.#", CultureInfo.InvariantCulture) + "A " + c.Part.Poles + "P)",
                    c.Part.WidthMm.ToString("0.#", CultureInfo.InvariantCulture) + "x" + c.Part.HeightMm.ToString("0.#", CultureInfo.InvariantCulture) + "x" + c.Part.DepthMm.ToString("0.#", CultureInfo.InvariantCulture),
                    c.Part.HeatLossWatts.ToString("0.#", CultureInfo.InvariantCulture), c.VaultSource
                });
            }

            sheet.FooterRow = new List<string>
            {
                "TOTAL",
                plan.Components.Count.ToString(CultureInfo.InvariantCulture) + " pcs",
                plan.WireConnections.Count.ToString(CultureInfo.InvariantCulture) + " wires",
                "Spare=" + plan.SpareSpacePercent.ToString("0.#", CultureInfo.InvariantCulture) + "% | Coll=" + plan.CollisionCount.ToString(CultureInfo.InvariantCulture),
                "T_top=" + plan.InternalTopTempC.ToString("0.1", CultureInfo.InvariantCulture) + "C",
                plan.TotalHeatLossW.ToString("0.#", CultureInfo.InvariantCulture) + " W",
                plan.NaturalCoolingOk ? "NATURAL OK" : "FAN REQ"
            };

            if (plan.WireConnections.Count > 0)
            {
                sheet.Rows.Add(new List<string> { "---", "---", "---", "---", "---", "---", "---" });
                sheet.Rows.Add(new List<string> { "WIRENO", "FROM:TERM", "TO:TERM", "Size (mm2) / Phase", "Cut Length (mm)", "Ferrule Label", "Status" });
                for (int i = 0; i < plan.WireConnections.Count; i++)
                {
                    PanelWireConnection w = plan.WireConnections[i];
                    sheet.Rows.Add(new List<string>
                    {
                        w.WireNo, w.FromTag + ":" + w.FromTerm, w.ToTag + ":" + w.ToTerm,
                        w.SizeMm2.ToString("0.#", CultureInfo.InvariantCulture) + "mm2 " + w.Phase + " (" + w.ColorName + ")",
                        w.LengthMm.ToString("0.#", CultureInfo.InvariantCulture), w.FerruleLabel, "3D-ROUTED"
                    });
                }
            }
            return sheet;
        }

        // ==================== Specialized Modes: DIN Snap, Resize Busbar, Trace Netlist, SLD Sync, View Presets ====================

        private static Dictionary<string, object> ExecuteSnapAndReroutePanel(
            Document doc, Database db, SwitchgearCatalogData catalog, Dictionary<string, object> args, double mmToDwg)
        {
            List<string> selHandles = GetSelectedHandleStrings(doc);
            List<PlacedPanelComponent> comps;
            double pOx, pOy, encW, encH;
            List<double> railCenterYs;
            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();
            int snappedCount = 0;
            double maxSnapDeltaMm = 0.0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!TryExtract2DPanelComponentsFromDrawing(db, tr, catalog, selHandles, mmToDwg, out comps, out pOx, out pOy, out encW, out encH, out railCenterYs))
                {
                    tr.Commit();
                    return Err("لم يتم العثور على مكونات لوحة 2D لمحاذاتها على قضبان الـ DIN Rail.");
                }

                // Infer rails if none stored explicitly
                if (railCenterYs.Count == 0)
                {
                    for (int i = 0; i < comps.Count; i++)
                    {
                        if (!comps[i].Part.IsDinRail || comps[i].Zone == "terminals") continue;
                        double cy = comps[i].YMm + comps[i].Part.HeightMm * 0.5;
                        bool exists = false;
                        for (int ri = 0; ri < railCenterYs.Count; ri++) if (Math.Abs(railCenterYs[ri] - cy) < 28.0) { exists = true; break; }
                        if (!exists) railCenterYs.Add(cy);
                    }
                }
                railCenterYs.Sort();

                // Snap each DIN-mountable component onto nearest DIN-rail centerline & align X horizontally
                for (int i = 0; i < comps.Count; i++)
                {
                    PlacedPanelComponent c = comps[i];
                    if (!c.Part.IsDinRail || railCenterYs.Count == 0 || c.Zone == "terminals") continue;
                    double curCy = c.YMm + c.Part.HeightMm * 0.5;
                    double bestRailY = curCy;
                    double bestDist = double.MaxValue;
                    for (int ri = 0; ri < railCenterYs.Count; ri++)
                    {
                        double d = Math.Abs(railCenterYs[ri] - curCy);
                        if (d < bestDist) { bestDist = d; bestRailY = railCenterYs[ri]; }
                    }
                    double snappedY = Math.Round(bestRailY - c.Part.HeightMm * 0.5, 2);
                    double snappedX = SnapToModuleGrid(c.XMm, 9.0);
                    double dy = Math.Abs(snappedY - c.YMm);
                    if (dy > 0.05 || Math.Abs(snappedX - c.XMm) > 0.05)
                    {
                        maxSnapDeltaMm = Math.Max(maxSnapDeltaMm, dy);
                        c.YMm = snappedY;
                        c.XMm = snappedX;
                        snappedCount++;
                    }
                }

                // Resolve any X overlaps introduced after snapping
                comps.Sort(delegate(PlacedPanelComponent a, PlacedPanelComponent b)
                {
                    int cy = a.YMm.CompareTo(b.YMm);
                    return cy != 0 ? cy : a.XMm.CompareTo(b.XMm);
                });
                for (int i = 0; i < comps.Count; i++)
                {
                    for (int j = i + 1; j < comps.Count; j++)
                    {
                        if (Math.Abs((comps[i].YMm + comps[i].Part.HeightMm * 0.5) - (comps[j].YMm + comps[j].Part.HeightMm * 0.5)) < 25.0)
                        {
                            double minNextX = comps[i].XMm + comps[i].Part.WidthMm + 4.0;
                            if (comps[j].XMm < minNextX) { comps[j].XMm = SnapToModuleGrid(minNextX, 9.0); snappedCount++; }
                        }
                    }
                }

                string panelName = GetStr(args, "panel_name") ?? "MDB-01";
                string manufacturer = GetStr(args, "manufacturer") ?? "Schneider Electric";
                string cableEntry = GetStr(args, "cable_entry") ?? "bottom";
                PanelManufacturingPlan plan = ComputePanelManufacturingPlan(
                    panelName, manufacturer, cableEntry,
                    GetDouble(args, "ambient_temp_c", 35.0),
                    GetDouble(args, "max_internal_temp_c", 55.0),
                    GetDouble(args, "spare_percent", 20.0),
                    GetDouble(args, "incomer_amps", 0.0),
                    GetDouble(args, "fault_ka", 25.0),
                    comps, ResolvePanel3DVaultDirectories(GetStr(args, "vault_dir")),
                    true, encW, encH, GetBool(args, "use_comb_busbar", true));
                ApplyLive2DBusbarOverrides(db, tr, catalog, plan, pOx, pOy, mmToDwg);

                BlockTableRecord msW = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                EraseAllPanelLayers(msW, tr, true, true, true, true, erasedIds);
                ObjectId uStyle = EnsureUnicodeTextStyle(db, tr);
                DrawPanel2DAnd3DAssemblies(db, msW, tr, plan, pOx, pOy, pOx + plan.Enclosure.WidthMm + 450.0, pOy,
                    mmToDwg, true, true, true, true, uStyle, createdIds);
                db.UpdateExt(true);
                tr.Commit();

                return Ok(new Dictionary<string, object>
                {
                    { "mode", "snap_and_reroute" },
                    { "panel_name", plan.PanelName },
                    { "snapped_components_count", snappedCount },
                    { "max_y_snap_delta_mm", Math.Round(maxSnapDeltaMm, 2) },
                    { "zero_collision_verified", plan.ZeroCollisionVerified },
                    { "layout_2d_drawn", true },
                    { "assembly_3d_drawn", true },
                    { "components_count", plan.Components.Count },
                    { "wires_count", plan.WireConnections.Count }
                });
            }
        }

        private static Dictionary<string, object> ExecuteResizeStandardBusbar(
            Document doc, Database db, SwitchgearCatalogData catalog, Dictionary<string, object> args, double mmToDwg)
        {
            string rawSize = GetStr(args, "target_busbar_size") ?? GetStr(args, "busbar_size");
            double? targetAmps = GetNullableDouble(args, "target_amps") ?? GetNullableDouble(args, "incomer_amps");
            CopperBusbarSpec stdBar = MatchNearestStandardBusbar(catalog, rawSize, targetAmps);

            int updatedCount = 0;
            List<string> selHandles = GetSelectedHandleStrings(doc);
            HashSet<string> selSet = selHandles.Count > 0 ? new HashSet<string>(selHandles, StringComparer.OrdinalIgnoreCase) : null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureRegApp(db, tr, PanelBusbarRegApp);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId oid in ms)
                {
                    if (oid.IsErased) continue;
                    Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    string xd = GetEntityXDataAscii(ent, PanelBusbarRegApp);
                    if (string.IsNullOrEmpty(xd)) continue;
                    if (selSet != null && !selSet.Contains(ent.Handle.ToString())) continue;
                    string[] p = xd.Split('|');
                    string label = p.Length > 1 ? p[1] : "R";
                    string kind = p.Length > 7 ? p[7] : "MAIN";
                    if (kind != "MAIN") continue;
                    if (!GetBool(args, "include_neutral_pe", false) && label != "R" && label != "Y" && label != "B") continue;

                    Polyline pl = tr.GetObject(oid, OpenMode.ForWrite) as Polyline;
                    if (pl != null)
                    {
                        pl.ConstantWidth = stdBar.WidthMm * mmToDwg;
                        pl.XData = new ResultBuffer(
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, PanelBusbarRegApp),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, string.Format(
                                CultureInfo.InvariantCulture, "v1|{0}|{1}|{2:0.##}|{3:0.##}|{4}|{5:0.#}|MAIN",
                                label, stdBar.Designation, stdBar.WidthMm, stdBar.ThicknessMm, stdBar.Runs, stdBar.MaxAmps35K)));
                        updatedCount++;
                    }
                }
                tr.Commit();
            }

            return Ok(new Dictionary<string, object>
            {
                { "mode", "resize_busbar" },
                { "requested_size", rawSize ?? "" },
                { "selected_standard_busbar", stdBar.Designation },
                { "standard_width_mm", stdBar.WidthMm },
                { "standard_thickness_mm", stdBar.ThicknessMm },
                { "max_amps_35k", stdBar.MaxAmps35K },
                { "updated_2d_busbars_count", updatedCount }
            });
        }

        private static Dictionary<string, object> ExecuteTracePanelConnections(
            Document doc, Database db, Dictionary<string, object> args)
        {
            string target = (GetStr(args, "trace_target") ?? "").Trim();
            List<string> selHandles = GetSelectedHandleStrings(doc);
            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();

            List<Dictionary<string, object>> allWires = new List<Dictionary<string, object>>();
            List<object> incoming = new List<object>();
            List<object> outgoing = new List<object>();
            List<string> chainPath = new List<string>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                Dictionary<string, List<Point3d>> wireGeomByNo = new Dictionary<string, List<Point3d>>(StringComparer.OrdinalIgnoreCase);

                foreach (ObjectId oid in ms)
                {
                    if (oid.IsErased) continue;
                    Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    if (string.IsNullOrEmpty(target) && selHandles.Contains(ent.Handle.ToString()))
                    {
                        string nx = GetEntityXDataAscii(ent, PanelNetRegApp);
                        if (!string.IsNullOrEmpty(nx)) { string[] np = nx.Split('|'); if (np.Length > 1) target = np[1]; }
                        string cx = GetEntityXDataAscii(ent, PanelCompRegApp);
                        if (!string.IsNullOrEmpty(cx)) { string[] cp = cx.Split('|'); if (cp.Length > 1) target = cp[1]; }
                    }

                    string netXd = GetEntityXDataAscii(ent, PanelNetRegApp);
                    if (string.IsNullOrEmpty(netXd)) continue;
                    string[] parts = netXd.Split('|');
                    if (parts.Length < 9) continue;

                    string wireNo = parts[1];
                    string fromFull = parts[2], toFull = parts[3];
                    string fromTag = fromFull.Contains(":") ? fromFull.Substring(0, fromFull.IndexOf(':')) : fromFull;
                    string toTag = toFull.Contains(":") ? toFull.Substring(0, toFull.IndexOf(':')) : toFull;
                    double sizeMm2 = 2.5, lenMm = 0.0;
                    double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out sizeMm2);
                    double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out lenMm);

                    allWires.Add(new Dictionary<string, object>
                    {
                        { "wire_no", wireNo },
                        { "from_device", fromTag },
                        { "from", fromFull },
                        { "to_device", toTag },
                        { "to", toFull },
                        { "size_mm2", sizeMm2 },
                        { "phase", parts[5] },
                        { "color", parts[6] },
                        { "length_mm", lenMm },
                        { "ferrule", parts[8] }
                    });

                    Polyline pl = ent as Polyline;
                    if (pl != null && !wireGeomByNo.ContainsKey(wireNo))
                    {
                        List<Point3d> pts = new List<Point3d>();
                        for (int vi = 0; vi < pl.NumberOfVertices; vi++)
                        {
                            Point2d p2 = pl.GetPoint2dAt(vi);
                            pts.Add(new Point3d(p2.X, p2.Y, 0.0));
                        }
                        wireGeomByNo[wireNo] = pts;
                    }
                }

                if (string.IsNullOrEmpty(target))
                {
                    tr.Commit();
                    return Err("حدد السلك أو القطعة في الرسم أو مرر `trace_target` (مثل 'W-01' أو 'QF1' أو 'KM1').");
                }

                BlockTableRecord msW = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                EraseLayerEntities(msW, tr, LayerPanelTraceHighlight, erasedIds);
                string lyrHl = EnsureLayerSafe(db, tr, LayerPanelTraceHighlight, 2, "Continuous");

                HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string curNode = target;
                for (int hop = 0; hop < 8; hop++)
                {
                    if (!visited.Add(curNode)) break;
                    chainPath.Add(curNode);
                    string nextNode = null;
                    for (int wi = 0; wi < allWires.Count; wi++)
                    {
                        Dictionary<string, object> wd = allWires[wi];
                        if (string.Equals(wd["from_device"].ToString(), curNode, StringComparison.OrdinalIgnoreCase))
                        {
                            nextNode = wd["to_device"].ToString();
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(nextNode)) break;
                    curNode = nextNode;
                }

                for (int wi = 0; wi < allWires.Count; wi++)
                {
                    Dictionary<string, object> wd = allWires[wi];
                    string wNo = wd["wire_no"].ToString();
                    string fDev = wd["from_device"].ToString();
                    string tDev = wd["to_device"].ToString();
                    bool isWireMatch = string.Equals(wNo, target, StringComparison.OrdinalIgnoreCase);
                    bool isIn = string.Equals(tDev, target, StringComparison.OrdinalIgnoreCase);
                    bool isOut = string.Equals(fDev, target, StringComparison.OrdinalIgnoreCase);
                    if (!isWireMatch && !isIn && !isOut) continue;

                    if (isWireMatch || isIn) incoming.Add(wd);
                    if (isWireMatch || isOut) outgoing.Add(wd);

                    List<Point3d> pts;
                    if (wireGeomByNo.TryGetValue(wNo, out pts) && pts.Count >= 2)
                    {
                        Polyline hl = new Polyline(pts.Count);
                        hl.SetDatabaseDefaults(db);
                        hl.Layer = lyrHl;
                        hl.ColorIndex = isIn ? (short)4 : (short)2;
                        for (int pi = 0; pi < pts.Count; pi++)
                            hl.AddVertexAt(pi, new Point2d(pts[pi].X, pts[pi].Y), 0, 2.5, 2.5);
                        msW.AppendEntity(hl);
                        tr.AddNewlyCreatedDBObject(hl, true);
                        createdIds.Add(hl.ObjectId);
                    }
                }
                tr.Commit();
            }

            return Ok(new Dictionary<string, object>
            {
                { "mode", "trace_connections" },
                { "target", target },
                { "incoming_count", incoming.Count },
                { "outgoing_count", outgoing.Count },
                { "incoming_connections", incoming },
                { "outgoing_connections", outgoing },
                { "downstream_chain", chainPath },
                { "highlights_drawn", createdIds.Count }
            });
        }

        private static Dictionary<string, object> ExecuteSldFromPanel(
            Document doc, Database db, SwitchgearCatalogData catalog, Dictionary<string, object> args, double mmToDwg)
        {
            List<PlacedPanelComponent> comps;
            double pOx, pOy, encW, encH;
            List<double> railYs;
            PanelManufacturingPlan plan;
            List<ObjectId> createdIds = new List<ObjectId>();
            List<ObjectId> erasedIds = new List<ObjectId>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (TryExtract2DPanelComponentsFromDrawing(db, tr, catalog, null, mmToDwg, out comps, out pOx, out pOy, out encW, out encH, out railYs))
                {
                    plan = ComputePanelManufacturingPlan(
                        GetStr(args, "panel_name") ?? "MDB-01",
                        GetStr(args, "manufacturer") ?? "Schneider Electric",
                        GetStr(args, "cable_entry") ?? "bottom",
                        35.0, 55.0, 20.0, 0.0, 25.0, comps, null, true, encW, encH, true);
                }
                else
                {
                    List<PlacedPanelComponent> raw = ParseRawPanelComponentsArg(catalog, args.ContainsKey("components") ? args["components"] : null, "Schneider Electric", 25.0);
                    if (raw.Count == 0) { tr.Commit(); return Err("لم يتم العثور على مكونات لوحة 2D أو components[] لتوليد المخطط الأحادي SLD."); }
                    plan = ComputePanelManufacturingPlan(
                        GetStr(args, "panel_name") ?? "MDB-01", "Schneider Electric", "bottom",
                        35.0, 55.0, 20.0, 0.0, 25.0, raw, null, false, null, null, true);
                    pOx = 0.0; pOy = 0.0;
                }

                BlockTableRecord msW = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                EnsureRegApp(db, tr, PanelSldRegApp);
                string sldLayer = EnsureLayerSafe(db, tr, "_AI_SLD", 7, "Continuous");
                EraseLayerEntities(msW, tr, sldLayer, erasedIds);
                ObjectId uStyle = EnsureUnicodeTextStyle(db, tr);

                List<PlacedPanelComponent> feeders = new List<PlacedPanelComponent>();
                PlacedPanelComponent incomer = null;
                Dictionary<string, PlacedPanelComponent> byTag = new Dictionary<string, PlacedPanelComponent>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < plan.Components.Count; i++)
                {
                    byTag[plan.Components[i].Tag] = plan.Components[i];
                    if (plan.Components[i].Zone == "incomer") incomer = plan.Components[i];
                    else if (plan.Components[i].Zone != "terminals" && !plan.Components[i].IsFedFromUpstream) feeders.Add(plan.Components[i]);
                }

                double sldX = (pOx + plan.Enclosure.WidthMm * 2.0 + 950.0) * mmToDwg;
                double sldTopY = (pOy + plan.Enclosure.HeightMm) * mmToDwg;
                double stepX = 160.0 * mmToDwg;
                double sldW = Math.Max(420.0 * mmToDwg, (feeders.Count + 1) * stepX);
                double busY = sldTopY - 220.0 * mmToDwg;
                double busMidX = sldX + sldW * 0.5;

                // Main SLD Header + Incomer + Busbar
                AddSldText(msW, tr, uStyle, sldLayer, 2,
                    "SINGLE LINE DIAGRAM FROM PANEL - " + plan.PanelName + " (" + plan.MainBusbar.Designation + ")",
                    busMidX, sldTopY - 25.0 * mmToDwg, 16.0 * mmToDwg, sldW, AttachmentPoint.MiddleCenter, 0.0, createdIds);
                if (incomer != null)
                {
                    AddSldBreakerSymbol(msW, tr, sldLayer, 1, busMidX, sldTopY - 70.0 * mmToDwg, sldTopY - 150.0 * mmToDwg, 18.0 * mmToDwg, createdIds);
                    AddSldLine(msW, tr, sldLayer, 1, busMidX, sldTopY - 150.0 * mmToDwg, busMidX, busY, 2.0 * mmToDwg, createdIds);
                    AddSldText(msW, tr, uStyle, sldLayer, 7,
                        incomer.Tag + " " + incomer.Part.RatedAmps.ToString("0", CultureInfo.InvariantCulture) + "A [" + incomer.Part.PartNumber + "]",
                        busMidX + 24.0 * mmToDwg, sldTopY - 110.0 * mmToDwg, 11.0 * mmToDwg, 220.0 * mmToDwg, AttachmentPoint.MiddleLeft, 0.0, createdIds);
                    StampSldEntityXData(msW, tr, sldLayer, plan.PanelName, 0, incomer, createdIds, mmToDwg, busMidX, sldTopY - 110.0 * mmToDwg, uStyle);
                }

                AddSldLine(msW, tr, sldLayer, 4, sldX + 30.0 * mmToDwg, busY, sldX + sldW - 30.0 * mmToDwg, busY, 4.0 * mmToDwg, createdIds);

                for (int fi = 0; fi < feeders.Count; fi++)
                {
                    double fx = sldX + 80.0 * mmToDwg + fi * stepX;
                    double curY = busY;
                    PlacedPanelComponent node = feeders[fi];
                    HashSet<string> loopGuard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (node != null && loopGuard.Add(node.Tag))
                    {
                        AddSldLine(msW, tr, sldLayer, 7, fx, curY, fx, curY - 35.0 * mmToDwg, 0.0, createdIds);
                        AddSldBreakerSymbol(msW, tr, sldLayer, GetPhaseColorIndex(node.Phase), fx, curY - 35.0 * mmToDwg, curY - 95.0 * mmToDwg, 14.0 * mmToDwg, createdIds);
                        AddSldText(msW, tr, uStyle, sldLayer, 7,
                            node.Tag + " " + node.Part.RatedAmps.ToString("0.#", CultureInfo.InvariantCulture) + "A [" + node.Part.PartNumber + "]",
                            fx + 18.0 * mmToDwg, curY - 65.0 * mmToDwg, 9.0 * mmToDwg, 130.0 * mmToDwg, AttachmentPoint.MiddleLeft, 0.0, createdIds);
                        StampSldEntityXData(msW, tr, sldLayer, plan.PanelName, fi + 1, node, createdIds, mmToDwg, fx, curY - 65.0 * mmToDwg, uStyle);
                        curY -= 115.0 * mmToDwg;
                        PlacedPanelComponent nextNode = null;
                        if (!string.IsNullOrEmpty(node.ConnectedToTag)) byTag.TryGetValue(node.ConnectedToTag, out nextNode);
                        node = (nextNode != null && nextNode.Zone != "terminals") ? nextNode : null;
                    }
                }
                tr.Commit();
            }

            return Ok(new Dictionary<string, object>
            {
                { "mode", "sld_from_panel" },
                { "panel_name", plan.PanelName },
                { "sld_drawn", true },
                { "main_busbar", plan.MainBusbar.Designation },
                { "components_synced", plan.Components.Count },
                { "created_count", createdIds.Count }
            });
        }

        private static void StampSldEntityXData(
            BlockTableRecord msW, Transaction tr, string layer, string panelName, int chainIdx,
            PlacedPanelComponent comp, List<ObjectId> createdIds, double mmToDwg, double x, double y, ObjectId uStyle)
        {
            DBText tagMarker = new DBText();
            tagMarker.SetDatabaseDefaults(msW.Database);
            tagMarker.Layer = layer;
            tagMarker.ColorIndex = 8;
            tagMarker.TextStyleId = uStyle;
            tagMarker.Height = Math.Max(0.01, 4.0 * mmToDwg);
            tagMarker.Position = new Point3d(x, y, 0.0);
            tagMarker.TextString = comp.Tag;
            msW.AppendEntity(tagMarker);
            tr.AddNewlyCreatedDBObject(tagMarker, true);
            string sldVal = string.Format(
                CultureInfo.InvariantCulture,
                "v1|{0}|{1}|{2}|{3}|{4}|{5:0.#}|{6}|{7:0.#}|{8}",
                panelName, chainIdx, comp.Tag, comp.Part.PartNumber, comp.Part.Category, comp.Part.RatedAmps, comp.Part.Poles, comp.CableMm2, comp.ConnectedToTag ?? "");
            tagMarker.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, PanelSldRegApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sldVal));
            createdIds.Add(tagMarker.ObjectId);
        }

        private static Dictionary<string, object> ExecuteSetViewPreset(Database db, Dictionary<string, object> args)
        {
            string rawPreset = (GetStr(args, "view_preset") ?? "full_assembly").Trim().ToLowerInvariant();
            string preset = rawPreset;
            if (rawPreset == "all" || rawPreset == "full") preset = "full_assembly";
            else if (rawPreset == "hide_duct_covers") preset = "wiring_uncovered";
            else if (rawPreset == "hide_door") preset = "no_door";
            else if (rawPreset == "cnc_drilling" || rawPreset == "cnc") preset = "cnc_drilling_only";

            List<string> layersOn = new List<string>();
            List<string> layersOff = new List<string>();

            HashSet<string> offTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (preset == "busbars_only")
            {
                offTargets.Add(LayerPanel3D03DuctsBody);
                offTargets.Add(LayerPanel3D03DuctsCover);
                offTargets.Add(LayerPanel3D06Wiring);
                offTargets.Add(LayerPanel3D07DoorDeadFront);
                offTargets.Add(LayerPanelCncDrilling);
            }
            else if (preset == "wiring_uncovered")
            {
                offTargets.Add(LayerPanel3D03DuctsCover);
                offTargets.Add(LayerPanel3D07DoorDeadFront);
            }
            else if (preset == "cnc_drilling_only")
            {
                offTargets.Add(LayerPanel3D00Enclosure);
                offTargets.Add(LayerPanel3D02RailsInsulators);
                offTargets.Add(LayerPanel3D03DuctsBody);
                offTargets.Add(LayerPanel3D03DuctsCover);
                offTargets.Add(LayerPanel3D04Components);
                offTargets.Add(LayerPanel3D05BusbarsCopper);
                offTargets.Add(LayerPanel3D06Wiring);
                offTargets.Add(LayerPanel3D07DoorDeadFront);
                offTargets.Add(LayerPanelGuideAlign);
            }
            else if (preset == "no_door")
            {
                offTargets.Add(LayerPanel3D07DoorDeadFront);
            }
            else if (preset == "components_only")
            {
                offTargets.Add(LayerPanel3D03DuctsBody);
                offTargets.Add(LayerPanel3D03DuctsCover);
                offTargets.Add(LayerPanel3D05BusbarsCopper);
                offTargets.Add(LayerPanel3D06Wiring);
                offTargets.Add(LayerPanel3D07DoorDeadFront);
                offTargets.Add(LayerPanelCncDrilling);
            }
            else if (preset == "2d_only")
            {
                offTargets.Add(LayerPanel3D00Enclosure);
                offTargets.Add(LayerPanel3D01MountingPlate);
                offTargets.Add(LayerPanel3D02RailsInsulators);
                offTargets.Add(LayerPanel3D03DuctsBody);
                offTargets.Add(LayerPanel3D03DuctsCover);
                offTargets.Add(LayerPanel3D04Components);
                offTargets.Add(LayerPanel3D05BusbarsCopper);
                offTargets.Add(LayerPanel3D06Wiring);
                offTargets.Add(LayerPanel3D07DoorDeadFront);
            }
            else if (preset == "3d_only")
            {
                offTargets.Add(LayerPanel2D);
                offTargets.Add(LayerPanelSideSection);
                offTargets.Add(LayerPanelCncDrilling);
                offTargets.Add(LayerPanelGuideAlign);
            }

            string[] managedLayers = new string[]
            {
                LayerPanel2D, LayerPanelSideSection, LayerPanelCncDrilling, LayerPanelGuideAlign,
                LayerPanel3D00Enclosure, LayerPanel3D01MountingPlate, LayerPanel3D02RailsInsulators,
                LayerPanel3D03DuctsBody, LayerPanel3D03DuctsCover, LayerPanel3D04Components,
                LayerPanel3D05BusbarsCopper, LayerPanel3D06Wiring, LayerPanel3D07DoorDeadFront
            };

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                for (int i = 0; i < managedLayers.Length; i++)
                {
                    string ln = managedLayers[i];
                    if (!lt.Has(ln)) continue;
                    LayerTableRecord ltr = tr.GetObject(lt[ln], OpenMode.ForWrite) as LayerTableRecord;
                    if (ltr == null) continue;
                    bool turnOff = offTargets.Contains(ln);
                    ltr.IsOff = turnOff;
                    if (turnOff) layersOff.Add(ln); else layersOn.Add(ln);
                }
                tr.Commit();
            }

            return Ok(new Dictionary<string, object>
            {
                { "mode", "set_view_preset" },
                { "view_preset", preset },
                { "layers_on", layersOn },
                { "layers_off", layersOff }
            });
        }

        // ==================== Main Public Entry Point (`design_panel_manufacturing_layout`) ====================

        private static Dictionary<string, object> DesignPanelManufacturingLayout(Document doc, Dictionary<string, object> args)
        {
            if (doc == null || doc.Database == null)
                return Err("لا يوجد ملف رسم نشط في أوتوكاد.");
            if (args == null) args = new Dictionary<string, object>();

            Database db = doc.Database;
            SwitchgearCatalogData catalog = ResolveSwitchgearCatalog();
            string mode = (GetStr(args, "mode") ?? "auto_2d_and_3d").Trim().ToLowerInvariant();
            double? explicitUnitScale = GetNullableDouble(args, "units_scale");

            using (DocumentLock docLock = doc.LockDocument())
            {
                double mmToDwg = 1.0;
                Extents3d? mainExt = null;
                using (Transaction trUnit = db.TransactionManager.StartTransaction())
                {
                    Dictionary<string, bool> hiddenMap = BuildLayerHiddenMap(db, trUnit);
                    double dwgSpan = 0.0, medSpan = 0.0;
                    ComputeDrawingFixtureMetrics(db, trUnit, true, hiddenMap, out dwgSpan, out medSpan, out mainExt);
                    Dictionary<string, object> uInfo = GetUnitsInfo(db);
                    double rawU = Convert.ToDouble(uInfo["units_per_m"], CultureInfo.InvariantCulture);
                    bool scaleInf; string scaleWarn;
                    double effUnitsPerM = ResolveEffectiveUnitsPerMeter(db, rawU, dwgSpan, medSpan, out scaleInf, out scaleWarn);
                    if (explicitUnitScale.HasValue && explicitUnitScale.Value > 0.0) mmToDwg = explicitUnitScale.Value;
                    else if (db.Insunits == UnitsValue.Meters || (scaleInf && effUnitsPerM == 1.0)) mmToDwg = 0.001;
                    else if (db.Insunits == UnitsValue.Centimeters) mmToDwg = 0.1;
                    else mmToDwg = 1.0;
                    trUnit.Commit();
                }

                // Interactive / Specialized Dispatcher Modes
                if (mode == "snap_and_reroute") return ExecuteSnapAndReroutePanel(doc, db, catalog, args, mmToDwg);
                if (mode == "resize_busbar") return ExecuteResizeStandardBusbar(doc, db, catalog, args, mmToDwg);
                if (mode == "trace_connections") return ExecuteTracePanelConnections(doc, db, args);
                if (mode == "sld_from_panel") return ExecuteSldFromPanel(doc, db, catalog, args, mmToDwg);
                if (mode == "set_view_preset") return ExecuteSetViewPreset(db, args);

                string panelName = GetStr(args, "panel_name") ?? "MDB-01";
                string manufacturer = GetStr(args, "manufacturer") ?? "Schneider Electric";
                string cableEntry = (GetStr(args, "cable_entry") ?? "bottom").Trim().ToLowerInvariant();
                string sourceMode = (GetStr(args, "source") ?? "auto").Trim().ToLowerInvariant();
                double ambientC = Math.Max(15.0, Math.Min(60.0, GetDouble(args, "ambient_temp_c", 35.0)));
                double maxTempC = Math.Max(40.0, Math.Min(80.0, GetDouble(args, "max_internal_temp_c", 55.0)));
                double sparePct = Math.Max(10.0, Math.Min(50.0, GetDouble(args, "spare_percent", 20.0)));
                double faultKa = Math.Max(6.0, GetDouble(args, "fault_ka", 25.0));
                double incomerAmpsHint = Math.Max(0.0, GetDouble(args, "incomer_amps", 0.0));
                string customVaultDir = GetStr(args, "vault_dir");

                bool draw2D = mode != "3d_from_2d";
                bool draw3D = mode != "2d_only";
                bool drawSideSection = GetBool(args, "draw_side_section", true);
                bool drawCncDrilling = GetBool(args, "draw_cnc_drilling", true);
                bool useCombBusbar = GetBool(args, "use_comb_busbar", true);
                bool drawBomTable = GetBool(args, "draw_bom_table", true);
                bool exportExcel = GetBool(args, "export_excel", false);
                bool replaceExisting = GetBool(args, "replace_existing", true);
                bool zoom = GetBool(args, "zoom", true);

                double fixtureVa = Math.Max(1.0, GetDouble(args, "fixture_va", 40.0));
                double voltageLnV = Math.Max(100.0, GetDouble(args, "voltage_ln_v", 220.0));
                double voltageLlV = Math.Max(180.0, GetDouble(args, "voltage_ll_v", 380.0));
                double pf = Math.Max(0.5, Math.Min(1.0, GetDouble(args, "power_factor", 0.90)));
                double df = Math.Max(0.2, Math.Min(1.5, GetDouble(args, "demand_factor", 0.85)));
                double feederLen = Math.Max(5.0, GetDouble(args, "feeder_length_m", 25.0));
                double maxVd = Math.Max(1.0, Math.Min(10.0, GetDouble(args, "max_vd_percent", 3.0)));
                string mat = GetStr(args, "material") ?? "Cu";
                string ins = GetStr(args, "insulation") ?? "XLPE";
                string meth = GetStr(args, "install_method") ?? "tray";

                List<string> vaultDirs = ResolvePanel3DVaultDirectories(customVaultDir);
                List<string> selHandles = GetSelectedHandleStrings(doc);
                List<ObjectId> createdIds = new List<ObjectId>();
                List<ObjectId> erasedIds = new List<ObjectId>();

                PanelManufacturingPlan plan = null;
                bool preserveCoords = false;
                double scannedOriginX = 0.0, scannedOriginY = 0.0;
                double? scannedEncW = null, scannedEncH = null;

                using (Transaction trRead = db.TransactionManager.StartTransaction())
                {
                    List<PlacedPanelComponent> rawComps = new List<PlacedPanelComponent>();

                    if (mode == "panel_from_sld")
                    {
                        string sldPanelName;
                        rawComps = ParseSldFromDrawing(db, trRead, catalog, manufacturer, faultKa, out sldPanelName);
                        if (rawComps.Count == 0)
                        {
                            trRead.Commit();
                            return Err("لم يتم العثور على مخطط أحادي SLD صالح على طبقة _AI_SLD لتحويله إلى لوحة 2D/3D.");
                        }
                        if (string.IsNullOrWhiteSpace(GetStr(args, "panel_name"))) panelName = sldPanelName;
                    }
                    else if (mode == "3d_from_2d" || sourceMode == "from_2d_layout")
                    {
                        List<PlacedPanelComponent> scanned2D;
                        List<double> railYs;
                        double pOx, pOy, ew, eh;
                        if (!TryExtract2DPanelComponentsFromDrawing(db, trRead, catalog, selHandles, mmToDwg, out scanned2D, out pOx, out pOy, out ew, out eh, out railYs))
                        {
                            trRead.Commit();
                            return Err("لم يتم العثور على مكونات لوحة 2D صالحة تحتوي على سمات ACADE (TAG1/CAT) أو طبقة _AI_PANEL_2D_LAYOUT للتحويل إلى 3D.");
                        }
                        rawComps = scanned2D;
                        preserveCoords = true;
                        scannedOriginX = pOx;
                        scannedOriginY = pOy;
                        scannedEncW = ew;
                        scannedEncH = eh;
                    }
                    else
                    {
                        if (args.ContainsKey("components") && args["components"] != null)
                            rawComps = ParseRawPanelComponentsArg(catalog, args["components"], manufacturer, faultKa);

                        if (rawComps.Count == 0)
                        {
                            List<TracedCircuitSummary> circSummaries = args.ContainsKey("circuits") && args["circuits"] != null
                                ? ParseExplicitPanelCircuitsArg(args["circuits"], fixtureVa)
                                : new List<TracedCircuitSummary>();

                            if (circSummaries.Count == 0 && (sourceMode == "saved_memory" || sourceMode == "memory" || sourceMode == "saved"))
                            {
                                List<SavedCircuitRecord> savedList = GetAllSavedCircuitRecords(doc);
                                for (int si = 0; si < savedList.Count; si++)
                                {
                                    SavedCircuitRecord sr = savedList[si];
                                    circSummaries.Add(new TracedCircuitSummary
                                    {
                                        CircuitName = sr.CircuitName,
                                        PanelName = ExtractPanelNameFallback(sr.CircuitName, sr.PanelName),
                                        TerminationType = sr.TerminationType,
                                        WireLayer = sr.WireLayer,
                                        WireColorKey = sr.WireColorKey,
                                        PathCurvesCount = sr.PathHandles != null ? sr.PathHandles.Count : 0,
                                        LengthMeters = sr.LengthMeters,
                                        MatchedBlocksCount = sr.MatchedBlockHandles != null ? sr.MatchedBlockHandles.Count : 0,
                                        CountsByBlock = new Dictionary<string, int>(sr.CountsByBlock ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase)
                                    });
                                }
                            }

                            if (circSummaries.Count == 0 && sourceMode != "circuits_only")
                            {
                                DrawingCircuitScanResult scan = ScanAllCircuitsInternal(doc, db, trRead, args, false);
                                if (scan != null && scan.Circuits.Count > 0) circSummaries = scan.Circuits;
                            }

                            if (circSummaries.Count > 0)
                            {
                                BalancedPanelSchedule sched = BuildBalancedPanelSchedule(
                                    doc, panelName, circSummaries, fixtureVa, voltageLnV, voltageLlV,
                                    pf, df, sparePct, feederLen, maxVd, mat, ins, meth, ambientC);
                                rawComps = ConvertPanelScheduleWaysToComponents(catalog, sched, manufacturer, faultKa);
                                if (incomerAmpsHint <= 0.0 && sched.MainFeeder != null)
                                    incomerAmpsHint = sched.MainFeeder.BreakerRatingA;
                            }
                        }

                        if (rawComps.Count == 0)
                        {
                            trRead.Commit();
                            return Err("يرجى تمرير مصفوفة components (أو أرقام القطع part_number / الدوائر circuits) أو رسم مكونات اللوحة لتوليد التصميم التصنيعي 2D/3D.");
                        }
                    }

                    plan = ComputePanelManufacturingPlan(
                        panelName, manufacturer, cableEntry, ambientC, maxTempC,
                        sparePct, incomerAmpsHint, faultKa, rawComps, vaultDirs,
                        preserveCoords, scannedEncW, scannedEncH, useCombBusbar);
                    if (preserveCoords)
                        ApplyLive2DBusbarOverrides(db, trRead, catalog, plan, scannedOriginX, scannedOriginY, mmToDwg);

                    trRead.Commit();
                }

                double anchorXMm = 0.0, anchorYMm = 0.0;
                if (preserveCoords)
                {
                    anchorXMm = scannedOriginX;
                    anchorYMm = scannedOriginY;
                }
                else if (mainExt.HasValue && IsFiniteBounds(mainExt.Value))
                {
                    anchorXMm = (mainExt.Value.MaxPoint.X / mmToDwg) + 600.0;
                    anchorYMm = mainExt.Value.MinPoint.Y / mmToDwg;
                }
                double origin3DXMm = anchorXMm + (draw2D || preserveCoords ? (plan.Enclosure.WidthMm + 450.0) : 0.0);
                double origin3DYMm = anchorYMm;

                if (!db.TileMode) { try { db.TileMode = true; } catch { } }
                Extents3d? bomExt = null;
                ExcelSheetData bomSheet = BuildPanelManufacturingBomSheet(plan);

                using (Transaction trDraw = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord msW = (BlockTableRecord)trDraw.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    ObjectId uStyle = EnsureUnicodeTextStyle(db, trDraw);

                    if (replaceExisting)
                        EraseAllPanelLayers(msW, trDraw, draw2D, draw3D, drawSideSection, drawCncDrilling, erasedIds);

                    DrawPanel2DAnd3DAssemblies(
                        db, msW, trDraw, plan, anchorXMm, anchorYMm, origin3DXMm, origin3DYMm,
                        mmToDwg, draw2D, draw3D, drawSideSection, drawCncDrilling, uStyle, createdIds);

                    if (drawBomTable)
                    {
                        Extents3d panelBounds = new Extents3d(
                            new Point3d(anchorXMm * mmToDwg, anchorYMm * mmToDwg, 0.0),
                            new Point3d(((draw3D ? origin3DXMm : anchorXMm) + plan.Enclosure.WidthMm) * mmToDwg, (anchorYMm + plan.Enclosure.HeightMm) * mmToDwg, 0.0));
                        bomExt = RenderStyledScheduleTable(
                            doc, db, msW, trDraw, bomSheet, LayerPanelBomTable,
                            replaceExisting, "outside_bottom", null, null, panelBounds, createdIds, erasedIds);
                    }

                    db.UpdateExt(true);
                    trDraw.Commit();
                }

                if (createdIds.Count > 0 || erasedIds.Count > 0)
                {
                    CadUndoEntry undo = new CadUndoEntry();
                    undo.ToolName = "design_panel_manufacturing_layout";
                    undo.Summary = "Panel 2D/3D Assembly " + plan.PanelName + " (" + plan.Components.Count.ToString(CultureInfo.InvariantCulture) + " components)";
                    undo.CreatedIds.AddRange(createdIds);
                    undo.ErasedIds.AddRange(erasedIds);
                    PushUndoEntry(doc, undo);
                }

                Extents3d zoomBox = new Extents3d(
                    new Point3d((anchorXMm - (drawSideSection ? plan.Enclosure.DepthMm + 160.0 : 0.0)) * mmToDwg, anchorYMm * mmToDwg, 0.0),
                    new Point3d(((draw3D ? origin3DXMm : anchorXMm) + plan.Enclosure.WidthMm) * mmToDwg, (anchorYMm + plan.Enclosure.HeightMm + 120.0) * mmToDwg, 0.0));
                if (bomExt.HasValue && IsFiniteBounds(bomExt.Value)) zoomBox.AddExtents(bomExt.Value);
                if (zoom) ZoomToExtents3d(doc, zoomBox, 1.15);
                try { doc.Editor.UpdateScreen(); } catch { }

                List<object> compOut = new List<object>();
                for (int i = 0; i < plan.Components.Count; i++)
                {
                    PlacedPanelComponent c = plan.Components[i];
                    compOut.Add(new Dictionary<string, object>
                    {
                        { "tag", c.Tag },
                        { "part_number", c.Part.PartNumber },
                        { "manufacturer", c.Part.Manufacturer },
                        { "category", c.Part.Category },
                        { "poles", c.Part.Poles },
                        { "rated_amps", c.Part.RatedAmps },
                        { "width_mm", c.Part.WidthMm },
                        { "height_mm", c.Part.HeightMm },
                        { "depth_mm", c.Part.DepthMm },
                        { "heat_loss_w", c.Part.HeatLossWatts },
                        { "zone", c.Zone },
                        { "x_mm", Math.Round(c.XMm, 1) },
                        { "y_mm", Math.Round(c.YMm, 1) },
                        { "z_mm", Math.Round(c.ZMm, 1) },
                        { "wire_no", c.WireNo },
                        { "connected_to", !string.IsNullOrEmpty(c.ConnectedToTag) ? c.ConnectedToTag : c.TargetTerminalTag },
                        { "vault_source", c.VaultSource }
                    });
                }

                List<object> wireOut = new List<object>();
                for (int i = 0; i < plan.WireConnections.Count; i++)
                {
                    PanelWireConnection w = plan.WireConnections[i];
                    wireOut.Add(new Dictionary<string, object>
                    {
                        { "wire_no", w.WireNo },
                        { "from", w.FromTag + ":" + w.FromTerm },
                        { "to", w.ToTag + ":" + w.ToTerm },
                        { "size_mm2", w.SizeMm2 },
                        { "phase", w.Phase },
                        { "color", w.ColorName },
                        { "length_mm", w.LengthMm },
                        { "ferrule", w.FerruleLabel }
                    });
                }

                Dictionary<string, object> resp = new Dictionary<string, object>
                {
                    { "panel_name", plan.PanelName },
                    { "mode", mode },
                    { "cable_entry", plan.CableEntry },
                    { "manufacturer", plan.Manufacturer },
                    { "zero_collision_verified", plan.ZeroCollisionVerified },
                    { "collision_count", plan.CollisionCount },
                    { "min_door_clearance_mm", plan.MinDoorClearanceMm },
                    { "max_duct_fill_percent", plan.MaxDuctFillPercent },
                    { "cnc_holes_count", plan.CncHoles.Count },
                    { "comb_busbar_used", plan.UseCombBusbar },
                    { "enclosure", new Dictionary<string, object>
                        {
                            { "part_number", plan.Enclosure.PartNumber },
                            { "series", plan.Enclosure.Series },
                            { "type", plan.Enclosure.EnclosureType },
                            { "width_mm", plan.Enclosure.WidthMm },
                            { "height_mm", plan.Enclosure.HeightMm },
                            { "depth_mm", plan.Enclosure.DepthMm }
                        }
                    },
                    { "busbar", new Dictionary<string, object>
                        {
                            { "main_designation", plan.MainBusbar.Designation },
                            { "max_amps_35k", plan.MainBusbar.MaxAmps35K },
                            { "neutral_designation", plan.NeutralBusbar.Designation },
                            { "pe_designation", plan.PeBusbar.Designation },
                            { "insulator_supports", plan.BusbarInsulatorCount }
                        }
                    },
                    { "thermal_iec61439", new Dictionary<string, object>
                        {
                            { "ambient_temp_c", plan.AmbientTempC },
                            { "max_internal_temp_c", plan.MaxInternalTempC },
                            { "device_heat_loss_w", plan.TotalHeatLossDevicesW },
                            { "busbar_heat_loss_w", plan.TotalHeatLossBusbarW },
                            { "total_heat_loss_w", plan.TotalHeatLossW },
                            { "effective_surface_m2", plan.EffectiveSurfaceM2 },
                            { "temp_rise_c", plan.TempRiseC },
                            { "internal_top_temp_c", plan.InternalTopTempC },
                            { "natural_cooling_ok", plan.NaturalCoolingOk },
                            { "required_airflow_m3h", plan.RequiredAirflowM3h },
                            { "recommended_fan", plan.SelectedFan != null ? plan.SelectedFan.PartNumber : "" }
                        }
                    },
                    { "spare_space_percent", plan.SpareSpacePercent },
                    { "components_count", plan.Components.Count },
                    { "wires_count", plan.WireConnections.Count },
                    { "droppers_count", plan.Busbars.FindAll(delegate(PanelBusbarRun b) { return b.IsDropper; }).Count },
                    { "layout_2d_drawn", draw2D },
                    { "side_section_drawn", drawSideSection },
                    { "cnc_drilling_drawn", drawCncDrilling },
                    { "assembly_3d_drawn", draw3D },
                    { "bom_table_drawn", drawBomTable && bomExt.HasValue },
                    { "created_count", createdIds.Count },
                    { "components", compOut },
                    { "wires_from_to", wireOut },
                    { "warnings", plan.Warnings }
                };

                if (exportExcel)
                {
                    string xlPath = GetStr(args, "excel_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "panel_manufacturing_" + plan.PanelName, ".xlsx");
                    resp["excel_exported"] = true;
                    resp["excel_path"] = ExcelTableInterop.WriteSpreadsheet(xlPath, bomSheet);
                }

                return Ok(resp);
            }
        }
    }

    // ==================== Dedicated 2D/3D Panel Manufacturing Palette & CLI Commands ====================

    public sealed class PanelManufacturingCommands
    {
#if PANEL_MFG_STANDALONE
        internal const string PanelCmdGroup = "AIENG_PANELMFG_DLL";
#else
        internal const string PanelCmdGroup = "AIENG_PANELMFG_CTX";
#endif
        private static PaletteSet _panelPalette;

        internal static PaletteSet CurrentPalette { get { return _panelPalette; } }

        [CommandMethod(PanelCmdGroup, "AIENGPANEL", CommandFlags.Session)]
        [CommandMethod(PanelCmdGroup, "AIENGPANELMFG", CommandFlags.Session)]
        [CommandMethod(PanelCmdGroup, "PANELAI", CommandFlags.Session)]
        public void ShowPanelManufacturingPalette()
        {
            if (_panelPalette == null)
            {
                _panelPalette = new PaletteSet(
                    "AI-Engineer — Panel Manufacturing 2D/3D",
                    "AIENGPANEL",
                    new Guid("7b4e91d2-3c8a-4f19-a6d5-8e12c49f0b33"));
                _panelPalette.Style = PaletteSetStyles.ShowAutoHideButton |
                    PaletteSetStyles.ShowCloseButton |
                    PaletteSetStyles.ShowPropertiesMenu;
                _panelPalette.DockEnabled = DockSides.Left | DockSides.Right;
                _panelPalette.MinimumSize = new Size(360, 520);
                _panelPalette.Add("Panel Manufacturing 2D/3D", new PanelManufacturingPaletteControl());
            }
            _panelPalette.Visible = true;
        }

        private static void RunPanelMode(string mode, Dictionary<string, object> extra)
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Dictionary<string, object> args = extra != null ? new Dictionary<string, object>(extra) : new Dictionary<string, object>();
            args["mode"] = mode;
            Dictionary<string, object> res = CadToolExecutor.ExecuteTool(doc, "design_panel_manufacturing_layout", args);
            try { doc.Editor.Regen(); } catch { }
            try
            {
                bool ok = res != null && res.ContainsKey("ok") && res["ok"] is bool && (bool)res["ok"];
                doc.Editor.WriteMessage("\n[AI-Engineer Panel " + mode + "] " + (ok ? "OK" : (res != null && res.ContainsKey("error") ? res["error"].ToString() : "ERR")) + "\n");
            }
            catch { }
        }

        [CommandMethod(PanelCmdGroup, "AIENGPANELSNAP", CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SnapPanelComponentsToDinRail()
        {
            RunPanelMode("snap_and_reroute", null);
        }

        [CommandMethod(PanelCmdGroup, "AIENGBUSBARSIZE", CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ResizeStandardCopperBusbar()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            PromptStringOptions pso = new PromptStringOptions("\nEnter target Cu busbar size (e.g. 28x6, 30x5, 40x10) or leave blank: ");
            pso.AllowSpaces = true;
            PromptResult pr = doc.Editor.GetString(pso);
            Dictionary<string, object> args = new Dictionary<string, object>();
            if (pr.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(pr.StringResult))
                args["target_busbar_size"] = pr.StringResult.Trim();
            RunPanelMode("resize_busbar", args);
        }

        [CommandMethod(PanelCmdGroup, "AIENGWIRETRACE", CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TraceSelectedPanelWireOrDevice()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Dictionary<string, object> args = new Dictionary<string, object>();
            PromptStringOptions pso = new PromptStringOptions("\nEnter WireNo or Device Tag to trace (or Enter to use current selection): ");
            pso.AllowSpaces = false;
            PromptResult pr = doc.Editor.GetString(pso);
            if (pr.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(pr.StringResult))
                args["trace_target"] = pr.StringResult.Trim();
            RunPanelMode("trace_connections", args);
        }

        [CommandMethod(PanelCmdGroup, "AIENGPANELVIEW", CommandFlags.Redraw)]
        public void TogglePanelDepthViewPreset()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            PromptKeywordOptions pko = new PromptKeywordOptions("\nSelect 3D panel view preset [Full/Busbars/Wiring/CNC/NoDoor/Components/2D/3D]: ");
            pko.Keywords.Add("Full");
            pko.Keywords.Add("Busbars");
            pko.Keywords.Add("Wiring");
            pko.Keywords.Add("CNC");
            pko.Keywords.Add("NoDoor");
            pko.Keywords.Add("Components");
            pko.Keywords.Add("2D");
            pko.Keywords.Add("3D");
            pko.Keywords.Default = "Full";
            PromptResult pr = doc.Editor.GetKeywords(pko);
            if (pr.Status != PromptStatus.OK) return;
            string k = (pr.StringResult ?? "Full").Trim().ToLowerInvariant();
            string preset = k == "busbars" ? "busbars_only"
                : (k == "wiring" ? "wiring_uncovered"
                : (k == "cnc" ? "cnc_drilling_only"
                : (k == "nodoor" ? "no_door"
                : (k == "components" ? "components_only"
                : (k == "2d" ? "2d_only"
                : (k == "3d" ? "3d_only" : "full_assembly"))))));
            RunPanelMode("set_view_preset", new Dictionary<string, object> { { "view_preset", preset } });
        }

        [CommandMethod(PanelCmdGroup, "AIENGPANELSLD", CommandFlags.Redraw)]
        public void SyncPanelAndSld()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            PromptKeywordOptions pko = new PromptKeywordOptions("\nSync direction [ToSld/FromSld]: ");
            pko.Keywords.Add("ToSld");
            pko.Keywords.Add("FromSld");
            pko.Keywords.Default = "ToSld";
            PromptResult pr = doc.Editor.GetKeywords(pko);
            if (pr.Status != PromptStatus.OK) return;
            string mode = string.Equals(pr.StringResult, "FromSld", StringComparison.OrdinalIgnoreCase) ? "panel_from_sld" : "sld_from_panel";
            RunPanelMode(mode, null);
        }
    }

    internal sealed class PanelManufacturingPaletteControl : UserControl
    {
        private static readonly string[] ViewPresetKeys = new string[]
        {
            "full_assembly",
            "busbars_only",
            "wiring_uncovered",
            "no_door",
            "cnc_drilling_only",
            "components_only",
            "2d_only",
            "3d_only"
        };

        private static readonly string[] StandardCuBusbarSizes = new string[]
        {
            "15x3", "20x3", "20x5", "25x5", "30x5", "30x10",
            "40x5", "40x10", "50x10", "60x10", "80x10", "100x10",
            "2x60x10", "2x80x10", "2x100x10"
        };

        private readonly ComboBox _language = new ComboBox();
        private readonly ComboBox _providerCombo = new ComboBox();
        private readonly ComboBox _modelCombo = new ComboBox();
        private readonly Button _apiSettings = new Button();
        private readonly Button _newChatBtn = new Button();
        private readonly Label _studioBanner = new Label();
        private readonly Label _studioSubLabel = new Label();
        private readonly Label _panelStatusLabel = new Label();
        private readonly Button _refreshBtn = new Button();

        // Direct one-click hardware & layer controls
        private readonly FlowLayoutPanel _hardwareBar = new FlowLayoutPanel();
        private readonly Button _btnSnapDin = new Button();
        private readonly ComboBox _busbarCombo = new ComboBox();
        private readonly Button _btnResizeBus = new Button();
        private readonly TextBox _traceInput = new TextBox();
        private readonly Button _btnTrace = new Button();
        private readonly Button _btn2Dto3D = new Button();
        private readonly Button _btnPanelToSld = new Button();
        private readonly Button _btnSldToPanel = new Button();
        private readonly Label _viewLabel = new Label();
        private readonly ComboBox _viewPresetCombo = new ComboBox();

        // Transcript + quick AI panel prompts + chat bar
        private readonly TextBox _transcript = new TextBox();
        private readonly FlowLayoutPanel _quickBar = new FlowLayoutPanel();
        private readonly Button _qDesignPanel = new Button();
        private readonly Button _qPartNumber = new Button();
        private readonly Button _qThermal = new Button();
        private readonly Button _qSideCnc = new Button();
        private readonly Button _qVault3D = new Button();
        private readonly Button _qBomExcel = new Button();

        private readonly TextBox _message = new TextBox();
        private readonly Button _send = new Button();
        private readonly Button _voiceMic = new Button();
        private readonly CheckBox _voiceAutoSend = new CheckBox();
        private readonly CheckBox _voiceSpeakReply = new CheckBox();
        private readonly CheckBox _attachVisualSelection = new CheckBox();
        private readonly Button _undoBtn = new Button();
        private readonly Button _clearBtn = new Button();
        private readonly Label _statusLabel = new Label();

        private DirectAiConfig _directConfig;
        private readonly DirectCadAgentSession _directSession = new DirectCadAgentSession();
        private readonly WaveInAudioRecorder _voiceRecorder = new WaveInAudioRecorder();
        private readonly Timer _voiceTimer = new Timer();
        private bool _suppressEvents;
        private bool _voiceTranscribing;
        private bool _busy;

        public PanelManufacturingPaletteControl()
        {
            _directConfig = DirectAiConfig.Load();
            BuildLayout();
            SyncProviderModelUi();
            ApplyLanguage();
            AppendWelcomeNote();
        }

        private bool IsArabic { get { return _language.SelectedIndex == 0; } }

        private string SelectedProvider
        {
            get
            {
                if (_providerCombo.SelectedIndex == 1) return DirectAiConfig.ProviderClaude;
                if (_providerCombo.SelectedIndex == 2) return DirectAiConfig.ProviderOpenAI;
                return DirectAiConfig.ProviderGemini;
            }
        }

        private string SelectedModel { get { return (_modelCombo.Text ?? "").Trim(); } }

        private void BuildLayout()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowCount = 11;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 0: banner
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 1: provider + language
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 2: model + api + new
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 3: panel status + refresh
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 4: hardware direct toolbar
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 5: transcript
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 6: quick AI chips
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); // 7: input
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 8: actions
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 9: status
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 10: subtitle reminder

            _studioBanner.AutoSize = true;
            _studioBanner.Font = new System.Drawing.Font("Segoe UI", 9.5F, FontStyle.Bold);
            _studioBanner.Padding = new Padding(4, 4, 4, 2);
            _studioBanner.Margin = new Padding(0, 0, 0, 1);

            _studioSubLabel.AutoSize = true;
            _studioSubLabel.Font = new System.Drawing.Font("Segoe UI", 8F, FontStyle.Regular);
            _studioSubLabel.Padding = new Padding(4, 0, 4, 2);

            _language.DropDownStyle = ComboBoxStyle.DropDownList;
            _language.Width = 74;
            _language.Margin = new Padding(2);
            _language.Items.Add("العربية");
            _language.Items.Add("English");
            _language.SelectedIndex = 0;
            _language.SelectedIndexChanged += delegate { ApplyLanguage(); };

            _providerCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _providerCombo.Width = 178;
            _providerCombo.Margin = new Padding(2);
            _providerCombo.Items.Add("Gemini Direct (API)");
            _providerCombo.Items.Add("Claude Direct (API)");
            _providerCombo.Items.Add("OpenAI / Codex Direct (API)");
            _providerCombo.SelectedIndexChanged += delegate
            {
                if (_suppressEvents) return;
                _directConfig.Provider = SelectedProvider;
                _directConfig.Save();
                PopulateModelPresets();
                RefreshPanelStatus();
            };

            _modelCombo.DropDownStyle = ComboBoxStyle.DropDown;
            _modelCombo.Width = 176;
            _modelCombo.DropDownWidth = 235;
            _modelCombo.Margin = new Padding(2);
            _modelCombo.Leave += delegate
            {
                if (!_suppressEvents && !string.IsNullOrWhiteSpace(_modelCombo.Text))
                {
                    _directConfig.SetModel(SelectedProvider, _modelCombo.Text);
                    _directConfig.Save();
                }
            };

            _apiSettings.Text = "🔑 API";
            _apiSettings.AutoSize = true;
            _apiSettings.Margin = new Padding(2);
            _apiSettings.Padding = new Padding(4, 2, 4, 2);
            _apiSettings.Click += delegate
            {
                using (ApiKeysDialog dlg = new ApiKeysDialog(_directConfig, IsArabic))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _directConfig = DirectAiConfig.Load();
                        SyncProviderModelUi();
                        RefreshPanelStatus();
                    }
                }
            };

            _newChatBtn.AutoSize = true;
            _newChatBtn.Margin = new Padding(2);
            _newChatBtn.Padding = new Padding(4, 2, 4, 2);
            _newChatBtn.Click += delegate
            {
                VoiceSpeechOutput.Stop();
                _directSession.ClearHistory();
                _transcript.Clear();
                AppendWelcomeNote();
                SetStatus(IsArabic ? "تم بدء جلسة تصنيع لوحة جديدة." : "Started new panel manufacturing session.");
            };

            FlowLayoutPanel topRow = new FlowLayoutPanel();
            topRow.Dock = DockStyle.Fill;
            topRow.AutoSize = true;
            topRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            topRow.WrapContents = true;
            topRow.Controls.Add(_providerCombo);
            topRow.Controls.Add(_language);

            FlowLayoutPanel modelRow = new FlowLayoutPanel();
            modelRow.Dock = DockStyle.Fill;
            modelRow.AutoSize = true;
            modelRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            modelRow.WrapContents = true;
            modelRow.Controls.Add(_modelCombo);
            modelRow.Controls.Add(_apiSettings);
            modelRow.Controls.Add(_newChatBtn);

            _panelStatusLabel.AutoSize = true;
            _panelStatusLabel.Margin = new Padding(2, 4, 2, 2);
            _refreshBtn.AutoSize = true;
            _refreshBtn.Margin = new Padding(2);
            _refreshBtn.Padding = new Padding(4, 2, 4, 2);
            _refreshBtn.Click += delegate { RefreshPanelStatus(); };

            FlowLayoutPanel statusRow = new FlowLayoutPanel();
            statusRow.Dock = DockStyle.Fill;
            statusRow.AutoSize = true;
            statusRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            statusRow.WrapContents = true;
            statusRow.Controls.Add(_refreshBtn);
            statusRow.Controls.Add(_panelStatusLabel);

            // Hardware & Live Fabrication Bar
            _hardwareBar.Dock = DockStyle.Fill;
            _hardwareBar.AutoSize = true;
            _hardwareBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _hardwareBar.WrapContents = true;
            _hardwareBar.Padding = new Padding(3);
            _hardwareBar.Margin = new Padding(0, 2, 0, 3);

            _busbarCombo.DropDownStyle = ComboBoxStyle.DropDown;
            _busbarCombo.Width = 76;
            _busbarCombo.Margin = new Padding(2, 3, 2, 2);
            foreach (string sz in StandardCuBusbarSizes) _busbarCombo.Items.Add(sz);
            _busbarCombo.Text = "30x5";

            _traceInput.Width = 72;
            _traceInput.Margin = new Padding(2, 3, 2, 2);

            _viewLabel.AutoSize = true;
            _viewLabel.Margin = new Padding(2, 6, 2, 2);

            _viewPresetCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _viewPresetCombo.Width = 152;
            _viewPresetCombo.DropDownWidth = 220;
            _viewPresetCombo.Margin = new Padding(2);

            Button[] hwBtns = new Button[] { _btnSnapDin, _btnResizeBus, _btnTrace, _btn2Dto3D, _btnPanelToSld, _btnSldToPanel };
            foreach (Button b in hwBtns)
            {
                b.AutoSize = true;
                b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                b.Margin = new Padding(2);
                b.Padding = new Padding(5, 2, 5, 2);
                b.FlatStyle = FlatStyle.Flat;
                b.Cursor = Cursors.Hand;
            }

            _hardwareBar.Controls.Add(_btnSnapDin);
            _hardwareBar.Controls.Add(_busbarCombo);
            _hardwareBar.Controls.Add(_btnResizeBus);
            _hardwareBar.Controls.Add(_traceInput);
            _hardwareBar.Controls.Add(_btnTrace);
            _hardwareBar.Controls.Add(_btn2Dto3D);
            _hardwareBar.Controls.Add(_btnPanelToSld);
            _hardwareBar.Controls.Add(_btnSldToPanel);
            _hardwareBar.Controls.Add(_viewLabel);
            _hardwareBar.Controls.Add(_viewPresetCombo);

            _btnSnapDin.Click += delegate
            {
                ExecuteDirectPanelAction("snap_and_reroute", null, "محاذاة القواطع على DIN Rail", "Snap to DIN Rail");
            };
            _btnResizeBus.Click += delegate
            {
                Dictionary<string, object> ex = new Dictionary<string, object>();
                string sz = (_busbarCombo.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(sz)) ex["target_busbar_size"] = sz;
                ExecuteDirectPanelAction("resize_busbar", ex, "تطبيق مقاس البارة القياسي", "Resize Cu Busbar");
            };
            _btnTrace.Click += delegate
            {
                Dictionary<string, object> ex = new Dictionary<string, object>();
                string tgt = (_traceInput.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(tgt)) ex["trace_target"] = tgt;
                ExecuteDirectPanelAction("trace_connections", ex, "تتبع الأسلاك والقاطع", "Trace Wire/Device");
            };
            _btn2Dto3D.Click += delegate
            {
                ExecuteDirectPanelAction("3d_from_2d", null, "تحديث التجميع الـ 3D من اللوحة 2D", "Rebuild 3D from 2D");
            };
            _btnPanelToSld.Click += delegate
            {
                ExecuteDirectPanelAction("sld_from_panel", null, "توليد SLD من اللوحة", "Panel -> SLD");
            };
            _btnSldToPanel.Click += delegate
            {
                ExecuteDirectPanelAction("panel_from_sld", null, "تحويل SLD إلى لوحة 2D+3D", "SLD -> 2D+3D Panel");
            };
            _viewPresetCombo.SelectedIndexChanged += delegate
            {
                if (_suppressEvents) return;
                int idx = _viewPresetCombo.SelectedIndex;
                if (idx < 0 || idx >= ViewPresetKeys.Length) return;
                ExecuteDirectPanelAction("set_view_preset",
                    new Dictionary<string, object> { { "view_preset", ViewPresetKeys[idx] } },
                    "تبديل طبقات الـ 3D", "View Preset");
            };

            _transcript.Dock = DockStyle.Fill;
            _transcript.Multiline = true;
            _transcript.ReadOnly = true;
            _transcript.ScrollBars = ScrollBars.Vertical;

            _quickBar.Dock = DockStyle.Fill;
            _quickBar.AutoSize = true;
            _quickBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _quickBar.WrapContents = true;
            _quickBar.Margin = new Padding(0, 2, 0, 2);
            Button[] quickBtns = new Button[] { _qDesignPanel, _qPartNumber, _qThermal, _qSideCnc, _qVault3D, _qBomExcel };
            foreach (Button qb in quickBtns)
            {
                qb.AutoSize = true;
                qb.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                qb.Margin = new Padding(2);
                qb.Padding = new Padding(5, 2, 5, 2);
                qb.FlatStyle = FlatStyle.Flat;
                qb.Cursor = Cursors.Hand;
                _quickBar.Controls.Add(qb);
            }

            _qDesignPanel.Click += delegate
            {
                InsertEditablePrompt(
                    "صمم لوحة تصنيع 2D و 3D اسمها MCC-01 (دخول الكابلات من أسفل bottom، درجة حرارة 40C، احتياطي 20%) تحتوي قاطع رئيسي 100A LV429630 وعدد 3 قواطع MCB 16A وكونتاكتور LC1D32M7 و VFD ATV320U22N4C مع السكشن الجانبي وتخريم الـ CNC",
                    "Design a complete 2D+3D manufacturing panel MCC-01 (bottom entry, 40C, 20% spare) with LV429630 100A incomer, 3x 16A MCBs, LC1D32M7 contactor, and ATV320U22N4C VFD including side section and CNC drilling");
            };
            _qPartNumber.Click += delegate
            {
                InsertEditablePrompt(
                    "ابحث بالـ Part Number LV429630 و A9F74116 و ATV320U22N4C وابنِ اللوحة بالمقاسات الحقيقية وتوزيع البارات النحاس الأمامي والخلفي",
                    "Build panel assembly using exact part numbers LV429630, A9F74116, ATV320U22N4C with real dimensions and copper busbar routing");
            };
            _qThermal.Click += delegate
            {
                InsertEditablePrompt(
                    "اعمل الحسابات الحرارية للوحة وفق IEC 61439 عند حرارة خارجية 45C وأقصى حرارة داخلية 55C واختر مروحة ClimaSys والبارات النحاس المناسبة بدون تصادم",
                    "Run IEC 61439 thermal rise analysis at 45C ambient / 55C max internal, select ClimaSys fan and Cu busbars with zero collision");
            };
            _qSideCnc.Click += delegate
            {
                InsertEditablePrompt(
                    "ارسم المسقط الجانبي للوحة (Side View Z-Depth) مع خلوص الباب ومقاس البارة وتخريم بليتة التجميع الـ CNC M4/M5/M6",
                    "Draw panel Side View Z-Depth showing door clearance and CNC mounting plate M4/M5/M6 drill holes");
            };
            _qVault3D.Click += delegate
            {
                InsertEditablePrompt(
                    "اجلب موديلات الـ 3D المتاحة من مجلد vault_dir المحلي (TraceParts/Schneider DWG) أو ابنِ المجسمات القياسية 3D Solid3d على 8 طبقات عمق Z",
                    "Load 3D CAD models from local vault_dir (TraceParts/Schneider DWG) or generate parametric 3D Solid3d stack on 8 Z-depth layers");
            };
            _qBomExcel.Click += delegate
            {
                InsertEditablePrompt(
                    "صدّر جدول خامات تصنيع اللوحة BOM وأطوال وأرقام الأسلاك From-To والفازات إلى ملف Excel .xlsx مع أداة تصنيع اللوحة",
                    "Export full panel BOM, From-To wire cut lengths, ferrule labels, and busbar sizing to Excel .xlsx");
            };

            _message.Dock = DockStyle.Fill;
            _message.Multiline = true;
            _message.MaxLength = 16384;
            _message.ScrollBars = ScrollBars.Vertical;
            _message.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
                {
                    e.SuppressKeyPress = true;
                    await SendPanelAiMessage();
                }
            };

            Button[] actBtns = new Button[] { _send, _voiceMic, _undoBtn, _clearBtn };
            foreach (Button b in actBtns)
            {
                b.AutoSize = true;
                b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                b.Margin = new Padding(2);
                b.Padding = new Padding(5, 2, 5, 2);
                b.FlatStyle = FlatStyle.Flat;
                b.Cursor = Cursors.Hand;
            }
            _voiceAutoSend.AutoSize = true;
            _voiceAutoSend.Margin = new Padding(4, 5, 2, 2);
            _voiceAutoSend.Checked = _directConfig != null ? _directConfig.VoiceAutoSend : true;
            _voiceAutoSend.CheckedChanged += delegate
            {
                if (_suppressEvents || _directConfig == null) return;
                _directConfig.VoiceAutoSend = _voiceAutoSend.Checked;
                _directConfig.Save();
            };
            _voiceSpeakReply.AutoSize = true;
            _voiceSpeakReply.Margin = new Padding(4, 5, 2, 2);
            _voiceSpeakReply.Checked = _directConfig != null ? _directConfig.VoiceSpeakReplies : true;
            _voiceSpeakReply.CheckedChanged += delegate
            {
                if (_suppressEvents || _directConfig == null) return;
                _directConfig.VoiceSpeakReplies = _voiceSpeakReply.Checked;
                _directConfig.Save();
                if (!_voiceSpeakReply.Checked) VoiceSpeechOutput.Stop();
            };
            _attachVisualSelection.AutoSize = true;
            _attachVisualSelection.Margin = new Padding(4, 5, 2, 2);
            _attachVisualSelection.Checked = false;

            _voiceTimer.Interval = 300;
            _voiceTimer.Tick += async delegate
            {
                if (!_voiceRecorder.IsRecording) { _voiceTimer.Stop(); return; }
                UpdateVoiceMicVisuals();
                if (_voiceRecorder.ElapsedSeconds >= 120)
                {
                    _voiceTimer.Stop();
                    await ToggleVoiceRecordingAsync();
                }
            };

            _send.Click += async delegate { await SendPanelAiMessage(); };
            _voiceMic.Click += async delegate { await ToggleVoiceRecordingAsync(); };
            _undoBtn.Click += delegate { RunUndo(); };
            _clearBtn.Click += delegate
            {
                VoiceSpeechOutput.Stop();
                _directSession.ClearHistory();
                _transcript.Clear();
                AppendWelcomeNote();
                SetStatus(IsArabic ? "تم مسح السجل." : "Transcript cleared.");
            };

            FlowLayoutPanel actionRow = new FlowLayoutPanel();
            actionRow.Dock = DockStyle.Fill;
            actionRow.AutoSize = true;
            actionRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actionRow.WrapContents = true;
            actionRow.Controls.Add(_send);
            actionRow.Controls.Add(_voiceMic);
            actionRow.Controls.Add(_voiceAutoSend);
            actionRow.Controls.Add(_voiceSpeakReply);
            actionRow.Controls.Add(_attachVisualSelection);
            actionRow.Controls.Add(_undoBtn);
            actionRow.Controls.Add(_clearBtn);

            _statusLabel.AutoSize = true;

            layout.SizeChanged += delegate
            {
                int wrapW = Math.Max(180, layout.ClientSize.Width - 10);
                Size maxSz = new Size(wrapW, 0);
                topRow.MaximumSize = maxSz;
                modelRow.MaximumSize = maxSz;
                statusRow.MaximumSize = maxSz;
                _hardwareBar.MaximumSize = maxSz;
                _quickBar.MaximumSize = maxSz;
                actionRow.MaximumSize = maxSz;
                _studioBanner.MaximumSize = maxSz;
                _studioSubLabel.MaximumSize = maxSz;
                _panelStatusLabel.MaximumSize = maxSz;
                _statusLabel.MaximumSize = maxSz;
            };

            layout.Controls.Add(_studioBanner, 0, 0);
            layout.Controls.Add(topRow, 0, 1);
            layout.Controls.Add(modelRow, 0, 2);
            layout.Controls.Add(statusRow, 0, 3);
            layout.Controls.Add(_hardwareBar, 0, 4);
            layout.Controls.Add(_transcript, 0, 5);
            layout.Controls.Add(_quickBar, 0, 6);
            layout.Controls.Add(_message, 0, 7);
            layout.Controls.Add(actionRow, 0, 8);
            layout.Controls.Add(_statusLabel, 0, 9);
            layout.Controls.Add(_studioSubLabel, 0, 10);
            Controls.Add(layout);

            ApplyCopperDarkTheme();
        }

        private void ApplyCopperDarkTheme()
        {
            Color bgMain = Color.FromArgb(30, 33, 38);
            Color bgSurface = Color.FromArgb(42, 46, 54);
            Color bgInput = Color.FromArgb(23, 25, 30);
            Color fgMain = Color.FromArgb(234, 238, 243);
            Color fgMuted = Color.FromArgb(160, 170, 182);
            Color borderCol = Color.FromArgb(72, 80, 92);
            Color copperAccent = Color.FromArgb(194, 65, 12);
            Color copperText = Color.FromArgb(251, 146, 60);

            BackColor = bgMain;
            ForeColor = fgMain;
            try { Font = new System.Drawing.Font("Segoe UI", 9F, FontStyle.Regular); } catch { }

            _studioBanner.BackColor = Color.FromArgb(48, 32, 24);
            _studioBanner.ForeColor = copperText;
            _studioSubLabel.ForeColor = fgMuted;
            _hardwareBar.BackColor = bgSurface;

            ComboBox[] combos = new ComboBox[] { _language, _providerCombo, _modelCombo, _busbarCombo, _viewPresetCombo };
            foreach (ComboBox cb in combos)
            {
                cb.BackColor = bgSurface;
                cb.ForeColor = fgMain;
                cb.FlatStyle = FlatStyle.Flat;
            }

            _traceInput.BackColor = bgInput;
            _traceInput.ForeColor = fgMain;
            _traceInput.BorderStyle = BorderStyle.FixedSingle;

            _transcript.BackColor = bgInput;
            _transcript.ForeColor = fgMain;
            _transcript.BorderStyle = BorderStyle.FixedSingle;

            _message.BackColor = bgInput;
            _message.ForeColor = fgMain;
            _message.BorderStyle = BorderStyle.FixedSingle;

            _panelStatusLabel.ForeColor = Color.FromArgb(125, 249, 255);
            _statusLabel.ForeColor = fgMuted;
            _viewLabel.ForeColor = fgMain;
            _voiceAutoSend.ForeColor = fgMain;
            _voiceSpeakReply.ForeColor = fgMain;
            _attachVisualSelection.ForeColor = Color.FromArgb(125, 249, 255);

            Button[] stdBtns = new Button[] {
                _apiSettings, _newChatBtn, _refreshBtn, _voiceMic, _clearBtn,
                _btnSnapDin, _btnResizeBus, _btnTrace, _btn2Dto3D, _btnPanelToSld, _btnSldToPanel,
                _qDesignPanel, _qPartNumber, _qThermal, _qSideCnc, _qVault3D, _qBomExcel
            };
            foreach (Button b in stdBtns)
            {
                b.BackColor = Color.FromArgb(50, 56, 66);
                b.ForeColor = fgMain;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = borderCol;
            }

            _send.BackColor = copperAccent;
            _send.ForeColor = Color.White;
            _send.FlatAppearance.BorderColor = copperText;

            _undoBtn.BackColor = Color.FromArgb(153, 27, 27);
            _undoBtn.ForeColor = Color.White;
            _undoBtn.FlatAppearance.BorderColor = Color.FromArgb(239, 68, 68);
        }

        private void SyncProviderModelUi()
        {
            _suppressEvents = true;
            try
            {
                string p = (_directConfig.Provider ?? DirectAiConfig.ProviderGemini).ToLowerInvariant();
                if (p == DirectAiConfig.ProviderClaude) _providerCombo.SelectedIndex = 1;
                else if (p == DirectAiConfig.ProviderOpenAI) _providerCombo.SelectedIndex = 2;
                else _providerCombo.SelectedIndex = 0;
                PopulateModelPresets();
            }
            finally { _suppressEvents = false; }
        }

        private void PopulateModelPresets()
        {
            _modelCombo.Items.Clear();
            foreach (string m in DirectAiConfig.GetModelPresets(SelectedProvider))
                _modelCombo.Items.Add(m);
            _modelCombo.Text = _directConfig.GetModel(SelectedProvider);
        }

        private void PopulateViewPresets()
        {
            bool prev = _suppressEvents;
            _suppressEvents = true;
            try
            {
                int prevIdx = _viewPresetCombo.SelectedIndex;
                _viewPresetCombo.Items.Clear();
                if (IsArabic)
                {
                    _viewPresetCombo.Items.Add("الكل (Full 2D+3D Assembly)");
                    _viewPresetCombo.Items.Add("البارات والقواطع فقط (Busbars Only)");
                    _viewPresetCombo.Items.Add("الأسلاك مكشوفة (No Duct Covers)");
                    _viewPresetCombo.Items.Add("بدون الباب (No Door)");
                    _viewPresetCombo.Items.Add("تخريم الـ CNC والبليتة فقط");
                    _viewPresetCombo.Items.Add("القواطع فقط (Components Only)");
                    _viewPresetCombo.Items.Add("طبقة 2D فقط (GA + Section)");
                    _viewPresetCombo.Items.Add("مجسمات 3D فقط (8 Z-Layers)");
                }
                else
                {
                    _viewPresetCombo.Items.Add("Full 2D+3D Assembly");
                    _viewPresetCombo.Items.Add("Busbars Only");
                    _viewPresetCombo.Items.Add("Wiring Uncovered (No Duct Covers)");
                    _viewPresetCombo.Items.Add("No Door / Dead-Front");
                    _viewPresetCombo.Items.Add("CNC Drilling & Mounting Plate Only");
                    _viewPresetCombo.Items.Add("Components Only");
                    _viewPresetCombo.Items.Add("2D Only (GA + Side Section)");
                    _viewPresetCombo.Items.Add("3D Z-Stack Only (8 Depth Layers)");
                }
                _viewPresetCombo.SelectedIndex = (prevIdx >= 0 && prevIdx < _viewPresetCombo.Items.Count) ? prevIdx : 0;
            }
            finally { _suppressEvents = prev; }
        }

        private void ApplyLanguage()
        {
            RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
            _studioBanner.Text = IsArabic
                ? "🏭 أستوديو تصنيع اللوحات الكهربائية 2D / 3D (AIENGPANEL)"
                : "🏭 Electrical Panel Manufacturing Studio 2D / 3D (AIENGPANEL)";
            _studioSubLabel.Text = IsArabic
                ? "أداة مستقلة لتصنيع اللوحات والبارات والـ 3D والـ CNC والـ SLD (منفصلة عن أداة المخططات AIENGPALETTE)"
                : "Dedicated Panel Manufacturing tool (separate from MEP Floor-Plan AIENGPALETTE)";

            _newChatBtn.Text = IsArabic ? "🆕 جديد" : "🆕 New";
            _refreshBtn.Text = IsArabic ? "🔄 فحص اللوحة" : "🔄 Scan Panel";
            _btnSnapDin.Text = IsArabic ? "🧲 محاذاة DIN Rail" : "🧲 Snap DIN Rail";
            _btnResizeBus.Text = IsArabic ? "📏 بارة Cu" : "📏 Resize Cu Bar";
            _btnTrace.Text = IsArabic ? "🔍 تتبع سلك/قاطع" : "🔍 Trace Wire";
            _btn2Dto3D.Text = IsArabic ? "🧊 2D ➔ 3D" : "🧊 2D ➔ 3D";
            _btnPanelToSld.Text = IsArabic ? "📋 لوحة ➔ SLD" : "📋 Panel ➔ SLD";
            _btnSldToPanel.Text = IsArabic ? "⚡ SLD ➔ لوحة" : "⚡ SLD ➔ Panel";
            _viewLabel.Text = IsArabic ? "طبقات العمق:" : "Depth View:";
            PopulateViewPresets();

            _qDesignPanel.Text = IsArabic ? "🏭 لوحة 2D+3D" : "🏭 Panel 2D+3D";
            _qPartNumber.Text = IsArabic ? "📦 Part No." : "📦 Part No.";
            _qThermal.Text = IsArabic ? "🌡️ حراري IEC" : "🌡️ Thermal IEC";
            _qSideCnc.Text = IsArabic ? "📐 مسقط + CNC" : "📐 Section + CNC";
            _qVault3D.Text = IsArabic ? "🧩 3D Vault" : "🧩 3D Vault";
            _qBomExcel.Text = IsArabic ? "📊 BOM Excel" : "📊 BOM Excel";

            _send.Text = IsArabic ? "🤖 اسأل AI اللوحات" : "🤖 Ask Panel AI";
            _voiceAutoSend.Text = IsArabic ? "إرسال تلقائي" : "Auto-send";
            _voiceSpeakReply.Text = IsArabic ? "🔊 نطق" : "🔊 Speak";
            _attachVisualSelection.Text = IsArabic ? "👁️ رؤية المحدد" : "👁️ See Selection";
            _undoBtn.Text = IsArabic ? "❌ تراجع" : "❌ Undo";
            _clearBtn.Text = IsArabic ? "🧹 مسح" : "🧹 Clear";
            UpdateVoiceMicVisuals();
            RefreshPanelStatus();
        }

        private void AppendWelcomeNote()
        {
            if (_transcript.TextLength > 0) return;
            _transcript.AppendText(IsArabic
                ? "🏭 [أداة تصنيع اللوحات الكهربائية 2D/3D — AIENGPANEL]\r\n" +
                  "• يمكنك الضغط مباشرة على أزرار التصنيع أعلاه (محاذاة DIN Rail / مقاس البارة القياسي / تتبع سلك أو قاطع / تبديل طبقات الـ 3D الـ 8 / تحويل SLD ↔ لوحة) بدون استهلاك API.\r\n" +
                  "• أو اكتب طلبك بالأسفل (مثلاً بأرقام القطع Schneider / ABB / Siemens أو جدول الأحمال أو مجلد 3D Vault).\r\n\r\n"
                : "🏭 [Electrical Panel Manufacturing Studio 2D/3D — AIENGPANEL]\r\n" +
                  "• Use the hardware toolbar buttons above directly (Snap DIN Rail, Resize DIN 43671 Cu Busbar, Trace Wire/Device, 8 Z-Depth Presets, SLD <-> Panel Sync).\r\n" +
                  "• Or chat naturally below using Part Numbers (Schneider / ABB / Siemens) or local 3D CAD vault.\r\n\r\n");
        }

        private void RefreshPanelStatus()
        {
            try
            {
                Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null || doc.Database == null)
                {
                    _panelStatusLabel.Text = IsArabic ? "افتح ملف DWG لبدء تصنيع اللوحة." : "Open a DWG file to inspect/build panel.";
                    return;
                }
                int count2d = 0, count3d = 0, wireCount = 0;
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                    foreach (ObjectId oid in ms)
                    {
                        if (oid.IsErased) continue;
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        string lyr = ent.Layer ?? "";
                        if (lyr.StartsWith("_AI_PANEL_", StringComparison.OrdinalIgnoreCase) || string.Equals(lyr, "_AI_CNC_DRILLING", StringComparison.OrdinalIgnoreCase))
                            count2d++;
                        else if (lyr.StartsWith("_AI_3D_", StringComparison.OrdinalIgnoreCase))
                            count3d++;
                        if (ent is Polyline3d && !string.IsNullOrEmpty(CadToolExecutor.GetPanelEntityXDataAscii(ent, "AIENG_PANEL_NET")))
                            wireCount++;
                    }
                    tr.Commit();
                }
                string fn = Path.GetFileName(doc.Name ?? "Drawing.dwg");
                string units = doc.Database.Insunits.ToString();
                _panelStatusLabel.Text = IsArabic
                    ? string.Format(CultureInfo.InvariantCulture, "{0} ({1}) | 2D: {2} | 3D: {3} | أسلاك: {4}", fn, units, count2d, count3d, wireCount)
                    : string.Format(CultureInfo.InvariantCulture, "{0} ({1}) | 2D: {2} | 3D: {3} | Wires: {4}", fn, units, count2d, count3d, wireCount);
            }
            catch
            {
                _panelStatusLabel.Text = IsArabic ? "أداة تصنيع اللوحات جاهزة." : "Panel Manufacturing Studio ready.";
            }
        }

        internal Dictionary<string, object> ExecuteDirectPanelToolForTest(string mode, Dictionary<string, object> extra)
        {
            return ExecuteDirectPanelAction(mode, extra, mode, mode);
        }

        private Dictionary<string, object> ExecuteDirectPanelAction(string mode, Dictionary<string, object> extra, string arLabel, string enLabel)
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            Dictionary<string, object> args = extra != null ? new Dictionary<string, object>(extra) : new Dictionary<string, object>();
            args["mode"] = mode;
            Dictionary<string, object> res = CadToolExecutor.ExecuteTool(doc, "design_panel_manufacturing_layout", args);
            try { if (doc != null && doc.Editor != null) doc.Editor.Regen(); } catch { }

            bool ok = res != null && res.ContainsKey("ok") && res["ok"] is bool && (bool)res["ok"];
            string title = IsArabic ? arLabel : enLabel;
            if (!ok)
            {
                string err = (res != null && res.ContainsKey("error") && res["error"] != null) ? res["error"].ToString() : "Action failed.";
                AppendTranscriptLine("⚠️ [" + title + "]: " + err);
                SetStatus(err);
                return res;
            }

            AppendTranscriptLine("✅ [" + title + "]: " + FormatPanelActionSummary(mode, res));
            RefreshPanelStatus();
            SetStatus((IsArabic ? "تم تنفيذ: " : "Completed: ") + title);
            return res;
        }

        private string FormatPanelActionSummary(string mode, Dictionary<string, object> res)
        {
            if (res == null) return "OK";
            if (mode == "trace_connections")
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("Target=" + res["target"] + " | In=" + res["incoming_count"] + " | Out=" + res["outgoing_count"]);
                List<string> chain = res.ContainsKey("downstream_chain") ? res["downstream_chain"] as List<string> : null;
                if (chain != null && chain.Count > 0) sb.Append(" | Chain: " + string.Join(" -> ", chain.ToArray()));
                List<object> outs = res.ContainsKey("outgoing_connections") ? res["outgoing_connections"] as List<object> : null;
                if (outs != null)
                {
                    foreach (object wo in outs)
                    {
                        Dictionary<string, object> wd = wo as Dictionary<string, object>;
                        if (wd != null)
                            sb.Append("\r\n   • " + wd["wire_no"] + ": " + wd["from"] + " -> " + wd["to"] + " (" + wd["size_mm2"] + "mm2 " + wd["phase"] + ", " + wd["length_mm"] + "mm)");
                    }
                }
                return sb.ToString();
            }
            if (mode == "resize_busbar")
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "Std={0} ({1}x{2}mm, {3}A) | Updated 2D Bars={4}",
                    res["selected_standard_busbar"], res["standard_width_mm"], res["standard_thickness_mm"],
                    res["max_amps_35k"], res["updated_2d_busbars_count"]);
            }
            if (mode == "snap_and_reroute")
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "Snapped={0}, MaxDeltaY={1}mm, ZeroCollision={2}, Components={3}, Wires={4}",
                    res["snapped_components_count"], res["max_y_snap_delta_mm"],
                    res["zero_collision_verified"], res["components_count"], res["wires_count"]);
            }
            if (mode == "set_view_preset")
            {
                List<string> on = res["layers_on"] as List<string>;
                List<string> off = res["layers_off"] as List<string>;
                return "Preset=" + res["view_preset"] + " | ON=" + (on != null ? on.Count : 0) + " | OFF=" + (off != null ? off.Count : 0);
            }
            if (mode == "sld_from_panel")
            {
                return "Panel=" + res["panel_name"] + " -> SLD Synced (" + res["components_synced"] + " components)";
            }
            if (mode == "panel_from_sld" || mode == "3d_from_2d")
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "Panel={0} | 2D={1} | 3D={2} | Components={3} | Wires={4}",
                    res["panel_name"], res["layout_2d_drawn"], res["assembly_3d_drawn"],
                    res["components_count"], res["wires_count"]);
            }
            return "OK";
        }

        private void RunUndo()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Dictionary<string, object> undoRes = CadToolExecutor.RollbackLastDirectAction(doc);
            bool ok = undoRes != null && undoRes.ContainsKey("ok") && undoRes["ok"] is bool && (bool)undoRes["ok"];
            if (ok)
            {
                AppendTranscriptLine(IsArabic
                    ? "↩️ [تراجع]: تم استعادة اللوحة قبل آخر تعديل بنجاح."
                    : "↩️ [Undo]: Reverted last panel CAD action.");
                RefreshPanelStatus();
                SetStatus(IsArabic ? "تم التراجع عن آخر تعديل." : "Reverted last action.");
            }
            else
            {
                string err = (undoRes != null && undoRes.ContainsKey("error") && undoRes["error"] != null) ? undoRes["error"].ToString() : "Nothing to undo.";
                AppendTranscriptLine("⚠️ [" + (IsArabic ? "تراجع" : "Undo") + "]: " + err);
                SetStatus(err);
            }
        }

        private async Task SendPanelAiMessage()
        {
            string content = (_message.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                SetStatus(IsArabic ? "اكتب طلباً لتصنيع اللوحة أولاً." : "Enter a panel manufacturing request first.");
                return;
            }
            if (_busy) return;
            _busy = true;
            UpdateBusyState();
            try
            {
                VoiceSpeechOutput.Stop();
                string provider = SelectedProvider;
                string model = SelectedModel;
                if (!string.IsNullOrWhiteSpace(model))
                {
                    _directConfig.SetModel(provider, model);
                    _directConfig.Save();
                }

                _message.Clear();
                AppendTranscriptLine("engineer: " + content);

                string taggedPrompt = "[أداة تصنيع اللوحات 2D/3D — استخدم design_panel_manufacturing_layout و calculate_lv_electrical عند اللزوم]\n" + content;
                DirectAgentReply reply = await _directSession.SendAsync(
                    this, _directConfig, provider, model, taggedPrompt, IsArabic, SetStatus, _attachVisualSelection.Checked);

                if (!string.IsNullOrEmpty(reply.FallbackNotice))
                    AppendTranscriptLine("ℹ️ " + reply.FallbackNotice);
                if (reply.ExecutedTools != null && reply.ExecutedTools.Count > 0)
                    AppendTranscriptLine("🛠️ [" + string.Join(" | ", reply.ExecutedTools.ToArray()) + "]");

                AppendTranscriptLine(reply.ProviderUsed + " (" + reply.ModelUsed + "): " + reply.Text);
                if (_voiceSpeakReply.Checked && !string.IsNullOrWhiteSpace(reply.Text))
                    VoiceSpeechOutput.SpeakAsync(reply.Text);
                RefreshPanelStatus();
                SetStatus(IsArabic ? "تم الرد بنجاح." : "Reply received.");
            }
            catch (System.Exception ex)
            {
                AppendTranscriptLine("⚠️ [Error]: " + ex.Message);
                SetStatus(ex.Message);
            }
            finally
            {
                _busy = false;
                UpdateBusyState();
            }
        }

        private async Task ToggleVoiceRecordingAsync()
        {
            if (_voiceTranscribing || _busy) return;
            if (!_voiceRecorder.IsRecording)
            {
                VoiceSpeechOutput.Stop();
                try
                {
                    _voiceRecorder.Start();
                    _voiceTimer.Start();
                    UpdateVoiceMicVisuals();
                    UpdateBusyState();
                    SetStatus(IsArabic ? "🎤 جاري التسجيل... تحدث ثم اضغط إيقاف." : "🎤 Recording... speak then click Stop.");
                }
                catch (System.Exception ex)
                {
                    _voiceTimer.Stop();
                    UpdateVoiceMicVisuals();
                    UpdateBusyState();
                    SetStatus(ex.Message);
                }
                return;
            }

            _voiceTimer.Stop();
            double dur; int peak;
            byte[] wav = _voiceRecorder.StopAndGetWavBytes(out dur, out peak);
            if (wav == null || wav.Length <= 44 || dur < 0.25 || peak < 80)
            {
                UpdateVoiceMicVisuals();
                UpdateBusyState();
                SetStatus(IsArabic ? "التسجيل قصير أو غير واضح." : "Recording too short or quiet.");
                return;
            }

            _voiceTranscribing = true;
            UpdateVoiceMicVisuals();
            UpdateBusyState();
            SetStatus(IsArabic ? "⏳ جاري تحويل الصوت..." : "⏳ Transcribing...");
            string transcribed = "";
            try
            {
                DirectAiConfig cfg = _directConfig ?? DirectAiConfig.Load();
                bool ar = IsArabic;
                transcribed = await Task.Run(delegate { return VoiceTranscriber.TranscribeWav(cfg, wav, ar); });
            }
            catch (System.Exception ex)
            {
                AppendTranscriptLine("⚠️ [Voice]: " + ex.Message);
                SetStatus(ex.Message);
                return;
            }
            finally
            {
                _voiceTranscribing = false;
                UpdateVoiceMicVisuals();
                UpdateBusyState();
            }

            if (string.IsNullOrWhiteSpace(transcribed)) return;
            _message.Text = string.IsNullOrWhiteSpace(_message.Text) ? transcribed.Trim() : (_message.Text.Trim() + " " + transcribed.Trim());
            if (_voiceAutoSend.Checked)
                await SendPanelAiMessage();
        }

        private void UpdateVoiceMicVisuals()
        {
            if (_voiceTranscribing)
            {
                _voiceMic.Text = "⏳";
                _voiceMic.Enabled = false;
                return;
            }
            _voiceMic.Enabled = !_busy;
            if (_voiceRecorder.IsRecording)
            {
                _voiceMic.Text = "⏹ " + _voiceRecorder.ElapsedSeconds.ToString(CultureInfo.InvariantCulture) + "s";
                _voiceMic.BackColor = Color.FromArgb(185, 28, 28);
            }
            else
            {
                _voiceMic.Text = "🎤";
                _voiceMic.BackColor = Color.FromArgb(50, 56, 66);
            }
        }

        private void UpdateBusyState()
        {
            bool idle = !_busy && !_voiceTranscribing;
            _send.Enabled = idle;
            _btnSnapDin.Enabled = idle;
            _btnResizeBus.Enabled = idle;
            _btnTrace.Enabled = idle;
            _btn2Dto3D.Enabled = idle;
            _btnPanelToSld.Enabled = idle;
            _btnSldToPanel.Enabled = idle;
            _viewPresetCombo.Enabled = idle;
            _undoBtn.Enabled = idle;
            _voiceMic.Enabled = _voiceRecorder.IsRecording || idle;
        }

        private void InsertEditablePrompt(string arText, string enText)
        {
            _message.Text = IsArabic ? arText : enText;
            _message.Focus();
            _message.SelectionStart = _message.TextLength;
            _message.SelectionLength = 0;
            SetStatus(IsArabic ? "يمكنك التعديل على الأمر ثم الضغط على Enter." : "Edit prompt and press Enter to send.");
        }

        private void AppendTranscriptLine(string line)
        {
            _transcript.AppendText(line + "\r\n\r\n");
            _transcript.SelectionStart = _transcript.TextLength;
            _transcript.ScrollToCaret();
        }

        private void SetStatus(string text)
        {
            _statusLabel.Text = text ?? "";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _voiceTimer.Stop(); _voiceTimer.Dispose(); } catch { }
                try { _voiceRecorder.Dispose(); } catch { }
                try { VoiceSpeechOutput.Stop(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    internal static partial class CadToolExecutor
    {
        internal static string GetPanelEntityXDataAscii(Entity ent, string regApp)
        {
            return GetEntityXDataAscii(ent, regApp);
        }
    }
}
