# AI-Engineer Work Queue

This file is the persistent source of truth for unfinished work only. Remove each step
after implementation, tests, and any required live verification succeed. Preserve the
completed result in `ROADMAP.md` and `CHANGELOG.md` in the same commit.

## Master Product Goal & Architectural Blueprint
The master vision, operational principles, and multi-LLM/installer requirements are fully detailed in:
- `docs/MASTER_PRODUCT_GOAL_AR.md` (ميثاق المنتج والهدف النهائي الشامل)
- `docs/COMMERCIAL_PRODUCT_BLUEPRINT_AR.md`
- Root instruction charter: `GEMINI.md`

### Core Milestones Summary:
- **M1: Baseline Reconciliation & Test Hygiene (C0/C1):** Clean unit tests, resolve orphan `ProjectConfigError`, command catalog parity.
- **M2: In-App Chat & Multi-LLM Provider Engine (BYOK):** AutoCAD `PaletteSet` + Revit `DockablePane`, selectable providers (Gemini, Claude, Codex, custom) with secure local API keys.
- **M3: Color-Coded Review & Human Approval Lifecycle:** Drawing/editing in temporary review color, commit on "تمام" / confirm, full rollback on cancel.
- **M4: Project Specs & Deterministic Engineering Engines:** Uploading PDF/Excel specs, electrical calculations (voltage drop, short circuit, cable sizing), BOQ & Legend generation.
- **M5: Cost-Effective Vision & Standalone Windows Installer:** On-demand screen review + unified setup wizard installing both CAD & BIM add-ins.

## Commercial product foundation — C0 audit and reproducibility

The owner requested a commercial, in-product AutoCAD/Revit chat with selectable AI providers,
reviewed drawing actions, and single-active-device email licensing. The proposed architecture
and staged delivery are in `docs/COMMERCIAL_PRODUCT_BLUEPRINT_AR.md`. The 2026-11-04 C0 audit
in `docs/COMMERCIAL_C0_AUDIT_2026-11-04_AR.md` is **not** an acceptance claim: the transfer
copy has 747 passing tests but 39 failures and 8 errors after excluding one collection blocker.
Complete these independently before C1/provider/licensing implementation:

1. Reconcile the 17 package-manifest hash differences against the real source history, and
   establish a reproducible Git/build baseline without overwriting later Revit work.
2. Separate portable unit fixtures from live-project evidence tests; never invent missing
   Revit/AutoCAD artifacts to satisfy a test.
3. Resolve the orphan `ProjectConfigError` import, the absent setup-script test expectation,
   and the API registered-command count 33/34 against documented intended behavior.
4. Pass the remaining AutoCAD/Operator and Revit live acceptance gates in this queue on
   Scratch copies, then rerun general tests and clean multi-PC restore/build acceptance.
5. Only then start C1 typed common context and C2/C3 provider/UI work; keep all existing
   preview, consent, transaction, backup, and recovery boundaries.

## Active — AutoCAD beta release preparation

The 2026-09-14 Hutchison Ports panel request supplies a 17-file Schneider 2D-front DWG set
and a preliminary BOM. Produce only a source-preserving review preview until the engineer
confirms the exact PrismaSeT P enclosure reference/dimensions and compartments, top/bottom cable
entry and cable data, system voltage/earthing/fault duty, IP/environmental/corrosion requirements,
internal separation form, MCB quantity split, pilot-light colours/functions, and approved SLD/control
assignments. Use IEC 61439-1/-2, the pinned Saudi SBC 401 edition, project/owner requirements, and
manufacturer assembly rules; do not claim fabrication readiness from front views alone.
R01-R03 are now live-created AutoCAD 2021 review drawings. R03 includes the supplied 1:1 front-view
blocks, proposed 1600 x 2000 x 600 mm arrangement, bottom cable zone, indicative Form 3b/busbar/
PE/N/gland geometry, tags, and a part-number legend. All 17 copied-source hashes remain unchanged;
the exact R03 hash and fabrication hold points are recorded in the project report. Keep the item
open until the listed engineering inputs and responsible-engineer review close those hold points.

The 2026-08-24 panel GA/STEP trial is now encoded in a self-hashed verified-lessons registry and
regression suite: no handwritten DXF, no ActiveX or interactive STEP import, no fuzzy manufacturer
CAD substitution, and no name-only/unverified drawing handoff. The local CAD intake and Core
Console plan/postcheck contracts are implemented. Before Professional 1.0, repeat the generic
pipeline on one second exact manufacturer STEP reference and a clean installed package; this is a
portability acceptance check, not permission to weaken any review gate.

The 2026-08-30 isolated Schneider hybrid preview now places 31 reviewed STEP instances at the GA
locations with explicit inch-to-millimetre and axis normalization. Nine unresolved components keep
temporary envelopes and busbars are intentionally omitted. Complete visual engineer review, resolve
the remaining exact product identities and missing CAD, then perform spacing, thermal, wiring,
terminal, protection, and fabrication checks before any workshop release.

Repository-wide verification currently has two pre-existing consistency blockers outside this
CAD-learning change: `tests/test_project_config.py` imports a missing `core.project_config` module
and `ProjectConfigError`, and one performance-document test still expects the superseded phrase
`complete release performance gate is not`. Decide whether the orphan project-config experiment
belongs in the main product before implementing or removing it, then reconcile the performance
test with the documented second-profile-only gate. The remaining suite passes.

The Manufacturer Asset Collector backend now supports reviewed official-domain searches and
separately approved ordered quarantine for PDF/DWG/DXF/STEP/IGES/JPEG/PNG. Before calling its UI
production-ready, run the three-product live matrix in `docs/MANUFACTURER_ASSET_COLLECTOR.md`,
record missing-kind behavior, and add the reviewed asset controls to the AutoCAD palette. Do not
enable automatic catalog registration, CAD import, or drawing mutation as part of collection.

