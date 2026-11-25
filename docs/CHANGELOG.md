# Changelog

## 2026-11-25 - Phase 7.1.1: Real-Project 8-Column Gridded Legend, Range Wattages & Zero-Load Accessories

- **Full-Span Grid Filtering & Header-Aware Symbol Column Isolation (`integrations/autocad_context_plugin/CadProjectLegendDb.cs`):**
  - Added `FilterMajorGridAxes` to drop short detail/diagram lines inside `TYPE` cells (`<45%` table width / `<40%` table height) and short sub-divider lines inside `RECOMMENDED TYPE` cells so table rows and columns align with outer borders cleanly.
  - Added `IsLegendHeaderSymbolText` (`ICON`/`SYMBOL`), `IsLegendHeaderDescText` (`DESCRIPTION`/`TYPE`), and `IsLegendHeaderIgnoreText` (`RECOMMENDED TYPE`/`PHOTO`/`CATALOG`) so `TryExtractGridTableLegend` locks onto the primary symbol column (`ICON`), ignores incidental sketch blocks in `TYPE` (e.g. `SPOTE`), stops description collection at the first `QTY` column, and ignores `RECOMMENDED` columns.
- **Range Wattages, Voltage Ranges & Zero-Load Panel/Accessory Classification (`CadProjectLegendDb.cs`, `AcCoreRegressionVerifier.cs`):**
  - Added wattage range parsing (`9-12 W`, `3-6W`, `10 to 15W`) preferring an in-range wattage embedded in the block name (`upligh9w` -> `9W`) or using the conservative upper bound (`3-6W` -> `6W`), plus hyphenated wattages (`LED-12W`) and `220-240V` parsing (`230V`).
  - Added `"accessory"` classification (`MANHOLE`, `METAL POST`, `ELECTRICAL BOX FOR LED TRANSFORMER`, `CABLE PHASE`, etc.) and `0 W` non-consuming handling in `ComputeCircuitLoadFromProjectDb` for `"panel"` and `"accessory"` entries so strict calculations never assume `40 VA` on non-luminaire blocks.
  - Updated `ManageProjectLegendDb` (`action: 'extract_from_selection'`) to prune stale `drawing_legend` entries cleanly and upgraded `TEST16A` to verify all 6 `ICON` blocks + zero-load accessory calculation.

## 2026-11-25 - Phase 7.1: Field-Feedback Precision Upgrade (Gridded Legend Extraction, Nearest-Wire Ownership, Multi-Line Feeder Callout Parser & Exact Circuit Lock)

- **Multi-Column Gridded Legend Extractor & Strict Selection Isolation (`integrations/autocad_context_plugin/CadProjectLegendDb.cs`):**
  - Added grid-line column/row table extractor (`TryExtractGridTableLegend`, `CollectLegendGridSegmentsFromEntity`, `ClusterSortedAxisCoords`, `FindGridBandIndex`) to handle bordered multi-column Legend schedules with single-letter tag columns, fixture symbol columns, title + spec columns, `PCS` / `M/L` quantity columns (`IsLegendQtyCellText`), and right-side illustration/photo columns without quantity leaks.
  - Added block-name wattage fallback in `ParseSpecFromLegendDescription` (e.g. `upligh9w` -> `9W`) when description omits explicit wattage, and updated `ManageProjectLegendDb` (`action: 'extract_from_selection'`) to return `extracted_items` scoped strictly to the current selection while pruning stale non-user-verified catalog entries.
- **Nearest-Competing-Wire Fixture Ownership & Exact Subset Lock (`integrations/autocad_context_plugin/CadMepAndTakeoffTools.cs`):**
  - Added `HasCloserCompetingWireToBlockCenter` and `HasCloserCompetingWireToBlockCenterCached` so wires touching a fixture's outer tolerance yield to a competing electrical wire passing closer to the fixture center core (`skipped_closer_other_wire_count`), while protecting fixtures whose own wire already terminates cleanly inside/on the fixture bounding box.
  - Fixed `FindBlocksAlongPath` `lock_selection_as_circuit: true` when the engineer re-selects only kept fixture blocks (`selectionHadOnlyBlocks`) to hydrate and lock strictly against the existing circuit's `PathHandles` without re-expanding unrequested wires.
- **Rich Multi-Line Feeder/Circuit Callout Parser & Propagation (`CadMepAndTakeoffTools.cs`, `CadGuidedInventoryTools.cs`, `CadPanelAndQaTools.cs`, `DirectCadAgent.cs`):**
  - Added `ParsedCircuitCallout`, `ParseCircuitCalloutText`, and `ExtractSelectedCalloutText` to extract multi-line callouts such as `4X10mm2+6MM2 UPVC 50mm class4 / under F.F.L 60cm DB-LCP6(14,16,18) V.D / 0.54%` (grouped panel ways `[14, 16, 18]`, phase + earth conductor counts/sizes, conduit spec, depth note, and annotated VD%) both automatically via `Leader`/`MLeader` proximity and interactively when the engineer selects callout text alone (`callout_applied: true` and `guided_circuit_inventory(action: 'rename')`).
  - Propagated `PanelWays`, `CableDesignation`, `ConduitSpec`, `InstallNote`, and `AnnotatedVdPercent` across `SavedCircuitRecord`, `TracedCircuitSummary`, `GuidedInventoryItem`, and `generate_panel_schedule_and_sld` (automatically sizing >=3-way / >=3-core feeders as 3-phase `RYB` and splitting `(` in `ExtractPanelNameFallback`).
  - Updated `DirectCadAgent.cs` Rules `6k` & `6o` and added AcCore tests `TEST16A`–`TEST16D` in `tests/autocad_accore_harness/AcCoreRegressionVerifier.cs`.

## 2026-11-25 - Phase 7: AutoCAD Commercial Release Polish (XData Circuit Recovery, Full MV/LV + Landscape Master SLD, Interactive DIALux Review Dialog & Commercial Palette UI)

- **XData Circuit Tagging, Saved-Circuit Recall & Cross-PC Portability (`integrations/autocad_context_plugin/CadGuidedInventoryTools.cs`, `CadProjectLegendDb.cs`, `CadMepAndTakeoffTools.cs`):**
  - Stamped persistent `AIENG_CIRCUIT_TAG` XData (`W|circ|panel|disc`, `B|...`) onto approved and `inspect_selection(save_to_panel: true)` circuit wires & blocks while clearing excluded handles, so `COPY` / `Ctrl+C Ctrl+V` / `MOVE` preserve circuit identity.
  - Added `guided_circuit_inventory(action: "recall", ...)` (aliases `edit`, `edit_circuit`, `open`, `show`) to reopen any saved circuit directly inside the active wizard (recovering copied handles via ModelSpace `AIENG_CIRCUIT_TAG` scan when original handles were erased).
  - Updated `EnsureProjectLegendDbLoadedLocked` to compare `mtime` between `%LOCALAPPDATA%\AI-Engineer\project_db\...\project_database.json` and DWG-adjacent `.ai-engineer-project-db.json` (loading newest), updated `IsRecordFromCurrentDrawing` with `.dwg` filename fallback across moved PCs/folders, and added `dialux_rooms` persistence (`GetSavedDialuxRoomPrefs` / `SaveDialuxRoomPrefs`).
- **Full Electrical Distribution Network, Cable Routing, Landscape Feeders & Master SLD (`CadDistributionNetworkTools.cs`, `ExcelTableInterop.cs`, `CadToolExecutor.cs`, `build.ps1`):**
  - Added Tool #42 **`build_distribution_network_and_sld`** (`AUTOCAD_TOOLS=42`, `TEST15B`–`TEST15C`): builds full hierarchy (`RMU -> TX -> MDB -> GEN/ATS -> SMDB -> DB / Feeder Pillar / Landscape Pole`) from CAD cable routing (`drawing_routing`), project DB (`merged_project`), or `explicit` `nodes[]`, hydrates DB/Landscape panel loads from `project_database.json`, and enforces a strict zero-hallucination gate (`blocked_for_missing_loads: true`).
  - Runs deterministic IEC 60364 / IEC 60909 / ECP sizing (transformer kVA & loading %, MV XLPE/SWA cable, generator kVA, 4P ATS, feeder breakers/cables via `ComputeSingleLvCircuit`, buried landscape feeders, cumulative VD %, and bus Isc kA), draws `_AI_MASTER_SLD` + `_AI_FEEDER_SCHEDULE` with Undo, and exports a 4-sheet OpenXML `.xlsx` workbook via `ExcelTableInterop.WriteWorkbook` (`Master Summary`, `Feeder & VD Calc`, `Transformer & ATS`, `Landscape Feeders`).
- **Interactive DIALux Pre-Export Review Dialog & Multi-Luminaire `.stf` (`CadLightingAndDialuxTools.cs`):**
  - Added `#if !PANEL_MFG_STANDALONE` command **`AIENGDIALUX`** (`DialuxWizardCommands`) and interactive `DialuxExportReviewForm` (`RoomsGrid` + `LuminairesGrid`), plus `preview_missing_only`, `interactive_dialog`, `room_overrides[]`, `luminaire_overrides[]`, `save_to_project_db`, multi-luminaire `[LUMINAIRE.L1..Ln]` & per-room `Height` in `WriteDialuxStfFile`, and auto-scaled `ResolveEffectiveUnitsPerMeter` (`TEST15D`–`TEST15E`).
- **Commercial `AIENGPALETTE` UI & Regression Wiring (`PaletteCommands.cs`, `DirectCadAgent.cs`, `autocad_release_readiness.py`, `test_autocad_accore_suite.py`, `AcCoreRegressionVerifier.cs`):**
  - Redesigned `AIENGPALETTE` into a dark card layout (`_headerPanel` + blue `_headerAccent`, `_headerBadge`, `_configCard`, `_transcriptCard`, elevated `_inventoryBar`, `_composerCard` with 12 editable presets / 13 combo items), hiding Local-Bridge controls in Direct mode and showing `_commitDrawing`/`_rollbackDrawing` only when preview entities exist while preserving all `test_autocad_context.py` invariants.

## 2026-11-24 - Phase 6: Interactive Guided Circuit Inventory, Cross-DWG Lighting+Power Merge, Strict Legend Spec Gate & Minimal Palette UI

- **Interactive Guided Circuit-by-Circuit Inventory (`integrations/autocad_context_plugin/CadGuidedInventoryTools.cs`, `CadToolExecutor.cs`, `build.ps1`):**
  - Added `guided_circuit_inventory` tool (`AUTOCAD_TOOLS=41`, `AIENGINVENTORY` command in `Context.dll` only): scans panel-by-panel and numerically circuit-by-circuit (`1, 2, 5, 7, 10`, finishing lighting first when `discipline='all'` unless `mix_disciplines: true`), highlights + implied-selects + zooms onto one circuit at a time on `_AI_GUIDED_INVENTORY` and waits for engineer confirmation.
  - Supports interactive correction: `add_selection` (adds manual curves/blocks and auto-traces broken extensions forward), `remove_selection` (permanently excludes wrong elements via `ExcludedHandles` across `refresh`), `refresh`, `rename`, `approve_next` (aliases `ok`/`approve`: checks Legend specs, locks/saves, redraws stacked per-panel tables in numeric order, and advances), `skip`, `jump`, `stop`, on-demand `inspect_selection` (random ad-hoc circuit lookup mid-session without altering queue cursor), `audit_remaining` (coverage %, orphan fixtures, missing numeric sequence gaps), and `export_merged_panels` (merged Lighting+Power Panel Schedule + SLD + Excel).
- **Cross-Plan / Cross-DWG Numeric Merge & Live Reload (`integrations/autocad_context_plugin/CadMepAndTakeoffTools.cs`, `CadProjectLegendDb.cs`, `CadPanelAndQaTools.cs`):**
  - Extended `SavedCircuitRecord` (`Discipline`, `SourceDwgPath`, `Approved`, `MatchedBlocksCount`, `ExcludedHandles`), implemented natural numeric circuit/panel sorting (`CompareNaturalTokens`, `CompareCircuitKeysNatural`, `GetMergedSavedCircuitRecords`), and guarded current-drawing handles (`IsRecordFromCurrentDrawing`) so separate Lighting (`DB-1/1, 5, 7`) and Power (`DB-1/2, 3, 4, 6`) DWGs merge safely into unified numerically sorted Panel Schedules (`DB-1/1..DB-1/7`) via `project_database.json` and `.ai-engineer-project-db.json` with mtime-aware cross-DWG reload (`s_legendLoadedMtimeByDb`).
- **Strict Zero-Hallucination Legend Spec Gate (`CadProjectLegendDb.cs`, `CadMepAndTakeoffTools.cs`, `CadPanelAndQaTools.cs`, `DirectCadAgent.cs`):**
  - Added `strict_legend_specs` (`true` by default across `guided_circuit_inventory` and `inspect_selection`; optional on `find_blocks_along_path` and `generate_panel_schedule_and_sld` while preserving `40 VA` legacy fallback when unset): blocks without verified Legend specs yield `0 W / 0 VA`, `load_source: "pending_user_specs"`, `needs_user_specs: true`, and block final approval unless `allow_pending_specs: true`.
  - Updated `DirectCadAgent.cs` (`41` tools, Rules `6o`, `6r`, `9`, intent routing) and `AcCoreRegressionVerifier.cs` (`TEST14A`–`TEST14G`), `autocad_release_readiness.py`, and `test_autocad_accore_suite.py`.
- **De-Cluttered Single-Dropdown `AIENGPALETTE` UI (`integrations/autocad_context_plugin/PaletteCommands.cs`):**
  - Removed `_coordSortLabel`/`_coordSortCombo`/`_sortRow` and all 7 quick-shortcut buttons (`_quickBar`, `_qVisual`..`_qQa`), replacing them with one compact labeled editable-draft command dropdown (`_commandPresetCombo`, 10 drafts that populate `_message.Text` without auto-sending), a hidden-by-default contextual `_inventoryBar`, and instant single-word chat routing (`اوكي`/`ok`/`اضف`/`شيل`/`تحديث`/`تخطي`/`انهاء`) when an inventory session is active.

## 2026-11-24 - Phase 5.2 Post-Review Hotfixes (Multi-Run Busbar Sizing, Interactive Panel Undo, Fresh-Clone Bootstrap, Standalone DLL Cleanup & Docs/CI)

- **Multi-Run DIN 43671 Busbar Sizing (`integrations/autocad_context_plugin/CadPanelManufacturingTools.cs`, `data/switchgear_3d_catalog.json`):**
  - Updated `MatchNearestStandardBusbar` to parse optional parallel-run syntax (`2x60x10`, `2x(80x10)`, `2x100x10`) before single-run dimensions so parallel Cu busbars never fall back to small single-run bars (`25x5 Cu`).
  - Added `100x10 Cu` (`1500A`) and `2x(100x10) Cu` (`2500A`) to both `data/switchgear_3d_catalog.json` and `BuildDefaultCatalog()` so every preset in the `AIENGPANEL` busbar dropdown resolves cleanly.
- **Full Interactive Undo & Modeless Document Locking (`integrations/autocad_context_plugin/CadToolExecutor.cs`, `CadPanelManufacturingTools.cs`):**
  - Extended `ModifiedEntitySnapshot` and `UndoLastAction` to capture and restore `Polyline.ConstantWidth` and `XData` (`PanelBusbarRegApp`), and wired `PushUndoEntry` into `ExecuteSnapAndReroutePanel`, `ExecuteResizeStandardBusbar`, `ExecuteTracePanelConnections`, and `ExecuteSldFromPanel`.
  - Wrapped `PanelManufacturingPaletteControl.RefreshPanelStatus()` in `doc.LockDocument()` to prevent modeless `eLockViolation`, and guarded `FormatPanelActionSummary` dictionary lookups via `SafeVal`.
- **Clean Standalone DLL & Full AI Schema (`integrations/autocad_context_plugin/DrawingExecutionCommands.cs`, `DirectCadAgent.cs`):**
  - Guarded `DrawingExecutionCommands` `[assembly: CommandClass]` and `[CommandMethod]` attributes under `#if !PANEL_MFG_STANDALONE` so `AIEngineer.AutoCAD.PanelManufacturing.dll` exports zero duplicate floor-plan/preview commands when loaded alongside `AIEngineer.AutoCAD.Context.dll`.
  - Synced all 8 `view_preset` modes (`full_assembly`, `busbars_only`, `wiring_uncovered`, `no_door`, `cnc_drilling_only`, `components_only`, `2d_only`, `3d_only`) and extra routing keywords in `DirectCadAgent.cs`.
- **Fresh-Clone Bootstrap, Readiness, Docs & CI (`.gitignore`, `core/package_manager.py`, `launcher.py`, `core/autocad_release_readiness.py`, `SETUP-AI-ENGINEER.ps1`, `.github/workflows/ci.yml`, `README.md`, `AUTOCAD_AI_TOOLS_REFERENCE_AR.md`, `package_manifest.json`):**
  - Updated `.gitignore` to track `data/switchgear_3d_catalog.json` while ignoring runtime state files, added `ensure_runtime_data_files` called by `launcher.py check` so fresh clones bootstrap schema-v1 `data/*.json` cleanly, included `data/switchgear_3d_catalog.json` in `PackageBuilder`, expanded `core/autocad_release_readiness.py` (`2021–2026` + Phase 1–5 files), updated `SETUP-AI-ENGINEER.ps1` and `AUTOCAD_AI_TOOLS_REFERENCE_AR.md` (all 40 tools + CLI commands), and added `.github/workflows/ci.yml`.

## 2026-11-24 - Phase 5.1 Zero-Collision Z-Depth Stack-Up, Magnetic DIN-Rail Auto-Snap, Wire Tracer & Bidirectional SLD ↔ Panel Sync

- **8 Ordered Z-Depth 3D Layers, Side Cross-Section & CNC Drilling (`integrations/autocad_context_plugin/CadPanelManufacturingTools.cs`):**
  - Replaced flat 3D layering with physical back-to-front depth layers (`_AI_3D_00_ENCLOSURE`, `_AI_3D_01_MOUNTING_PLATE`, `_AI_3D_02_RAILS_INSULATORS`, `_AI_3D_03_DUCTS_BODY`, `_AI_3D_03_DUCTS_COVER`, `_AI_3D_04_COMPONENTS`, `_AI_3D_05_BUSBARS_COPPER`, `_AI_3D_06_WIRING`, `_AI_3D_07_DOOR_DEADFRONT`) so duct covers, cabinet doors, copper busbars, or wires toggle cleanly (`mode: "set_view_preset"`).
  - Added 2D Side Cross-Section View (`_AI_PANEL_SIDE_SECTION`) displaying Z-depth stacking and `min_door_clearance_mm`, plus workshop CNC Mounting Plate drill templates (`_AI_CNC_DRILLING` with M4/M5/M6 mounting hole coordinates) and `AIENG_PANEL_RAIL` alignment guides (`_AI_GUIDE_ALIGNMENT`).
- **Anti-Hallucination Collision Solver, Exact Terminal Droppers & Comb Busbars:**
  - Enforced `VerifyAndResolvePanelCollisions` (checking component-component, component-busbar, boundary margins, and door clearance), exact per-phase `QFMAIN` Z-bend copper droppers (`DROP-R/Y/B`) with bolt joints (`BoltJointsMm`), 1P/3P `Comb Busbars` (`use_comb_busbar: true`), series device chains (`connected_to`), and IEC 61439 wire duct fill-ratio checking/upsizing (`max_duct_fill_percent`).
- **Interactive Panel Editing, Tracer, Standard Busbar Sizer & Bidirectional SLD Sync (`PanelManufacturingCommands`, `PanelManufacturingPaletteControl`):**
  - Added dedicated standalone command **`AIENGPANEL`** (`PANELAI` / `AIENGPANELMFG`) opening a dedicated Copper/Dark-Theme `PaletteSet` (`"AI-Engineer — Panel Manufacturing 2D/3D"`, separated cleanly from the MEP Engineering Drawing palette **`AIENGPALETTE`**) featuring one-click fabrication buttons (Magnetic DIN-Rail Snap `snap_and_reroute` / `AIENGPANELSNAP`, DIN 43671 Cu Standard Busbar Sizer `resize_busbar` / `AIENGBUSBARSIZE`, Interactive Netlist Wire/Device Tracer `trace_connections` / `AIENGWIRETRACE`, `2D -> 3D` rebuild, Bidirectional `SLD <-> Panel` sync `AIENGPANELSLD`, and all 8 Z-depth View Presets `full_assembly`, `busbars_only`, `wiring_uncovered`, `no_door`, `cnc_drilling_only`, `components_only`, `2d_only`, `3d_only` via `AIENGPANELVIEW`).
  - Updated `integrations/autocad_context_plugin/build.ps1` to also emit a standalone `NETLOAD`-able **`AIEngineer.AutoCAD.PanelManufacturing.dll`** alongside `AIEngineer.AutoCAD.Context.dll`, using distinct `EdCommandStack` command groups (`AIENG_PANELMFG_DLL` vs `AIENG_PANELMFG_CTX`) so both can be loaded together without command collisions.
  - Updated `DirectCadAgent.cs` Rule `6q`, intent triggers, and schema, plus `TEST13A`–`TEST13G` in `AcCoreRegressionVerifier.cs`.

## 2026-11-23 - Phase 5 Industrial Switchgear & 2D/3D Panel Manufacturing Engine

- **Switchgear & 3D Component Catalog (`data/switchgear_3d_catalog.json`):**
  - Added catalog specifications for Schneider Electric (`LV429630`, `LV431630`, `LV432693`, `NW08H1`, `A9F74110..A9F74363`, `LC1D18M7/32M7`, `ATV320U22N4C`, `ABL8REM24050`, `NSYTRV42/162`), ABB (`XT1N160`, `S201-C16`, `S203-C32`), and Siemens (`3VA1110`, `5SY4116-7`, `5SY4332-7`), plus IEC 61439 / DIN 43671 flat Cu busbars (`15x3` .. `2x(80x10)` mm), enclosures (`NSYCRN`, `AE`, `NSYSF`, `VX25`), and ClimaSys filter fans (`38..300 m3/h`).
- **2D/3D Panel Manufacturing Engine (`integrations/autocad_context_plugin/CadPanelManufacturingTools.cs`, `build.ps1`):**
  - Added `design_panel_manufacturing_layout` (`mode`: `auto_2d_and_3d`, `2d_only`, `3d_from_2d`) implementing IEC 61439-1 / IEC 60890 thermal temperature rise calculation + automatic filter fan sizing, Cu `R/Y/B/N/PE` busbar sizing, and thermal/ergonomic zoning (`cable_entry`: `bottom` | `top`, top-zone VFD convection exhaust, modular DIN rails + slotted ducts + spare reserve, incomer arc clearance, and `XT1` terminal strip).
  - Draws 2D panel General Arrangement blocks on `_AI_PANEL_2D_LAYOUT` with AutoCAD Electrical (`ACADE`) attributes (`TAG1`, `MFG`, `CAT`, `RATING1`, `LOC`, `INST`, `TERM01`, `TERM02`, `WIRENO`) + `AIENG_PANEL_COMP` XData, constructs 3D `Solid3d` enclosures/rails/ducts/busbars/components/routed 3D wiring across `_AI_3D_*` layers (importing `<part_number>.dwg` from the local/TraceParts 3D vault when present or generating parametric 3D solids), converts live user-edited 2D layouts to 3D (`3d_from_2d`), and exports RTL Excel `.xlsx` BOM + From–To Wire & Ferrule cut-list schedules.
- **Agent Wiring & Regression (`CadToolExecutor.cs`, `DirectCadAgent.cs`, `AcCoreRegressionVerifier.cs`, `run_autocad_accore_tests.ps1`, `test_autocad_accore_suite.py`):**
  - Registered `design_panel_manufacturing_layout` (`AUTOCAD_TOOLS=40`), Rule `6q`, intent triggers, `TEST13A`/`TEST13B` in `AcCoreRegressionVerifier.cs`, and `CadPanelManufacturingTools.cs` build check in `test_autocad_accore_suite.py`.

## 2026-11-23 - Phase 4.1 Post-Phase-4 Audit Hotfixes & Revit LV Parity

- **Per-Database Cache Eviction & Hook Registration (`integrations/autocad_context_plugin/CadToolExecutor.cs`, `CadMepAndTakeoffTools.cs`, `CadProjectLegendDb.cs`):**
  - Extended `OnDocumentToBeDestroyed` to evict closing `Database.UnmanagedObject` keys from all per-database static caches across partial class files (`_undoStacks`, `s_last*` coordinate/table/selection caches, `s_circuitProfileByDb`, `s_savedCircuitsByDb`, `s_lastActiveCircuitNameByDb`, `s_loadedSidecarForDb`, `s_legendCatalogByDb`, and `s_legendLoadedForDb`) without nested locking.
  - Added `EnsureDocDestroyedHook()` calls inside `EnsureProjectLegendDbLoadedLocked`, `EnsureSidecarLoadedLocked`, `LearnCircuitSampleProfile`, and `RecordUserSelectionSnapshot` so cleanup stays armed regardless of which tool runs first.
- **Unbalanced Panel Main Feeder Protection (`integrations/autocad_context_plugin/CadPanelAndQaTools.cs`):**
  - Updated `BuildBalancedPanelSchedule` to size `panel.MainFeeder` conservatively using `Math.Max(panel.DemandKva, maxPhaseEquivalent3PKva)` (`3 * heaviest_phase_kVA * demand_factor`) so heavy single-phase imbalance cannot undersize the main breaker or feeder cable, while keeping `ConnectedKva`, `DemandKva`, `ConnectedKw`, and `DemandKw` panel totals exact.
  - Added `imbalance_warning` to `GeneratePanelScheduleAndSld` output and `panel.MainFeeder.Notes` whenever `ImbalancePercent > 15.0%`.
- **Visual Capture Path Hardening (`integrations/autocad_context_plugin/ExcelTableInterop.cs`, `CadVisualCaptureTools.cs`):**
  - Extracted shared `ExcelTableInterop.ResolveSafeOutputPath` helper reusing `IsProtectedSystemDirectoryPath` and `ResolveWritableExportDirectory`, and wired `CaptureVisualView` `save_path` handling through it so relative paths and Windows/`Program Files` system paths redirect safely and enforce `.png`.
- **Portable AutoCAD AcCore Test Runner (`scripts/run_autocad_accore_tests.ps1`, `tests/test_autocad_accore_suite.py`):**
  - Replaced hardcoded AutoCAD 2021 paths with `AUTOCAD_ROOT` override + automatic AutoCAD `2015–2030` `accoreconsole.exe` discovery and added `/codepage:65001` UTF-8 flag to `csc.exe` harness compilation.
- **Revit Direct AI LV Calculation Parity (`integrations/revit_direct_plugin/RevitLvCalculationsTools.cs`, `DirectRevitAgent.cs`):**
  - Synced `lighting`/`socket` (English + Arabic alias) `1P / 220V` default rules, `fixture_count` + `fixture_watt`/`fixture_va` deterministic load inputs, and 3-decimal `load_kw`/`load_kva` rounding (`2`-decimal `length_m`) plus schema fields in `BuildToolCatalog`.

## 2026-11-23 - Phase 4 High-Resolution Vector Visual Capture & Multimodal Vision Loop

- **Vector Visual Capture Tool (`integrations/autocad_context_plugin/CadVisualCaptureTools.cs`, `CadToolExecutor.cs`, `build.ps1`):**
  - Added `capture_cad_view` (`CaptureVisualView`) GDI+ vector capture engine supporting `auto`, `viewport`, `extents`, `selection`, and `bbox` scopes with layer visibility filtering, curve/arc/polyline/hatch/text rendering, anti-overlap block badge annotations, and rolling PNG capture output under `%LOCALAPPDATA%\AI-Engineer\visual_captures`.
- **Multimodal Vision Feedback (`integrations/autocad_context_plugin/DirectCadAgent.cs`):**
  - Wired initial viewport/extents PNG pre-capture, `ExtractAndStripVisionImage`, `BuildGeminiCandidates` `inlineData` attachment, action-tool classification, and visual routing triggers (`AUTOCAD_TOOLS=39|SCHEMA_ERRORS=0`).
- **AcCore Visual Regression Tests (`tests/autocad_accore_harness/AcCoreRegressionVerifier.cs`):**
  - Added `TEST12A`–`TEST12C` verifying vector capture extents, bbox targeting, selection highlights, and custom PNG `save_path` output.

## 2026-11-23 - Phase 3 Persistent Project Legend Spec Database, Per-Panel Stacked Circuit Board & Saved-Memory Panel Schedules

