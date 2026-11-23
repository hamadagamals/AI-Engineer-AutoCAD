using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        private static readonly double[] LvStandardCableSizesMm2 = new double[]
        {
            1.5, 2.5, 4.0, 6.0, 10.0, 16.0, 25.0, 35.0, 50.0,
            70.0, 95.0, 120.0, 150.0, 185.0, 240.0, 300.0, 400.0
        };

        private static readonly double[] LvStandardBreakersA = new double[]
        {
            6, 10, 16, 20, 25, 32, 40, 50, 63, 80, 100, 125,
            160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3200
        };

        private static readonly double[] LvStandardIcuKa = new double[]
        {
            6.0, 10.0, 15.0, 25.0, 36.0, 50.0, 65.0, 85.0, 100.0
        };

        // Base ampacities (A) at 30C ambient (air) / 20C (ground), IEC 60364-5-52 / ECP reference values
        // Copper XLPE 90C, 3 loaded conductors
        private static readonly double[] CuXlpeTray3P = new double[]
        {
            23, 31, 42, 54, 75, 100, 127, 158, 192,
            246, 298, 346, 399, 456, 538, 621, 720
        };
        private static readonly double[] CuXlpeConduit3P = new double[]
        {
            19.5, 26, 35, 45, 62, 83, 108, 134, 162,
            207, 251, 290, 332, 378, 444, 510, 585
        };
        private static readonly double[] CuXlpeBuried3P = new double[]
        {
            26, 34, 44, 56, 75, 98, 128, 157, 185,
            228, 275, 313, 353, 399, 464, 524, 600
        };

        // Copper PVC 70C, 3 loaded conductors
        private static readonly double[] CuPvcTray3P = new double[]
        {
            18.5, 25, 34, 43, 60, 80, 101, 126, 153,
            196, 238, 276, 319, 364, 430, 497, 575
        };
        private static readonly double[] CuPvcConduit3P = new double[]
        {
            15.5, 21, 28, 36, 50, 68, 89, 110, 134,
            171, 207, 239, 275, 314, 370, 426, 490
        };
        private static readonly double[] CuPvcBuried3P = new double[]
        {
            22, 29, 37, 46, 61, 79, 101, 122, 144,
            178, 211, 240, 271, 304, 351, 396, 450
        };

        // AC Resistance R at 20C (Ohm/km) for Copper; Aluminum scaled by 28.2 / 17.241
        private static readonly double[] CuR20OhmPerKm = new double[]
        {
            12.10, 7.41, 4.61, 3.08, 1.83, 1.15, 0.727, 0.524, 0.387,
            0.268, 0.193, 0.153, 0.124, 0.0991, 0.0754, 0.0601, 0.0470
        };

        // Typical 50Hz Reactance X (Ohm/km) for LV multicore cables
        private static readonly double[] LvReactanceOhmPerKm = new double[]
        {
            0.115, 0.108, 0.100, 0.095, 0.090, 0.086, 0.083, 0.082, 0.080,
            0.079, 0.078, 0.077, 0.077, 0.076, 0.076, 0.075, 0.075
        };

        private sealed class LvCircuitCalcResult
        {
            public string CircuitName;
            public string LoadType;
            public int Phases;
            public double VoltageV;
            public double LoadKw;
            public double LoadKva;
            public double PowerFactor;
            public double LengthM;
            public double DesignCurrentA;
            public double RequiredCurrentA;
            public double BreakerRatingA;
            public string BreakerType;
            public double RecommendedIcuKa;
            public string Material;
            public string Insulation;
            public string InstallMethod;
            public double AmbientTempC;
            public int GroupedCircuits;
            public double TempFactorCa;
            public double GroupFactorCg;
            public double CombinedDerating;
            public int ParallelRuns;
            public double CableSizeMm2;
            public double PeSizeMm2;
            public string CableDesignation;
            public double BaseAmpacityPerRunA;
            public double DeratedAmpacityTotalA;
            public double VoltageDropV;
            public double VoltageDropPercent;
            public double MaxAllowedVdPercent;
            public bool UpsizedForVd;
            public bool UpsizedForSc;
            public bool HasShortCircuit;
            public double SourceIscKa;
            public double LoadIscKa;
            public double MinAdiabaticCableMm2;
            public string Status;
            public List<string> Notes = new List<string>();
        }

        private static double GetAmbientTempFactor(double tempC, string insulation, string installMethod)
        {
            bool isXlpe = string.Equals(insulation, "XLPE", StringComparison.OrdinalIgnoreCase);
            string meth = (installMethod ?? "tray").Trim().ToLowerInvariant();
            bool isBuried = meth == "buried" || meth == "ground" || meth == "underground";
            double maxTemp = isXlpe ? 90.0 : 70.0;
            if (tempC >= maxTemp)
                throw new InvalidOperationException("Ambient temperature (" + tempC.ToString("0.#", CultureInfo.InvariantCulture) + "C) meets or exceeds " + insulation + " maximum conductor temperature (" + maxTemp.ToString("0", CultureInfo.InvariantCulture) + "C).");
            double refTemp = isBuried ? 20.0 : 30.0;
            double clamped = Math.Max(10.0, tempC);
            double ratio = (maxTemp - clamped) / (maxTemp - refTemp);
            if (ratio <= 0.0) return 0.35;
            double factor = Math.Sqrt(ratio);
            return Math.Round(Math.Max(0.35, Math.Min(1.22, factor)), 3);
        }

        private static double GetGroupingFactor(int circuits)
        {
            if (circuits <= 1) return 1.00;
            if (circuits == 2) return 0.80;
            if (circuits == 3) return 0.70;
            if (circuits == 4) return 0.65;
            if (circuits == 5) return 0.60;
            if (circuits == 6) return 0.57;
            if (circuits == 7) return 0.54;
            if (circuits == 8) return 0.52;
            if (circuits <= 10) return 0.48;
            if (circuits <= 12) return 0.45;
            return 0.40;
        }

        private static double GetBaseAmpacity(int sizeIdx, string material, string insulation, string installMethod, int phases)
        {
            bool isXlpe = string.Equals(insulation, "XLPE", StringComparison.OrdinalIgnoreCase);
            bool isAl = string.Equals(material, "AL", StringComparison.OrdinalIgnoreCase);
            string meth = (installMethod ?? "tray").Trim().ToLowerInvariant();

            double[] table;
            if (isXlpe)
            {
                if (meth == "conduit" || meth == "trunking" || meth == "embedded") table = CuXlpeConduit3P;
                else if (meth == "buried" || meth == "ground" || meth == "underground") table = CuXlpeBuried3P;
                else table = CuXlpeTray3P;
            }
            else
            {
                if (meth == "conduit" || meth == "trunking" || meth == "embedded") table = CuPvcConduit3P;
                else if (meth == "buried" || meth == "ground" || meth == "underground") table = CuPvcBuried3P;
                else table = CuPvcTray3P;
            }

            double iz = table[sizeIdx];
            if (phases == 1) iz *= 1.12; // 2 loaded conductors carry ~12% more than 3 loaded conductors
            if (meth == "free_air" || meth == "cleats") iz *= 1.05;
            if (isAl) iz *= 0.78;
            return Math.Round(iz, 1);
        }

        private static double SelectStandardBreaker(double requiredAmps)
        {
            for (int i = 0; i < LvStandardBreakersA.Length; i++)
            {
                if (LvStandardBreakersA[i] >= requiredAmps - 1e-6)
                    return LvStandardBreakersA[i];
            }
            return Math.Ceiling(requiredAmps / 100.0) * 100.0;
        }

        private static string ClassifyBreakerType(double inAmps)
        {
            if (inAmps <= 63.0) return "MCB";
            if (inAmps <= 1600.0) return "MCCB";
            return "ACB";
        }

        private static double SelectStandardIcu(double faultKa)
        {
            for (int i = 0; i < LvStandardIcuKa.Length; i++)
            {
                if (LvStandardIcuKa[i] >= faultKa - 1e-6)
                    return LvStandardIcuKa[i];
            }
            return 100.0;
        }

        private static double SelectPeConductorMm2(double phaseMm2)
        {
            return SelectPeConductorMm2(phaseMm2, 0.0, 1);
        }

        private static double SelectPeConductorMm2(double phaseMm2, double minTotalAdiabaticMm2, int runs)
        {
            double tablePe;
            if (phaseMm2 <= 16.0) tablePe = phaseMm2;
            else if (phaseMm2 <= 35.0) tablePe = 16.0;
            else
            {
                double half = phaseMm2 * 0.5;
                tablePe = 240.0;
                for (int i = 0; i < LvStandardCableSizesMm2.Length; i++)
                {
                    if (LvStandardCableSizesMm2[i] >= half - 1e-6)
                    {
                        tablePe = LvStandardCableSizesMm2[i];
                        break;
                    }
                }
            }
            double perRunAdiabatic = minTotalAdiabaticMm2 > 0.0 ? (minTotalAdiabaticMm2 / Math.Max(1, runs)) : 0.0;
            double reqPe = Math.Max(tablePe, perRunAdiabatic);
            for (int i = 0; i < LvStandardCableSizesMm2.Length; i++)
            {
                if (LvStandardCableSizesMm2[i] >= reqPe - 1e-6)
                    return LvStandardCableSizesMm2[i];
            }
            return 240.0;
        }

        private static double GetOperatingResistanceOhmPerKm(int sizeIdx, string material, string insulation)
        {
            bool isAl = string.Equals(material, "AL", StringComparison.OrdinalIgnoreCase);
            bool isXlpe = string.Equals(insulation, "XLPE", StringComparison.OrdinalIgnoreCase);
            double r20 = CuR20OhmPerKm[sizeIdx] * (isAl ? (28.2 / 17.241) : 1.0);
            double alpha = isAl ? 0.00403 : 0.00393;
            double opTemp = isXlpe ? 90.0 : 70.0;
            return r20 * (1.0 + alpha * (opTemp - 20.0));
        }

        private static LvCircuitCalcResult ComputeSingleLvCircuit(Dictionary<string, object> item, Dictionary<string, object> defaults, int fallbackIndex)
        {
            string name = GetStr(item, "name") ?? GetStr(item, "circuit") ?? GetStr(item, "circuit_name") ?? ("C-" + fallbackIndex.ToString(CultureInfo.InvariantCulture));
            string loadType = (GetStr(item, "load_type") ?? GetStr(defaults, "load_type") ?? "power").Trim().ToLowerInvariant();

            int phases = GetInt(item, "phases", GetInt(defaults, "phases", 3));
            if (phases != 1 && phases != 3) phases = 3;

            double defaultV = (phases == 1) ? 220.0 : 380.0;
            double voltageV = GetDouble(item, "voltage_v", GetDouble(defaults, "voltage_v", defaultV));
            if (voltageV <= 10.0) voltageV = defaultV;

            double defaultPf = (loadType == "lighting" || loadType == "إنارة") ? 0.90 : 0.85;
            double pf = GetDouble(item, "power_factor", GetDouble(defaults, "power_factor", defaultPf));
            pf = Math.Max(0.4, Math.Min(1.0, pf));

            double eff = GetDouble(item, "efficiency", GetDouble(defaults, "efficiency", 1.0));
            eff = Math.Max(0.4, Math.Min(1.0, eff));

            double demandFactor = GetDouble(item, "demand_factor", GetDouble(defaults, "demand_factor", 1.0));
            demandFactor = Math.Max(0.1, Math.Min(1.5, demandFactor));

            double safetyFactor = GetDouble(item, "safety_factor", GetDouble(defaults, "safety_factor", (loadType == "motor" || loadType == "continuous") ? 1.25 : 1.0));
            safetyFactor = Math.Max(1.0, Math.Min(2.0, safetyFactor));

            double? loadKwOpt = GetNullableDouble(item, "load_kw");
            double? loadKvaOpt = GetNullableDouble(item, "load_kva");
            double? loadHpOpt = GetNullableDouble(item, "load_hp");
            double? loadAmpsOpt = GetNullableDouble(item, "load_amps");

            double loadKw = 0.0;
            double loadKva = 0.0;
            double ib = 0.0;

            if (loadAmpsOpt.HasValue && loadAmpsOpt.Value > 0)
            {
                ib = loadAmpsOpt.Value * demandFactor;
                loadKva = (phases == 3)
                    ? (Math.Sqrt(3.0) * voltageV * ib / 1000.0)
                    : (voltageV * ib / 1000.0);
                loadKw = loadKva * pf;
            }
            else if (loadKwOpt.HasValue && loadKwOpt.Value > 0)
            {
                loadKw = loadKwOpt.Value * demandFactor;
                loadKva = loadKw / (pf * eff);
                ib = (phases == 3)
                    ? (loadKva * 1000.0) / (Math.Sqrt(3.0) * voltageV)
                    : (loadKva * 1000.0) / voltageV;
            }
            else if (loadKvaOpt.HasValue && loadKvaOpt.Value > 0)
            {
                loadKva = loadKvaOpt.Value * demandFactor;
                loadKw = loadKva * pf;
                ib = (phases == 3)
                    ? (loadKva * 1000.0) / (Math.Sqrt(3.0) * voltageV)
                    : (loadKva * 1000.0) / voltageV;
            }
            else if (loadHpOpt.HasValue && loadHpOpt.Value > 0)
            {
                bool hasExplicitEff = GetNullableDouble(item, "efficiency").HasValue || GetNullableDouble(defaults, "efficiency").HasValue;
                double motorEff = hasExplicitEff ? eff : 0.88;
                loadKw = loadHpOpt.Value * 0.746 * demandFactor;
                loadKva = loadKw / (pf * motorEff);
                ib = (phases == 3)
                    ? (loadKva * 1000.0) / (Math.Sqrt(3.0) * voltageV)
                    : (loadKva * 1000.0) / voltageV;
            }
            else
            {
                throw new InvalidOperationException("Circuit '" + name + "' requires one positive load input: load_kw, load_kva, load_hp, or load_amps.");
            }

            double lengthM = Math.Max(0.0, GetDouble(item, "length_m", GetDouble(defaults, "length_m", 25.0)));

            string materialRaw = (GetStr(item, "material") ?? GetStr(defaults, "material") ?? "Cu").Trim().ToUpperInvariant();
            string material = (materialRaw == "AL" || materialRaw == "ALUMINUM" || materialRaw == "ALUMINIUM" || materialRaw == "ألومنيوم") ? "Al" : "Cu";

            string insRaw = (GetStr(item, "insulation") ?? GetStr(defaults, "insulation") ?? "XLPE").Trim().ToUpperInvariant();
            string insulation = (insRaw == "PVC") ? "PVC" : "XLPE";

            string installMethod = (GetStr(item, "install_method") ?? GetStr(defaults, "install_method") ?? "tray").Trim().ToLowerInvariant();
            double ambientC = GetDouble(item, "ambient_temp_c", GetDouble(defaults, "ambient_temp_c", 40.0));
            int grouped = Math.Max(1, GetInt(item, "grouped_circuits", GetInt(defaults, "grouped_circuits", 1)));

            double? customDerating = GetNullableDouble(item, "derating_factor") ?? GetNullableDouble(defaults, "derating_factor");
            if (customDerating.HasValue && (customDerating.Value < 0.05 || customDerating.Value > 1.50))
                throw new InvalidOperationException("Explicit derating_factor must be between 0.05 and 1.50.");

            double ca;
            try { ca = GetAmbientTempFactor(ambientC, insulation, installMethod); }
            catch
            {
                if (!customDerating.HasValue) throw;
                ca = 1.0;
            }
            double cg = GetGroupingFactor(grouped);
            double combinedDerating = customDerating.HasValue
                ? customDerating.Value
                : Math.Round(ca * cg, 3);

            double defaultMaxVd = (loadType == "lighting" || loadType == "إنارة") ? 3.0 : 5.0;
            double maxVdPct = Math.Max(0.5, Math.Min(15.0, GetDouble(item, "max_vd_percent", GetDouble(defaults, "max_vd_percent", defaultMaxVd))));

            double requiredCurrent = ib * safetyFactor;
            double? explicitBreaker = GetNullableDouble(item, "breaker_a");
            if (explicitBreaker.HasValue && explicitBreaker.Value < requiredCurrent - 1e-6)
                throw new InvalidOperationException("Explicit breaker_a (" + explicitBreaker.Value.ToString(CultureInfo.InvariantCulture) + " A) is less than required current Ib * safety_factor (" + Math.Round(requiredCurrent, 2).ToString(CultureInfo.InvariantCulture) + " A).");
            double breakerA = explicitBreaker.HasValue
                ? explicitBreaker.Value
                : SelectStandardBreaker(requiredCurrent);

            // Minimum cable size rules (ECP / IEC practical minimums: 1.5mm2 lighting, 2.5mm2 sockets/power, Al >= 16mm2)
            double minSizeMm2 = 1.5;
            if (loadType == "socket" || loadType == "sockets" || loadType == "power" || loadType == "motor" || loadType == "feeder")
                minSizeMm2 = 2.5;
            if (material == "Al" && minSizeMm2 < 16.0)
                minSizeMm2 = 16.0;

            int minIdx = 0;
            for (int i = 0; i < LvStandardCableSizesMm2.Length; i++)
            {
                if (LvStandardCableSizesMm2[i] >= minSizeMm2 - 1e-6) { minIdx = i; break; }
            }

            // Optional short-circuit parameters
            double? sourceIscOpt = GetNullableDouble(item, "source_isc_ka") ?? GetNullableDouble(defaults, "source_isc_ka");
            double? txKvaOpt = GetNullableDouble(item, "transformer_kva") ?? GetNullableDouble(defaults, "transformer_kva");
            double txUkPct = Math.Max(2.0, Math.Min(15.0, GetDouble(item, "transformer_uk_percent", GetDouble(defaults, "transformer_uk_percent", 6.0))));
            double faultTimeSec = Math.Max(0.01, Math.Min(5.0, GetDouble(item, "fault_time_sec", GetDouble(defaults, "fault_time_sec", 0.1))));

            bool hasSc = false;
            double sourceIscKa = 0.0;
            double rSrcOhm = 0.0;
            double xSrcOhm = 0.0;
            double uLineV = (phases == 3) ? voltageV : (voltageV * Math.Sqrt(3.0));

            if (sourceIscOpt.HasValue && sourceIscOpt.Value > 0)
            {
                hasSc = true;
                sourceIscKa = sourceIscOpt.Value;
                double zSrc = (1.05 * uLineV) / (Math.Sqrt(3.0) * sourceIscKa * 1000.0);
                // Typical LV upstream R/X ratio ~ 0.3
                xSrcOhm = zSrc / Math.Sqrt(1.0 + 0.09);
                rSrcOhm = 0.3 * xSrcOhm;
            }
            else if (txKvaOpt.HasValue && txKvaOpt.Value > 0)
            {
                hasSc = true;
                double zTx = (txUkPct / 100.0) * (uLineV * uLineV) / (txKvaOpt.Value * 1000.0);
                xSrcOhm = zTx * 0.96;
                rSrcOhm = zTx * 0.28;
                sourceIscKa = (1.05 * uLineV) / (Math.Sqrt(3.0) * zTx * 1000.0);
            }

            double kAdiabatic = (material == "Cu")
                ? (insulation == "XLPE" ? 143.0 : 115.0)
                : (insulation == "XLPE" ? 94.0 : 76.0);

            int chosenIdx = -1;
            int chosenRuns = 1;
            bool upsizedForVd = false;
            bool upsizedForSc = false;
            double chosenBaseIz = 0.0;
            double chosenTotalIz = 0.0;
            double chosenVdV = 0.0;
            double chosenVdPct = 0.0;
            double loadIscKa = 0.0;
            double minScMm2 = 0.0;

            double sinPhi = Math.Sqrt(Math.Max(0.0, 1.0 - pf * pf));
            double vdMult = (phases == 3) ? Math.Sqrt(3.0) : 2.0;

            for (int runs = 1; runs <= 8 && chosenIdx < 0; runs++)
            {
                // For multi-run feeders, avoid tiny parallel cables (start at >= 70 mm2 for runs > 1)
                int startIdx = (runs == 1) ? minIdx : Math.Max(minIdx, 9);
                double scLoopMult = (phases == 1) ? 2.0 : 1.0; // Phase + Neutral return loop for 1P faults
                for (int idx = startIdx; idx < LvStandardCableSizesMm2.Length; idx++)
                {
                    double baseIz = GetBaseAmpacity(idx, material, insulation, installMethod, phases);
                    double totalIz = baseIz * combinedDerating * runs;
                    if (totalIz < breakerA - 1e-6) continue;

                    double rOp = GetOperatingResistanceOhmPerKm(idx, material, insulation);
                    double xCable = LvReactanceOhmPerKm[idx];
                    double vdV = vdMult * ib * (lengthM / runs) * (rOp * pf + xCable * sinPhi) / 1000.0;
                    double vdPct = (vdV / voltageV) * 100.0;

                    if (vdPct > maxVdPct + 1e-6)
                    {
                        upsizedForVd = true;
                        continue;
                    }

                    double curLoadIscKa = 0.0;
                    double curMinScMm2 = 0.0;
                    if (hasSc)
                    {
                        double r20 = CuR20OhmPerKm[idx] * (material == "Al" ? (28.2 / 17.241) : 1.0);
                        double rCab = scLoopMult * r20 * (lengthM / 1000.0) / runs;
                        double xCab = scLoopMult * xCable * (lengthM / 1000.0) / runs;
                        double zTot = Math.Sqrt((rSrcOhm + rCab) * (rSrcOhm + rCab) + (xSrcOhm + xCab) * (xSrcOhm + xCab));
                        double uFault = (phases == 3) ? (1.05 * uLineV / Math.Sqrt(3.0)) : (1.05 * voltageV);
                        curLoadIscKa = uFault / (Math.Max(1e-6, zTot) * 1000.0);
                        // Conservative IEC 60364-4-43 adiabatic check against sending-end fault current near panel origin
                        curMinScMm2 = (sourceIscKa * 1000.0 * Math.Sqrt(faultTimeSec)) / kAdiabatic;
                        if (LvStandardCableSizesMm2[idx] * runs < curMinScMm2 - 1e-6)
                        {
                            upsizedForSc = true;
                            continue;
                        }
                    }

                    chosenIdx = idx;
                    chosenRuns = runs;
                    chosenBaseIz = baseIz;
                    chosenTotalIz = totalIz;
                    chosenVdV = vdV;
                    chosenVdPct = vdPct;
                    loadIscKa = curLoadIscKa;
                    minScMm2 = curMinScMm2;
                    break;
                }
            }

            if (chosenIdx < 0)
            {
                throw new InvalidOperationException("Load on '" + name + "' (" + Math.Round(ib, 1).ToString(CultureInfo.InvariantCulture) + " A) exceeds maximum 8x400mm2 cable sizing capacity; consider busway/busbar trunking or split load.");
            }

            double cableMm2 = LvStandardCableSizesMm2[chosenIdx];
            double peMm2 = SelectPeConductorMm2(cableMm2, hasSc ? minScMm2 : 0.0, chosenRuns);
            string corePrefix = (phases == 3) ? "4x" : "2x";
            string runPrefix = (chosenRuns > 1) ? (chosenRuns.ToString(CultureInfo.InvariantCulture) + "x(") : "";
            string runSuffix = (chosenRuns > 1) ? ")" : "";
            string designation = runPrefix + corePrefix + cableMm2.ToString("0.#", CultureInfo.InvariantCulture) + " + " +
                                 peMm2.ToString("0.#", CultureInfo.InvariantCulture) + "mm2 " + material + "/" + insulation + runSuffix;

            double recIcu = hasSc ? SelectStandardIcu(sourceIscKa) : (breakerA <= 63 ? 6.0 : (breakerA <= 250 ? 25.0 : 36.0));

            LvCircuitCalcResult res = new LvCircuitCalcResult();
            res.CircuitName = name;
            res.LoadType = loadType;
            res.Phases = phases;
            res.VoltageV = voltageV;
            res.LoadKw = Math.Round(loadKw, 2);
            res.LoadKva = Math.Round(loadKva, 2);
            res.PowerFactor = pf;
            res.LengthM = Math.Round(lengthM, 1);
            res.DesignCurrentA = Math.Round(ib, 2);
            res.RequiredCurrentA = Math.Round(requiredCurrent, 2);
            res.BreakerRatingA = breakerA;
            res.BreakerType = ClassifyBreakerType(breakerA);
            res.RecommendedIcuKa = recIcu;
            res.Material = material;
            res.Insulation = insulation;
            res.InstallMethod = installMethod;
            res.AmbientTempC = ambientC;
            res.GroupedCircuits = grouped;
            res.TempFactorCa = ca;
            res.GroupFactorCg = cg;
            res.CombinedDerating = combinedDerating;
            res.ParallelRuns = chosenRuns;
            res.CableSizeMm2 = cableMm2;
            res.PeSizeMm2 = peMm2;
            res.CableDesignation = designation;
            res.BaseAmpacityPerRunA = Math.Round(chosenBaseIz, 1);
            res.DeratedAmpacityTotalA = Math.Round(chosenTotalIz, 1);
            res.VoltageDropV = Math.Round(chosenVdV, 2);
            res.VoltageDropPercent = Math.Round(chosenVdPct, 2);
            res.MaxAllowedVdPercent = maxVdPct;
            res.UpsizedForVd = upsizedForVd;
            res.UpsizedForSc = upsizedForSc;
            res.HasShortCircuit = hasSc;
            res.SourceIscKa = Math.Round(sourceIscKa, 2);
            res.LoadIscKa = Math.Round(loadIscKa, 2);
            res.MinAdiabaticCableMm2 = Math.Round(minScMm2, 2);
            res.Status = "PASS";

            if (upsizedForVd)
                res.Notes.Add("Cable upsized to satisfy voltage drop limit (" + maxVdPct.ToString("0.#", CultureInfo.InvariantCulture) + "%).");
            if (upsizedForSc)
                res.Notes.Add("Cable upsized for short-circuit thermal withstand (k*S/sqrt(t)).");
            if (chosenRuns > 1)
                res.Notes.Add("Parallel runs selected (" + chosenRuns.ToString(CultureInfo.InvariantCulture) + "x" + cableMm2.ToString("0.#", CultureInfo.InvariantCulture) + "mm2); ensure identical lengths & phase symmetry.");
            if (loadType == "motor")
                res.Notes.Add("Motor circuit: select Type C/D MCB or MPCB sized for starting inrush (IEC 60947-4-1).");

            return res;
        }

        private static Dictionary<string, object> CalculateLvElectrical(Document doc, Dictionary<string, object> args)
        {
            List<Dictionary<string, object>> inputs = new List<Dictionary<string, object>>();
            object circuitsRaw = args != null && args.ContainsKey("circuits") ? args["circuits"] : null;
            IEnumerable circuitsEnum = circuitsRaw as IEnumerable;
            if (circuitsEnum != null && !(circuitsRaw is string))
            {
                foreach (object o in circuitsEnum)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null) inputs.Add(d);
                }
            }
            if (inputs.Count == 0)
            {
                inputs.Add(args ?? new Dictionary<string, object>());
            }

            List<LvCircuitCalcResult> results = new List<LvCircuitCalcResult>();
            List<object> resultDicts = new List<object>();
            double totalKw = 0.0;
            double totalKva = 0.0;

            for (int i = 0; i < inputs.Count; i++)
            {
                LvCircuitCalcResult r = ComputeSingleLvCircuit(inputs[i], args ?? new Dictionary<string, object>(), i + 1);
                results.Add(r);
                totalKw += r.LoadKw;
                totalKva += r.LoadKva;

                Dictionary<string, object> itemDict = new Dictionary<string, object>
                {
                    { "circuit", r.CircuitName },
                    { "load_type", r.LoadType },
                    { "phases", r.Phases },
                    { "voltage_v", r.VoltageV },
                    { "load_kw", r.LoadKw },
                    { "load_kva", r.LoadKva },
                    { "power_factor", r.PowerFactor },
                    { "length_m", r.LengthM },
                    { "design_current_ib_a", r.DesignCurrentA },
                    { "breaker_in_a", r.BreakerRatingA },
                    { "breaker_type", r.BreakerType },
                    { "recommended_icu_ka", r.RecommendedIcuKa },
                    { "derating_ca", r.TempFactorCa },
                    { "derating_cg", r.GroupFactorCg },
                    { "combined_derating", r.CombinedDerating },
                    { "cable_designation", r.CableDesignation },
                    { "cable_size_mm2", r.CableSizeMm2 },
                    { "parallel_runs", r.ParallelRuns },
                    { "pe_size_mm2", r.PeSizeMm2 },
                    { "base_ampacity_a", r.BaseAmpacityPerRunA },
                    { "derated_ampacity_iz_a", r.DeratedAmpacityTotalA },
                    { "voltage_drop_v", r.VoltageDropV },
                    { "voltage_drop_percent", r.VoltageDropPercent },
                    { "max_vd_percent", r.MaxAllowedVdPercent },
                    { "upsized_for_vd", r.UpsizedForVd },
                    { "upsized_for_sc", r.UpsizedForSc },
                    { "status", r.Status },
                    { "notes", r.Notes }
                };
                if (r.HasShortCircuit)
                {
                    itemDict["source_isc_ka"] = r.SourceIscKa;
                    itemDict["load_isc_ka"] = r.LoadIscKa;
                    itemDict["min_adiabatic_cable_mm2"] = r.MinAdiabaticCableMm2;
                }
                resultDicts.Add(itemDict);
            }

            bool drawTable = GetBool(args, "draw_table", false);
            bool exportExcel = GetBool(args, "export_excel", false);
            bool replaceExisting = GetBool(args, "replace_existing", true);
            bool zoom = GetBool(args, "zoom", true);
            bool hasAnySc = false;
            for (int i = 0; i < results.Count; i++) if (results[i].HasShortCircuit) { hasAnySc = true; break; }

            ExcelSheetData sheet = new ExcelSheetData();
            sheet.Title = "جدول حسابات الجهد المنخفض - الكابلات والقواطع وهبوط الجهد (IEC 60364 / ECP)";
            sheet.SheetName = "LV_Calculations";
            sheet.Headers = hasAnySc
                ? new List<string> { "الدائرة", "الحمل (kW)", "الفاز/الجهد", "التيار Ib (A)", "القاطع In", "معامل التخفيض", "مقطع الكابل + PE", "سعة الكابل Iz (A)", "الطول (m)", "هبوط الجهد %", "قصر الدائرة Isc (kA)", "الحالة" }
                : new List<string> { "الدائرة", "الحمل (kW)", "الفاز/الجهد", "التيار Ib (A)", "القاطع In", "معامل التخفيض", "مقطع الكابل + PE", "سعة الكابل Iz (A)", "الطول (m)", "هبوط الجهد %", "الحالة" };

            for (int i = 0; i < results.Count; i++)
            {
                LvCircuitCalcResult r = results[i];
                List<string> row = new List<string>
                {
                    r.CircuitName,
                    r.LoadKw.ToString("0.##", CultureInfo.InvariantCulture),
                    r.Phases.ToString(CultureInfo.InvariantCulture) + "P / " + r.VoltageV.ToString("0", CultureInfo.InvariantCulture) + "V",
                    r.DesignCurrentA.ToString("0.0", CultureInfo.InvariantCulture),
                    r.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " + r.BreakerType + " (" + r.RecommendedIcuKa.ToString("0", CultureInfo.InvariantCulture) + "kA)",
                    r.CombinedDerating.ToString("0.00", CultureInfo.InvariantCulture),
                    r.CableDesignation,
                    r.DeratedAmpacityTotalA.ToString("0.0", CultureInfo.InvariantCulture),
                    r.LengthM.ToString("0.#", CultureInfo.InvariantCulture),
                    r.VoltageDropPercent.ToString("0.00", CultureInfo.InvariantCulture) + "% (max " + r.MaxAllowedVdPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%)"
                };
                if (hasAnySc)
                    row.Add(r.HasShortCircuit ? r.LoadIscKa.ToString("0.00", CultureInfo.InvariantCulture) + " / Src " + r.SourceIscKa.ToString("0.0", CultureInfo.InvariantCulture) : "-");
                row.Add(r.Status);
                sheet.Rows.Add(row);
            }

            sheet.FooterRow = hasAnySc
                ? new List<string> { "الإجمالي (" + results.Count + ")", Math.Round(totalKw, 2).ToString("0.##", CultureInfo.InvariantCulture) + " kW / " + Math.Round(totalKva, 2).ToString("0.##", CultureInfo.InvariantCulture) + " kVA", "", "", "", "", "", "", "", "", "", "PASS" }
                : new List<string> { "الإجمالي (" + results.Count + ")", Math.Round(totalKw, 2).ToString("0.##", CultureInfo.InvariantCulture) + " kW / " + Math.Round(totalKva, 2).ToString("0.##", CultureInfo.InvariantCulture) + " kVA", "", "", "", "", "", "", "", "", "PASS" };

            Dictionary<string, object> response = new Dictionary<string, object>
            {
                { "standard", "IEC 60364 / ECP (Deterministic)" },
                { "circuits_count", results.Count },
                { "total_load_kw", Math.Round(totalKw, 2) },
                { "total_load_kva", Math.Round(totalKva, 2) },
                { "results", resultDicts }
            };

            if (drawTable && doc != null)
            {
                Database db = doc.Database;
                List<ObjectId> createdIds = new List<ObjectId>();
                List<ObjectId> erasedIds = new List<ObjectId>();
                Extents3d? tableExt = null;

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (!db.TileMode) { try { db.TileMode = true; } catch { } }
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    Extents3d? mainExt = null;
                    foreach (ObjectId oid in ms)
                    {
                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        if (string.Equals(ent.Layer, "_AI_LV_CALC_TABLE", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            Extents3d e = ent.GeometricExtents;
                            if (IsFiniteBounds(e))
                            {
                                if (!mainExt.HasValue) mainExt = e;
                                else { Extents3d cur = mainExt.Value; cur.AddExtents(e); mainExt = cur; }
                            }
                        }
                        catch { }
                    }
                    tableExt = RenderStyledScheduleTable(
                        doc, db, ms, tr, sheet, "_AI_LV_CALC_TABLE",
                        replaceExisting, GetStr(args, "table_position") ?? "outside_right",
                        GetNullableDouble(args, "table_gap"), null, mainExt,
                        createdIds, erasedIds);
                    db.UpdateExt(true);
                    tr.Commit();
                }

                if (createdIds.Count > 0 || erasedIds.Count > 0)
                {
                    CadUndoEntry undo = new CadUndoEntry();
                    undo.ToolName = "calculate_lv_electrical";
                    undo.Summary = "LV Electrical Calculation Table (" + results.Count.ToString(CultureInfo.InvariantCulture) + " circuits)";
                    undo.CreatedIds.AddRange(createdIds);
                    undo.ErasedIds.AddRange(erasedIds);
                    PushUndoEntry(doc, undo);
                }

                if (zoom && tableExt.HasValue && IsFiniteBounds(tableExt.Value))
                    ZoomToExtents3d(doc, tableExt.Value, 1.18);
                else
                    doc.Editor.Regen();
                doc.Editor.UpdateScreen();

                response["table_drawn"] = true;
                response["table_layer"] = "_AI_LV_CALC_TABLE";
                response["created_count"] = createdIds.Count;
            }

            if (exportExcel)
            {
                string xlTarget = GetStr(args, "excel_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "lv_electrical_calculations", ".xlsx");
                response["excel_exported"] = true;
                response["excel_path"] = ExcelTableInterop.WriteSpreadsheet(xlTarget, sheet);
            }

            return Ok(response);
        }
    }
}