1. Run representative real-project acceptance against `docs/AUTOCAD_SUPPORTED_FEATURES.md`:
   record units, layer/block/tag conventions, supported/unknown objects, context size, calculation
   assumptions, code profile, preview/execution/recovery results, performance, and unchanged-source
   evidence. The first `lighting.dwg` read-only inventory and two selection trials are recorded in
   `docs/evidence/autocad-real-project-lighting-2026-08-13.md`. Its complete 15-entity context and
   exact self-hashed convention profile now recognize six devices, one route, panel `DB3-GA2`, and
   circuit `4`. A fresh exact rebind passed live palette acceptance with 7 devices, 6 routes,
   8 unknowns, 5 warnings, and presentation-only DBMOD=16; next review explicit
   device/route/control topology. The first broader topology selection was rejected for unreviewed
   block pairs and DBMOD=20. The block/text-only `AIENGDEVICETAGCONTEXT` live capture then passed
   with 26 complete entities at DBMOD=16: 12 reviewed devices, panel `DB3-GA2`, circuit `4`, zero
   unknowns, and no route geometry. Its reopened visual palette check passed and retained the same
   counts after Refresh. An exact self-hashed topology sidecar now assigns all 12 reviewed block
   handles to circuit `4 / DB3-GA2`; its 12 `a/b/c` observations and `WP` qualifier remain explicitly
   unassociated and route connectivity remains false. A bounded route-only capture then accepted nine
   complete E-Lite-Circ polylines (maximum 1,025 vertices), zero unknowns, and 10.2773298425 m of
   observed centerline. Their exact handles are assigned to circuit 4, while endpoint connectivity
   and installed cable length remain explicitly unconfirmed. The exact cross-context connection-anchor
   contract and read-only `AIENGCONNECTIONANCHOR` capture command are now automated-verified: each
   capture requires an explicit block, route, engineer click near the intended endpoint, and terminal
   name (default `CIRCUIT`). A new allowlisted local review channel has also passed one full live
   request/display/Yes-No/result/storage cycle, reducing later reviews to a modal decision without
   arbitrary command execution. A later batch review was invalidated after the engineer reported that
   some endpoints had no visible marker and one view was zoomed too far out to identify reliably. Its
   six derived captures and final connection/readiness reports are retained only under superseded
   evidence. The channel now requires endpoint-marker-v2: zoom is bounded to the device and exact
   endpoint, with a red cross and yellow guide. All 13 uncertain decisions were repeated individually.
   Seven electrical routes now have 14/14 connected endpoints; two other polylines were explicitly
   classified as one circuit-number arrow/leader and excluded from cable length/topology. The rebuilt
   review/readiness reports pass exact validation. A live copied-Scratch one-line execution/recovery
   also passed in 2.1374 seconds with unchanged source identity. Packaged context/palette and the
   fixed maximum-fixture Scratch gate now pass. Add support
   only through separate bounded fixtures; do not tune silently to one file. The exact calculation-
   readiness report is now complete and correctly blocks calculation on missing load, system, cable,
   installed-length, protection, and pinned-code inputs; units, membership, and endpoint accounting
   pass. Portable candidate r40 is the current tested build. It includes an exact, expiring,
   SHA-bound no-mutation operator channel for drawing-context capture, document status, and palette
   display plus exact cooperative cancellation; arbitrary command text is impossible. It passes
   `542 passed, 1 skipped`, clean
   installation, 18-file readiness, installed x64 DLL build, and all six installed local-core
   targets. Clean-installed r39 passed the full-drawing channel on a short inherited-ACL install:
   4,848/4,848 top-level entities were captured and independently reloaded in 76 chunks in
   1.2566212 seconds, and exact status plus palette-display actions passed at `DBMOD=0 -> 0`.
   The engineer visually confirmed the palette, and the test copy closed without saving.
   Cooperative cancellation had already passed live: an exact hash-bound cancel request was
   acknowledged as `cancelled` in 619.1593 ms, produced no partial context, performed no drawing
   mutation, and a follow-up status action confirmed `DBMOD=0 -> 0`. Both project-drive physical
   disks are healthy NVMe SSDs. The maximum Scratch gate now passes; the second-profile gate below
   remains open.
   The first explicitly approved live Gemini question returned a bounded Arabic answer without
   drawing mutation. That trial exposed a summary gap: 155 selected entities were all reported as
   unknown even though the drawing visibly contained blocks. r26 now separately counts top-level
   selected AutoCAD `INSERT` references as `raw_blocks` without claiming they are recognized
   electrical devices. The first live restart verified exactly 57 raw blocks in a complete
   184-entity selection, but also exposed the same ambiguity for routes: the selection contains
   42 raw `LINE`/`LWPOLYLINE` primitives while the new context has no exact rebound convention
   profile and therefore reports zero recognized routes. The palette now separately reports raw
   route primitives and labels recognized counts. A second live selection passed the new display:
   independent inspection and the palette both reported 49 raw blocks and 38 raw route primitives,
   with zero classified devices/routes clearly separated. Existing reviewed mappings match 40/57
   block references and 41/42 route primitives in the preceding broader selection, but
   the remaining objects require separate review and a new exact profile before they can be typed.
   A full-drawing panel/circuit text-tag pass now records 10 panels and 45 distinct circuit
   references in `workspace/reports/lighting-panel-schedule-draft-20260824.md`. Complete the
   project Panel Schedules only after reviewed circuit membership, legend-to-block load/wattage
   bindings, panel system/way data, phase allocation, protection, cables, demand/diversity, and the
   ambiguous `4Y / DB3-GA1` reference are resolved. Do not populate missing schedule fields from
   typical practice or model memory.
   The first conductor-mark group is now structurally identified: home-run polyline `1A1C3975`,
   plain tick `1A1C396C`, circled tick/circle `1A1C396D`/`1A1C396E`, circuit tag `1A1C3976`, and
   adjacent Ø20 mm UPVC-conduit note. Obtain one engineer-confirmed plain/circled conductor-type
   mapping before implementing whole-drawing mark counting; the drawing has no explicit symbol key
   and its general notes defer numeric wire sizes to separate Panel Details.
   The engineer has now authorized explicitly labeled type/location estimates for missing legend
   loads and PF values. The first provisional calculation for DB3-GA2/circuit 4 is 311 W,
   351.111 VA, combined PF 0.8858, and 1.527 A at 230 V with demand factor 1.00. Extend this only
   after producing reviewable block-to-legend candidates and circuit-membership evidence; keep
   every estimated row distinguishable from drawing/manufacturer facts.
   A deterministic whole-drawing candidate pass now covers all 45 observed references and retains
   missing numeric ways as blank, never `Spare`. It reproduces the reviewed DB3-GA2/circuit-4 load
   without double-counting its two equivalent drawing occurrences. Conflicting repeated occurrences,
   unresolved load types, all spatial memberships, and `4Y / DB3-GA1` remain explicit review work.
   A deterministic read-only classification audit now preserves the exact accepted inventory and
   candidate-evidence boundaries. It reports 45 circuit references, seven repeated-occurrence
   conflicts, 35 unresolved loads across 12 non-conflict circuit rows, 647 route candidates, and
   3,856 unclassified entities without promoting proximity to connectivity. By engineer decision,
   final Panel Schedule/SLD, protection, cable, phase, and final-load review for this workflow is
   deferred to a later drawing with clearer evidence; retain these `lighting.dwg` items as unresolved
   regression evidence and do not request repetitive manual review on this drawing.
   The engineer approved Microsoft Excel COM as the named fallback. The requested 14-sheet
   standalone workbook is generated, formula-checked, and visually verified. Next review the
   conflict/unresolved rows and confirm panel rated ways, phases, protection, cables, and `4Y`.
   A source/evidence-bound SLD preview for `DB3-GA2` shows ways 1-19 with seven observed circuits,
   twelve blank non-spare positions, red provisional loads, and TBD engineering inputs. Its exact
   bounded AutoCAD producer now passes live Scratch execution and new-file handoff: the reviewed
   empty rectangle below the legend received exactly 234 entities (`4,848 -> 5,082`) on four
   reserved layers, a read-only reopen retained `DBMOD=0`, and `lighting-working.dwg` remained
   byte-identical. Review the resulting DWG visually before treating layout as accepted, and keep
   all incomer, busbar, protection, cable, phase, and frequency fields at TBD until supplied.
   A second exact review overlay now isolates the right-side DB3-GA2 occurrence: circuits 2, 4, 6,
   and 7 contain 30 copied route geometries and 28 load halos on removable layers; circuits 1 and 3
   were absent, and the shared source-5/7 component is assigned to circuit 7 by engineer direction.
   Live Scratch/new-file handoff and read-only reopen passed at `4,848 -> 4,913`, `DBMOD=0`.
   The diagnostic overlay was correctly rejected as an SLD presentation; it remains review evidence
   only. A clean replacement no-mutation SLD is now regenerated from only this right group at preview
   SHA-256 `d29072e3...32a1f2`: ways 1/3/5 are blank, 2/4/6/7 are populated, circuit 6 includes eight
   reviewed loads, and circuit 7 remains a partial known load with two unresolved items. Obtain exact
   visual approval of this replacement preview before building or executing a new AutoCAD producer;
   do not reuse the earlier 1-19 candidate SLD as final way mapping.
   Nested blocks and Xref contents remain outside the raw-block count until separately designed and
   reviewed. A live-accepted `capture_drawing_index` operator action now removes the manual
   512-object selection loop for top-level Model/paper-layout entities: it discovers the active
   document locally, creates deterministic non-overlapping context shards, and commits a hash-bound
   complete manifest only after every shard validates. `lighting.dwg` passed with 4,848/4,848 unique
   entities in 76 chunks and 1.119 s at `DBMOD=0 -> 0`, with unchanged source SHA and no manual
   selection. The exact chunks now aggregate into one immutable drawing-wide inventory with
   independent reload validation and handle-level deterministic comparison. On `lighting.dwg`,
   the inventory retained all 4,848 entities and safely separated 336 reviewed devices, seven
   exact reviewed routes, two reviewed annotation polylines, 647 route candidates, and 3,856
   unclassified entities. Layer matches alone remain candidates, never confirmed cables. Next add
   a change-triggered selective recapture path; the present comparison is incremental after two
   complete captures. A source-verified automatic refresh coordinator now removes the remaining
   manual orchestration: it discovers exactly one named active drawing, enqueues the allowlisted
   complete index, validates it, builds and persists the inventory, compares an optional baseline,
   and writes an immutable local report. If the source DWG changed, old source-bound semantics are
   not reused and the report requires semantic review. The unchanged-source coordinator passed
   live AutoCAD 2021 acceptance without selection: 4,848 entities refreshed in 1.093 s at
   `DBMOD=0 -> 0`; comparison returned zero additions/removals/changes/reclassifications and
   4,848 unchanged. Changed-source live acceptance also passed on a separately approved Scratch
   copy containing exactly one added LINE: the refresh found that single new handle, no false
   removal or geometry change, withheld all 991 source-bound semantic classifications, and set
   `semantic_review_required=true` at `DBMOD=0 -> 0`. An event-backed, in-session selective
   capture is now live accepted: a complete index arms the baseline; AutoCAD append, modify,
   and erase events record disjoint handles; one allowlisted read-only delta request exports only
   added/modified objects plus removed handles. It is one-use, capped at 4,096 candidates, and
   fails closed after restart, missing baseline, or overflow. A separately approved same-session
   Scratch trial returned exactly one added, one modified, and one removed handle in 25.6422 ms,
   with no unchanged-control handle and `DBMOD=0 -> 0`. The validated delta now composes into an
   immutable schema-v2 inventory with explicit baseline-inventory and delta lineage. Exact catalog
   meanings are recalculated while all stale source-bound reviewed semantics are withheld and
   `semantic_review_required=true`. Live evidence composition returned exactly one added, one
   removed, one geometry-changed, and 4,849 unchanged handles. Nested
   definitions and Xrefs stay separate follow-on scopes.
   The palette also exposes prerequisite-aware button states and a visible active-session
   label; those states passed the first live restart. Diagnostic candidates r30-r38 are retained
   only as evidence of two corrected packaging defects: restrictive ACLs inherited from a renamed
   temporary directory, and a legacy AutoCAD/.NET path above 259 characters. The installer now
   inherits the destination ACL directly and rolls back on failure; the launcher fails fast with
   an actionable short-install-path message. r40 adds a separate non-production, hash-bound,
   one-use-approved maximum performance fixture without weakening normal drafting limits. Its live
   run created exactly 1,792 lines in a copied Scratch drawing (`4,848 -> 6,640`) in 2.4977 seconds,
   or 2.5679 seconds including self-hashed recovery evidence, at `DBMOD=0`. Source hashes remained
   unchanged and the output is explicitly ineligible for handoff. Only the second-profile gate
   remains open for this release-performance matrix.