- **Persistent Project Legend Specification Database (`integrations/autocad_context_plugin/CadProjectLegendDb.cs`, `build.ps1`):**
  - Added `manage_project_legend_db` tool (`action`: `get_catalog`, `extract_from_drawing`, `extract_from_selection`, `set_item`, `set_items`, `export_excel`, `import_excel`, `clear_catalog`) with deterministic regex parsing for Watts (`45 WATT`, `22W`), compound wattages (`2x18W`), kW/VA/kVA, voltage (`220/230/380V`), emergency flags, and top-to-bottom `"AS ABOVE"` spec inheritance across native AutoCAD `Table`, loose Legend columns, and nested Legend blocks.
  - Persists unified `project_database.json` (`legend_catalog` + `circuits`) under `%LOCALAPPDATA%\AI-Engineer\project_db\<project_slug>_<hash>\`, writable DWG folder mirror `.ai-engineer-project-db.json`, and `%LOCALAPPDATA%\AI-Engineer\project_db\projects_index.json` across AutoCAD/PC restarts (strictly scoped to saved `.dwg` files, excluding `.dwt` AutoCAD templates in both `ResolveProjectDbPaths` and `ResolveCircuitMemorySidecarPath`).
- **Mixed-Fixture Auto-Lookup & Per-Panel Stacked Circuit Table Board (`integrations/autocad_context_plugin/CadToolExecutor.cs`, `CadMepAndTakeoffTools.cs`):**
  - Wired `ComputeCircuitLoadFromProjectDb` into `FinalizeCircuitPathOutput` (`find_blocks_along_path`) so mixed-fixture circuits (`2x 60x60E + 1x APPE`) automatically sum exact per-block catalog Wattages (`load_source: "project_legend_db"`, transparent missing-block warnings).
  - Upgraded `DrawBlockLegend` with `AIENG_CIRC_TBL` XData tagging and `stack_by_panel` layout: circuits sharing a `PANEL` stack vertically underneath one column, each new panel opens a clean column to the right, and re-running a circuit updates only its own table cleanly in-place.
- **Saved-Memory Panel Schedule + Catalog Enrichment (`integrations/autocad_context_plugin/CadPanelAndQaTools.cs`, `DirectCadAgent.cs`):**
  - Enriched `BuildBalancedPanelSchedule` with `ComputeCircuitLoadFromProjectDb` and added `source: "saved_memory"` support to `GeneratePanelScheduleAndSld` plus `connected_kw` / `demand_kw` panel totals.
  - Registered `manage_project_legend_db` (`AUTOCAD_TOOLS=38|SCHEMA_ERRORS=0`), intent router triggers, and anti-hallucination Rule `6o`.
- **Verification:**
  - Built `AIEngineer.AutoCAD.Context.dll` (`1,245,184` bytes) and passed AutoCAD 2021 `accoreconsole.exe` regression `Tests 1–11` (`TEST11A`: 4-row Legend extraction + `"AS ABOVE"` inheritance; `TEST11B`: mixed circuit `LP-DB1/1` = `0.112 kW`; `TEST11C`: per-panel stacking + new panel column + in-place refresh; `TEST11D`: `saved_memory` Panel Schedule + SLD `LP-DB1` = `0.202 kW`; `BENCHMARK_30C_300F_MS=53`; `ACCORE_REGRESSION_PASS`).

## 2026-11-23 - Phase 2.3 Field-Feedback Hotfix (`INSUNITS=Millimeters` Scale Inference, Strict Crossing Bridges & `merge_with_saved` Branch Tracing)

- **Meter-in-Millimeters Scale Inference (`integrations/autocad_context_plugin/CadMepAndTakeoffTools.cs`):**
  - Updated `ResolveEffectiveUnitsPerMeter` so drawings authored in Meters (`medianFixtureSpan` `0.08..2.5` units) are automatically detected and corrected to `1.0` units/m even when `INSUNITS` is mistakenly set to `Millimeters`, `Centimeters`, `Inches`, `Feet`, or `Undefined` (similarly covering Centimeters and Millimeters mismatches without false positives on real Inch troffers).
  - Updated `ScanCircuitSeedEntities` to pass inferred `seedUnitsPerM` into `SeedCurvesFromSelectedBlocks` and `LearnCircuitSampleProfile`.
- **Anti-Leak Crossing Bridges & Gap Tightening (`integrations/autocad_context_plugin/CadSpatialIndex.cs`, `CadMepAndTakeoffTools.cs`):**
  - Hardened `HasCrossingObstacleInGap` to require a strictly transverse obstacle (`|dot| < 0.65`) and interior intersection (`0.08 <= t <= 0.92`) with capped midpoint tolerance, preventing parallel architectural/parking lines from bridging distant circuits.
  - Filtered architectural layers out of `obstacleSegments` in `CollectChainTraceCandidates`, raised Pass 3 collinearity dot threshold to `0.88`, and tightened `breakGapMax` (`0.45m` cap), `crossingGapMax` (`1.85m` cap), `crossingLateralTol` (`0.22m` cap), and `obsTol` (`0.22m` cap) in `ExpandConnectedPathCurvesCore`.
- **Block+Wire `merge_with_saved` Branch Expansion (`integrations/autocad_context_plugin/CadMepAndTakeoffTools.cs`):**
  - Updated `merge_with_saved` in `FindBlocksAlongPath` to isolate newly selected wires/blocks relative to `savedRec`, seed touching wires when only new blocks are selected, and run full `ExpandConnectedPathCurves` + `MatchBlocksAlongCircuitPath` on new branches even when fixture blocks are also selected.
- **Verification:**
  - Built `AIEngineer.AutoCAD.Context.dll` (`1,198,080` bytes) and passed AutoCAD 2021 `accoreconsole.exe` regression `Tests 1–10` (`AUTOCAD_TOOLS=37|SCHEMA_ERRORS=0`, `TEST10A` Meter-in-Millimeters `LP-MM/1` isolated `3` fixtures / `6.5m` / `VD=0.05%` across `1.0m` crossing bridge without jumping `2.4m` + parallel wire, `TEST10B` wire+block `merge_with_saved` = `5` fixtures / `11.4m`, `BENCHMARK_30C_300F_MS=321`, `ACCORE_REGRESSION_PASS`).

## 2026-11-23 - Phase 2.2 Deterministic Lighting Load Calculations & Circuit Legend Electrical Columns

- **Deterministic Fixture Load & Smart Default Phase Rules (`integrations/autocad_context_plugin/CadLvCalculationsTools.cs`):**
  - Updated `ComputeSingleLvCircuit` so `load_type` normalizes English/Arabic aliases (`lighting`/`إنارة`, `socket`/`برايز`/`أفياش`), defaulting single-phase `1P / 220V` automatically for `lighting` and `socket` circuits while keeping `3P / 380V` for `power`/`motor`/`feeder`.
  - Added deterministic `fixture_count` + `fixture_watt` / `fixture_va` inputs to `ComputeSingleLvCircuit` (with 3-decimal `load_kw`/`load_kva` and 2-decimal `length_m` precision), eliminating LLM mental-math guessing of lighting circuit currents.
- **Total Load & Max Voltage Drop Columns Directly on Circuit Legend + Excel (`integrations/autocad_context_plugin/CadToolExecutor.cs`, `CadMepAndTakeoffTools.cs`):**
  - Extended `DrawBlockLegend` with optional `include_electrical_columns`, rendering 7-column bilingual tables (`الحمل الكلي` and `أقصى هبوط جهد VD%`) and exporting matching columns to `_AI_BLOCK_LEGEND` Excel workbooks while keeping 5-column legends unchanged when disabled.
  - Wired `include_electrical_calc`, `fixture_watt`, `fixture_va`, `phases`, `voltage_v`, `power_factor`, and `max_vd_percent` inside `FinalizeCircuitPathOutput` (`find_blocks_along_path`) with transparent `load_assumption_warning` when `40 VA/fixture` default is assumed.
- **Anti-Hallucination Prompt & Schema Updates (`integrations/autocad_context_plugin/DirectCadAgent.cs`):**
  - Updated `calculate_lv_electrical` and `find_blocks_along_path` schemas and Rule `6k` (`Pattern H`) strictly forbidding fabricated lighting currents/wattages.
- **Verification:**
  - Built `AIEngineer.AutoCAD.Context.dll` (`1,197,568` bytes) and passed AutoCAD 2021 `accoreconsole.exe` regression Tests 1–9 (`AUTOCAD_TOOLS=37|SCHEMA_ERRORS=0`, `TEST9A` `ELP-B3.2/6` `7 x 40W` @ `85.31m` = `1P 220V`, `0.28 kW`, `0.311 kVA`, `1.41 A`, `VD=1.53%`, `PASS`; `TEST9B` `LP-B3.2/1` 7-column `_AI_BLOCK_LEGEND` + `electrical_calc` + Excel export; `BENCHMARK_30C_300F_MS=281`; `ACCORE_REGRESSION_PASS`).

## 2026-11-23 - Phase 2.1 Field-Feedback Hotfix (Crossing Bridges, Circuit Memory, Table Collision, Export Path & Model Auto-Upgrade)

- **Crossing-Bridge Wire Tracing (`integrations/autocad_context_plugin/CadSpatialIndex.cs`, `CadMepAndTakeoffTools.cs`):**
  - Added `CrossingObstacleSegment`, geometric intersection/clearance helpers (`SegmentsIntersect2D`, `TryCreateObstacleSegment`, `HasCrossingObstacleInGap`), and Pass 3 verified crossing-bridge jumps across `IsCollinearBreakBridgeMatch`, `CollectChainTraceCandidates`, `EnqueueConnectedCurvesAtEndpoints`, `ExpandConnectedPathCurvesCore`, and `ScanAllCircuitsInternal` so circuits stay connected across perpendicular jumper/bridge gaps up to `1.80m` (and never jump empty space or stray lines).
- **Persistent Circuit Memory & AI-Table Selection Guard (`integrations/autocad_context_plugin/CadMepAndTakeoffTools.cs`, `DirectCadAgent.cs`):**
  - Added per-drawing and persistent JSON sidecar (`%LOCALAPPDATA%\AI-Engineer\circuit_memory\`) circuit registry (`SavedCircuitRecord`) with `circuit_name` recall, `merge_with_saved`, `lock_selection_as_circuit`, and `overwrite_locked` preservation during batch `trace_all_circuits`.
  - Added `skippedAiLayerCount` guard in `ScanCircuitSeedEntities` so accidentally selecting an `_AI_*` legend/schedule row never hijacks tracing, falling back transparently to `circuit_name` memory or clean guidance.
- **Zero-Overlap AI Table Placement & Writable Excel Exports (`integrations/autocad_context_plugin/CadToolExecutor.cs`, `ExcelTableInterop.cs`):**
  - Updated `DrawBlockLegend` and `RenderStyledScheduleTable` to check all AI schedule/diagram layers (`_AI_COORD_TABLE`, `_AI_CIRCUIT_SCHEDULE`, `_AI_PANEL_SCHEDULE`, `_AI_SLD`, `_AI_ELECTRICAL_QA`, `_AI_LV_CALC_TABLE`, `_AI_BOQ_TABLE`, `_AI_EXCEL_TABLE`, `_AI_LIGHTING_TABLE`, `_AI_BLOCK_LEGEND`) and stack non-overlapping vertical bands.
  - Updated `ExcelTableInterop.WriteSpreadsheet` to redirect relative or protected (`Program Files` / `Windows`) output paths automatically to writable `Desktop\AI-Engineer-Exports`.
- **Automatic Gemini Config Migration (`integrations/autocad_context_plugin/DirectCadAgent.cs`):**
  - Added one-time `ModelMigratedV2` (`model_migrated_v2`) migration in `DirectAiConfig.Load()` so existing saved configs using deprecated `gemini-3.5-flash-lite` auto-upgrade to `gemini-3.5-flash` while keeping user API keys intact.
- **Verification:**
  - Removed duplicate `PointToSegmentDist2D` in `CadSpatialIndex.cs`, compiled `AIEngineer.AutoCAD.Context.dll` (`1,186,816` bytes), and passed AutoCAD 2021 `accoreconsole.exe` regression Tests 1–8 (`AUTOCAD_TOOLS=37|SCHEMA_ERRORS=0`, `1.40m` crossing bridge `LP-CROSS/1`, `LP-B3.2/2` memory recall over `_AI_CIRCUIT_SCHEDULE` selection + merge + user lock, zero Y-overlap between `_AI_CIRCUIT_SCHEDULE` and `_AI_BLOCK_LEGEND`, relative Excel redirect to `AI-Engineer-Exports`, `291 ms` 30-circuit/300-fixture benchmark, `ACCORE_REGRESSION_PASS`).

## 2026-11-23 - Phase 2 Consulting Panel/SLD Generator, Electrical QA Auditor, Spatial Grid & AutoCAD Regression Suite

- **2D Spatial Grid & Block Geometry Cache (`integrations/autocad_context_plugin/CadSpatialIndex.cs`, `CadMepAndTakeoffTools.cs`):**
  - Added uniform spatial hash index `SpatialGrid2D<T>` (with cell deduplication stamps and oversized-bounds handling) and single-pass `DrawingBlockGeometryCache` (`FixtureGrid`, `TerminatorGrid`, `TextTagGrid`).
  - Upgraded `ExpandConnectedPathCurves` and refactored batch circuit discovery into `ScanAllCircuitsInternal` / `TraceSingleCircuitComponentCached`, reducing circuit tracing and block matching from $O(N^2)$ ModelSpace scans to $O(N)$ spatial lookups (`46 ms` for 30 circuits / 300 fixtures in live AutoCAD 2021).
- **Panel Schedule + Vector SLD Generator & Consulting Electrical QA Auditor (`integrations/autocad_context_plugin/CadPanelAndQaTools.cs`, `CadToolExecutor.cs`, `DirectCadAgent.cs`):**
  - Added `generate_panel_schedule_and_sld`: groups circuits by panel, computes IEC 60364 / ECP branch and main incomer sizing via `ComputeSingleLvCircuit`, balances 1-phase circuits across `R/Y/B` by LPT (`Imbalance %`), adds spare ways, renders bilingual tables on `_AI_PANEL_SCHEDULE` and crisp vector busbar/breaker/feeder SLDs on `_AI_SLD`, exports RTL Excel `.xlsx`, and pushes unified Undo entries.
  - Added `audit_electrical_drawing`: detects disconnected `ORPHAN_FIXTURE`, `OVERLOADED_CIRCUIT`, `DUPLICATE_CIRCUIT_NAME`, `VOLTAGE_DROP_EXCEEDED` (with IEC cable upsizing recommendation), `UNNAMED_CIRCUIT`, and `UNIT_SCALE_MISMATCH`, computing `health_score` (0..100), severity-colored markers & report tables on `_AI_ELECTRICAL_QA`, and optional Excel `.xlsx` reports.
  - Fixed session Undo persistence in `CadToolExecutor.cs` by keying `_undoStacks` on `Database.UnmanagedObject` (`IntPtr`) instead of managed wrapper identity so `undo_last_action` works consistently across AutoCAD RCW wrapper lifetimes and `accoreconsole.exe`.
  - Registered both tools (`AUTOCAD_TOOLS=37`, `SCHEMA_ERRORS=0`), intent routing in `SelectRelevantToolSpecs`, concise prompts (`6m`, `6n`), and compiled via `integrations/autocad_context_plugin/build.ps1`.
- **Automated AutoCAD Regression Harness (`tests/autocad_accore_harness/AcCoreRegressionVerifier.cs`, `scripts/run_autocad_accore_tests.ps1`, `tests/test_autocad_accore_suite.py`):**
  - Verified live inside AutoCAD 2021 `accoreconsole.exe`: schema/router/JIT validation (`16` methods), Meter-in-Inches circuit tracing (`6.15m`, strict `learn_sample`), Panel Schedule + SLD + LPT R/Y/B balance (`R=160, Y=120, B=80`) + Undo (`206` entities reverted), seeded Electrical QA defect detection (`FAIL`, score `33`), and 30-circuit / 300-fixture spatial benchmark (`46 ms`, `ACCORE_REGRESSION_PASS`).

## 2026-11-23 - Phase 1 Precision Circuit Tracing, Intent Tool Routing & Gemini 3.5 Flash Upgrade

- **Circuit Tracing Accuracy & Scale Inference (`integrations/autocad_context_plugin/CadMepAndTakeoffTools.cs`):**
  - Added `ResolveEffectiveUnitsPerMeter` to infer actual drawing scale (`1.0` units/m on Meter drawings mistagged with `INSUNITS=Inches`) from fixture bounding spans when `INSUNITS` reports imperial/cm/mm mismatch, emitting a bilingual Arabic/English `unit_scale_warning` and accurate `total_path_length_m`.
  - Fixed `SeedCurvesFromSelectedBlocks` (`seedTol`), tightened `ExpandConnectedPathCurves` collinear/crossing gap thresholds (`breakGapMax <= 0.90 * medianFixtureSpan`, `breakLateralTol`, `tightSnapTol`), enforced strict `max_circuit_blocks` (default `35`) circuit boundary sanity guard, and scaled `tagSearchRadius` proportional to fixture span (`refSpan * 4.5`) so nearby circuits never merge into 263-block clusters.
  - Enforced strict `learn_sample` locking (`effectiveFollowConnected = false` on selected sample circuits) and added `TraceAllCircuits` (`trace_all_circuits`) batch detector/scheduler generating `_AI_CIRCUIT_SCHEDULE` in AutoCAD and optional RTL Excel workbooks.
- **Intent Tool Router & Model Upgrade (`integrations/autocad_context_plugin/DirectCadAgent.cs`, `CadToolExecutor.cs`):**
  - Added `SelectRelevantToolSpecs` Intent-Based Tool Router across Gemini, Claude, OpenAI Responses, and OpenAI Chat loops (cutting 35 AutoCAD tools down to a focused task subset, e.g. 15 circuit/takeoff tools), registered `trace_all_circuits` and `max_circuit_blocks`, and upgraded the default C# Gemini model from `gemini-3.5-flash-lite` to `gemini-3.5-flash`.
- **Verification:**
  - Verified clean C# build against AutoCAD 2021 (`AIEngineer.AutoCAD.Context.dll`, `1,100,288` bytes, `35` tools registered with `0` tool/type/array-`items` schema errors; 4 pre-existing `modify_entities` object properties omit optional `description`) and passed live `accoreconsole.exe` tests on a Meter drawing mistagged as `INSUNITS=Inches` (single circuit `LP-B3.2/1` = 3 `F1` / `6.15m`, strict `learn_sample` lock = 3 `F1` / `4.8m`, and `trace_all_circuits` = 3 circuits / 9 blocks / `20.15m` / `_AI_CIRCUIT_SCHEDULE`).

## 2026-11-23 - Audited Multi-Version AutoCAD + Revit Direct Copilot, IEC/ECP LV Calculator & DIALux STFF 1.0.5 Release

- Applied the authoritative audited `AI-Engineer-AutoCAD-fixes` bundle (36 files updated/added, 6 obsolete files removed: `agent/scheduler.py`, `integrations/browser.py`, `integrations/excel.py`, and legacy `0.1.3`–`0.1.5` `.vsix` bundles).
- **AutoCAD Direct Plugin (`integrations/autocad_context_plugin/`):**
  - Added deterministic IEC 60364 / ECP LV calculator (`CadLvCalculationsTools.cs` / `calculate_lv_electrical`), single-tab Dark-Theme `AIENGPALETTE` + hidden `AIENGLEGACY` + shared `BridgeHttpClientFactory` (`PaletteCommands.cs`), canonical DIALux STFF 1.0.5 writer (`CadLightingAndDialuxTools.cs`), DPAPI API-key protection + shared `HttpClient` + strengthened Anti-Hallucination guards (`DirectCadAgent.cs`), deadlock-free WinMM voice recorder (`VoiceAssistant.cs`), and multi-version `2015–2030` `build.ps1` / `DwgVersion.Current` / reflection `TrySetFocusToDwgView` compatibility.
  - Fixed OpenAPI 3.0 array `items` schema on `calculate_lv_electrical.circuits` in `DirectCadAgent.cs` (`34` tools, `0` schema errors), rebuilt `AIEngineer.AutoCAD.Context.dll` against AutoCAD 2021, and deployed to `ProgramData` and `%APPDATA%` bundles.
- **Universal Multi-Version Revit Direct Plugin (`integrations/revit_direct_plugin/`):**
  - Added `SimpleJson.cs`, `RevitCompat.cs` (`2015–2026+`, `.NET 4.x` + `net8.0-windows`), `RevitDirectConfigAndVoice.cs`, `RevitLvCalculationsTools.cs`, `RevitLightingAndDialuxTools.cs`, `RevitToolExecutor.cs`, `DirectRevitAgent.cs`, `RevitDockablePaneAndApp.cs`, `build.ps1`, and `.addin.template`.
- **Python Core & Tests (`core/`, `integrations/`, `tests/`):**
  - Fixed `ContextLayer.to_dict()` indentation in `integrations/autocad_context.py`, `copilot_instructions(request)` + no-redirect TLS handling in `integrations/claude_provider.py`, vendor/temperature matching in `core/spec_matcher.py`, offline fallback/header auth/word-boundary regexes in `integrations/cad_geometry_engine.py`, clean-checkout runtime defaults in `core/package_manager.py`, and aligned `tests/test_autocad_context.py` and `tests/test_claude_provider.py` (`create_egress_field` + `BridgeHttpClientFactory.GetSharedClient(210)`).

## 2026-11-01 - School V5 family and power-mark read-only foundation

- Captured the exact loaded Revit family-type inventory: eight lighting types,
  two outlet types, zero switch types and zero panel types. Audited eight installed
  Autodesk stock RFA files without loading them into the project; 220 V receptacle
  is a candidate, while the sampled 120/277 V lighting/switch types and 208 V panel
  are not accepted for the adopted 380/220 V project basis.
- Updated the room spot query to select the V5 lighting review view before V4.
  Added a read-only, source-hash-bound and paged extractor for exact V5 power
  review marks with shape, count and room checks. The bridge compiles and two
  focused paging/validation tests pass. Live acceptance requires loading the new
  bridge in a fresh Revit session; no RVT was changed by this work.
  The live V5 room check subsequently matched all eight CLASSROOM 1501 points to
  room `221095` with same-level ceiling bbox `278802` and no door/stair/shaft hit.
  The first power-mark read exposed a six-versus-three view-count assumption;
  corrected it against the V5 handoff and recompiled. Power-mark live acceptance
  remains open until Revit reloads; the failed request did not mutate the model.

## 2026-11-01 - CLASSROOM 1501 Mark correction prepared

- Added a bounded Revit API action to change only eight source-bound lighting
  fixture Marks in one transaction, check their type, level, coordinates and
  existing Marks, reject duplicate proposed Marks, and Save As a new RVT.
- Generated preview `141A51C0...933A1820` for IDs `457165`–`457172` from the
  verified physical sample, changing CLASSROOM 1500 suffixes `09..16` to
  CLASSROOM 1501 suffixes `01..08`. The original sample is unchanged.
- Compiled the bridge without errors and passed two focused Python tests.
  The operator approved exact fingerprint `141A51C0...933A1820`, and the bridge
  saved a new RVT at `DABF745E...F80544F`. Post-save API verification and a
  separate category read confirmed all eight room-specific Marks. The source
  and verified backup remained unchanged at `66405403...8EA8E3AB`. A separate
  close-and-reopen, ceiling-host and visual review remain open.

## 2026-10-31 - Approved CLASSROOM 1501 physical fixture sample

- Executed the operator-approved preview `5B5C9F09...906F4DC8` against the
  unchanged V5 source. A first attempt with the physical `D:` path was rejected
  before mutation because Revit had opened the same workspace through `R:`. The
  bridge now validates a same-directory `R:` output alias against the approved
  physical workspace path; focused regression tests cover that boundary.
- Created a verified source backup and Save-As sample RVT with eight new L1
  fixtures, IDs `457165`–`457172`. Post-save active-document Revit verification matched all
  eight approved locations and type; output SHA-256 is
  `66405403F8A34A3F4E444E09E94B5FCD34D78AD67A2625F56756A3658EA8E3AB`.
  The source and backup remain byte-identical at
  `8D533212466999B4D28C4BB6A4B15D63F945B779A9E126117431BA6F79FAE1B8`.
- A separate live electrical-relationship query found 61 total physical lighting
  fixtures; all eight new instances have one unassigned PowerCircuit connector.
  It also exposed a new blocker: their Revit-generated Marks carry the
  `CLASSROOM-1500-09` through `-16` suffix, despite CLASSROOM 1501 placement.
  Room-bound Mark correction and read-back are required before circuit creation
  or broader physical distribution. A separate close-and-reopen check, UI appearance,
  mounting, ratings, photometry,
  panels, routes and compliance remain unverified.

## 2026-10-31 - V5 physical fixture reconciliation and CLASSROOM 1501 sample preview

- Captured both complete live read-only lighting-fixture pages from the open V5 RVT.
  A deterministic, level-bound one-to-one minimum-distance audit matched the 45
  existing GYM 3500 instances and eight CLASSROOM 1500 instances to 53 unique V5
  review points. The largest candidate pair offsets are 198.307 and 2401.175 mm,
  respectively. The latter room needs position review before physical conversion;
  existing instances must not be duplicated. The other 894 proposed points are
  candidates, not approved installations.
- Queried reference fixture `447420` live in V5: one unassigned PowerCircuit connector.
  Its instance result did not expose voltage or VA, so no electrical rating was
  inferred from it. Preserved the read-only result.
- Completed two paged live electrical-relationship reads of all 53 existing V5
  lighting fixtures: every fixture has one PowerCircuit connector and none belongs
  to an ElectricalSystem. The 16 observed electrical fixtures are 12 Duplex and
  four GFI family instances; the bounded category result does not expose voltage.
  No V5 panels/equipment were observed. All reads were non-mutating.
- Prepared a non-executable, SHA-bound eight-point physical placement preview for
  CLASSROOM 1501 using the loaded L1 family/type, Level 1 and 2743.2 mm elevation.
  Exact points, source SHA and absence of existing fixtures in that room pass focused
  regression checks. This entry records the preparation state; the approved live
  execution and read-back are recorded above. The original V5 RVT was preserved.

## 2026-10-31 - School V5 calculation readiness and failure-prevention record

- Recorded the operator's lack of further V5 placement comments as an interim
  visual-review response, while retaining mounting, stair, photometric and electrical
  hold points. Documented V1-V5 failure causes and future geometry/host/circuit/
  engineering gates in `docs/SCHOOL_ELECTRICAL_LAYOUT_LESSONS.md`.
- Added a source-bound, read-only V5 load ledger. It validates the approved preview
  fingerprint, live Save-As identity, every room/point count and handoff totals. It
  reconciles 108 rooms, 947 lighting marks, preliminary 25,730 W active lighting,
  426 duplex, 21 GFI and 78 dedicated outlet candidates, grouped by proposed zone.
  VA, actual circuits, breakers, cable sizes, routes and compliance remain empty.
- Live read-only V5 category queries found 53 existing physical lighting fixtures,
  16 existing electrical fixtures and zero electrical-equipment instances. Ledger V2
  includes these counts and blocks naive subtraction or duplicate physical placement.
  The first ledger files remain as superseded evidence.
- Ledger V3 separates proposed lighting-zone W from power-zone outlet counts; V2's
  combined table was ambiguous even though its room sets happened to align.
- A bounded live Revit query inspected eight donor RFA files without loading them into
  V5. The outlet, switch, general-connection and panel files returned no connector
  elements; troffer types include 277 V, CoreLine has a 230 V candidate needing range
  review, and the industrial family has incomplete load data. Preserved the raw
  query and three category-count snapshots for follow-up.
- The first duplicate check exposed 203 identical X/Y positions on different floors;
  corrected the regression gate to include the level. No same-level point-center
  duplicates were found. Focused tests and compile checks pass.
- Created a three-sheet V5 calculation-input workbook from the reconciled room ledger.
  It preserves 108 room loads and separate lighting/power-zone summaries. Green cells
  hold missing type PF/voltage, duplex/GFI unit VA and room dedicated VA. VA/current
  formulas stay blank until their inputs are present; breaker/cable/route calculations
  are not fabricated. Artifact Tool input-change and restoration checks passed, with
  zero scanned formula errors and rendered sheet review. Native Excel read baseline
  108/947/25,730 totals; a dynamic COM trial hit a busy/null-workbook response, so
  native dynamic input acceptance remains open.

## 2026-10-31 - School V5 actual-area review handoff

- Added a hash-bound, read-only Level 1 symbol-stroke audit. Live Revit checked
  3,568 strokes against exact room containment and same-level door, stair and ceiling
  bounding boxes. No stroke crossed a room boundary or intersected a detected door
  or stair bounding box. 1,150 strokes had no same-level ceiling bounding-box candidate,
  including gym and corridor symbols. No RVT mutation occurred.
- Prepared an actual-room-area V5 candidate from the V4 area audit. Its initial 945
  lights passed read-only layout checks in 106/108 rooms. The two failing storage
  rooms were raised from two to three lights; a second Revit pass validated 108/108
  rooms and 947 exact room-light coordinates without mutation.
- Bound the 947 coordinates, unchanged source hash, output path and stair wall-light
  review marker into a new non-construction Save-As preview. Generated zoomable vector
  plan previews for Level 1, Level 2, Level 2 Check and the west stair. 170 points
  have no same-level ceiling bounding-box candidate and will be visibly flagged.
  The V5 execution adapter compiles and binds execution to those coordinates.
- After installing the execution adapter, repeated the exact execution preview as a
  read-only Revit request. It returned 108/108 feasible rooms, 947/947 matching
  coordinates and identical source/preview hashes, with no model mutation. The
  unsigned-add-in Load Once step completed before approval.
- The operator approved preview SHA-256 `80431B30...365D0751`. Live Revit Save As
  created `School-Electrical-Review-V5-20261031.rvt` at verified SHA-256
  `8D533212...FAE1B8`, preserving the source RVT. Revit reported 108 rooms,
  947 lighting symbols, 170 mounting-review points, one stair wall-light review
  marker, 426 duplex, 21 GFI and 78 dedicated power candidates, with zero exact
  duplicates, outside-room points, detected door-clearance or stair/shaft hits.
  The new Level 1 lighting view is open. Operator visual acceptance and physical
  family/photometric design remain pending; these are Detail Lines for review.

## 2026-10-31 - Operator-marked V4 review audit

- Preserved the marked V4 screenshot and added a bounded, hash-bound, read-only Revit
  spot query. Live Revit verified exact room containment for 17 approximate mark centers:
  one is outside all rooms, one overlaps a stair bounding box, and three have no ceiling
  bounding box at their center. The query changed no document data.
- A second live query measured the nearest lighting Detail Line stroke in the same room.
  The upper west stair mark is 3,531.8 mm away; the lower west storage mark is 109 mm
  away. Other circles include existing lights whose position needs review. These
  distances are not fixture-center distances or illuminance readings.
- A third live read-only query sampled nearby review-symbol strokes at five points;
  none within 3 m of the 17 marked centers crossed its containing room boundary.
  This narrows the position concern but does not establish door clearance, a valid
  mounting host, suitable lamp type, or photometric coverage.
- The first distance query failed safely because Revit does not allow `DetailCurve` in
  `FilteredElementCollector.OfClass`; changed it to collect `CurveElement` and filter
  to `DetailCurve`. The successful rerun produced no model mutation. Corrected future
  review legends to print their actual version instead of a hardcoded V3 label.
- V4 remains a rejected candidate for final placement. Door clearance, mounting hosts,
  actual-area quantities, and photometry still need review. The focused tests pass;
  the Revit bridge compiles and installs.

## 2026-10-30 - School V3 gap diagnosis and V4 candidate

- Preserved the operator-marked V3 screenshot and diagnosed three isolated gym gaps: the old
  layout chose 45 points from 48 grid cells. The check that reported zero duplicates and
  points outside rooms did not measure coverage. V3 remains rejected for visual layout.
- Added a V4 preview and Revit path that allocates the exact point count across sampled room
  segments, checks the maximum interior distance to a proposed light, and records actual
  Revit room area alongside the earlier bounding-box area. It uses a new output filename and
  leaves V3 and the source intact. The bridge compiles and the V4 preview integrity test passes.
- First V4 live request `3e170313-f805-4c2d-be77-b5ab50e61011` failed closed at
  `CORRIDOR 2513`: 2,382 mm maximum sampled gap exceeded its initial 2,118 mm geometric
  threshold. Revit reported no mutation and produced no V4 RVT. The row search was widened
  and the minimum geometric coverage threshold adjusted to 2,500 mm; the rebuild passed.
  One startup lacked the add-in after the unsigned-publisher prompt, so its pending request was
  held without execution. After a fresh `Load Once` and verified startup beacon, live request
  `952b0774-c7c5-42a4-ad09-956e5462f8e1` completed and saved
  `School-Electrical-Review-V4-20261030.rvt` at reported SHA-256
  `104619CD004D5C5189D09B2D713F93B8A70A6C74651F61C8DEBC0BD2CA84D645`.
  The unchanged source is `2F4E3543...73F582`. Revit reported 108 complete rooms, 1,152
  lights, 426 new duplex, 21 GFI, 78 dedicated, 16 existing outlets, two panel marks, and
  zero duplicate/outside-room/door/stair/shaft hits. The gym now has five rows of nine and a
  2,954.1 mm maximum sampled nearest-light distance. The raw live report and a 108-room area
  audit are preserved in the school preview directory; three V4 tests pass.
- Eight rooms have bounding-box areas more than 1.5 times their actual Revit room areas.
  CORRIDOR 2513 was counted from 726.274 m2 instead of 208.744 m2. If only area changes
  in the previous preliminary lumen formula, the indicative total falls from 1,152 to 945.
  This is an engineering review item, not a revised approved luminaire schedule or lux proof.

## 2026-10-30 - School electrical review V3

- Added a bounded, source-hash-bound Revit Save-As review path with six Electrical-discipline
  lighting/power views, six distinct luminaire review symbols, receptacle/GFI/dedicated-power
  review symbols, and two panel-zone marks. Placement uses actual room boundaries and checks
  detected doors, stairs, landings, and shafts. A slow door-boundary check was reordered and room
  boundary loops cached after one live run had to be stopped without changing the source RVT.
- Live request `c8afe3ff-0399-4f04-b9ad-16a692309145` completed preview
  `16CD9AFCB5723B771B5CA24C2F33F0BEFC356BB0D7B492269DCCA2548F5C6E1E`.
  Revit saved `School-Electrical-Review-V3-20261030.rvt` at reported SHA-256
  `B90A74899E9DB91BD50060FA0EFFFA0703B5692D54995BF5BA7C331A3434DF83`.
  The unchanged source hash is `2F4E35430E635D47ACF8931D9473246D692AAF73B4450F8BAD2744138F73F582`.
- Revit reported 108 rooms, 1,152 lighting review points, 426 new duplex, 21 GFI, 78 generic
  dedicated points, 16 existing outlets, and two panel marks. The six views contain 10,484
  detail curves with zero reported exact duplicates, outside-room points, door-clearance hits,
  or detected stair/shaft hits. The raw live report is preserved under
  `workspace/revit-school/previews/electrical-distribution/School-Electrical-Review-V3-Live-Report-20261030.json`.
  New regression tests reconcile every room and the view totals. These are review graphics,
  pending engineer visual acceptance; no new physical Revit families, circuits, routes, or code
  compliance were established.

## 2026-10-30 - Corrected room-boundary Revit lighting review V2

- Added the hash-bound `execute_school_lighting_review_v2_save_as` action plus deterministic preview
  and request producers. The Revit-side algorithm uses actual room boundaries, validates every point
  with `Room.IsPointInRoom`, maintains boundary clearance, excludes detected stairs/landings/shafts,
  refuses partial placement, and saves only to a new RVT.
- Live request `51943128-7b85-4b55-be1f-ad60805fbe5d` executed preview
  `81A30E22B8A6D54CF0C6465E653CF8F38615830D5FCE213C47E1CC184D651FF5` from unchanged source
  `2F4E35430E635D47ACF8931D9473246D692AAF73B4450F8BAD2744138F73F582`. Revit created
  `School-Electrical-Lighting-Review-V2-20261030.rvt` at output SHA-256
  `DE509ED57737DAA309D5920F48D33F01971C829FE82D43F2653B9AD6855FBA08`.
- The three V2 review views contain all 1,152 scheduled lighting points for 108 rooms using 4,608
  detail curves: 710 points on Level 1, 399 on Level 2, and 43 in the separate level-check view.
  Revit reported zero exact duplicates, zero points outside their rooms, and zero detected obstacle
  hits. Direct visual checks of both stair zones showed no symbols on flights or landings.
- Marked the earlier bounding-box distribution overlay as rejected/superseded for lighting. V2 is
  still a lighting-only review output: it creates no real families, wiring, circuits, receptacles,
  panels, breakers, cables, photometric proof, or code approval. Engineer visual approval remains
  open.

## 2026-10-30 - Live Revit whole-school distribution review views

- Added a bounded `execute_school_distribution_review_save_as` bridge action and an exact preview/
  request producer for the school review. The action creates view-specific colored detail symbols in
  new Level 1, Level 2, and level-check plan views, refuses overwrite, and saves as a new RVT.
- Live Revit 2024 execution succeeded for preview
  `8297D93F6AFB5C564CCF8F195420B77DC7A047C2AF16AEFA6BCD7413127F0B92` from source
  `2F4E35430E635D47ACF8931D9473246D692AAF73B4450F8BAD2744138F73F582`. The resulting review file
  contains 1,400 review points and 6,852 detail curves across three views: 799 points on Level 1,
  599 on Level 2, and two on the unclassified level check. Revit opened both principal review views
  for operator inspection. The output SHA-256 recorded by Revit is
  `E420A853B7A4177E582A95C4BDCE2D5B56027CC8B29692E7D70205D2DCF47D2E`.
- The overlay distinguishes 851 lighting locations, 438 general duplex points including 12 observed
  existing points, 25 GFI marks including four observed existing points, 78 generic dedicated points,
  and eight main/zone panel guides. It deliberately does not claim that 301 lighting units without
  trustworthy XY geometry were placed, and it creates no circuits, routes, breakers, or cable sizes.

## 2026-10-29 - School project engineering calculation extraction

- Built a visual, source-hash-bound full-school review preview at
  `workspace/revit-school/previews/full-school-distribution-review-20261029/` and packaged it as
  `workspace/revit-school/outputs/School-Electrical-Distribution-Review-2026-10-29.zip`. The three
  plan images include room labels, lighting, new and existing duplex/GFI points, 78 generic dedicated
  points, and main-panel/branch-zone candidates. CSVs provide 108 room rows and 1,417 point/hold
  records. Counts reconcile to 1,152 lighting units / 29.680 kW, 851 XY points / 301 held, 438
  duplex target (12 existing + 426 candidates), 21 new GFI target points, and 78 generic dedicated
  points. The 53 verified scratch-RVT lights use green rings. A separate unclassified-level plan
  isolates room `CORRIDOR 4002`. No RVT was modified. The package marks geometry/code limits and
  cites the official Egyptian-code and socket-outlet listings without claiming clause compliance.

- Added `workspace/revit-school/outputs/School-Electrical-Loads-and-Cable-Inputs.xlsx` with
  a 108-room lighting/receptacle schedule, a formula-driven six-type luminaire schedule,
  78 dedicated equipment load rows, green missing-input cells, and 14 conceptual panel rows.
  It calculates demand VA/current and single-/three-phase equipment voltage drop after the
  user enters nameplate and route inputs. It deliberately leaves cable and breaker selection
  for reviewed engineering criteria. Recalculation matched the existing preview: 1,152
  luminaires / 29.680 kW, 438 general receptacles, 21 GFI, and 78 dedicated points. No RVT
  was changed; this workbook is a calculation intake and review schedule, not a code-approved
  or construction-ready design.

- Added `workspace/revit-school/ENGINEERING_CALCULATIONS_2026-10-29.md`, a source-hash-bound
  Arabic report summarizing the 108-room lighting and receptacle study, the 1,152 / 29.680 kW
  preliminary lighting program, the 806 base XY points plus 45 gym points, and the verified
  45-gym + 8-classroom current RVT sample.
- Recorded the classroom and gym lumen-method results and separated calculated loads from
  assumptions. The report explicitly blocks unsupported cable, breaker, panel, fault, and
  compliance conclusions while the model lacks project circuits, equipment loads, route inputs,
  source fault data, and pinned licensed code clauses.
- Updated the active school queue and roadmap to replace the superseded 764-point summary.
  No RVT or workbook was modified in this reporting step.

- Executed the approved `CLASSROOM 1500` L1 eight-fixture sample under fingerprint
  `0293A7A6...39B116` and request `5cea88a3...9639`, saving a separate RVT at
  `2F4E3543...3F582`. Reopened read-only verification `b48b19ad...60e3` matched all eight
  placements, parameters and connectors, preserved 45 L4 fixtures, and reported no mutation.
  The original source and its timestamped backup remain at `FB72029C...C52D1`.
- Visual query `7a38bb8d...2c89` found fixture `447423` visible in the Level 1 floor plan at
  `2743.2 mm`, but its automatic image export captured the whole-school 3D view. Visual acceptance
  and bulk classroom distribution remain blocked pending an intelligible ceiling-plan review.
- Opened the existing Level 1 `01 RCP - MEP COORDINATION` view, used Revit's Select Elements by ID
  on all eight created IDs, and visually confirmed the 3/2/3 layout in `CLASSROOM 1500`; Properties
  reported `Lighting Fixtures (8)`. No model write was performed. Engineer acceptance and detailed
  ceiling/photometric coordination remain open; the automatic image exporter remains faulty.

- Refreshed the unexecuted `CLASSROOM 1500` sample preview on 2026-10-24. The source hash,
  official L1 family/IES hashes, and all eight placements match the preceding preview. The new
  fingerprint is `0293A7A6...39B116`; no Revit model was changed.

## 2026-10-17 - Classroom 1500 L1 visual sample preview

- Added a source-bound, non-executable classroom sample derived from the complete gym output
  `FB72029C...C52D1`. It binds room `221093`, fully overlapping ACT ceiling `278813`, exact L1
  RFA `7AEC687A...6DAC2`, and exact IES `7970BE1E...3D18D`.
- Proposed eight 600x600 panel positions in a symmetric `3x3` perimeter pattern with the center
  omitted, at ceiling underside `2743.2 mm`. The established lumen method gives `310.8 lux` and
  `248 W` active load.
- Preview fingerprint is `4414E83E...55C423`. It authorizes neither execution nor bulk classroom
  distribution and retains ceiling-grid, board/projector/furniture, point-calculation, glare, and
  licensed-code review. Eight focused classroom/batch tests pass; no RVT was modified.

## 2026-10-17 - Verified L4 gym Revit batch 2

- Implemented and installed the exact fixed action for approved preview `56552925...943801`.
  Live request `05cb6ac6-bad7-4f2d-a4e9-cfc54eb2e71a` preserved the 23 verified batch-1 fixtures
  and added the 22 complementary points at `9500 mm`.
- Saved only new `School-Architectural-electrical-L4-Complete.rvt` at SHA-256
  `FB72029C02CF900C70487BFD4636AF1C34806E31F62946324376BE38296C52D1`. Reopened read-only
  request `f4c7f5f7-2568-4e35-b1ba-5fff1b203b03` verified all 45 unique elements, locations,
  marks, family/type bindings, elevation, and one electrical connector each.
- Batch-1 source and its new timestamped backup remain `941551BE...41A1A`. The fixed action and
  related previews pass 23 focused tests. The next gate is one representative classroom sample
  for visual engineer acceptance before any broader school distribution.

## 2026-10-17 - L4 gym Revit batch-2 preview

- Added a strict batch-2 preview builder that accepts only verified batch-1 RVT
  `941551BE...41A1A`, handoff `012317B4...98129`, verification `C5C3AC0A...CD3B`, and the exact
  reviewed L4 RFA/IES hashes. It rejects changed evidence, changed partition, expiry, tampering,
  and an existing output.
- Generated 22 complementary checkerboard points at the engineer-approved `9500 mm`, yielding 45
  total fixtures after execution. New preview fingerprint is
  `565529255B384C229F43B446A1A7169037E07DF63AD0F17D744FF37F50943801`; proposed output is a
  new `School-Architectural-electrical-L4-Complete.rvt`.
- The first generated batch-2 preview expired without execution and remains historical evidence.
  No Revit process was opened and no RVT was modified. Nine focused preview and handoff tests pass;
  exact fingerprint approval remains required before implementing or submitting batch 2.

## 2026-10-17 - Verified L4 gym Revit batch 1

- Implemented, compiled, installed, and live-ran the fixed Revit 2024 action bound only to approved
  preview `5E78A601...6F273C`, exact family `404CCB84...2A139F`, source
  `D0D0C48E...09F3`, `Level 1`, and mounting elevation `9.500 m`.
- Live request `592c9a53-1f9c-4160-bc7a-e0a7bd36e5ce` loaded the exact
  `BY120P G5 LED105S_840 WB PSU` type and created the 23 approved checkerboard instances. Every
  instance has its bound `AIENG-L4-GYM-*` mark and one electrical connector.
- Saved only new output `School-Architectural-electrical-L4-Batch1.rvt` at SHA-256
  `941551BE095D7CC743924ACDFAE335E8CB549D648DB1C9ABDECADF669BE41A1A`. Reopened read-only
  request `e88d158a-c3d8-46b1-8a9d-fca777402734` verified all 23 element IDs, exact coordinates,
  `9500 mm` elevation, level, family/type, marks, and connectors without mutation.
- The timestamped backup and protected working source both remain
  `D0D0C48E4E800B0045567D23A94A656AA83C7589BF872D6C6E58EB81381809F3`. The focused suite
  passes 26 tests and the installed DLL builds successfully. Batch 2 remains a separate preview,
  approval, backup, new-file transaction, and reopen gate; lighting compliance checks remain open.

## 2026-10-17 - Bounded L4 gym Revit batch preview

- Added a non-executable, hash-bound school-gym preview for the exact L4 family and IES. It binds
  the unchanged school source `D0D0C48...09F3`, RFA `404CCB84...2A139F`, IES
  `648B25C3...6258A8`, a new non-overwriting RVT output, backup, and reopened verification.
- Proposed `9.500 m` as a provisional mounting elevation from the recorded `10.619 m` room top,
  leaving `1.119 m` clearance. The existing `9x5` layout is split as a spatial checkerboard into
  23 points for batch 1 and 22 deferred points for batch 2, keeping each transaction below the
  established 32-instance bound.
- Preview fingerprint is `5E78A601...6F273C`; PNG plan SHA-256 is
  `9BAE1E7E...D4DDF976`. Added strict tamper, expiry, changed-input, point-partition and overwrite
  validation. The focused preview/handoff suite passes 19 tests.
- No Revit process was opened and no RVT was modified. Exact fingerprint/height approval and a
  reviewed load-family + placement transaction are required before batch 1 execution.

## 2026-10-16 - Engineer-approved 67 W L4 gym distribution preview

- Recorded the engineer's decision to use the exact official L4 IES value `10501.2 lm/67 W` for
  preliminary distribution, without treating active power as apparent power or claiming final
  Egyptian-code compliance.
- Added a deterministic `9x5` XY placement preview for 45 high-bay units in `GYM 3500`. The room
  bounding area reconciles to `801.171 m2`; the lumen method returns `306.7 lux` and `3.015 kW`.
  Preview fingerprint is `1E3B1726...4B06FA3`; execution remains disabled.
- Published a new nine-sheet Excel preview at SHA-256 `E4F6575C...154F042`. It binds current exact
  L1/L2/L4/L5-L6 photometry, adds the 45-point gym schedule, and highlights missing mounting Z in
  green. Artifact Tool reimport and native Excel full recalculation found zero formula errors.
- Added a focused regression test. Mounting height, PF/VA, IES point calculation, glare review,
  licensed Egyptian clause evidence, and a separately approved Revit transaction remain required.

## 2026-10-16 - Verified L2 and L5-L6 Revit parameter update

- Implemented and installed the separately approved fixed Revit action for preview
  `8359BB401FE1EC0221E116948B540AEC4118125E6A0BAB51B44672B22021781D`. It accepts only the exact
  verified L1 source and two exact hashed IES inputs, edits a copied RVT through supported API
  transactions, refuses overwrite, and reopens the output before publication.
- Live request `4056f61e-e41e-499d-bf6a-e499b5038506` passed. L2 reopened at `19 VA/19W` with exact
  IES `C95CF992...651FAA`; L5-L6 reopened at `30 VA/30W` with exact IES
  `5754E154...2213A3`. Both retained one electrical connector and L1 remained verified/preserved.
- Published `School-Lighting-L1-L2-L5L6-Parameter-Validation.rvt` at SHA-256
  `B4EC298B684B32186CF1C8DFB4A24F8C1D3345615D591BA137C06FD1B150D910`. Its source remained
  `3302C249...7C604D`, and the protected school RVT remained `D0D0C48...09F3`. L4 and all actual
  school placement remain held.

## 2026-10-16 - Exact L2 and L5-L6 official photometry refresh

- Retrieved current public Signify IES assets directly from exact product endpoints for order codes
  `911401816685`, `910505100047`, and `911401629308`; recorded URLs, archive/file hashes, and exact
  bounded entries without login, payment, installer execution, or model mutation.
- Added a safe ZIP validator/extractor and self-hashed refresh artifact. It rejects traversal,
  symlinks, oversized/non-IES members, changed archives, changed entries, ambiguous entries, and
  overwrite conflicts. Five new tests plus the existing photometry tests pass.
- Refresh preview `4BCD2DE54EC122AA0C153D022A2ED16F4D9F51D570554BE1530225A3484CB1E6`
  confirms L2 `2500.4 lm/19 W` and L5-L6 `4000.6 lm/30 W` against their exact order codes.
- Created non-executable parameter preview
  `8359BB401FE1EC0221E116948B540AEC4118125E6A0BAB51B44672B22021781D`, bound to verified L1 output
  `3302C249...7C604D`. It changes only L2's stale IES and L5-L6's `19 VA/19W` plus stale IES.
  L4 remains held because the refreshed exact official IES still reports `67 W` while the current
  exact product page reports `63 W`. No Revit request was submitted.

## 2026-10-16 - Verified school L1 photometry parameter update

- Implemented the exact approval-bound L1 parameter writer and installed it in Revit 2024. The
  writer copies the hash-verified IES to Revit's versioned IES library, writes its absolute path
  through the supported API, saves only a new isolated RVT, reopens it, and reports the basename.
- Live request `633a5606-15e5-459e-8a93-76cc75dcff6d` passed reopened verification for `31 VA`,
  `31W`, exact IES `7970BE1...3D18D`, and one electrical connector. Published output
  `School-Lighting-L1-Parameter-Validation.rvt` has SHA-256
  `3302C249AEFD093B033050378ACE0A2E6CDF3F8289E788418E60971C557C604D`.
- The isolated source remained byte-identical at `49F851D3...53EAC`; the protected school source
  remained unchanged at `D0D0C48...09F3`. L2, L4, and L5-L6 remain held, and this acceptance does
  not authorize fixture placement, circuit creation, routing, or mutation of the school model.

## 2026-10-16 - Source-bound school photometry and L1 parameter preview

- Added a bounded LM-63 parser and deterministic, self-hashed lighting evidence preview. It binds
  exact RFA, IES, reopened scratch-RVT, and live-report hashes without permitting execution,
  mutation, saving, or provider egress. Six focused tests pass.
- Generated `LIGHTING_TYPE_PHOTOMETRY_PREVIEW.json` at preview SHA-256
  `043B331542D1A1D27552AF7C922DB6B7C673EB3BE4CFECC7EE738E71AF0C45CB`.
  L1 is the only exact product/IES match (`4310.8 lm`, `31 W`). L2 remains held at
  `2500 lm/19 W` versus IES `2883.8 lm/19.257 W`; L4 remains held at product `63 W` versus exact
  IES `67 W`; L5-L6 remain held because the selected `4000 lm/30 W` product lacks an exact reviewed
  IES and the nearest reviewed file is `4500 lm/30 W`.
- Generated a separate non-executable L1-only parameter update preview
  `310E62C9FBAD3B1AE50A06B3D9983AA10355AABD26718134E527C3EFACFB8FDA`. It proposes changing the
  verified scratch type from `19 VA/19W` to `31 VA/31W`, binding the exact hashed L1 IES, saving
  only to a new RVT, and reopening it for verification. Its later exact approval and execution are
  recorded in the verified entry above.

## 2026-10-15 - Live Revit 2024 school lighting-family scratch acceptance

- Added an approval-bound, non-overwriting Revit scratch-family workflow for four exact
  Signify/Philips RFA hashes and type names. Legacy families are upgraded only in per-request local
  copies; an exact embedded type is retained, or the explicit manufacturer `Type Catalog` type is
  renamed inside that copy when unambiguous.
- Live request `21d66517-1a40-467c-8ae1-263a50f77ae0` created, saved, reopened, and independently
  verified four Lighting Fixture instances. L1, L2, L4, and L5-L6 each retained one electrical
  connector, visible geometry, a bounding box, the exact requested type name, and the approved
  millimetre coordinates.
- Published `workspace/revit-school/scratch/School-Lighting-Family-Validation.rvt` at SHA-256
  `49F851D350FBF74DC77A2C980B27E6D8BFA8EF2815571EAD22AA2B15CA453EAC`; preserved the bounded
  report beside it. The protected school source remained unchanged at SHA-256
  `D0D0C48E4E800B0045567D23A94A656AA83C7589BF872D6C6E58EB81381809F3`.
- The live report confirms `230 V` types, but exposes stale embedded load/photometric values in
  several manufacturer RFAs. Companion Type Catalog rows were not silently accepted after Revit
  rejected them. Keep final load, lumen, and IES bindings on hold for a separately reviewed type-
  parameter/photometry gate; this scratch success is not permission to place fixtures in the school.

## 2026-10-14 - Product-bound school lighting preview and safe RFA inspection

- Added a bounded, hash-staged, read-only `family_file_query` for Revit 2024. It opens at most eight
  external RFA files, reports their family/category/placement/host data, relevant type parameters and
  electrical connectors, and closes every family without saving or loading it into the active model.
- Added strict Python result validation for outer and nested report data plus request, escape, hash,
  mutation and bound tests. The plugin compiles successfully and the focused suite passes 25 tests;
  DLL `B19C828F...3468D` is staged in a versioned install path and selected by the add-in manifest.
  Live acceptance awaits a controlled Revit restart because the current process has the prior DLL
  loaded; the prior manifest is preserved under `backup/revit-addon/20261014-155157`.
- Live-read four initial external families in Revit 2024 without loading or saving them. A second
  live request inspected product-specific Batten and Waterproof packages and exposed stale internal
  default photometric references, so only exact companion Type Catalog names may be used.
- Downloaded the current official product-specific Revit packages for Batten `911401816685` and
  Waterproof `910505100047`. Their current product pages confirm `220-240 V`; Batten low setting is
  `2500 lm/19 W`, and Waterproof is `4000 lm/30 W`, IP65/IK08.
- Regenerated the school electrical workbook with the exact product data. It distinguishes 1,152
  calculated normal-light units at 29.68 kW from 806 geometry-ready review points at 20.231 kW,
  and the legend now shows required quantity, preview quantity, design load, RFA hash, live Revit
  result and approval state for every type.
- Added a separate emergency/exit-lighting hold register for 19 egress or higher-review spaces. It
  deliberately contains no fabricated quantities or coordinates pending the fire strategy, exit and
  egress validation, mounting requirements, and licensed Egyptian code clauses. The school RVT was
  not modified.
- Prepared non-executable scratch-family preview `3F0ABF99...D28E7E` for four exact RFA/type pairs,
  one test instance each, a new non-overwriting RVT output, source non-mutation, and reopened
  verification. It remains behind explicit approval.

## 2026-10-14 - Free official lighting-library intake

- Downloaded 17 public Signify/Philips assets without payment, login, or installer execution for
  CoreLine Panel, Batten, Waterproof and Highbay, Ledinaire IP44 wall lighting, and exit signs.
- Safely validated ZIP paths and extracted 14 Revit families, 36 IES files, and one LDT file. All
  RFA files are readable by the Revit 2024 BasicFileInfo API and every IES file has a valid IESNA
  header. Six official data sheets retain the product context.
- Recorded source URLs and SHA-256 hashes plus the proposed room-use mapping. Two manufacturer-data
  deltas remain explicit: Batten page versus IES lumen output and Highbay page versus IES power.
- Microsoft Defender's on-demand scan returned no detection. No family was loaded and the school
  RVT was not changed; connector, host, type, regional-SKU, and upgrade review remain a scratch-
  model gate before any placement preview.

## 2026-10-14 - Supplied Revit and Philips library intake

- Inventoried the supplied archives without executing them. `family.rar` contains 1,603 RFA files
  across nine nested libraries and no standalone IES/LDT photometry; no executable or script was
  found in those nested libraries.
- Extracted 80 priority candidates into an isolated review directory. Revit 2024 BasicFileInfo
  reads all of them; 75 are Revit 2011 files, three are 2012, one is 2009, and one is 2013.
- Identified panel, outlet, GFI, switch, general-connection, troffer/linear, emergency-light, and
  exit-sign candidates for a later scratch-model test. They are not yet approved for 220 V use.
- Identified `Philips.rar` as unsigned Philips Product Selector 5.2.12.2 legacy installation media,
  not a Revit family package. Its 848 files include the product database and proprietary 3D/data
  formats but no RFA or standalone IES/LDT. The installer was not run; an on-demand Defender scan
  returned no detection, while real-time protection was reported disabled.

## 2026-10-14 - School electrical distribution preview

- Read the School Architectural working copy through the bounded Revit connector and retained a
  source-bound inventory of 108 rooms, 96 ceilings, 16 existing receptacles, and no existing
  lighting fixtures, panels, or circuits.
- Generated a seven-sheet, non-mutating electrical distribution workbook for the approved
  `380/220 V`, three-phase, four-live-conductor, `50 Hz`, `TN-S` design basis. It proposes 764
  lighting points, 438 general receptacles, 21 GFI points, 78 dedicated points, and a zoned panel
  topology while keeping final engineering inputs explicit.
- Held 18 rooms from placement because exact ceiling support was insufficient, rejected the loaded
  120 V troffer for the 220 V basis, and left every Revit write, circuit, route, cable/protection
  selection, and compliance claim behind later approval gates.
- Native Excel full recalculation found zero formula errors; a reversible target-illuminance change
  updated the required fixture count before the delivered workbook was restored.

## 2026-10-13 - Complete Revit electrical-system schedules

- Added the bounded, read-only `electrical_system_query` Revit action, Python request/loader,
  deterministic page-chain inventory, launcher commands, and tests. Unlike fixture-driven
  extraction, it collects `ElectricalSystem` elements directly and retains member-category
  counts and load classifications without changing the RVT.
- Live-captured all 448 power circuits in 14 ordered pages with no duplicate, gap, or non-progress
  result: 113 lighting, 264 receptacle, 67 feeder/panel, and four spare/empty circuits across
  54 panels. The source-bound inventory is self-hashed at `076BEFF5...B6257B8`.
- Generated a 54-sheet complete panel-schedule workbook in the operator's reference form. It
  handles 138 multi-pole circuits once at their start slot, reconciles each circuit's phase
  allocation to its Revit apparent load, and keeps missing engineering inputs green.
- A reversible loaded-panel test recalculated category demand, spare allowance, maximum demand,
  and phase current. All sheets were rendered and representative branch, main, and zero-load PV
  schedules were visually reviewed; Excel native recalculation found 1,566 formulas and zero
  formula errors. The aggregate 2,093,743.732737 VA is the sum of all
  circuit rows, including hierarchical feeder and downstream loads, and is not a non-duplicated
  project demand value.
- Raised Revit readiness to `v29`. The approved RVT remained read-only; final demand factors,
  route verification, fault duty, breaking capacity, coordination, and code approval remain open.

## 2026-10-13 - Template-matched Revit panel schedules

- Inspected the operator-supplied 12-sheet panel-schedule reference without modifying it and
  reproduced its odd/even circuit layout, R/Y/B phase presentation, load summary, and feeder
  summary for the current Revit extraction.
- Generated 31 panel sheets covering 113 extracted lighting circuits and 45,546.749 VA. Each
  panel's phase totals reconcile to its circuit total; a reversible demand-factor/spare test
  recalculated connected load, demand load, maximum demand, and maximum phase current correctly.
- Preserved missing location, breaking capacity, routing, verified length, feeder type, short-
  circuit current, demand factor, and spare allowance as green review inputs. The output states
  that its circuit scope is the extracted lighting inventory and does not imply a complete power
  panel schedule.
- Rendered all 31 sheets and scanned both the working and reopened XLSX for formula errors; both
  scans returned zero matches. The supplied reference workbook remained byte-identical.

## 2026-10-13 - Revit MEP quantities and ten-sheet workbook

- Added deterministic category pagination and strict chain validation for electrical fixtures,
  lighting devices, electrical equipment, conduit, conduit fittings, cable tray, and cable-tray
  fittings. Generic inventory remains usable when a linked host is unavailable while preserving
  the unresolved linked element identity; ceiling-hosted safety queries still fail closed.
- Live-captured 538 electrical fixtures, 196 lighting devices, 200 equipment instances, 530
  conduit segments, 322 conduit fittings, and no cable-tray elements. Supported Revit curve data
  totals 1,730.348 m of modelled conduit; 16 wiring pull boxes remain explicitly counted.
- Expanded the visually verified workbook from six to ten sheets with device, equipment, conduit,
  and fitting BOQs. Green cells hold verified installed quantities, waste, specifications, and
  engineering review inputs; model geometry is never relabelled as installed quantity.
- Raised Revit readiness to `v28`. The source RVT was not mutated; missing linked architectural
  hosts, unmodelled accessories, and final engineering/code approval remain explicit limitations.

## 2026-10-13 - Revit complete lighting inventory and project workbook

- Live-captured all 589 lighting fixtures from the approved Snowdon output through 19 ordered,
  bounded 32-fixture pages. The strict chain proved no duplicate, gap, or non-progress result and
  reported no provider egress or document mutation.
- Added a source-bound project aggregator and tests. The accepted inventory contains 563 connected
  fixtures, 26 unassigned fixtures, 113 unique circuits, 31 panels, 22 family/type rows, and
  4,998.453 m of modelled conductor quantity.
- Generated and visually verified a six-sheet Excel workbook with Summary, Panel Schedule, Panels,
  Lighting BOQ, Cable Quantities, and Engineering Inputs. Missing engineering inputs are green and
  formula readiness remains incomplete instead of silently substituting typical values.
- Raised Revit readiness to `v27`. Conduit, cable tray, boxes, switches, receptacles, non-lighting
  equipment, verified installed routes, and final code/protection approval remain separate gates.

## 2026-10-12 - Revit lighting-circuit pagination gate

- Added deterministic `start_after_element_id` pagination to the read-only lighting-fixture circuit
  request, Python loader, launcher output, and Revit 2024 add-in. Exact-element requests cannot be
  mixed with a page cursor, and each truncated page must continue from its exact last element ID.
- The loader rejects duplicate, unsorted, non-advancing, and invalid final continuations while
  retaining the 32-fixture per-request cap. Existing non-paged evidence remains loadable.
- Revit tests pass `50 passed`; Python compilation and the Revit 2024 DLL build/install pass, and
  installed and built DLL hashes match. Readiness is now `v26`; live multi-page acceptance remains.

## 2026-10-12 - Revit engineering-input Excel prototype

- Added a standalone three-sheet Excel calculator for the live `P109 / 5` intake. Blue cells retain
  exact Revit observations, green cells accept engineer inputs/overrides, yellow cells flag observed
  or assumed values requiring confirmation, and every calculation consumes a separate active-value
  column so provenance remains visible.
- Added formula-driven load current, corrected ampacity, `Ib <= In <= Iz`, provisional common-series
  breaker selection, voltage drop, fault impedance/current, breaking-capacity, and calculation-
  readiness checks. Missing values remain `Required input` rather than zero or false acceptance.
- Formula inspection has no spreadsheet errors. A disposable change test completed the ampacity and
  voltage-drop prerequisites and updated their results, after which the delivered inputs were reset.
  The workbook preserves all three source SHA-256 identities and does not modify the RVT.

## 2026-10-12 - Revit upstream feeder evidence and fail-closed finding

- Extended the exact lighting-circuit query with up to eight incoming systems for the supplying
  panel. A system is accepted only when the panel is an actual system element and is not that
  system's base equipment; `Supply From` text alone is never treated as connectivity evidence.
- Live request `dc67827d...e175` proved panel `P109` is fed by system `1672726`, circuit `3`, from
  `MB13` element `1668955`. Revit reports `208 V`, `24.415 kVA`, `67.769263 A`, `20 A` rating,
  `26.129 m`, `3-#4, 1-#4, 1-#4`, and `45.438594%` modelled voltage drop.
