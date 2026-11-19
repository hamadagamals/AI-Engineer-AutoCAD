# Roadmap

## 2026-11-01 - School V5 loaded-family inventory and reviewed power-mark reader

A live read-only inventory of the corrected sample found eight loaded lighting
types, two outlet types, no switch types and no panel types. Eight Autodesk stock
families were inspected without loading; the 220 V receptacle has a connector,
while sampled stock panel/lighting/switch types need voltage and project-suitability
review. A read-only V5 power-mark extractor and room-query fix compile, and three
power-page/proximity tests pass. A fresh Revit session is required for live
acceptance of these two bridge changes. No RVT model change resulted from this work.

## 2026-11-01 - School V5 CLASSROOM 1501 Mark correction verified

After the operator approved preview `141A51C0...933A1820`, a bounded Revit
transaction corrected the eight physical fixture Marks and saved a new RVT at
`DABF745E...F80544F`. Post-save Revit verification and a separate read-only
category query found IDs `457165`–`457172` with CLASSROOM 1501 Marks `01..08`.
The source and verified backup stayed at `66405403...8EA8E3AB`. Visual,
ceiling-host, close-and-reopen, electrical-rating and circuit reviews remain.

## 2026-10-31 - School V5 CLASSROOM 1501 physical sample verified

The operator approved exact preview `5B5C9F09...906F4DC8`. Revit created eight
Level 1 L1 fixtures in a new sample RVT and post-save API verification matched all
eight positions and type. The output is `66405403...8EA8E3AB`; original V5 and
backup are both `8D533212...FAE1B8`. Read-only circuit inspection found one
unassigned PowerCircuit connector per new fixture, 61 physical lights total.
Revit auto-numbered the new Marks under CLASSROOM 1500 despite CLASSROOM 1501
coordinates. Mark/room correction, ceiling mounting, visual approval, ratings and
photometry are now the next gates before circuit creation or wider placement.

## 2026-10-30 - School V4 spatial review completed, quantity basis open

The operator's marked V3 gaps were traced to selecting 45 gym lights from a 48-cell grid.
V4 uses balanced room rows and sampled nearest-light coverage. Live request
`952b0774-c7c5-42a4-ad09-956e5462f8e1` saved a new RVT at reported hash
`104619CD...84D645`; the source remains `2F4E3543...73F582`. Across 108 rooms, Revit reported
1,152 lighting points, 525 new power points, 16 observed outlets, two panel marks, and zero
duplicate, outside-room, door, stair, or shaft hits. The gym is now five rows of nine with a
2,954.1 mm maximum sampled nearest-light distance. User visual inspection remains open.
The new actual-area audit found eight rooms with area ratios above 1.5 and an indicative
area-only count of 945 lights instead of 1,152. Resolve counts and photometry before physical
fixture placement or code-compliance claims.

## 2026-10-30 - School electrical review V3 ready for visual acceptance

Live Revit request `c8afe3ff-0399-4f04-b9ad-16a692309145` saved
`School-Electrical-Review-V3-20261030.rvt` at reported hash `B90A7489...43DF83` from
unchanged source `2F4E3543...73F582`. Six level-specific Electrical review views carry
1,152 lighting points of six symbol types, 525 new power point candidates, 16 observed
existing outlets, and two panel-zone marks across 108 rooms. The Revit API report has zero
exact duplicate, outside-room, door-clearance, or detected stair/shaft hits. The operator's
subsequent screenshot found isolated unlit cells, including three in the gym where the algorithm
selected 45 of 48 regular grid candidates. V3 is rejected for layout acceptance despite its
count and containment checks. A balanced V4 preview is compiled and awaits live Revit execution
and room-by-room visual acceptance; then prepare a separate approved physical
family/host preview. Circuiting, routing, protection, and calculations remain open.

## 2026-10-30 - Corrected Revit room-boundary lighting review V2

Implemented and live-ran a second bounded Revit Save-As path after the operator rejected the V1
bounding-box layout. Preview `81A30E22...D651FF5` produced
`School-Electrical-Lighting-Review-V2-20261030.rvt` at `DE509ED5...FBA08`, with 1,152 lighting
review points for all 108 scheduled rooms and 4,608 detail curves across Level 1, Level 2, and the
level-check view. Exact room containment and detected stair/landing/shaft exclusions passed with
zero duplicates, outside-room points, or obstacle hits. Both stair areas were visually inspected in
Revit and showed no marks on flights or landings. The file is open for operator visual review; it is
not approval to place real families or proceed to circuiting, power, panels, or calculations.

## 2026-10-30 - Live Revit distribution review overlay

Implemented and live-ran one project-specific Revit Save-As review path. This V1 lighting layout was
later rejected by the operator and is retained only as superseded diagnostic evidence. Preview
`8297D93F...F0B92` produced `School-Electrical-Full-Distribution-Review-20261030.rvt` with three
new plan views, 1,400 review points, and 6,852 colored view-specific detail curves. Revit opened the
Level 1 and Level 2 review views for direct operator inspection. The source remained the existing
53-fixture scratch sample at SHA-256 `2F4E3543...73F582`; the new output is independently hashed at
`E420A853...F47D2E`. The action is bounded to 1,500 points, refuses overwrite, validates source and
preview hashes, and writes no real MEP devices or circuits. The remaining 301 lighting units without
usable XY geometry stay held rather than being invented into corridors, kitchen/service spaces, or
the unclassified level.

## 2026-10-29 - Whole-school distribution visual review preview

Created `workspace/revit-school/previews/full-school-distribution-review-20261029/` plus
`workspace/revit-school/outputs/School-Electrical-Distribution-Review-2026-10-29.zip`. The three
overview plans show 108 room bounding rectangles, 851 candidate/verified lighting XY points, 301
unresolved lighting units, 426 new duplex points after crediting 12 mapped existing duplex outlets,
21 new GFI points (four existing GFI points are shown but belong to rooms outside the target list),
and 78 generic dedicated equipment points. Green rings distinguish the 45 gym plus eight classroom
fixtures verified in a separate Revit scratch file. MDB-L1/SMDB-L2 candidate spaces use named model
rooms. Six branch lighting/power zone pairs appear only as centroid guides. Room and point CSVs
contain the full schedule and coordinates. A separate sheet flags the unclassified level containing
`CORRIDOR 4002`. The plan is a review diagram based on bounding boxes. It is not exact wall-face
placement or code approval, and no RVT was changed.

## 2026-10-29 - School load calculation intake

Created `workspace/revit-school/outputs/School-Electrical-Loads-and-Cable-Inputs.xlsx` from the
read-only school inventory and existing preliminary lighting program. It contains room-level
lighting/receptacle counts, per-luminaire-type totals, 78 equipment load input rows, and conceptual
MDB/SMDB and zone schedules. Editable green cells collect absent rating, PF, demand, route, and
cable property inputs. Equipment demand VA, current, and conditional voltage drop recalculate from
those values. It marks power demand incomplete until required inputs are present. This is a review
calculation workbook only; no Revit distribution beyond the previously verified 53-fixture sample
was changed or claimed.

## 2026-10-24 - Classroom 1500 sample handoff

The exact approved eight-fixture L1 sample was saved in a new RVT with SHA-256
`2F4E35430E635D47ACF8931D9473246D692AAF73B4450F8BAD2744138F73F582`.
Independent reopened verification confirmed the eight placements, parameters, connectors and
45 preserved L4 fixtures without mutation. Execution and verification reports are retained in
`workspace/revit-school/scratch`. The eight fixtures were subsequently made visible together
in the existing `01 RCP - MEP COORDINATION` reflected ceiling plan, with Revit reporting
`Lighting Fixtures (8)`. Engineer visual acceptance remains open; automatic image export still
captures the wrong 3D view.
No wider classroom batch, circuiting, or Egyptian-code compliance has been approved.

## Milestone 0 — Audit and Foundation (complete)

Implemented and verified:

- version metadata and project-root-relative typed configuration;
- project-specific configuration, persistence, and lifecycle exceptions;
- UTF-8 structured JSON-lines logging;
- atomic JSON state persistence;
- explicit application start/stop lifecycle and console entry point;
- valid versioned settings, mission, state, and history data;
- baseline unit tests and operator documentation.

## Milestone 1 — Safe Core (complete)

Implement a sandboxed `FileManager`, bounded `TerminalManager`, `ProjectScanner`,
`ProcessManager`, `SystemInfo`, and safe `GitManager`. Include canonical workspace
containment, traversal and symlink escape tests, command policies, subprocess
timeouts, output capture, backups where appropriate, and actionable failures.

Completed:

- sandboxed UTF-8 `FileManager` with configurable workspace roots;
- canonical traversal and Windows junction/symlink escape protection;
- bounded text reads and explicit regular-file checks;
- atomic file creation and overwrite;
- mandatory confirmation plus atomic timestamped backup before overwrite/delete;
- structured success, denial, backup, and failure audit events;
- Windows-focused and portable unit tests;
- bounded, read-only `ProjectScanner` built on the same workspace policy;
- deterministic metadata results without opening file contents;
- ignored dependency/cache/control directories and skipped link/junction traversal;
- limits for files, directories, per-directory entries, depth, and cumulative bytes;
- project-marker discovery, extension summaries, classifications, and explicit
  truncation/skipped-path reporting;
- fail-closed `TerminalManager` with explicit prefixes, filtered environment,
  workspace validation, timeouts, bounded output, and process-tree cleanup;
- identity-safe `SystemInfo` and bounded process inspection;
- confirmation-gated graceful process termination with PID-reuse protection;
- read-only, workspace-confined Git status, diff, and commit-log inspection;
- complete Safe Core compile, entry-point, and test verification.

## Milestone 2 — Command and Task Engine (complete)

Add typed command registration, durable task states and history, cancellation,
bounded retries, and explicit approvals.

Implemented and verified:

- immutable typed command definitions and duplicate-safe registry;
- bounded JSON payload validation and result serialization;
- exact capability and explicit namespace permission policy;
- durable expiring approvals bound to one task/command and consumed once;
- atomic task/event history and validated lifecycle transitions;
- immediate queued cancellation and cooperative running cancellation;
- explicit retryable failures with a maximum of three attempts;
- safe handling of unexpected errors without durable/log detail leakage;
- ten registered Safe Core commands with approval gates for mutations;
- application lifecycle initialization and integration tests.

## Milestone 3 — Local API (complete)

Add validated FastAPI models and authenticated routes bound to `127.0.0.1` for
health, status, safe file/command/scan operations, and tasks.

Implemented and verified:

- exact `127.0.0.1` single-worker Uvicorn configuration;
- strong environment-only Bearer token with constant-time comparison;
- authentication before body parsing plus protected-router dependency;
- trusted Host validation, disabled proxy trust, access logs, docs, and OpenAPI;
- streamed request byte limits and conservative response security headers;
- strict request models without coercion, extra fields, or input echo;
- status, command, task, cancellation, approval, file, and scan routes;
- task responses that omit submitted payloads and preserve approval separation;
- safe exception-to-status mapping and generic unexpected-error responses;
- lifecycle, middleware, configuration, security, and end-to-end approval tests.

## Milestone 4 — Developer Agent (complete)

Implemented and verified:

- callback-based provider abstraction with bounded responses and redacted failures;
- strict phase-specific JSON for plans, actions, verification, and completion;
- durable atomic developer runs and metadata-only audit events;
- mandatory human review of every plan and exact command payload;
- execution exclusively through the registered command and task engines;
- independent durable approval retention for high/destructive commands;
- bounded goals, plan fields, actions, provider calls, history, and observations;
- fail-closed unknown commands, malformed output, premature completion, and limits;
- protected provider-status and developer-run API routes;
- unit and API integration tests for full reviewed workflows and failure modes.

## Milestone 5 — VS Code Integration (complete)

Implemented and verified:

- supported `code` CLI discovery with bounded non-secret version metadata;
- Windows wrapper validation and direct `Code.exe`/`cli.js` execution without shell;
- workspace-confined existing file/directory targets and bounded line navigation;
- filtered child environment, no stdin, captured output, and hard CLI timeout;
- mandatory direct confirmation and durable high-risk command approval;
- `vscode.detect` and `vscode.open` command-engine registration;
- availability through the authenticated generic tasks API and developer loop;
- fail-closed wrapper, metadata, path, navigation, timeout, and exit handling tests.

## Milestone 6 — AutoCAD 2021 Integration (complete)

Implemented and verified in automated tests:

- R24.0/AutoCAD 2021 registry and executable validation without sensitive metadata;
- exact `AutoCAD.Application.24`, Core Console, and secure AutoLISP capability checks;
- durable-approval-gated inspection of atomic bounded scratch drawing copies;
- fixed metadata-only script with no provider/caller command injection;
- `/readonly`, filtered environment, no stdin, timeout, bounded output, and process-
  tree cleanup;