2. Complete the remaining live portion of `docs/AUTOCAD_PERFORMANCE_TARGETS.md`: repeat the
   packaged matrix on a second initialized Windows/AutoCAD profile. Current-profile packaged
   context, palette, maximum copied-Scratch execution/recovery, and cancellation are accepted; the
   latter completed in 619.1593 ms and the
   project drive is a healthy NVMe SSD. The five-run synthetic
   local parse, preview-hash, and LV-calculation trials pass with self-hashed evidence; they do not
   close the live release gate. The bounded one-LINE `lighting.dwg` Scratch execution now passes;
   general circuit-fragment acceptance on this project additionally requires an explicit reviewed
   mapping for its pre-existing incompatible `E-WIRE` layer. Do not recolor, rename, or silently
   reuse that layer, and do not weaken the registered symbol/layer checks. The fail-closed path now
   reports the exact rejected validation markers (`WIRELAYERVALID=0` in the live project trial)
   instead of only a generic unchanged-hash error.

3. Complete the preliminary Schneider `NSYSM16840` panel GA after visual review of the 2D preview
   SHA-256 `b62ec2ba...15928` and 3D envelope preview `77e1be86...a724d1`. Before any
   fabrication/construction release, resolve the likely
   `C25B32TM250`/`C25B3TM250` typo, exact `METSEPM21xx`, CT model/dimensions, `NSYTRV42GAT`
   suffix, NSX communication/accessory references, mounting plate, busbar material/support/fault
   duty, cable sizes/bending radii, ambient/installation conditions and IEC 61439 temperature-rise
   verification. The current standalone 1:1 2D and 3D DXFs are review previews only and do not
   modify `lighting.dwg`; detailed manufacturer casing geometry remains outside the 3D envelope
   model until official CAD assets are reviewed.
   The first exact supplied CAD substitution is complete in the separate hybrid preview:
   `RE22R1MYMR` replaces both `KT1`/`KT2` envelopes and passes a read-only `DBMOD=0` postcheck.
   Continue only with exact BOM-to-asset matches; do not substitute the supplied `C25W32D250` or
   `C25W35E250` models for the requested `C25B3TM...` references without engineer approval.

Any placement of a registered manufacturer symbol must use the existing preview, review,
one-use approval, recovery, and verification lifecycle.

All later drawing creation/update must continue through the completed Milestone 18
preview, exact review, one-use approval, scratch execution, recovery, and verification
lifecycle before live mutation acceptance.

## Supporting platform — Operator Bridge live acceptance

The source command catalog now includes the separately approved HIGH-risk
`autocad.summarize_drawing_index_for_ai` egress boundary. It validates one exact index ID/SHA pair
and gives the configured provider only a bounded engineering inventory, never local paths or full
entity records. The 410-entity Hutchison R03 capture produces a 2,972-byte summary and focused
tests pass. Restart the credential-isolated Gemini bridge, approve this exact summary action, and
verify that Gemini returns a useful read-only panel analysis without claiming thermal, clearance,
fabrication, or standards certification before calling the live path accepted.

The destination AutoCAD 2023 Gemini session reached Google successfully but received a transient
HTTP 503 high-demand responses from `gemini-3.6-flash` and `gemini-3.8-flash`; the destination
account also reports `gemini-2.5-flash` unavailable to new users. Source now defaults to the stable
`gemini-3.5-flash-lite` model and retries exact reviewed requests after bounded 1-second and 2-second
delays for HTTP 429/503; focused tests pass. Restart one credential-isolated Gemini session and
complete one approved general-chat response before calling the destination live retry accepted.

Existing-component relocation is now decomposed in `docs/AUTOCAD_COMPONENT_MOVES.md`. The new
registered HIGH-risk `autocad.preview_component_moves` tool produces exact source/context-bound
read-only XY translation proposals for 1..32 named blocks. The native selection button and goal
egress review compile; 172 focused tests pass. No execution or spacing certification is enabled.
Next capture native extents/dependencies and reconcile source fingerprints/transforms, then extend
the existing Milestone 18 lifecycle with exact move-preview approval, scratch-only supported-API
transaction, verified unmoved controls, recovery and separately approved new-file handoff. Finally
run native live acceptance and update the installed package. Do not treat a filename match or
distinct insertion points as proof of source identity or geometric clearance.

The r50 continuation bundle now carries latest sanitized source, tests, the Hutchison Ports review
project and all 17 component source DWGs, plus checksum-verified installation and Arabic handoff
notes under `docs/transfer-20260917`. Clean local Windows restoration, existing-folder refusal and
corruption refusal pass; the expanded focused suite has 175 passing tests. On the second PC follow
that guide, rebuild against its own licensed AutoCAD assemblies, run read-only acceptance and then
continue the relocation hold points above. Do not reuse session credentials or treat local transfer
installation as live destination AutoCAD acceptance.

The source AutoCAD palette now also contains a `Project Agent` client for the existing reviewed
developer-run API. It accepts a natural-language goal, renders the proposed plan and exact pending
tool payload, supports approve/reject/cancel, can continue LOW/MODERATE local actions after one
visible plan approval, and retains a distinct final gate for sensitive commands. Source tests and
the AutoCAD 2021 DLL build pass. Package and run one live Gemini acceptance from a clean read-only
drawing: approve a harmless status/palette plan, verify its activity/history, reject one proposed
action, and confirm `DBMOD=0 -> 0`. Do not expand this into raw command strings, blanket filesystem
or shell access, or direct live-DWG mutation; every new capability must be a registered typed tool
with its own validation, risk level, recovery, and verification.