- Review `D6CDC73E...260A` calculates `338.846315%` modelled rating utilization and
  `-47.769263 A` headroom. It makes no code-threshold claim, but blocks engineering readiness
  because the modelled current exceeds the modelled rating. Intake `74CDE784...3B9F` retains the
  blocker plus 25 missing reviewed inputs. Revit closed without saving, tests/build/install pass,
  and readiness is now `v25`.

## 2026-10-12 - Revit circuit calculation intake gate

- Added `revit-build-circuit-calculation-intake`, which consumes a valid self-hashed Revit
  engineering review and creates a new, atomic, self-hashed input inventory. It refuses changed
  reviews, path escape, symlinks, oversized evidence, and output overwrite.
- Generated intake `AA0C2226...4198` for `P109 / 5`. It retains five Revit observations as
  requiring engineer confirmation and identifies 25 missing reviewed inputs covering the pinned
  code profile, voltage-drop criterion, installation/derating, source/path impedance, exact breaker
  identity/curve/breaking capacity, manufacturer evidence, selectivity, and reviewer identity.
- The intake explicitly blocks ampacity, short-circuit, protection-coordination, and compliance
  conclusions and performs no calculation or model mutation. Focused and complete Revit tests,
  compilation, self-hash verification, and readiness `v24` pass.