- strict success markers and retained bounded diagnostics without false success;
- workspace scan exclusion for generated `.ai-engineer` scratch trees;
- detection, path, size, copy, command, marker, failure, and approval tests.

Live verification completed on 2026-08-06 against a copied Autodesk `acad.dwt` under
the initialized interactive Windows user. Core Console returned version `24.0`, the
five expected metadata values, `DBMOD=0`, and no truncated output. Service/sandbox
accounts remain intentionally unable to reuse another user's AutoCAD profile.

## Milestone 7 — Packaging (complete)

Implemented and verified:

- fixed-command Python and Windows launchers with no arbitrary shell surface;
- clean portable ZIP generation from an explicit source allowlist;
- generated runtime defaults that exclude local history, approvals, run state,
  logs, backups, workspace files, caches, tests, Git data, and API tokens;
- deterministic SHA-256 payload manifest with bounded source/archive size and count;
- confirmed installation into a new destination after traversal, link, duplicate,
  manifest, and content-hash validation;
- allowlisted runtime JSON recovery snapshots with secret-like key rejection;
- confirmed restore with pre-restore backup, atomic writes, and rollback;
- confirmed idempotent schema-v1 layout migration with pre-change snapshot and
  rollback;
- multi-computer setup, transfer, integrity, privacy, recovery, and limitation
  documentation;
- unit and real build/install/check verification.

## Milestone 8 — Bounded AutoCAD inventory pilot (complete)

Implemented and verified:

- independent durable-approval-gated `autocad.inventory_scratch` command;
- the existing workspace containment, atomic scratch copy, `/readonly`, timeout,
  filtered environment, bounded output, and process-tree boundaries;
- fixed `tblnext` layer-table and `ssget "_X"` database-entity aggregate counting;
- no layer names, entity types, handles, text, attributes, coordinates, or geometry in
  results;
- configured count ceilings, strict complete-line markers, and mandatory `DBMOD=0`;
- SHA-256 comparison proving both source and scratch bytes remained unchanged;
- parser, mutation, confirmation, command-catalog, API-count, and integration tests;
- live AutoCAD 2021 verification returning one layer and zero entities from the empty
  Autodesk template with unchanged hashes and no truncated output.

## Milestone 9 — Temporary drawing writer (complete)

Implemented and verified:

- independent durable-approval-gated `autocad.create_line_scratch` command;
- strict source path plus two distinct finite 3D points bounded to ±1e9;
- numeric-only rendering into a fixed script with no caller command, layer, output
  path, save format, or arbitrary AutoLISP content;
- exactly one `LINE` operation and one `QSAVE` against the already named scratch copy;
- mandatory one-entity count increase, created DXF type `LINE`, and post-save
  `DBMOD=0`;
- unchanged source SHA-256 and changed output SHA-256 before success;
- validator, approval, script, parser, hash-state, entity-count, and API catalog tests;
- live AutoCAD 2021 verification creating a `(0,0,0)` to `(1000,0,0)` line in the
  scratch DWT while preserving the input.

## Milestone 10 — Electrical drawing primitive (complete)

Implemented and verified:

- independent durable-approval-gated `autocad.create_wire_route_scratch` command;
- 2–16 finite bounded 3D points, normalized to nine decimal places, with distinct
  adjacent points;
- fixed `E-WIRE` layer, ACI color 3, and `CONTINUOUS` linetype with no caller-supplied
  layer or script text;
- NCS/AIA-inspired discipline/major-group naming, explicitly not full NCS compliance;
- one verified `LINE` per adjacent point pair, limited to 1–15 segments;
- exact count growth, type/layer membership, clean save, and source/output hashes;
- unit, command-engine, API-catalog, and live AutoCAD 2021 verification;
- live two-segment route with entity count zero to two and unchanged source bytes.

## Milestone 11 — Electrical symbol/block foundation (complete)

Implemented and verified:

- bounded UTF-8 JSON symbol catalog with schema/catalog/symbol versions;
- canonical per-definition SHA-256 approval and fail-closed edit detection;
- strict identifiers, names, units, layers, ACI colors, linetypes, base points,
  attributes, and allowlisted line/circle geometry;
- low-risk `autocad.list_electrical_symbols` metadata command;
- durable-approval-gated `autocad.place_electrical_symbol_scratch` bound to the exact
  definition hash, insertion point, rotation, and uniform scale;
- fixed `socket_single` starter block with three entities, no attributes, and
  `E-POWR-DEVC` insertion-layer ownership;
- refusal to redefine any same-named block already present in the scratch source;
- exact definition, geometry count, `INSERT`, block, layer, transform, zero-attribute,
  entity-growth, clean-save, and source/output hash verification;
- catalog, tamper, parser, script, payload, approval, API-count, and integration tests;
- live AutoCAD 2021 placement at `(2000,1000,0)`, entity count zero to one, clean save,
  unchanged source, and changed scratch output.

## Milestone 12 — User block intake and validation (complete)

Implemented and verified:

- catalog millimeter contract enforced against drawing `INSUNITS=4` before placement;
- durable-approval-gated `autocad.review_electrical_symbol_source` command;
- workspace-confined atomic scratch copy, `/readonly`, no save, unchanged source and
  scratch hashes, bounded output, timeout, and strict result markers;
- exact named block lookup with flags and base-point inspection;
- bounded traversal of at most 32 block-definition entities;
- lossless proposal extraction for supported `LINE` and `CIRCLE` geometry;
- counts for geometry, layer 0 membership, `ATTDEF`, nested `INSERT`, unsupported
  types, and truncation;
- explicit eligibility reasons for missing/renamed/flagged/non-mm/empty/oversized,
  unsupported, non-layer-0, attributed, or nested definitions;
- complete proposed catalog entry and canonical SHA-256 only when all checks pass;
- no catalog write, block import/redefinition, source mutation, or caller script text;
- unit, parser, payload, approval, immutable-hash, and integration tests;
- live eligible review of a three-entity block in an Autodesk millimeter DWT with
  `DBMOD=0`, unchanged review hashes, and no truncated output.

## Milestone 13 — Approved catalog registration (complete)

Implemented and verified:

- every completed review persists bounded schema-v1 evidence atomically inside its
  isolated run directory;
- independent durable-approval-gated `autocad.register_electrical_symbol_review`;
- registration payload contains only review run ID, proposal SHA-256, and source
  SHA-256 rather than caller-provided catalog geometry;
- evidence identity, eligibility, exact proposal hash, workspace source path, and
  current source hash are revalidated before mutation;
- duplicate symbol IDs and block names fail before backup or catalog mutation;
- timestamped UUID catalog backup with byte-for-byte original content;
- staged complete-catalog validation, concurrent-change check, atomic replacement,
  post-write reload, and automatic original-byte rollback on validation failure;
- project-specific errors and metadata-only structured logging;
- confirmation, evidence/hash binding, changed/duplicate source, backup, version,
  atomic success, and rollback tests against temporary catalogs;
- portable package includes the registration implementation without runtime evidence,
  workspace sources, or local backups.

## Milestone 14 — Verified symbol reuse and circuit fragment (complete)

Implemented and verified:

- existing catalog block definitions are checked for flags, base point, exact ordered
  geometry, layer 0, geometry count, and clean block termination before reuse;
- single-symbol placement now permits a verified existing definition instead of
  rejecting every name collision or redefining it silently;
- durable-approval-gated `autocad.create_electrical_circuit_fragment_scratch`;
- one hash-approved symbol definition, 2–8 bounded transforms, and one connected
  1–15-segment wire route per transaction;
- millimeter, insertion-layer, wire-layer, block, reference transform, route endpoint,
  aggregate count, save-state, source hash, and output hash verification;
- one scratch save only after every expected entity validates; source is never saved;
- payload, approval, bounds, parser-conflict, existing-block reuse, exact count, and
  isolated source/output hash tests;
- live AutoCAD 2021 reuse of `AIENG_E_SOCKET_SINGLE_V1`: three references plus two
  `E-WIRE` segments increased model entity count from one to six with `DBMOD=0`;
- live clean-template creation of the same approved definition: two references plus
  one wire segment increased model entity count from zero to three with `DBMOD=0`;
- independent read-only live inventory confirmed six entities, three layers, unchanged
  scratch hash, and `DBMOD=0`.

## Milestone 15 — Verified drawing handoff (complete)

Implemented and verified:

- atomic schema-v1 evidence for every successful writable producer, bound to its run
  ID, exact source/output paths, suffix, byte count, and source/output SHA-256;
- `autocad.handoff_verified_output_new`, accepting only run ID, output digest, and a
  same-suffix workspace destination, with exclusive no-overwrite creation;
- distinct `autocad.replace_with_verified_output` capability and durable approval,
  additionally bound to the existing destination SHA-256;
- timestamped byte-identical backups outside the workspace, concurrent-change checks,
  atomic replacement, post-write verification, and automatic rollback;
- rejection of arbitrary source paths, internal run destinations, producer source or
  scratch destinations, traversal, suffix/digest/run mismatches, and link escapes;
- confirmation, evidence schema, containment, race, backup, success, rollback,
  metadata-only logging, strict payload, and separate-approval tests;
- live AutoCAD 2021 creation and new-file handoff to
  `workspace/autocad-live/ai-engineer-beta-0.16.0.dwt`;
- independent live inspection and inventory: 32,725 bytes, SHA-256
  `11fddeb136d129b2f6c38312c9780e2080519bb393edcf03e9528bf9fb1c4682`, three
  entities, three layers, `DBMOD=0`, and unchanged template/producer bytes.

## Milestone 16 — Electrical project model and drawing context (complete)

- immutable schema-v1 projects, drawing layers, sources, devices, terminals, panels,
  circuits, protection, cables, conductors, loads, routes, units, provenance,
  confidence, privacy, and explicit unknown classifications;
- strict AutoCAD context records for the active drawing or engineer's explicit
  selection, with allowlisted types, stable handles/document identity, layouts,
  blocks, attributes, geometry, units, truncation, and independent resource limits;
- cyclic, nested, oversized, non-finite, duplicate, ambiguous, unsupported, malformed,
  out-of-workspace, hash-mismatched, or egress-enabled evidence fails closed;
- conservative catalog-aware mapping recognizes reviewed devices and `E-WIRE` routes
  while retaining source identity and reporting unknown meaning rather than guessing;
- a buildable x64 AutoCAD 2021 .NET bridge uses only `OpenMode.ForRead`, fixed commands,
  atomic workspace evidence, explicit selection, and no drawing/provider write path;
- a high-risk local import command requires durable approval plus an exact context ID
  and SHA-256 and reports that provider egress was not performed;
- live read-only AutoCAD 2021 verification exported both active-drawing and explicit-
  selection context from the controlled 32,725-byte template: each contained three
  entities mapped to two reviewed devices and one route, `DBMOD=0`, no unknowns, and
  `provider_egress_performed=false`;
- the controlled drawing SHA-256 remained
  `11fddeb136d129b2f6c38312c9780e2080519bb393edcf03e9528bf9fb1c4682` before and
  after both exports.

## Milestone 17 — Read-only AutoCAD copilot palette

Build the first in-product Arabic/English palette through an official supported
AutoCAD extension boundary and the authenticated loopback API. Show selected context,
conversation, plan review, missing information, progress, cancellation, and results.
Add provider-independent sessions and an explicit preview/approval policy for any
cloud data egress; default operation remains local and read-only.

Completed foundation:

- immutable provider-independent schema-v1 session, exact context binding, and
  Arabic/English message contracts with chronological, count, UTF-8 byte, identity,
  expiry, and lifecycle validation;
- an invariant `drawing_write_allowed=false` boundary for every Milestone 17 session;
- exact privacy-classified egress preview fields containing the reviewed value, UTF-8
  byte count, value SHA-256, aggregate preview SHA-256, destination, and expiry;
- pending/approved/rejected review decisions are immutable and do not claim or perform
  transmission; whole-drawing egress is prohibited by the contract.
- authenticated loopback session, message, cancellation, result, egress-preview, and
  egress-decision routes validate the exact local context evidence before session
  creation and require an independent high-entropy ownership key thereafter;
- the ownership key is returned only at creation and retained by the service only as a
  SHA-256, while expiry, active-session concurrency, per-session operation rate, API
  payload, and message/history bounds fail closed;
- structured logs contain session/review identities, counts, language, byte sizes, and
  decisions but never ownership keys or message/preview values.
- the AutoCAD 2021 x64 bridge now includes a dockable `AIENGPALETTE` PaletteSet with
  Arabic/English labels, last explicit-context status, device/route/unknown/missing/
  warning counts, bounded transcript/input, progress state, local session start,
  owner-key-protected message submission, and cancellation;