The source AutoCAD palette now has a natural `Ask AI` flow and explicit general-chat sessions that
need no drawing selection. It retains bounded multi-turn history, offers drawing context only by an
explicit checkbox, and combines local message storage, exact egress preview, a fail-closed Yes/No
decision, and the read-only provider answer behind one user-facing action. Focused tests and the
AutoCAD 2021 DLL build pass. Candidate r48 is packaged, installed side by side, readiness-checked,
and compiled against AutoCAD 2021. Restart from its Gemini launcher and complete one general
question plus one explicitly context-bound question. Do
not restart the current AutoCAD document until its existing `DBMOD=21` work is saved or discarded by
the engineer.

The ordinary Gemini Code Assist Agent now has a separately configured local stdio MCP facade over
the existing AI-Engineer boundary. Its initial surface contains only
`ai_engineer_autocad_status`; it cannot accept AutoCAD command text, read entities, save, or mutate.
Automated protocol/configuration/regression checks and the real settings installation pass. Reload
the isolated VS Code window and complete one `/mcp`, `/tools`, and live status call at
`DBMOD=0 -> 0` before adding any further read-only tool or beginning the deferred Copilot MCP setup.

The provider selector now includes independently gated `openai`, `gemini`, `ollama`, `codex`, and
the existing Ollama-to-Codex `hybrid` mode. Gemini's fixed endpoint, strict response schema,
environment-only credential, packaging, and no-execution-authority contracts are automated-tested.
The first separately approved live Gemini call returned a bounded Arabic answer in AutoCAD without
drawing mutation. The `raw_blocks` field, prerequisite-aware palette states, and follow-on raw-route
primitive distinction have passed source-tree live restart. Portable r40 retains that behavior and
adds the full drawing-index, event-backed selective-delta, immutable inventory-v2 composition, and
refresh pipeline. Its installer also creates the required empty `workspace`, `logs`, and `backup`
runtime directories, removing the manual directory creation exposed by r28. Its clean short-path
install passes 18-file readiness, direct installed DLL compilation, the installed inventory-delta
CLI, all local-core targets, packaged full-drawing capture, status, and visually confirmed palette
acceptance. Its fixed maximum Scratch fixture also passes; second-profile acceptance remains open.
The source command catalog now also exposes three fixed live AutoCAD tools to provider-planned VS
Code developer runs: exact document status, palette display, and approval-gated full-drawing index.
No arbitrary command string or mutation is accepted, and the returned provider observation omits
paths and drawing contents. Automated coverage passes; package and run one separately approved live
Gemini round trip before calling this channel accepted. Portable candidate r41 is clean-built and
installed under package acceptance with all three tools present; r40 remains the current daily copy
until the corrected successor passes live acceptance. The r41 trial proved the AutoCAD operator
round trip at `DBMOD=0 -> 0`, but exposed that its Gemini launcher did not open a token-bound VS Code
Operator. Source now launches an isolated VS Code profile with the reviewed bundled extension;
package and live-test that correction next.
Candidate r42 is now built and installed side by side. Its bridge, AutoCAD beacon, exact installed
`get_document_status` round trip, `DBMOD=0 -> 0`, and extension installation passed. The isolated
VS Code window was blocked only by the workstation's pending global VS Code updater mutex. Resume
after saving work, closing all VS Code/AutoCAD windows, and allowing that update to complete; then
repeat only the r42 launcher and one reviewed Gemini typed-tool request.
The r43 live trial exposed one final webview-script startup defect after the bridge, token, port,
and extension host were independently verified. Operator 0.1.6 corrected it with a packaged local
script under restrictive CSP. The panel connected automatically and the reviewed Gemini typed-tool
run passed with exactly one `autocad.get_live_document_status` action at `DBMOD=0 -> 0`, accepted
verification, no paths/drawing content in the provider observation, and no drawing mutation.
Clean successor r44 contains the accepted fix and passes package installation/readiness.

0. Complete only the remaining destination Windows **visual panel** acceptance with
   extension `0.1.4`: start VS Code from the real bridge launcher/session, press Refresh,
   visually confirm `API ok · 0.18.0 · provider ollama`, Working/progress/activity, and
   persistent Cancel visibility. The model installation, authenticated loopback checks,
   live `project.scan` + `file.read` run, duplicate prevention, and responsive live Cancel
   have passed through the same API used by the panel.
1. From the installed VS Code Operator side panel, visually repeat the already-passing
   API command acceptance: disposable backed-up write, bounded compileall, HIGH pytest
   approval gate, and read-only AutoCAD inventory. The authenticated API path has passed;
   only direct panel observation remains.
2. Enable Codex egress for one separately approved isolated reasoning call and verify
   hybrid fallback without
   exposing bridge/provider credentials or unapproved drawing content in logs. OpenAI API
   acceptance remains optional.
3. Keep AutoCAD drawing mutation behind Milestone 18 preview/execution acceptance; the
   Operator Bridge must not bypass drafting lifecycle hashes, preview, recovery, or
   approval gates.

## User-directed Revit 2024 read-only foundation

The bounded Revit 2024 inventory connector and first chat-driven request loop are live
accepted. Licensed startup, ribbon
registration, the strict Python boundary, the Autodesk Snowdon Towers electrical sample,
and a small 6.46 MiB Autodesk architectural sample passed. Every category count is now
cross-checked by two independent Revit collectors; mismatch fails closed. Both samples
closed without saving with unchanged SHA-256 values and no provider egress or mutation.
The local `revit-read` launcher now submits an exact document-bound UUID request,
wakes Revit through the supported `ExternalEvent` mechanism, and receives the correlated
inventory result without an in-product button press. Same-session live acceptance passed.

The School Architectural working copy has a source-bound, non-mutating electrical distribution
preview for all 108 rooms and 96 ceilings. The approved basis is `380/220 V`, three phase, four
live conductors plus separate `PE`, `50 Hz`, `TN-S`. The current detailed workbook calculates
1,152 preliminary lighting units / `29.680 kW`, locates 806 review-only XY points plus 45 separate
gym XY points, and proposes 438 general receptacles, 21 GFI points, and 78 dedicated points. The
latest verified sample RVT contains 45 gym high-bays and 8 classroom panels; those 53 instances are
not the whole-school distribution. The source-bound extraction report is
`workspace/revit-school/ENGINEERING_CALCULATIONS_2026-10-29.md`. Seventeen rooms still have no XY
points, and exact hosted geometry/room review is open. Circuit and panel sizing, protection, cable
ampacity, voltage drop, fault duty, and code compliance are not established from the architectural
model. Keep remaining distribution as review-only until licensed Egyptian clauses, approved
photometry/families, loads, panel/source data, and routing inputs are reviewed; the loaded 120 V
troffer must not be used for the 220 V design basis.

A formula-driven intake workbook now carries the room counts, per-type luminaire quantities and
connected watts, 78 dedicated equipment rows, receptacle VA/current inputs, demand factors, and
equipment current/voltage-drop formulas. A new source-bound review package shows full-building
lighting and outlet candidates on three overview plans, including existing points and candidate
MDB/SMDB/branch service zones. Counts reconcile to the calculation workbook. The user must review
the room-by-room candidate points before any Revit edit. The drawings use room/ceiling bounding
boxes and retain 301 unresolved lighting positions plus one level-classification exception.
Generic dedicated outlets have no appliance load/phase identity. Next resolve the visual review,
exact wall/ceiling hosts, level classification, approved Egyptian-code clauses, and equipment
schedule as available; only then make a hash-bound Revit preview. Circuit/cable/protection design
remains separately gated on verified routes and source/fault inputs.

The first in-Revit visual review overlay is now live in
`scratch/School-Electrical-Full-Distribution-Review-20261030.rvt`: 1,400 candidate/observed points
and 6,852 view-specific detail curves across three new plan views. This file is visual review
evidence only and its bounding-box lighting distribution is rejected/superseded after the engineer
identified stair placements, missing room coverage, and apparent overlaps.