## 2026-10-12 - Revit bounded protection-parameter evidence

- Added a read-only, locally filtered snapshot of protection-related parameters from the exact
  circuit/panel instances and their types. Each list is capped at 32 entries and retains source,
  definition name, stable parameter ID, storage type, and displayed value; it does not enumerate
  unrelated project parameters or infer missing protection data.
- Live request `6441f18d...b075` found circuit `Rating = 20 A` and `Frame = 400 A`, plus panel
  `MCB Rating = 225 A` and `Max Number of Single Pole Breakers = 30`. No breaker curve, fault
  current, interrupting-capacity/AIC, or selectivity parameter was found, and the panel short-
  circuit-rating field remains empty. `Frame` is not treated as fault or interrupting capacity.
- Self-hashed review `F7CFA4BB...CF83` records the four observed values while keeping protection
  coordination and code compliance unresolved. Revit closed normally without saving; the output
  remains SHA-256 `A5B3AC70...16031CE`. Tests, compilation, DLL build/install, and readiness
  `v23` pass.

## 2026-10-12 - Revit circuit wire and protection evidence

- Extended the strict read-only circuit contract with modelled circuit length, runs, ways,
  notes, exact `WireType` identity/material/temperature/insulation/conduit/neutral settings,
  and the panel short-circuit-rating display. The maximum-size ampacity is retained only as
  type-definition evidence and is never treated as selected-conductor derated ampacity.
- Live request `e6cbd42d...3736` read the verified `P109 / 5` output without mutation. Revit
  reports `12988.925 mm`, one run/way, Copper `THWN`, `60` temperature-rating text,
  `Non-Magnetic` conduit, hot-sized required neutral, and an empty panel short-circuit rating.
  Revit closed normally and the output remained byte-identical at `A5B3AC70...16031CE`.
- New source/query/preview-bound review `F0EE0DAC...414F` records complete WireType evidence,
  but keeps installation/ambient/grouping derating, selected-conductor ampacity, breaker curve,
  fault current/selectivity, missing panel fault rating, governing code criteria, phase semantics,
  and 120/208 V project applicability unresolved. Focused tests, compilation, live DLL build and
  install pass. Readiness is now `v22`.

## 2026-10-12 - Revit circuit engineering evidence review

- Extended the read-only circuit query with circuit rating/frame, apparent and true current,
  phase currents, phase label, voltage drop, wire description, conductor counts, and exact
  supplying-panel evidence including MCB rating, phase/wire count, supply source, total currents,
  and per-phase currents.
- Live read `fc117125...0857` on the verified output reported circuit `P109 / 5` at `120 V`,
  `20 A`, `100 VA`, `95 W`, `0.833333 A` apparent current, phase `C`, `1.473381%` modelled
  voltage drop, and `1-#12` hot/neutral/ground conductors. Panel `P109` is modelled as `225 A`,
  3-phase/4-wire, supplied from `MB13`, with phase currents `85.491306 / 62.241306 / 57.097760 A`.
- Added a deterministic, source/query/preview-bound engineering review. Artifact
  `369C8EC5...2B615D` calculates `4.166665%` branch-rating utilization, `30.119672%`
  panel-demand/MCB utilization, `157.230737 A` modelled panel headroom, and a `-0.101966`
  percentage-point change in the panel phase-imbalance indicator after the added `20 VA / 19 W`.
  It explicitly does not claim cable ampacity, protection coordination, phase-balance acceptance,
  voltage-drop compliance, or Egyptian-code compliance. Readiness is now `v21`.

## 2026-10-11 - Revit single lighting-circuit assignment live acceptance

- Added a separately allowlisted `ElectricalSystem.AddToCircuit(ElementSet)` path for exactly
  one approved fixture and one existing power circuit. Python and Revit both revalidate the
  expiring preview, source hash, fixture/reference/type/connector identities, actual reference
  membership, unassigned target fixture, new workspace output, and verified backup.
- Explicit approval of preview `FE9DB83D...A7F3AEF` produced request
  `6c08de07-c84d-441b-a61d-9f4d50fe71ab`. One transaction assigned fixture `1735503` to
  system `1622590` (`P109 / 5`) and saved only
  `Snowdon-Towers-Electrical-hosted-ceiling-circuit-P109-05.rvt`, SHA-256
  `A5B3AC70...16031CE`.
- Reopened read-only verification `119f68bf...a399` matched the exact fixture, system, preview,
  and output hash. Independent relationship query `1ad807e3...b64e` returned one actual system
  and reported the circuit at `120 V`, `100 VA`, `95 W`, PF `0.95`, one pole. The observed
  change from the reference query is `+20 VA / +19 W`; it is not a capacity, protection,
  demand, phase-balance, or code-compliance approval.
- Revit closed without further saving. Source and verified backup both retain SHA-256
  `1C390703...BCCB8`; 46 Revit tests, Python compilation, Revit 2024 DLL compilation, install,
  and readiness `v19` pass.

## 2026-10-11 - Revit circuit connector compatibility and review preview

- Extended the bounded lighting-circuit query with supported Revit MEP connector evidence:
  connector ID, domain, connector type, electrical system type, and current system identity.
  Exact live requests `a463baa3...5096` and `a6b21b5c...2e7e` found the same single
  `DomainElectrical / End / PowerCircuit` connector on created fixture `1735503` and
  reference fixture `1622255`; the created fixture remains unassigned and the reference is
  an actual member of system `1622590`.
- Added `revit-preview-circuit-assignment`, a local, expiring, source-bound, self-hashed
  review artifact. It requires exact single-fixture queries, matching family/type and connector
  signatures, an unassigned target fixture, and reference membership in the requested system.
  It has no execution path and always records `execution_allowed=false`.
- Generated preview `FE9DB83D...A7F3AEF` for adding fixture `1735503` to `P109 / 5`, bound
  to source SHA-256 `1C390703...BCCB8`. Fixture instance voltage/load were unavailable, and
  circuit capacity, demand, protection, and phase impact remain explicitly unresolved. Revit
  closed without saving; 16 focused tests and Python compilation pass. Readiness is now `v18`.

## 2026-10-11 - Revit lighting circuit relationships read-only gate

- Added the bounded `lighting_circuit_query` action and
  `revit-read-lighting-circuits` command. It reads up to 32 exact lighting fixtures
  through `MEPModel.GetElectricalSystems()` and reports true system IDs, system type,
  circuit, panel, load name, volts, VA, watts, power factor, and poles without mutation
  or provider egress. An exact element ID can be required; missing identities fail closed.
- Live request `c1a87887...a5ba` proved exemplar `1622255` belongs to power circuit
  `1622590`, panel `P109`, circuit `5`, with `120 V`, `80 VA`, `76 W`, PF `0.95`, and
  one pole. Request `6e682cd9...cf4b` proved created fixture `1735503` has zero systems;
  no circuit membership was inferred from nearby geometry or parameter text.
- Bounded whole-model request `afba598e...8f62` reported 589 lighting fixtures and
  returned exactly 32 with `truncated=true`; all 32 returned fixtures were connected.
  Readiness is now `v17`.

## 2026-10-11 - Revit hosted-light visual acceptance and deterministic focus

- Added an exact read-only lighting-fixture focus report with active-view identity, model/view
  bounds, orientation and flip state, host-relative elevation, visible geometry count, effective
  element overrides, category visibility, and category-aware matching view filters.
- `revit-focus-element` now performs two separate supported `ExternalEvent` passes so Revit can
  finish the UI redraw before the evidence image is captured. A reversed-order live trial proved
  that the earlier blank/point export followed the first redraw, not the chosen element.
- Live comparison of created element `1735503` with exemplar `1622255` matched family/type,
  linked ceiling face, 201.166 mm plan bounds, four visible geometry objects, orientation,
  flip/mirror state, category visibility, filters, and graphics overrides. The created symbol then
  rendered fully in first-command evidence `f7b364fa...96c0`; no RVT mutation or provider egress
  occurred. The user's saved-view copy was preserved at SHA-256 `1C390703...BCCB8`.
- Readiness is now `v16`; the focused Python tests and Revit 2024 DLL build pass.

## 2026-10-10 - Revit linked-ceiling execution gate implemented

- Added separate fixed execute/verify actions for exactly one zero-rotation `WorkPlaneBased`
  lighting fixture on an exact linked architectural ceiling face; the existing level-based path
  remains unchanged.
- The execute action revalidates exemplar, symbol, link, linked document, ceiling, stable face,
  in-face point, and a 250 mm centre-clearance integration gate, then uses one rollback-capable
  transaction and saves only to a nonexistent workspace RVT. Reopened verification rechecks the
  output hash, created element ID, symbol, point, and host face.
- Installed readiness `v14`; all 39 Revit tests and the Revit 2024 DLL build pass. Fresh preview
  `37FA4716...99CFDC8E` was explicitly approved. Live request `465adfae...dfa7` created element
  `1735503` in a new RVT with SHA-256 `B9D6A610...296D30`; reopened read-only verification
  `76d5e38b...a3f5` matched the exact output, symbol, point, and linked host face. Source and backup
  retained `9015E932...04CBEB30`; visual acceptance remains open.

## 2026-10-10 - Revit linked-ceiling face geometry live acceptance

- Added a fixed read-only `host_face_query` action that validates the reference fixture, exact
  stable linked-face identity, loaded link, architectural ceiling category, planar geometry, and
  bounded triangulation before returning in-face candidate points.
- Live request `a109ccf2-b464-4fe6-8aee-8ef32c14ae56` read a 4,789,345.26 mm2 ceiling face and
  returned a leading distinct candidate at `(13592.175, 11213.042, 2743.2)` mm with 366.486 mm
  centre clearance. Source SHA-256 remained `9015E932...04CBEB30`; no mutation or egress occurred.
- Created review-only preview `DC5AE2D0...998525C5`, bound to the exact link, ceiling, face,
  family type, and point. Hosted execution remains disabled pending explicit review and a separate
  rollback-capable new-file execution implementation and acceptance.

## 2026-10-10 - Revit linked-ceiling preview contract

- Extended the non-mutating family preview with one exact linked-ceiling face binding per
  `WorkPlaneBased` placement: link instance, linked element, linked document, stable face
  reference, and locale-independent ceiling key.
- Level-based previews reject host faces; face-based previews reject missing, extra, malformed,
  or non-ceiling bindings. The existing execution handoff remains level-based only.
- Created reference-only preview `E1EC3667...69659B55` for symbol `1453791` and ceiling
  `1566775`. It repeats the exemplar point solely to verify identity, has
  `execution_allowed=false`, and is not an approved new fixture location.
- Twenty-four preview/integration/handoff tests pass. A bounded face-geometry read and distinct
  non-overlapping target remain required before any hosted write action.

## 2026-10-10 - Revit linked-ceiling discovery live acceptance

- Added bounded hosted-only and ceiling-hosted-only category queries, using the stable
  `OST_Ceilings` identity plus strict result-side category keys rather than localized names.
- Resolved linked face identities through supported Revit `Reference` APIs and fail closed when
  a link document is unavailable; restored the Autodesk architectural/structural sample links
  beside the isolated workspace copy without changing their bytes.
- Added a throttled `Idling` recovery signal so missed `FileSystemWatcher` events do not strand a
  pending request or create a tight loop.
- Live query `194fa83e-0e09-4628-8229-86622aaf5cae` found four exact architectural-ceiling
  candidates. Fixture `1622255` binds symbol `1453791`, link instance `1362762`, ceiling
  `1566775`, and an exact stable face reference; an independent family-type query confirmed
  `WorkPlaneBased`. Twenty-six focused tests and the Revit 2024 DLL build pass. No hosted write
  action is enabled yet.

## 2026-10-10 - Revit read-only host identity candidate

- Extended bounded family-instance reads with local/linked host identity, linked document and
  element metadata, and the exact `HostFace` stable representation when available.
- Added fail-closed validation for inconsistent or incomplete linked-face records; no hosted
  placement or new mutation action is enabled.
- Twenty-five focused tests and Revit 2024 DLL compilation pass; live Snowdon host discovery
  remains queued.

## 2026-10-10 - Revit per-instance rotation live acceptance

- Added a self-hashed normalized `rotation_degrees` field for every new family placement;
  old previews remain verifiable as zero degrees.
- Rotation is applied around each actual `LocationPoint` vertical axis inside the existing
  all-or-nothing transaction and checked to 0.001 degrees before commit and after reopen.
- Invalid ranges, non-finite values, missing rotations, and count mismatch fail closed.
- Twenty focused tests and the Revit 2024 DLL build pass. Approved preview
  `DAFEA9FE...D41090CE` created three elements at `0`, `45`, and `90` degrees; reopened
  verification matched their IDs, points, levels, elevations, angles, and output
  `C5AFEA4A...09D02187` without mutation or egress. The operator's isolated L2 screenshot
  visually confirmed the horizontal, diagonal, and vertical orientations.

## 2026-10-10 - Revit same-type family batch live acceptance

- Extended the reference-bound `OneLevelBased` new-file handoff from one instance to a
  bounded 1..32 same-family/same-level batch in one Revit transaction.
- Added fail-closed coordinate, common-elevation, count, unique-element-ID, output-hash,
  and reopened exact-ID/point verification.
- Kept rotation, mixed types or levels, hosted families, parameters, circuits, overwrite,
  and direct save outside the allowlist.
- Passed 16 focused tests, compiled and installed the DLL against Revit 2024, then executed
  separately approved preview `64D00C9F...77089B74`. One transaction created elements
  `1735582`, `1735583`, and `1735584`; reopened read-only verification matched all three and
  output `09070A9D...A0A8475` without mutation or provider egress. Source and backup retained
  `9015E932...04CBEB30`. The operator's isolated L2 Lighting screenshot then visibly confirmed
  three aligned, equally spaced selected symbols and the approved family type; room association,
  clearance, photometrics, and code compliance remain outside this placement acceptance.

- Live-accepted the separately bounded Revit single-family handoff. The final preview
  binds exact symbol `1456719`, level `9946`, reference instance `1457231`, and zero
  elevation from level. Revit verifies the reference and proposed physical location
  inside the transaction before commit, creates a verified backup, and saves only to a
  new workspace RVT. Output `6F19E8EA...ECEE167C` contains element `1735582`; reopened
  read-only verification matched its type, level, and point with no provider egress.
  Two earlier outputs remain rejected evidence because their Z coordinate was derived
  from a bounding-box centre rather than the family's actual `LocationPoint`.

- Added a bounded, read-only Revit family-type discovery action. It reports loaded
  electrical family/type IDs, placement mode, host requirement, and activation state for
  four allowlisted categories. Live Snowdon acceptance found a level-based under-cabinet
  downlight type and enabled the first exact, hash-bound placement preview. Preview
  `031A293E...A4B594CB` identifies `symbol_id=1456719`, `level_id=9946`, one point,
  and `OneLevelBased` placement. It remains review-only; no family-placement transaction
  or save path is enabled. Twenty-one focused Revit tests pass.

- Live-accepted the separately gated Revit new-file handoff. The fixed action created exactly
  four approved Model Lines, uses `SaveAs` with overwrite disabled, and accepts only a
  nonexistent `.rvt` below the source workspace. A second non-mutating action verifies
  the reopened output hash, element IDs, and line endpoints against the exact preview.
  Explicitly approved preview `56EF15B0...4E79A2AC` created output SHA-256
  `78DAEE6D...D13625` with element IDs `1058904`, `1058906`, `1058908`, and
  `1058910`. Reopened read-only request `10a90880-90d4-4916-89a2-75adc45a29b1`
  verified the hash and all four exact geometries without mutation or provider egress.
  The source retained SHA-256 `B9D1DB38...691FC5E4`; source overwrite and direct save
  remain unavailable. The Revit 2024 DLL builds and 14 focused tests pass.

- Live-accepted the first approval-bound Revit transaction. Exact preview
  `F9F06DE7...B7743CB0`, bound to the Dynamo workspace copy and its SHA-256,
  created exactly four Model Lines in one transaction with element IDs `1058904`,
  `1058906`, `1058908`, and `1058910`. The bridge reported `saved=false`; Revit
  was closed without saving, and the source plus verified timestamped backup retained
  SHA-256 `B9D1DB38...691FC5E4`. Direct save, source overwrite, and arbitrary Revit
  commands remain disabled.

- Added an exact Model Lines preview contract for the first bounded Revit transaction.
  It accepts 1..16 horizontal 10..100000 mm segments, binds the exact workspace RVT hash,
  expires, self-hashes, and permanently denies execution and saving in the preview
  artifact. Tampering, path escape, source mismatch, invalid coordinates, and non-horizontal
  lines fail closed. The first four-line 1000 x 1000 mm review square has preview SHA-256
  `F9F06DE7...B7743CB0`; its separately approved live execution is recorded above.

- Added nine allowlisted displayed parameters to bounded Revit category results and
  live-tested three Snowdon electrical panelboards with circuit, mark, upstream-panel,
  type, and geometry data. Added a self-hashed, expiring, source-RVT-bound placement
  preview for 1..32 millimetre points. It is always review-only and rejects tampering or
  source mismatch; family placement/host compatibility remains a mandatory execution
  hold point. Ten focused Revit tests pass.

- Added live chat-driven bounded Revit category reading for 13 fixed architectural and
  electrical categories. Results contain at most 32 element summaries with document-local
  ID, name, type, family, level, and bounding box in millimetres. Unknown categories and
  excessive limits fail closed. Live request `77615722-3d76-4413-b3f2-8302fb422275`
  returned three of 38 walls during the same session with no egress, mutation, or save;
  the model hash remained unchanged. Eight focused tests pass.

- Added the first chat-driven Revit request loop. The local `revit-read` command writes
  an exact document-bound UUID request with egress and mutation disabled; a user-scoped
  watcher raises Revit's supported `ExternalEvent`, which accepts only the fixed
  inventory action and writes a correlated response. Same-session live acceptance
  returned the dual-verified Dynamo sample counts with request
  `939465da-c92f-4ec3-af93-8b0435e108e7`, no button press, no egress, and no mutation.
  Revit closed without saving and the model SHA-256 stayed unchanged. Seven focused
  tests pass. Parameter/geometry queries, previews, transactions, saving, and drawing
  remain disabled.

- Added and live-tested the user-directed Autodesk Revit 2024 read-only connector
  foundation. A new
  `revit-readiness` command detects the installed official API and .NET 4.8 build
  prerequisites; a user-scoped `IExternalApplication` installs an AI-Engineer ribbon
  panel and fixed `TransactionMode.ReadOnly` inventory command. The bounded JSON loader
  rejects path escape, oversized/malformed counts, provider egress, or any mutation flag.
  After the operator resolved Autodesk licensing, live Revit startup reported `ready`
  and registered the ribbon command. A process-only one-shot acceptance trigger captured
  Autodesk's Snowdon Towers electrical sample. The first run exposed and drove a
  `UseSimpleDictionaryFormat` correction; the final 977-byte result passed the strict
  loader with 588 lighting fixtures, 200 electrical-equipment instances, 530 conduits,
  80 MEP spaces, and 448 electrical systems. Revit closed without saving and the source
  SHA-256 stayed `9015E932...4CBEB30`. A second acceptance on Autodesk's 6.46 MiB
  Dynamo sample cross-checked every category through two independent Revit collectors;
  38 walls, 5 doors, 7 windows, and 14 rooms matched. The strict loader passed and the
  copy retained SHA-256 `B9D1DB38...691FC5E4`. The bounded inventory connector is live
  accepted; no selection/parameter/geometry or model-write capability is enabled.