- the palette connects only to fixed `http://127.0.0.1:<validated-port>`, disables
  proxies, reads the API token only from the AutoCAD process environment, bounds API
  responses, and contains no drawing write, arbitrary command, process launch, or
  provider call.
- an injected copilot-provider adapter accepts only the exact fields from one approved,
  unexpired, unused egress review; it has bounded concurrency/output/timeout and parses
  exact JSON containing only language, answer text, and citations;
- provider output cannot contain commands, change language, cite a field outside the
  approved preview, write a drawing, or reuse an egress decision; validated answers
  retain provider/review/preview provenance and explicit egress/write evidence;
- the palette keeps local message submission separate from egress, displays exact
  field values, byte counts, and preview SHA-256, and requires a second explicit
  approve-and-ask or reject action before the provider endpoint can run;
- localization, timeout, malformed output, review gating, single use, ownership,
  palette source contract, API, and portable-package coverage are implemented.

Live AutoCAD 2021 acceptance completed on 2026-08-06. The palette command completed
against an explicit three-object selection, exported three entities on two layers,
reported `DBMOD=0`, retained `provider_egress_allowed=false`, kept `SECURELOAD=1`, and
restored `TRUSTEDPATHS`. Interactive AutoCAD rewrote writable controlled files even
with `DBMOD=0`, so the successful safety acceptance used a separate OS-read-only DWG;
its SHA-256 remained exactly
`ac3a5ad27cbcacfa6ad2bb4970200d19aecfab59fbc239f006ab426033a33c4e`.
The original Milestone 16 source was restored byte-for-byte to its documented hash,
and the unexpected writable result was retained in `backup` for diagnosis. Version
`0.18.0` completes Milestone 17. Sessions remain process-local and intentionally
expire or disappear on API restart.

## Milestone 18 — Previewed heterogeneous electrical drafting

Move the existing heterogeneous circuit goal here. Model multiple independently
approved devices, terminals, circuits, and routes; generate a visible change preview;
then execute only through supported undoable transactions or verified scratch handoff.
Reject unknown endpoints, ambiguous topology, block conflicts, arbitrary script text,
and unreviewed catalog definitions.

Completed foundation:

- immutable schema-v1 typed proposal records cover multiple devices, terminals,
  circuits, routes with exact endpoint identities, exact layer schemas, and explicit
  missing, unknown, or ambiguous input issues;
- each proposal is bound to the exact project, document, drawing, context evidence
  SHA-256, canonical shared-model SHA-256, reviewed symbol-catalog SHA-256, and unit
  system, and its full content carries a verified canonical SHA-256;
- immutable preview snapshots enumerate every proposed object in exact order, remain
  inside the proposal lifetime, carry their own canonical SHA-256, and cannot claim a
  drawing mutation or grant execution approval;
- contract tests cover heterogeneous serialization, nested immutability, context/model
  evidence mismatch, content tampering, exact preview coverage, mutable collection
  rejection, and bounded preview lifetime.
- deterministic validation rechecks the exact source-model and catalog snapshots,
  cross-kind and source identity collisions, supported physical units, reviewed symbol
  definition/block/attribute hashes, existing/new layer schemas and layer state;
- bounded geometry validation covers insertion and terminal positions, normalized
  rotation, positive scale, route point/length bounds, zero-length segments, and exact
  route-to-terminal position matching with a fixed physical tolerance;
- topology validation requires every route and endpoint to resolve exactly, prevents a
  terminal from joining multiple circuits, and accepts only one connected acyclic
  terminal tree per circuit while unresolved inputs fail closed;
- immutable self-hashed validation results are deterministic for the same proposal,
  model, and catalog and permit preview only when their ordered findings are empty.
- an atomic durable drafting lifecycle persists only exact proposal, validation,
  model, and catalog hashes plus status and metadata-only audit events; owner keys are
  returned once and retained only as SHA-256;
- validated and blocked proposals survive application restart with bounded expiry,
  ownership isolation, cancellation, separate pending review, approve/reject decisions,
  terminal-state replay rejection, and an invariant no-drawing-mutation record.
- strict bounded JSON parsing reconstructs every proposal, binding, layer, device,
  terminal, circuit, endpoint-bound route, and input issue without coercion; extra
  fields, non-JSON values, cycles, excessive depth/nodes, and content-hash tampering
  fail before local context access;
- trusted planning orchestration ignores any provider-supplied model or catalog,
  re-imports the exact local context ID and evidence SHA-256 read-only, reloads the
  reviewed local symbol catalog, re-runs deterministic validation, and persists only
  the resulting lifecycle hashes and metadata.
- authenticated loopback drafting routes create, retrieve, cancel, request preview
  review, and decide that review using a separate one-time owner key header; every
  post-creation operation is rate-limited per run;
- review decisions are durable, proposal-lifetime bounded, terminal, and replay-safe,
  while API responses and lifecycle records retain an explicit false drawing-mutation
  invariant and expose no drawing-write operation.
- a new authenticated transient-preview route re-imports the exact local AutoCAD context,
  reloads the reviewed catalog, and deterministically revalidates the complete proposal;
  it returns the exact ordered preview objects/geometry but persists only preview ID,
  SHA-256, and expiry in the lifecycle;
- lifecycle review is impossible before that exact preview is bound. Preview ownership,
  run/proposal/model/catalog hashes, lifetime, restart compatibility, replay, and expiry
  fail closed, while preview records hard-code both drawing mutation and execution approval
  to false.

Lifecycle restart/redaction/authentication/package hardening and the transient preview
implementation are complete in automated coverage. Live AutoCAD 2021 palette preview
acceptance passed on 2026-08-10 against an explicit four-entity metric selection in a
separate OS-read-only DWT. The palette displayed the exact ordered layer, two devices,
two terminals, circuit, and route geometry under drafting run
`ac93826c27ec438ebba0b1f68205bc7a`; preview SHA-256 was
`e5f47cc19fe894998313b62b4363629d4a422552994c56da56be889b93e432b0`.
The lifecycle remained `preview_ready` with no review or decision, both mutation and
execution-approval flags were false, and the read-only drawing stayed byte-identical at
SHA-256 `54d4ff5fa776e45612a939b26b6adfab1a5c556cc75eb9ad6cdea7803cca17c5`.
`DBMOD=16` represented a view-only change under Autodesk's documented bitcode; object,
database-variable, and field-change bits remained clear.

A bounded execution vertical slice is now implemented behind a new, separate review.
The review hash covers the exact preview, validated proposal, source path/SHA-256,
catalog symbol, placements, and route geometry. Approval is one-use; execution creates
only a Core Console scratch output, verifies the original source stayed unchanged, and
persists timestamped source/output/recovery hashes. `executing`, `executed`, and
`recovery_required` states prevent replay after either success or an uncertain failure.
The AutoCAD palette exposes distinct review, approve/reject, and execute controls with
two explicit confirmations and no source replacement or handoff control. Automated API,
lifecycle, integration, UI-contract, compile, and full-suite checks passed before live
acceptance.

Milestone 18 live execution acceptance passed on 2026-08-10. Drafting run
`75d42932bef548de949f2557796c82e3` consumed execution request SHA-256
`e771ac78681188d4858186d945eea4251f35a289f16734879d05dcd1880aac1d`
once and completed as execution `ef69772c667b4270bc97ce914deeb5e2` / AutoCAD run
`ecd874b4477f4e2f80b49b8d4b4f1980`. AutoCAD created exactly two reviewed block
references and two route segments in the scratch copy, increasing its entities from
four to eight with `DBMOD=0`. The read-only source remained byte-identical at SHA-256
`54d4ff5fa776e45612a939b26b6adfab1a5c556cc75eb9ad6cdea7803cca17c5`;
the output SHA-256 is
`4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`,
and the independently verified recovery-evidence SHA-256 is
`1d6292f2532539cb928484a7e4f72ebde46137e2340ea596a246d4d12c7f21e7`.
No handoff or source replacement occurred, and later handoff remains separately
approved. Automated failure/recovery-required and handoff rollback coverage plus the
347-test full suite pass. Milestone 18 is complete within its documented one-circuit,
single-symbol-type scratch-execution feature boundary.

## Milestone 19 — Traceable quantity takeoff

Measure and classify cable, wire, conduit, tray, devices, and components from the
shared model. Separate geometric length, vertical allowance, termination allowance,
waste, and user overrides. Link every quantity to drawing objects and export reviewed
CSV/Excel reports without silently inferring missing routes.

The first implementation slice is complete: immutable self-hashed `m19.takeoff.v1`
reports bind exact context evidence and shared-model hashes, count recognized devices by
reviewed symbol/block identity, and calculate source-linked 3D route lengths in meters.
Vertical and per-end termination allowances, waste, and reasoned reviewer overrides stay
separate and auditable. Unitless/unknown drawings, zero-length route segments, changed
evidence, and overrides for unknown routes fail closed; unclassified source objects mark
the report incomplete instead of being guessed. The authenticated read-only API re-imports
the exact local context and reports `drawing_mutation_performed=false`. Reviewed CSV export
and live AutoCAD 2021 acceptance remain before milestone completion.

Milestone 19 live acceptance completed on AutoCAD 2021 on 2026-08-10. Exact selection
context `27a16daaa78e42d183ab138c33766af8` contained eight metric objects with no unknowns
and evidence SHA-256
`da11275536e790b285871f9438af4aefbb50d45d53d120a4d4dbb2be7f45b454`.
Report SHA-256
`b6417994dfc1cb9a4598cd9ad93856b531001d5d089b8a6b26f7281b16d434e9`
returned four reviewed `socket_single` devices and four separately source-linked 0.5 m
routes with `is_complete=true` and no drawing mutation. The separately confirmed CSV
matched that exact report and produced content SHA-256
`c21dfc0961dc0af9342288c964644c5195591b0f291a94e3e487b5c4e15db8eb`.
Every row retained document, object, context, evidence, report, and calculation identities;
no file write occurred. The OS-read-only drawing copy remained byte-identical to its source
at SHA-256 `4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`.
Milestone 19 is complete within its current recognized-device and explicit-route boundary.

## Milestone 20 — Deterministic low-voltage calculations

Add versioned, unit-tested connected/demand load, diversity, phase balance, current
capacity, derating, cable sizing, voltage drop, protective-device, and earth-conductor
engines. Results must reproduce without AI and expose formulas, inputs, units,
assumptions, rule-set versions, warnings, and source-object links.

The first Milestone 20 slice passed live AutoCAD 2021 acceptance on 2026-08-10.
`m20.lv_current_voltage_drop.v1` implements pure balanced single-phase and balanced
three-phase load current plus approximate steady-state voltage drop. Inputs explicitly
separate output power, demand factor, voltage basis, power factor, efficiency, reviewed
conductor AC resistance/reactance, reviewer, and assumptions. Route length is derived from
exact source-linked model geometry; unknown units, missing/unsourced load objects, absent
routes, zero-length segments, and invalid factors fail closed. Results retain exact inputs,
formula identities/expressions, source references, limitations, model/context hashes, and
a self-hash, while explicitly denying compliance evaluation and drawing mutation.

Live context `551f8b86f9184a62adb44a1eaee75253` contained eight metric objects with DBMOD=0
and evidence SHA-256
`3b80c4e4806cab04347b3e5ee8bab672ed2bbc286de4e3c6bbdf477bcc08b13d`.
Using four devices, four routes totaling 2 m, 1000 W output, demand factor 1, 230 V,
power factor 0.9, efficiency 0.95, R=7.41 ohm/km, and X=0.08 ohm/km produced
5.085177 A and 0.136361 V / 0.059288% drop. Result SHA-256 was
`582af6bc273247372539eecdbd334f4beeeb4f20848dc426025f783a93ea070e`.
The read-only drawing stayed byte-identical to its source at SHA-256
`4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`.
The next implemented slice, `m20.load_aggregation.v1`, adds a typed reviewed project
rule set with one connected power, demand factor, and L1/L2/L3/three-phase assignment
per source-backed load. Every load must belong to exactly one explicit diversity group.
It returns connected load, demand before and after diversity, per-group results, ordered
phase totals, average, maximum spread, and maximum percentage deviation with exact
source/model/context hashes. It deliberately contains no hidden diversity defaults,
balance acceptance threshold, code limit, compliance conclusion, or drawing mutation.
Golden/rejection, authenticated API, package, and AutoCAD palette tests pass. Live
AutoCAD 2021 acceptance used context `19940de5708f4d5cb10e4135c0568184`, eight selected
metric entities, DBMOD=0, and evidence SHA-256
`c46d58b59a3ec5f9345b0da8a36b299f365e48617fcca73a6a691c8fecbc9c56`.
Four 1000 W loads with demand/diversity 1 and allocations L1/L2/L3/three-phase produced
4000 W before/after diversity and exactly 1333.333333 W per phase, with zero spread and
deviation. Result SHA-256 was
`6fd06995638c3f52ecdb440dd2684d2b6692a466c73e9d069b4fbc29856e60e0`.
The source and OS-read-only copy remained byte-identical at SHA-256
`4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`.