V2 and V3 lighting layouts are superseded. V3 filled 45 of 48 gym grid cells and left three
isolated holes visible in the operator's marked screenshot; its count and containment checks
did not measure coverage. V4 balanced rows and sampled coverage have passed live Revit execution
in `scratch/School-Electrical-Review-V4-20261030.rvt`: six Electrical lighting/power views contain
1,152 lighting points, 426 new duplex candidates, 21 GFI candidates, 78 generic dedicated points,
16 observed outlets, and two panel-zone marks. The gym now has five rows of nine and a maximum
sampled nearest-light distance of 2,954.1 mm. The operator's 17 marked Level 1 locations
were checked against exact Revit room containment: one lies outside all rooms, one overlaps
a stair bounding box, and three have no ceiling bounding box at their centers. The marked
circles include wrong positions as well as apparent gaps. The read-only nearest-light
query passed live: the marked upper west stair point is 3,531.8 mm from the nearest
same-room symbol stroke, while several other marked points are within 700 mm of one.
The 17-mark local symbol-stroke audit found zero room-boundary crossings within 3 m.
A full Level 1 V4 read-only audit found 3,568 symbol strokes, zero room-boundary or
same-level door/stair bounding-box intersections, and 1,150 strokes without same-level
ceiling bounding-box candidates across 14 rooms. Door clearance and actual mounting
faces remain unresolved. The V5 actual-area proposal passed Revit's read-only geometry
test for 108/108 rooms and 947 exact room-light points after two storage rooms were
raised from two to three; 170 points across the three views have no same-level ceiling
bbox candidate. The exact execution preview `80431B30...365D0751` passed a final
read-only Revit rerun with 108/108 rooms, 947/947 identical coordinates and matching
source/preview hashes. The operator approved that preview, and live Save As created
`scratch/School-Electrical-Review-V5-20261031.rvt` at SHA-256
`8D533212...FAE1B8`; the source hash stayed unchanged. Revit opened the new Level 1
lighting view. The operator reported no further V5 placement comments, but the
west-stair wall-light review marker and 170 mounting-review points remain unresolved
engineering exceptions. The stair mark has count/height/photometry held. Resolve mounting,
type suitability, photometry and actual family placement separately. The V4 area
audit found eight rooms with bounding-box/actual-area ratios above 1.5; CORRIDOR 2513 is
726.274 m2 in the old schedule but 208.744 m2 actual. Reconcile preliminary counts and
photometry with actual areas and approved design criteria. An area-only recalculation suggests
945 rather than 1,152 lights, but is not a validated final design. V4 draws view-specific Detail
Lines, not placed lighting/outlet families, circuits, cable routes, or actual panels. After
visual approval, resolve exact wall/ceiling hosts, approved 220 V families,
the Level 2 classification exception, and circuit/route/source data through a separate bound
preview before physical placement or engineering approval. Do not reuse rejected V1 positions.

On 2026-10-31 the operator reported no additional V5 placement comments and asked to
proceed with circuits, connections, calculations and a reusable error record. The
project-specific causes and prevention gates are now in
`docs/SCHOOL_ELECTRICAL_LAYOUT_LESSONS.md`. A tested read-only V5 ledger V3 reconciles
all 108 rooms, 947 review lights, preliminary 25,730 W active lighting load, and
426/21/78 duplex/GFI/dedicated candidates. A separate live category query found 53
existing Lighting Fixtures, 16 Electrical Fixtures and zero Electrical Equipment;
these must be matched to the review marks before new placement. An eight-file
read-only donor-family audit found absent connector elements in the sampled panel,
outlet, switch and dedicated-connection families, 277 V troffer types, a 230 V
CoreLine type requiring range verification, and an industrial family with incomplete
load data. The ledger leaves VA, circuit IDs, breakers, cable sizes and installed
lengths empty. A three-sheet V5 Excel intake now ties all 108 room rows to proposed
lighting/power zones, computes active W and conditionally computes VA/current only
after green PF/voltage/unit-load inputs are entered. The artifact engine passed a
blank-to-filled-to-blank formula test and zero formula errors. Native Excel read the
baseline 108/947/25,730 totals, but its separate live input-change trial was blocked
by a transient COM busy/null-workbook response; do not claim native dynamic
recalculation acceptance yet. This is an interim visual review response, not final
engineering acceptance of the 170 mounting flags or west stair. Next, in order:

1. Extend the live family intake beyond the first eight candidates or repair suitable
   families in a controlled scratch model; confirm true connector/host/voltage/load
   behavior and current photometry. Reconcile the 53 existing lighting fixtures and
   16 existing outlets to V5 marks so conversion cannot double-place them. Resolve
   170 flagged mounting points,
   the stair fixture design and Level 2 classification exception. Record actual versus
   proposed symbols; reject non-electrical or incompatible families.
2. Prepare a new hash-bound placement preview with exact family/type/host/coordinates,
   source/output paths and recovery. Place a bounded sample in a new RVT after review,
   then read back IDs, host relationships, connector data and counts. Scale to the
   remaining project only after the sample passes visual and electrical checks.
3. From verified physical elements, design lighting and power circuit membership,
   actual panels, phase/load allocation and switch control separately. Preview each
   change and verify ElectricalSystem membership after approved Save As.
4. Trace and review real wiring/conduit paths, vertical drops and routing constraints.
   Only then calculate VA/current, demand, voltage drop, cable/conduit/box quantities,
   breaker/fault/coordination and panel schedules. Keep every missing project or code
   input explicit; no implicit circuit rating, cable size or compliance inference.
5. Repeat the V5 workbook input-change/recalculation test in a clean Excel session
   before relying on native Excel results. Preserve the source-bound blank inputs;
   confirm no formula errors and independent sums after reviewed inputs arrive.