- Added a source-preserving lumen-method lighting overlay for the 150 m2 house
  concept. The separate DXF provides 31 general and 3 task luminaires, calculated
  maintained illuminance for nine spaces, fixture-specific utilization factors,
  an 0.80 maintenance factor, spacing checks, and CSV/JSON/Markdown schedules.
  The drawing now also contains a gridded room-illuminance schedule and an adjacent
  symbol legend with type, description, watts, lumens, CCT, CRI, IP, quantity, and
  served design-illuminance criteria on dedicated table layers.
  Every stated general target and spacing check passes; connected lighting is
  505 W (3.37 W/m2). Final IES/LDT photometric simulation, glare/uniformity,
  bathroom zones, emergency lighting, circuit/protection design, current
  clause-level Egyptian review, and licensed-engineer approval remain hold points.

- Added an isolated 150 m2 single-storey architectural concept generator for AutoCAD
  review. The assumed 15 x 10 m footprint uses nominal 200 mm walls, a private foyer,
  1.30 m clear corridor, three bedrooms, living/reception, kitchen, bathroom and utility
  space on separate editable DXF layers. A self-hashed JSON and review report record
  Article 93/94/96 room, dimension, clear-height and natural-opening checks. The artifact
  is explicitly not a permit, structural, MEP or construction release; plot, orientation,
  setbacks, building ratio and local-authority review remain required.

- Accepted the portable AutoCAD 2023 candidate on the destination Windows profile. The
  non-mutating readiness report passed with the installed English `24.2.53.0` product,
  Python 3.13.15, .NET Framework 4.8, all six Autodesk dependencies, and an available
  localhost port. The bridge built against the destination assemblies, 69 focused tests
  passed, and the documented manual-load fallback completed exact read-only status plus a
  410-entity bounded context capture in 86.05 ms at `DBMOD=0 -> 0`. Source and disposable
  copy retained SHA-256 `e0bf950a...dec4bb04`; provider egress and drawing mutation were
  false. The separate second-profile performance matrix and any Scratch mutation remain open.

- Added the approval-gated HIGH-risk `autocad.summarize_drawing_index_for_ai` command.
  It independently reloads and hash-validates a complete captured drawing index, then exposes a
  sub-4-KiB engineering inventory containing bounded counts, top-level block names, point bounds,
  selected drawing notes, scope, and explicit engineering hold points. It does not disclose local
  paths or complete entity records and cannot mutate the drawing. The Hutchison R03 index produced
  a 2,972-byte summary for all 410 entities; focused compile and tests pass (`64 passed`). Live
  Gemini plan/action approval and the resulting engineering response remain to be accepted.

- Updated the opt-in Gemini launcher/provider default to the stable `gemini-3.5-flash-lite`
  Interactions model and added bounded exponential retry delays of 1 and 2 seconds for
  transient HTTP 429/503 responses. Provider credentials remain process-only, every retry
  reuses the same exact reviewed request, and local approval/execution authority is unchanged.
  Focused Gemini provider and AutoCAD live-tool tests pass (`10 passed`).

- Built the r50 second-PC continuation bundle with sanitized current portable source, tests,
  Hutchison Ports R01-R03 and all 17 supplied front-view component sources, Arabic startup and
  continuation notes, and a checksum-verified non-overwriting installer. Windows installation,
  source/test/asset restoration, existing-directory refusal and corruption refusal passed locally;
  175 focused tests and compile checks pass. Gemini startup now optionally selects AutoCAD
  2021/2023 and a destination installation root without changing the accepted 2021 default.
  Credentials/session history are excluded; native 2023 and component-move execution acceptance
  remain open. This is a continuation candidate, not a Professional 1.0 release.

- Added a read-only existing-component relocation foundation: the registered HIGH-risk
  `autocad.preview_component_moves` command validates 1..32 exact Model-space block translations,
  preserves transform evidence, binds distinct source/context hashes, checks sandboxed source
  identity before/after import, and emits a deterministic self-hashed proposal. Native Project
  Agent selection uses the supported AutoCAD command-context API; plan preparation now shows a
  fail-closed goal/observation egress review. Fixtures verify malformed/unsupported contexts,
  locked layers, source tampering and durable approval/rejection. Native relocation execution,
  geometric-clearance certification and optimized/fabrication-ready panel layouts remain disabled.

- Added a reviewed `Project Agent` tab to the AutoCAD palette. An engineer can enter a natural-
  language project goal, inspect the provider's bounded plan, approve or reject it, and watch the
  existing plan-act-observe-verify engine execute registered typed tools. One approved plan may
  automatically continue LOW/MODERATE local steps when the visible option is enabled; HIGH or
  destructive commands still stop at a separate exact action review and durable one-use command
  approval. The palette exposes no arbitrary AutoCAD command string, shell, permanent blanket
  permission, or bypass of the scratch/preview/recovery drawing lifecycle.

- Added a natural one-button AutoCAD chat path. `Ask AI` can create a bounded general session on
  first use without drawing context, retain a bounded reviewed conversation history, and optionally
  bind a separately captured drawing context. Every provider call still shows the exact egress
  preview and a fail-closed Yes/No decision; no DWG file, drawing write, or arbitrary AutoCAD
  command is exposed. Added the authenticated `/v1/copilot/general-sessions` contract, explicit
  `general` context scope, provider instructions for natural multi-turn discussion, API/contract/UI
  tests, and a successful AutoCAD 2021 DLL build.

- Created and live-reviewed Hutchison Ports panel GA previews R01-R03 in AutoCAD 2021 from 17
  user-supplied Schneider 2D front-view DWGs. R03 adds a proposed two-section 1600 x 2000 x 600 mm
  arrangement, bottom-entry cable zone, indicative Form 3b/busbar/PE/N/gland geometry, door layout,
  device tags, abbreviation key, part numbers, quantities, title block, and explicit TBD hold points.
  The workflow preserved every source hash and recorded the final preview SHA-256; the drawing is
  review-only and is not a workshop or IEC 61439 fabrication release.
- Added an isolated Schneider panel hybrid 3D review workflow that imports reviewed manufacturer
  STEP geometry through AutoCAD Core Console, normalizes inch-based source units to millimetres,
  rotates source axes into the panel coordinate system, and places 31 actual component instances.
  Nine unresolved components retain clearly identified temporary envelopes, busbars are omitted by
  request, and the workflow emits hash-bound evidence without mutating an existing project drawing.
  The result remains review-only and is not a fabrication release.
- Added the Manufacturer Asset Collector backend for exact reviewed product identities. It searches
  approved official domains for an ordered request of data sheets, 2D CAD, 3D CAD, and images;
  returns candidates without selection/download; then accepts a separate self-hashed ordered
  download approval. PDF, DWG, DXF, STEP/STP, IGES/IGS, JPEG, and PNG are quarantined into
  manufacturer/part-number/order folders with bounded size, signature validation, public-DNS and
  no-redirect/no-proxy transport, atomic no-overwrite writes, hashes, and full lineage. ZIP and
  executable content remain unsupported, and collection cannot register a catalog item or change a
  drawing. A bilingual interactive launcher now performs the two reviews, preserves the requested
  output order, supports separately approved manufacturer product/CDN hosts, and keeps the provider
  key process-only. Clean-installed portable candidate r47 passed focused tests and package checks.
- Added an AutoCAD 2023 second-PC portable profile without replacing the accepted AutoCAD 2021
  path. Readiness schema v2 selects the exact 2023 installation root, records expected `ACADVER`
  family `24.2`, validates required Autodesk assemblies, selects installed .NET 4.8/4.7.2 reference
  assemblies, and performs no provider egress or drawing mutation. The destination launcher builds
  the bridge against that PC's own AutoCAD 2023 assemblies and retains the read-only-copy, localhost,
  active-document, DBMOD, approval, recovery, and verification gates. A timestamped diagnostic JSON
  supports later machine-specific adaptation without guessing from chat memory.
- Fixed the dedicated Gemini-to-AutoCAD launcher so an unrelated running VS Code session no longer
  blocks startup. AutoCAD isolation, occupied-port detection, provider consent, session-only secret
  handling, read-only preview, and drawing safety gates remain unchanged. Its default bridge port is
  now `8766`, isolated from the existing Operator/VS Code session normally using `8765`.
- Gemini provider failures now preserve a bounded, API-key-redacted Google status/message through
  the local copilot boundary, replacing an unactionable generic `HTTP 400` during live acceptance.
- Fixed Gemini REST response parsing for the documented `steps[].content[].text` structure instead
  of relying only on the SDK-only `output_text` convenience property.
- AutoCAD copilot summaries now distinguish raw `INSERT` block-reference count from recognized
  electrical-device count, so unknown or unregistered blocks remain countable without inventing
  their engineering type.
- AutoCAD copilot summaries now also distinguish raw `LINE`/`LWPOLYLINE` route primitives from
  recognized electrical routes and label both recognized counts explicitly. A live 184-entity
  `lighting.dwg` selection contained 57 raw blocks and 42 raw route primitives while its new exact
  context had no rebound convention profile; the former zero-device/zero-route display therefore
  represented classification state, not an empty drawing.
- The corrected summary passed live source-tree acceptance on a second complete selection: the
  palette and independent evidence inspection both reported 49 raw blocks and 38 raw route
  primitives, while retaining zero classified devices/routes and all 156 unknown objects until an
  exact convention profile is reviewed. No provider egress or drawing mutation occurred.
- The copilot palette now displays `Session active` after successful creation and enables Start,
  local Send, egress Preview, approval/rejection, and Cancel only when their prerequisite state
  exists, preventing the misleading always-enabled sequence observed during live Gemini review.

All notable repository changes are recorded here.

## Unreleased

### Added

- Added a provider-planned, allowlisted live AutoCAD tool channel to the VS Code developer
  workflow. Gemini can now request exact document status, show the existing AI-Engineer palette,
  or request a bounded full-drawing index; it cannot submit AutoCAD command text or authorize
  drawing mutation. The full index remains HIGH risk and therefore requires the existing durable
  human approval. Provider observations contain bounded metadata and hashes, not local paths or
  drawing contents.
- Added regression coverage for the three fixed tools, risk levels, command-text rejection, and
  metadata-only no-mutation results. The focused Gemini/operator/API/foundation checks pass; live
  Gemini-to-AutoCAD round-trip acceptance remains intentionally open.
- Built clean portable candidate r41 and verified its manifest, readiness, Operator configuration,
  and installed command catalog. Archive SHA-256 is
  `144513c13c64ee4414220aa98fee1ac93ec34f9420567a8ca0452b5bb9ca68dd`; r40 remains the daily
  installation pending r41 live acceptance.
- The Gemini launcher now installs the reviewed bundled VS Code Operator into an isolated runtime
  profile and opens a separate window connected to the same loopback token/port. Existing VS Code
  windows remain open, never inherit the Gemini key, and no longer prevent the Gemini-to-AutoCAD
  typed-tool workflow.
- Built and installed candidate r42. Its live bridge and installed AutoCAD status action passed at
  `DBMOD=0 -> 0` with no mutation, and extension 0.1.4 installed into the isolated profile. Final
  panel acceptance paused because the workstation's VS Code updater mutex is still held; the exact
  resume condition is recorded in the live-trial evidence rather than relying on chat memory.
- Updated the VS Code Operator to 0.1.5 with an explicit webview-ready handshake before the first
  health request and immediate visible Refresh feedback. This removes the startup race that could
  discard the first status message and leave the panel at `Connecting to AI-Engineer...`.
- Built and clean-installed side-by-side candidate r43 with Operator 0.1.5 present from first
  process startup. Its 41 focused Gemini/operator/provider/package tests and installed readiness
  check pass. Archive SHA-256 is
  `003164050e2217a9cafa9d65f53e9c7c5c997b5f32eea35849bcea3b65d1dc6f`;
  live visual status and one reviewed Gemini fixed-tool run remain pending a clean r42 shutdown.
- Fixed the remaining r43 panel stall in Operator 0.1.6 by loading the webview controller as a
  packaged local resource under a restrictive CSP and registering the extension message listener
  before assigning the page. The panel then connected automatically without Refresh.
- The reviewed live Gemini typed-tool acceptance passed: Gemini proposed only
  `autocad.get_live_document_status`, the engineer approved the exact plan, one verified action
  completed at `DBMOD=0 -> 0`, result SHA-256
  `91e412229a0dc1cd828bd1e8d2d9188691655fde378d0756c77b9e321e005eb0`, and
  `drawing_mutation_performed=false`.
- Built and clean-installed candidate r44 containing Operator 0.1.6. Its archive SHA-256 is
  `b0dfc6a51e35011541f5531b3b6a2d0a6d248793142b0c01be93d996540b94b8`;
  installed readiness passes with 201 files and all 18 AutoCAD product files.

- Added a deterministic, self-hashed read-only classification audit for `lighting.dwg`. It validates
  the existing schedule-candidate evidence and accepted source identity, records all 45 circuit
  references, seven occurrence conflicts, 35 unresolved loads across 12 non-conflict rows, 647
  route candidates, and 3,856 unclassified entities. Candidate occurrence totals remain explicitly
  non-unique and spatial membership is never promoted to connectivity. The engineer deferred final
  Panel Schedule/SLD, protection, cable, phase, and final-load review to a clearer future drawing.
  Three focused tests and Python compilation pass; no AutoCAD read, mutation, or provider egress was
  performed.

- Added the initial Gemini Code Assist Agent-to-AutoCAD MCP boundary. A dependency-free local
  stdio server exposes only the fixed `ai_engineer_autocad_status` tool and delegates it to the
  authenticated loopback AI-Engineer task engine. Command text, drawing entities, save, and mutation
  are absent from the surface. A backup-preserving installer registered it in the current Gemini
  user settings without storing either API token, and repository-scoped Gemini instructions forbid
  shell/COM/UI/AutoLISP bypass. Automated MCP, live-tool, Operator, compilation, installer, and
  packaging checks pass; one Gemini Agent `/mcp` + `/tools` + live status acceptance remains open.

- Registered the engineer-supplied `PANELS SCH 8 STATION.xlsx` as an immutable reusable
  panel-schedule template. Its exact SHA-256, seven worksheet identities, `D3` panel-name cell,
  used ranges, formula counts, merged-cell counts, and generation policy are now validated by
  automated tests before a generation plan can be issued. The source workbook remains unchanged,
  unsafe/duplicate Excel sheet names fail closed, and exports must target a new workbook.
- Added a deterministic one-sheet-per-panel generation-plan contract for the registered template.
  After explicit engineer approval, the Excel COM adapter now clones only the requested source
  sheets into a new workbook, preserves formulas/formatting/merged cells, updates the reviewed
  `D3` panel identity, rejects formula overwrites and duplicate/unsafe sheet names, styles only
  explicitly estimated input cells red, and refuses to overwrite the source, template, or an
  existing output. The six-panel acceptance workbook passes full recalculation, zero formula
  errors, unchanged-template SHA-256, and visual rendering of every sheet.

- Added a bounded, append-only verified-operational-learning registry loaded during application
  startup. Every active lesson is self-hashed, bound to immutable evidence and engineer review,
  and can fail closed on a known-forbidden action; it cannot alter source code, grant approval,
  select products, or mutate drawings automatically.
- Added an atomic operational-incident journal for future failures. It deduplicates repeated
  scope/trigger/action candidates, counts occurrences, retains timestamps/evidence hashes, and
  permanently labels them `candidate_only` until a separate reviewed lesson and regression test
  are supplied.
- Captured four reviewed 2026-08-24 CAD lessons covering native DXF compatibility, deterministic
  STEP import, exact manufacturer-part matching/SHA deduplication, and exact drawing handoff.
  Added regression coverage so the corrected workflow survives future sessions.
- Added local manufacturer CAD intake for bounded STEP/DWG/DXF/SAT/IGES files. It verifies STEP
  structural completeness, hashes every artifact, reports exact duplicates, accepts only exact
  normalized commercial-reference matches, and leaves similar references explicitly unbound.
- Added a hash-bound AutoCAD STEP/Core Console pipeline contract. It produces only fixed
  non-interactive `FILEDIA=0`/`IMPORT`/`SAVEAS` commands and requires `Import Job Complete`, a
  non-empty output DWG, exact hybrid instance counts, `DBMOD=0`, and unchanged source identity.
- Verification for this change passes 33 focused foundation/learning/CAD tests and Python
  compilation. The repository suite passes 566 tests with one skip when two unrelated consistency
  checks are excluded; both outstanding checks are recorded in `docs/WORK_QUEUE.md` rather than
  silently changing experimental files.

- Added a deterministic preliminary Schneider panel general-arrangement producer for the supplied
  BOM. It creates self-hashed JSON/SVG/PNG evidence plus a standalone ASCII DXF with 237 entities,
  millimetre units and 1:1 model-space geometry for an `NSYSM16840` 1600 x 800 x 400 mm enclosure.
  The internal and door views reserve bottom cable entry, top cable exit, wiring ducts and thermal
  zones while retaining incomplete references, mounting plate, CTs, busbar/fault duty and thermal
  verification as visible VERIFY/TBD items. Existing project drawings are not changed.
- Added a standalone 1:1 millimetre 3D envelope DXF for the same panel: 43 equipment, duct and
  proposed-busbar solids represented by 258 `3DFACE` entities inside the 800 x 400 x 1600 mm
  enclosure. Equipment uses short IDs plus a separate reference/dimension legend, eliminating the
  overlapping full labels. Red objects remain unresolved and the model is explicitly not a
  fabrication release; no existing drawing was modified.
- Replaced the initial hand-written 3D DXF serialization after AutoCAD 2021 rejected it and returned
  to the prior document. The producer now uses a standards-aware R2010 DXF writer, records metric
  units and a shaded isometric active viewport, and passes an independent audit with zero errors or
  fixes. The corrected DXF SHA-256 is `A302E2B9...6C3198`.
- Added the first supplied-manufacturer-geometry hybrid panel preview. AutoCAD 2021 successfully
  translated exact `RE22R1MYMR.stp` and the two `KT1`/`KT2` dimensional boxes were replaced by two
  real STEP-derived blocks on `3D-EXACT-STEP`. A separate read-only reopen confirmed both instances,
  their reviewed WCS bounds, 357 model-space entities and `DBMOD=0`; all other devices remain
  envelope-only and no existing project drawing was changed.

- Added a replacement no-mutation `DB3-GA2` SLD preview derived only from the visually reviewed
  right-side occurrence rather than the earlier whole-drawing candidates. It shows exactly ways
  1-7, keeps 1/3/5 blank, assigns the reviewed shared 5/7 component only to way 7, and extends way
  6 to its eight reviewed load blocks after route completion. Way 7 reports only its known
  180 W / 205.6 VA partial load and leaves the total TBD because L04 and LED-tape wattages remain
  unresolved. The self-hashed JSON/SVG/PNG preview is `d29072e3...32a1f2`; no AutoCAD drawing
  mutation was performed.

- Added a project-specific, removable AutoCAD review overlay for the right-side occurrence of
  `DB3-GA2` circuits 2, 4, 6, and 7. Exact source handles bind 30 original route geometries and
  28 load blocks to preview SHA-256 `afa1bb24...e03097`; circuit 6 includes its reviewed
  T-junction and stable 255-unit device bridge, and the former shared 5/7 component is explicitly
  assigned to circuit 7 by engineer instruction. Live Scratch execution created exactly 65 review
  entities (`4,848 -> 4,913`) on two removable layers, retained `DBMOD=0`, preserved the source
  drawing hash, and handed off a new review DWG. A separate read-only reopen confirmed 100 layers,
  4,913 entities, and clean unchanged output.

- Added the first project-specific, reviewed AutoCAD SLD producer for `DB3-GA2`. It accepts only
  the exact self-hashed preview and unchanged `lighting-working.dwg` SHA-256, rejects any occupied
  placement or pre-existing reserved layer, and creates the diagram on four dedicated layers in a
  copied Scratch drawing. Live AutoCAD 2021 execution created exactly 234 entities below the legend
  (`4,848 -> 5,082`) in 2.7063 seconds, retained `DBMOD=0`, and handed off a new drawing without
  overwriting the source. A separate read-only reopen confirmed 102 layers, 5,082 entities, clean
  save, and unchanged output SHA-256 `b3765d590dde716699b11ca453947062a83f2d7c4c15eb6303d69fdfc1d8d856`.

- Added the first project-specific no-mutation SLD preview for panel `DB3-GA2`, bound to the
  complete schedule-candidate evidence and unchanged source drawing hash. It displays numeric ways
  1-19, seven observed circuits, twelve blank non-spare positions, red provisional load values,
  and explicit TBD placeholders for incomer, busbar, protection, cable, phase, and frequency.
  SVG, PNG, and self-hashed JSON outputs passed a focused test and visual review; AutoCAD drawing
  execution was subsequently accepted through the bounded project-specific Scratch producer.

- Added a deterministic, read-only whole-drawing lighting schedule candidate analyzer. It validates
  every shard in the 4,848-entity index, avoids joining visual route crossings, preserves spatial
  membership as unreviewed, applies the engineer-approved legend/estimate hierarchy, and records
  unresolved loads and repeated-occurrence conflicts without fake totals. Four focused tests pass.
- Added draft numeric schedule envelopes for all ten observed panels. Rows run from way 1 through
  the highest numeric circuit reference observed per panel; missing ways remain blank and are never
  labelled as spare. The ambiguous `4Y / DB3-GA1` remains a separate review item. Equivalent repeated
  depictions are not double-counted; this independently reproduces DB3-GA2/circuit 4 as 311 W,
  351.111 VA, and 1.527 A at 230 V.
- Added the engineer-approved standalone Excel fallback for the draft lighting panel schedules.
  The 14-sheet workbook contains 181 way rows across ten panels, formula-driven PF/current/demand,
  explicit review/conflict sheets, load-basis provenance, and 137 intentionally blank rows for
  ways not observed in this drawing. Read-only reopen found 129 formulas and no formula errors;
  every worksheet passed rendered visual review.

- Added the first real-project draft panel/circuit register from the complete 4,848-entity
  `lighting.dwg` index: 10 explicit/inferred panels and 45 distinct observed circuit references.
  The report preserves missing load, phase, protection, cable, demand, and panel-system data as
  `UNKNOWN`, flags ambiguous `4Y / DB3-GA1`, and includes the prior reviewed DB3-GA2/circuit-4
  membership and route evidence without converting observed centerline into installed length.
- Recorded the engineer-approved draft panel-system assumption as 400/230 V, 3 phase, 4 wire,
  with lighting circuits based on 230 V line-to-neutral and individual L1/L2/L3 allocation still
  withheld pending load calculation and phase balancing. The report distinguishes this instruction
  from drawing-extracted evidence and from any compliance/as-built claim.
- Added an engineer-approved provisional load-estimation hierarchy that preserves source class and
  confidence. The first reviewed-membership example, DB3-GA2/circuit 4, now calculates to 311 W,
  351.111 VA, combined PF 0.8858, and 1.527 A at 230 V from explicitly listed legend/functional
  estimates; it remains blocked for breaker/cable compliance and final schedule approval.

- Installed portable r40 as the current-PC daily working copy at `D:\AI-Engineer`, with a
  separately copied `lighting-working.dwg` whose SHA-256 exactly matches the protected original.
  The installed AutoCAD 2021 bridge, loopback readiness, palette action, and complete 4,848-entity
  drawing index passed. The full index used 76 validated chunks in 1,247.7822 ms at `DBMOD=0 -> 0`
  with no drawing mutation. Second-profile/second-PC release acceptance remains deferred.

- Added the separately HIGH-approved `autocad.create_maximum_performance_fixture_scratch`
  release-test operation. It accepts only an exact source path/SHA-256, creates a fixed 1,792-LINE
  grid on a reserved previously absent layer in a copied drawing, verifies exact counts and clean
  save, and writes self-hashed recovery evidence with `handoff_allowed=false`. Production circuit,
  route, and symbol bounds are unchanged.
- Portable candidate r40 passed that maximum live Scratch gate: 4,848 to 6,640 entities,
  `DBMOD=0`, execution `2.4976913 s`, and total through recovery evidence `2.5679253 s`, below the
  30-second target. Source SHA-256 remained unchanged; output SHA-256 is
  `13283a1b15c676d66d998e807d631a0adb9dc62f655fe5ba34e42b31df5b825e`. Archive SHA-256 is
  `2c0fa7098c06c7029c382e0a39229d58dd72645c9b465e8253db835ce0c27a5f`. The suite passes
  `545 passed, 1 skipped`; only second-profile performance acceptance remains.

- Portable candidate r39 supersedes r29 after fixing Windows install ACL inheritance and adding
  full rollback for failed new installations. AutoCAD launch now fails fast when the local operator
  workspace path reaches the legacy 260-character boundary and directs the operator to a short
  install location. Diagnostic candidates r30-r38 are retained only as failure evidence.
- r39 passed clean-installed current-profile AutoCAD 2021 acceptance: exact status, 4,848-entity
  full-drawing capture in 76 validated chunks (`1.2566212 s`), independent reload, palette command,
  and engineer visual confirmation all completed at `DBMOD=0 -> 0`. The copied drawing closed
  without saving. Archive SHA-256 is
  `5ce420dbe212dfa0eca0f9151fca4f84a74d53e5734570f2d59ba57b7109a4a7`; installed DLL SHA-256
  is `1db8274fa48d00c52517fb6d30885165628ec9aab93eaf31b999b014ab969808`.
- The official suite now passes `542 passed, 1 skipped`. r39's installed five-run local-core
  benchmark passes all six targets; maximum-fixture Scratch execution/recovery and second-profile
  acceptance remain open.

- Added an event-backed, in-session AutoCAD drawing-delta channel. A successful full index arms an
  exact baseline; append, modify, and erase events record disjoint candidate handles; the
  allowlisted no-mutation `capture_drawing_delta` action exports only live added/modified entities
  and records removed handles. The journal is one-use, limited to 4,096 candidates, and fails
  closed after restart, absent baseline, overflow, or capture failure.
- Added a strict Python delta-manifest/shard loader and automatic capture CLI with exact baseline,
  document, current-source, DBMOD, partition, and changed-handle coverage validation. The AutoCAD
  2021 x64 plugin builds and the official source suite passes 541 tests with one skip (excluding
  the unowned `tests/test_project_config.py`).
- Live same-session Scratch delta acceptance passed with exactly one added (`1A1C523C`), one
  modified (`1A1C51F4`), and one removed (`1A1C520C`) handle, excluding unchanged control
  `1A1C5224`, in 25.6422 ms at `DBMOD=0 -> 0`. The first retained diagnostic attempt exposed that
  managed `Database` wrappers are not stable reference identities; the tracker now binds and checks
  AutoCAD's unmanaged database identity instead. The writable acceptance launcher is restricted to
  exact managed-run `source.dwg` inputs; normal Preview copies remain read-only.
- Added immutable drawing-inventory schema v2 and the `autocad_inventory_delta` CLI. Composition
  requires exact baseline document/source/index/catalog lineage, rehashes every changed context,
  rejects candidate conflicts and tampering, recalculates catalog-stable meaning, withholds all
  source-bound reviewed convention/topology identities, and sets `semantic_review_required=true`.
  Composition of the live accepted delta produced 4,851 entities and compared as exactly one added,
  one removed, one geometry-changed, zero reclassified, and 4,849 unchanged handles.
- Expanded AutoCAD release-readiness coverage from 12 to 18 exact product files so the complete
  drawing-index, event tracker, delta loader/capture CLI, inventory-v2 composer, and refresh
  coordinator cannot be omitted from a portable package.
- Portable candidate r28 packages that complete pipeline. After `541 passed, 1 skipped`, its clean
  install passed 18-file readiness, installed x64 DLL compilation, installed inventory-delta CLI
  startup, and all six five-run local-core targets. Archive SHA-256 is
  `fab3870f357994e61853810043553f6a10d70cf1b74352a9370d05602251f4ea`; installed DLL SHA-256
  is `904d85ad0ab2a5492c8592b52ac63670eb40c1b100875991e4a95d4c88ef5778`.