This does not yet implement cable or protection selection, earthing, short circuit, or
standards compliance.

The implemented `m20.cable_ampacity_derating.v1` slice introduces a route-linked reviewed
cable designation, base ampacity and basis reference, plus one through sixteen ordered,
uniquely identified derating factors with labels and basis references. Each stage retains
its incoming ampacity, factor, and outgoing ampacity; the final result retains the combined
factor, exact route/model/context evidence, formula, warnings, and self-hash. Factors are
strictly greater than zero and no greater than one, so this derating chain cannot silently
increase the reviewed base value. Golden/rejection, authenticated API, package, AutoCAD
palette, DLL build, and full `385 passed, 1 skipped` automation are complete. Live AutoCAD
2021 acceptance used context `0b3d9bbe18514da7a854eb3213a1555a`, eight selected metric
entities, view/window-only DBMOD=16, and evidence SHA-256
`8bc0db0144dc5cf4c5f11564500e42d8e4a09262bdc172c4031905dbbf382fac`.
A reviewed 20 A basis with ambient 0.94 and grouping 0.8 produced ordered stages 18.8 A
then 15.04 A, combined factor 0.752, and result SHA-256
`53c66553f7c2af7c4760725fc9763f1d5851c84a809b52227e0476df94e96b5a`.
The source and OS-read-only copy remained byte-identical at SHA-256
`4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`.

This slice does not verify the external ampacity/factor basis, compare against load current,
or perform cable/protective-device selection, voltage-drop coordination, short circuit,
earthing, or standards compliance.

The implemented `m20.short_circuit_source_equivalent.v1` boundary supports a reviewed
single-phase complete-loop interpretation using L-N voltage and a balanced three-phase
bolted-fault interpretation using L-L voltage. It links one exact route and retains nominal
voltage, explicit voltage multiplier, separate source/path R and X, their reviewed basis,
total impedance, symmetrical RMS current, apparent fault power, formulas, warnings,
model/context evidence, and a self-hash. Zero total impedance, unknown routes, changed
evidence, coercion, and invalid ranges fail closed. Benchmarks, rejection, authenticated
API, package, AutoCAD palette, DLL build, and the full `392 passed, 1 skipped` automation
are complete. Live AutoCAD 2021 acceptance used context
`441998daec6842159128e85284d73a53`, eight selected metric entities, DBMOD=0, and evidence
SHA-256 `1eef7034e53762987c04faf9c02db766279a101055320d6eea0d0d2f7580c8e2`.
The reviewed three-phase 400 V boundary used source R/X 0.01/0.05 ohm and path R/X
0.02/0.04 ohm, producing total magnitude 0.094868 ohm, 2434.322478 A symmetrical RMS,
and 1686548.085424 VA. Result SHA-256 was
`f2e11584c488c85a9410b9c0deaf74593729246068fcc5ada0d9ff4a73d17fd6`.
The source and OS-read-only copy remained byte-identical at SHA-256
`4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`.

This boundary does not calculate generator/motor/converter contributions, sequence
networks, transient/peak current, arc impedance or arc flash, and does not evaluate an
equipment rating, interruption capacity, coordination, or standards compliance.

Milestone 20 remains active: its declared scope still requires reviewed project-rule
interfaces for cable/protective-device adequacy and PE/earthing-conductor sizing. Those
interfaces must consume the completed current/drop, aggregation, ampacity, and fault
results without hard-coded uncited code limits. The successful source-equivalent boundary
does not itself close that gap.

The next slice, `m20.candidate_adequacy.v1`, now has its deterministic core, strict
authenticated composite API, package coverage, and AutoCAD review UI. The API re-imports
the exact context and recalculates current/drop, derated ampacity, and short-circuit inputs
server-side rather than accepting client result numbers. Five findings cover required
ampacity margin, maximum reviewed voltage drop, device rating not below load current,
device rating not above derated ampacity, and breaking capacity not below symmetrical RMS
fault current. The palette displays every PASS/FAIL finding and source result hash while
automatic selection, code compliance, and drawing mutation remain false. The DLL builds;
`397 passed, 1 skipped` and compileall pass.

Read-only AutoCAD 2021 live acceptance passed on 2026-08-11. All five reviewed findings
passed under result SHA-256
`4695740c2634ba899ab77a3fc70e7aad9a0542288fedb251682808ed8d8eca42`.
The source and disposable copy remained byte-identical at SHA-256
`4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`.
No automatic selection, compliance evaluation, or drawing mutation occurred. See
`docs/evidence/m20-candidate-adequacy-live-acceptance.md`.

The remaining PE/earthing interface is implemented as
`m20.protective_conductor_adiabatic.v1`. It calculates `S=I*sqrt(t)/k` only when an
explicit reviewed applicability decision, disconnection basis/time, conductor factor,
rule-source identity, units, and exact same-route short-circuit result are present.
Unknown applicability returns `UNKNOWN`; an explicit exclusion returns
`NOT_APPLICABLE`; neither emits a calculated value. The API recalculates the fault input
server-side and rejects client result payloads, coercion, mismatched routes, and numeric
inputs on unresolved outcomes. The AutoCAD palette displays source identity, outcome,
inputs, formula, and result without selecting a standard conductor or claiming compliance
or drawing mutation. Compileall, the DLL build, and `403 passed, 1 skipped` automation
pass.

Read-only AutoCAD 2021 live acceptance passed on 2026-08-11 with result SHA-256
`b69287cd88b700b520e19b2daa5cec0646018f954f32ed4fb354fc8cc36abb3b`.
The exact recalculated fault result produced 9.466627 mm2 for the explicit reviewed
0.2 s and 115 A*sqrt(s)/mm2 inputs. The source and disposable copy remained byte-identical
at SHA-256 `4b9b3af7f0091c5392896087a196332c3bf5f048487720d9b2ab5c2f90cd8535`;
selection, compliance, and mutation flags stayed false. See
`docs/evidence/m20-protective-conductor-live-acceptance.md`.

Milestone 20 is complete within this deterministic reviewed-input boundary. It provides
load/current/drop, aggregation/diversity/phase allocation, reviewed ampacity derating,
source-equivalent short circuit, one-candidate adequacy findings, and an explicit
adiabatic PE requirement. It does not search for or select products/standard sizes,
verify external code/manufacturer authority, or claim standards compliance; those remain
separate evidence and compliance work in Milestones 22 and 23.

## Milestone 21 — Panels and single-line diagrams

Create bounded panel schedules and single-line diagrams from approved model data and
extract supported existing diagrams back into the model. Preserve circuit identity
between drawing, schedule, and SLD; surface ambiguity and conflicts for human review.

The engineer-supplied seven-sheet Excel panel-schedule form is now registered as an
immutable, SHA-256-bound template. Automated validation fixes the reviewed worksheet
catalog, formula/merge counts, used ranges, and `D3` panel-name convention before a
deterministic one-sheet-per-panel generation plan is accepted. Template registration and
planning are complete. The explicitly approved Excel COM adapter now performs the actual
one-sheet-per-panel cloning into a new output while preserving formulas, formatting, merged
cells, and blank unknown values. It colors only explicitly estimated input cells red without
recoloring formula totals, fails closed on formula overwrites/errors or unsafe sheet names,
and passed six-sheet recalculation plus visual acceptance with the template hash unchanged.

The first contract foundation is implemented and verified. Immutable self-hashed panel,
outgoing-way, reviewed protective-device, load-assignment, and SLD node/edge records bind
to the exact project, document, AutoCAD context evidence, and canonical shared-model hash.
Validation requires complete one-to-one selected-panel circuit/way/protection coverage,
explicit load-versus-spare ways and phases, source-linked load coverage, and the exact
panel-to-protection-to-load topology. Missing, duplicate, ambiguous, foreign, or
conflicting identities fail closed. This layer performs no schedule generation or drawing
mutation; deterministic generation, supported extraction, API, and live palette review
remain active work.

Deterministic read-only panel-schedule generation is also implemented. It revalidates the
exact contract against the current typed model, orders rows by way number, preserves the
circuit/protection/load source identities, records explicit load and spare counts, and
returns a self-hashed `m21.panel_schedule.v1` result. Generation declares that no existing
schedule was extracted and no drawing mutation or standards-compliance decision occurred.
The current AutoCAD context mapper intentionally does not invent panel semantics from
generic blocks; supported bounded extraction must populate those identities before the
authenticated live API can honestly accept real drawing context.

Supported read-only extraction and its authenticated API are now implemented in automated
coverage. The fixed v1 marker profile uses explicit panel, outgoing-way, SLD-node, and
SLD-edge block references with exact attribute schemas; generic blocks are ignored rather
than guessed. A complete bounded context creates a semantic model and revalidated contract,
then the API returns the deterministic schedule. Malformed, truncated, missing, duplicate,
or conflicting observations return `UNKNOWN`/`CONFLICT` findings and publish no partial
model or schedule. Every extracted SLD node and edge retains its source AutoCAD object,
document, and context identity. Extraction profile v6 resolves load source handles against
recognized devices in the exact context instead of trusting selection-order-derived IDs.

The AutoCAD 2021 palette now contains a separate `Panels / SLD` read-only review tab. It
uses the last explicit in-memory context and authenticated extraction endpoint, requires a
bounded reviewer and assumption, rejects mismatched context/contract hashes and unsafe
flags, displays incomplete UNKNOWN/CONFLICT findings without partial semantic data, and
shows ordered way/circuit/phase/protection rows only for a complete result. The x64 DLL
build and automated source/package coverage pass. The disposable marker fixture and
AutoCAD 2021 complete/fail-closed visual read-only palette paths are live-verified.

The separately approval-gated fixed fixture producer is implemented and covered. It binds
execution to the exact disposable-source SHA-256, writes only a scratch copy, creates four
marker definitions, ten marker references, 58 attributes, and one recognized load reference,
and re-verifies every tag/value, the actual load handle, exact 11-entity growth, clean save,
and both drawing hash states. Live run `cdd846e7d3c24aa59400a12721f32bdb`
passed with `DBMOD=0`, an unchanged DWT source, and saved scratch SHA-256
`82f8f28780b70c72fb1018e44e332d0ed3b82be3020112f80ca88229c58dca2c`.
The subsequent palette read-only acceptance passed.
An independent AutoCAD 2021 read-only inventory run then confirmed two layers, 11
entities, `DBMOD=0`, and byte-identical accepted/inventory drawings at SHA-256
`82f8f28780b70c72fb1018e44e332d0ed3b82be3020112f80ca88229c58dca2c`.
The visible in-product palette portion also passed.
The visible complete-result path subsequently passed against an explicit 11-entity
selection. Extraction SHA-256 `0a7bf895...e86bea` and schedule SHA-256
`df4cf74...938f1` independently reproduced; the palette showed panel DB-1, two ordered
ways, one load, one spare, and false egress/mutation flags while drawing bytes stayed
unchanged. A one-panel negative selection then visibly returned three `UNKNOWN` findings,
no schedule, null semantic model/contract, false egress/mutation flags, and the same
unchanged read-only drawing hash. Milestone 21 is complete within this fixed-marker
read-only boundary.

The later `lighting.dwg` classification audit is also retained as project evidence without
expanding that boundary. It deterministically separates 336 reviewed devices, seven reviewed
electrical routes, two reviewed annotation polylines, 647 route candidates, and 3,856 unclassified
entities. Its 45 candidate circuit references include seven repeated-occurrence conflicts and 35
unresolved loads across 12 non-conflict rows. Final Panel Schedule/SLD and protection/cable/phase
review is deliberately deferred by the engineer to a clearer drawing; no candidate proximity is
treated as circuit connectivity.

The first project-specific drawing path is also live-verified for panel `DB3-GA2`. The producer is
bound to reviewed preview SHA-256 `9d7e7dd29b453d1bbb13c17dcdbe2ec194b63b91cb6c92b7a033f551427727d7`
and source SHA-256 `1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`.
It rechecked an empty model-space rectangle below the legend, created exactly 234 LINE/TEXT/CIRCLE
entities on four reserved semantic layers in a copied Scratch drawing, and handed off a new file
without overwriting the source. Live counts were `4,848 -> 5,082`, clean save was `DBMOD=0`, and a
second read-only AutoCAD 2021 reopen confirmed 102 layers and 5,082 entities. Output SHA-256 is
`b3765d590dde716699b11ca453947062a83f2d7c4c15eb6303d69fdfc1d8d856`. Missing incomer,
busbar, phase, protection, cable, and frequency data remain visibly TBD; this is a draft engineering
diagram, not an approved construction/as-built SLD.

