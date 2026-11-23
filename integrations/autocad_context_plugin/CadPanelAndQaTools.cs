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
        // ==================== Phase 2: Panel Schedule + SLD Generator & Electrical QA Auditor ====================

        private sealed class BalancedCircuitWay
        {
            public int WayIndex;
            public string CircuitName;
            public string PanelName;
            public int MatchedBlocksCount;
            public double LengthMeters;
            public double ConnectedVa;
            public int Phases;
            public string PhaseLabel;
            public double RVa;
            public double YVa;
            public double BVa;
            public bool IsSpare;
            public LvCircuitCalcResult Calc;
        }

        private sealed class BalancedPanelSchedule
        {
            public string PanelName;
            public double VoltageLnV;
            public double VoltageLlV;
            public double DemandFactor;
            public List<BalancedCircuitWay> Ways = new List<BalancedCircuitWay>();
            public int ActiveWaysCount;
            public int SpareWaysCount;
            public double TotalRVa;
            public double TotalYVa;
            public double TotalBVa;
            public double TotalConnectedVa;
            public double ImbalancePercent;
            public double ConnectedKva;
            public double DemandKva;
            public double ConnectedKw;
            public double DemandKw;
            public LvCircuitCalcResult MainFeeder;
        }

        private sealed class ElectricalQaIssue
        {
            public string Code;
            public string Severity;
            public string Target;
            public string Handle;
            public double X;
            public double Y;
            public bool HasPoint;
            public string Details;
            public string Recommendation;
        }

        private static string ExtractPanelNameFallback(string circuitName, string rawPanelName)
        {
            if (!string.IsNullOrWhiteSpace(rawPanelName)) return rawPanelName.Trim();
            if (!string.IsNullOrWhiteSpace(circuitName))
            {
                int sep = circuitName.LastIndexOfAny(new char[] { '/', '\\' });
                if (sep > 0) return circuitName.Substring(0, sep).Trim();
            }
            return "LP-MAIN";
        }

        private static int FindNearestCableSizeIndex(double targetMm2)
        {
            for (int i = 0; i < LvStandardCableSizesMm2.Length; i++)
            {
                if (LvStandardCableSizesMm2[i] >= targetMm2 - 1e-6) return i;
            }
            return LvStandardCableSizesMm2.Length - 1;
        }

        private static List<TracedCircuitSummary> ParseExplicitPanelCircuitsArg(
            object rawCircuitsObj,
            double defaultFixtureVa)
        {
            List<TracedCircuitSummary> list = new List<TracedCircuitSummary>();
            IEnumerable en = rawCircuitsObj as IEnumerable;
            if (en == null || rawCircuitsObj is string) return list;

            int seq = 0;
            foreach (object obj in en)
            {
                Dictionary<string, object> d = obj as Dictionary<string, object>;
                if (d == null) continue;
                seq++;
                string cName = GetStr(d, "circuit_name") ?? GetStr(d, "name") ?? GetStr(d, "circuit") ?? ("C-" + seq.ToString(CultureInfo.InvariantCulture));
                string pName = ExtractPanelNameFallback(cName, GetStr(d, "panel_name") ?? GetStr(d, "panel"));
                double lenM = Math.Max(1.0, GetDouble(d, "length_m", GetDouble(d, "total_path_length_m", 20.0)));
                int blkCount = GetInt(d, "blocks_count", GetInt(d, "matched_blocks_count", 0));

                double? vaOpt = GetNullableDouble(d, "load_va") ?? GetNullableDouble(d, "estimated_va");
                double? kvaOpt = GetNullableDouble(d, "load_kva");
                double? kwOpt = GetNullableDouble(d, "load_kw");
                double pf = Math.Max(0.4, Math.Min(1.0, GetDouble(d, "power_factor", 0.90)));
                double va = vaOpt.HasValue && vaOpt.Value > 0
                    ? vaOpt.Value
                    : (kvaOpt.HasValue && kvaOpt.Value > 0
                        ? kvaOpt.Value * 1000.0
                        : (kwOpt.HasValue && kwOpt.Value > 0
                            ? (kwOpt.Value / pf) * 1000.0
                            : Math.Max(1, blkCount) * Math.Max(10.0, defaultFixtureVa)));

                int phases = GetInt(d, "phases", 1);
                if (phases != 1 && phases != 3) phases = 1;

                TracedCircuitSummary summary = new TracedCircuitSummary
                {
                    CircuitName = cName,
                    PanelName = pName,
                    TerminationType = "panel_connection",
                    WireLayer = GetStr(d, "load_type") ?? (phases == 3 ? "3P-POWER" : "LIGHTING"),
                    WireColorKey = phases == 3 ? "ACI:4" : "ACI:6",
                    PathCurvesCount = 1,
                    LengthUnits = lenM,
                    LengthMeters = lenM,
                    MatchedBlocksCount = blkCount > 0 ? blkCount : Math.Max(1, (int)Math.Round(va / Math.Max(10.0, defaultFixtureVa))),
                    CountsByBlock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    CountsSummary = va.ToString("0.#", CultureInfo.InvariantCulture) + " VA"
                };
                summary.CountsByBlock["__VA__"] = (int)Math.Round(va);
                summary.CountsByBlock["__PHASES__"] = phases;
                list.Add(summary);
            }
            return list;
        }

        private static BalancedPanelSchedule BuildBalancedPanelSchedule(
            Document doc,
            string panelName,
            List<TracedCircuitSummary> rawCircuits,
            double fixtureVa,
            double voltageLnV,
            double voltageLlV,
            double powerFactor,
            double demandFactor,
            double sparePercent,
            double feederLengthM,
            double maxVdPercent,
            string material,
            string insulation,
            string installMethod,
            double ambientTempC)
        {
            BalancedPanelSchedule panel = new BalancedPanelSchedule();
            panel.PanelName = panelName;
            panel.VoltageLnV = voltageLnV;
            panel.VoltageLlV = voltageLlV;
            panel.DemandFactor = demandFactor;

            Dictionary<string, object> baseDefaults = new Dictionary<string, object>
            {
                { "power_factor", powerFactor },
                { "material", material },
                { "insulation", insulation },
                { "install_method", installMethod },
                { "ambient_temp_c", ambientTempC }
            };

            List<BalancedCircuitWay> sizedWays = new List<BalancedCircuitWay>();
            for (int i = 0; i < rawCircuits.Count; i++)
            {
                TracedCircuitSummary c = rawCircuits[i];
                int circPhases = 1;
                double estVa = Math.Max(40.0, c.MatchedBlocksCount * Math.Max(10.0, fixtureVa));
                bool useExactKw = false;
                double exactKw = 0.0;
                if (c.CountsByBlock != null && c.CountsByBlock.ContainsKey("__VA__"))
                {
                    estVa = Math.Max(40.0, c.CountsByBlock["__VA__"]);
                }
                else if (doc != null && c.CountsByBlock != null && c.CountsByBlock.Count > 0)
                {
                    double dbKw, dbKva;
                    bool dbAssumed;
                    string dbSrc;
                    Dictionary<string, object> dbRows, dbBrk;
                    List<string> dbMiss;
                    ComputeCircuitLoadFromProjectDb(
                        doc, c.CountsByBlock, null, null, fixtureVa, powerFactor,
                        out dbKw, out dbKva, out dbAssumed, out dbSrc, out dbRows, out dbMiss, out dbBrk);
                    if (dbSrc == "project_legend_db" || dbSrc == "partial_legend_db")
                    {
                        if (dbKva > 1e-6) estVa = Math.Max(10.0, Math.Round(dbKva * 1000.0, 1));
                        if (dbKw > 1e-6) { exactKw = dbKw; useExactKw = true; }
                    }
                }
                if (c.CountsByBlock != null && c.CountsByBlock.ContainsKey("__PHASES__"))
                    circPhases = c.CountsByBlock["__PHASES__"] == 3 ? 3 : 1;

                string loadType = circPhases == 3 ? "power" : "lighting";
                double circV = circPhases == 3 ? voltageLlV : voltageLnV;
                double circMaxVd = circPhases == 3 ? Math.Max(maxVdPercent, 5.0) : maxVdPercent;
                double circLen = Math.Max(5.0, c.LengthMeters);
                string cName = !string.IsNullOrWhiteSpace(c.CircuitName)
                    ? c.CircuitName
                    : (panelName + "/" + (i + 1).ToString(CultureInfo.InvariantCulture));

                Dictionary<string, object> itemArgs = new Dictionary<string, object>
                {
                    { "name", cName },
                    { "load_type", loadType },
                    { "phases", circPhases },
                    { "voltage_v", circV },
                    { "power_factor", powerFactor },
                    { "length_m", circLen },
                    { "max_vd_percent", circMaxVd },
                    { "material", material },
                    { "insulation", insulation },
                    { "install_method", installMethod },
                    { "ambient_temp_c", ambientTempC }
                };
                if (useExactKw)
                    itemArgs["load_kw"] = exactKw;
                else
                    itemArgs["load_kva"] = estVa / 1000.0;

                LvCircuitCalcResult calc = ComputeSingleLvCircuit(itemArgs, baseDefaults, i + 1);
                sizedWays.Add(new BalancedCircuitWay
                {
                    WayIndex = i + 1,
                    CircuitName = cName,
                    PanelName = panelName,
                    MatchedBlocksCount = c.MatchedBlocksCount,
                    LengthMeters = Math.Round(c.LengthMeters, 2),
                    ConnectedVa = Math.Round(estVa, 1),
                    Phases = circPhases,
                    PhaseLabel = circPhases == 3 ? "RYB" : "R",
                    IsSpare = false,
                    Calc = calc
                });
            }

            // LPT greedy 3-phase balancing (R, Y, B)
            double sumR = 0.0;
            double sumY = 0.0;
            double sumB = 0.0;
            List<BalancedCircuitWay> singlePhaseWays = new List<BalancedCircuitWay>();
            for (int i = 0; i < sizedWays.Count; i++)
            {
                BalancedCircuitWay w = sizedWays[i];
                if (w.Phases == 3)
                {
                    double perPhase = Math.Round(w.ConnectedVa / 3.0, 1);
                    w.PhaseLabel = "RYB";
                    w.RVa = perPhase;
                    w.YVa = perPhase;
                    w.BVa = perPhase;
                    sumR += w.ConnectedVa / 3.0;
                    sumY += w.ConnectedVa / 3.0;
                    sumB += w.ConnectedVa / 3.0;
                }
                else
                {
                    singlePhaseWays.Add(w);
                }
            }

            singlePhaseWays.Sort(delegate(BalancedCircuitWay a, BalancedCircuitWay b)
            {
                int c = b.ConnectedVa.CompareTo(a.ConnectedVa);
                return c != 0 ? c : a.WayIndex.CompareTo(b.WayIndex);
            });

            for (int i = 0; i < singlePhaseWays.Count; i++)
            {
                BalancedCircuitWay w = singlePhaseWays[i];
                if (sumR <= sumY && sumR <= sumB)
                {
                    w.PhaseLabel = "R";
                    w.RVa = w.ConnectedVa;
                    sumR += w.ConnectedVa;
                }
                else if (sumY <= sumB)
                {
                    w.PhaseLabel = "Y";
                    w.YVa = w.ConnectedVa;
                    sumY += w.ConnectedVa;
                }
                else
                {
                    w.PhaseLabel = "B";
                    w.BVa = w.ConnectedVa;
                    sumB += w.ConnectedVa;
                }
            }

            sizedWays.Sort(delegate(BalancedCircuitWay a, BalancedCircuitWay b)
            {
                return a.WayIndex.CompareTo(b.WayIndex);
            });

            panel.ActiveWaysCount = sizedWays.Count;
            panel.Ways.AddRange(sizedWays);

            int rawSpares = sparePercent > 1e-3
                ? Math.Max(1, (int)Math.Ceiling(sizedWays.Count * (sparePercent / 100.0)))
                : 0;
            if (rawSpares > 0 && (sizedWays.Count + rawSpares) >= 3)
            {
                int rem = (sizedWays.Count + rawSpares) % 3;
                if (rem != 0) rawSpares += (3 - rem);
            }
            panel.SpareWaysCount = rawSpares;
            string[] cyclePhases = new string[] { "R", "Y", "B" };
            for (int s = 0; s < rawSpares; s++)
            {
                int idx = sizedWays.Count + s + 1;
                panel.Ways.Add(new BalancedCircuitWay
                {
                    WayIndex = idx,
                    CircuitName = "SPARE-" + (s + 1).ToString(CultureInfo.InvariantCulture),
                    PanelName = panelName,
                    MatchedBlocksCount = 0,
                    LengthMeters = 0.0,
                    ConnectedVa = 0.0,
                    Phases = 1,
                    PhaseLabel = cyclePhases[(sizedWays.Count + s) % 3],
                    IsSpare = true,
                    Calc = null
                });
            }

            panel.TotalRVa = Math.Round(sumR, 1);
            panel.TotalYVa = Math.Round(sumY, 1);
            panel.TotalBVa = Math.Round(sumB, 1);
            panel.TotalConnectedVa = Math.Round(sumR + sumY + sumB, 1);
            double avg = (sumR + sumY + sumB) / 3.0;
            double maxP = Math.Max(sumR, Math.Max(sumY, sumB));
            double minP = Math.Min(sumR, Math.Min(sumY, sumB));
            panel.ImbalancePercent = avg > 1e-6 ? Math.Round(((maxP - minP) / avg) * 100.0, 2) : 0.0;
            panel.ConnectedKva = Math.Round(panel.TotalConnectedVa / 1000.0, 3);
            panel.DemandKva = Math.Round(panel.ConnectedKva * demandFactor, 3);
            double sumKwAll = 0.0;
            for (int i = 0; i < sizedWays.Count; i++)
            {
                if (sizedWays[i].Calc != null) sumKwAll += sizedWays[i].Calc.LoadKw;
            }
            panel.ConnectedKw = Math.Round(sumKwAll, 3);
            panel.DemandKw = Math.Round(panel.ConnectedKw * demandFactor, 3);

            Dictionary<string, object> mainArgs = new Dictionary<string, object>
            {
                { "name", panelName + "-MAIN" },
                { "load_type", "feeder" },
                { "phases", 3 },
                { "voltage_v", voltageLlV },
                { "load_kva", Math.Max(0.5, panel.DemandKva) },
                { "power_factor", powerFactor },
                { "length_m", Math.Max(5.0, feederLengthM) },
                { "max_vd_percent", 2.0 },
                { "material", material },
                { "insulation", insulation },
                { "install_method", installMethod },
                { "ambient_temp_c", ambientTempC }
            };
            panel.MainFeeder = ComputeSingleLvCircuit(mainArgs, baseDefaults, 0);
            return panel;
        }

        private static ExcelSheetData BuildPanelScheduleSheet(BalancedPanelSchedule panel, string customTitle)
        {
            ExcelSheetData sheet = new ExcelSheetData();
            sheet.SheetName = panel.PanelName;
            sheet.Title = !string.IsNullOrWhiteSpace(customTitle)
                ? customTitle
                : ("جدول أحمال اللوحة " + panel.PanelName + " وتوزيع الفازات (3P+N+E " +
                   panel.VoltageLlV.ToString("0", CultureInfo.InvariantCulture) + "/" +
                   panel.VoltageLnV.ToString("0", CultureInfo.InvariantCulture) + "V | نسبة عدم الاتزان: " +
                   panel.ImbalancePercent.ToString("0.##", CultureInfo.InvariantCulture) + "%)");
            sheet.Headers = new List<string>
            {
                "م",
                "اللينيا",
                "الفاز",
                "البلوكات",
                "الطول (م)",
                "R (VA)",
                "Y (VA)",
                "B (VA)",
                "التيار Ib (A)",
                "القاطع In",
                "الكابل (IEC)",
                "هبوط الجهد %",
                "الحالة"
            };

            int totalBlocks = 0;
            double totalLen = 0.0;
            for (int i = 0; i < panel.Ways.Count; i++)
            {
                BalancedCircuitWay w = panel.Ways[i];
                if (w.IsSpare)
                {
                    sheet.Rows.Add(new List<string>
                    {
                        w.WayIndex.ToString(CultureInfo.InvariantCulture),
                        w.CircuitName,
                        w.PhaseLabel,
                        "-",
                        "-",
                        "0",
                        "0",
                        "0",
                        "-",
                        "10A MCB",
                        "احتياطي (SPARE)",
                        "-",
                        "SPARE"
                    });
                    continue;
                }

                totalBlocks += w.MatchedBlocksCount;
                totalLen += w.LengthMeters;
                sheet.Rows.Add(new List<string>
                {
                    w.WayIndex.ToString(CultureInfo.InvariantCulture),
                    w.CircuitName,
                    w.PhaseLabel,
                    w.MatchedBlocksCount.ToString(CultureInfo.InvariantCulture),
                    w.LengthMeters.ToString("0.##", CultureInfo.InvariantCulture),
                    w.RVa.ToString("0.#", CultureInfo.InvariantCulture),
                    w.YVa.ToString("0.#", CultureInfo.InvariantCulture),
                    w.BVa.ToString("0.#", CultureInfo.InvariantCulture),
                    w.Calc.DesignCurrentA.ToString("0.00", CultureInfo.InvariantCulture),
                    w.Calc.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " + w.Calc.BreakerType,
                    w.Calc.CableDesignation,
                    w.Calc.VoltageDropPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%",
                    w.Calc.Status
                });
            }

            string mainSummary = panel.MainFeeder.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " +
                                 panel.MainFeeder.BreakerType + " (" +
                                 panel.MainFeeder.RecommendedIcuKa.ToString("0", CultureInfo.InvariantCulture) + "kA) | " +
                                 panel.MainFeeder.CableDesignation;
            sheet.FooterRow = new List<string>
            {
                "الإجمالي",
                panel.PanelName + " (Conn " + panel.ConnectedKva.ToString("0.##", CultureInfo.InvariantCulture) + " / Dem " + panel.DemandKva.ToString("0.##", CultureInfo.InvariantCulture) + " kVA)",
                "Imb " + panel.ImbalancePercent.ToString("0.##", CultureInfo.InvariantCulture) + "%",
                totalBlocks.ToString(CultureInfo.InvariantCulture),
                Math.Round(totalLen, 1).ToString("0.#", CultureInfo.InvariantCulture),
                panel.TotalRVa.ToString("0.#", CultureInfo.InvariantCulture),
                panel.TotalYVa.ToString("0.#", CultureInfo.InvariantCulture),
                panel.TotalBVa.ToString("0.#", CultureInfo.InvariantCulture),
                panel.MainFeeder.DesignCurrentA.ToString("0.0", CultureInfo.InvariantCulture) + "A",
                mainSummary,
                "DF " + panel.DemandFactor.ToString("0.##", CultureInfo.InvariantCulture),
                panel.MainFeeder.VoltageDropPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%",
                "PASS"
            };
            return sheet;
        }

        private static ExcelSheetData BuildCombinedPanelsWorkbookSheet(List<BalancedPanelSchedule> panels, string customTitle)
        {
            if (panels.Count == 1) return BuildPanelScheduleSheet(panels[0], customTitle);

            ExcelSheetData sheet = new ExcelSheetData();
            sheet.SheetName = "Panel_Schedules";
            sheet.Title = !string.IsNullOrWhiteSpace(customTitle)
                ? customTitle
                : ("جداول أحمال اللوحات الكهربائية (" + panels.Count.ToString(CultureInfo.InvariantCulture) + " لوحات)");
            sheet.Headers = new List<string>
            {
                "اللوحة",
                "اللينيا",
                "الفاز",
                "البلوكات",
                "الطول (م)",
                "R (VA)",
                "Y (VA)",
                "B (VA)",
                "التيار Ib (A)",
                "القاطع In",
                "الكابل (IEC)",
                "هبوط الجهد %",
                "الحالة"
            };

            double allR = 0.0, allY = 0.0, allB = 0.0, allDem = 0.0;
            for (int pi = 0; pi < panels.Count; pi++)
            {
                BalancedPanelSchedule p = panels[pi];
                allR += p.TotalRVa;
                allY += p.TotalYVa;
                allB += p.TotalBVa;
                allDem += p.DemandKva;
                for (int wi = 0; wi < p.Ways.Count; wi++)
                {
                    BalancedCircuitWay w = p.Ways[wi];
                    if (w.IsSpare)
                    {
                        sheet.Rows.Add(new List<string>
                        {
                            p.PanelName, w.CircuitName, w.PhaseLabel, "-", "-", "0", "0", "0", "-", "10A MCB", "SPARE", "-", "SPARE"
                        });
                    }
                    else
                    {
                        sheet.Rows.Add(new List<string>
                        {
                            p.PanelName,
                            w.CircuitName,
                            w.PhaseLabel,
                            w.MatchedBlocksCount.ToString(CultureInfo.InvariantCulture),
                            w.LengthMeters.ToString("0.##", CultureInfo.InvariantCulture),
                            w.RVa.ToString("0.#", CultureInfo.InvariantCulture),
                            w.YVa.ToString("0.#", CultureInfo.InvariantCulture),
                            w.BVa.ToString("0.#", CultureInfo.InvariantCulture),
                            w.Calc.DesignCurrentA.ToString("0.00", CultureInfo.InvariantCulture),
                            w.Calc.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " + w.Calc.BreakerType,
                            w.Calc.CableDesignation,
                            w.Calc.VoltageDropPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%",
                            w.Calc.Status
                        });
                    }
                }
            }

            sheet.FooterRow = new List<string>
            {
                "الإجمالي (" + panels.Count.ToString(CultureInfo.InvariantCulture) + ")",
                "Demand " + Math.Round(allDem, 2).ToString("0.##", CultureInfo.InvariantCulture) + " kVA",
                "3P",
                "",
                "",
                Math.Round(allR, 1).ToString("0.#", CultureInfo.InvariantCulture),
                Math.Round(allY, 1).ToString("0.#", CultureInfo.InvariantCulture),
                Math.Round(allB, 1).ToString("0.#", CultureInfo.InvariantCulture),
                "",
                "",
                "",
                "",
                "PASS"
            };
            return sheet;
        }

        private static void AddSldLine(
            BlockTableRecord ms,
            Transaction tr,
            string layer,
            short colorIndex,
            double x1,
            double y1,
            double x2,
            double y2,
            double width,
            List<ObjectId> createdIds)
        {
            Polyline pl = new Polyline(2);
            pl.SetDatabaseDefaults(ms.Database);
            pl.Layer = layer;
            pl.ColorIndex = colorIndex;
            pl.AddVertexAt(0, new Point2d(x1, y1), 0.0, width, width);
            pl.AddVertexAt(1, new Point2d(x2, y2), 0.0, width, width);
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            createdIds.Add(pl.ObjectId);
        }

        private static void AddSldBreakerSymbol(
            BlockTableRecord ms,
            Transaction tr,
            string layer,
            short colorIndex,
            double cx,
            double topY,
            double botY,
            double halfW,
            List<ObjectId> createdIds)
        {
            double midY1 = topY - (topY - botY) * 0.25;
            double midY2 = botY + (topY - botY) * 0.25;
            AddSldLine(ms, tr, layer, colorIndex, cx, topY, cx, midY1, 0.0, createdIds);
            AddSldLine(ms, tr, layer, colorIndex, cx, midY1, cx - halfW, (midY1 + midY2) * 0.5, 0.0, createdIds);
            AddSldLine(ms, tr, layer, colorIndex, cx, midY2, cx, botY, 0.0, createdIds);

            Circle c1 = new Circle(new Point3d(cx, midY1, 0.0), Vector3d.ZAxis, halfW * 0.18);
            c1.Layer = layer;
            c1.ColorIndex = colorIndex;
            ms.AppendEntity(c1);
            tr.AddNewlyCreatedDBObject(c1, true);
            createdIds.Add(c1.ObjectId);

            Circle c2 = new Circle(new Point3d(cx, midY2, 0.0), Vector3d.ZAxis, halfW * 0.18);
            c2.Layer = layer;
            c2.ColorIndex = colorIndex;
            ms.AppendEntity(c2);
            tr.AddNewlyCreatedDBObject(c2, true);
            createdIds.Add(c2.ObjectId);
        }

        private static MText AddSldText(
            BlockTableRecord ms,
            Transaction tr,
            ObjectId unicodeStyleId,
            string layer,
            short colorIndex,
            string text,
            double x,
            double y,
            double height,
            double maxW,
            AttachmentPoint attach,
            double rotationRad,
            List<ObjectId> createdIds)
        {
            MText mt = new MText();
            mt.SetDatabaseDefaults(ms.Database);
            mt.Layer = layer;
            mt.ColorIndex = colorIndex;
            mt.TextStyleId = unicodeStyleId;
            mt.TextHeight = Math.Max(1e-4, height);
            mt.Width = Math.Max(height * 2.0, maxW);
            mt.Attachment = attach;
            mt.Location = new Point3d(x, y, 0.0);
            mt.Rotation = rotationRad;
            mt.Contents = EscapeMTextPlain(text ?? "");
            ms.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);
            createdIds.Add(mt.ObjectId);
            return mt;
        }

        private static short GetPhaseColorIndex(string phaseLabel)
        {
            if (string.Equals(phaseLabel, "R", StringComparison.OrdinalIgnoreCase)) return 1;   // Red
            if (string.Equals(phaseLabel, "Y", StringComparison.OrdinalIgnoreCase)) return 2;   // Yellow
            if (string.Equals(phaseLabel, "B", StringComparison.OrdinalIgnoreCase)) return 5;   // Blue
            if (string.Equals(phaseLabel, "RYB", StringComparison.OrdinalIgnoreCase)) return 4; // Cyan
            return 8;                                                                           // Gray
        }

        private static Extents3d? DrawSingleLineDiagram(
            Database db,
            BlockTableRecord ms,
            Transaction tr,
            List<BalancedPanelSchedule> panels,
            bool replaceExisting,
            double refSpan,
            double drawingSpan,
            double effectiveUnitsPerM,
            Extents3d? anchorBelowExt,
            Extents3d? mainExt,
            List<ObjectId> createdIds,
            List<ObjectId> erasedIds)
        {
            if (panels == null || panels.Count == 0) return null;
            string sldLayer = EnsureLayerSafe(db, tr, "_AI_SLD", 7, "Continuous");

            if (replaceExisting)
            {
                foreach (ObjectId id in ms)
                {
                    Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null || e.IsErased) continue;
                    if (string.Equals(e.Layer, sldLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        Entity ew = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                        ew.Erase(true);
                        erasedIds.Add(id);
                    }
                }
            }

            double h = refSpan > 1e-3
                ? Math.Max(0.16, refSpan * 0.42)
                : (drawingSpan > 1.0
                    ? Math.Max(0.18 * effectiveUnitsPerM, Math.Min(1.2 * effectiveUnitsPerM, drawingSpan * 0.010))
                    : Math.Max(0.20, 0.25 * effectiveUnitsPerM));

            double gap = anchorBelowExt.HasValue
                ? Math.Max(h * 12.0, (anchorBelowExt.Value.MaxPoint.Y - anchorBelowExt.Value.MinPoint.Y) * 0.14)
                : (mainExt.HasValue
                    ? Math.Max(h * 16.0, (mainExt.Value.MaxPoint.X - mainExt.Value.MinPoint.X) * 0.06)
                    : h * 16.0);

            double startLeftX;
            double cursorTopY;
            if (anchorBelowExt.HasValue)
            {
                startLeftX = anchorBelowExt.Value.MinPoint.X;
                cursorTopY = anchorBelowExt.Value.MinPoint.Y - gap;
            }
            else if (mainExt.HasValue)
            {
                startLeftX = mainExt.Value.MaxPoint.X + gap;
                cursorTopY = mainExt.Value.MaxPoint.Y;
            }
            else
            {
                startLeftX = 0.0;
                cursorTopY = 0.0;
            }

            ObjectId unicodeStyleId = EnsureUnicodeTextStyle(db, tr);
            Extents3d? totalExt = null;

            for (int pi = 0; pi < panels.Count; pi++)
            {
                BalancedPanelSchedule panel = panels[pi];
                int slotCount = Math.Max(1, panel.Ways.Count);
                double slotSpacing = h * 7.5;
                double sideMargin = h * 8.0;
                double panelW = Math.Max(h * 48.0, sideMargin * 2.0 + Math.Max(0, slotCount - 1) * slotSpacing);
                double panelH = h * 46.0;

                double leftX = startLeftX;
                double rightX = leftX + panelW;
                double topY = cursorTopY;
                double botY = topY - panelH;
                double midX = (leftX + rightX) * 0.5;

                Extents3d boxExt = new Extents3d(new Point3d(leftX, botY, 0.0), new Point3d(rightX, topY, 0.0));
                Polyline frame = CreateFramePolyline(boxExt, sldLayer, 7);
                frame.ConstantWidth = h * 0.08;
                ms.AppendEntity(frame);
                tr.AddNewlyCreatedDBObject(frame, true);
                createdIds.Add(frame.ObjectId);

                AddSldText(ms, tr, unicodeStyleId, sldLayer, 2,
                    "SINGLE LINE DIAGRAM (SLD) - " + panel.PanelName,
                    midX, topY - h * 2.2, h * 1.35, panelW * 0.92,
                    AttachmentPoint.MiddleCenter, 0.0, createdIds);

                AddSldLine(ms, tr, sldLayer, 7, leftX, topY - h * 4.4, rightX, topY - h * 4.4, h * 0.04, createdIds);

                double feederTopY = topY - h * 5.8;
                double mainBrkTop = topY - h * 9.8;
                double mainBrkBot = topY - h * 13.8;
                double busY = topY - h * 16.5;

                // Incoming feeder
                AddSldLine(ms, tr, sldLayer, 3, midX, feederTopY, midX, mainBrkTop, h * 0.08, createdIds);
                AddSldText(ms, tr, unicodeStyleId, sldLayer, 3,
                    "FROM MDB | " + panel.MainFeeder.CableDesignation + " (VD=" +
                    panel.MainFeeder.VoltageDropPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%)",
                    midX + h * 1.4, (feederTopY + mainBrkTop) * 0.5, h * 0.90, panelW * 0.45,
                    AttachmentPoint.MiddleLeft, 0.0, createdIds);

                // Main incomer breaker symbol & rating
                AddSldBreakerSymbol(ms, tr, sldLayer, 1, midX, mainBrkTop, mainBrkBot, h * 1.1, createdIds);
                AddSldLine(ms, tr, sldLayer, 3, midX, mainBrkBot, midX, busY, h * 0.08, createdIds);
                AddSldText(ms, tr, unicodeStyleId, sldLayer, 1,
                    panel.MainFeeder.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " +
                    panel.MainFeeder.BreakerType + " (" +
                    panel.MainFeeder.RecommendedIcuKa.ToString("0", CultureInfo.InvariantCulture) + "kA)",
                    midX + h * 1.8, (mainBrkTop + mainBrkBot) * 0.5, h * 0.95, panelW * 0.40,
                    AttachmentPoint.MiddleLeft, 0.0, createdIds);

                // Main 3-Phase Busbar
                double busLeft = leftX + sideMargin * 0.65;
                double busRight = rightX - sideMargin * 0.65;
                AddSldLine(ms, tr, sldLayer, 4, busLeft, busY, busRight, busY, h * 0.25, createdIds);

                AddSldText(ms, tr, unicodeStyleId, sldLayer, 4,
                    panel.PanelName + " BUSBAR | Conn: " + panel.ConnectedKva.ToString("0.##", CultureInfo.InvariantCulture) +
                    " kVA | Dem: " + panel.DemandKva.ToString("0.##", CultureInfo.InvariantCulture) +
                    " kVA | R:" + panel.TotalRVa.ToString("0", CultureInfo.InvariantCulture) +
                    " Y:" + panel.TotalYVa.ToString("0", CultureInfo.InvariantCulture) +
                    " B:" + panel.TotalBVa.ToString("0", CultureInfo.InvariantCulture) +
                    " VA | Imb: " + panel.ImbalancePercent.ToString("0.##", CultureInfo.InvariantCulture) + "%",
                    midX, busY + h * 1.4, h * 0.90, panelW * 0.92,
                    AttachmentPoint.BottomCenter, 0.0, createdIds);

                double spanFirstToLast = Math.Max(0, slotCount - 1) * slotSpacing;
                double firstDropX = midX - spanFirstToLast * 0.5;
                double brkDropTop = busY - h * 2.2;
                double brkDropBot = busY - h * 5.4;
                double tailTipY = busY - h * 9.5;
                double labelTopY = tailTipY - h * 1.0;

                for (int wi = 0; wi < panel.Ways.Count; wi++)
                {
                    BalancedCircuitWay w = panel.Ways[wi];
                    double wx = firstDropX + wi * slotSpacing;
                    short pColor = GetPhaseColorIndex(w.IsSpare ? "SPARE" : w.PhaseLabel);

                    AddSldLine(ms, tr, sldLayer, pColor, wx, busY, wx, brkDropTop, 0.0, createdIds);
                    AddSldBreakerSymbol(ms, tr, sldLayer, pColor, wx, brkDropTop, brkDropBot, h * 0.75, createdIds);
                    AddSldLine(ms, tr, sldLayer, pColor, wx, brkDropBot, wx, tailTipY, 0.0, createdIds);

                    // Arrow head at feeder end
                    AddSldLine(ms, tr, sldLayer, pColor, wx, tailTipY, wx - h * 0.35, tailTipY + h * 0.65, 0.0, createdIds);
                    AddSldLine(ms, tr, sldLayer, pColor, wx, tailTipY, wx + h * 0.35, tailTipY + h * 0.65, 0.0, createdIds);

                    // Phase indicator next to drop
                    AddSldText(ms, tr, unicodeStyleId, sldLayer, pColor,
                        w.PhaseLabel, wx + h * 0.95, (brkDropTop + brkDropBot) * 0.5, h * 0.72, h * 3.5,
                        AttachmentPoint.MiddleLeft, 0.0, createdIds);

                    string branchTag = w.IsSpare
                        ? (w.CircuitName + " | 10A MCB | SPARE")
                        : (w.CircuitName + " | " +
                           w.Calc.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " + w.Calc.BreakerType + " | " +
                           w.Calc.CableDesignation + " | " +
                           w.ConnectedVa.ToString("0", CultureInfo.InvariantCulture) + "VA (" +
                           w.Calc.VoltageDropPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%)");

                    AddSldText(ms, tr, unicodeStyleId, sldLayer, w.IsSpare ? (short)8 : (short)7,
                        branchTag, wx, labelTopY, h * 0.78, h * 17.0,
                        AttachmentPoint.MiddleRight, Math.PI * 0.5, createdIds);
                }

                if (!totalExt.HasValue) totalExt = boxExt;
                else { Extents3d curE = totalExt.Value; curE.AddExtents(boxExt); totalExt = curE; }

                cursorTopY = botY - h * 6.0;
            }

            return totalExt;
        }

        private static Dictionary<string, object> GeneratePanelScheduleAndSld(Document doc, Dictionary<string, object> args)
        {
            if (args == null) args = new Dictionary<string, object>();
            Database db = doc.Database;
            string panelFilter = GetStr(args, "panel_name");
            double fixtureVa = GetDouble(args, "fixture_va", 40.0);
            double voltageLnV = GetDouble(args, "voltage_ln_v", 220.0);
            double voltageLlV = GetDouble(args, "voltage_ll_v", 380.0);
            double powerFactor = Math.Max(0.4, Math.Min(1.0, GetDouble(args, "power_factor", 0.90)));
            double demandFactor = Math.Max(0.1, Math.Min(1.5, GetDouble(args, "demand_factor", 0.85)));
            double sparePercent = Math.Max(0.0, Math.Min(100.0, GetDouble(args, "spare_percent", 20.0)));
            double feederLengthM = Math.Max(5.0, GetDouble(args, "feeder_length_m", 30.0));
            double maxVdPercent = Math.Max(0.5, Math.Min(15.0, GetDouble(args, "max_vd_percent", 3.0)));
            string material = GetStr(args, "material") ?? "Cu";
            string insulation = GetStr(args, "insulation") ?? "XLPE";
            string installMethod = GetStr(args, "install_method") ?? "conduit";
            double ambientTempC = GetDouble(args, "ambient_temp_c", 40.0);

            bool drawPanelSchedule = GetBool(args, "draw_panel_schedule", true);
            bool drawSld = GetBool(args, "draw_sld", true);
            string tablePos = GetStr(args, "table_position") ?? "outside_right";
            double? tableGap = GetNullableDouble(args, "table_gap");
            string customTitle = GetStr(args, "title");
            bool replaceExisting = GetBool(args, "replace_existing", true);
            bool exportExcel = GetBool(args, "export_excel", false);
            bool zoom = GetBool(args, "zoom", true);
            bool honorSelection = GetBool(args, "use_selected", false);
            string sourceMode = (GetStr(args, "source") ?? "auto").Trim().ToLowerInvariant();

            List<TracedCircuitSummary> sourceCircuits = ParseExplicitPanelCircuitsArg(
                args.ContainsKey("circuits") ? args["circuits"] : null,
                fixtureVa);

            double effectiveUnitsPerM = 1.0;
            bool scaleInferred = false;
            string scaleWarning = "";
            double refSpan = 0.0;
            double drawingSpan = 0.0;
            Extents3d? mainExt = null;

            using (DocumentLock docLock = doc.LockDocument())
            {
                if (sourceCircuits.Count == 0 && (sourceMode == "saved_memory" || sourceMode == "memory" || sourceMode == "saved"))
                {
                    List<SavedCircuitRecord> savedList = GetAllSavedCircuitRecords(doc);
                    if (savedList.Count == 0)
                        return Err("لا توجد لينيات محفوظة في ذاكرة المشروع حتى الآن. استخرج اللينيات أولاً أو استخدم source: 'auto'.");
                    for (int si = 0; si < savedList.Count; si++)
                    {
                        SavedCircuitRecord sr = savedList[si];
                        sourceCircuits.Add(new TracedCircuitSummary
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
                    using (Transaction trExt = db.TransactionManager.StartTransaction())
                    {
                        Dictionary<string, bool> hiddenMap = BuildLayerHiddenMap(db, trExt);
                        double tmpMed;
                        ComputeDrawingFixtureMetrics(db, trExt, true, hiddenMap, out drawingSpan, out tmpMed, out mainExt);
                        refSpan = tmpMed;
                        Dictionary<string, object> uInfo = GetUnitsInfo(db);
                        double rawU = Convert.ToDouble(uInfo["units_per_m"], CultureInfo.InvariantCulture);
                        effectiveUnitsPerM = ResolveEffectiveUnitsPerMeter(db, rawU, drawingSpan, refSpan, out scaleInferred, out scaleWarning);
                        trExt.Commit();
                    }
                }
                else if (sourceCircuits.Count == 0)
                {
                    DrawingCircuitScanResult scan;
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        scan = ScanAllCircuitsInternal(doc, db, tr, args, honorSelection);
                        tr.Commit();
                    }
                    if (!string.IsNullOrEmpty(scan.ErrorMessage)) return Err(scan.ErrorMessage);
                    if (scan.Circuits.Count == 0)
                        return Err("لم يتم رصد دوائر في الرسم لتوليد جدول اللوحة والمخطط الأحادي. تأكد من رسم اللينيات أو مرر مصفوفة circuits يدوياً.");

                    sourceCircuits = scan.Circuits;
                    effectiveUnitsPerM = scan.EffectiveUnitsPerM;
                    scaleInferred = scan.ScaleInferred;
                    scaleWarning = scan.ScaleWarning;
                    refSpan = scan.RefSpan;
                    drawingSpan = scan.DrawingSpan;
                    mainExt = scan.MainExt;
                }
                else
                {
                    using (Transaction trExt = db.TransactionManager.StartTransaction())
                    {
                        Dictionary<string, bool> hiddenMap = BuildLayerHiddenMap(db, trExt);
                        double tmpMed;
                        ComputeDrawingFixtureMetrics(db, trExt, true, hiddenMap, out drawingSpan, out tmpMed, out mainExt);
                        refSpan = tmpMed;
                        Dictionary<string, object> uInfo = GetUnitsInfo(db);
                        double rawU = Convert.ToDouble(uInfo["units_per_m"], CultureInfo.InvariantCulture);
                        effectiveUnitsPerM = ResolveEffectiveUnitsPerMeter(db, rawU, drawingSpan, refSpan, out scaleInferred, out scaleWarning);
                        trExt.Commit();
                    }
                }

                Dictionary<string, List<TracedCircuitSummary>> groupedByPanel = new Dictionary<string, List<TracedCircuitSummary>>(StringComparer.OrdinalIgnoreCase);
                List<string> panelOrder = new List<string>();
                for (int i = 0; i < sourceCircuits.Count; i++)
                {
                    TracedCircuitSummary c = sourceCircuits[i];
                    string pName = ExtractPanelNameFallback(c.CircuitName, c.PanelName);
                    if (!string.IsNullOrWhiteSpace(panelFilter) &&
                        !string.Equals(panelFilter.Trim(), "ALL", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(pName, panelFilter.Trim(), StringComparison.OrdinalIgnoreCase) &&
                        !MatchesPatternOrSubstring(pName, panelFilter))
                        continue;

                    List<TracedCircuitSummary> bucket;
                    if (!groupedByPanel.TryGetValue(pName, out bucket))
                    {
                        bucket = new List<TracedCircuitSummary>();
                        groupedByPanel[pName] = bucket;
                        panelOrder.Add(pName);
                    }
                    bucket.Add(c);
                }

                if (panelOrder.Count == 0)
                    return Err("لم يتم العثور على لوحات مطابقة للاسم '" + (panelFilter ?? "") + "'.");

                panelOrder.Sort(StringComparer.OrdinalIgnoreCase);

                List<BalancedPanelSchedule> panels = new List<BalancedPanelSchedule>();
                int totalActiveCircuits = 0;
                for (int pi = 0; pi < panelOrder.Count; pi++)
                {
                    string pn = panelOrder[pi];
                    BalancedPanelSchedule built = BuildBalancedPanelSchedule(
                        doc, pn, groupedByPanel[pn], fixtureVa, voltageLnV, voltageLlV, powerFactor,
                        demandFactor, sparePercent, feederLengthM, maxVdPercent,
                        material, insulation, installMethod, ambientTempC);
                    panels.Add(built);
                    totalActiveCircuits += built.ActiveWaysCount;
                }

                bool scheduleDrawn = false;
                bool sldDrawn = false;
                int scheduleCreatedCount = 0;
                int sldCreatedCount = 0;
                Extents3d? allCreatedExt = null;

                if (drawPanelSchedule || drawSld)
                {
                    if (!db.TileMode) { try { db.TileMode = true; } catch { } }
                    List<ObjectId> createdIds = new List<ObjectId>();
                    List<ObjectId> erasedIds = new List<ObjectId>();
                    Extents3d? lastScheduleExt = null;

                    using (Transaction trDraw = db.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord msW = (BlockTableRecord)trDraw.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                        string schedLayer = EnsureLayerSafe(db, trDraw, "_AI_PANEL_SCHEDULE", 7, "Continuous");

                        if (drawPanelSchedule)
                        {
                            if (replaceExisting)
                            {
                                foreach (ObjectId oid in msW)
                                {
                                    Entity e = trDraw.GetObject(oid, OpenMode.ForRead) as Entity;
                                    if (e == null || e.IsErased) continue;
                                    if (string.Equals(e.Layer, schedLayer, StringComparison.OrdinalIgnoreCase))
                                    {
                                        Entity ew = (Entity)trDraw.GetObject(oid, OpenMode.ForWrite);
                                        ew.Erase(true);
                                        erasedIds.Add(oid);
                                    }
                                }
                            }

                            int beforeCount = createdIds.Count;
                            for (int pi = 0; pi < panels.Count; pi++)
                            {
                                ExcelSheetData pSheet = BuildPanelScheduleSheet(panels[pi], panels.Count == 1 ? customTitle : null);
                                Extents3d? tblExt = RenderStyledScheduleTable(
                                    doc, db, msW, trDraw, pSheet, "_AI_PANEL_SCHEDULE",
                                    false, tablePos, tableGap, null, mainExt, createdIds, erasedIds);
                                if (tblExt.HasValue && IsFiniteBounds(tblExt.Value))
                                {
                                    if (!lastScheduleExt.HasValue) lastScheduleExt = tblExt;
                                    else { Extents3d cur = lastScheduleExt.Value; cur.AddExtents(tblExt.Value); lastScheduleExt = cur; }
                                    if (!allCreatedExt.HasValue) allCreatedExt = tblExt;
                                    else { Extents3d curA = allCreatedExt.Value; curA.AddExtents(tblExt.Value); allCreatedExt = curA; }
                                }
                            }
                            scheduleCreatedCount = createdIds.Count - beforeCount;
                            scheduleDrawn = lastScheduleExt.HasValue;
                        }

                        if (drawSld)
                        {
                            int beforeSld = createdIds.Count;
                            Extents3d? sldExt = DrawSingleLineDiagram(
                                db, msW, trDraw, panels, replaceExisting, refSpan, drawingSpan, effectiveUnitsPerM,
                                lastScheduleExt, mainExt, createdIds, erasedIds);
                            sldCreatedCount = createdIds.Count - beforeSld;
                            sldDrawn = sldExt.HasValue;
                            if (sldExt.HasValue && IsFiniteBounds(sldExt.Value))
                            {
                                if (!allCreatedExt.HasValue) allCreatedExt = sldExt;
                                else { Extents3d curA = allCreatedExt.Value; curA.AddExtents(sldExt.Value); allCreatedExt = curA; }
                            }
                        }

                        db.UpdateExt(true);
                        trDraw.Commit();
                    }

                    if (createdIds.Count > 0 || erasedIds.Count > 0)
                    {
                        CadUndoEntry undo = new CadUndoEntry();
                        undo.ToolName = "generate_panel_schedule_and_sld";
                        undo.Summary = "Panel Schedule + SLD (" + panels.Count.ToString(CultureInfo.InvariantCulture) + " panels, " +
                                       totalActiveCircuits.ToString(CultureInfo.InvariantCulture) + " circuits)";
                        undo.CreatedIds.AddRange(createdIds);
                        undo.ErasedIds.AddRange(erasedIds);
                        PushUndoEntry(doc, undo);
                    }

                    if (zoom && allCreatedExt.HasValue && IsFiniteBounds(allCreatedExt.Value))
                        ZoomToExtents3d(doc, allCreatedExt.Value, 1.15);
                    try { doc.Editor.UpdateScreen(); } catch { }
                }

                List<object> panelDicts = new List<object>();
                for (int pi = 0; pi < panels.Count; pi++)
                {
                    BalancedPanelSchedule p = panels[pi];
                    List<object> cRows = new List<object>();
                    for (int wi = 0; wi < p.Ways.Count; wi++)
                    {
                        BalancedCircuitWay w = p.Ways[wi];
                        cRows.Add(new Dictionary<string, object>
                        {
                            { "way", w.WayIndex },
                            { "circuit_name", w.CircuitName },
                            { "is_spare", w.IsSpare },
                            { "phase", w.PhaseLabel },
                            { "matched_blocks_count", w.MatchedBlocksCount },
                            { "length_m", w.LengthMeters },
                            { "connected_va", w.ConnectedVa },
                            { "r_va", w.RVa },
                            { "y_va", w.YVa },
                            { "b_va", w.BVa },
                            { "design_current_ib_a", w.Calc != null ? w.Calc.DesignCurrentA : 0.0 },
                            { "breaker", w.IsSpare ? "10A MCB" : (w.Calc.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " + w.Calc.BreakerType) },
                            { "cable", w.IsSpare ? "SPARE" : w.Calc.CableDesignation },
                            { "voltage_drop_percent", w.Calc != null ? w.Calc.VoltageDropPercent : 0.0 }
                        });
                    }

                    panelDicts.Add(new Dictionary<string, object>
                    {
                        { "panel_name", p.PanelName },
                        { "active_ways", p.ActiveWaysCount },
                        { "spare_ways", p.SpareWaysCount },
                        { "phase_r_va", p.TotalRVa },
                        { "phase_y_va", p.TotalYVa },
                        { "phase_b_va", p.TotalBVa },
                        { "imbalance_percent", p.ImbalancePercent },
                        { "connected_kw", p.ConnectedKw },
                        { "demand_kw", p.DemandKw },
                        { "connected_kva", p.ConnectedKva },
                        { "demand_kva", p.DemandKva },
                        { "main_breaker", p.MainFeeder.BreakerRatingA.ToString("0", CultureInfo.InvariantCulture) + "A " + p.MainFeeder.BreakerType + " (" + p.MainFeeder.RecommendedIcuKa.ToString("0", CultureInfo.InvariantCulture) + "kA)" },
                        { "main_cable", p.MainFeeder.CableDesignation },
                        { "main_vd_percent", p.MainFeeder.VoltageDropPercent },
                        { "circuits", cRows }
                    });
                }

                Dictionary<string, object> res = new Dictionary<string, object>
                {
                    { "panels_count", panels.Count },
                    { "circuits_count", totalActiveCircuits },
                    { "panels", panelDicts },
                    { "panel_schedule_drawn", scheduleDrawn },
                    { "panel_schedule_layer", "_AI_PANEL_SCHEDULE" },
                    { "schedule_created_count", scheduleCreatedCount },
                    { "sld_drawn", sldDrawn },
                    { "sld_layer", "_AI_SLD" },
                    { "sld_created_count", sldCreatedCount },
                    { "effective_units_per_m", Round(effectiveUnitsPerM) },
                    { "unit_scale_inferred", scaleInferred }
                };
                if (!string.IsNullOrEmpty(scaleWarning)) res["unit_scale_warning"] = scaleWarning;

                if (exportExcel)
                {
                    ExcelSheetData xlSheet = BuildCombinedPanelsWorkbookSheet(panels, customTitle);
                    string xlTarget = GetStr(args, "excel_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "panel_schedule", ".xlsx");
                    res["excel_exported"] = true;
                    res["excel_path"] = ExcelTableInterop.WriteSpreadsheet(xlTarget, xlSheet);
                }
                return Ok(res);
            }
        }

        private static short GetQaSeverityColorIndex(string severity)
        {
            if (string.Equals(severity, "CRITICAL", StringComparison.OrdinalIgnoreCase)) return 1; // Red
            if (string.Equals(severity, "WARNING", StringComparison.OrdinalIgnoreCase)) return 2;  // Yellow
            return 6;                                                                             // Magenta
        }

        private static Dictionary<string, object> AuditElectricalDrawing(Document doc, Dictionary<string, object> args)
        {
            if (args == null) args = new Dictionary<string, object>();
            Database db = doc.Database;
            double fixtureVa = Math.Max(5.0, GetDouble(args, "fixture_va", 40.0));
            double maxCircuitVa = Math.Max(100.0, GetDouble(args, "max_circuit_va", 1800.0));
            int maxFixturesPerCircuit = Math.Max(1, GetInt(args, "max_fixtures_per_circuit", 18));
            double minCableMm2 = Math.Max(1.0, GetDouble(args, "min_cable_mm2", 1.5));
            double maxVdPercent = Math.Max(0.5, Math.Min(15.0, GetDouble(args, "max_vd_percent", 3.0)));
            double voltageLnV = Math.Max(100.0, GetDouble(args, "voltage_ln_v", 220.0));
            double powerFactor = Math.Max(0.4, Math.Min(1.0, GetDouble(args, "power_factor", 0.90)));
            bool drawMarkers = GetBool(args, "draw_markers", true);
            bool drawReportTable = GetBool(args, "draw_report_table", true);
            string tablePos = GetStr(args, "table_position") ?? "outside_right";
            double? tableGap = GetNullableDouble(args, "table_gap");
            bool replaceExisting = GetBool(args, "replace_existing", true);
            bool exportExcel = GetBool(args, "export_excel", false);
            bool zoom = GetBool(args, "zoom", true);
            bool honorSelection = GetBool(args, "use_selected", false);

            List<ElectricalQaIssue> issues = new List<ElectricalQaIssue>();
            DrawingCircuitScanResult scan;

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    scan = ScanAllCircuitsInternal(doc, db, tr, args, honorSelection);
                    tr.Commit();
                }

                if (scan.Cache == null && !string.IsNullOrEmpty(scan.ErrorMessage))
                    return Err(scan.ErrorMessage);
                if (scan.Circuits.Count == 0 && (scan.Cache == null || scan.Cache.FixtureBlocks.Count == 0))
                    return Err(!string.IsNullOrEmpty(scan.ErrorMessage) ? scan.ErrorMessage : "لم يتم العثور على عناصر كهربائية للفحص.");

                HashSet<ObjectId> wiredBlockIds = new HashSet<ObjectId>();
                HashSet<string> knownFixtureBlockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int ci = 0; ci < scan.Circuits.Count; ci++)
                {
                    TracedCircuitSummary c = scan.Circuits[ci];
                    for (int bi = 0; bi < c.MatchedBlockIds.Count; bi++)
                        wiredBlockIds.Add(c.MatchedBlockIds[bi]);
                    if (c.CountsByBlock != null)
                    {
                        foreach (string bn in c.CountsByBlock.Keys)
                        {
                            if (!string.IsNullOrWhiteSpace(bn)) knownFixtureBlockNames.Add(bn);
                        }
                    }
                }

                int orphanCount = 0;
                int overloadCount = 0;
                int duplicateCount = 0;
                int vdViolCount = 0;
                int unnamedCount = 0;

                // 1. Orphan Fixtures
                if (scan.Cache != null)
                {
                    for (int fi = 0; fi < scan.Cache.FixtureBlocks.Count; fi++)
                    {
                        CachedBlockEntry fb = scan.Cache.FixtureBlocks[fi];
                        if (wiredBlockIds.Contains(fb.Id)) continue;
                        if (IsArchitecturalLayerName(fb.Layer)) continue;
                        bool isCandidate = knownFixtureBlockNames.Contains(fb.BlockName) || IsCandidateCircuitLayerName(fb.Layer);
                        if (!isCandidate) continue;

                        orphanCount++;
                        issues.Add(new ElectricalQaIssue
                        {
                            Code = "ORPHAN_FIXTURE",
                            Severity = "WARNING",
                            Target = fb.BlockName + " (#" + fb.Handle + ")",
                            Handle = fb.Handle,
                            X = fb.CenterX,
                            Y = fb.CenterY,
                            HasPoint = true,
                            Details = "كشاف/وحدة '" + fb.BlockName + "' على طبقة '" + fb.Layer + "' غير متصل بأي مسار لينية (" +
                                      Math.Round(fb.CenterX, 2).ToString(CultureInfo.InvariantCulture) + ", " +
                                      Math.Round(fb.CenterY, 2).ToString(CultureInfo.InvariantCulture) + ").",
                            Recommendation = "توصيل سلك اللينية بالوحدة أو التأكد من إغلاق فجوة الاتصال."
                        });
                    }
                }

                // Track circuit names for duplicate detection
                Dictionary<string, List<TracedCircuitSummary>> circGroups = new Dictionary<string, List<TracedCircuitSummary>>(StringComparer.OrdinalIgnoreCase);
                int baseCableIdx = FindNearestCableSizeIndex(minCableMm2);
                double baseCableSizeActual = LvStandardCableSizesMm2[baseCableIdx];
                double rOpBase = GetOperatingResistanceOhmPerKm(baseCableIdx, "Cu", "XLPE");
                double xBase = LvReactanceOhmPerKm[baseCableIdx];
                double sinPhi = Math.Sqrt(Math.Max(0.0, 1.0 - powerFactor * powerFactor));
                Dictionary<string, object> recDefaults = new Dictionary<string, object>
                {
                    { "load_type", "lighting" },
                    { "phases", 1 },
                    { "voltage_v", voltageLnV },
                    { "power_factor", powerFactor },
                    { "material", "Cu" },
                    { "insulation", "XLPE" },
                    { "install_method", "conduit" },
                    { "ambient_temp_c", 40.0 }
                };

                for (int ci = 0; ci < scan.Circuits.Count; ci++)
                {
                    TracedCircuitSummary c = scan.Circuits[ci];
                    string displayCirc = !string.IsNullOrWhiteSpace(c.CircuitName) ? c.CircuitName : ("UNNAMED-" + (ci + 1).ToString(CultureInfo.InvariantCulture));
                    double estVa = Math.Round(c.MatchedBlocksCount * fixtureVa, 1);

                    // 2. Overloaded circuits
                    if (estVa > maxCircuitVa || c.MatchedBlocksCount > maxFixturesPerCircuit || c.GuardTriggered)
                    {
                        overloadCount++;
                        issues.Add(new ElectricalQaIssue
                        {
                            Code = "OVERLOADED_CIRCUIT",
                            Severity = "CRITICAL",
                            Target = displayCirc,
                            Handle = c.PathCurveIds.Count > 0 ? c.PathCurveIds[0].Handle.ToString() : "",
                            X = c.MinX,
                            Y = c.MinY,
                            HasPoint = true,
                            Details = "اللينيا '" + displayCirc + "' محملة بـ " + c.MatchedBlocksCount.ToString(CultureInfo.InvariantCulture) +
                                      " وحدة (" + estVa.ToString("0", CultureInfo.InvariantCulture) + " VA)، وتتجاوز الحد (" +
                                      maxFixturesPerCircuit.ToString(CultureInfo.InvariantCulture) + " وحدة / " +
                                      maxCircuitVa.ToString("0", CultureInfo.InvariantCulture) + " VA)" +
                                      (c.GuardTriggered ? " مع تفعيل حد الأمان." : "."),
                            Recommendation = "تقسيم اللينيا إلى دائرتين لتقليل عدد الكشافات والأحمال."
                        });
                    }

                    // 3. Collect for duplicate check
                    if (!string.IsNullOrWhiteSpace(c.CircuitName))
                    {
                        string dupKey = !string.IsNullOrWhiteSpace(c.PanelName)
                            ? (c.PanelName.Trim() + "|" + c.CircuitName.Trim())
                            : c.CircuitName.Trim();
                        List<TracedCircuitSummary> grp;
                        if (!circGroups.TryGetValue(dupKey, out grp))
                        {
                            grp = new List<TracedCircuitSummary>();
                            circGroups[dupKey] = grp;
                        }
                        grp.Add(c);
                    }
                    else
                    {
                        // 5. Unnamed / open end
                        unnamedCount++;
                        issues.Add(new ElectricalQaIssue
                        {
                            Code = "UNNAMED_CIRCUIT",
                            Severity = "WARNING",
                            Target = displayCirc,
                            Handle = c.PathCurveIds.Count > 0 ? c.PathCurveIds[0].Handle.ToString() : "",
                            X = c.MinX,
                            Y = c.MinY,
                            HasPoint = true,
                            Details = "تم رصد مسار لينية بطول " + c.LengthMeters.ToString("0.##", CultureInfo.InvariantCulture) +
                                      " م (" + c.MatchedBlocksCount.ToString(CultureInfo.InvariantCulture) + " وحدة) بدون اسم لينية أو بسهم نهاية مفقود (" + c.TerminationType + ").",
                            Recommendation = "إضافة سهم Home-Run ARROW وتسمية اللينيا بجوار نهاية السلك."
                        });
                    }

                    // 4. Voltage Drop on minCableMm2
                    if (estVa > 0.0 && c.LengthMeters > 0.0)
                    {
                        double ib = estVa / voltageLnV;
                        double vdV = 2.0 * ib * c.LengthMeters * (rOpBase * powerFactor + xBase * sinPhi) / 1000.0;
                        double vdPct = Math.Round((vdV / voltageLnV) * 100.0, 2);
                        if (vdPct > maxVdPercent)
                        {
                            vdViolCount++;
                            string recCable = "2x2.5mm2 Cu/XLPE";
                            try
                            {
                                Dictionary<string, object> calcIn = new Dictionary<string, object>
                                {
                                    { "name", displayCirc },
                                    { "load_type", "lighting" },
                                    { "phases", 1 },
                                    { "voltage_v", voltageLnV },
                                    { "load_kva", Math.Max(0.04, estVa / 1000.0) },
                                    { "length_m", c.LengthMeters },
                                    { "max_vd_percent", maxVdPercent }
                                };
                                LvCircuitCalcResult recCalc = ComputeSingleLvCircuit(calcIn, recDefaults, ci + 1);
                                recCable = recCalc.CableDesignation + " (VD=" + recCalc.VoltageDropPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%)";
                            }
                            catch { }

                            issues.Add(new ElectricalQaIssue
                            {
                                Code = "VOLTAGE_DROP_EXCEEDED",
                                Severity = "CRITICAL",
                                Target = displayCirc,
                                Handle = c.PathCurveIds.Count > 0 ? c.PathCurveIds[0].Handle.ToString() : "",
                                X = c.MinX,
                                Y = c.MinY,
                                HasPoint = true,
                                Details = "هبوط الجهد على سلك " + baseCableSizeActual.ToString("0.#", CultureInfo.InvariantCulture) +
                                          "mm2 يبلغ " + vdPct.ToString("0.00", CultureInfo.InvariantCulture) + "% بطول " +
                                          c.LengthMeters.ToString("0.##", CultureInfo.InvariantCulture) + " م (" +
                                          estVa.ToString("0", CultureInfo.InvariantCulture) + " VA) متجاوزاً حد " +
                                          maxVdPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%.",
                                Recommendation = "رفع مقطع السلك إلى " + recCable + "."
                            });
                        }
                    }
                }

                // Emit duplicate name issues
                foreach (KeyValuePair<string, List<TracedCircuitSummary>> kv in circGroups)
                {
                    if (kv.Value.Count < 2) continue;
                    duplicateCount++;
                    string firstName = kv.Value[0].CircuitName;
                    for (int di = 0; di < kv.Value.Count; di++)
                    {
                        TracedCircuitSummary dc = kv.Value[di];
                        issues.Add(new ElectricalQaIssue
                        {
                            Code = "DUPLICATE_CIRCUIT_NAME",
                            Severity = "CRITICAL",
                            Target = firstName,
                            Handle = dc.PathCurveIds.Count > 0 ? dc.PathCurveIds[0].Handle.ToString() : "",
                            X = dc.MinX,
                            Y = dc.MinY,
                            HasPoint = true,
                            Details = "اسم اللينيا '" + firstName + "' مكرر " + kv.Value.Count.ToString(CultureInfo.InvariantCulture) +
                                      " مرات في اللوحة (المثيل #" + (di + 1).ToString(CultureInfo.InvariantCulture) + ").",
                            Recommendation = "إعادة ترقيم إحدى اللينيات المكررة لتجنب تعارض القواطع باللوحة."
                        });
                    }
                }

                // 6. Unit scale mismatch info
                if (scan.ScaleInferred)
                {
                    issues.Add(new ElectricalQaIssue
                    {
                        Code = "UNIT_SCALE_MISMATCH",
                        Severity = "INFO",
                        Target = "INSUNITS",
                        Handle = "",
                        X = 0.0,
                        Y = 0.0,
                        HasPoint = false,
                        Details = scan.ScaleWarning,
                        Recommendation = "يفضل ضبط أمر UNITS / INSUNITS على Meters لتطابق المقياس الهندسي."
                    });
                }

                int criticalCount = 0;
                int warningCount = 0;
                int infoCount = 0;
                for (int i = 0; i < issues.Count; i++)
                {
                    if (issues[i].Severity == "CRITICAL") criticalCount++;
                    else if (issues[i].Severity == "WARNING") warningCount++;
                    else infoCount++;
                }

                int healthScore = Math.Max(0, 100 - criticalCount * 15 - warningCount * 5 - infoCount * 2);
                string overallStatus = criticalCount > 0 ? "FAIL" : (warningCount > 0 ? "WARN" : "PASS");

                ExcelSheetData qaSheet = new ExcelSheetData();
                qaSheet.SheetName = "Electrical_QA";
                qaSheet.Title = "تقرير الفحص الفني الكهربائي للمخطط (الحالة: " + overallStatus + " | التقييم: " +
                                healthScore.ToString(CultureInfo.InvariantCulture) + "/100)";
                qaSheet.Headers = new List<string>
                {
                    "م",
                    "الخطورة",
                    "نوع الملاحظة",
                    "العنصر / اللينيا",
                    "التفاصيل الهندسية",
                    "التوصية"
                };
                if (issues.Count == 0)
                {
                    qaSheet.Rows.Add(new List<string>
                    {
                        "1", "PASS", "ALL_CLEAR", "-", "لم يتم رصد أي كشافات مفصولة أو أحمال زائدة أو تكرار أسماء أو هبوط جهد متجاوز.", "المخطط سليم فنياً."
                    });
                }
                else
                {
                    for (int i = 0; i < issues.Count; i++)
                    {
                        ElectricalQaIssue q = issues[i];
                        qaSheet.Rows.Add(new List<string>
                        {
                            (i + 1).ToString(CultureInfo.InvariantCulture),
                            q.Severity,
                            q.Code,
                            q.Target,
                            q.Details,
                            q.Recommendation
                        });
                    }
                }
                qaSheet.FooterRow = new List<string>
                {
                    "الملخص",
                    overallStatus,
                    issues.Count.ToString(CultureInfo.InvariantCulture) + " ملاحظة",
                    "حرج: " + criticalCount + " | تحذير: " + warningCount,
                    "كشافات مفصولة: " + orphanCount + " | تكرار: " + duplicateCount + " | VD: " + vdViolCount,
                    "Score " + healthScore + "/100"
                };

                bool markersDrawn = false;
                bool tableDrawn = false;
                int createdCount = 0;
                Extents3d? qaExt = null;

                if (drawMarkers || drawReportTable)
                {
                    if (!db.TileMode) { try { db.TileMode = true; } catch { } }
                    List<ObjectId> createdIds = new List<ObjectId>();
                    List<ObjectId> erasedIds = new List<ObjectId>();

                    using (Transaction trQa = db.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord msW = (BlockTableRecord)trQa.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                        string qaLayer = EnsureLayerSafe(db, trQa, "_AI_ELECTRICAL_QA", 1, "Continuous");

                        if (replaceExisting)
                        {
                            foreach (ObjectId oid in msW)
                            {
                                Entity e = trQa.GetObject(oid, OpenMode.ForRead) as Entity;
                                if (e == null || e.IsErased) continue;
                                if (string.Equals(e.Layer, qaLayer, StringComparison.OrdinalIgnoreCase))
                                {
                                    Entity ew = (Entity)trQa.GetObject(oid, OpenMode.ForWrite);
                                    ew.Erase(true);
                                    erasedIds.Add(oid);
                                }
                            }
                        }

                        if (drawMarkers && issues.Count > 0)
                        {
                            ObjectId unicodeStyleId = EnsureUnicodeTextStyle(db, trQa);
                            double markerRadius = Math.Max(scan.RefSpan * 0.85, 0.35 * scan.EffectiveUnitsPerM);
                            double textH = Math.Max(markerRadius * 0.45, 0.14 * scan.EffectiveUnitsPerM);
                            for (int i = 0; i < issues.Count; i++)
                            {
                                ElectricalQaIssue q = issues[i];
                                if (!q.HasPoint) continue;
                                short colIdx = GetQaSeverityColorIndex(q.Severity);
                                Circle marker = new Circle(new Point3d(q.X, q.Y, 0.0), Vector3d.ZAxis, markerRadius);
                                marker.Layer = qaLayer;
                                marker.ColorIndex = colIdx;
                                msW.AppendEntity(marker);
                                trQa.AddNewlyCreatedDBObject(marker, true);
                                createdIds.Add(marker.ObjectId);

                                AddSldText(msW, trQa, unicodeStyleId, qaLayer, colIdx,
                                    "[" + q.Code + "] " + q.Target,
                                    q.X + markerRadius * 1.15, q.Y + markerRadius * 0.55,
                                    textH, markerRadius * 18.0,
                                    AttachmentPoint.BottomLeft, 0.0, createdIds);
                                markersDrawn = true;
                            }
                        }

                        if (drawReportTable)
                        {
                            qaExt = RenderStyledScheduleTable(
                                doc, db, msW, trQa, qaSheet, "_AI_ELECTRICAL_QA",
                                false, tablePos, tableGap, null, scan.MainExt, createdIds, erasedIds);
                            tableDrawn = qaExt.HasValue;
                        }

                        db.UpdateExt(true);
                        trQa.Commit();
                    }

                    createdCount = createdIds.Count;
                    if (createdIds.Count > 0 || erasedIds.Count > 0)
                    {
                        CadUndoEntry undo = new CadUndoEntry();
                        undo.ToolName = "audit_electrical_drawing";
                        undo.Summary = "Electrical QA Audit (" + overallStatus + ", " + issues.Count.ToString(CultureInfo.InvariantCulture) + " issues)";
                        undo.CreatedIds.AddRange(createdIds);
                        undo.ErasedIds.AddRange(erasedIds);
                        PushUndoEntry(doc, undo);
                    }

                    if (zoom && qaExt.HasValue && IsFiniteBounds(qaExt.Value))
                        ZoomToExtents3d(doc, qaExt.Value, 1.18);
                    try { doc.Editor.UpdateScreen(); } catch { }
                }

                List<object> issueDicts = new List<object>();
                for (int i = 0; i < issues.Count; i++)
                {
                    ElectricalQaIssue q = issues[i];
                    issueDicts.Add(new Dictionary<string, object>
                    {
                        { "code", q.Code },
                        { "severity", q.Severity },
                        { "target", q.Target },
                        { "handle", q.Handle },
                        { "x", Math.Round(q.X, 3) },
                        { "y", Math.Round(q.Y, 3) },
                        { "details", q.Details },
                        { "recommendation", q.Recommendation }
                    });
                }

                Dictionary<string, object> res = new Dictionary<string, object>
                {
                    { "overall_status", overallStatus },
                    { "health_score", healthScore },
                    { "circuits_checked", scan.Circuits.Count },
                    { "total_issues", issues.Count },
                    { "critical_count", criticalCount },
                    { "warning_count", warningCount },
                    { "info_count", infoCount },
                    { "orphan_fixtures_count", orphanCount },
                    { "overloaded_circuits_count", overloadCount },
                    { "duplicate_names_count", duplicateCount },
                    { "vd_violations_count", vdViolCount },
                    { "unnamed_circuits_count", unnamedCount },
                    { "unit_scale_mismatch", scan.ScaleInferred },
                    { "effective_units_per_m", Round(scan.EffectiveUnitsPerM) },
                    { "issues", issueDicts },
                    { "markers_drawn", markersDrawn },
                    { "report_table_drawn", tableDrawn },
                    { "qa_layer", "_AI_ELECTRICAL_QA" },
                    { "created_count", createdCount }
                };
                if (!string.IsNullOrEmpty(scan.ScaleWarning)) res["unit_scale_warning"] = scan.ScaleWarning;

                if (exportExcel)
                {
                    string xlTarget = GetStr(args, "excel_path") ?? ExcelTableInterop.ResolveDefaultExportPath(doc, "electrical_qa_audit", ".xlsx");
                    res["excel_exported"] = true;
                    res["excel_path"] = ExcelTableInterop.WriteSpreadsheet(xlTarget, qaSheet);
                }
                return Ok(res);
            }
        }
    }
}