The next read-only V5 step now has exact physical-instance evidence: two complete
Revit category pages matched all 53 existing lighting fixtures one-to-one to the
approved V5 symbols. GYM 3500 has 45 physical / 45 proposed with 15 pairs within
50 mm and a maximum candidate-pair offset of 198.307 mm. CLASSROOM 1500 has eight
physical / eight proposed with no pair within 50 mm and a maximum offset of
2401.175 mm; its physical fixtures need position review, not duplication. The other
894 V5 lighting points are candidate new positions only after family/host checks.
The operator approved the exact CLASSROOM 1501 preview
`5B5C9F09...906F4DC8`. Eight physical L1 fixtures were saved in a new RVT and
verified in the active Revit document after Save As; IDs `457165`–`457172`, output SHA-256
`66405403...8EA8E3AB`, original V5 source and backup SHA-256
`8D533212...FAE1B8`. All eight have one unassigned PowerCircuit connector.
The live circuit read initially found their auto-generated Mark values were
`AIENG-L1-CLASSROOM-1500-09` through `-16`, although the approved coordinates
belong to CLASSROOM 1501. A separate approved Save-As correction now gives the
eight physical instances room-specific Marks; verify actual room containment
before circuit membership or full-building scaling. Perform a distinct close-and-reopen
read of the saved RVT. The generic placement verifier checks location/type/count,
not Mark or room. Inspect the sample visually
in Level 1 RCP/MEP view before accepting its appearance. The reference fixture
has one PowerCircuit connector and no circuit assignment; voltage and VA were not
returned by its V5 instance query, so type parameters and manufacturer
compatibility remain engineering holds. The V5 preview does not expose exact
candidate outlet coordinates, so the 16 existing outlets cannot yet be subtracted
from the 525 power review marks.
On 2026-11-01 the operator approved the eight-element correction preview
`141A51C0...933A1820`. Its bounded Save-As bridge saved
`School-Electrical-V5-Classroom1501-Marks-Corrected-20261101.rvt` at SHA-256
`DABF745E...F80544F`. Post-save API verification and independent category read
confirmed all eight `CLASSROOM-1501-01..08` Marks, unchanged element IDs and
locations. Source and backup SHA-256 remain `66405403...8EA8E3AB`.
**Next:** confirm the saved file after a distinct close-and-reopen and visual
inspection in a suitable Level 1 MEP/RCP view; the eight instances are level-based
and `host: null`, so ceiling attachment is unproven. Then resolve voltage/VA,
photometry, panel family, circuits and the remaining project placement gates.
The existing `school_spot_query` room-containment action refused this V5 sample
because it requires a V4 lighting review view name. Extend that read-only action
to support the exact V5 review view (or a view-independent room-only mode), test
it on a copy, and retry room containment before claiming this independent check.
On 2026-11-01 a live read-only family-type inventory found only eight Lighting
Fixtures types, two Electrical Fixtures types, zero Lighting Devices and zero
Electrical Equipment types in the corrected V5 RVT. An eight-file Autodesk stock
family intake found a 220 V receptacle with a connector, but sampled stock switches
and lighting types are 120/277 V and the stock panelboard is 208 V. See
`workspace/revit-school/previews/electrical-distribution/School-V5-Family-Readiness-20261101.md`.
No stock family was loaded or accepted for the project. The V5 room query now
selects the exact V5 review view before V4, and a new read-only power-mark extractor
is compiled and unit-tested to recover exact centres of 426 duplex, 21 GFI,
78 dedicated and two panel review marks. The V5 room query passed live acceptance;
the power extractor still needs live acceptance. Do not claim the 525 power marks
are physical outlets.
The first live V5 room query now confirms all eight CLASSROOM 1501 sample points
inside room `221095`, with ceiling bounding-box candidate `278802` and no detected
door/stair/shaft hit. This is spatial evidence, not a true hosted-face check.
The first live power-mark query stopped before output because it mistakenly
expected six power views; the V5 handoff proves three power and three lighting
views. Corrected and recompiled the read-only query to expect the three power
views; repeat live acceptance after Revit reload, then assemble all pages and
reconcile physical outlets before any outlet placement.
The corrected DLL was installed on 2026-11-01; build and installed SHA-256 both
`E3E2A29712F73A3A72F72BFEF801C1CC02090EB71F22A45CA371C4EBB4C520EB`.
Revit PID 2216 started on the unchanged model, whose SHA-256 before reopening was
`DABF745E7E00D9B5526043414F7B7DC3906D6B43CA2310EDFA718D744F80544F`.
The user completed Load Once and the project loaded. Python tests: 3 passed.
Do not install another DLL while Revit is open.
The user loaded the add-in. Live paged capture then succeeded: 527 marks in 17
pages (426 duplex, 21 GFI, 78 dedicated, two panel), exactly one containing
room per mark and zero count mismatches across 108 V5 preview rooms. A fresh
read found 16 hosted physical outlets and 61 physical lights. Five physical
outlets lie within 1,000 mm in plan of a proposed outlet mark; inspect them
before any placement. All 61 physical lights are Level 1; the category query
does not verify ceiling host or voltage. See `School-V5-Power-Mark-Live-Audit-20261101.md`.
An independent two-page electrical relationship query found exactly one
PowerCircuit connector and zero assigned circuits on each of the 61 physical
lights, with no reported voltage or VA. The existing V5 calculation workbook
still states 53 physical lights; correct that provenance in its next revision.
Keep the preliminary 25,730 W / 947 light symbols separate from installed load.
A dedicated read-only `school_v5_outlet_connector_query` is compiled, and five
validator tests pass. It captures connector, instance/type voltage and
apparent-load parameters, wall host and circuit membership for the 16 existing
outlets in the SHA-pinned model.
The user closed Revit, the new DLL was installed and loaded, and the live query
completed after increasing the Python response output-name bound to 96 chars.
All 16 existing wall-hosted outlets have **zero MEP connectors**, with no
electrical voltage or VA reported. They cannot form Revit power circuits as-is.
The response-bound fix and outlet validator passed 21 related Python tests.
Next validate an actual 220 V connector-bearing outlet in an isolated RVT,
including mounting/host, then prepare a source-bound project placement sample.
The owner approved use of a standard provisional allowance without inventing
special-equipment loads. A new calculation workbook sets the editable general
and GFI inputs to 180 VA each, preserves dedicated VA as blank, and updates
the physical light count from 53 to 61. This is a comparative NFPA 70
220.14(I) planning allowance, not verified Egyptian-code compliance. See
`School-V5-Outlet-Design-Basis-20261101.md` and the new workbook under
`workspace/revit-school/outputs/school-v5-20261101-standard-outlets/`.
An isolated 220 V outlet placement/circuit test action is now built but not
installed while the user's Revit session is open. It creates a separate metric
test RVT with a wall, loads the exact SHA-pinned Autodesk RFA, places one
face-hosted instance, attempts one unassigned PowerCircuit, and saves only the
new file. Preview `2160ae0a-3be4-4b16-baac-e54e56dc8ec5` / file SHA-256
`5041A9EE84A2B28AC31800595CD6625DD52445DB4926B6407E1DECAFD7C2F932`
expires 2026-11-02 00:43 Cairo. See the isolated-test preview note. Close Revit
after any personal save, then install and reopen the exact source to run it.
The first live isolated test reached `Application.NewProjectDocument` but
`LoadFamily` returned false/null; no test RVT was saved and the source was
unchanged. The Revit journal showed a new `Project1` created without a specific
API exception. The new code checks for a matching family preloaded in the
blank project and gives a bounded list of existing families if unavailable.
It compiles, but Revit must reload the DLL before the retry. Keep the same
preview only while its source/family/output hashes and expiry remain valid.
The retry showed `LoadFamily=False`, `LoadFamilySymbol=False` and no matching
family in the blank `Project1`; no output RVT was saved. A separate read-only
intake of the Imperial Autodesk 220 V receptacle found the same one connector
and 220 V / 180 VA type parameters. A revised isolated test now uses the
installed `Electrical-Default_Metric.rte` instead of `NewProjectDocument(UnitSystem.Metric)`.
The revised preview ID is `63cbc481-347d-4abf-803a-05a1a258d497`, file SHA
`5D3E5F79D08D5108EBF067378629DF17C0CD03F3AEFBFB4799EC8CB79AC9F3DC`,
expiring 2026-11-02 00:51 Cairo, with output
`School-Outlet-220V-Electrical-Template-Test-20261101.rvt`. It is compiled,
not installed while Revit is open. The user was asked to close Revit for reload.
The electrical-template retry also returned `LoadFamily=False` and
`LoadFamilySymbol=False`, despite `IsModifiable=False` and `IsReadOnly=False`.
Using the exact hash-matched staged RFA path produced the same result. Neither
retry saved an RVT or changed the source. An additional compiled fallback now
opens the RFA as a family document and uses `familyDocument.LoadFamily(test)`;
this API should either load it or provide a specific exception. Revit must be
closed/reopened for that new DLL. The current staged-family preview ID is
`1edfb7bf-ea55-43d4-8490-cd27e1445ee8`, SHA
`92CE98318B559922130A180F39E9D9007EB134B50C6B1713FFADD530434BAB4C`,
output `School-Outlet-220V-Staged-Family-Test-20261101.rvt`.
The family-document `LoadFamily(test)` fallback succeeded in the staged test:
the isolated RVT was saved, with one wall-face-hosted 220 V/180 VA receptacle,
one electrical connector, and PowerCircuit capability. Output SHA-256:
`1035995C7F7188CB3F9C1A1A908B4586DB6C539CE878365953D58FDB31AF3F7F`.
The circuit verification reported `Modification of the document is forbidden`:
`test.Regenerate()` was placed after transaction commit. Moved it before commit
and prepared a separate circuit-test preview `0c7bf6c4-bfa0-491f-aea4-063998045d92`,
SHA `FA24AC21E7F15786D1D4819D496C075296812949841574E43434DAAA16CEBD7B`.
No school source mutation occurred; source SHA stayed `DABF745E7E00D9B5526043414F7B7DC3906D6B43CA2310EDFA718D744F80544F`.
The corrected circuit test passed in a second isolated RVT,
`School-Outlet-220V-Circuit-Test-20261101.rvt`, SHA-256
`A381DEC98ED272E327A460534372F5F7F971247DFDF6F545468482F03F262FF2`.
Revit created PowerCircuit ID 810331 with 220 V and 180 VA; `circuit_error` is
empty. This proves the selected RFA can be loaded, face-hosted, and circuited
under Revit 2024. The school source SHA remained unchanged. Next map power
markers to actual school wall faces and host constraints, check existing outlets,
then preview a small physical outlet sample in a new school RVT before scaling.
Two further complete read-only V5 electrical-relationship pages confirmed all 53
physical lighting fixtures have exactly one PowerCircuit connector and **zero**
ElectricalSystem memberships. The 16 observed Level 1 outlets comprise 12 Duplex
and four GFI family instances; their bounded category query has no voltage field.
Actual panel family/equipment count is still zero. Start circuit design only after
approved physical placement and reviewed panel/distribution family intake; do not
infer circuit membership or voltage from the review symbols or outlet names.