The visually confusing right-group diagnostic overlay is retained only as selection evidence, not
as an SLD. A clean replacement no-mutation diagram is now bound to that exact reviewed occurrence
and preview SHA-256 `d29072e3831d207ea63396da448512007ce7f114ffc1d8cf75e743d3b532a1f2`.
It shows ways 1-7 only: 1/3/5 are blank non-spares; 2/4/6/7 are populated; way 6 includes its four
newly reached L03 blocks; and way 7 reports a known partial load while its unresolved L04 and LED
tape keep the total at TBD. No replacement AutoCAD insertion is authorized until this exact preview
receives visual approval.

## Milestone 22 — Live code intelligence and compliance

Implement project-pinned jurisdiction/code profiles, official or lawfully licensed
source retrieval, citations, editions, effective dates, clause evidence, content
identity, deterministic rule evaluation, conflict handling, and newer-edition impact
alerts. Never bypass access controls, reproduce protected codes wholesale, silently
change an active edition, or claim regulatory approval.

The first local contract slice is implemented. Immutable self-hashed
`m22.code_profile.v1` profiles pin jurisdiction/project scope, ordered authority
precedence, reviewed source editions and effective dates, lawful access basis, locators,
retrieval/content identity, project requirements, reviewer, and assumptions. Profiles
cannot store full protected text or claim retrieval/compliance. Self-hashed
`m22.edition_impact.v1` records classify same/newer/older/unknown discoveries against one
exact profile hash; newer/unknown discoveries require impact review and activation is
always false, so discovery never silently changes the pinned edition. Clause/rule
evidence and deterministic compliance findings are also implemented as self-hashed
`m22.clause_evidence.v1`, `m22.compliance_rule.v1`, `m22.engineering_fact.v1`, and
`m22.compliance_result.v1` records. They bind exact profile/source editions and hashes,
reviewed clause locators and bounded paraphrases, upstream calculation/model result
hashes, decimal values, units, formulas, reviewers, dates, and assumptions. Missing
evidence/facts produce `UNKNOWN`; duplicate facts, inconsistent units, or competing
requirements produce `CONFLICT`; neither state can satisfy requirements. Evaluation
does not retrieve provider data, claim regulatory approval, or mutate a drawing.
The authenticated strict `POST /v1/compliance/evaluate` route now parses and revalidates
the complete self-hashed profile/clause/rule/fact graph without coercion, returns citations
with the exact profile/source/evidence/result hashes, and logs only bounded result identity
and safety flags. The AutoCAD `Code Compliance` palette tab opens a reviewed bounded JSON
request, evaluates it locally, validates returned hash bindings and negative safety claims,
and visibly renders citations plus `PASS/FAIL/UNKNOWN/CONFLICT`; it contains no drawing-write
or AutoCAD execution path. The x64 AutoCAD 2021 DLL and the `433 passed, 1 skipped` automated
baseline pass. Live palette acceptance with official or lawfully licensed evidence remains.

Live acceptance passed against a separately scoped public OSHA functional fixture,
not an Egyptian-project authority. The downloaded official page, profile, clause, rule,
four requests, and observation facts have exact recorded hashes; automated reconstruction
proves all four finding states. The authenticated API health check and AutoCAD 2021 palette
launch passed. The operator visually confirmed the exact citation, profile/source/evidence
hashes, deterministic PASS, completeness/satisfaction states, and false provider retrieval,
regulatory approval, and drawing mutation flags. Independent reconstruction reproduced the
displayed result SHA-256 exactly. Milestone 22 is complete within its pinned-source,
reviewed-evidence, read-only AutoCAD boundary; it does not activate the US fixture as
authority for an Egyptian project.

## Milestone 23 — Manufacturer evidence and component assets

Add opt-in official manufacturer search for data sheets, properties, dimensions,
manuals, and CAD assets. Retain source/version/date/hash provenance, quarantine and
validate downloads, map reviewed properties into the shared model, and route generated
or imported blocks through existing approval and catalog controls.

The first local trust-contract slice is implemented as self-hashed
`m23.manufacturer_evidence.v1` bundles. It binds an exact manufacturer and approved official
domains to one product family/model/part number/variant, exact data-sheet/manual/CAD
artifact revision, publication/retrieval dates, access/license basis, locator, media type,
byte count and content SHA-256, plus bounded engineer-reviewed property claims with source
location and exact applicability. Official-public artifacts require HTTPS and an approved
exact manufacturer host. CAD assets must remain quarantined, executable content is rejected,
and bundle creation cannot claim retrieval, provider egress, catalog registration, or
drawing mutation.

Bounded retrieval is now implemented with self-hashed
`m23.manufacturer_retrieval_request.v1` approvals and `m23.quarantine_record.v1` results.
Each approval binds one full HTTPS URL, exact host, product/artifact identity, permitted
media types, byte limit, optional expected content hash, engineer/date, and invariantly
forbids redirects. Retrieval disables proxies and redirects, validates public DNS addresses,
streams through a hard byte limit, checks PDF/DXF/DWG signatures and executable magic,
verifies the optional hash, and creates an immutable generated filename atomically inside
the caller-configured quarantine. The result records resolved addresses, media/bytes/hash,
relative path, and false redirect/proxy/catalog/drawing flags. Existing quarantine identity
cannot be overwritten. Official hosts that declare exact `.dwg`/`.dxf` paths as generic
binary are accepted only after explicit typed approval and CAD-signature validation; other
generic binaries remain rejected.

Reviewed property/model and CAD/symbol mapping are now implemented without mutating either
the shared model or catalog. Self-hashed `m23.manufacturer_model_binding.v1` sidecars bind
one exact project/context/model hash and an existing device/load/protection/panel/cable ID to
the exact product/part number, evidence-bundle hash, reviewed claims, artifact content hashes,
and `MANUFACTURER_EVIDENCE` provenance. Self-hashed
`m23.manufacturer_symbol_candidate.v1` records require a matching evidence bundle, exact
retrieval approval, quarantine record, CAD locator/media/bytes/content hash, and a manually
reviewed catalog proposal that passes the catalog's existing strict validator. Automated
geometry approval, catalog registration, and drawing mutation are invariantly false.

Separately confirmed manufacturer-candidate registration is now implemented on top of the
existing strict catalog registrar. It revalidates the exact canonical candidate proposal,
records catalog SHA-256/version before and after, verifies the timestamped backup equals the
prior catalog, and persists a self-hashed `m23.manufacturer_catalog_registration.v1` lineage
record covering candidate, bundle, product/part number, CAD/quarantine, symbol definition,
catalog, backup, registrar, and time. If lineage persistence or post-write verification
fails, the exact prior catalog is restored and verified. Registration explicitly performs
no drawing placement or mutation.

The authenticated `POST /v1/manufacturers/review` endpoint and AutoCAD `Manufacturers`
palette now reconstruct, rehash, and display exact evidence, retrieval, quarantine,
model-binding, symbol-candidate, and registration lineage. Review itself hard-codes false
provider-egress, retrieval, registration, and drawing-mutation flags. Live AutoCAD 2021
acceptance passed with official Legrand part `004280`, data-sheet SHA-256
`413bf9fbb4dd1186721f35360d6cf849919f9046dd66bf13539b87077e6c085d`, quarantined DWG
SHA-256 `08b5fd966ee2df8eac93668f0d9317b2d0fc8d3ccc59f0ddc337907e471ab430`, bundle SHA-256
`b7d8704620a60f64a9fb2ff94ac5d41361bf0da4ea87690d79d47a2f954b7743`, and result SHA-256
`e18ced57a6b574b1f857475e61e475705635f121577686df6220d113ff37d6fd`.
The operator visually confirmed `Complete: true`, three `PASS` findings, and every negative
action flag. This is functional evidence, not project suitability or procurement approval.
Project-scoped opt-in official product discovery and cited candidate comparison remain
before Milestone 23 closes.

The local discovery contract, strict injected-provider boundary, authenticated API, and
AutoCAD `Product Discovery` review tab are now implemented. Each requirement retains its
source kind/identity/hash, operator, value, unit, and mandatory status. Candidates must use
an approved official host and carry exact product URLs, source dates/hashes, part numbers,
and property locators. Comparison is deterministic; missing or competing data remains
visible. Provider egress is explicit, while automatic selection, download, catalog
registration, and drawing mutation remain false. The separately opted-in production OpenAI
Responses adapter now restricts web search to the reviewed official domains and requests
strict structured output. It independently re-fetches exact product pages, enforces public
DNS/no proxy/no redirect/text/size boundaries, verifies the part number and quoted properties
against the bytes, and calculates source hashes locally. An authenticated self-hashed
choice-for-follow-on-review route and palette control provide the explicit engineer decision
without product approval or follow-on action. Live AutoCAD acceptance with multiple verified
candidates remains before Milestone 23 closes.

Discovery can now be launched in `Free`, `Economy`, or `Quality` mode. Free mode accepts a
user-prepared candidate draft and performs the same independent official-page verification
without an AI-provider call. Economy selects `gpt-5.6-luna`/low reasoning, while Quality
selects `gpt-5.6`/high reasoning. The mode is explicit before API startup, and paid credentials
are still removed before AutoCAD launches.

Free mode now has a browser-assisted fallback for official sites that reject transparent
automated requests. It reads only bounded UTF-8 HTML through the sandboxed workspace service,
validates the approved official URL/host and exact evidence against the saved bytes, and binds
the result to their SHA-256. This is explicitly engineer-saved evidence, not a claim that the
API retrieved the page or that a product was approved.

The browser-assisted Free path passed live AutoCAD 2021 acceptance on 2026-08-12 with Legrand
`004280` and Schneider `A9A15306`. Exact saved HTML hashes matched the verifier, both candidates
matched all three acceptance properties, and the engineer's Legrand choice was hash-bound for
separate review. Every provider/retrieval and follow-on action flag remained false. Paid hosted
search acceptance subsequently passed when funded API credit became available.

The live run also exposed a palette layout defect that compressed discovery output and truncated
button labels. The result area now receives the flexible panel space, long evidence lines wrap,
review controls stack responsively, buttons autosize, and a local `Copy complete result` action
supports exact review handoff without invoking any API or follow-on operation.
The corrected layout then passed live AutoCAD 2021 visual acceptance with all controls readable,
the candidate URL and three requirement findings visible, and no follow-on action performed.

The paid discovery boundary now prevents accidental duplicate charges within a session by
caching only successfully parsed candidate tuples under the exact approved request SHA-256.
Identical repeats reuse immutable validated data; changed approvals call the provider separately,
and provider/validation failures are not cached.

The remaining acceptance is reproducible rather than ad hoc. The checked-in builder creates a
self-hashed 16 A/230 V/2P+E functional fixture restricted to two reviewed official domains,
and the dedicated Windows launcher isolates the API key from AutoCAD, preloads the request,
and owns temporary API cleanup. Exact artifact hashes and visible pass criteria are recorded
in `docs/evidence/m23-discovery-live-acceptance.md`; the paid-provider visual run is not yet
claimed.

The funded Quality acceptance passed in AutoCAD 2021 on 2026-08-13. OpenAI `gpt-5.6` returned
Legrand `004280` and Schneider Electric `A9A15306`; each identity was matched to the already saved
official HTML, revalidated locally, and passed all three deterministic requirements. Result
SHA-256 `e0edddc0df3169e3c08432ac62e25a1e084cf333f145211718d03a7594c35045` and Legrand review-choice
SHA-256 `26c5e85d55d60b8ac24baa2e2b8fa21756a69b1a4f5ffb9336b2e95f7dc55d55` were visually confirmed.
Provider egress was true; automatic selection, product approval, download, catalog registration,
and drawing mutation remained false. This closes Milestone 23 within its documented boundary;
earlier paragraphs above are retained as the implementation history, not current open work.

## Milestone 24 — ETAP connector

This milestone cannot start until the AutoCAD-only product gate has passed for
previewed drafting, quantities, calculations, schedules/SLD, selected-code evidence,
manufacturer assets, recovery, security, and representative real-project acceptance.
It is a separate integration edition rather than a prerequisite for the AutoCAD
product.

Validate the installed ETAP version and its supported interfaces, then implement a
read-only capability matrix and reviewed exchange through the shared model. Add
network-model reconciliation and result import before any write-back. Live acceptance
must cover supported load-flow/short-circuit/protection data without GUI automation or
claims beyond ETAP's returned results.

