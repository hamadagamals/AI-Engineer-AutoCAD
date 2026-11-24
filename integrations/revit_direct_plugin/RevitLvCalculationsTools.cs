using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace AIEngineer.Revit.Direct
{
    internal static partial class RevitToolExecutor
    {
        private static readonly double[] LvCableSizes = new double[]
        {
            1.5, 2.5, 4.0, 6.0, 10.0, 16.0, 25.0, 35.0, 50.0,
            70.0, 95.0, 120.0, 150.0, 185.0, 240.0, 300.0, 400.0
        };
        private static readonly double[] LvBreakers = new double[]
        {
            6, 10, 16, 20, 25, 32, 40, 50, 63, 80, 100, 125,
            160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3200
        };
        private static readonly double[] LvIcuKa = new double[] { 6, 10, 15, 25, 36, 50, 65, 85, 100 };

        private static readonly double[] CuXlpeTray3P = new double[] { 23, 31, 42, 54, 75, 100, 127, 158, 192, 246, 298, 346, 399, 456, 538, 621, 720 };
        private static readonly double[] CuXlpeConduit3P = new double[] { 19.5, 26, 35, 45, 62, 83, 108, 134, 162, 207, 251, 290, 332, 378, 444, 510, 585 };
        private static readonly double[] CuXlpeBuried3P = new double[] { 26, 34, 44, 56, 75, 98, 128, 157, 185, 228, 275, 313, 353, 399, 464, 524, 600 };
        private static readonly double[] CuPvcTray3P = new double[] { 18.5, 25, 34, 43, 60, 80, 101, 126, 153, 196, 238, 276, 319, 364, 430, 497, 575 };
        private static readonly double[] CuPvcConduit3P = new double[] { 15.5, 21, 28, 36, 50, 68, 89, 110, 134, 171, 207, 239, 275, 314, 370, 426, 490 };
        private static readonly double[] CuPvcBuried3P = new double[] { 22, 29, 37, 46, 61, 79, 101, 122, 144, 178, 211, 240, 271, 304, 351, 396, 450 };
        private static readonly double[] CuR20 = new double[] { 12.10, 7.41, 4.61, 3.08, 1.83, 1.15, 0.727, 0.524, 0.387, 0.268, 0.193, 0.153, 0.124, 0.0991, 0.0754, 0.0601, 0.0470 };
        private static readonly double[] LvX50 = new double[] { 0.115, 0.108, 0.100, 0.095, 0.090, 0.086, 0.083, 0.082, 0.080, 0.079, 0.078, 0.077, 0.077, 0.076, 0.076, 0.075, 0.075 };

        private static double GetCa(double tempC, string ins, string meth)
        {
            bool xlpe = string.Equals(ins, "XLPE", StringComparison.OrdinalIgnoreCase);
            string m = (meth ?? "tray").Trim().ToLowerInvariant();
            bool buried = m == "buried" || m == "ground" || m == "underground";
            double maxT = xlpe ? 90.0 : 70.0;
            if (tempC >= maxT)
                throw new InvalidOperationException("Ambient temperature (" + tempC.ToString("0.#", CultureInfo.InvariantCulture) + "C) meets or exceeds " + ins + " maximum conductor temperature (" + maxT.ToString("0", CultureInfo.InvariantCulture) + "C).");
            double refT = buried ? 20.0 : 30.0;
            double t = Math.Max(10.0, tempC);
            double ratio = (maxT - t) / (maxT - refT);
            return ratio <= 0 ? 0.35 : Math.Round(Math.Max(0.35, Math.Min(1.22, Math.Sqrt(ratio))), 3);
        }

        private static double GetCg(int g)
        {
            if (g <= 1) return 1.00;
            if (g == 2) return 0.80;
            if (g == 3) return 0.70;
            if (g == 4) return 0.65;
            if (g == 5) return 0.60;
            if (g == 6) return 0.57;
            if (g == 7) return 0.54;
            if (g == 8) return 0.52;
            if (g <= 10) return 0.48;
            if (g <= 12) return 0.45;
            return 0.40;
        }

        private static double GetBaseIz(int idx, string mat, string ins, string meth, int phases)
        {
            bool xlpe = string.Equals(ins, "XLPE", StringComparison.OrdinalIgnoreCase);
            bool al = string.Equals(mat, "Al", StringComparison.OrdinalIgnoreCase);
            string m = (meth ?? "tray").Trim().ToLowerInvariant();
            double[] tbl = xlpe
                ? ((m == "conduit" || m == "trunking" || m == "embedded") ? CuXlpeConduit3P : ((m == "buried" || m == "ground" || m == "underground") ? CuXlpeBuried3P : CuXlpeTray3P))
                : ((m == "conduit" || m == "trunking" || m == "embedded") ? CuPvcConduit3P : ((m == "buried" || m == "ground" || m == "underground") ? CuPvcBuried3P : CuPvcTray3P));
            double iz = tbl[idx];
            if (phases == 1) iz *= 1.12;
            if (m == "free_air" || m == "cleats") iz *= 1.05;
            if (al) iz *= 0.78;
            return Math.Round(iz, 1);
        }

        private static double PickBreaker(double reqA)
        {
            for (int i = 0; i < LvBreakers.Length; i++) if (LvBreakers[i] >= reqA - 1e-6) return LvBreakers[i];
            return Math.Ceiling(reqA / 100.0) * 100.0;
        }

        private static double PickIcu(double ka)
        {
            for (int i = 0; i < LvIcuKa.Length; i++) if (LvIcuKa[i] >= ka - 1e-6) return LvIcuKa[i];
            return 100.0;
        }

        private static double PickPe(double phaseMm2)
        {
            return PickPe(phaseMm2, 0.0, 1);
        }

        private static double PickPe(double phaseMm2, double minTotalAdiabaticMm2, int runs)
        {
            double tablePe;
            if (phaseMm2 <= 16.0) tablePe = phaseMm2;
            else if (phaseMm2 <= 35.0) tablePe = 16.0;
            else
            {
                double half = phaseMm2 * 0.5;
                tablePe = 240.0;
                for (int i = 0; i < LvCableSizes.Length; i++)
                {
                    if (LvCableSizes[i] >= half - 1e-6) { tablePe = LvCableSizes[i]; break; }
                }
            }
            double perRunAdiabatic = minTotalAdiabaticMm2 > 0.0 ? (minTotalAdiabaticMm2 / Math.Max(1, runs)) : 0.0;
            double reqPe = Math.Max(tablePe, perRunAdiabatic);
            for (int i = 0; i < LvCableSizes.Length; i++) if (LvCableSizes[i] >= reqPe - 1e-6) return LvCableSizes[i];
            return 240.0;
        }

        internal static Dictionary<string, object> ComputeSingleLv(Dictionary<string, object> item, Dictionary<string, object> defs, int seq)
        {
            string name = GetStr(item, "name") ?? GetStr(item, "circuit") ?? ("C-" + seq.ToString(CultureInfo.InvariantCulture));
            string rawLoadType = (GetStr(item, "load_type") ?? GetStr(defs, "load_type") ?? "power").Trim().ToLowerInvariant();
            string loadType = rawLoadType;
            if (rawLoadType == "lighting" || rawLoadType == "light" || rawLoadType == "luminaire" ||
                rawLoadType == "إنارة" || rawLoadType == "انارة" || rawLoadType == "اضاءة" || rawLoadType == "إضاءة")
                loadType = "lighting";
            else if (rawLoadType == "socket" || rawLoadType == "sockets" || rawLoadType == "receptacle" ||
                     rawLoadType == "بريزة" || rawLoadType == "برايز" || rawLoadType == "أفياش" || rawLoadType == "افياش")
                loadType = "socket";
            bool isLighting = loadType == "lighting";
            int defaultPhases = (loadType == "lighting" || loadType == "socket") ? 1 : 3;
            int phases = GetInt(item, "phases", GetInt(defs, "phases", defaultPhases));
            if (phases != 1 && phases != 3) phases = defaultPhases;
            double voltage = GetDouble(item, "voltage_v", GetDouble(defs, "voltage_v", phases == 1 ? 220.0 : 380.0));
            if (voltage <= 10.0) voltage = phases == 1 ? 220.0 : 380.0;
            double pf = Math.Max(0.4, Math.Min(1.0, GetDouble(item, "power_factor", GetDouble(defs, "power_factor", isLighting ? 0.90 : 0.85))));
            double eff = Math.Max(0.4, Math.Min(1.0, GetDouble(item, "efficiency", GetDouble(defs, "efficiency", 1.0))));
            double df = Math.Max(0.1, Math.Min(1.5, GetDouble(item, "demand_factor", GetDouble(defs, "demand_factor", 1.0))));
            double sf = Math.Max(1.0, Math.Min(2.0, GetDouble(item, "safety_factor", GetDouble(defs, "safety_factor", (loadType == "motor" || loadType == "continuous") ? 1.25 : 1.0))));

            double? kwOpt = GetNullableDouble(item, "load_kw");
            double? kvaOpt = GetNullableDouble(item, "load_kva");
            double? hpOpt = GetNullableDouble(item, "load_hp");
            double? aOpt = GetNullableDouble(item, "load_amps");
            double? fixtureCountOpt = GetNullableDouble(item, "fixture_count") ?? GetNullableDouble(defs, "fixture_count");
            double? fixtureWattOpt = GetNullableDouble(item, "fixture_watt") ?? GetNullableDouble(defs, "fixture_watt");
            double? fixtureVaOpt = GetNullableDouble(item, "fixture_va") ?? GetNullableDouble(defs, "fixture_va");
            string fixtureCalcNote = null;

            double kw, kva, ib;
            if (aOpt.HasValue && aOpt.Value > 0)
            {
                ib = aOpt.Value * df;
                kva = (phases == 3 ? Math.Sqrt(3.0) : 1.0) * voltage * ib / 1000.0;
                kw = kva * pf;
            }
            else if (kwOpt.HasValue && kwOpt.Value > 0)
            {
                kw = kwOpt.Value * df;
                kva = kw / (pf * eff);
                ib = (kva * 1000.0) / ((phases == 3 ? Math.Sqrt(3.0) : 1.0) * voltage);
            }
            else if (kvaOpt.HasValue && kvaOpt.Value > 0)
            {
                kva = kvaOpt.Value * df;
                kw = kva * pf;
                ib = (kva * 1000.0) / ((phases == 3 ? Math.Sqrt(3.0) : 1.0) * voltage);
            }
            else if (hpOpt.HasValue && hpOpt.Value > 0)
            {
                bool hasEff = GetNullableDouble(item, "efficiency").HasValue || GetNullableDouble(defs, "efficiency").HasValue;
                double mEff = hasEff ? eff : 0.88;
                kw = hpOpt.Value * 0.746 * df;
                kva = kw / (pf * mEff);
                ib = (kva * 1000.0) / ((phases == 3 ? Math.Sqrt(3.0) : 1.0) * voltage);
            }
            else if (fixtureCountOpt.HasValue && fixtureCountOpt.Value > 0 && fixtureWattOpt.HasValue && fixtureWattOpt.Value > 0)
            {
                double rawKw = (fixtureCountOpt.Value * fixtureWattOpt.Value) / 1000.0;
                kw = rawKw * df;
                kva = kw / (pf * eff);
                ib = (kva * 1000.0) / ((phases == 3 ? Math.Sqrt(3.0) : 1.0) * voltage);
                fixtureCalcNote = "Fixture load: " + Math.Round(fixtureCountOpt.Value, 2).ToString(CultureInfo.InvariantCulture) +
                    " x " + Math.Round(fixtureWattOpt.Value, 2).ToString(CultureInfo.InvariantCulture) +
                    " W = " + Math.Round(rawKw * 1000.0, 1).ToString(CultureInfo.InvariantCulture) +
                    " W (" + Math.Round(kw, 3).ToString(CultureInfo.InvariantCulture) + " kW).";
            }
            else if (fixtureCountOpt.HasValue && fixtureCountOpt.Value > 0 && fixtureVaOpt.HasValue && fixtureVaOpt.Value > 0)
            {
                double rawKva = (fixtureCountOpt.Value * fixtureVaOpt.Value) / 1000.0;
                kva = rawKva * df;
                kw = kva * pf;
                ib = (kva * 1000.0) / ((phases == 3 ? Math.Sqrt(3.0) : 1.0) * voltage);
                fixtureCalcNote = "Fixture load: " + Math.Round(fixtureCountOpt.Value, 2).ToString(CultureInfo.InvariantCulture) +
                    " x " + Math.Round(fixtureVaOpt.Value, 2).ToString(CultureInfo.InvariantCulture) +
                    " VA = " + Math.Round(rawKva * 1000.0, 1).ToString(CultureInfo.InvariantCulture) +
                    " VA (" + Math.Round(kva, 3).ToString(CultureInfo.InvariantCulture) + " kVA).";
            }
            else
            {
                throw new InvalidOperationException("Circuit '" + name + "' requires one positive load input: load_kw, load_kva, load_hp, load_amps, or fixture_count with fixture_watt/fixture_va.");
            }

            double lenM = Math.Max(0.0, GetDouble(item, "length_m", GetDouble(defs, "length_m", 25.0)));
            string matRaw = (GetStr(item, "material") ?? GetStr(defs, "material") ?? "Cu").Trim().ToUpperInvariant();
            string mat = (matRaw == "AL" || matRaw == "ALUMINUM" || matRaw == "ALUMINIUM" || matRaw == "ألومنيوم") ? "Al" : "Cu";
            string ins = string.Equals((GetStr(item, "insulation") ?? GetStr(defs, "insulation") ?? "XLPE").Trim(), "PVC", StringComparison.OrdinalIgnoreCase) ? "PVC" : "XLPE";
            string meth = (GetStr(item, "install_method") ?? GetStr(defs, "install_method") ?? "tray").Trim().ToLowerInvariant();
            double ambC = GetDouble(item, "ambient_temp_c", GetDouble(defs, "ambient_temp_c", 40.0));
            int grp = Math.Max(1, GetInt(item, "grouped_circuits", GetInt(defs, "grouped_circuits", 1)));

            double? customDer = GetNullableDouble(item, "derating_factor") ?? GetNullableDouble(defs, "derating_factor");
            if (customDer.HasValue && (customDer.Value < 0.05 || customDer.Value > 1.50))
                throw new InvalidOperationException("Explicit derating_factor must be between 0.05 and 1.50.");

            double ca;
            try { ca = GetCa(ambC, ins, meth); }
            catch
            {
                if (!customDer.HasValue) throw;
                ca = 1.0;
            }
            double cg = GetCg(grp);
            double der = customDer.HasValue ? customDer.Value : Math.Round(ca * cg, 3);

            double maxVd = Math.Max(0.5, Math.Min(15.0, GetDouble(item, "max_vd_percent", GetDouble(defs, "max_vd_percent", isLighting ? 3.0 : 5.0))));
            double reqCurrent = ib * sf;
            double? expIn = GetNullableDouble(item, "breaker_a");
            if (expIn.HasValue && expIn.Value < reqCurrent - 1e-6)
                throw new InvalidOperationException("Explicit breaker_a (" + expIn.Value.ToString(CultureInfo.InvariantCulture) + " A) is less than required current Ib * safety_factor (" + Math.Round(reqCurrent, 2).ToString(CultureInfo.InvariantCulture) + " A).");
            double inA = expIn.HasValue ? expIn.Value : PickBreaker(reqCurrent);
            string brkType = inA <= 63.0 ? "MCB" : (inA <= 1600.0 ? "MCCB" : "ACB");

            double minMm2 = (loadType == "socket" || loadType == "power" || loadType == "motor" || loadType == "feeder") ? 2.5 : 1.5;
            if (mat == "Al" && minMm2 < 16.0) minMm2 = 16.0;
            int minIdx = 0;
            for (int i = 0; i < LvCableSizes.Length; i++) if (LvCableSizes[i] >= minMm2 - 1e-6) { minIdx = i; break; }

            double? srcIscOpt = GetNullableDouble(item, "source_isc_ka") ?? GetNullableDouble(defs, "source_isc_ka");
            double? txKvaOpt = GetNullableDouble(item, "transformer_kva") ?? GetNullableDouble(defs, "transformer_kva");
            double txUk = Math.Max(2.0, Math.Min(15.0, GetDouble(item, "transformer_uk_percent", GetDouble(defs, "transformer_uk_percent", 6.0))));
            double faultSec = Math.Max(0.01, Math.Min(5.0, GetDouble(item, "fault_time_sec", GetDouble(defs, "fault_time_sec", 0.1))));

            bool hasSc = false;
            double srcIscKa = 0.0, rSrc = 0.0, xSrc = 0.0;
            double uLL = phases == 3 ? voltage : (voltage * Math.Sqrt(3.0));
            if (srcIscOpt.HasValue && srcIscOpt.Value > 0)
            {
                hasSc = true; srcIscKa = srcIscOpt.Value;
                double zSrc = (1.05 * uLL) / (Math.Sqrt(3.0) * srcIscKa * 1000.0);
                xSrc = zSrc / Math.Sqrt(1.09); rSrc = 0.3 * xSrc;
            }
            else if (txKvaOpt.HasValue && txKvaOpt.Value > 0)
            {
                hasSc = true;
                double zTx = (txUk / 100.0) * (uLL * uLL) / (txKvaOpt.Value * 1000.0);
                xSrc = zTx * 0.96; rSrc = zTx * 0.28;
                srcIscKa = (1.05 * uLL) / (Math.Sqrt(3.0) * zTx * 1000.0);
            }

            double kAd = (mat == "Cu") ? (ins == "XLPE" ? 143.0 : 115.0) : (ins == "XLPE" ? 94.0 : 76.0);
            double sinPhi = Math.Sqrt(Math.Max(0.0, 1.0 - pf * pf));
            double vdMult = phases == 3 ? Math.Sqrt(3.0) : 2.0;

            int chosenIdx = -1, chosenRuns = 1;
            bool upVd = false, upSc = false;
            double chosenIz = 0, chosenVdV = 0, chosenVdPct = 0, loadIscKa = 0, minScMm2 = 0;

            for (int runs = 1; runs <= 8 && chosenIdx < 0; runs++)
            {
                int start = (runs == 1) ? minIdx : Math.Max(minIdx, 9);
                double scLoopMult = (phases == 1) ? 2.0 : 1.0;
                for (int idx = start; idx < LvCableSizes.Length; idx++)
                {
                    double izTot = GetBaseIz(idx, mat, ins, meth, phases) * der * runs;
                    if (izTot < inA - 1e-6) continue;

                    double r20 = CuR20[idx] * (mat == "Al" ? (28.2 / 17.241) : 1.0);
                    double rOp = r20 * (1.0 + (mat == "Al" ? 0.00403 : 0.00393) * ((ins == "XLPE" ? 90.0 : 70.0) - 20.0));
                    double x = LvX50[idx];
                    double vdV = vdMult * ib * (lenM / runs) * (rOp * pf + x * sinPhi) / 1000.0;
                    double vdPct = (vdV / voltage) * 100.0;
                    if (vdPct > maxVd + 1e-6) { upVd = true; continue; }

                    double curIsc = 0, curMinSc = 0;
                    if (hasSc)
                    {
                        double rC = scLoopMult * r20 * (lenM / 1000.0) / runs;
                        double xC = scLoopMult * x * (lenM / 1000.0) / runs;
                        double zT = Math.Sqrt((rSrc + rC) * (rSrc + rC) + (xSrc + xC) * (xSrc + xC));
                        double uFault = (phases == 3) ? (1.05 * uLL / Math.Sqrt(3.0)) : (1.05 * voltage);
                        curIsc = uFault / (Math.Max(1e-6, zT) * 1000.0);
                        // Conservative IEC 60364-4-43 adiabatic check against sending-end fault current near panel origin
                        curMinSc = (srcIscKa * 1000.0 * Math.Sqrt(faultSec)) / kAd;
                        if (LvCableSizes[idx] * runs < curMinSc - 1e-6) { upSc = true; continue; }
                    }

                    chosenIdx = idx; chosenRuns = runs; chosenIz = izTot;
                    chosenVdV = vdV; chosenVdPct = vdPct; loadIscKa = curIsc; minScMm2 = curMinSc;
                    break;
                }
            }
            if (chosenIdx < 0)
                throw new InvalidOperationException("Load on '" + name + "' exceeds 8x400mm2 cable capacity; use busway/split load.");

            double mm2 = LvCableSizes[chosenIdx];
            double pe = PickPe(mm2, hasSc ? minScMm2 : 0.0, chosenRuns);
            string core = phases == 3 ? "4x" : "2x";
            string desig = (chosenRuns > 1 ? chosenRuns + "x(" : "") + core + mm2.ToString("0.#", CultureInfo.InvariantCulture) + " + " +
                           pe.ToString("0.#", CultureInfo.InvariantCulture) + "mm2 " + mat + "/" + ins + (chosenRuns > 1 ? ")" : "");
            double recIcu = hasSc ? PickIcu(srcIscKa) : (inA <= 63 ? 6.0 : (inA <= 250 ? 25.0 : 36.0));

            Dictionary<string, object> d = new Dictionary<string, object>
            {
                { "circuit", name },
                { "load_type", loadType },
                { "phases", phases },
                { "voltage_v", voltage },
                { "load_kw", Math.Round(kw, 3) },
                { "load_kva", Math.Round(kva, 3) },
                { "power_factor", pf },
                { "length_m", Math.Round(lenM, 2) },
                { "design_current_ib_a", Math.Round(ib, 2) },
                { "breaker_in_a", inA },
                { "breaker_type", brkType },
                { "recommended_icu_ka", recIcu },
                { "derating_ca", ca },
                { "derating_cg", cg },
                { "combined_derating", der },
                { "cable_designation", desig },
                { "cable_size_mm2", mm2 },
                { "parallel_runs", chosenRuns },
                { "pe_size_mm2", pe },
                { "derated_ampacity_iz_a", Math.Round(chosenIz, 1) },
                { "voltage_drop_v", Math.Round(chosenVdV, 2) },
                { "voltage_drop_percent", Math.Round(chosenVdPct, 2) },
                { "max_vd_percent", maxVd },
                { "upsized_for_vd", upVd },
                { "upsized_for_sc", upSc },
                { "status", "PASS" }
            };
            if (hasSc)
            {
                d["source_isc_ka"] = Math.Round(srcIscKa, 2);
                d["load_isc_ka"] = Math.Round(loadIscKa, 2);
                d["min_adiabatic_cable_mm2"] = Math.Round(minScMm2, 2);
            }
            List<object> notes = new List<object>();
            if (!string.IsNullOrEmpty(fixtureCalcNote)) notes.Add(fixtureCalcNote);
            if (upVd) notes.Add("Cable upsized to satisfy voltage drop limit (" + maxVd.ToString("0.#", CultureInfo.InvariantCulture) + "%).");
            if (upSc) notes.Add("Cable upsized for short-circuit thermal withstand (k*S/sqrt(t)).");
            if (chosenRuns > 1) notes.Add("Parallel runs selected (" + chosenRuns.ToString(CultureInfo.InvariantCulture) + "x" + mm2.ToString("0.#", CultureInfo.InvariantCulture) + "mm2); ensure identical lengths & phase symmetry.");
            if (loadType == "motor") notes.Add("Motor circuit: select Type C/D MCB or MPCB sized for starting inrush (IEC 60947-4-1).");
            if (notes.Count > 0) d["notes"] = notes;
            return d;
        }

        private static Dictionary<string, object> CalculateLvElectrical(Document doc, Dictionary<string, object> args)
        {
            List<Dictionary<string, object>> inputs = new List<Dictionary<string, object>>();

            // Option 1: Pull real electrical circuits directly from a Revit panel!
            string panelFilter = GetStr(args, "from_model_panel");
            if (!string.IsNullOrWhiteSpace(panelFilter) && doc != null)
            {
                FilteredElementCollector col = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem));
                foreach (Element el in col)
                {
                    ElectricalSystem sys = el as ElectricalSystem;
                    if (sys == null) continue;
                    if (sys.SystemType != ElectricalSystemType.PowerCircuit) continue;
                    string pName = sys.PanelName ?? "";
                    if (!string.Equals(panelFilter, "*", StringComparison.Ordinal) &&
                        pName.IndexOf(panelFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    double rawLenM = 0.0;
                    try { rawLenM = RevitCompat.FeetToMeters(sys.Length); } catch { rawLenM = 0.0; }
                    double lenM = rawLenM > 0.1 ? rawLenM : GetDouble(args, "length_m", 25.0);
                    double apparentVa = sys.ApparentLoad; // Revit internal electrical power is W/VA * 10.7639104167
                    double kva = (apparentVa / 10.7639104167) / 1000.0;
                    if (kva <= 0.001) continue;

                    double volts = sys.Voltage / 10.7639104167;
                    double pfSys = sys.PowerFactor;
                    int poles = sys.PolesNumber >= 3 ? 3 : 1;
                    Dictionary<string, object> cItem = new Dictionary<string, object>
                    {
                        { "name", (string.IsNullOrEmpty(pName) ? "P" : pName) + " / " + (sys.CircuitNumber ?? sys.Name) },
                        { "load_kva", Math.Round(kva, 3) },
                        { "phases", poles },
                        { "length_m", Math.Round(lenM, 1) }
                    };
                    if (volts > 10.0) cItem["voltage_v"] = Math.Round(volts, 1);
                    if (pfSys >= 0.4 && pfSys <= 1.0) cItem["power_factor"] = Math.Round(pfSys, 3);
                    inputs.Add(cItem);
                }
                if (inputs.Count == 0)
                    return Err("No Revit PowerCircuit ElectricalSystems with non-zero ApparentLoad matched panel filter '" + panelFilter + "'.");
            }

            // Option 2: Explicit batch circuits[]
            if (inputs.Count == 0 && args != null && args.ContainsKey("circuits"))
            {
                IEnumerable seq = args["circuits"] as IEnumerable;
                if (seq != null && !(args["circuits"] is string))
                {
                    foreach (object o in seq)
                    {
                        Dictionary<string, object> d = o as Dictionary<string, object>;
                        if (d != null) inputs.Add(d);
                    }
                }
            }
            if (inputs.Count == 0) inputs.Add(args ?? new Dictionary<string, object>());

            List<Dictionary<string, object>> typedResults = new List<Dictionary<string, object>>();
            List<object> results = new List<object>();
            double totKw = 0.0, totKva = 0.0;
            bool hasAnySc = false;
            for (int i = 0; i < inputs.Count; i++)
            {
                Dictionary<string, object> r = ComputeSingleLv(inputs[i], args ?? new Dictionary<string, object>(), i + 1);
                typedResults.Add(r);
                results.Add(r);
                totKw += Convert.ToDouble(r["load_kw"], CultureInfo.InvariantCulture);
                totKva += Convert.ToDouble(r["load_kva"], CultureInfo.InvariantCulture);
                if (r.ContainsKey("load_isc_ka")) hasAnySc = true;
            }

            RevitExcelSheet xl = new RevitExcelSheet();
            xl.Title = "جدول حسابات الجهد المنخفض - Revit (IEC 60364 / ECP)";
            xl.SheetName = "LV_Calculations";
            xl.Headers = hasAnySc
                ? new List<string> { "الدائرة", "الحمل (kW)", "الفاز/الجهد", "التيار Ib (A)", "القاطع In", "معامل التخفيض", "الكابل + PE", "السعة Iz (A)", "الطول (m)", "هبوط الجهد %", "قصر الدائرة Isc (kA)", "الحالة" }
                : new List<string> { "الدائرة", "الحمل (kW)", "الفاز/الجهد", "التيار Ib (A)", "القاطع In", "معامل التخفيض", "الكابل + PE", "السعة Iz (A)", "الطول (m)", "هبوط الجهد %", "الحالة" };

            for (int i = 0; i < typedResults.Count; i++)
            {
                Dictionary<string, object> r = typedResults[i];
                List<string> row = new List<string>
                {
                    Convert.ToString(r["circuit"], CultureInfo.InvariantCulture),
                    Convert.ToDouble(r["load_kw"], CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture),
                    Convert.ToInt32(r["phases"], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "P / " + Convert.ToDouble(r["voltage_v"], CultureInfo.InvariantCulture).ToString("0", CultureInfo.InvariantCulture) + "V",
                    Convert.ToDouble(r["design_current_ib_a"], CultureInfo.InvariantCulture).ToString("0.0", CultureInfo.InvariantCulture),
                    Convert.ToDouble(r["breaker_in_a"], CultureInfo.InvariantCulture).ToString("0", CultureInfo.InvariantCulture) + "A " + Convert.ToString(r["breaker_type"], CultureInfo.InvariantCulture) + " (" + Convert.ToDouble(r["recommended_icu_ka"], CultureInfo.InvariantCulture).ToString("0", CultureInfo.InvariantCulture) + "kA)",
                    Convert.ToDouble(r["combined_derating"], CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture),
                    Convert.ToString(r["cable_designation"], CultureInfo.InvariantCulture),
                    Convert.ToDouble(r["derated_ampacity_iz_a"], CultureInfo.InvariantCulture).ToString("0.0", CultureInfo.InvariantCulture),
                    Convert.ToDouble(r["length_m"], CultureInfo.InvariantCulture).ToString("0.#", CultureInfo.InvariantCulture),
                    Convert.ToDouble(r["voltage_drop_percent"], CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture) + "% (max " + Convert.ToDouble(r["max_vd_percent"], CultureInfo.InvariantCulture).ToString("0.#", CultureInfo.InvariantCulture) + "%)"
                };
                if (hasAnySc)
                {
                    row.Add(r.ContainsKey("load_isc_ka")
                        ? (Convert.ToDouble(r["load_isc_ka"], CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture) + " / Src " + Convert.ToDouble(r["source_isc_ka"], CultureInfo.InvariantCulture).ToString("0.0", CultureInfo.InvariantCulture))
                        : "-");
                }
                row.Add("PASS");
                xl.Rows.Add(row);
            }
            string totSummary = Math.Round(totKw, 2).ToString("0.##", CultureInfo.InvariantCulture) + " kW / " + Math.Round(totKva, 2).ToString("0.##", CultureInfo.InvariantCulture) + " kVA";
            xl.FooterRow = hasAnySc
                ? new List<string> { "الإجمالي (" + results.Count + ")", totSummary, "", "", "", "", "", "", "", "", "", "PASS" }
                : new List<string> { "الإجمالي (" + results.Count + ")", totSummary, "", "", "", "", "", "", "", "", "PASS" };

            Dictionary<string, object> resp = new Dictionary<string, object>
            {
                { "standard", "IEC 60364 / ECP (Deterministic)" },
                { "circuits_count", results.Count },
                { "total_load_kw", Math.Round(totKw, 2) },
                { "total_load_kva", Math.Round(totKva, 2) },
                { "results", results }
            };
            if (GetBool(args, "export_excel", false))
            {
                string docPath = doc != null ? (string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName) : "Revit";
                string reqPath = GetStr(args, "excel_path");
                string target = !string.IsNullOrWhiteSpace(reqPath) ? reqPath : RevitExcelExporter.ResolveDefaultPath(docPath, "revit_lv_calculations", ".xlsx");
                resp["excel_exported"] = true;
                resp["excel_path"] = RevitExcelExporter.WriteXlsx(target, xl);
            }
            return Ok(resp);
        }
    }
}