The operator-supplied `family.rar` and `Philips.rar` libraries have completed static intake review.
The first contains 1,603 Revit families and no external IES/LDT files; 80 priority candidates are
readable by Revit 2024 but mostly originate from Revit 2011. The second is an unsigned legacy
Philips Product Selector 5.2.12.2 database with proprietary DIALux/Relux-era model data, no RFA,
and no standalone IES/LDT. Do not bulk-load or install either package. Next use a scratch Revit
copy to inspect only the priority families for category, host, connectors, voltage, load, type
parameters, photometry, and upgrade warnings, then bind accepted 220 V/50 Hz types to a new preview.

A no-cost official lighting intake is now staged from public Signify/Philips sources: 20 downloads,
17 RFA files (16 unique hashes), 36 valid IES files, one LDT file, and six data sheets. It covers classroom
panel, corridor batten, IP44 service-room photometry, waterproof linear, gym high-bay, and exit-sign
photometry. ZIP paths and hashes are recorded and an on-demand Defender scan returned no detection.
Do not yet load these into the school RVT. The bounded read-only external-RFA query is implemented,
installed at DLL `B19C828F...3468D`, compiles against Revit 2024, and passes its Python contract
tests. Live read-only requests `77dfd186...d5c2` and `149ad490...8362` inspected the
selected Panel, Batten, Highbay and Waterproof RFA files without loading them into the school model.
Current product-specific packages are now bound for Batten `911401816685` and Waterproof
`910505100047`. Approved exact scratch preview `3F0ABF99...D28E7E` passed live Revit 2024 execution
and reopened verification under request `21d66517-1a40-467c-8ae1-263a50f77ae0`: all four exact type
names, locations, visible geometry, bounding boxes, Lighting Fixtures categories, and one electrical
connector per instance were verified. Output `49F851D3...53EAC` is isolated under the school scratch
directory and protected source `D0D0C48...09F3` remained unchanged. The live result also confirms
that several RFA internal load/photometric defaults are stale; Revit rejected the companion Type
Catalog rows, so do not use those embedded wattage/lumen/IES values as the calculation source.
The original source-bound photometry preview `043B3315...0C45CB` is complete. A current exact-code
official refresh `4BCD2DE5...84CB1E6` now resolves L2 at `2500.4 lm/19 W` and L5-L6 at
`4000.6 lm/30 W`. L4 remains held because its refreshed exact IES is still `67 W` versus the
current exact product page's `63 W`.
L1-only parameter preview `310E62C9...FB8FDA` proposes exact changes from `19 VA/19W` to
`31 VA/31W`, exact IES binding, a new non-overwriting RVT, unchanged source proof, and reopened
verification. Exact approval was consumed by live request `633a5606-15e5-459e-8a93-76cc75dcff6d`;
the reopened output passed and was published at SHA-256 `3302C249...7C604D`, while the isolated
source stayed `49F851D3...53EAC`. Non-executable L2/L5-L6 parameter preview
`8359BB40...021781D` was explicitly approved and live request
`4056f61e-e41e-499d-bf6a-e499b5038506` passed reopened verification. Published output
`B4EC298B...50D910` preserves L1, verifies L2 at `19 VA/19W`, verifies L5-L6 at `30 VA/30W`, and
retains one connector per target; source `3302C249...7C604D` remained unchanged. The room preview is bound to the official product
candidates and includes an emergency/exit hold register, but remains non-executable. Obtain the
licensed Egyptian illuminance and emergency-lighting clauses, approved fire/egress strategy, exact
regional order codes, and responsible-engineer approval before issuing final quantities or an RVT
placement preview.

L4 batch 1 is complete. Approved preview `5E78A601...6F273C` and `9.500 m` mounting elevation
produced 23 exact level-based fixtures under live request `592c9a53...e5ce`; reopened read-only
verification `e88d158a...2734` confirmed every point, family/type, mark, level, elevation, and one
electrical connector per fixture. Output `School-Architectural-electrical-L4-Batch1.rvt` is
`941551BE...41A1A`; source and backup remain `D0D0C48E...09F3`. Next generate a new expiring,
hash-bound batch-2 preview against output `941551BE...41A1A` for the complementary 22 points.
Batch 2 is complete. Approved preview `56552925...943801` produced 22 complementary fixtures
under request `05cb6ac6...e71a`; reopened verification `f4c7f5f7...3b03` confirmed the complete
45-instance set, exact marks/coordinates/elevation, and one connector per fixture. Complete output
`School-Architectural-electrical-L4-Complete.rvt` is `FB72029C...C52D1`; batch-1 source and backup
remain `941551BE...41A1A`. The earlier batch-2 preview `80A0DE51...9D971A` expired unused.

The approved `CLASSROOM 1500` sample fingerprint `0293A7A6...39B116` has been consumed.
New output `scratch/School-Architectural-electrical-Classroom1500-Sample.rvt` has SHA-256
`2F4E3543...3F582`; separate reopened read-only verification confirmed all eight exact points,
fixture parameters/connectors, and preservation of the 45 L4 instances. The execution and
verification reports are beside the RVT. The source remained `FB72029C...C52D1` and was backed up.
Do not expand to other rooms yet. A live UI review located all eight fixtures in the existing
`01 RCP - MEP COORDINATION` Level 1 reflected ceiling plan and selected exact IDs
`447420` through `447427` together; Revit showed `Lighting Fixtures (8)`. The ordinary `Level 1`
floor plan was the wrong view for this ceiling-mounted sample. Obtain explicit engineer acceptance
of the now-visible layout and review ceiling-grid alignment, board/projector/furniture, point-by-point
photometry and glare. The visual-query exporter still incorrectly captured the whole-school 3D
view and needs a separate fix. Keep the 18 held rooms and emergency/code/photometric holds unchanged.

The responsible engineer has now accepted the exact L4 IES value `10501.2 lm/67 W` for preliminary
distribution. Non-executable preview `1E3B1726...4B06FA3` places 45 XY points on a `9x5` grid in
`GYM 3500`; the rectangular room bounding area reconciles to `801.171 m2`, the lumen-method result
is `306.7 lux`, and active connected load is `3.015 kW`. Excel output
`E4F6575C...154F042` binds the current exact L1/L2/L4/L5-L6 photometry and passes native Excel
recalculation with zero formula errors. The approved `9.500 m` batch-1 model placement does not
close power factor/VA, IES point calculation, sports glare review, or licensed Egyptian-clause
evidence; keep compliance and final electrical design claims blocked while preparing batch 2.