## Milestone 25 — SOLIDWORKS Electrical connector

Start this separate integration edition only after the ETAP connector has passed its
own read-only, reconciliation, recovery, and live-acceptance gate.

Validate supported interfaces and map reviewed schematics, components, wires, cables,
terminals, cross-references, and bills of materials into the shared model. Start
read-only, preserve external identities, detect conflicts, and add separately approved
write-back only after round-trip fixture and live verification.

## Milestone 26 — Professional 1.0 release

The 2026-08-24 CAD incident set is now durable program behavior rather than chat memory. A
self-hashed, append-only reviewed-lessons registry loads at startup, and typed manufacturer-CAD
intake plus AutoCAD STEP/Core Console contracts enforce exact part identity, hash deduplication,
non-interactive import, native completion evidence, hybrid instance counts, `DBMOD=0`, and
unchanged-source verification. It deliberately does not perform autonomous self-modification or
promote unreviewed model output.

Complete real-project acceptance suites, performance and incremental-update targets,
security and privacy audit, calculation benchmark review, connector failure recovery,
multi-PC installation, operator training, documentation freeze, and professional
packaging. Version `1.0.0` requires honest supported-feature and code-edition matrices.

AutoCAD beta packaging preparation now has a deterministic read-only readiness command. On the
current Windows destination it passed Python 3.13.14, all fixed AutoCAD 2021/.NET dependencies,
seven hashed product entrypoints/assets, and a free localhost port without provider egress or
drawing mutation. This reduces installation ambiguity but does not replace multi-PC or real-project
acceptance.

An honest supported-feature matrix now freezes the current beta boundary across context import,
drafting, calculations, panels/SLD, evidence, providers, packaging, and future connectors. Local
performance gates are also defined for small and maximum-supported context, preview, calculation,
and recovery fixtures. Measured five-run Windows/AutoCAD acceptance remains required before the
beta can claim those targets.

The deterministic local portion now has a packaged-command benchmark and passed five-run small and
maximum context parsing, preview hashing, and LV calculation on the destination PC. The self-hashed
report explicitly leaves the release gate incomplete until live AutoCAD capture/display and copied-
scratch execution/recovery measurements pass.

The portable AutoCAD beta candidate was also built, integrity-validated, installed into a clean
local destination, and passed installed-copy product/readiness/template checks. Its exact archive
identity is recorded in `docs/evidence/autocad-beta-package-candidate-2026-08-12.md`; second-profile,
real-project and live-performance gates remain open; paid discovery is complete.

After paid discovery acceptance, UI-timeout correction, and packaged local-core performance work,
the current dated candidate was built and installed cleanly. It passed installed readiness and all six
local benchmark targets. Package-identity evidence is excluded from the archive to prevent stale
self-reference. Its exact current revision and external identity are recorded in
`docs/evidence/autocad-beta-package-candidate-2026-08-13.md`.

Representative real-project acceptance then started against `lighting.dwg`. Read-only Core Console
inspection recorded 4,848 entities, 98 layers, millimeter units, DBMOD=0, and an unchanged source
hash. Explicit-selection evidence exposed valid mirrored block scales, now supported as bounded
non-zero values, plus honest remaining gaps for long-polyline truncation, project-specific
block/tag mapping, and persistent DBMOD bits in the visible read-only copy. Exact evidence is in
`docs/evidence/autocad-real-project-lighting-2026-08-13.md`.

The subsequent complete 15-entity selection received explicit engineer review. A strict self-hashed,
exact-context profile now maps six observed fixtures/switches, one lighting-circuit route, panel
`DB3-GA2`, and circuit `4` through the normal import service. Circuit device membership and six
control-tag associations remain deliberately empty/unknown; only explicit endpoints connect routes,
and tampered, changed, or duplicate profiles fail closed.

A fresh source-identical, non-truncated context then passed strict review rebind and normal
authenticated AutoCAD palette acceptance. The visible Arabic summary showed 7 devices, 6 routes,
8 unknown/missing items, and 5 warnings at presentation-only DBMOD=16; the local session retained
`drawing_write_allowed=false` and no provider egress occurred.

Later exact device/tag and route-only captures recognize 12 reviewed devices and nine complete
routes for `DB3-GA2 / 4`. The cross-context connection-anchor contract passed real-project review
after replacing an unclear batch with endpoint-marker-v2. Seven electrical routes now have 14/14
reviewed endpoint connections; two polylines are explicitly classified as one circuit-number
arrow/leader and excluded from topology and cable length. A copied-Scratch one-LINE execution also
passed with unchanged source identity. Circuit-fragment creation remains correctly blocked by the
project's incompatible existing `E-WIRE` layer until an explicit mapping is reviewed.

Portable AutoCAD beta candidate r20 captures the operator-channel and live-cancellation checkpoint
after `520 passed, 1 skipped`. Its
clean installed copy passed product check, twelve-file readiness, x64 DLL compilation, and all six
five-run local-core targets. Exact package evidence is recorded in
`docs/evidence/autocad-beta-package-candidate-2026-08-16-r20.md`. The exact no-mutation channel passed
live context, status, and palette actions; context capture returned 512 entities in 26.5282 ms at
`DBMOD=0 -> 0`. The engineer visually confirmed the tab and click-to-open palette. Exact
cooperative cancellation then passed in 619.1593 ms with no partial context or mutation, and the
follow-up status remained `DBMOD=0 -> 0`. Storage was identified as healthy NVMe SSD. Maximum
Scratch and second-profile acceptance remain release gates.

The next source slice adds an automated-verified complete top-level drawing-index path without
raising the per-context safety bound. AutoCAD publishes a fresh local document identity beacon;
the exact operator request then scans Model and paper layouts, sorts handles deterministically,
exports complete adaptive shards, and atomically commits one source-SHA-bound manifest. The
strict local loader verifies every chunk hash, document identity, DBMOD, count, completeness, and
global handle uniqueness. Source tests and the AutoCAD 2021 x64 plugin build pass; representative
live `lighting.dwg` acceptance then passed without a manual AutoCAD command or selection:
4,848/4,848 unique top-level entities were validated across 76 chunks in 1.119 seconds at
`DBMOD=0 -> 0`, with unchanged source SHA-256 and a successful follow-up status action.
Nested definitions/Xrefs remain unsupported by this first scope.

The accepted index now feeds an immutable drawing-wide inventory instead of requiring repeated
manual selections. Exact reviewed membership handles are the only route primitives promoted to
confirmed electrical routes; same-layer geometry remains a candidate, and explicitly reviewed
arrow/leader geometry remains annotation. The first `lighting.dwg` inventory revalidated all
4,848 entities as 336 reviewed devices, seven reviewed routes, two reviewed annotations, 647
route candidates, and 3,856 unclassified entities. A deterministic handle-level comparator
separates added, removed, geometry-changed, semantically reclassified, and unchanged entities;
the accepted baseline self-comparison returned 4,848 unchanged and zero changes. This does not yet
perform selective AutoCAD recapture: each new comparison input is still a complete read-only index.

An automated refresh coordinator now connects the already live-accepted local operator channel to
that inventory pipeline. It discovers exactly one requested active drawing, submits only the
allowlisted `capture_drawing_index` action, validates the returned manifest and all shards, builds
and atomically stores the inventory, compares an optional baseline, and writes a self-hashed local
report. Source-identical reviewed semantics are applied only when their exact document and drawing
SHA-256 still match. A changed drawing falls back to structural classification and explicitly marks
semantic review required; stale electrical meaning is never silently rebound. Automated unchanged-
source, changed-source, ambiguity, and no-mutation coverage passes. The unchanged-source
coordinator then passed live AutoCAD 2021 acceptance on the isolated `lighting.dwg` copy: all
4,848 entities were captured in 1.093 seconds at `DBMOD=0 -> 0`, reviewed semantics remained
applicable, and comparison returned 4,848 unchanged with zero additions, removals, geometry
changes, or reclassifications. The source hash stayed unchanged and the isolated copy closed
without saving. Changed-source live acceptance then passed on an explicitly approved Scratch copy
with one added LINE: the comparator found exactly handle `1A1C51F4` as added, no removed or changed
handles, and 3,857 structurally unchanged objects. All 991 source-bound semantic classifications
were withheld/reclassified and the report set `semantic_review_required=true`; the original source
remained byte-identical. The AutoCAD capture itself remains complete rather than change-selective.

The next source slice adds a bounded event-backed drawing-delta channel. Every successful complete
index arms an in-memory baseline for that exact AutoCAD database. `ObjectAppended`,
`ObjectModified`, and `ObjectErased` events maintain disjoint handle sets; the new exact
`capture_drawing_delta` action exports only added/modified entities and records removed handles.
The strict Python loader verifies baseline index ID/SHA, old/current source hashes, document,
DBMOD, candidate partitions, shards, and exact changed-handle coverage. The journal is in-session,
one-use, and capped at 4,096 candidates; restart, missing baseline, overflow, overlap, tampering,
or reuse fails closed. The AutoCAD 2021 x64 DLL builds and the 539-test official suite passes.
Live same-session mutation acceptance passed on a separately approved writable Scratch copy. The
4,851-entity baseline was armed at `DBMOD=0`; after one exact append, modification, and erase plus a
clean save, delta `533ca85d68a04501b1545146ce8dc8f8` returned only added `1A1C523C`, modified
`1A1C51F4`, and removed `1A1C520C` in 25.6422 ms at `DBMOD=0 -> 0`. Unchanged control
`1A1C5224` was absent. The trial exposed and fixed a managed-wrapper identity defect by binding the
tracker to AutoCAD's stable unmanaged database identity.

The accepted delta then composed into immutable inventory v2
`af3ca94266f22a7ce8ea02ac5c4d9d59`, SHA-256
`7d956d2d5585ce226755e015db49a0524e45e856ec7df4187223b07b7e8ef4d2`. Its lineage binds baseline
inventory SHA-256 `2ed048f95b47b413d586e764a633d1f65b7a8bc91733df0c1da1fc9e1d382c23`
and the accepted delta ID/SHA. Independent reload and comparison returned the exact one added, one
removed, one geometry-changed, zero reclassified, and 4,849 unchanged handles. Source-bound
convention/topology identities are absent and semantic review is mandatory.

Portable beta candidate r28 packages the complete index/selective-delta/inventory-v2 pipeline.
Archive `ai-engineer-0.18.0-autocad-beta-candidate-20260823-r28.zip` has SHA-256
`fab3870f357994e61853810043553f6a10d70cf1b74352a9370d05602251f4ea`. A clean installation
passed the product check, 18-file AutoCAD readiness on available port 8778, installed x64 plugin
compilation, installed inventory-delta CLI startup, and all six five-run local-core performance
targets. The installed DLL SHA-256 is
`904d85ad0ab2a5492c8592b52ac63670eb40c1b100875991e4a95d4c88ef5778`. Live packaged
context/palette restart, maximum Scratch execution/recovery, second-profile acceptance, and nested
block/Xref support remain open; professional release completion is not claimed.

Portable candidate r29 supersedes r28 for installation correctness. The validated installer now
creates empty `workspace`, `logs`, and `backup` runtime directories atomically with every clean
install, so the installed AutoCAD plugin build no longer needs a manual workspace-preparation step.
The 173-entry archive has SHA-256
`e51521bb96271fdae9ae7a8179a54603804af3e9d547d0d5406639184f52fd04`. Its clean install
passed 18-file readiness, direct x64 DLL compilation, inventory-delta CLI startup, and all six
five-run local-core targets. Source and installed-package tests remain `541 passed, 1 skipped`.

Portable candidate r39 supersedes r29 after resolving two Windows packaging defects exposed by
live acceptance. New installations now write directly into the final destination so runtime files
inherit the destination ACL, with complete rollback on failure. The AutoCAD launcher also rejects
legacy operator-channel paths of 260 characters or more with an actionable short-install-path
message. Diagnostic candidates r30-r38 are not release candidates. The 173-entry r39 archive has
SHA-256 `5ce420dbe212dfa0eca0f9151fca4f84a74d53e5734570f2d59ba57b7109a4a7`; its installed plugin
DLL has SHA-256 `1db8274fa48d00c52517fb6d30885165628ec9aab93eaf31b999b014ab969808`.

The clean-installed r39 package passed current-profile live AutoCAD 2021 acceptance. Exact status,
full-drawing capture, and palette-display requests completed at `DBMOD=0 -> 0`; 4,848 unique
top-level entities were captured into 76 validated chunks in 1.2566212 seconds and independently
reloaded. The engineer visually confirmed the palette. The copied read-only drawing then closed
without saving and the original SHA-256 remained unchanged. The official suite passes
`542 passed, 1 skipped`. Maximum-fixture Scratch execution/recovery and a second initialized
Windows/AutoCAD profile remain release gates.