- Fixed clean portable installation to create empty `workspace`, `logs`, and `backup` runtime
  directories atomically after archive validation. This removes the manual workspace creation
  required before r28's first installed AutoCAD-plugin build attempt.
- Portable candidate r29 supersedes r28 with that installer correction. Its 173-entry archive
  SHA-256 is `e51521bb96271fdae9ae7a8179a54603804af3e9d547d0d5406639184f52fd04`.
  A 172-payload-file clean install created all three runtime directories, passed 18-file readiness,
  directly compiled the installed x64 DLL, started the installed inventory-delta CLI, and passed
  all six five-run local-core targets. The official suite remains `541 passed, 1 skipped`.

- Added a no-selection automatic AutoCAD inventory-refresh coordinator. It discovers exactly one
  requested active drawing, uses the existing expiring no-mutation `capture_drawing_index` channel,
  validates the complete result, builds the immutable inventory, compares an optional baseline,
  and records a self-hashed local refresh report. If the DWG source hash changed, stale reviewed
  conventions and route membership are not reused; structural comparison proceeds with an explicit
  semantic-review-required flag. Live coordinator acceptance remains separate.
- The automatic coordinator passed unchanged-source live AutoCAD 2021 acceptance on an isolated
  `lighting.dwg` copy: 4,848 entities were indexed in 1.093 seconds at `DBMOD=0 -> 0`, reviewed
  semantics remained source-valid, and comparison returned zero additions, removals, changes, or
  reclassifications with 4,848 unchanged. The source SHA-256 remained unchanged and the copy was
  closed without saving. Changed-source acceptance is recorded in the following separate run.
- Changed-source automatic-refresh acceptance passed on an explicitly approved Scratch copy with
  one added LINE (`4,848 -> 4,849`). The refresh detected exactly new handle `1A1C51F4`, no false
  removal or geometry change, withheld/reclassified all 991 source-bound semantic observations,
  and set `semantic_review_required=true` at `DBMOD=0 -> 0`. The original `lighting.dwg` remained
  byte-identical; exact evidence is recorded separately.
- Added a strict regular-file loader for reviewed route-membership evidence and automated coverage
  for unchanged-source refresh, changed-source semantic invalidation, baseline comparison, active-
  document ambiguity, and no-mutation reporting. The official source suite passes 536 tests with
  one skip (excluding the unowned `tests/test_project_config.py`).

- Added an immutable drawing-wide inventory over the complete chunked AutoCAD index, with strict
  independent reload validation and deterministic handle-level added/removed/geometry-changed/
  reclassified comparison. Route-layer matches are candidates only; exact reviewed membership
  handles are required before a primitive is classified as an electrical route, and reviewed
  leader/arrow handles remain annotations.
- The first source-bound `lighting.dwg` inventory retained all 4,848 top-level entities: 336
  reviewed devices, seven reviewed electrical routes, two reviewed annotation polylines, 647
  route candidates, and 3,856 unclassified entities. Its self-comparison returned zero changes
  and 4,848 unchanged entities. Selective change-triggered AutoCAD recapture remains pending.

- Added an automated-verified, no-mutation `capture_drawing_index` operator action. The
  AutoCAD 2021 plugin now discovers every top-level entity in Model and paper layouts,
  orders identities deterministically, exports complete non-truncated adaptive context
  shards (starting at 64 entities and halving when payload density requires it), and writes
  one source-SHA-bound manifest with exact chunk hashes, totals, scope,
  DBMOD, and duration. The local loader rejects missing, changed, truncated, overlapping,
  cross-document, or partially counted chunks. Nested block definitions and Xrefs remain
  explicitly outside this first index scope.
- Added a local active-document beacon so the external operator can discover the idle open
  drawing's document ID and exact source SHA-256 before enqueueing an allowlisted request.
  It publishes no drawing geometry, accepts no command text, caches the drawing hash until
  file metadata changes, and retains `drawing_mutation_performed=false`.
- The source-tree full-drawing index passed live AutoCAD 2021 acceptance on `lighting.dwg`
  without a manual command or selection: 4,848 unique entities were validated across 76
  exact adaptive chunks in 1.119 seconds at `DBMOD=0 -> 0`, and the source SHA-256 remained
  unchanged. Exact evidence is recorded in
  `docs/evidence/autocad-full-drawing-index-live-acceptance-2026-08-23.md`.

- Added an independently opt-in Gemini Interactions API provider for developer reasoning and the
  read-only AutoCAD copilot. It uses the provider-neutral strict JSON contracts, a fixed Google
  endpoint, `store=false`, disabled redirects/proxies, bounded request/response sizes, and an
  environment-only key behind separate enable and egress-confirmation flags. Provider status,
  portable packaging, a secure Windows launcher, and failure-path tests include Gemini. It has no
  direct AutoCAD command or drawing-write authority; live API acceptance remains separate.
- Portable candidate r21 contains the multi-provider selector and secure Gemini launcher after the
  `524 passed, 1 skipped` source baseline. It supersedes r20 as the current delivery candidate;
  archive SHA-256 is `b5a1862b6abf6f3fd6d454b1afd80706dbd71d16e618d870603c803ba8ea655a`.
- r21 is superseded because its Gemini launcher stopped at the general Operator/VS Code bridge.
  Candidate r22 adds an explicit `UseConfiguredProvider` boundary to the existing isolated AutoCAD
  preview launcher and makes the Gemini launcher start that AutoCAD path, so the in-product palette
  receives the selected Gemini provider while the default preview path still forces `provider=none`.
  Archive SHA-256 is `7af856c3568d8a1b772c7398111756b15f9d3ae31838bb08dfed0232785aac6c`.
- r23 supersedes r22 only to include the corrected in-package README and provider-boundary
  documentation describing the direct Gemini-to-AutoCAD launch path. Archive SHA-256 is
  `efdd38ab042de3632f0f719d30dba507bb1ff6aa0d2cd8cc9eb7ae35e7460a42`.
- r24 supersedes r23 with the live-discovered Gemini REST response parser correction, bounded
  redacted Google error diagnostics, independent default port `8766`, and raw AutoCAD `INSERT`
  count in copilot summaries. It passed `526 passed, 1 skipped`, clean install, twelve-file
  readiness, installed x64 DLL compilation, and all six installed local-core targets. Archive
  SHA-256 is `1c70cfbbcc44741ffa7fd2a8c5966cb60e6dcb74974627fabb52ea646c83fe8a`.
- r25 supersedes r24 only so the portable archive itself contains the corrected Gemini launcher,
  provider-boundary, port, and raw-block documentation. It passed a second clean install,
  twelve-file readiness, installed x64 DLL compilation, and all six installed local-core targets.
  Archive SHA-256 is `16a261761348a6bea850a607d6dde3a7cb33b66fa62445972aff19f2f5912a37`.
- r26 supersedes r25 with the prerequisite-aware copilot button state machine and visible active-
  session label. It passed `526 passed, 1 skipped`, isolated x64 DLL compilation, clean install,
  twelve-file installed readiness, installed DLL compilation, and all six installed local-core
  targets. Archive SHA-256 is `f5268cfcf31c9d55e13b3bf052d0b3c34114b17b565c37ac9d8f7133a6a88398`.
- r27 supersedes r26 with explicit classified labels and a separate raw `LINE`/`LWPOLYLINE`
  primitive count. It passed `526 passed, 1 skipped`, source-tree live acceptance, clean install,
  twelve-file readiness, installed x64 DLL compilation, and all six installed local-core targets.
  Archive SHA-256 is `2da7fd67c3993db4349be768577e53e749d38d7f0203bf3f9616c648f3cc4fff`.

- The unclear `lighting.dwg` connection-review batch and its derived reports were superseded rather
  than trusted. Review UI v2 now zooms to the device plus exact endpoint and draws a red endpoint
  cross with a yellow guide. Thirteen uncertain decisions were repeated individually. The final
  reviewed partition contains seven electrical routes with 14/14 connected endpoints and two
  polylines explicitly classified by the engineer as one circuit-number arrow/leader, not cable.
  Electrical centerline is now `9.3919227269 m`; annotation centerline `0.8854071156 m` is excluded.
  Connection-review SHA-256 is `c58bdf23acfec53d319e9bd222e93317ce791efd12359df0135eb576f5b870db`.
- A live bounded Scratch execution against `lighting.dwg` added exactly one `LINE` only to copied
  output run `ceca1501d7ed42199182498b1b00ef39` (`4848 -> 4849`) in 2.1374 seconds. Source SHA-256
  remained `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`; output SHA-256 is
  `7774bfb14d1b8983a1b5c3180b29af5b5e4bfdd4b811721e54cede57dba2e34f`, with recovery evidence.
- Portable candidate r12 is superseded pending a new package containing endpoint-marker-v2 and the
  reviewed electrical/annotation route partition.
- Portable candidate r13 contains both corrections. After `514 passed, 1 skipped`, it built 156
  archive entries, installed 155 files into a clean directory, passed the ten-file AutoCAD readiness
  gate on port 8766, rebuilt the installed x64 plugin, and passed all six installed five-run local-
  core targets. Archive SHA-256 is
  `e77679fcfd372254a60937e58f77b8eb949627e5cfc6a6d51519d16cd43b6b8d`.
- The preview launcher now validates `ActiveDocument`, `FullName`, exact disposable path, and
  `CMDACTIVE=0` through a null-safe helper both before and after bootstrap settling. If the reviewed
  document disappears it cleans up both exact processes and fails with an actionable error instead
  of dereferencing a null COM object. Live retest opened `lighting - Read Only 20260816-141125.dwg`
  at `DBMOD=0` and closed that exact copy without saving.
- Scratch mutation failures now distinguish source/hash corruption from a fixed validation refusal.
  When the copied drawing remains unchanged, bounded zero-valued `*VALID` markers are reported in
  the actionable error. A live `lighting.dwg` circuit-fragment retry now reports
  `VALIDREFERENCECOUNT=0`, `VALIDSEGMENTCOUNT=0`, and `WIRELAYERVALID=0`; source identity remained
  unchanged and no invalid output was saved.
- Portable candidate r14 is superseded because it predates the actionable Scratch validation-marker
  diagnostic. Portable candidate r15 contains all current corrections. After `515 passed, 1 skipped`,
  it built 156 archive entries, installed 155 files into a clean directory, passed installed product
  check and ten-file readiness on port 8766, rebuilt the installed x64 plugin, and passed all six
  installed five-run local-core targets. Archive SHA-256 is
  `adc80c5457734122583f3efa2b6ac140cd3356b982f89c297686ad15245bd651`.
- AutoCAD drawing-context export now prints `AIENG_CONTEXT_DURATION_MS` from the existing bounded
  stopwatch after extraction, atomic file write, SHA-256 calculation, and evidence publication. The
  strict context JSON schema remains unchanged. Before that instrumentation, a live current-profile
  full-drawing capture rendered the palette with 512 entities, nine layers, `DBMOD=0`, and expected
  `maximum_entities` truncation; the source drawing remained unchanged.
- Portable candidate r16 contains the duration marker. After `515 passed, 1 skipped`, it built 156
  entries, installed 155 files cleanly, passed product/readiness checks, rebuilt the installed x64
  DLL, and passed all six installed five-run local-core targets. Archive SHA-256 is
  `75e49e3dc8125ae9bd4acd3d3276d0c9cad2f0a5669accc8440b7d6589dc9ac5`.
- A separate AutoCAD operator command channel now polls only exact UTF-8 JSON requests for three
  fixed no-mutation capabilities: `capture_drawing_context`, `get_document_status`, and
  `show_palette`. Requests expire within ten minutes and bind document fingerprint, source drawing
  SHA-256, action, mutation=false, and request SHA-256. The plugin accepts no AutoCAD command string,
  `SendStringToExecute`, or write-open database access.
- Live acceptance sent all three actions without a manually entered AutoCAD command. Context capture
  returned 512 entities in `26.5282 ms`, context SHA-256
  `9f25972864e9666dc66c721750b4be7ddce2502669c99bb33540cbbf4ad37b54`, and
  `DBMOD=0 -> 0`. Status and palette actions also completed at `DBMOD=0 -> 0`.
  The engineer visually confirmed the AI-Engineer tab was present and the palette opened on click;
  AutoCAD and the loopback bridge then closed cleanly without saving.
- Portable candidate r18 contains the operator channel and bounded CLI. After `519 passed, 1 skipped`,
  it built 158 entries, installed 157 files cleanly, passed twelve-file readiness, rebuilt the
  installed x64 DLL, and passed all six installed local-core targets. Archive SHA-256 is
  `42f51b99d17d14fbbf352c57b234834d0603225bbf3b226b651aacd6c150e2bf`.
- Cooperative operator-channel cancellation now uses an exact, expiring, request-SHA-bound cancel
  record and checks between drawing entities. Live request `17a946ac5a994e20a67f4a38e31b2756`
  returned `status=cancelled` in `619.1593 ms`, emitted no partial context, and performed no drawing
  mutation. A separate status request then confirmed `DBMOD=0 -> 0`; both detected project-PC
  physical drives are healthy NVMe SSDs.
- Portable candidate r19 was the clean installed pre-documentation checkpoint. Candidate r20
  supersedes it as the current delivery build, includes the accepted cancellation implementation
  and updated operator documentation, and passes the `520 passed, 1 skipped` source baseline,
  twelve-file readiness, installed x64 DLL build, and all six installed local-core targets. Its
  archive SHA-256 is `cd483d51701148c92ec244e6a9b720fb82bb4673499d92d6f7fa3ea2ada1b7af`.
- The AutoCAD preview launcher now removes its own startup-only DBMOD=4 contamination automatically.
  It waits for the fixed plugin script to finish, validates the exact disposable drawing through
  AutoCAD COM, and, only when forbidden DBMOD bits exist, closes that read-only copy without saving
  and reopens the same hash-identical file read-only. A live AutoCAD 2021 test finished with exactly
  one document, `DBMOD=0`, `CMDACTIVE=0`, and the source SHA-256 unchanged.
- Portable beta candidate r12 supersedes r11 and includes that launcher correction. It passed clean
  install, ten-file readiness, installed DLL compilation, and all six local-core benchmark targets;
  archive SHA-256 is `02693c2437e96146061f0b66d7bb3defd05ff0df2f3f276fc1b32e3ad5e3cc19`.
- Portable AutoCAD beta candidate r11 was built after `514 passed, 1 skipped`, installed into a
  clean directory, passed ten-file product readiness, rebuilt the x64 AutoCAD DLL, and passed all
  six installed five-run local-core benchmark targets. Archive SHA-256 is
  `84ad450f19d9665f817c682292310c95bab003c532e2630c4b25a62c2cd63ee4`.
- The `lighting.dwg` acceptance builder now emits a self-hashed calculation-readiness record. Units,
  circuit membership, and complete endpoint accounting pass, while load ratings, electrical system,
  cable/conductor, installed length, protection, and project code profile remain explicit UNKNOWN.
  No calculation, compliance decision, provider call, or drawing mutation is claimed.