1. Design the next separate Revit gate for ceiling-hosted lighting families. Live Snowdon
   discovery now resolves loaded links through supported Revit references and filters by the
   locale-independent `OST_Ceilings` identity. Query
   `194fa83e-0e09-4628-8229-86622aaf5cae` found four exact linked-ceiling candidates; retain
   candidate fixture `1622255`, symbol `1453791`, link instance `1362762`, architectural ceiling
   `1566775`, and its stable `HostFace`. Non-executable preview `E1EC3667...69659B55` now binds
   those exact identities per placement and rejects missing, malformed, or non-ceiling faces.
   Read-only host-face request `a109ccf2-b464-4fe6-8aee-8ef32c14ae56` now validates that exact
   linked ceiling and returns bounded in-face candidates from a 2-triangle/4-vertex mesh. The best
   candidate is `(13592.175, 11213.042, 2743.2)` mm with 366.486 mm centre clearance from the
   nearest fixture; preview `DC5AE2D0...998525C5` binds it but remains non-executable. Before
   single linked-ceiling write path is now implemented and installed behind exact preview approval.
   It re-resolves the linked ceiling and face, checks the point is still on that face, enforces a
   250 mm centre-clearance integration gate, uses one rollback-capable transaction, saves only to a
   new workspace RVT, and has a separate reopened verifier. Fresh preview
   `37FA4716...99CFDC8E` was explicitly approved and passed live execution plus reopened API
   verification. Request `465adfae-6b4f-4466-ab1e-fff5f7efdfa7` created element `1735503` in a
   new RVT with output SHA-256 `B9D6A610...296D30`; verification
   `76d5e38b-a197-4fe2-9866-4d66effda3f5` matched its symbol, point, linked face, and file hash.
   Visual acceptance is now complete. Exact read-only comparison matched the created and exemplar
   family/type, linked host, plan bounds, visible geometry count, orientation, visibility, view
   filters, and overrides. Reversed-order trials proved the prior blank/point export was a first-UI-
   redraw timing artifact; the two-stage focus flow rendered the complete created symbol in live
   evidence `f7b364fa...96c0` without RVT mutation. Keep the current live-accepted execution
   path limited to 1..32 reference-bound instances of one `OneLevelBased` family type, one
   level, one physical elevation, and explicit per-instance rotation. Mixed types or levels,
   parameter edits, and circuits remain later independent gates. Bounding-box centres must
   never substitute for actual `LocationPoint` or explicit level-offset data.
   The separate first-transaction Model Lines preview and execution gate passed live:
   approved preview `F9F06DE7...B7743CB0` created exactly four Model Lines in one
   transaction, verified their element IDs, did not save, and closed at unchanged source
   and backup SHA-256 `B9D1DB38...691FC5E4`.
2. Complete engineering review of the live-accepted Revit circuit assignment. The bounded
   read-only relationship query proved exemplar `1622255` is a member of system
   `1622590` (`P109 / 5`, `120 V`, `80 VA`, `76 W`, PF `0.95`, one pole), while created
   fixture `1735503` has no electrical system. Exact connector reads now show both fixtures
   have the same single `DomainElectrical / End / PowerCircuit` connector, and review-only
   preview `FE9DB83D...A7F3AEF` binds the unassigned fixture, actual reference membership,
   system `1622590`, and source SHA-256 `1C390703...BCCB8`. It is explicitly non-executable.
   The operator explicitly approved the exact preview. One supported-API transaction assigned
   fixture `1735503` to system `1622590` and saved a new RVT at SHA-256 `A5B3AC70...16031CE`.
   Reopened verification and an independent relationship query passed; source and backup remain
   byte-identical at `1C390703...BCCB8`. The resulting circuit reads `100 VA / 95 W`, an observed
   increase of `20 VA / 19 W`. Extended queries `fc117125...0857` and `e6cbd42d...3736`, plus
   deterministic review `F0EE0DAC...414F`, now record `20 A` circuit rating, `0.833333 A`
   apparent current, `1.473381%` modelled voltage drop, phase `C`, `12988.925 mm` modelled length,
   one run/way, three `#12` conductors, Copper `THWN` WireType at `60` temperature-rating text,
   non-magnetic conduit, `225 A` panel MCB, and panel phase currents. Arithmetic margins are
   positive and the calculated phase-imbalance indicator improves by `0.101966` percentage points.
   A bounded instance/type parameter scan (`6441f18d...b075`, review `F7CFA4BB...CF83`) additionally
   finds circuit `Rating = 20 A` and `Frame = 400 A`, plus panel `MCB Rating = 225 A` and a maximum
   of 30 single-pole breakers. It finds no breaker curve, fault-current, interrupting-capacity/AIC,
   or selectivity value; never reinterpret `Frame` as one. Obtain the governing Egyptian code/project
   criteria, installation/ambient/grouping derating and selected-conductor ampacity, breaker curve,
   missing panel short-circuit rating, fault current/selectivity, and responsible-engineer
   confirmation before treating this as an approved design.
   Calculation intake `AA0C2226...4198` now converts those hold points into 25 explicit, unit-aware
   missing inputs and binds them to review `F7CFA4BB...CF83`, query `6441f18d...b075`, preview
   `FE9DB83D...A7F3AEF`, and output RVT `A5B3AC70...16031CE`. Populate them only from reviewed
   project/code/manufacturer evidence; the intake fails closed for ampacity, fault, coordination,
   and compliance until then.
   Supported-API upstream tracing now proves `P109` is an actual member of feeder system `1672726`,
   circuit `3`, from `MB13` element `1668955`; it does not rely on the `Supply From` label. Revit
   reports `67.769263 A` against `20 A`, giving `338.846315%` modelled rating utilization and
   `-47.769263 A` headroom, plus `45.438594%` modelled voltage drop. Review
   `D6CDC73E...260A` and intake `74CDE784...3B9F` fail closed on that inconsistency. Determine
   whether the feeder rating/wire/drop inputs are stale or incorrect before any design acceptance.
   A first standalone Excel intake calculator now presents the exact `P109 / 5` observations,
   green engineer-input cells, active-value provenance, formula-driven ampacity/voltage-drop/
   short-circuit/protection readiness, and source hashes without changing the RVT. Expand this
   reviewed one-circuit pattern to all panels/circuits only after adding deterministic multi-circuit
   extraction, installed-route length evidence, and separately reviewed cable/conduit/device/box
   quantity rules. Missing inputs must remain visibly incomplete rather than becoming zero or a
   fabricated default.
   The installed `v28` bridge completed a live 19-page capture of all 589 fixtures with no
   duplicate, gap, or non-progress page. The source-bound inventory aggregates 563 connected and
   26 unassigned fixtures, 113 unique circuits, 31 panels, 22 family/type quantity rows, and
   4,998.453 m of modelled conductor quantity. A second strict paged capture now covers 538
   electrical fixtures, 196 lighting devices, 200 electrical-equipment instances, 530 conduit
   segments, 322 conduit fittings, and zero modelled cable-tray elements. It records 1,730.348 m
   of modelled conduit curves and retains 16 wiring pull boxes in the equipment inventory. The
   reviewed ten-sheet Excel workbook contains the
   Panel Schedule, panel inventory, lighting BOQ, cable quantities, and green engineering-input
   cells plus device, equipment, conduit, and fitting BOQs. Next obtain verified installed-route
   lengths, accessories not represented as model elements, material/specification evidence, and
   reviewed classification for the green cells. Keep modelled circuit length separate from verified installed route, vertical allowance,
   terminations, slack, waste, and parallel-run quantities.
   Direct bounded `ElectricalSystem` extraction is now live accepted at readiness `v29`: 448 power
   circuits were captured in 14 ordered pages without duplicate, gap, non-progress, egress, or
   mutation. The self-hashed inventory classifies 113 lighting, 264 receptacle, 67 feeder/panel,
   and four spare/empty circuits across 54 panels. A separate 54-sheet workbook preserves the
   supplied panel-schedule form, handles 138 multi-pole circuits without double-counting their
   circuit loads, and passes native Excel recalculation with 1,566 formulas and zero errors.
   Next obtain and enter the green engineering inputs: reviewed demand/diversity factors, spare
   allowance, location/mounting, breaker interrupting capacity, verified installed routing and
   length, feeder type, short-circuit current, coordination, and governing code/project criteria.
   Keep the aggregate circuit-row load distinct from non-duplicated project demand because feeder
   and downstream circuit values coexist in the direct-system inventory.
   Never infer membership from proximity or parameter text.
## Ordered later milestones

AutoCAD is the only active production target through its complete product and
real-project acceptance gate. Do not begin ETAP or SOLIDWORKS Electrical implementation
early; add them afterward as separate, sequential integration editions.

1. Milestone 22 — project-pinned, current, cited, official/licensed code intelligence
   and deterministic compliance findings with edition-impact review.
2. Milestone 23 — opt-in official manufacturer evidence, data sheets, properties, and
   quarantined reviewed CAD assets.
3. Milestone 24 — reviewed read-first ETAP connector and result reconciliation through
   the shared model.
4. Milestone 25 — reviewed read-first SOLIDWORKS Electrical connector for schematics,
   components, wires, terminals, cross-references, and bills of materials.
5. Milestone 26 — real-project acceptance, performance, privacy/security, recovery,
    multi-PC validation, documentation freeze, and professional `1.0.0` release.