Portable candidate r40 adds a dedicated maximum-size execution fixture rather than expanding any
production drafting limit. The HIGH-risk command accepts only a workspace drawing path and exact
reviewed SHA-256, copies the source, creates exactly 1,792 fixed `LINE` objects on a reserved layer,
requires exact clean-save/count markers, and writes source/output-bound recovery evidence with
`handoff_allowed=false`. Its command payload cannot supply geometry, layer, script, executable, or
output path. Automated coverage now passes `545 passed, 1 skipped`.

The separately approved r40 live run closed the current-profile maximum Scratch performance gate.
It changed only the copied output from 4,848 to 6,640 entities, retained `DBMOD=0`, and completed
execution in 2.4976913 seconds and execution plus verified recovery evidence in 2.5679253 seconds,
below the 30-second target. The original and reviewed copy remained SHA-256
`1d08ccb0557e740f7d471e006d5accad0675d54b82e45d9aabc64e239ecfb117`; output SHA-256 is
`13283a1b15c676d66d998e807d631a0adb9dc62f655fe5ba34e42b31df5b825e`. Candidate r40 archive
SHA-256 is `2c0fa7098c06c7029c382e0a39229d58dd72645c9b465e8253db835ce0c27a5f`.
Only second-profile acceptance remains for the release-performance matrix.


## Supporting platform — Operator Bridge foundation (unreleased)

The portable boundary now includes a separate AutoCAD 2023 destination profile. It keeps AutoCAD
2021 unchanged, targets the destination's own 2023 assemblies, records expected `ACADVER` family
`24.2`, selects installed .NET reference assemblies deterministically, and provides a non-mutating
readiness JSON for capability-specific follow-up. The destination Windows profile passed the live
read-only gate on 2026-09-25: readiness and the native DLL build passed, 69 focused tests passed,
and an exact operator request captured 410 entities in 86.05 ms at `DBMOD=0 -> 0` with unchanged
source/copy SHA-256 `e0bf950a...dec4bb04` and no provider egress or drawing mutation. The automatic
`/b` launch failed closed, so the accepted run used the documented manual-load fallback. The
separate second-profile packaged performance matrix and all mutation paths remain release gates.

The provider boundary now exposes independently selected OpenAI, Gemini, Ollama, and Codex paths,
plus the existing local-first Ollama-to-Codex hybrid. Gemini uses the official Interactions API
shape with strict JSON response schemas and process-only credentials. Automated configuration,
endpoint, packaging, and provider-routing coverage passes. The first explicitly approved live
Gemini egress trial returned a bounded Arabic answer inside AutoCAD with no drawing mutation. Its
live response shape exposed and drove a REST parser correction, while the selected drawing context
exposed a separate presentation gap now addressed by reporting top-level raw `INSERT` references
independently from recognized electrical devices. A live restart verified 57 raw blocks in a
complete 184-entity selection. That trial exposed the equivalent route ambiguity, so summaries now
also report raw `LINE`/`LWPOLYLINE` primitives independently from recognized electrical routes and
label all recognized counts explicitly. A second complete live selection passed with an exact
49 raw blocks and 38 raw route primitives in both independent inspection and the palette; packaged
installation retention remains pending. Raw counts intentionally do not infer device type, circuit connectivity,
installed cable length, nested blocks, or Xref contents. The palette now also derives every action-
button enabled state from its actual local session/message/review prerequisite and displays an
explicit active-session label; this UX correction passed the same first live restart.

A localhost-only operator runtime now exposes the already-reviewed developer-agent state
machine to IDE/CAD sidecar clients without creating a public remote-control port. Extra
workspace roots are runtime opt-in and use an external backup vault. The command catalog
adds bounded Python compilation and approval-gated pytest execution, while file writes and
all existing sensitive AutoCAD actions retain their durable HIGH/DESTRUCTIVE approvals.

The same developer command catalog now includes three exact no-mutation AutoCAD operator actions:
read active-document status, show the existing palette, and capture a complete bounded drawing
index. Gemini may select these typed tools only through the reviewed developer plan/action flow;
arbitrary AutoCAD command text is rejected, the index requires HIGH approval, and observations
exclude local paths and drawing content. The packaged live Gemini round trip is now accepted:
Operator 0.1.6 connected automatically, Gemini proposed only the fixed document-status tool, and
the engineer-approved action completed with accepted verification at `DBMOD=0 -> 0` and no drawing
mutation. Candidate r44 contains the accepted webview fix from first startup.

A dependency-free VS Code Activity Bar panel can create and inspect developer runs, review
plans and exact action payloads, auto-continue only LOW/MODERATE actions when the operator
opts in, and stop for sensitive command approvals. The existing AutoCAD palette can use the
same optional provider process while preserving its exact per-question data-egress preview.

The provider layer now supports four explicit modes without changing local execution
authority: loopback-only Ollama for free routine work, ChatGPT-authenticated Codex CLI as an
isolated read-only reasoning provider, hybrid Ollama-first/Codex-fallback routing, and the
optional fixed-endpoint OpenAI Responses API. Codex is launched ephemerally in an empty
temporary directory with a minimal non-secret environment; it never receives the project as
its working directory. Hybrid routing escalates only provider/contract failures so valid local
work does not silently spend Codex capacity. See `docs/HYBRID_AI_PROVIDERS.md`.

Destination Windows accepted the local Ollama read-only path on 2026-08-09: authenticated
health/provider checks passed, `project.scan` and two useful `file.read` actions all
succeeded and verified without repetition, and cancellation remained responsive and durable
during inference. Direct visual VS Code panel observation and the Codex/hybrid path remain
unfinished. This foundation does not replace Milestone 18 previewed-drafting safety gates or
the deterministic/evidence-backed requirements of Milestones 19–23.

The subsequent destination API acceptance passed a backed-up disposable write, bounded
compileall, the one-use HIGH pytest approval gate with 337 passing tests/1 skip, and a
real AutoCAD 2021 `/readonly` inventory of an official copied Autodesk fixture. The drawing
source and scratch hashes stayed unchanged with `DBMOD=0`; later drawing writes still require
Milestone 18 preview, separate approval, recovery, and verification.

## Supporting platform - Revit 2024 read-only foundation (unreleased)

The operator explicitly requested a Revit connector foundation. AI-Engineer now detects the
installed Autodesk Revit 2024 product and official managed API, reports deterministic
readiness, builds a user-scoped .NET Framework 4.8 add-in, and installs an
`IExternalApplication` with one fixed `TransactionMode.ReadOnly` inventory command. Its strict
local loader accepts only bounded schema-v1 JSON beneath the configured bridge root and rejects
provider egress or document mutation. Five focused tests, compileall, readiness, compilation
against the installed Autodesk assemblies, and installation pass.

After the operator resolved Autodesk licensing, Revit 2024 loaded the add-in and emitted a
`ready` startup beacon. A one-shot, process-only acceptance path captured Autodesk's Snowdon
Towers electrical sample; its corrected simple-dictionary JSON passed the strict loader with no
egress or mutation. Revit closed without saving and the sample SHA-256 remained unchanged.
The smaller Autodesk Dynamo sample then passed a fail-closed dual-collector comparison for every
category, the strict loader, no-save close, and unchanged source hash. The bounded inventory
connector is live accepted. Geometry, parameters, circuits, linked-model expansion, transactions,
saving, and all drawing remain outside the implemented boundary.

The first chat-driven local Revit loop is also live accepted. A UUID-correlated, exact
document-bound `revit-read` request wakes the open host through Revit's supported
`ExternalEvent` mechanism and returns the existing strict inventory result without an in-product
button press. The same-session Dynamo trial passed with no egress, no mutation, no save, and an
unchanged source hash. Broader reads, previews, and any transaction remain later gates.

The same bridge now live-accepts bounded category queries for 13 fixed categories. The
first same-session query returned three wall summaries with IDs, type/family/level, and
millimetre bounding boxes, then closed without saving at an unchanged source hash. Reviewed
parameter/selection reads, previews, and transactions remain later gates.

Nine allowlisted displayed parameters now pass live electrical reading on three Snowdon
panelboards. Loaded electrical family-type discovery is now live accepted on the workspace
Snowdon electrical review copy. It is bounded to four electrical categories and exposes
family/type identity plus FamilyPlacementType and conservative host requirements, never a
write. The first strengthened placement preview is hash-bound to an actual OneLevelBased
downlight type (1456719) and level (9946); it has no execution authority. The separate
single-family handoff is now live accepted for one reference-bound `OneLevelBased`
instance. The final preview binds reference `1457231` and zero elevation from level;
Revit validates the proposed physical point before committing and saves only to a new
workspace RVT. Reopened verification matched element `1735582`, its symbol, level, point,
and output SHA-256 `6F19E8EA...ECEE167C` without mutation or provider egress.

The bounded same-type batch extension is also live accepted. Exact preview
`64D00C9F...77089B74` bound three points to the same symbol, level, reference-derived
elevation, and source hash. Revit created elements `1735582`, `1735583`, and `1735584` in
one transaction and saved a new workspace RVT with SHA-256 `09070A9D...A0A8475`.
Reopened read-only verification matched every ID and point with no mutation or provider
egress. Rotation, hosted families, mixed types or levels, parameters, and circuits remain
separate future gates.

Per-instance rotation is now live accepted for that bounded batch path. Preview
`DAFEA9FE...D41090CE` explicitly bound `0`, `45`, and `90` degree angles to the three
placements. Revit applied them inside the all-or-nothing transaction; reopened read-only
verification matched all three rotations and output SHA-256 `C5AFEA4A...09D02187`.
The operator's isolated L2 screenshot visibly confirmed horizontal, diagonal, and vertical
symbols. Ceiling-hosted families remain the next separate gate.

The first separately approved Revit transaction is now live accepted for the fixed
Model Lines action. The bridge verified the exact preview and active-model hashes,
created a timestamped byte-identical backup, created exactly four lines in one
transaction, and reported their four element IDs. Revit then closed without saving;
the workspace source and backup hashes remained unchanged. No Save, Save As, source
overwrite, family placement, or arbitrary-command path is enabled.

The follow-on new-file handoff is now implemented, automated-tested, and live accepted.
Python and the Revit add-in independently restrict `SaveAs` to a new RVT
below `workspace`, preserve a verified source backup, and reject overwrite or path escape.
A separate read-only reopened-file action checks the output SHA-256, all four element IDs,
and exact preview endpoints. Approved preview `56EF15B0...4E79A2AC` produced output
`78DAEE6D...D13625`; reopened verification matched IDs `1058904`, `1058906`,
`1058908`, and `1058910` and exact geometry without mutation. The source hash remained
unchanged. Direct save, source overwrite, and arbitrary commands remain disabled.

The separate lighting-circuit foundation now reads true `MEPModel` electrical-system
relationships and connector metadata without mutation. Live evidence proves fixture `1735503`
is unassigned, reference `1622255` belongs to system `1622590` (`P109 / 5`), and both expose
the same `DomainElectrical / End / PowerCircuit` connector. Non-executable preview
`FE9DB83D...A7F3AEF` binds those identities and source SHA-256 `1C390703...BCCB8` for review.
At that checkpoint no circuit write action existed; capacity, demand, protection, phase impact,
exact approval, new-file execution, reopened verification, and recovery were still required.

That circuit write gate is now implemented and live accepted after exact operator approval.
Request `6c08de07...71ab` used one supported `AddToCircuit` transaction to assign fixture
`1735503` to system `1622590` (`P109 / 5`) and saved only a new workspace RVT with SHA-256
`A5B3AC70...16031CE`. Reopened read-only verification `119f68bf...a399` and independent system
query `1ad807e3...b64e` matched the exact membership. The source and backup remain byte-identical.
The circuit now reports `100 VA / 95 W`, an observed `+20 VA / +19 W`; capacity, protection,
demand, phase balance, and code compliance remain separate engineering review work.

The follow-on read-only engineering evidence gate now exposes circuit/panel ratings, currents,
phase allocation, modelled voltage drop, wire description, and panel per-phase currents. Live
query `fc117125...0857` and self-hashed review `369C8EC5...2B615D` calculate a `4.166665%`
branch-rating utilization, `30.119672%` panel-demand/MCB utilization, and a small improvement in
the phase-imbalance indicator. The artifact deliberately leaves code criteria, cable ampacity,
fault duty, protection coordination, and 120/208 V project applicability unresolved.