- A capability-scoped two-way AutoCAD review channel now lets the local AI-Engineer process enqueue
  one expiring, exact-hash connection-pair request while the AutoCAD 2021 plugin polls locally,
  verifies the open drawing/document/handles/DBMOD, fits and highlights only that device and route,
  and asks the engineer Yes/No. It never accepts an arbitrary AutoCAD command and the request fixes
  `drawing_mutation_allowed=false`. The plugin atomically returns the exact endpoint geometry,
  evidence hashes, presentation DBMOD transition, decision, and no-mutation flag. The first live
  `lighting.dwg` cycle approved device `1A1C2F20` to route `1A1C308D/start`; its result SHA-256 is
  `25c101a066bc7a9a0e0dba6c980de84908982e32c130bc1ab8ce501ffa42c289` and its validated stored
  anchor SHA-256 is `0c54fd83d26f0d483fe8ac33f31ddc58be6dc8b2c39a23e1dd986e0134f677a7`.
  The unchanged source remains `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.
- The representative `lighting.dwg` connection review now accounts for all 18 endpoints exactly
  once: 11 engineer-approved device-terminal connections and seven explicitly unresolved endpoints
  backed by exact rejected modal-result hashes. Unresolved means no reviewed candidate was accepted;
  it does not fabricate a disconnected conductor. The immutable review SHA-256 is
  `69e28f6854ff3fd495cefa050729135d14e531f260900496e7262d53ab3d9145` and its file SHA-256 is
  `73d8c7efacdfe7322758a9433d44a8ea54ec6a0bf99f0e4a319dde676db5167e`.
- Exact reviewed connection-anchor evidence now joins separate device/tag and route contexts without
  using proximity or crossings. Every route endpoint must be recorded exactly once as connected to
  an engineer-reviewed device terminal/world point or unresolved; foreign handles, displaced points,
  evidence mismatches, gaps, duplicates, and tampering fail closed. Read-only AutoCAD command
  `AIENGCONNECTIONANCHOR` captures one explicit block + route + engineer endpoint pick + terminal
  decision. A first live usability trial showed that operators cannot reliably know a polyline's
  internal Start/End direction, so the command now asks them to click near the intended end, records
  both distances, selects only the strictly nearer endpoint, and defaults the terminal to `CIRCUIT`,
  verifies unchanged DBMOD, and records `drawing_mutation_performed=false`. The AutoCAD 2021 DLL
  builds; manual and channel-assisted live `lighting.dwg` endpoint review is now in progress.
  A later live attempt exposed pre-existing `DBMOD=20`; capture now rejects every non-presentation
  DBMOD bit before writing instead of accepting unchanged-but-dirty state. The outside-circuit and
  DBMOD-20 captures were retained under `connection-anchors/rejected`, not deleted.
- AutoCAD real-project convention review now has a read-only `AIENGDEVICETAGCONTEXT` command that
  filters an explicit selection to block references and TEXT/MTEXT, preventing unrelated long route
  geometry from truncating a device/tag evidence capture. The preview launcher now starts and records
  the exact Python bridge process and runs an exact-PID monitor that stops it when AutoCAD exits;
  startup failure also cleans up the bridge. This replaces the prior PowerShell-parent PID lifecycle
  that could leave Python orphaned. Full verification is `500 passed, 1 skipped`.
- The live `lighting.dwg` block/text-only command captured 26 non-truncated entities at DBMOD=16:
  12 block references and 14 MTEXT records with no route geometry. The exact self-hashed profile
  recognizes all 12 devices (L05, L02, toilet spots, EF-02, and three reviewed switch types), panel
  `DB3-GA2`, circuit `4`, and zero unknown objects. `WP` is modeled as an exact Weatherproof
  environmental qualifier, separate from control identifiers `a/b/c`; neither kind is spatially
  associated without a separate reviewed topology record.
- The Copilot palette now retains and redraws the imported session summary when the operator presses
  Refresh, instead of replacing verified device/route/unknown/warning counts with only the raw
  context entity/DBMOD line. Cancelling a session explicitly clears the retained summary. Reopened
  AutoCAD 2021 acceptance visually retained 12 devices, zero routes, zero unknowns, and five warnings
  after Refresh, with provider egress and drawing writes both disabled.
- Exact reviewed AutoCAD topology sidecars now bind circuit membership by stable source object
  handles rather than selection order. The `lighting.dwg` record assigns all 12 reviewed devices to
  circuit `4 / DB3-GA2`, preserves 12 `a/b/c` tag observations and one `WP` qualifier, and explicitly
  keeps control association and route connectivity false. Changed evidence/profile/source identities,
  missing/foreign handles, inferred control/route claims, malformed files, and tampering fail closed.
- Read-only command `AIENGROUTECONTEXT` now filters an explicit region to LINE/lightweight-POLYLINE
  entities on one separately picked representative layer. This avoids architectural-line ingestion
  and does not infer crossing connectivity. The bridge point bound is raised from 256 to 4,096 so
  the observed long circuit polyline can remain complete; legacy contexts retain and validate their
  original stricter 256-point policy.
- The first live route-only `lighting.dwg` capture contains nine complete E-Lite-Circ polylines,
  zero unknowns, and a maximum 1,025 vertices. Its self-hashed route-membership record assigns all
  nine stable handles to circuit `4 / DB3-GA2` and deterministically reproduces 10.2773298425 m of
  observed drawing centerline. It explicitly leaves endpoint connectivity and installed cable
  length unconfirmed; allowances, drops, waste, and unrepresented geometry are not invented.
- Representative `lighting.dwg` acceptance began with a byte-stable read-only 4,848-entity/98-layer
  inventory and two explicit selections. The trials exposed and fixed rejection of valid mirrored
  AutoCAD block scales; negative non-zero scales are now retained while zero remains invalid. The
  evidence honestly records selection truncation, persistent visible-copy DBMOD bits, and 102
  unclassified objects pending explicit project convention review.
  A third 15-entity selection is complete and non-truncated and supplies exact candidate identities
  for two fixture families, two switch families, one circuit tag, control tags, and a short circuit-
  layer polyline. No candidate meaning is activated before engineer review.
- Exact-context project conventions now use a strict self-hashed profile bound to the document,
  context evidence, and source drawing. After engineer approval, the normal import service
  recognizes the reviewed `lighting.dwg` fixture/switch aliases, route layer, panel/circuit tag,
  and signed mirrored scales. It never infers connections at crossings or spatially associates
  control tags; changed/tampered/duplicate profiles fail closed.
  Exact-source rebind rejects truncation and every unreviewed block, layer, tag, or entity class.
  Fresh AutoCAD 2021 palette acceptance displayed 7 reviewed devices, 6 endpoint-only routes,
  8 unknowns, and 5 warnings at DBMOD=16 with local-session/no-egress/no-write boundaries intact.
  A broader proposed topology selection was correctly rejected for new unreviewed blocks and
  DBMOD=20; the rebind gate now explicitly permits only view/window bits 8/16 and rejects persistent
  object, database-variable, and field bits 1/4/32.
- Portable package builds now exclude post-build package-identity evidence, preventing a new ZIP
  from embedding stale/self-referential candidate identities. The current dated candidate passed
  clean install, readiness, integrity inspection, and its packaged five-run local-core benchmark;
  its exact revision and hash remain in the external evidence record.
- `launcher.py autocad-benchmark` now measures five-run small/maximum deterministic context parsing,
  preview creation/hashing, and LV calculation with exact fixture cardinalities, output identities,
  target comparisons, hardware metadata, and a self-hashed no-overwrite JSON report. All six local
  trials passed on the destination PC, while the result honestly keeps the release gate false until
  AutoCAD capture/display and copied-scratch execution/recovery are measured live.
- Funded Quality manufacturer discovery passed live AutoCAD 2021 acceptance with Legrand `004280`
  and Schneider Electric `A9A15306`, both revalidated from exact engineer-saved official HTML and
  passing all three mandatory requirements. The hash-bound Legrand choice remained separate from
  product approval. An initial 45-second palette timeout did not cancel the bounded server result;
  the exact repeat used the in-process cache without a duplicate paid call. Product Discovery now
  waits 210 seconds, slightly longer than the server's 180-second hard bound. Milestone 23 is closed
  within its documented no-download/no-catalog/no-drawing boundary.
- Paid manufacturer discovery now fails over from an explicit official-page HTTP 403/503 only to
  previously engineer-saved official HTML whose host and part number match identities returned by
  that paid search. The saved bytes still pass exact part/property checks and local SHA-256 hashing;
  unmatched products fail closed and the provider identity records `:saved-evidence`. No browser
  impersonation or access-control bypass is attempted.
- Manufacturer-discovery acceptance artifacts are now stored beneath their canonical ISO review
  date. A newly funded run on a later day cannot collide with or overwrite the prior immutable
  evidence package; invalid or path-like dates fail closed.
- The current full verification baseline is `496 passed, 1 skipped`; Python compileall and the
  AutoCAD 2021 x64 palette build also pass. The portable candidate was installed into a clean
  local destination, passed runtime/product-file checks and read-only AutoCAD readiness, and
  materialized the packaged canonical Free candidate template with identical SHA-256. No provider
  call or drawing mutation was used for these release-preparation checks.
- AutoCAD beta performance gates now define bounded small/maximum fixtures and measurable targets
  for context import, preview, deterministic calculation, scratch verification/recovery, and cancel
  acknowledgement. The protocol requires exact package/drawing identities, five-run median and
  maximum, unchanged-source evidence, and honest target-miss reporting; live measurements remain
  outstanding and no performance pass is claimed yet.
- An honest `AUTOCAD_SUPPORTED_FEATURES.md` now distinguishes live-accepted, automated-only,
  prepared, and unsupported capabilities across drawing context, drafting, calculations,
  panels/SLD, code/manufacturer evidence, providers, packaging, and future connectors. It states
  explicitly that raw lines/crossings do not establish cable semantics and that broad code
  libraries, unrestricted drawing edits, automatic product actions, ETAP, and SOLIDWORKS remain
  outside the current beta boundary.
- `python launcher.py autocad-readiness` now produces a machine-readable, read-only AutoCAD beta
  preflight covering Windows/Python, six fixed AutoCAD 2021 files, three .NET build files, seven
  exact product-source SHA-256 identities, and localhost-port availability. It starts no process,
  calls no provider, and reports provider egress and drawing mutation false. The destination PC
  passed `complete=true`; exact evidence is recorded under `docs/evidence`.
- Paid manufacturer discovery now caches up to 32 successfully validated results by exact
  self-hashed request for the life of the local API process. Repeated clicks for the identical
  approved request reuse the immutable candidate tuple without a second billable provider call;
  changed requests remain separate, and failures are never cached.
  The exact configured model is also embedded in the signed result's provider name and API log
  (`openai-official-search:gpt-5.6-luna` or `openai-official-search:gpt-5.6`), making Economy
  and Quality runs distinguishable during cost and evidence review.
- The AutoCAD `Product Discovery` palette now gives its complete comparison output the
  expandable area of the panel, wraps long URLs/hashes instead of collapsing to one horizontal
  line, sizes all action buttons to their labels, stacks candidate/rationale/choice controls,
  and provides `Copy complete result`. The copy operation is local UI state only and grants no
  product, download, catalog, or drawing authority.
  Live AutoCAD 2021 acceptance confirmed the full button labels, scrollable wrapped results,
  visible Legrand identity/URL and all three `PASS` findings, stacked review controls, and the
  explicit no-follow-on-action status.
- Product discovery now has three explicit launch modes: `Free` verifies a user-supplied
  candidate JSON file by independently fetching approved official pages without any AI
  provider, `Economy` uses OpenAI `gpt-5.6-luna` with low reasoning, and `Quality` uses
  `gpt-5.6` with high reasoning. The AutoCAD palette exposes both free verification and paid
  search, records the correct provider-egress flag, and preserves every no-selection,
  no-download, no-catalog, and no-drawing-mutation boundary.
  For official sites that return anti-automation `403`/`503`, Free mode also accepts an
  engineer-saved UTF-8 HTML snapshot inside the workspace. The sandboxed API validates and
  hashes the saved bytes, checks the approved official URL/host, part number, and exact property
  evidence locally, and never claims provider egress or automated retrieval. A two-product
  browser-save template is included under `workspace/manufacturer-evidence`.
  Live AutoCAD 2021 browser-assisted acceptance passed with exact saved-page hashes for Legrand
  `004280` and Schneider `A9A15306`, three verified properties each, two candidates, and a
  hash-bound engineer choice of Legrand for separate review. Provider/retrieval, product
  approval, selection automation, download, catalog registration, and drawing mutation all
  remained false. Exact identities are recorded in the M23 discovery evidence document.
- An authenticated strict manufacturer-package review API and read-only AutoCAD
  `Manufacturers` tab. Exact nested evidence, retrieval, quarantine, model-sidecar, symbol
  candidate, and catalog-lineage records are reconstructed and rehashed; deterministic
  review displays `PASS`/`UNKNOWN`/`CONFLICT` without retrieval, registration, provider
  egress, or drawing mutation. Official Legrand part `004280` live acceptance passed with
  a 182,314-byte PDF and separately quarantined 95,300-byte DWG, exact bundle/result hashes,
  and three visually confirmed `PASS` findings. Typed `.dwg`/`.dxf` approval now safely
  handles official servers that declare CAD as generic binary only when the exact URL
  extension and file signature agree.
- Project-scoped self-hashed manufacturer-discovery approvals now bind category/search
  text, reviewed official domains, model hash, and mandatory/optional requirements to
  exact shared-model, calculation, or engineer-input source hashes. A bounded injected
  provider returns strict official-host candidates only; deterministic comparison reports
  `PASS`/`FAIL`/`UNKNOWN`/`CONFLICT` per requirement and never selects, downloads,
  registers, or edits a drawing. Authenticated `POST /v1/manufacturers/discover` and the
  AutoCAD `Product Discovery` tab display cited candidates and part numbers. A production
  opt-in OpenAI Responses web-search adapter restricts hosted search to the reviewed official
  domains, requests strict structured output, then independently re-fetches every exact
  product page without proxy/redirect, rejects private DNS and oversized/non-text content,
  verifies the part number and property evidence in the returned bytes, and computes the
  source SHA-256 locally. A new authenticated selection route and palette control record a
  self-hashed engineer choice for separate review without approving the product, downloading,
  registering, or changing a drawing. An absent provider returns a sanitized 503 instead of
  fabricated results. Unsupported strict-schema length/cardinality keywords were removed
  from the external request while equivalent and stronger bounds remain in local validators;
  typed provider failures and bounded API detail are now visible in the palette. The full
  baseline is `482 passed, 1 skipped`; live
  multiple-candidate AutoCAD acceptance remains.
- A deterministic M23 discovery live-acceptance builder now emits exact source, request, and
  self-hashed manifest artifacts for a two-official-domain modular-socket fixture. A dedicated
  Windows launcher requires exact session egress confirmation, reads the API key securely,
  enables only manufacturer search while general provider mode stays `none`, removes the key
  before starting AutoCAD, preloads the reviewed request, and stops the temporary API when
  AutoCAD closes. The acceptance evidence template states the exact multi-candidate and
  negative-action criteria; the visible paid-provider run remains outstanding.
- Separately confirmed, backed-up manufacturer symbol-candidate registration with durable
  self-hashed source lineage across the evidence bundle, product/part number, CAD artifact,
  quarantine record, symbol definition, catalog before/after hashes and versions, and backup
  hash. Failed provenance persistence restores and verifies the exact prior catalog. The
  operation cannot claim drawing placement or mutation. Confirmation, success, backup, and
  rollback tests raise the full baseline to `452 passed, 1 skipped`.
- Self-hashed manufacturer-to-model sidecars that bind reviewed claims and artifact hashes
  to one exact project/context/model hash and existing model object without changing the
  model or drawing. Self-hashed manufacturer symbol candidates bind the evidence bundle,
  exact retrieval approval, quarantine record, CAD bytes/media/hash, and a manually reviewed
  proposal accepted by the existing catalog validator. Candidate creation cannot auto-approve
  geometry, register the catalog, or mutate AutoCAD. Five strict tests raise the full
  baseline to `449 passed, 1 skipped`.
- Self-hashed exact-URL manufacturer retrieval approvals and immutable quarantine records.
  The downloader requires an engineer-approved HTTPS URL/host, allowed type, byte cap, and
  optional expected SHA-256; disables redirects and proxies; rejects non-public resolved
  addresses, unapproved media, oversize/empty content, executable signatures, invalid
  PDF/DXF/DWG signatures, hash mismatches, and overwrite attempts; and grants no provider,
  catalog, or drawing authority. Five strict tests raise the full baseline to
  `444 passed, 1 skipped`.
- Immutable self-hashed `m23.manufacturer_evidence.v1` bundles for exact manufacturer,
  official domains, product family/model/part number/variant, artifact kind/revision/date,
  lawful access and license reference, locator/media/bytes/content hash, quarantined CAD,
  and bounded reviewed property claims with exact applicability. Official artifacts require
  an approved HTTPS host, CAD cannot leave quarantine through this contract, executable
  content is rejected, and creation grants no retrieval/provider/catalog/drawing authority.
  Five strict tests raise the full baseline to `439 passed, 1 skipped`.
- A reproducible Milestone 22 live-acceptance builder and AutoCAD launcher. They hash a
  downloaded official public source, create exact PASS/FAIL/UNKNOWN/CONFLICT fixture
  requests, preload one reviewed request into the read-only compliance palette, start an
  authenticated loopback-only API, and stop it when AutoCAD closes. The fixture explicitly
  disclaims authority for the Egyptian project. Automated outcome coverage raises the full
  baseline to `434 passed, 1 skipped`. The operator visually confirmed the exact citation,
  hashes, PASS result, completeness, satisfaction, and negative action flags; independent
  reconstruction reproduced result SHA-256
  `8d935a9dffd2fbd3c5ef356f6593351c763e35072b45850fd56430c59cc928ab`.
- An authenticated strict `POST /v1/compliance/evaluate` endpoint that parses the exact
  self-hashed project profile, clause evidence, deterministic rules, and engineering facts,
  rejects unknown fields/coercion/tampering, and returns the reviewed profile, citations,
  and deterministic result without retrieval, regulatory approval, or drawing mutation.
- A read-only AutoCAD `Code Compliance` palette tab that loads a bounded reviewed JSON
  request, validates exact profile/source/evidence/result SHA-256 bindings, displays clause
  locators and editions, and renders `PASS/FAIL/UNKNOWN/CONFLICT`. It contains no database
  write or AutoCAD command execution path. The AutoCAD 2021 x64 DLL builds and the full
  baseline passed before the live-acceptance builder was added.
- Self-hashed Milestone 22 clause evidence, deterministic rules, exact engineering facts,
  and compliance results bound to the pinned project profile, authority, source edition,
  content hash, reviewed clause locator, upstream result hash, decimal value/unit,
  formula, reviewer, date, and assumptions. Missing inputs return `UNKNOWN`; competing
  rules, duplicate fact identities, and unit mismatches return `CONFLICT`; both states
  fail closed. Evaluations cannot claim provider retrieval, regulatory approval, or
  drawing mutation.
- Immutable self-hashed `m22.code_profile.v1` project code profiles with jurisdiction,
  project type, contiguous authority precedence, pinned reviewed source editions/effective
  dates, lawful access basis, bounded locators/content hashes, explicit project
  requirements, reviewer, and assumptions. Profile creation cannot claim provider
  retrieval/compliance or store full protected code text.
- Self-hashed `m22.edition_impact.v1` discovery assessments bound to an exact active
  profile/source/authority. Newer or unknown editions require impact review while
  `activation_performed` is invariantly false, preventing silent edition changes.
- Strict tests for tampered hashes, invalid calendar dates, non-HTTPS official sources,
  broken authority order, unknown sources, full-text claims, mismatched same-edition
  content, and non-activating newer-edition review.

- Opt-in local Operator Bridge runtime for IDE/CAD sidecar clients. It keeps the HTTP
  control plane on authenticated `127.0.0.1`, supports explicit extra workspace roots,
  and moves operator backups to a separate external vault so source access never grants
  access to recovery copies.
- Optional fixed-endpoint OpenAI Responses API adapters for the reviewed developer agent
  and AutoCAD copilot. Credentials stay in process environment only; redirects and
  proxies are disabled; provider/data-egress enablement is explicit; local contracts
  continue to validate every plan, action, verification, answer, and citation.
- Loopback-only Ollama adapters with structured JSON output and a CPU-friendly default
  coding model, plus ChatGPT-authenticated Codex CLI adapters that run ephemerally in an
  empty read-only temporary directory with an environment allowlist and no project-execution
  authority.
- Cost-aware hybrid routing that keeps structurally valid work local and escalates only
  local provider/contract failures to Codex, with separate one-click Local, Codex, and
  Hybrid Windows launchers and explicit Codex data-egress confirmation.
- Provider-neutral developer/copilot schemas and an engineering-assurance architecture
  that reserves calculations, standards evidence, compliance, and safety-significant
  acceptance for deterministic/provenance-backed layers rather than model memory.
- A dependency-free VS Code Activity Bar side panel for reviewed developer runs. It shows
  exact plans/actions, can auto-continue only LOW/MODERATE local steps when opted in, and
  always stops for HIGH/DESTRUCTIVE action and durable command approvals.
- Bounded `python.compileall` and approval-gated `python.pytest` developer commands so the
  local agent can compile and test code without unrestricted shell access.
- `start-operator.ps1` and a reviewed VS Code extension installer for the local operator
  experience.
- Cross-platform rejection of foreign Windows absolute paths in `FileManager`, closing a
  test-visible containment ambiguity outside Windows.

- Immutable schema-v1 Milestone 18 proposal contracts for multiple proposed devices,
  terminals, circuits, endpoint-bound routes, exact layer schemas, and explicit
  missing, unknown, or ambiguous inputs.
- Exact project/document/drawing bindings to context evidence, canonical shared-model,
  and reviewed symbol-catalog SHA-256 values with an explicit unit system.
- Self-hashed read-only preview snapshots that cover every proposed object exactly,
  stay within the proposal lifetime, and cannot claim drawing mutation or execution
  approval.
- Deterministic fail-closed proposal validation for exact model/catalog bindings,
  identity collisions, physical units, reviewed symbols and attributes, layer schemas
  and state, bounded geometry, terminal endpoint positions, and connected acyclic
  circuit topology.
- Immutable self-hashed validation results whose preview permission is true only when
  the ordered finding set is empty.
- Atomic durable drafting lifecycle state containing exact proposal/validation/model/
  catalog hashes, hashed owner keys, bounded expiry, cancellation, separate review
  decisions, replay-safe terminal states, and metadata-only audit events.
- Strict bounded no-coercion JSON proposal parsing with exact fields, structure limits,
  cycle/non-JSON rejection, and canonical content-hash verification.
- Trusted drafting planning orchestration that re-imports exact local AutoCAD context
  evidence and the reviewed local symbol catalog before deterministic validation and
  durable lifecycle creation.
- Authenticated loopback drafting create/get/cancel/preview-review/decision routes with
  separate hashed ownership, per-run operation limits, proposal-bounded expiry,
  durable approval, replay rejection, and no drawing-write authority.
- Authenticated transient drafting preview creation that re-imports exact local AutoCAD
  evidence, reloads the reviewed catalog, revalidates the proposal, returns every ordered
  preview object/geometry, and durably binds only preview ID/SHA-256/expiry. Review cannot
  begin before preview; mismatched, replayed, expired, or wrong-owner previews fail closed.
- An AutoCAD `Drafting Preview` palette tab that loads an explicitly selected reviewed
  proposal from the configured workspace, checks its current context ID/evidence hash,
  creates the read-only preview first, and displays every ordered object and geometry
  with mutation and execution-approval flags. Execution controls remain disabled until
  a later exact source-bound review and separate one-use approval.
- A local metric preview-acceptance proposal builder plus a Windows launcher that builds
  the bridge, scopes the Operator API to the exact workspace, creates a verified OS-read-only
  drawing copy, restores `TRUSTEDPATHS`, and opens the palette without saving the drawing.
- A separately reviewed drafting execution contract that binds the exact preview,
  proposal validation, source path/SHA-256, reviewed symbol placements, and `E-WIRE`
  geometry before one-use approval. The authenticated API creates only a verified
  AutoCAD Core Console scratch output and records timestamped source, output, and
  recovery-evidence hashes; handoff remains separately approved.
- Durable `execution_review_pending`, `executing`, `executed`, and `recovery_required`
  lifecycle states with restart persistence, mismatch rejection, terminal replay
  prevention, and conservative failure handling.
- Separate AutoCAD palette controls for execution review, approve/reject, and execute,
  including exact source hashing, two explicit confirmations, and verified recovery
  output. The palette contains no source replacement or handoff command.
- Immutable self-hashed `m19.takeoff.v1` quantity reports bound to exact AutoCAD context
  evidence and shared-model hashes, with recognized-device counts linked to every device
  and source object.
- Deterministic 3D route-length calculation in meters for explicit inch, foot,
  millimeter, centimeter, and meter drawings. Geometric length, vertical allowance,
  per-end termination allowance, waste, calculated total, and reasoned reviewer override
  remain separate fields; unknown units, incomplete segments, and unknown-route overrides
  fail closed.
- An authenticated read-only `POST /v1/takeoff/reports` route that re-imports the exact
  local context evidence and returns a transient report with no drawing mutation or
  provider egress. Unclassified objects remain explicit and make the report incomplete.
- Deterministic UTF-8 CSV export that is issued only after the exact reviewed report
  SHA-256 is supplied and the server re-imports and recalculates the same context. Rows
  retain source document/object, context/evidence, report, and calculation identities;
  formula-like reviewed text is escaped and row/byte limits fail closed.
- A `Quantity Takeoff` AutoCAD palette tab for explicit allowances, local report review,
  and separately confirmed hash-bound CSV display. It has no drawing-write or file-write
  path and rejects mismatched API evidence.
- Immutable self-hashed `m20.lv_current_voltage_drop.v1` inputs/results for balanced
  single-phase and balanced three-phase current plus approximate steady-state voltage
  drop. Reviewed power, demand, voltage, power factor, efficiency, conductor R/X,
  assumptions, model object IDs, route geometry, formulas, units, source evidence, and
  limitations remain traceable.
- An authenticated read-only `POST /v1/calculations/low-voltage` route that re-imports
  exact AutoCAD context evidence, rejects missing or unsourced load/route identities, and
  records only bounded calculation/result identities. It performs no provider call,
  compliance evaluation, or drawing mutation.
- An `LV Calculation` AutoCAD palette tab that collects visible reviewed engineering
  inputs, derives recognized device/route IDs from the complete takeoff context, and
  displays formulae, numeric results, warnings, and explicit safety flags.
- Immutable self-hashed `m20.load_aggregation.v1` rule sets and results. Each source-backed
  load has explicit connected power, demand factor, and L1/L2/L3/three-phase allocation;
  every load belongs to exactly one explicit diversity group. Results retain group and
  phase totals, average, spread, maximum deviation, formulas, warnings, and source hashes.
- An authenticated read-only `POST /v1/calculations/load-aggregation` route plus visible
  AutoCAD controls for per-load power/demand, group diversity, and exact phase CSV. No
  balance limit, compliance decision, provider call, or drawing mutation is performed.
- Immutable self-hashed `m20.cable_ampacity_derating.v1` inputs/results linked to one exact
  source route. A reviewed cable designation, base ampacity/basis, and ordered uniquely
  identified factors retain every input/output stage, combined factor, formula, warnings,
  source hashes, and explicit false selection/compliance/mutation flags.
- An authenticated `POST /v1/calculations/cable-ampacity` route and AutoCAD controls for
  route number, cable designation, base ampacity, ambient/grouping factors, basis, reviewer,
  and assumptions. The route rejects unknown context routes, coercion, duplicate factors,
  missing bases, and uplift factors above one.
- Immutable self-hashed `m20.short_circuit_source_equivalent.v1` inputs/results for a
  reviewed single-phase complete loop or balanced three-phase bolted fault. Exact route,
  voltage basis/multiplier, source/path R/X, impedance basis, total impedance, symmetrical
  RMS current, apparent fault power, formulas, warnings, and source hashes remain traceable.
- An authenticated `POST /v1/calculations/short-circuit` route and AutoCAD controls for
  route, arrangement, voltage, multiplier, source/path R/X, basis, reviewer, and assumption.
  It explicitly denies protective-device adequacy, peak-current, arc-flash, compliance,
  provider, and drawing-mutation claims.
- Immutable self-hashed `m21.panel_sld_contract.v1` records for panels, ordered outgoing
  ways, reviewed protective devices, load assignments, and SLD nodes/edges. They bind to
  exact project/document/context evidence and canonical model hashes, preserve circuit
  identity across every representation, distinguish load ways from explicit spares, and
  fail closed on missing, duplicate, ambiguous, foreign, or conflicting relationships.
  The contract foundation has no schedule-generation or drawing-mutation authority.
- Deterministic self-hashed `m21.panel_schedule.v1` generation from an exact revalidated
  panel/SLD contract. Rows are ordered by way number and retain circuit names and IDs,
  phase/pole data, reviewed protective-device inputs, source-linked loads, and explicit
  load/spare totals. Results contain no extraction, compliance, provider, or AutoCAD
  mutation claim. Live API exposure remains behind supported semantic extraction because
  the current AutoCAD context mapper correctly leaves generic panel objects unclassified.
- Bounded `m21.panel_sld_extraction.v6` extraction for fixed explicit panel, way, SLD-node,
  and SLD-edge marker blocks. Exact attribute schemas populate a new semantic model only
  when every identity and topology relationship validates; truncated, malformed, missing,
  duplicate, or conflicting observations produce `UNKNOWN`/`CONFLICT` findings and no
  partial model. Node and edge records retain exact source AutoCAD object references.
- Stable load binding through atomic `LOAD_BINDINGS` load-ID/AutoCAD-handle pairs resolved
  against recognized devices in the exact context, eliminating selection-order coupling.
- Durable-approval-gated `autocad.create_panel_sld_fixture_scratch`, bound to an exact
  disposable source SHA-256 and fixed to four marker blocks, ten marker references, 58
  attributes with exact tag/value revalidation, one recognized load reference, exact
  11-entity growth, clean save, unchanged
  source, changed run-local `.dwg` scratch output (including from DWT input), and no
  automatic handoff.
- Live acceptance exposed and corrected missing fixture-local insertion-layer validation.
  The sandbox-profile attempt and initialized-user-profile fail-closed attempt consumed
  distinct approvals, retained unchanged source/scratch hashes, published no output
  evidence, and are recorded without a false live-success claim. `LAYERVALID=1` is now
  mandatory before block setup and in the parsed producer result.
- A subsequent live run verified exact 11-reference creation but rejected the long
  `LOAD_SOURCE_OBJECT_IDS` way attribute: both way references omitted it, producing 60/62
  attributes and no save. A second live attempt reproduced the same omission with
  `LOAD_HANDLES`, and a third attempt reproduced it with `LOAD_REFS`. The fixed profile
  now uses one atomic `LOAD_BINDINGS` field, omits empty load fields from spare ways,
  and resolves exactly one source reference using a bounded exact-name AutoLISP selection.
  A live Core Console rejection removed the attempted ActiveX conversion. Extraction is
  v6, fixture output is V6, precomputed binding and non-sensitive per-way diagnostics
  are enforced, and exact-context handle resolution remains mandatory.
- Live per-way diagnostics identified premature `entlast` capture for complex attributed
  inserts. Marker references are now captured only after their closing `SEQEND`, preventing
  one-marker-lag validation while retaining the same fixed content and safety gates.
- Live AutoCAD 2021 producer run `cdd846e7d3c24aa59400a12721f32bdb` passed profile V6:
  10 marker references, one recognized load, 58 attributes, way counts 15/13, valid
  binding/content, exact 0-to-11 entity growth, and `DBMOD=0`. The source stayed at
  SHA-256 `0b40e693...2479129`; the 37,266-byte saved scratch changed to SHA-256
  `82f8f287...dca2c`. Independent read-only inventory followed successfully.
- Independent read-only inventory run `34f3d86e784b492faf9ed50d0f4083de` passed with
  AutoCAD 24.0, two layers, 11 entities, `DBMOD=0`, unchanged source/scratch hashes,
  and no output truncation. Visible in-product `Panels / SLD` acceptance followed.
- Visible `Panels / SLD` complete-result acceptance passed for explicit 11-entity context
  `a5b86d29e9724b7bb94e6111edb26468`. Displayed extraction and schedule hashes matched
  independent local recomputation, panel DB-1 and its two ordered load/spare ways were
  correct, no egress/mutation was claimed, and the OS-read-only DWG hash stayed unchanged.
  The visible negative `UNKNOWN/CONFLICT` path was then exercised separately.
- Visible negative-path context `fc768c23c4324a3b8b9f0c57adbaac06` passed fail-closed
  acceptance: one selected panel produced three explicit `UNKNOWN` findings, no schedule,
  null semantic model/contract, and false egress/mutation flags. Independent recomputation
  matched extraction SHA-256 `f2933511...86404`; the OS-read-only drawing hash remained
  `82f8f287...dca2c`. Milestone 21 is complete within its fixed-marker boundary.
- Authenticated `POST /v1/panels/extract-schedule` server-side context re-import,
  extraction, and deterministic schedule generation with strict request validation,
  package coverage, no provider egress, and no drawing mutation.
- A `Panels / SLD` AutoCAD palette tab that reuses the exact explicit context, collects a
  bounded reviewer/assumption, validates extraction/context/contract hashes and all
  read-only flags, renders UNKNOWN/CONFLICT without partial semantics, and displays ordered
  way/circuit/phase/protection rows only for complete schedules. The AutoCAD 2021 x64 DLL
  builds successfully and the full automated baseline is `421 passed, 1 skipped`.

### Changed

- Synchronized the 2026-08-11 continuation checkpoint with the authoritative work queue:
  Milestones 0–20 are recorded complete within their documented boundaries, Milestone 21
  is active, the verified baseline after supported extraction and API coverage is `415
  passed, 1 skipped`, and visible AutoCAD palette/live read-only acceptance is the next
  task. No drawing mutation or new execution authority was introduced.

- Repaired the Windows Operator connection path: all bridge launchers now require a
  real loopback health response before reporting success, and VS Code extension 0.1.4
  reports API unavailable, authentication failed, or provider unavailable instead of
  leaving the panel indefinitely on Connecting. The rebuilt VSIX is covered by source-
  content/version integrity tests.
- Made Windows setup tests use an explicit unique pytest base directory and disabled the
  inaccessible shared pytest cache, with cleanup limited to the generated runtime path.
- Hardened the post-0.18.0 Developer Agent against no-progress local-model loops by
  blocking unchanged successful actions unless an intervening state-changing command can
  change their inputs, plus short repeated cycles before execution. VERIFY calls now see
  only the exact current command observation, preventing a small local model from comparing
  a successful file read with an unrelated terse plan step. The user's explicit ONLY-tool
  scope remains independent of terse model plan steps.
- Kept developer-run reads and cancellation responsive during long local-provider calls.
  Concurrent Advance is rejected, cancellation is durable, and a provider response arriving
  after cancellation is discarded instead of reviving the run.
- Made bounded Python commands report process truthfully: non-zero compileall/pytest now
  fails the task. The pytest command preserves the minimal non-secret Windows profile
  variables needed by Python/Git, disables cache, uses and cleans a private basetemp, and
  handles Git-created read-only artifacts on Windows.
- Expanded the VS Code Operator panel with visible Working state, elapsed time, progress,
  live activity, duplicate-click protection, and persistent Cancel behavior for active
  runs. Destination Windows API acceptance passed with Ollama `qwen2.5-coder:7b`: one
  project scan and two useful file reads succeeded and verified without repetition, and
  live cancellation responded in 0.039 seconds during inference. The automated baseline
  is 340 passed and 1 skipped. The ordered backed-up write, compileall, HIGH pytest, and
  read-only AutoCAD inventory API acceptance also passes; direct visual VS Code panel
  observation remains pending.
- Established an AutoCAD-only complete-product gate before connector development.
  ETAP and then SOLIDWORKS Electrical are separate sequential integration editions;
  neither may start before the preceding product passes its documented live acceptance.
- Live AutoCAD 2021 drafting-preview acceptance passed on an explicit four-entity metric
  selection. Drafting run `ac93826c27ec438ebba0b1f68205bc7a` displayed seven exact
  ordered items with preview SHA-256
  `e5f47cc19fe894998313b62b4363629d4a422552994c56da56be889b93e432b0`;
  no review or decision existed, mutation/execution flags remained false, and the
  OS-read-only DWT hash stayed
  `54d4ff5fa776e45612a939b26b6adfab1a5c556cc75eb9ad6cdea7803cca17c5`.
- Live AutoCAD 2021 scratch execution acceptance passed for drafting run
  `75d42932bef548de949f2557796c82e3`. One separately reviewed request created exactly
  two socket references and two `E-WIRE` segments, increasing only the scratch drawing
  from four to eight entities with `DBMOD=0`. The source stayed read-only and unchanged;
  output SHA-256 is
  `4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`
  and recovery-evidence SHA-256 is
  `1d6292f2532539cb928484a7e4f72ebde46137e2340ea596a246d4d12c7f21e7`.
  No handoff or source replacement occurred.
- Live AutoCAD 2021 Milestone 19 acceptance passed on eight selected metric objects with
  no unknowns: four `socket_single` devices and four exact 0.5 m routes. Report SHA-256 is
  `b6417994dfc1cb9a4598cd9ad93856b531001d5d089b8a6b26f7281b16d434e9`;
  reviewed CSV SHA-256 is
  `c21dfc0961dc0af9342288c964644c5195591b0f291a94e3e487b5c4e15db8eb`.
  The report was complete, no drawing/file write occurred, and the read-only drawing copy
  stayed byte-identical to its source.
- The first Milestone 20 live AutoCAD calculation acceptance passed with four load objects,
  four routes totaling 2 m, and explicit 1000 W / 230 V / PF 0.9 / efficiency 0.95 /
  R 7.41 / X 0.08 inputs. It returned 5.085177 A and 0.136361 V / 0.059288% drop with
  result SHA-256
  `582af6bc273247372539eecdbd334f4beeeb4f20848dc426025f783a93ea070e`.
  DBMOD was zero, the drawing hash stayed unchanged, and both compliance and mutation
  flags remained false.
- Live AutoCAD 2021 acceptance for `m20.load_aggregation.v1` passed on eight selected
  metric entities with DBMOD=0. Four 1000 W loads at demand/diversity 1, allocated
  L1/L2/L3/three-phase, produced 4000 W total and 1333.333333 W per phase with zero
  spread/deviation. Result SHA-256 is
  `6fd06995638c3f52ecdb440dd2684d2b6692a466c73e9d069b4fbc29856e60e0`;
  the read-only drawing stayed unchanged and compliance/mutation flags remained false.
- Live AutoCAD 2021 acceptance for `m20.cable_ampacity_derating.v1` passed with a
  route-linked reviewed 20 A basis, ambient factor 0.94, and grouping factor 0.8. Ordered
  stages returned 18.8 A then 15.04 A and combined factor 0.752. Result SHA-256 is
  `53c66553f7c2af7c4760725fc9763f1d5851c84a809b52227e0476df94e96b5a`;
  the OS-read-only drawing stayed byte-identical and every selection/compliance/mutation
  flag remained false.
- Live AutoCAD 2021 acceptance for `m20.short_circuit_source_equivalent.v1` passed on an
  eight-entity metric selection with DBMOD=0. Reviewed 400 V three-phase source/path
  impedance totaling R=0.03 and X=0.09 ohm produced |Z|=0.094868 ohm,
  2434.322478 A symmetrical RMS, and 1686548.085424 VA. Result SHA-256 is
  `f2e11584c488c85a9410b9c0deaf74593729246068fcc5ada0d9ff4a73d17fd6`;
  the read-only drawing stayed byte-identical and all equipment/peak/arc-flash/compliance/
  mutation flags remained false.
- The long `LV Calculations` palette now pins all command buttons above independent
  scrollable inputs and a separately sized result pane. Manual plugin-load launcher mode
  bypasses AutoCAD `/b` startup when that path starts without a visible main window.
- `m20.candidate_adequacy.v1` contracts and evaluation for one reviewed cable/device
  candidate, plus a strict authenticated composite API that re-imports exact evidence and
  recalculates current/drop, ampacity, and fault inputs server-side. Client-submitted result
  numbers, coercion, unknown routes, and inconsistent route evidence fail closed.
- Visible AutoCAD candidate inputs and five PASS/FAIL findings with all three source result
  hashes. Automatic selection, code compliance, and drawing mutation remain false. The DLL
  builds and package/API/palette coverage passes the full `397 passed, 1 skipped` baseline.
- Read-only AutoCAD 2021 candidate acceptance with five PASS findings and result SHA-256
  `4695740c2634ba899ab77a3fc70e7aad9a0542288fedb251682808ed8d8eca42`.
  Source and disposable copy stayed byte-identical; selection, compliance, and mutation
  flags remained false.
- Reviewed `m20.protective_conductor_adiabatic.v1` sizing with explicit rule-source
  identity, units, applicability, disconnection basis/time, and conductor factor. Exact
  same-route fault evidence produces `S=I*sqrt(t)/k`; unresolved applicability produces
  `UNKNOWN` or `NOT_APPLICABLE` without a number.
- A strict authenticated protective-conductor API that recalculates fault current from
  typed inputs server-side, plus AutoCAD inputs/output, package coverage, deterministic
  benchmark and rejection tests. No standard conductor selection, external-source
  verification, compliance evaluation, or drawing mutation is claimed. Compileall, DLL
  build, and the full `403 passed, 1 skipped` automation pass.
- Read-only AutoCAD 2021 PE acceptance result
  `b69287cd88b700b520e19b2daa5cec0646018f954f32ed4fb354fc8cc36abb3b`
  calculated 9.466627 mm2 from exact fault evidence and explicit reviewed inputs. The
  source/copy hashes remained identical and all selection/compliance/mutation flags false.
- Milestone 20 completed within its deterministic reviewed-input boundary. Product/standard
  size selection, external code/manufacturer authority, and compliance remain excluded.

### Notes

- Milestone 18 drafting proposal, validation, lifecycle, API, read-only preview, one-use
  approval, verified scratch execution, recovery evidence, and live AutoCAD 2021
  acceptance are complete within the documented supported-feature boundary. No original
  drawing replacement is enabled; handoff remains separately approved.
- Milestone 19 typed records, deterministic device/route quantities, strict rejection,
  authenticated report/CSV APIs, package coverage, in-product palette, and live AutoCAD
  2021 acceptance are complete within the recognized-device and explicit-route boundary.
  The resulting full automated baseline is 362 passed and 1 skipped.
- Milestone 20 remains active. Current/voltage-drop and load aggregation/diversity/phase
  allocation now pass engine, strict rejection, API, package, palette, and live AutoCAD
  acceptance in the 379-pass/1-skip baseline. Ampacity/derating,
  cable/protection/earthing, and short circuit remain.
- The reviewed ampacity/derating slice passes golden/rejection, API, package, palette, DLL
  build, full automation, and live AutoCAD acceptance in the `385 passed, 1 skipped`
  baseline. It makes no cable or protective-device selection claim. The remaining active
  Milestone 20 slice is the short-circuit input/result boundary.
- The short-circuit boundary passes benchmarks, rejection, API, package, palette, DLL build,
  full `392 passed, 1 skipped` automation, and live AutoCAD acceptance. Milestone 20
  remains active because the documented
  cable/protective-device adequacy and PE/earthing rule interfaces are not yet implemented;
  they remain explicit work rather than being silently treated as complete.
- Subsequent candidate-adequacy and protective-conductor slices completed the remaining
  reviewed-rule interfaces with live read-only evidence and no automatic selection or
  compliance claims. Milestone 20 is now complete within the documented boundary.

## 0.18.0 — 2026-08-06

### Added

- Immutable provider-independent Milestone 17 copilot session/context/message contracts
  for bounded Arabic/English local history with exact context identity, expiry,
  lifecycle validation, and an invariant prohibition on drawing writes.
- Exact local data-egress preview and decision records with privacy classification,
  destination, reviewed values, per-field and aggregate SHA-256, byte counts, expiry,
  and explicit whole-drawing/transmission denial.
- Authenticated loopback copilot session, message, cancellation, result, egress-preview,
  and egress-decision routes with hash-verified context creation, hashed ownership
  secrets, expiry, active-session concurrency, operation-rate, payload, and history
  limits plus metadata-only audit logging.
- A buildable AutoCAD 2021 `AIENGPALETTE` dockable read-only shell with Arabic/English
  UI, explicit-context status, device/route/unknown/missing/warning summary, bounded
  local transcript/input, progress, session start, message submission, and cancellation.
  Its HTTP client is fixed to loopback, disables proxies, bounds responses, and reads
  the API token only from the AutoCAD process environment.
- A bounded injected read-only copilot provider adapter and answer/result routes. One
  approved, unexpired, unused exact egress preview permits one call; strict output
  allows only same-language answer text and citations to approved fields, with timeout,
  concurrency, size, malformed-output, command-field, and replay rejection.
- Palette review controls that display exact egress values, byte counts, and aggregate
  SHA-256 before separate approve-and-ask or reject actions. Local message submission
  never invokes the provider by itself.

### Notes

- Copilot sessions are process-local in this slice. The provider boundary is injectable
  but no real external provider is configured or contacted by repository tests.
- Live AutoCAD 2021 acceptance completed the palette command against an explicit
  three-object selection. Evidence contained three entities, two layers, `DBMOD=0`,
  `provider_egress_allowed=false`, `SECURELOAD=1`, and restored `TRUSTEDPATHS`.
- AutoCAD's interactive process rewrote writable controlled files despite `DBMOD=0`.
  The passing safety run therefore used a separate OS-read-only DWG and preserved its
  SHA-256 `ac3a5ad27cbcacfa6ad2bb4970200d19aecfab59fbc239f006ab426033a33c4e`.
  The original controlled DWT was restored to its previously documented exact bytes.

## 0.17.0 — 2026-08-06

### Added

- Immutable schema-v1 shared electrical records covering drawing/model identity,
  layers, devices, terminals, panels, circuits, protection, cables, conductors, loads,
  routes, units, provenance, confidence, privacy, and explicit unknowns.
- Strict bounded AutoCAD drawing/explicit-selection context validation, exact evidence
  hash binding, conservative catalog-aware mapping, and a durable high-risk local
  import command that performs no provider egress.
- A buildable x64 AutoCAD 2021 .NET read-only context bridge with fixed commands,
  allowlisted entity extraction, independent resource limits, atomic workspace JSON,
  and no arbitrary command or drawing-write input.
- Tests for serialization, immutability, units, provenance, privacy, limits, cyclic and
  malformed input, containment, hash binding, mapping, unknowns, command approval, API
  catalog, bridge source safety, and packaging.
- Live AutoCAD 2021 active-drawing and explicit-selection exports of three entities,
  both mapped to two reviewed devices and one route with `DBMOD=0`, no provider egress,
  and unchanged source SHA-256
  `11fddeb136d129b2f6c38312c9780e2080519bb393edcf03e9528bf9fb1c4682`.

### Changed

- Reframed the authoritative product goal as a local-first electrical-engineering
  copilot and integration platform, with an Arabic/English AutoCAD palette as the
  primary user experience.
- Added `docs/PRODUCT_VISION.md` covering selection-aware drawing context, the shared
  electrical project model, deterministic calculations, quantities, panels and SLD,
  live code intelligence, manufacturer evidence, privacy, and engineer authority.
- Reordered Milestones 16–26 so structured read-only drawing context and the shared
  model precede the palette, drafting, calculations, evidence retrieval, ETAP,
  SOLIDWORKS Electrical, and professional `1.0.0` hardening.
- Moved heterogeneous circuit drafting from Milestone 16 to Milestone 18 so it can use
  stable selection, identity, provenance, topology, preview, and transaction contracts.
- Recorded official/licensed-source, pinned-edition, citation, update-impact, access-
  control, and no-silent-code-change requirements for future compliance work.
- Recorded read-first, supported-interface, identity-preserving, conflict-reviewed
  connector boundaries for ETAP and SOLIDWORKS Electrical.

### Notes

- The palette, calculations, live internet retrieval, and external connectors do not
  yet exist. Version `0.17.0` is the latest implemented release.

## 0.16.0 — 2026-08-06

### Added

- Atomic `drawing-output.json` evidence for every successful writable AutoCAD run,
  bound to the producer ID, paths, suffix, byte count, and source/output SHA-256.
- High-risk `autocad.handoff_verified_output_new` command for exclusive creation of
  one evidence-bound workspace `.dwg`/`.dwt` without overwrite.
- Separately approved `autocad.replace_with_verified_output`, bound to the existing
  destination hash, with byte-identical backup, atomic replacement, verification, and
  automatic rollback.
- Evidence, confirmation, containment, traversal/link, mismatch, destination-race,
  new-file, replacement, backup, rollback, logging, command, and API-catalog tests.

### Changed

- The protected command catalog now exposes twenty-four bounded commands with
  separate `autocad.handoff.write` and `autocad.drawing.replace` capabilities.
- Added persistent `docs/WORK_QUEUE.md` discipline: unfinished steps remain there,
  while verified completion evidence moves to roadmap and changelog history.

### Verified result

- AutoCAD 2021 produced a two-reference, one-wire circuit in run
  `4cf5e666dfd44346bc6a74977bd45560` and handed it off without overwrite to
  `workspace/autocad-live/ai-engineer-beta-0.16.0.dwt`.
- The delivered file is 32,725 bytes with SHA-256
  `11fddeb136d129b2f6c38312c9780e2080519bb393edcf03e9528bf9fb1c4682`.
- Independent read-only inspection run `abb27d7c562744078f80c7e0f3a1589c`
  and inventory run `2a23f57ec1b84c26be646d1fb2e321c7` reported three entities,
  three layers, and `DBMOD=0`; template and producer hashes remained unchanged.

### Limitations

- A circuit transaction still repeats one symbol type; heterogeneous typed topology
  is now Milestone 18 in the revised product roadmap.
- Replacement is covered by deterministic atomic/rollback tests but was not applied
  to a user drawing during live verification; the live acceptance used safer
  exclusive new-file handoff.
- Link-escape behavior is covered by `FileManager` Windows junction tests; one direct
  drawing-handoff symlink test skips when the Windows account lacks link privileges.

## 0.15.0 — 2026-08-06

### Added

- Exact existing-block validation for flags, base point, layer-0 ordered geometry,
  clean end, and catalog-bound definition hash before reuse.
- `CircuitPlacement` and verified circuit-fragment result models with source/output
  SHA-256 values and bounded reference/segment metadata.
- High-risk `autocad.create_electrical_circuit_fragment_scratch` command for 2–8
  references to one approved symbol plus 1–15 connected `E-WIRE` segments.
- Strict payload, approval, transform, route, marker, count, save-state, and hash tests.

### Changed

- Single-symbol scratch placement can reuse an exact existing catalog block and reports
  whether the definition was reused; incompatible definitions still fail closed.
- The protected command catalog now exposes twenty-two bounded commands.

### Verified result

- Live AutoCAD 2021 Core Console reused `AIENG_E_SOCKET_SINGLE_V1`, created three block
  references and two wire segments in one scratch save, changed entity count from one
  to six, retained the source SHA-256, and returned a distinct output SHA-256.
- The clean Autodesk millimeter template path defined the absent approved block, then
  created two references and one wire segment, changing model count from zero to three
  with `DBMOD=0` and an unchanged source hash.
- A separate read-only live inventory reported six entities, three layers, `DBMOD=0`,
  and unchanged inspection scratch bytes.

### Limitations

- One circuit-fragment transaction currently uses one symbol definition repeatedly;
  heterogeneous symbol types require a later typed topology milestone.
- Verified output remains under the isolated run directory. Safe user-selected drawing
  handoff is Milestone 15.

## 0.14.0 — 2026-08-06

### Added

- Atomic schema-v1 `symbol-review.json` evidence persisted in each completed intake
  run, including source/proposal hashes and eligibility.
- Separately approved `autocad.register_electrical_symbol_review` command accepting
  only review ID and exact proposal/source SHA-256 values.
- Revalidation of evidence, proposal, workspace source path, and current source bytes
  before any catalog mutation.
- Duplicate ID/name prevention, timestamped backup, staged full-catalog validation,
  concurrent-change detection, atomic replacement, post-write reload, and rollback.
- Registration approval, binding, duplicate, backup, atomic-write, version, and forced
  post-write failure tests against isolated temporary catalogs.

### Verified result

- Safe temporary-catalog integration increased catalog version from one to two,
  preserved a byte-identical backup, and loaded the newly registered symbol.
- A forced post-write validation failure restored the exact original catalog bytes and
  retained the recovery backup.

### Limitations

- Registration updates only the JSON catalog; it does not import or redefine blocks
  inside drawings.
- Existing drawing definitions cannot yet be reused until their geometry is verified
  against the registered catalog. That is Milestone 14.

## 0.13.0 — 2026-08-06

### Added

- Durable-approval-gated `autocad.review_electrical_symbol_source` for one named
  user-created block in an unchanged read-only scratch drawing.
- Bounded extraction of block flags/base point and up to 32 `LINE`/`CIRCLE` entities,
  with layer-0, attribute, nested-block, unsupported-type, and truncation counts.
- Explicit eligibility reasons and a complete hash-approved catalog proposal only for
  compatible millimeter block definitions.
- Source SHA-256 evidence and strict source/scratch equality with mandatory `DBMOD=0`.
- Drawing-unit enforcement (`INSUNITS=4`) for millimeter catalog placement.
- Review parser, immutable-copy, proposal, unit-contract, payload, approval, and live
  integration tests.

### Verified result

- Live placement succeeded in Autodesk `acadiso.dwt`, then read-only intake recovered
  the three-entity block as one circle and two lines on layer 0 in about 0.68 seconds.
- The review found zero unsupported entities, attributes, nested blocks, or truncation,
  returned `eligible=true`, and produced proposal SHA-256
  `1c232bd9e8c960e9ef7cc2db29efefcd08eac11f8b69960ff54288af2a08cfbc`.
- Review source and scratch bytes stayed identical and neither stream was truncated.

### Limitations

- Intake reviews one exact safe block name and only proposes; it never writes the
  catalog or imports/redefines a block.
- Eligible geometry is limited to 1–32 layer-0 lines/circles in millimeter drawings,
  without attributes or nested blocks. Atomic approved registration is Milestone 13.

## 0.12.0 — 2026-08-06

### Added

- Versioned, bounded, UTF-8 electrical symbol catalog with canonical per-definition
  SHA-256 approval and fail-closed edit detection.
- Starter `socket_single` definition using three line/circle entities in millimeters,
  block `AIENG_E_SOCKET_SINGLE_V1`, and insertion layer `E-POWR-DEVC`.
- Low-risk `autocad.list_electrical_symbols` command and durable-approval-gated
  `autocad.place_electrical_symbol_scratch` command.
- Exact-hash-bound placement with bounded point, rotation, and uniform scale.
- Strict block-new/defined, geometry-count, `INSERT`, block/layer/transform,
  zero-attribute, entity-growth, clean-save, and source/output hash validation.
- Catalog tamper/schema and AutoCAD parser/script/approval/payload/integration tests.

### Verified result

- Live AutoCAD 2021 execution created the three-entity starter block and inserted one
  reference at `(2000,1000,0)` on `E-POWR-DEVC` in about 2.17 seconds.
- Model-space entity count increased from zero to one, `DBMOD=0`, source bytes stayed
  identical, scratch bytes changed, and neither console stream was truncated.

### Limitations

- The starter symbol has no attributes; zero attributes are explicitly verified.
- Catalog geometry currently supports only bounded lines and circles. User-authored
  DWG/DWT block intake, inspection, registration, and safe reuse are Milestone 12.
- Existing same-named blocks fail closed and are never silently redefined.

## 0.11.0 — 2026-08-06

### Added

- Durable-approval-gated `autocad.create_wire_route_scratch` command for a bounded
  1–15 segment electrical route in an isolated drawing copy.
- Fixed `E-WIRE` layer with ACI color 3 and `CONTINUOUS` linetype. The name is
  NCS/AIA-inspired; the display color is a local project policy.
- Strict 2–16 point validation, fixed numeric script generation, exact entity-count,
  `LINE` type/layer, clean-save, and SHA-256 state verification.
- Manager, parser, script-policy, approval, payload, catalog, and API tests.

### Verified result

- Live AutoCAD 2021 execution created two `E-WIRE` line segments through `(0,0,0)`,
  `(1000,0,0)`, and `(1000,500,0)` in about 2.15 seconds.
- Entity count increased from zero to two, `DBMOD=0`, source bytes stayed identical,
  scratch bytes changed, and neither console stream was truncated.

### Limitations

- This is one wire geometry primitive, not complete electrical/NCS validation.
- It does not create blocks, devices, circuits, panels, annotations, wire numbers,
  single-line diagrams, or promote scratch output over an input drawing.

## 0.10.0 — 2026-08-06

### Added

- Durable-approval-gated `autocad.create_line_scratch` command, the first bounded
  writer for an isolated AutoCAD drawing copy.
- Strict two-point payload validation for finite numeric 3D coordinates within ±1e9,
  normalized deterministically to nine decimal places.
- Fixed `LINE` plus `QSAVE` script with no caller-provided command, layer, output path,
  format, or arbitrary AutoLISP text.
- Verification of exact one-entity growth, created DXF type `LINE`, post-save
  `DBMOD=0`, unchanged source SHA-256, and changed scratch SHA-256.
- Writer validation, approval, script-policy, parser, count, and hash-state tests.

### Verified result

- Live AutoCAD 2021 execution created a line from `(0,0,0)` to `(1000,0,0)` in the
  copied DWT in about 2.10 seconds.
- Entity count increased from zero to one, the source remained byte-identical, the
  saved output changed, and neither output stream was truncated.

### Limitations

- The writer creates one `LINE` on the current scratch layer only. It does not yet
  create electrical layers, circuits, blocks, annotations, panels, or diagrams.
- Output remains in the isolated run directory and is never promoted over an input
  drawing automatically.

## 0.9.0 — 2026-08-06

### Added

- Durable-approval-gated `autocad.inventory_scratch` command for aggregate layer and
  database-entity counts on isolated read-only drawing copies.
- Fixed AutoLISP inventory using documented `tblnext`, `ssget "_X"`, and `sslength`
  operations without returning drawing names, types, handles, text, or geometry.
- Configurable ten-million aggregate count ceilings and strict `DBMOD=0` validation.
- SHA-256 verification of the original source and scratch bytes after Core Console
  execution, with fail-closed mutation handling.
- Unit and command-engine tests for inventory results, approval, script bounds,
  malformed/oversized counts, and unexpected scratch mutation.

### Verified result

- Live AutoCAD 2021 execution on the copied Autodesk empty template returned one
  layer, zero database entities, and `DBMOD=0` in about 0.66 seconds.
- Source and scratch hashes remained unchanged and neither output stream was
  truncated.

### Limitations

- Inventory is aggregate only and does not yet classify electrical objects, inspect
  layer names, validate standards, or interpret geometry.
- Core Console must run under the initialized interactive Windows account as
  documented in 0.8.1.

## 0.8.1 — 2026-08-06

### Fixed

- Completed real AutoCAD 2021 Core Console verification under the initialized
  interactive Windows user using only a copied Autodesk template and `/readonly`.
- Changed the fixed metadata script to one `progn` expression with a silent final
  `princ`, preventing Core Console from appending AutoLISP return-value echoes to
  result markers.
- Anchored marker parsing to complete output lines so command and return echoes cannot
  be mistaken for inspection evidence.
- Added a regression test using the exact echoed-output pattern observed during live
  verification.

### Verified result

- AutoCAD `24.0`, `INSUNITS=1`, `MEASUREMENT=0`, `TILEMODE=1`, and `DBMOD=0` were
  returned in about 0.67 seconds; the original template was not opened by Core
  Console and neither output stream was truncated.

### Limitations

- AutoCAD profiles are Windows-user-specific. Core Console must run as the initialized
  interactive user; sandbox or service accounts fail closed rather than creating or
  borrowing Registry profile state.
- The verified command remains metadata-only on a scratch copy. It does not create or
  modify drawing entities, attach to the GUI, or accept arbitrary AutoCAD commands.

## 0.8.0 — 2026-08-05

### Added

- Fixed-command Python launcher, executable-ZIP entry point, and Windows command
  wrapper for checks, runtime/API startup, packaging, installation, snapshots,
  restore, and migration.
- Portable ZIP builder with explicit source allowlists and clean generated runtime
  data that excludes local operational state and tokens.
- Deterministic SHA-256 payload manifest, bounded archive resources, and atomic
  package creation without overwrite.
- Confirmed new-directory installer with full pre-write validation of paths, links,
  duplicate entries, manifest records, and content hashes.
- Allowlisted runtime JSON recovery snapshots with secret-like key rejection,
  confirmed restore, pre-restore backups, atomic writes, and rollback.
- Confirmed idempotent schema-v1 layout migration with a pre-change recovery archive
  and rollback.
- Multi-computer packaging, transfer, install, recovery, migration, privacy, and
  limitation documentation.
- Packaging, tampering, traversal, confirmation, restore, corrupt-archive, migration,
  and launcher tests.

### Limitations

- SHA-256 manifests detect corruption or modification but are not publisher digital
  signatures and cannot establish archive provenance.
- Recovery archives reject secret-like JSON keys but may still contain sensitive
  task, goal, mission, or result text; they must remain private and local.
- Migration supports the documented schema-v1 runtime layout only. Unknown schema
  versions fail closed.
- AutoCAD live scratch verification was still pending when 0.8.0 was released; it is
  completed by 0.8.1.

## 0.7.0 — 2026-08-05

### Added

- AutoCAD 2021 R24.0 installation, exact COM ProgID, Core Console, and secure
  AutoLISP capability detection without exposing installation/license data.
- Approval-gated atomic scratch copies for bounded workspace DWG/DWT inputs.
- Fixed metadata-only Core Console script and strict output-marker parsing.
- Read-only drawing flag, filtered environment, no stdin, hard timeout, bounded
  output capture, and process-tree termination.
- Local diagnostic output retention inside scratch runs without API/log disclosure.
- AutoCAD unit, integration-boundary, and command-approval tests.
- Official Autodesk support/security references and current-machine verification
  documentation.

### Limitations

- The installed AutoCAD 2021 current-user profile has not completed first-run setup;
  real Core Console inspection stops with a profile-setup error until AutoCAD is
  started and closed manually once.
- ActiveX and secure AutoLISP are detection-only future methods. No open drawing,
  object mutation, arbitrary command, LSP loading, or GUI automation is implemented.

## 0.6.0 — 2026-08-05

### Added

- Workspace-confined Visual Studio Code integration through the supported `code` CLI.
- Non-secret installation/version/commit/architecture detection.
- Strict Windows `code.cmd` wrapper parsing and direct `Code.exe` plus `cli.js`
  execution with no shell.
- Filtered environment, no stdin, bounded captured output, and CLI timeout.
- Existing-path validation and bounded `--goto` line/column navigation.
- Low-risk `vscode.detect` and durable-approval-gated `vscode.open` commands exposed
  through existing task/API/developer-agent boundaries.
- Wrapper escape, malformed metadata, containment, confirmation, timeout, nonzero
  exit, environment, and argument tests.

### Limitations

- The adapter launches or reuses VS Code only; it does not install an extension,
  modify settings, control the integrated terminal, inject editor input, or automate
  the GUI.
- CLI discovery depends on a supported `code` launcher being available on `PATH`.

## 0.5.0 — 2026-08-05

### Added

- Bounded callback-based provider adapter with typed phase requests, response byte
  limits, redacted callback representation, and sanitized invocation failures.
- Atomic persistent developer-run state and metadata-only audit events.
- Strict JSON plan, exact command proposal, verification, and completion contracts.
- Mandatory human review of every plan and command payload before task submission.
- Execution through the existing registry, permission, task, cancellation, and
  one-use approval boundaries only.
- Plan → act → observe → verify enforcement with evidence required before completion.
- Independent action/provider-call limits and fail-closed malformed-output handling.
- Protected API routes for provider status, developer runs, reviews, advancement,
  and cancellation.
- Developer-agent unit and end-to-end API tests.

### Limitations

- No cloud/provider SDK or network transport is bundled. The normal CLI/API has no
  provider configured; a trusted embedding application must inject an adapter.
- Provider callbacks are synchronous and cooperatively bounded at the response and
  state-machine layers; a future network adapter must implement its own hard timeout,
  cancellation, credential, and explicit data-egress policy.
- Every proposed action requires human review. This is intentionally conservative
  and is not unattended autonomous execution.

## 0.4.0 — 2026-08-05

### Added

- Authenticated FastAPI application owned by the existing application lifecycle.
- Environment-only high-entropy Bearer token with constant-time comparison and
  redacted configuration/middleware representations.
- Exact localhost binding, one worker, Host validation, disabled proxy trust, access
  logs, interactive docs, Redoc, and OpenAPI schema.
- Authentication middleware before request-body parsing plus router-level defense in
  depth.
- Declared and streamed request-body enforcement with bounded pre-buffering.
- Strict Pydantic request models and validation responses that omit submitted input.
- Protected status, command, task, cancellation, approval, file, and scan routes.
- Task responses without original payloads and an end-to-end durable approval flow.
- Conservative cache, content-type, frame, and referrer response headers.
- Validated CLI configuration check and secure single-worker Uvicorn runner.
- API security, lifecycle, configuration, body-limit, validation, task, file, scan,
  and approval integration tests.

### Limitations

- HTTP is intentionally plaintext because the service is restricted to loopback; it
  must not be proxied, tunneled, forwarded, or bound to LAN/public interfaces.
- The Bearer token is process-local operator authentication, not multi-user identity
  or role-based access control.
- FastAPI's current TestClient emits one upstream deprecation warning recommending
  the future `httpx2` test transport; runtime behavior and tests pass.

## 0.3.0 — 2026-08-05

### Added

- Typed immutable command registry with validators, permissions, risk, deadlines, and
  bounded JSON payload/result handling.
- Exact capability policy and explicit namespace wildcard support.
- Atomic durable approvals with expiration, decisions, task/command binding, and
  one-use consumption.
- Persistent tasks and transition events with validated states, bounded history,
  cooperative cancellation, and explicit maximum-three retries.
- Safe unexpected-error handling without persisting or logging exception details.
- Ten built-in Safe Core commands for file, scan, system, process, and read-only Git
  operations; mutations require durable approval and no terminal command is exposed.
- Application lifecycle wiring and command/task/approval integration tests.

### Limitations

- In-process cancellation and deadlines are cooperative; Python threads are not
  force-terminated. Subprocess operations retain hard TerminalManager timeouts.
- Task and approval persistence is synchronized within one process, not through a
  multi-process database.
- Commands and approval decisions are not exposed through an API yet.

## 0.2.0 — 2026-08-05

### Added

- Initialized Git and recorded Milestone 0 as baseline commit `bc9ab87`.
- Added sandboxed UTF-8 `FileManager` operations within configured workspace roots.
- Added canonical path containment and Windows directory-junction escape protection.
- Added configurable read-size limits and regular-file validation.
- Added atomic writes, explicit destructive-operation confirmation, timestamped
  atomic backups, and non-recursive single-file deletion.
- Added structured audit events for file actions, denials, backups, and failures.
- Added tests for traversal, junction escapes, multiple roots, backup separation,
  UTF-8 behavior, size limits, confirmations, backups, atomic writes, and audit logs.
- Recorded the verified `FileManager` slice in commit `fdd3b38`.
- Added a read-only `ProjectScanner` that reuses workspace-root policy and never reads
  file contents or follows directory links and Windows junctions.
- Added deterministic, JSON-compatible scan results with file metadata, extension
  summaries, project markers, skipped paths, and explicit truncation reasons.
- Added independent limits for file count, directory count, entries per directory,
  depth, and cumulative bytes, plus graceful permission-error handling.
- Added scanner tests covering classification, project discovery, ignored paths,
  junction escape prevention, every resource bound, configuration validation,
  serialization, permission failures, and audit logging.
- Added fail-closed subprocess policies, workspace-confined working directories,
  filtered environments, timeouts, process-tree cleanup, and bounded output capture.
- Added non-secret system snapshots and bounded process metadata inspection.
- Added confirmation-gated graceful process termination with PID-reuse checks and no
  force-kill behavior.
- Added read-only, workspace-confined Git status, diff, and commit log inspection with
  external diff/text conversion disabled and optional locks suppressed.
- Added comprehensive Terminal, system, process, and real temporary-repository Git
  tests.

### Limitations

- Safe Core components remain library services and are not exposed through an API.
- Command policies reduce risk but do not constitute an operating-system sandbox;
  future command registration must remain narrow and reviewable.

## 0.1.0 — 2026-08-05

### Added

- Typed configuration with paths resolved from the project root.
- Project-specific exception hierarchy.
- Structured UTF-8 JSON-lines application logging.
- Atomic JSON state persistence using same-directory temporary files and replacement.
- Validated application start/stop lifecycle and a clean console entry point.
- Valid versioned JSON settings, state, history, and mission documents.
- Foundation tests covering configuration, persistence, logging, lifecycle behavior,
  and failure cases.
- Foundation architecture, roadmap, run instructions, and safety boundaries.

### Audit notes

- The starting repository contained empty scaffold files plus `AGENTS.md` and the
  project brief; no prior Python implementation or old prototype was present.
- The workspace initially had no Git metadata; Git was initialized after the
  foundation passed its required verification.
- Recovered the unconsumed `CLASSROOM 1500` sample request from 2026-10-17. Revit loaded the bridge
  but the unquoted Arabic/space-containing launch path opened no active document, so no transaction,
  response, or RVT output occurred. The expired request is archived intact at SHA-256
  `A0AEFE12...D7637`; source and backup remain `FB72029C...C52D1`. A replacement non-executable
  preview now binds the same eight L1 points at fingerprint `9075F097...60EDA`; execution still
  requires exact approval and will use a correctly quoted launch argument.