The same read-only gate now also captures circuit length/runs/ways/notes, exact Revit `WireType`
properties, and panel short-circuit-rating text. Live query `e6cbd42d...3736` reports
`12988.925 mm`, one run/way, Copper `THWN`, `60` temperature-rating text, non-magnetic conduit,
and no populated panel short-circuit rating. Self-hashed review `F0EE0DAC...414F` therefore narrows
the remaining engineering work without converting the WireType maximum-size ampacity into a claim
about the selected `#12` conductor. Installation derating, selected ampacity, fault duty,
coordination, pinned code criteria, and responsible-engineer acceptance remain open.

A bounded protection-parameter snapshot now checks the exact circuit and panel instance/type
parameters without enumerating unrelated model data. Live query `6441f18d...b075` found circuit
`Rating = 20 A` and `Frame = 400 A`, plus panel `MCB Rating = 225 A` and 30 maximum single-pole
breakers. It found no breaker curve, fault-current, AIC/interrupting-capacity, or selectivity value,
and the short-circuit-rating field remains blank. Review `F7CFA4BB...CF83` preserves that negative
evidence and does not reinterpret frame size as fault duty.

The next fail-closed calculation-intake gate is implemented. Command
`revit-build-circuit-calculation-intake` validates the self-hashed engineering review, binds the
exact RVT/preview/query identities, preserves observed Revit values as requiring engineer
confirmation, and lists every missing unit-aware engineering input. The live `P109 / 5` intake is
`AA0C2226...4198`: five model observations and 25 missing reviewed inputs. It performs no
calculation and keeps ampacity, short-circuit, coordination, and compliance readiness false.

The read-only query now follows one bounded upstream step using actual Revit electrical-system
membership. Live request `dc67827d...e175` proved `P109` belongs to feeder system `1672726`
from `MB13`, rather than trusting its supply label. The model reports `67.769263 A` against a
`20 A` rating and `45.438594%` voltage drop. Review `D6CDC73E...260A` records
`338.846315%` utilization and `-47.769263 A` headroom; intake `74CDE784...3B9F` carries this as a
blocking model finding. This is an observed inconsistency, not a code-compliance determination.

A standalone Excel engineering-input prototype now consumes the exact upstream-bound intake and
review artifacts for `P109 / 5`. It separates Revit observations, green engineer overrides, and
formula-active values; calculates load current and provisional breaker-series selection; and gates
ampacity, voltage-drop, short-circuit, breaking-capacity, and coordination results on their required
inputs. A reversible input-change test proved formula updates before the delivered workbook was
restored to its intentionally incomplete state. It is a one-circuit review artifact, not yet a
whole-model panel schedule or quantity takeoff.

The installed Revit bridge is now readiness `v28`. Lighting-fixture circuit queries retain the
existing 32-element request bound but add an element-ID continuation cursor and strict page
validation. Unit tests cover first/final pages, exact-element incompatibility, and invalid final
continuation. The DLL compiles and the installed copy matches the build. Live acceptance captured
all 589 fixtures in 19 ordered pages without duplicate, gap, or non-progress results. The immutable
inventory aggregates 563 connected fixtures, 26 unassigned fixtures, 113 unique circuits, 31
panels, and 22 lighting family/type rows. A separate strict category-page chain captures 538
electrical fixtures, 196 lighting devices, 200 equipment instances, 530 conduit segments, 322
conduit fittings, and no cable tray. Geometry evidence totals 1,730.348 m of modelled conduit.
A visually verified ten-sheet Excel workbook presents the source-bound panel schedule, panel list,
lighting/device/equipment/conduit/fitting BOQs, modelled cable quantities, and green
engineer-input cells. It intentionally does not claim installed route length, final cable size,
breaker coordination, short-circuit compliance, unmodelled accessories, or code approval.

The operator's 12-sheet panel-schedule reference is now implemented as a separate project output.
Thirty-one visually reviewed sheets retain the reference's odd/even circuit, R/Y/B, load-summary,
and feeder-summary form while using corrected unit-consistent formulas. The 113 extracted lighting
circuits total 45,546.749 VA and reconcile per panel. Missing engineering values remain green and
the workbook explicitly labels its scope as lighting-only until the remaining circuit categories
and reviewed project inputs are available.

Readiness `v29` adds a direct, read-only `ElectricalSystem` query instead of discovering circuits
only through lighting-fixture membership. Fourteen live pages captured all 448 power circuits with
strict continuation: 113 lighting, 264 receptacle, 67 feeder/panel, and four spare/empty circuits
across 54 panels. The source-bound inventory self-hash is `076BEFF5...B6257B8`. A new 54-sheet
workbook matches the supplied panel-schedule presentation, places all circuits, and allocates 138
multi-pole circuit loads across phases exactly once. A loaded-panel change test recalculates demand,
spare, maximum-demand, and phase-current formulas; native Excel reports 1,566 formulas and zero
formula errors. Its 2,093,743.732737 VA aggregate is a circuit-row reconciliation that includes
both feeders and downstream circuits, not a non-duplicated whole-project demand. Missing green
engineering inputs and final code/protection review remain intentionally open.

The user-directed School Architectural model has a source-bound electrical distribution study.
The current workbook binds the 108-room/96-ceiling inventory to `380/220 V`, three-phase,
four-live-conductor plus `PE`, `50 Hz`, `TN-S`, calculates 1,152 preliminary luminaires and
`29.680 kW`, locates 806 review-only points plus a separate 45-point gym layout, and proposes 438
general receptacles, 21 GFI points, 78 dedicated points, and zoned panel candidates. The latest
verified sample RVT contains 45 gym high-bays and eight classroom panels. A 2026-10-29 calculation
report records this distinction and the supporting lumen-method results. These point estimates
are not code-verified or an executable whole-school design; 17 rooms still lack XY points, and
exact hosted geometry, equipment loads, circuits, protection, cabling, emergency lighting, and
routing remain open review gates.

The school lighting-library gate has now passed an isolated live Revit 2024 scratch acceptance.
Approved preview `3F0ABF99...D28E7E` bound four exact official-family hashes, type names, and test
locations. Request `21d66517-1a40-467c-8ae1-263a50f77ae0` upgraded only per-request copies, loaded
the exact or unambiguous manufacturer type, placed one L1/L2/L4/L5-L6 instance, saved a new RVT,
and reopened it. All four instances retained their exact type, Lighting Fixtures category, one
electrical connector, visible geometry, bounds, and millimetre coordinates. Published output
`49F851D3...53EAC` is isolated under `workspace/revit-school/scratch`; protected source
`D0D0C48...09F3` remained unchanged. Stale internal load/photometric defaults and rejected Type
Catalog rows remain a separate explicit parameter/IES gate before any school-model placement.

That parameter/IES review is now source-bound in non-executable preview `043B3315...0C45CB`.
Exact LM-63 parsing and file hashes confirm L1 at `4310.8 lm/31 W`. The preview preserves rather
than averages or hides the remaining deltas: L2 product `2500 lm/19 W` versus IES
`2883.8 lm/19.257 W`, L4 product `63 W` versus exact IES `67 W`, and L5-L6 product `4000 lm`
versus the nearest reviewed `4500 lm` IES. Only L1 advances to separately approved scratch-
parameter preview `310E62C9...FB8FDA`; no Revit write is authorized yet.

The exact L1 parameter preview was subsequently approved and live-executed as request
`633a5606-15e5-459e-8a93-76cc75dcff6d`. Revit 2024 saved and reopened a new isolated RVT and
verified `31 VA`, `31W`, the exact L1 IES filename, and one electrical connector. Published output
SHA-256 is `3302C249...7C604D`; isolated source `49F851D3...53EAC` and protected school source
`D0D0C48...09F3` remained unchanged. L2/L4/L5-L6 and school placement remain separate hold points.

A subsequent current official Signify endpoint refresh resolves the L2 and L5-L6 photometry
identity holds without touching Revit. Exact code `911401816685` now binds `2500.4 lm/19 W`; exact
code `910505100047` binds `4000.6 lm/30 W`. Self-hashed refresh `4BCD2DE5...84CB1E6` feeds
non-executable parameter preview `8359BB40...021781D`, bound to verified L1 output
`3302C249...7C604D`. L4 remains held at official product `63 W` versus exact official IES `67 W`;
no remaining-parameter write or school placement is authorized yet.

The remaining-parameter preview was then explicitly approved and live-executed as request
`4056f61e-e41e-499d-bf6a-e499b5038506`. Revit 2024 reopened and verified L2 at `19 VA/19W` with
the exact `2500.4 lm` IES, L5-L6 at `30 VA/30W` with the exact `4000.6 lm` IES, one connector per
target, and preserved L1. Published output SHA-256 is `B4EC298B...50D910`; input
`3302C249...7C604D` and protected school source `D0D0C48...09F3` stayed unchanged. L4 power
reconciliation and all school-model placement remain separate gates.

The engineer subsequently accepted the exact official L4 IES value `10501.2 lm/67 W` for
preliminary lighting distribution. A deterministic, non-executable school preview now generates
45 high-bay XY points as a `9x5` grid inside the exact rectangular `GYM 3500` bounding area. The
lumen method gives `306.7 lux` and `3.015 kW` active load. Preview fingerprint is
`1E3B1726838E571D32F57BBB27F48AC0A9A84B8C135659D72AE2FC3154B06FA3`; the updated nine-sheet
workbook SHA-256 is `E4F6575CA409683699E4149EB52CCDB7DBBEE6FF027544C9115AA9FB6154F042`.
The green mounting-Z cells, PF/VA, IES point calculation, sports glare review, licensed Egyptian
clause, and separate Revit transaction approval remain open. No school RVT was changed.

On 2026-10-17, the next bounded L4 review gate was generated without opening Revit. The recorded
gym top is `10.619 m`; non-executable preview `5E78A601...6F273C` proposes a provisional
`9.500 m` level-based mounting elevation and retains `1.119 m` clearance. A checkerboard split
keeps each transaction below the existing 32-instance limit: batch 1 contains 23 points distributed
over the whole room and batch 2 defers 22 complementary points until batch 1 is reopened and
verified. The preview binds the unchanged school source, exact RFA and exact IES hashes, refuses
overwrite, requires backup/reopen verification, and passes tamper/expiry/input-change tests. Its
PNG plan SHA-256 is `9BAE1E7E...D4DDF976`. Explicit fingerprint approval is still required; no
school RVT was changed.

The engineer then approved fingerprint `5E78A601...6F273C` and the provisional `9.500 m`
elevation. A new fixed supported-API action loaded only the exact hashed L4 RFA and placed the 23
batch-1 checkerboard points on `Level 1`, saving only
`School-Architectural-electrical-L4-Batch1.rvt`. Live request `592c9a53...e5ce` committed and
produced SHA-256 `941551BE...41A1A`; all 23 fixtures report one electrical connector. After a full
close/reopen, read-only request `e88d158a...2734` verified their IDs, family/type, marks, level,
coordinates, and `9500 mm` elevation. Source and timestamped backup remain byte-identical at
`D0D0C48E...09F3`. Batch 2 must bind to `941551BE...41A1A` and receive a separate exact approval.

The non-executable batch-2 gate now exists at fingerprint `56552925...943801`. It derives the 22
complementary checkerboard points from the original 45-point layout, binds the verified batch-1
RVT `941551BE...41A1A` plus both handoff reports and exact RFA/IES hashes, and targets only a new
complete RVT. Its tamper, expiry, evidence-change, partition, and overwrite tests pass. The earlier
batch-2 preview expired without use. No batch-2 Revit action has been implemented or submitted;
exact approval of the current fingerprint is the next gate.

That exact approval was consumed by live request `05cb6ac6...e71a`. The action preserved all 23
batch-1 fixtures and added exactly 22 complementary fixtures. New complete RVT
`FB72029C...C52D1` reopened under request `f4c7f5f7...3b03` and verified all 45 unique IDs,
approved points, `9500 mm` elevation, marks, family/type identity, and connectors without mutation.
The next expansion gate is a single representative classroom visual sample, not a bulk school write.

A non-executable representative lighting sample is now prepared for `CLASSROOM 1500` on Level 1.
It binds the verified complete L4 source `FB72029C...C52D1`, the exact official L1 family and IES
hashes, room `221093`, and ACT ceiling `278813`. The proposed eight-panel perimeter layout gives
`310.8 lux` by the recorded lumen method and `248 W` connected active load. Preview fingerprint is
`4414E83E...5C423`; a visual plan is published beside it. No classroom fixture has been written to
Revit, bulk school distribution remains unauthorized, and exact approval plus new-file execution and
reopen verification are still required before the engineer's visual acceptance.

The first approved sample request was not consumed: an unquoted Arabic/space-containing Revit
launch argument started Revit without the source document, and the bridge therefore had no active
model to process. No output or transaction occurred. That expired request is archived intact, and
replacement fingerprint `9075F097...60EDA` binds the unchanged source and identical eight-point
sample. A corrected quoted launch plus exact renewed approval is required before execution.
